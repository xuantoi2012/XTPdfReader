#include <windows.h>
#include <psapi.h>
#include <dxgi.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include "public/fpdfview.h"
#include "include/core/SkCanvas.h"
#include "include/core/SkImage.h"
#include "include/core/SkSurface.h"
#include "include/gpu/ganesh/GrDirectContext.h"
#include "include/gpu/ganesh/SkSurfaceGanesh.h"
#include "include/gpu/ganesh/gl/GrGLAssembleInterface.h"
#include "include/gpu/ganesh/gl/GrGLDirectContext.h"

using Clock = std::chrono::steady_clock;
static double Ms(Clock::time_point t) {
  return std::chrono::duration<double, std::milli>(Clock::now() - t).count();
}
static double PrivateMB() {
  PROCESS_MEMORY_COUNTERS_EX m{};
  GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&m), sizeof(m));
  return m.PrivateUsage / 1048576.0;
}
static std::string Utf8(const wchar_t* s) {
  int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
  std::string r(n, 0);
  WideCharToMultiByte(CP_UTF8, 0, s, -1, r.data(), n, nullptr, nullptr);
  r.pop_back();
  return r;
}
static void Fail(const char* message) {
  std::fprintf(stderr, "%s\n", message);
  std::exit(2);
}

// Load EGL dynamically: the Skia ABI comes from PDFium, not libSkiaSharp.
struct Angle {
  HMODULE egl = LoadLibraryW(L"libEGL.dll");
  HMODULE gles = LoadLibraryW(L"libGLESv2.dll");
  using Ptr = void*;
  using GetProc = FARPROC(WINAPI*)(const char*);
  GetProc getProc = nullptr;
  Ptr display = nullptr, context = nullptr, surface = nullptr;
  template<typename T> T Proc(const char* name) {
    auto p = GetProcAddress(egl, name);
    if (!p && getProc) p = getProc(name);
    if (!p) Fail(name);
    return reinterpret_cast<T>(p);
  }
  Angle() {
    if (!egl || !gles) Fail("ANGLE DLL loading failed");
    getProc = Proc<GetProc>("eglGetProcAddress");
    IDXGIFactory* factory = nullptr;
    if (FAILED(CreateDXGIFactory(__uuidof(IDXGIFactory), reinterpret_cast<void**>(&factory)))) Fail("DXGI factory failed");
    LUID luid{};
    bool found = false;
    for (UINT i = 0; ; ++i) {
      IDXGIAdapter* adapter = nullptr;
      if (factory->EnumAdapters(i, &adapter) == DXGI_ERROR_NOT_FOUND) break;
      DXGI_ADAPTER_DESC desc{};
      adapter->GetDesc(&desc);
      if (desc.VendorId == 0x10de) { luid = desc.AdapterLuid; found = true; }
      adapter->Release();
      if (found) break;
    }
    factory->Release();
    if (!found) Fail("NVIDIA adapter not found; select hardware adapter explicitly");
    // EGL_PLATFORM_ANGLE_ANGLE, D3D11 hardware, exact NVIDIA adapter LUID.
    const int attrs[] = {0x3203, 0x3208, 0x3209, 0x320A, 0x34A0, luid.HighPart, 0x34A1, static_cast<int>(luid.LowPart), 0x3038};
    display = Proc<Ptr(WINAPI*)(unsigned, Ptr, const int*)>("eglGetPlatformDisplayEXT")(0x3202, nullptr, attrs);
    int major = 0, minor = 0;
    if (!display || !Proc<unsigned(WINAPI*)(Ptr, int*, int*)>("eglInitialize")(display, &major, &minor))
      Fail("EGL D3D11 hardware initialization failed");
    Proc<unsigned(WINAPI*)(unsigned)>("eglBindAPI")(0x30A0);
    const int configAttrs[] = {0x3033, 1, 0x3040, 0x40, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3038};
    Ptr config = nullptr;
    int count = 0;
    if (!Proc<unsigned(WINAPI*)(Ptr, const int*, Ptr*, int, int*)>("eglChooseConfig")(display, configAttrs, &config, 1, &count) || !count)
      Fail("EGL ES3 pbuffer configuration unavailable");
    const int pbufferAttrs[] = {0x3057, 16, 0x3056, 16, 0x3038};
    surface = Proc<Ptr(WINAPI*)(Ptr, Ptr, const int*)>("eglCreatePbufferSurface")(display, config, pbufferAttrs);
    const int contextAttrs[] = {0x3098, 3, 0x3038};
    context = Proc<Ptr(WINAPI*)(Ptr, Ptr, Ptr, const int*)>("eglCreateContext")(display, config, nullptr, contextAttrs);
    if (!surface || !context || !Proc<unsigned(WINAPI*)(Ptr, Ptr, Ptr, Ptr)>("eglMakeCurrent")(display, surface, surface, context))
      Fail("EGL context creation failed");
  }
  static GrGLFuncPtr GLProc(void* ctx, const char* name) {
    auto* self = static_cast<Angle*>(ctx);
    auto p = GetProcAddress(self->gles, name);
    if (!p) p = self->getProc(name);
    return reinterpret_cast<GrGLFuncPtr>(p);
  }
  ~Angle() {
    Proc<unsigned(WINAPI*)(Ptr, Ptr, Ptr, Ptr)>("eglMakeCurrent")(display, nullptr, nullptr, nullptr);
    Proc<unsigned(WINAPI*)(Ptr, Ptr)>("eglDestroyContext")(display, context);
    Proc<unsigned(WINAPI*)(Ptr, Ptr)>("eglDestroySurface")(display, surface);
    Proc<unsigned(WINAPI*)(Ptr)>("eglTerminate")(display);
    FreeLibrary(gles);
    FreeLibrary(egl);
  }
};

