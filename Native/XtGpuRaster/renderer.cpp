#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d9.h>
#include <wrl/client.h>
#include <unordered_map>
#include <cmath>
#include <cstring>
#include <new>
using Microsoft::WRL::ComPtr;
struct Command { int id; float x,y,w,h,cx,cy,angle,alpha,left,top,right,bottom; };
struct Vertex { float x,y,z,rhw; DWORD color; float u,v; };
struct Renderer {
    ComPtr<IDirect3D9Ex> api;
    ComPtr<IDirect3DDevice9Ex> device;
    ComPtr<IDirect3DSurface9> surface;
    std::unordered_map<int,ComPtr<IDirect3DTexture9>> textures;
    int width=0,height=0;
    UINT adapter=D3DADAPTER_DEFAULT;
};
#define API extern "C" __declspec(dllexport)
API HRESULT __cdecl xtgpu_create(Renderer** result,HWND window) {
    if (!result) return E_POINTER;
    *result=nullptr;
    auto r=new(std::nothrow) Renderer();
    if(!r) return E_OUTOFMEMORY;
    HRESULT hr=Direct3DCreate9Ex(D3D_SDK_VERSION,&r->api);
    if (SUCCEEDED(hr)) {
        HWND owner=IsWindow(window)?window:GetDesktopWindow();
        HMONITOR monitor=MonitorFromWindow(owner,MONITOR_DEFAULTTONEAREST);
        for(UINT i=0;i<r->api->GetAdapterCount();++i) if(r->api->GetAdapterMonitor(i)==monitor) {r->adapter=i;break;}
        D3DPRESENT_PARAMETERS p{};
        p.Windowed=TRUE; p.SwapEffect=D3DSWAPEFFECT_DISCARD;
        p.hDeviceWindow=owner; p.BackBufferWidth=1; p.BackBufferHeight=1;
        p.BackBufferFormat=D3DFMT_UNKNOWN; p.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
        hr=r->api->CreateDeviceEx(r->adapter,D3DDEVTYPE_HAL,p.hDeviceWindow,
            D3DCREATE_MULTITHREADED|D3DCREATE_FPU_PRESERVE|D3DCREATE_HARDWARE_VERTEXPROCESSING,&p,nullptr,&r->device);
    }
    if (FAILED(hr)) { delete r; return hr; }
    *result=r; return S_OK;
}
API void __cdecl xtgpu_destroy(Renderer* r) { delete r; }
API HRESULT __cdecl xtgpu_adapter(Renderer* r,char* text,int capacity) {
    if(!r || !text || capacity<512) return E_INVALIDARG;
    D3DADAPTER_IDENTIFIER9 info{};
    HRESULT hr=r->api->GetAdapterIdentifier(r->adapter,0,&info);
    if(SUCCEEDED(hr)) strcpy_s(text,(size_t)capacity,info.Description);
    return hr;
}
API HRESULT __cdecl xtgpu_resize(Renderer* r,int width,int height,IDirect3DSurface9** output) {
    if (!r || !output || width<=0 || height<=0 || width>16384 || height>16384) return E_INVALIDARG;
    if (r->width!=width || r->height!=height) {
        ComPtr<IDirect3DSurface9> next;
        HRESULT hr=r->device->CreateRenderTarget(width,height,D3DFMT_A8R8G8B8,D3DMULTISAMPLE_NONE,0,FALSE,&next,nullptr);
        if (FAILED(hr)) return hr;
        r->surface=next; r->width=width; r->height=height;
    }
    *output=r->surface.Get(); return S_OK; // borrowed; D3DImage takes its own reference
}
API HRESULT __cdecl xtgpu_upload(Renderer* r,int id,int width,int height,const unsigned char* pixels,int stride) {
    if (!r || !pixels || id<=0 || width<=0 || height<=0 || width>16384 || height>16384 || stride<width*4) return E_INVALIDARG;
    ComPtr<IDirect3DTexture9> texture;
    HRESULT hr=r->device->CreateTexture(width,height,1,D3DUSAGE_DYNAMIC,D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,&texture,nullptr);
    if (FAILED(hr)) return hr;
    D3DLOCKED_RECT lock{};
    hr=texture->LockRect(0,&lock,nullptr,D3DLOCK_DISCARD);
    if (FAILED(hr)) return hr;
    for (int y=0;y<height;++y) memcpy((unsigned char*)lock.pBits+y*lock.Pitch,pixels+(size_t)y*stride,(size_t)width*4);
    hr=texture->UnlockRect(0);
    if (FAILED(hr)) return hr;
    try {r->textures[id]=texture;} catch(...) {return E_OUTOFMEMORY;}
    return S_OK;
}
API void __cdecl xtgpu_remove(Renderer* r,int id) { if(r) { r->device->SetTexture(0,nullptr); r->textures.erase(id); } }
API HRESULT __cdecl xtgpu_render(Renderer* r,const Command* commands,int count) {
    if (!r || !r->surface || count<0 || count>65536 || (count && !commands)) return E_INVALIDARG;
    auto d=r->device.Get();
    HRESULT hr=d->SetRenderTarget(0,r->surface.Get());
    if (FAILED(hr)) return hr;
    D3DVIEWPORT9 vp{0,0,(DWORD)r->width,(DWORD)r->height,0,1};
    d->SetViewport(&vp); d->SetDepthStencilSurface(nullptr);
    d->SetRenderState(D3DRS_SCISSORTESTENABLE,FALSE);
    hr=d->Clear(0,nullptr,D3DCLEAR_TARGET,0,1,0);
    if (FAILED(hr)) return hr;
    hr=d->BeginScene(); if (FAILED(hr)) return hr;
    d->SetFVF(D3DFVF_XYZRHW|D3DFVF_DIFFUSE|D3DFVF_TEX1);
    d->SetRenderState(D3DRS_ZENABLE,FALSE); d->SetRenderState(D3DRS_LIGHTING,FALSE);
    d->SetRenderState(D3DRS_CULLMODE,D3DCULL_NONE);
    d->SetRenderState(D3DRS_ALPHABLENDENABLE,TRUE);
    d->SetRenderState(D3DRS_SRCBLEND,D3DBLEND_ONE); d->SetRenderState(D3DRS_DESTBLEND,D3DBLEND_INVSRCALPHA);
    d->SetRenderState(D3DRS_SCISSORTESTENABLE,TRUE);
    d->SetSamplerState(0,D3DSAMP_MINFILTER,D3DTEXF_LINEAR); d->SetSamplerState(0,D3DSAMP_MAGFILTER,D3DTEXF_LINEAR);
    d->SetSamplerState(0,D3DSAMP_ADDRESSU,D3DTADDRESS_CLAMP); d->SetSamplerState(0,D3DSAMP_ADDRESSV,D3DTADDRESS_CLAMP);
    d->SetTextureStageState(0,D3DTSS_COLORARG1,D3DTA_TEXTURE); d->SetTextureStageState(0,D3DTSS_COLORARG2,D3DTA_DIFFUSE);
    d->SetTextureStageState(0,D3DTSS_ALPHAARG1,D3DTA_TEXTURE); d->SetTextureStageState(0,D3DTSS_ALPHAARG2,D3DTA_DIFFUSE);
    d->SetTextureStageState(1,D3DTSS_COLOROP,D3DTOP_DISABLE);
    for(int i=0;i<count;++i) {
        const auto& c=commands[i];
        RECT clip{(LONG)std::max(0.f,std::floor(c.left)),(LONG)std::max(0.f,std::floor(c.top)),
            (LONG)std::min((float)r->width,std::ceil(c.right)),(LONG)std::min((float)r->height,std::ceil(c.bottom))};
        if(clip.right<=clip.left || clip.bottom<=clip.top) continue;
        d->SetScissorRect(&clip);
        IDirect3DTexture9* texture=nullptr;
        if(c.id) { auto it=r->textures.find(c.id); if(it==r->textures.end()) { hr=E_INVALIDARG; break; } texture=it->second.Get(); }
        d->SetTexture(0,texture);
        d->SetTextureStageState(0,D3DTSS_COLOROP,c.id?D3DTOP_MODULATE:D3DTOP_SELECTARG2);
        d->SetTextureStageState(0,D3DTSS_ALPHAOP,c.id?D3DTOP_MODULATE:D3DTOP_SELECTARG2);
        BYTE alpha=(BYTE)std::max(0.f,std::min(255.f,std::round(c.alpha*255)));
        DWORD color=D3DCOLOR_ARGB(alpha,alpha,alpha,alpha);
        float co=std::cos(c.angle),si=std::sin(c.angle);
        auto vertex=[&](float x,float y,float u,float v) { float dx=x-c.cx,dy=y-c.cy;
            return Vertex{c.cx+dx*co-dy*si-.5f,c.cy+dx*si+dy*co-.5f,0,1,color,u,v}; };
        Vertex vertices[]{vertex(c.x,c.y,0,0),vertex(c.x+c.w,c.y,1,0),vertex(c.x,c.y+c.h,0,1),vertex(c.x+c.w,c.y+c.h,1,1)};
        hr=d->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP,2,vertices,sizeof(Vertex)); if(FAILED(hr)) break;
    }
    HRESULT end=d->EndScene(); return FAILED(hr)?hr:end;
}
// Test-only readback; normal presentation never copies the composed frame to CPU.
API HRESULT __cdecl xtgpu_read(Renderer* r,unsigned char* pixels,int stride) {
    if(!r || !r->surface || !pixels || stride<r->width*4) return E_INVALIDARG;
    ComPtr<IDirect3DSurface9> copy;
    HRESULT hr=r->device->CreateOffscreenPlainSurface(r->width,r->height,D3DFMT_A8R8G8B8,D3DPOOL_SYSTEMMEM,&copy,nullptr);
    if(FAILED(hr)) return hr;
    hr=r->device->GetRenderTargetData(r->surface.Get(),copy.Get()); if(FAILED(hr)) return hr;
    D3DLOCKED_RECT lock{}; hr=copy->LockRect(&lock,nullptr,D3DLOCK_READONLY); if(FAILED(hr)) return hr;
    for(int y=0;y<r->height;++y) memcpy(pixels+(size_t)y*stride,(unsigned char*)lock.pBits+y*lock.Pitch,(size_t)r->width*4);
    return copy->UnlockRect();
}