static void SavePPM(const std::wstring& name, const std::vector<unsigned char>& p, int w, int h) {
  FILE* f = nullptr;
  _wfopen_s(&f, name.c_str(), L"wb");
  if (!f) Fail("Cannot write quality image");
  std::fprintf(f, "P6\n%d %d\n255\n", w, h);
  for (size_t i = 0; i < p.size(); i += 4) std::fwrite(p.data() + i, 1, 3, f);
  std::fclose(f);
}
static void Stats(const char* name, std::vector<double> times) {
  std::sort(times.begin(), times.end());
  std::printf("%s_median_ms,%.4f\n%s_p95_ms,%.4f\n", name, times[times.size()/2], name, times[static_cast<size_t>(std::ceil(times.size() * .95)) - 1]);
}

int wmain(int argc, wchar_t** argv) {
  if (argc < 4) Fail("Usage: xt_gpu_poc PDF page(1-based) output-prefix [frames=30] [cacheMB=128] [gpu-only]");
  SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS);
  constexpr int w = 1280, h = 800;
  int frames = argc > 4 ? _wtoi(argv[4]) : 30;
  if (frames < 5 || frames > 300) Fail("Frames must be 5..300");
  int cacheMB = argc > 5 ? _wtoi(argv[5]) : 128;
  if (cacheMB < 16 || cacheMB > 512) Fail("Cache must be 16..512 MB");
  bool gpuOnly = argc > 6 && std::wstring(argv[6]) == L"gpu-only";
  FPDF_LIBRARY_CONFIG config{};
  config.version = 4;
  config.m_RendererType = FPDF_RENDERERTYPE_SKIA;
  FPDF_InitLibraryWithConfig(&config);
  auto t = Clock::now();
  auto doc = FPDF_LoadDocument(Utf8(argv[1]).c_str(), nullptr);
  if (!doc) Fail("PDF load failed");
  auto page = FPDF_LoadPage(doc, _wtoi(argv[2]) - 1);
  if (!page) Fail("Page load failed");
  std::printf("load_ms,%.4f\nprivate_loaded_mb,%.3f\n", Ms(t), PrivateMB());
  const int pw = static_cast<int>(std::ceil(FPDF_GetPageWidthF(page)));
  const int ph = static_cast<int>(std::ceil(FPDF_GetPageHeightF(page)));
  const float scale = static_cast<float>(w) / pw * 3.f;
  t = Clock::now();
  FPDF_SKIA_PICTURE picture = FPDF_CreateSkiaPicture(page, pw, ph);
  if (!picture) Fail("PDFium Skia picture recording failed");
  std::printf("record_ms,%.4f\npicture_approx_mb,%.3f\nprivate_recorded_mb,%.3f\n", Ms(t), FPDF_SkiaPictureApproximateBytes(picture)/1048576.0, PrivateMB());
  std::printf("picture_ops,%d\n", FPDF_SkiaPictureApproximateOpCount(picture));
  t = Clock::now();
  Angle angle;
  auto getString = reinterpret_cast<const unsigned char*(WINAPI*)(unsigned)>(Angle::GLProc(&angle, "glGetString"));
  const char* renderer = reinterpret_cast<const char*>(getString(0x1F01));
  std::printf("renderer,%s\n", renderer ? renderer : "null");
  if (!renderer || std::string(renderer).find("Direct3D11") == std::string::npos || std::string(renderer).find("Microsoft") != std::string::npos)
    Fail("Hardware D3D11 renderer not confirmed");
  auto glInterface = GrGLMakeAssembledGLESInterface(&angle, Angle::GLProc);
  auto gpu = GrDirectContexts::MakeGL(glInterface);
  if (!gpu) Fail("Skia Ganesh initialization failed");
  gpu->setResourceCacheLimit(cacheMB * 1024 * 1024);
  std::printf("gpu_cache_limit_mb,%d\n", cacheMB);
  auto info = SkImageInfo::Make(w, h, kRGBA_8888_SkColorType, kPremul_SkAlphaType);
  auto cpuSurface = SkSurfaces::Raster(info);
  auto gpuSurface = SkSurfaces::RenderTarget(gpu.get(), skgpu::Budgeted::kYes, info);
  if (!cpuSurface || !gpuSurface) Fail("Skia surface allocation failed");
  std::printf("gpu_setup_ms,%.4f\nprivate_gpu_setup_mb,%.3f\n", Ms(t), PrivateMB());
  auto draw = [&](SkSurface* s, int frame, bool direct) {
    auto* c = s->getCanvas();
    c->clear(SK_ColorWHITE);
    c->save();
    c->translate(-w * .65f - frame * 4.f, -h * .35f - frame * 2.f);
    c->scale(scale * (1.f + frame * .002f), scale * (1.f + frame * .002f));
    if (direct) FPDF_RenderPageSkia(c, page, pw, ph);
    else FPDF_RenderSkiaPicture(c, picture);
    c->restore();
  };
  t = Clock::now();
  draw(gpuSurface.get(), 0, false);
  gpu->flushAndSubmit(GrSyncCpu::kYes);
  std::printf("picture_gpu_first_ms,%.4f\n", Ms(t));
  for (int mode = 0; mode < 4; ++mode) {
    if (gpuOnly && mode != 2) continue;
    auto* s = mode >= 2 ? gpuSurface.get() : cpuSurface.get();
    std::vector<double> times;
    for (int frame = -3; frame < frames; ++frame) {
      t = Clock::now();
      draw(s, std::max(frame, 0) % 30, mode == 0 || mode == 3);
      if (mode >= 2) gpu->flushAndSubmit(GrSyncCpu::kYes);
      if (frame >= 0) times.push_back(Ms(t));
    }
    Stats(mode == 0 ? "direct_cpu" : mode == 1 ? "picture_cpu" : mode == 2 ? "picture_gpu" : "direct_gpu", times);
    std::printf("private_mode%d_mb,%.3f\n", mode, PrivateMB());
  }
  std::printf("private_after_replay_mb,%.3f\n", PrivateMB());
  // Compare identical transforms outside all timing loops.
  draw(cpuSurface.get(), 0, true);
  std::vector<unsigned char> reference(w*h*4), cpu(w*h*4), actual(w*h*4);
  if (!cpuSurface->readPixels(info, reference.data(), w*4, 0, 0)) Fail("Reference readback failed");
  draw(cpuSurface.get(), 0, false);
  if (!cpuSurface->readPixels(info, cpu.data(), w*4, 0, 0)) Fail("CPU readback failed");
  draw(gpuSurface.get(), 0, false);
  gpu->flushAndSubmit(GrSyncCpu::kYes);
  if (!gpuSurface->readPixels(info, actual.data(), w*4, 0, 0)) Fail("GPU readback failed");
  double cpuError = 0, gpuError = 0;
  size_t ink = 0, changed = 0;
  for (size_t i = 0; i < actual.size(); i += 4) {
    bool delta = false;
    for (int ch = 0; ch < 3; ++ch) {
      cpuError += std::abs(int(reference[i+ch]) - int(cpu[i+ch]));
      int d = std::abs(int(reference[i+ch]) - int(actual[i+ch]));
      gpuError += d;
      delta |= d > 16;
    }
    ink += reference[i] < 240 || reference[i+1] < 240 || reference[i+2] < 240;
    changed += delta;
  }
  std::printf("cpu_mae,%.5f\ngpu_mae,%.5f\ngpu_changed_fraction,%.6f\nreference_ink_fraction,%.6f\n", cpuError/(w*h*3), gpuError/(w*h*3), double(changed)/(w*h), double(ink)/(w*h));
  SavePPM(std::wstring(argv[3])+L"-reference.ppm", reference, w, h);
  SavePPM(std::wstring(argv[3])+L"-gpu.ppm", actual, w, h);
  if (ink < 100) Fail("Reference viewport blank: choose another viewport/page");
  auto cached = gpuSurface->makeImageSnapshot();
  std::vector<double> cacheTimes;
  for (int frame = -3; frame < frames; ++frame) {
    t = Clock::now();
    auto* c = gpuSurface->getCanvas();
    c->clear(SK_ColorWHITE);
    c->save();
    c->translate(float(std::max(frame, 0)), float(std::max(frame, 0)));
    c->scale(1.f + std::max(frame, 0)*.002f, 1.f + std::max(frame, 0)*.002f);
    c->drawImage(cached, 0, 0);
    c->restore();
    gpu->flushAndSubmit(GrSyncCpu::kYes);
    if (frame >= 0) cacheTimes.push_back(Ms(t));
  }
  Stats("texture_gpu", cacheTimes);
  size_t bytes = 0;
  gpu->getResourceCacheUsage(nullptr, &bytes);
  std::printf("skia_gpu_cache_mb,%.3f\nprivate_final_mb,%.3f\n", bytes/1048576.0, PrivateMB());
  cached.reset();
  gpuSurface.reset();
  gpu->freeGpuResources();
  gpu->flushAndSubmit(GrSyncCpu::kYes);
  std::printf("private_after_gpu_purge_mb,%.3f\n", PrivateMB());
  gpu.reset();
  std::printf("private_after_gpu_context_release_mb,%.3f\n", PrivateMB());
  FPDF_DestroySkiaPicture(picture);
  FPDF_ClosePage(page);
  FPDF_CloseDocument(doc);
  FPDF_DestroyLibrary();
  return 0;
}
