// XtMuPdfWorker: the MuPDF viewing worker without Python. Same protocol as Tests/GpuPdfium/MuPdfViewportWorker.py:
// one JSON request per stdin line; the reply is one JSON line (and, for a render, the BGR(A) pixels right after it).
// Viewing, text, bounded caches and lazy block reading for network files.
#define NOMINMAX
#include <mupdf/fitz.h>
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#include <algorithm>
#include <chrono>
#include <cctype>
#include <cfloat>
#include <cmath>
#include <cwctype>
#include <iostream>
#include <stdexcept>
#include <cstdio>
#include <cstring>
#include <list>
#include <map>
#include <memory>
#include <set>
#include <sstream>
#include <iomanip>
#include <string>
#include <unordered_map>
#include <vector>
#include "json.h"
#include "fidelity.h"
#include "ocr.h"
#include "edit.h"
#include "objects.h"
#include "transport.h"

// XT_PATCHED_MUPDF: built against the patched MuPDF source (mupdf-xt/mupdf-1.28.2-xt.patch).
// It renders a large page in horizontal bands on several threads and produces the same
// pixels, byte for byte, as a single-threaded render of the whole area.
#ifdef XT_PATCHED_MUPDF
#include <atomic>
#include <mutex>
#include <thread>
extern "C" {
void fz_set_alloc_lockless(int on);
void fz_draw_set_virtual_scissor(fz_context* ctx, fz_device* dev, fz_irect virtual_bounds);
}
#ifdef XT_MIMALLOC
#include <mimalloc.h>
// mimalloc (MIT): thread safe, and about 12 % faster than the CRT heap for building display lists.
static void* MuMalloc(void*, size_t n) { return mi_malloc(n); }
static void* MuRealloc(void*, void* p, size_t n) { return mi_realloc(p, n); }
static void MuFree(void*, void* p) { mi_free(p); }
#endif
static CRITICAL_SECTION g_mupdfLocks[FZ_LOCK_MAX];
static void MuLock(void*, int i) { EnterCriticalSection(&g_mupdfLocks[i]); }
static void MuUnlock(void*, int i) { LeaveCriticalSection(&g_mupdfLocks[i]); }
#endif

using Clock = std::chrono::steady_clock;
static double MillisSince(Clock::time_point t) { return std::chrono::duration<double, std::milli>(Clock::now() - t).count(); }

static std::wstring Widen(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), w.data(), n);
    return w;
}

static double EnvNumber(const char* name, double fallback) {
    const char* v = std::getenv(name);
    return v && *v ? std::atof(v) : fallback;
}

// ── lazy block reading ───────────────────────────────────────────────────────

// The file is read in big blocks (a few round trips instead of thousands of small reads) and only the blocks MuPDF asks for.
class BlockFile {
public:
    BlockFile(HANDLE handle, int64_t size, size_t block, size_t budget) : handle_(handle), size_(size), block_(block), budget_(budget) {}
    ~BlockFile() {
        for (auto& e : blocks_) _aligned_free(e.second.data);
        if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_);
    }
    int64_t size() const { return size_; }
    size_t blockSize() const { return block_; }
    size_t bytes() const { return used_; }
    void setBudget(size_t budget) { budget_ = budget; evict(lastBlock_); }

    // The bytes of block `index`; stays valid until the next call that is not for the same block.
    const uint8_t* get(int64_t index, size_t& length) {
        lastBlock_ = index; // the stream may still point into this block between requests
        auto it = blocks_.find(index);
        if (it == blocks_.end()) {
            int64_t offset = index * (int64_t)block_;
            if (offset >= size_) return nullptr;
            size_t want = (size_t)std::min<int64_t>((int64_t)block_, size_ - offset);
            auto* data = (uint8_t*)_aligned_malloc((want + 4095) & ~(size_t)4095, 4096);
            if (!data) return nullptr;
            OVERLAPPED ov{};
            ov.Offset = (DWORD)(offset & 0xFFFFFFFF);
            ov.OffsetHigh = (DWORD)(offset >> 32);
            DWORD got = 0;
            DWORD chunk = (DWORD)want;
            if (!ReadFile(handle_, data, chunk, &got, &ov) || got < want) { _aligned_free(data); return nullptr; }
            used_ += want;
            lru_.push_front(index);
            it = blocks_.emplace(index, Entry{ data, want, lru_.begin() }).first;
            evict(index);
        } else {
            lru_.erase(it->second.pos);
            lru_.push_front(index);
            it->second.pos = lru_.begin();
        }
        length = it->second.length;
        return it->second.data;
    }

private:
    struct Entry { uint8_t* data; size_t length; std::list<int64_t>::iterator pos; };
    HANDLE handle_;
    int64_t size_;
    size_t block_;
    size_t budget_;
    size_t used_ = 0;
    int64_t lastBlock_ = -1;
    std::unordered_map<int64_t, Entry> blocks_;
    std::list<int64_t> lru_;

    void evict(int64_t keep) {
        while (used_ > budget_ && lru_.size() > 1) {
            int64_t victim = lru_.back();
            if (victim == keep) break;
            auto it = blocks_.find(victim);
            used_ -= it->second.length;
            _aligned_free(it->second.data);
            lru_.pop_back();
            blocks_.erase(it);
        }
    }
};

struct BlockStreamState { BlockFile* file; int64_t at; };  // `at` = file position of stm->wp (like MuPDF's stdio stream)

static int BlockNext(fz_context* ctx, fz_stream* stm, size_t) {
    auto* s = (BlockStreamState*)stm->state;
    BlockFile* f = s->file;
    if (s->at >= f->size()) { stm->rp = stm->wp = nullptr; return EOF; }
    int64_t index = s->at / (int64_t)f->blockSize();
    size_t length = 0;
    const uint8_t* block = f->get(index, length);
    if (!block) fz_throw(ctx, FZ_ERROR_SYSTEM, "cannot read the file at offset %lld", (long long)s->at);
    size_t inside = (size_t)(s->at - index * (int64_t)f->blockSize());
    stm->rp = const_cast<uint8_t*>(block) + inside;
    stm->wp = const_cast<uint8_t*>(block) + length;
    s->at = index * (int64_t)f->blockSize() + (int64_t)length;
    stm->pos = s->at;
    return *stm->rp++;
}

static void BlockSeek(fz_context*, fz_stream* stm, int64_t offset, int whence) {
    auto* s = (BlockStreamState*)stm->state;
    int64_t tell = stm->pos - (stm->wp - stm->rp);
    int64_t target = whence == SEEK_SET ? offset : whence == SEEK_CUR ? tell + offset : s->file->size() + offset;
    target = std::clamp<int64_t>(target, 0, s->file->size());
    s->at = target;
    stm->pos = target;
    stm->rp = stm->wp = nullptr;
}

static void BlockDrop(fz_context*, void* state) { delete (BlockStreamState*)state; }

// ── documents ────────────────────────────────────────────────────────────────

static bool IsNetworkPath(const std::wstring& path) {
    if (path.rfind(L"\\\\", 0) == 0 && path.rfind(L"\\\\?\\", 0) != 0) return true;
    if (path.size() >= 3 && path[1] == L':') {
        wchar_t root[4] = { path[0], L':', L'\\', 0 };
        return GetDriveTypeW(root) == DRIVE_REMOTE;
    }
    return false;
}

struct Doc {
    fz_document* doc = nullptr;
    std::unique_ptr<BlockFile> file;   // when the document is read through blocks
    int pageCount = 0;
    DocExtras extras;
};

// Learned single-thread cost (ms per megapixel) of rendering this page: one value for whole-page
// renders and one per cell of an 8 x 8 grid for viewport tiles. 0 means not known yet.
struct ListEntry { fz_display_list* list = nullptr; fz_rect bounds{}; double fullCost = 0; double cellCost[64] = {}; double buildMs = 0; };
struct RasterEntry { std::string stamp, header; std::vector<uint8_t> pixels; };

class Worker {
public:
    Worker() {
#ifdef XT_PATCHED_MUPDF
        for (auto& cs : g_mupdfLocks) InitializeCriticalSectionAndSpinCount(&cs, 2000);
        fz_locks_context locks{ nullptr, MuLock, MuUnlock };
        fz_set_alloc_lockless(1);   // the CRT heap is thread safe; no global lock per malloc/free
#ifdef XT_MIMALLOC
        static fz_alloc_context allocator{ nullptr, MuMalloc, MuRealloc, MuFree };
        ctx_ = fz_new_context(&allocator, &locks, 256u << 20);
#else
        ctx_ = fz_new_context(nullptr, &locks, 256u << 20);
#endif
        renderThreads_ = (int)std::clamp(EnvNumber("XTPDF_RENDER_THREADS",
            (double)std::min(8u, std::max(1u, std::thread::hardware_concurrency()))), 1.0, 32.0);
        bandMinPixels_ = (size_t)std::max(0.0, EnvNumber("XTPDF_BAND_MIN_PIXELS", 2000000));
        bandMinMilliseconds_ = std::max(0.0, EnvNumber("XTPDF_BAND_MIN_MS", 25));
#else
        ctx_ = fz_new_context(nullptr, nullptr, 256u << 20);
#endif
        if (!ctx_) { std::fputs("cannot create the MuPDF context\n", stderr); std::exit(3); }
        fz_try(ctx_) { fz_register_document_handlers(ctx_); }
        fz_catch(ctx_) { std::fputs("cannot register the document handlers\n", stderr); std::exit(3); }
        minLine_ = EnvNumber("XTPDF_MIN_LINE_PX", 1.2);
        gamma_ = (float)EnvNumber("XTPDF_GAMMA", 1.4);
        rasterBudget_ = EnvNumber("XTPDF_MUPDF_NO_RASTER_CACHE", 0) == 1 ? 0 :
            (size_t)(std::clamp(EnvNumber("XTPDF_MUPDF_RASTER_MB", 128), 0.0, 512.0) * 1024 * 1024);
        configuredLists_ = (size_t)std::max(1.0, EnvNumber("XTPDF_MUPDF_LISTS", 64));
        configuredDocuments_ = (size_t)std::max(1.0, EnvNumber("XTPDF_MUPDF_DOCUMENTS", 64));
        listLimit_ = configuredLists_;
        documentLimit_ = configuredDocuments_;
        blockBytes_ = (size_t)std::clamp(EnvNumber("XTPDF_BLOCK_KB", 256), 4.0, 4096.0) * 1024;
        blockBudget_ = (size_t)std::clamp(EnvNumber("XTPDF_BLOCK_BUDGET_MB", 64), 0.0, 512.0) << 20;
    }

    void Run() {
        std::string line;
        while (std::getline(std::cin, line)) {
            if (line.empty()) continue;
            Handle(line);
        }
    }

    int RunJob(const std::wstring& jobPath, bool ocr) {
        try {
            FILE* file = _wfopen(jobPath.c_str(), L"rb");
            if (!file) throw std::runtime_error("cannot open native job");
            std::string line; char buffer[8192]; size_t n;
            while ((n = std::fread(buffer, 1, sizeof buffer, file)) != 0) line.append(buffer, n);
            std::fclose(file);
            Json job; JsonReader reader(line);
            if (!reader.parse(job) || job.type != Json::Object) throw std::runtime_error("invalid native job");
            std::string stamp; Doc& d = Open(job, stamp);
            if (ocr) nativeocr::Run(ctx_, d.doc, d.pageCount, job);
            else if (job.str("mode") == "runs" || job.str("mode") == "area" || job.str("mode") == "apply") nativeedit::RunJob(ctx_, d.doc, job);
            else nativeobjects::RunJob(ctx_, d.doc, job);
            return 0;
        } catch (const std::exception& e) {
            nativeocr::Emit("{\"type\":\"error\",\"message\":" + JsonQuote(e.what()) + '}');
            return 1;
        }
    }

private:
    fz_context* ctx_ = nullptr;
    double minLine_ = 1.2;
    float gamma_ = 1.4f;
    size_t configuredLists_ = 64, configuredDocuments_ = 64;
    size_t listLimit_ = 64, documentLimit_ = 64, blockBytes_ = 256 * 1024, blockBudget_ = 256u << 20;
    std::list<std::string> docOrder_;                         // most recent first
    std::map<std::string, Doc> docs_;
    std::list<std::string> listOrder_;
    std::map<std::string, ListEntry> lists_;
    size_t rasterBudget_ = 0, rasterLimit_ = 0, rasterBytes_ = 0;
    size_t rasterHits_ = 0, rasterMisses_ = 0;
    std::list<std::string> rasterOrder_;
    std::map<std::string, RasterEntry> rasters_;
    SharedRaster sharedRaster_;
    size_t sharedFrames_ = 0;
    size_t cancelledRenders_ = 0;

    void TrimRasters(size_t limit) {
        while (rasterBytes_ > limit && !rasterOrder_.empty()) {
            auto it = rasters_.find(rasterOrder_.back());
            rasterBytes_ -= it->second.pixels.size();
            rasters_.erase(it);
            rasterOrder_.pop_back();
        }
    }

    static void SendRaster(const std::string& header, const std::vector<uint8_t>& pixels) {
        std::fwrite(header.data(), 1, header.size(), stdout);
        std::fwrite(pixels.data(), 1, pixels.size(), stdout);
        std::fflush(stdout);
    }

    static void Send(const std::string& s) {
        std::fwrite(s.data(), 1, s.size(), stdout);
        std::fputc('\n', stdout);
        std::fflush(stdout);
    }
    static void SendError(const std::string& message) { Send("{\"error\":" + JsonQuote(message) + "}"); }

    // One request. Every MuPDF call is inside fz_try; an exception never leaves this function.
    void Handle(const std::string& line) {
        Json request;
        JsonReader reader(line);
        if (!reader.parse(request) || request.type != Json::Object) { SendError("bad request"); return; }
        std::string op = request.str("op");
        try {
            int memoryState = request.integer("memoryState");
            size_t configured = (size_t)std::max(1, std::min(64, request.has("nativeListLimit") ? request.integer("nativeListLimit") : (int)configuredLists_));
            listLimit_ = memoryState == 0 ? configured : std::min<size_t>(configured, memoryState == 1 ? 2 : 1);
            documentLimit_ = memoryState == 0 ? configuredDocuments_ : std::min<size_t>(configuredDocuments_, 1);
            rasterLimit_ = memoryState == 0 ? rasterBudget_ : 0;
            TrimRasters(rasterLimit_);
            for (auto& item : docs_) if (item.second.file)
                item.second.file->setBudget(memoryState == 0 ? blockBudget_ : memoryState == 1 ? std::min<size_t>(blockBudget_, 4u << 20) : 0);
            while (lists_.size() > listLimit_ && !listOrder_.empty()) {
                auto it = lists_.find(listOrder_.back());
                fz_drop_display_list(ctx_, it->second.list);
                lists_.erase(it); listOrder_.pop_back();
            }
            if (op == "memory") { sharedRaster_.Reset(); Memory(request, memoryState); return; }
            if (op == "stats") {
                size_t blockBytes = 0;
                for (auto& item : docs_) if (item.second.file) blockBytes += item.second.file->bytes();
                Send("{\"documents\":" + std::to_string(docs_.size()) + ",\"displayLists\":" + std::to_string(lists_.size()) + ",\"rasterBytes\":" + std::to_string(rasterBytes_) + ",\"rasterHits\":" + std::to_string(rasterHits_) + ",\"rasterMisses\":" + std::to_string(rasterMisses_) + ",\"blockBytes\":" + std::to_string(blockBytes) + ",\"sharedBytes\":" + std::to_string(sharedRaster_.Capacity()) + ",\"sharedFrames\":" + std::to_string(sharedFrames_) + ",\"cancelledRenders\":" + std::to_string(cancelledRenders_) + ",\"storeBytes\":0}"); return;
            }
            if (op == "close" || op == "release") { sharedRaster_.Reset(); Close(request, op == "close"); return; }
            if (op == "words") { Words(request); return; }
            if (op == "select") { Select(request); return; }
            if (op == "search") { Search(request); return; }
            if (op == "searchrange") { SearchRange(request); return; }
            if (op == "metadata") { Metadata(request); return; }
            if (!op.empty()) { SendError("unknown operation '" + op + "'"); return; }
            Render(request);
        } catch (const std::exception& e) {
            if (std::strcmp(e.what(), "render cancelled") == 0) { ++cancelledRenders_; sharedRaster_.Reset(); }
            SendError(e.what());
        }
    }

    static std::string FullPath(const std::string& utf8) {
        std::wstring w = Widen(utf8);
        wchar_t buffer[32768];
        DWORD n = GetFullPathNameW(w.c_str(), 32768, buffer, nullptr);
        std::wstring full = n > 0 && n < 32768 ? std::wstring(buffer, n) : w;
        int len = WideCharToMultiByte(CP_UTF8, 0, full.c_str(), (int)full.size(), nullptr, 0, nullptr, nullptr);
        std::string out(len, '\0');
        WideCharToMultiByte(CP_UTF8, 0, full.c_str(), (int)full.size(), out.data(), len, nullptr, nullptr);
        for (auto& c : out) c = (char)std::tolower((unsigned char)c);
        return out;
    }

    // Documents are kept by (path, time, size, password): a changed file is a new document and the old one is closed.
    Doc& Open(const Json& request, std::string& stampOut) {
        std::string path = request.str("path");
        std::wstring wide = Widen(path);
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (!GetFileAttributesExW(wide.c_str(), GetFileExInfoStandard, &info)) throw std::runtime_error("cannot find the file");
        unsigned long long size = ((unsigned long long)info.nFileSizeHigh << 32) | info.nFileSizeLow;
        unsigned long long mtime = ((unsigned long long)info.ftLastWriteTime.dwHighDateTime << 32) | info.ftLastWriteTime.dwLowDateTime;
        std::string full = FullPath(path);
        std::string password = request.str("password");
        // Layers: the file in another layer state is another document (as in the Python worker).
        bool haveHidden = false;
        std::set<int> hiddenOff;
        if (auto* h = request.find("hidden"); h && h->type == Json::Array) {
            haveHidden = true;
            for (auto& item : h->items)
                if (item.type == Json::String) hiddenOff.insert(std::atoi(item.text.c_str()));
        }
        std::string stamp = full + "|" + std::to_string(mtime) + "|" + std::to_string(size) + "|" + password;
        if (haveHidden) { stamp += "|h"; for (int x : hiddenOff) stamp += "," + std::to_string(x); }
        stampOut = stamp;

        auto found = docs_.find(stamp);
        if (found != docs_.end()) {
            docOrder_.remove(stamp);
            docOrder_.push_front(stamp);
            return found->second;
        }
        // The same file with another time or size: the old one goes.
        for (auto it = docs_.begin(); it != docs_.end();) {
            if (it->first.compare(0, full.size() + 1, full + "|") == 0) { DropListsOf(it->first); DropDoc(it->second); docOrder_.remove(it->first); it = docs_.erase(it); }
            else ++it;
        }

        Doc d;
        bool failed = false;
        std::string message;
        fz_var(failed);
        fz_try(ctx_) {
            if (IsNetworkPath(wide) || EnvNumber("XTPDF_FORCE_BLOCK_STREAM", 0) == 1) {
                HANDLE h = CreateFileW(wide.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_RANDOM_ACCESS, nullptr);
                if (h == INVALID_HANDLE_VALUE) fz_throw(ctx_, FZ_ERROR_SYSTEM, "cannot open the file (%lu)", GetLastError());
                d.file = std::make_unique<BlockFile>(h, (int64_t)size, blockBytes_, blockBudget_);
                auto* state = new BlockStreamState{ d.file.get(), 0 };
                fz_stream* stm = fz_new_stream(ctx_, state, BlockNext, BlockDrop);
                stm->seek = BlockSeek;
                fz_try(ctx_) { d.doc = fz_open_document_with_stream(ctx_, ".pdf", stm); }
                fz_always(ctx_) { fz_drop_stream(ctx_, stm); }
                fz_catch(ctx_) { fz_rethrow(ctx_); }
            } else {
                d.doc = fz_open_document(ctx_, path.c_str());
            }
            if (fz_needs_password(ctx_, d.doc) && !fz_authenticate_password(ctx_, d.doc, password.c_str()))
                fz_throw(ctx_, FZ_ERROR_ARGUMENT, "Password required or incorrect");
            d.pageCount = fz_count_pages(ctx_, d.doc);
            if (haveHidden) fidelity::ApplyHiddenLayers(ctx_, d.doc, hiddenOff);
        }
        fz_catch(ctx_) { failed = true; message = fz_caught_message(ctx_); }
        if (failed) {
            if (d.doc) fz_drop_document(ctx_, d.doc);
            throw std::runtime_error(message);
        }
        while (docs_.size() >= documentLimit_ && !docOrder_.empty()) {
            std::string oldest = docOrder_.back();
            docOrder_.pop_back();
            DropListsOf(oldest);
            auto it = docs_.find(oldest);
            if (it != docs_.end()) { DropDoc(it->second); docs_.erase(it); }
        }
        auto inserted = docs_.emplace(stamp, std::move(d)).first;
        docOrder_.push_front(stamp);
        return inserted->second;
    }

    void DropDoc(Doc& d) {
        if (d.doc) { fz_drop_document(ctx_, d.doc); d.doc = nullptr; }
        d.file.reset();
    }

    void DropListsOf(const std::string& stamp) {
        for (auto it = rasters_.begin(); it != rasters_.end();) {
            if (it->second.stamp == stamp) {
                rasterBytes_ -= it->second.pixels.size();
                rasterOrder_.remove(it->first);
                it = rasters_.erase(it);
            } else ++it;
        }
        for (auto it = lists_.begin(); it != lists_.end();) {
            if (it->first.compare(0, stamp.size() + 1, stamp + "#") == 0) {
                fz_drop_display_list(ctx_, it->second.list);
                listOrder_.remove(it->first);
                it = lists_.erase(it);
            } else ++it;
        }
    }

    // The app is short of memory: only what the visible pages need stays (the Python worker does the same).
    void Memory(const Json& request, int memoryState) {
        TrimRasters(0);
        std::vector<std::pair<std::string, int>> protectedPages;
        if (auto* data = request.find("data"); data && data->type == Json::Array)
            for (auto& item : data->items)
                protectedPages.emplace_back(FullPath(item.str("path")), item.integer("page"));
        auto isProtectedDoc = [&](const std::string& stamp) {
            std::string path = stamp.substr(0, stamp.find('|'));
            for (auto& p : protectedPages) if (p.first == path) return true;
            return false;
        };
        for (auto it = docs_.begin(); it != docs_.end();) {
            if (!isProtectedDoc(it->first)) { DropListsOf(it->first); DropDoc(it->second); docOrder_.remove(it->first); it = docs_.erase(it); }
            else ++it;
        }
        for (auto it = lists_.begin(); it != lists_.end();) {
            std::string stamp = it->first.substr(0, it->first.rfind('#'));
            std::string rest = it->first.substr(it->first.rfind('#') + 1);
            int page = std::atoi(rest.c_str());
            std::string path = stamp.substr(0, stamp.find('|'));
            bool keep = false;
            for (auto& p : protectedPages) if (p.first == path && p.second == page) keep = true;
            if (!keep) { fz_drop_display_list(ctx_, it->second.list); listOrder_.remove(it->first); it = lists_.erase(it); }
            else ++it;
        }
        fz_shrink_store(ctx_, memoryState == 2 ? 0 : 50);
        Send("{\"ok\":true,\"documents\":" + std::to_string(docs_.size()) + ",\"displayLists\":" + std::to_string(lists_.size()) + ",\"storeBytes\":0}");
    }

    void Close(const Json& request, bool one) {
        std::string target = one ? FullPath(request.str("path")) : "";
        std::vector<std::string> active;
        if (!one) {
            if (auto* data = request.find("data"); data && data->type == Json::Array)
                for (auto& item : data->items) if (item.type == Json::String) active.push_back(FullPath(item.text));
        }
        for (auto it = docs_.begin(); it != docs_.end();) {
            std::string path = it->first.substr(0, it->first.find('|'));
            bool drop = one ? path == target : std::find(active.begin(), active.end(), path) == active.end();
            if (drop) { DropListsOf(it->first); DropDoc(it->second); docOrder_.remove(it->first); it = docs_.erase(it); }
            else ++it;
        }
        fz_shrink_store(ctx_, 0);
        Send("{\"ok\":true}");
    }

    void Metadata(const Json& request) {
        std::string stamp;
        Doc& d = Open(request, stamp);
        // countOnly: opening a file only needs the number of pages; the sizes (one random read per page on a share) are asked for separately.
        if (auto* data = request.find("data"); data && data->flag("countOnly")) { Send("{\"count\":" + std::to_string(d.pageCount) + "}"); return; }
        const Json* data = request.find("data");
        bool ranged = data && data->has("first");
        int first = ranged ? data->integer("first") : 0;
        int count = ranged ? data->integer("count", 16) : d.pageCount;
        if (first < 0 || first > d.pageCount || count < 0) throw std::runtime_error("invalid metadata range");
        int end = first + std::min(count, d.pageCount - first);
        std::string out = "{\"count\":" + std::to_string(d.pageCount) +
            (ranged ? ",\"first\":" + std::to_string(first) : "") + ",\"sizes\":[";
        bool failed = false;
        std::string message;
        fz_var(failed);
        fz_try(ctx_) {
            for (int i = first; i < end; ++i) {
                fz_page* page = fz_load_page(ctx_, d.doc, i);
                fz_rect r{};
                fz_try(ctx_) { r = fz_bound_page(ctx_, page); }
                fz_always(ctx_) { fz_drop_page(ctx_, page); }
                fz_catch(ctx_) { fz_rethrow(ctx_); }
                char buf[96];
                std::snprintf(buf, sizeof buf, "%s[%.4f,%.4f]", i > first ? "," : "", r.x1 - r.x0, r.y1 - r.y0);
                out += buf;
            }
        }
        fz_catch(ctx_) { failed = true; message = fz_caught_message(ctx_); }
        if (failed) throw std::runtime_error(message);
        Send(out + "]}");
    }


    // ── text (step 1D) ───────────────────────────────────────────────────────

    struct Ch { int c; bool hasBox; float x0, y0, x1, y1; };   // line ends are { '\n', no box }
    struct PageText { std::vector<Ch> chars; fz_rect bounds{}; fz_matrix ctm{ 1, 0, 0, 1, 0, 0 }; };

    PageText ExtractText(Doc& d, int page) {
        PageText out;
        bool failed = false;
        std::string message;
        fz_var(failed);
        fidelity::ResolveUnembeddedFonts(ctx_, d.doc, page, d.extras);   // the same fonts as the rendering: the boxes line up with the picture
        fidelity::TurnIntoStamps(ctx_, d.doc, page, false, d.extras);
        fz_page* p = nullptr;
        fz_stext_page* st = nullptr;
        fz_var(p);
        fz_var(st);
        fz_try(ctx_) {
            p = fz_load_page(ctx_, d.doc, page);
            out.bounds = fz_bound_page(ctx_, p);
            if (pdf_page* pp = pdf_page_from_fz_page(ctx_, p)) { fz_rect box; pdf_page_transform(ctx_, pp, &box, &out.ctm); }
            fz_stext_options opts;
            fz_init_stext_options(ctx_, &opts);
            opts.flags = FZ_STEXT_PRESERVE_LIGATURES | FZ_STEXT_PRESERVE_WHITESPACE | FZ_STEXT_CLIP | FZ_STEXT_USE_CID_FOR_UNKNOWN_UNICODE;
            st = fz_new_stext_page_from_page(ctx_, p, &opts);
            for (fz_stext_block* b = st->first_block; b; b = b->next) {
                if (b->type != FZ_STEXT_BLOCK_TEXT) continue;
                for (fz_stext_line* l = b->u.t.first_line; l; l = l->next) {
                    for (fz_stext_char* ch = l->first_char; ch; ch = ch->next) {
                        fz_rect r = fz_rect_from_quad(ch->quad);
                        out.chars.push_back({ ch->c, true, r.x0, r.y0, r.x1, r.y1 });
                    }
                    out.chars.push_back({ '\n', false, 0, 0, 0, 0 });
                }
            }
        }
        fz_always(ctx_) {
            if (st) fz_drop_stext_page(ctx_, st);
            if (p) fz_drop_page(ctx_, p);
        }
        fz_catch(ctx_) { failed = true; message = fz_caught_message(ctx_); }
        if (failed) throw std::runtime_error(message);
        return out;
    }

    static void AppendUtf8(std::string& s, unsigned cp) {
        if (cp < 0x80) s += (char)cp;
        else if (cp < 0x800) { s += (char)(0xC0 | (cp >> 6)); s += (char)(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) { s += (char)(0xE0 | (cp >> 12)); s += (char)(0x80 | ((cp >> 6) & 0x3F)); s += (char)(0x80 | (cp & 0x3F)); }
        else { s += (char)(0xF0 | (cp >> 18)); s += (char)(0x80 | ((cp >> 12) & 0x3F)); s += (char)(0x80 | ((cp >> 6) & 0x3F)); s += (char)(0x80 | (cp & 0x3F)); }
    }

    static std::string TextOf(const std::vector<Ch>& chars, size_t from, size_t to) {
        std::string s;
        for (size_t i = from; i < to && i < chars.size(); ++i) AppendUtf8(s, (unsigned)chars[i].c);
        return s;
    }

    static std::string Rect(const PageText& t, float x0, float y0, float x1, float y1) {
        double w = t.bounds.x1 - t.bounds.x0, h = t.bounds.y1 - t.bounds.y0;
        char buf[160];
        std::snprintf(buf, sizeof buf, "[%.6f,%.6f,%.6f,%.6f]", (x0 - t.bounds.x0) / w, (y0 - t.bounds.y0) / h, (x1 - t.bounds.x0) / w, (y1 - t.bounds.y0) / h);
        return buf;
    }

    static std::string Rects(const PageText& t, size_t from, size_t to) {
        std::string out = "[";
        bool first = true;
        for (size_t i = from; i < to && i < t.chars.size(); ++i) {
            const Ch& c = t.chars[i];
            if (!c.hasBox) continue;
            if (!first) out += ",";
            first = false;
            out += Rect(t, c.x0, c.y0, c.x1, c.y1);
        }
        return out + "]";
    }

    // Word boxes of one page (the I-beam hit areas): words are cut at spaces and at the end of a line.
    void Words(const Json& request) {
        std::string stamp;
        Doc& d = Open(request, stamp);
        PageText t = ExtractText(d, request.integer("page"));
        std::string out = "{\"rects\":[";
        bool first = true;
        bool open = false;
        float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        auto flush = [&]() {
            if (!open) return;
            if (!first) out += ",";
            first = false;
            out += Rect(t, x0, y0, x1, y1);
            open = false;
        };
        for (const Ch& c : t.chars) {
            if (!c.hasBox) { flush(); continue; }        // end of line
            if (c.c == ' ' || c.c == 0xA0 || c.c == '\t') { flush(); continue; }
            if (!open) { x0 = c.x0; y0 = c.y0; x1 = c.x1; y1 = c.y1; open = true; }
            else { x0 = std::min(x0, c.x0); y0 = std::min(y0, c.y0); x1 = std::max(x1, c.x1); y1 = std::max(y1, c.y1); }
        }
        flush();
        Send(out + "]}");
    }

    // Text between two points (user space, as the reader gives them): the characters from the nearest one to the other nearest one.
    void Select(const Json& request) {
        std::string stamp;
        Doc& d = Open(request, stamp);
        PageText t = ExtractText(d, request.integer("page"));
        const Json* data = request.find("data");
        double v[4] = { 0, 0, 0, 0 };
        if (data) { v[0] = data->num("ax"); v[1] = data->num("ay"); v[2] = data->num("bx"); v[3] = data->num("by"); }
        fz_point pts[2] = { fz_transform_point_xy((float)v[0], (float)v[1], t.ctm), fz_transform_point_xy((float)v[2], (float)v[3], t.ctm) };
        long indices[2] = { -1, -1 };
        int found = 0;
        for (int k = 0; k < 2; ++k) {
            double best = 1e300;
            long bestIndex = -1;
            for (size_t i = 0; i < t.chars.size(); ++i) {
                const Ch& c = t.chars[i];
                if (!c.hasBox) continue;
                double dx = std::max({ (double)c.x0 - pts[k].x, 0.0, pts[k].x - (double)c.x1 });
                double dy = std::max({ (double)c.y0 - pts[k].y, 0.0, pts[k].y - (double)c.y1 });
                double dist = dx * dx + dy * dy;
                if (dist < best) { best = dist; bestIndex = (long)i; }
            }
            if (bestIndex < 0 || best > 400) break;
            indices[k] = bestIndex;
            ++found;
        }
        if (found != 2) { Send("{\"text\":\"\",\"rects\":[]}"); return; }
        size_t from = (size_t)std::min(indices[0], indices[1]), to = (size_t)std::max(indices[0], indices[1]) + 1;
        Send("{\"text\":" + JsonQuote(TextOf(t.chars, from, to)) + ",\"rects\":" + Rects(t, from, to) + "}");
    }

    static std::vector<unsigned> Codepoints(const std::string& utf8) {
        std::vector<unsigned> out;
        for (size_t i = 0; i < utf8.size();) {
            unsigned char c = (unsigned char)utf8[i];
            unsigned cp; int n;
            if (c < 0x80) { cp = c; n = 1; }
            else if ((c >> 5) == 6) { cp = c & 0x1F; n = 2; }
            else if ((c >> 4) == 14) { cp = c & 0x0F; n = 3; }
            else { cp = c & 0x07; n = 4; }
            for (int k = 1; k < n && i + k < utf8.size(); ++k) cp = (cp << 6) | ((unsigned char)utf8[i + k] & 0x3F);
            out.push_back(cp);
            i += (size_t)n;
        }
        return out;
    }

    static unsigned Fold(unsigned c) { return c < 0x10000 ? (unsigned)(wchar_t)std::towlower((wint_t)c) : c; }
    static bool WordChar(unsigned c) { return c == '_' || (c < 0x10000 && std::iswalnum((wint_t)c)); }

    // The matches of the query in the text of one page (at most 200): snippet and the boxes of the characters.
    std::string Matches(const PageText& t, const std::vector<unsigned>& query, bool matchCase, bool wholeWord, bool& hasText) {
        std::vector<unsigned> text;
        text.reserve(t.chars.size());
        hasText = false;
        for (const Ch& c : t.chars) { text.push_back((unsigned)c.c); if (c.hasBox && c.c > ' ' && c.c != 0xA0) hasText = true; }
        std::string out = "[";
        int count = 0;
        if (!query.empty() && text.size() >= query.size()) {
            for (size_t i = 0; i + query.size() <= text.size() && count < 200; ++i) {
                bool same = true;
                for (size_t k = 0; k < query.size() && same; ++k)
                    same = matchCase ? text[i + k] == query[k] : Fold(text[i + k]) == Fold(query[k]);
                if (!same) continue;
                if (wholeWord) {
                    if (i > 0 && WordChar(text[i - 1])) continue;
                    if (i + query.size() < text.size() && WordChar(text[i + query.size()])) continue;
                }
                size_t from = i, to = i + query.size();
                size_t s0 = from > 35 ? from - 35 : 0, s1 = std::min(text.size(), to + 35);
                std::string snippet;
                for (size_t k = s0; k < s1; ++k) AppendUtf8(snippet, text[k] == '\n' ? ' ' : text[k]);
                if (count) out += ",";
                out += "{\"snippet\":" + JsonQuote(snippet) + ",\"rects\":" + Rects(t, from, to) + "}";
                ++count;
                i = to - 1;   // matches do not overlap (as re.finditer)
            }
        }
        return out + "]";
    }

    void Search(const Json& request) {
        std::string stamp;
        Doc& d = Open(request, stamp);
        PageText t = ExtractText(d, request.integer("page"));
        const Json* data = request.find("data");
        bool hasText = false;
        std::string matches = Matches(t, Codepoints(data ? data->str("query") : ""), data && data->flag("matchCase"), data && data->flag("wholeWord"), hasText);
        Send(std::string("{\"hasText\":") + (hasText ? "true" : "false") + ",\"matches\":" + matches + "}");
    }

    void SearchRange(const Json& request) {
        std::string stamp;
        Doc& d = Open(request, stamp);
        const Json* data = request.find("data");
        int first = request.integer("page");
        int count = data ? data->integer("count") : 1;
        auto query = Codepoints(data ? data->str("query") : "");
        bool matchCase = data && data->flag("matchCase"), wholeWord = data && data->flag("wholeWord");
        std::string out = "{\"pages\":[";
        for (int index = first; index < std::min(d.pageCount, first + count); ++index) {
            PageText t = ExtractText(d, index);
            bool hasText = false;
            std::string matches = Matches(t, query, matchCase, wholeWord, hasText);
            if (index > first) out += ",";
            out += "{\"hasText\":" + std::string(hasText ? "true" : "false") + ",\"matches\":" + matches + ",\"page\":" + std::to_string(index) + "}";
        }
        Send(out + "]}");
    }

    ListEntry& DisplayList(Doc& d, const std::string& stamp, int page, bool annotations, fz_cookie* cookie) {
        std::string key = stamp + "#" + std::to_string(page) + (annotations ? "a" : "c");
        auto found = lists_.find(key);
        if (found != lists_.end()) {
            listOrder_.remove(key);
            listOrder_.push_front(key);
            return found->second;
        }
        ListEntry entry;
        auto buildStart = Clock::now();
        fz_var(entry.list);
        bool failed = false;
        std::string message;
        fz_var(failed);
        fz_try(ctx_) {
            fidelity::ResolveUnembeddedFonts(ctx_, d.doc, page, d.extras);   // same fonts as the rendering and the text operations
            fidelity::TurnIntoStamps(ctx_, d.doc, page, annotations, d.extras);
            fz_page* p = fz_load_page(ctx_, d.doc, page);
            fz_try(ctx_) {
                pdf_page* pp = pdf_page_from_fz_page(ctx_, p);
                auto sig = d.extras.signatures.find(page);
                bool hasSignatures = sig != d.extras.signatures.end() && !sig->second.empty();
                bool hasWidgets = pp && pdf_first_widget(ctx_, pp) != nullptr;
                fz_rect bound = fz_bound_page(ctx_, p);
                entry.list = fz_new_display_list(ctx_, bound);
                fz_device* dev = fz_new_list_device(ctx_, entry.list);
                fz_matrix identity = { 1, 0, 0, 1, 0, 0 };
                fz_try(ctx_) {
                    if (annotations) fz_run_page(ctx_, p, dev, identity, cookie);
                    else {
                        fz_run_page_contents(ctx_, p, dev, identity, cookie);
                        // Widgets and stamped signatures remain visible without annotations.
                        if (hasWidgets || hasSignatures) fz_run_page_widgets(ctx_, p, dev, identity, cookie);
                        if (hasSignatures && pp)
                            for (pdf_annot* a = pdf_first_annot(ctx_, pp); a; a = pdf_next_annot(ctx_, a))
                                if (sig->second.count(pdf_to_num(ctx_, pdf_annot_obj(ctx_, a)))) pdf_run_annot(ctx_, a, dev, identity, cookie);
                    }
                    fz_close_device(ctx_, dev);
                    if (cookie->abort) fz_throw(ctx_, FZ_ERROR_ABORT, "render cancelled");
                }
                fz_always(ctx_) { fz_drop_device(ctx_, dev); }
                fz_catch(ctx_) { fz_rethrow(ctx_); }
                entry.bounds = fz_bound_display_list(ctx_, entry.list);
            }
            fz_always(ctx_) { fz_drop_page(ctx_, p); }
            fz_catch(ctx_) { fz_rethrow(ctx_); }
        }
        fz_catch(ctx_) { failed = true; message = fz_caught_message(ctx_); }
        if (failed) { fz_drop_display_list(ctx_, entry.list); throw std::runtime_error(cookie->abort ? "render cancelled" : message); }
        entry.buildMs = MillisSince(buildStart);
        while (lists_.size() >= listLimit_ && !listOrder_.empty()) {
            std::string oldest = listOrder_.back();
            listOrder_.pop_back();
            auto it = lists_.find(oldest);
            if (it != lists_.end()) { fz_drop_display_list(ctx_, it->second.list); lists_.erase(it); }
        }
        listOrder_.push_front(key);
        return lists_.emplace(key, entry).first->second;
    }

#ifdef XT_PATCHED_MUPDF
    int renderThreads_ = 1;
    size_t bandMinPixels_ = 2000000;   // used before the cost of a page is known
    double bandMinMilliseconds_ = 25;  // estimated single-thread cost from which bands are used
    std::vector<fz_context*> bandContexts_;   // [0] is ctx_ (the calling thread); the others are clones

    // Renders `bbox` (device pixels, written straight into `output`) in bands. Every band is
    // drawn as a part of the whole area: the device keeps the scissor of the whole-area render
    // (fz_draw_set_virtual_scissor), so geometry, dashes, tiles, groups and masks resolve exactly
    // as in a single render. The display list is culled with a margin that covers the minimum
    // line width, so objects entirely outside the band are skipped.
    void RenderBands(fz_display_list* list, fz_matrix m, fz_irect bbox, fz_rect clip, uint8_t* output,
                     int channels, bool alpha, float minWidthPx, fz_cookie* cookie) {
        const int rows = bbox.y1 - bbox.y0, width = bbox.x1 - bbox.x0;
        const int threads = std::min(renderThreads_, std::max(1, rows / 64));
        // Every band scans the whole display list to cull it, so keep the band count modest for
        // medium areas; large areas get more bands for load balance (heavy rows differ a lot).
        const size_t area = (size_t)rows * (size_t)width;
        const int perThread = area >= 8000000 ? 4 : 2;
        const int bands = std::max(threads, std::min(rows / 32, threads * perThread));
        while ((int)bandContexts_.size() < threads) {
            fz_context* c = bandContexts_.empty() ? ctx_ : fz_clone_context(ctx_);
            if (!c) throw std::runtime_error("cannot clone the MuPDF context");
            bandContexts_.push_back(c);
        }
        static const double configuredMargin = EnvNumber("XTPDF_BAND_MARGIN_PX", -1);
        const float margin = configuredMargin >= 0 ? (float)configuredMargin : (float)(8.0 + 12.0 * minWidthPx);
        const fz_matrix inverse = fz_invert_matrix(m);
        std::atomic<int> next{ 0 };
        std::mutex failure;
        bool failed = false;
        std::string message;
        auto work = [&](fz_context* c) {
            fz_set_graphics_min_line_width(c, minWidthPx);
            // MuPDF counts progress in the cookie for every object; one cookie shared by all
            // threads would make them fight over a cache line. Each thread has its own and
            // looks at the shared cancellation flag between bands.
            fz_cookie local{};
            for (;;) {
                int band = next.fetch_add(1);
                if (band >= bands || *(volatile int*)&cookie->abort) break;
                int y0 = bbox.y0 + (int)((long long)rows * band / bands);
                int y1 = bbox.y0 + (int)((long long)rows * (band + 1) / bands);
                fz_pixmap* pix = nullptr;
                fz_device* dev = nullptr;
                fz_var(pix);
                fz_var(dev);
                fz_try(c) {
                    fz_irect part = { bbox.x0, y0, bbox.x1, y1 };
                    pix = fz_new_pixmap_with_bbox_and_data(c, fz_device_bgr(c), part, nullptr, alpha ? 1 : 0,
                        output + (size_t)(y0 - bbox.y0) * (size_t)width * channels);
                    dev = fz_new_draw_device(c, m, pix);
                    fz_draw_set_virtual_scissor(c, dev, bbox);
                    fz_rect device = { (float)bbox.x0 - margin, (float)y0 - margin, (float)bbox.x1 + margin, (float)y1 + margin };
                    fz_rect bandClip = fz_intersect_rect(fz_transform_rect(device, inverse), clip);
                    fz_matrix identity = { 1, 0, 0, 1, 0, 0 };
                    fz_run_display_list(c, list, dev, identity, bandClip, &local);
                    fz_close_device(c, dev);
                    if (gamma_ > 1.0f) fz_gamma_pixmap(c, pix, gamma_);
                }
                fz_always(c) {
                    fz_drop_device(c, dev);
                    fz_drop_pixmap(c, pix);
                }
                fz_catch(c) {
                    std::lock_guard<std::mutex> guard(failure);
                    if (!failed) { failed = true; message = fz_caught_message(c); }
                    next.store(bands);
                }
                local.abort = 0;
            }
        };
        std::vector<std::thread> pool;
        for (int t = 1; t < threads; ++t) pool.emplace_back(work, bandContexts_[t]);
        work(bandContexts_[0]);
        for (auto& th : pool) th.join();
        if (cookie->abort) throw std::runtime_error("render cancelled");
        if (failed) throw std::runtime_error(message);
    }
#endif

    void Render(const Json& request) {
        auto prepare = Clock::now();
        RenderCancellation cancellation(request.str("cancelEvent"));
        cancellation.Check();
        std::string stamp;
        Doc& d = Open(request, stamp);
        int page = request.integer("page");
        if (page < 0 || page >= d.pageCount) throw std::runtime_error("page out of range");
        bool annotations = request.flag("annotations");
        bool alpha = request.flag("alpha");
        // Use the authenticated document stamp and every input affecting the pixels.
        // Preserve double precision: displayWidth changes the minimum stroke width.
        std::ostringstream cacheKey;
        cacheKey << std::setprecision(17) << stamp.size() << ':' << stamp << ':' << page << ':'
            << annotations << ':' << alpha << ':' << request.integer("fullWidth") << ':'
            << request.integer("fullHeight") << ':' << request.num("displayWidth");
        if (auto* rect = request.find("rect"); rect && rect->type == Json::Array)
            for (auto& value : rect->items) cacheKey << ':' << value.number;
        std::string key = cacheKey.str();
        auto cached = rasters_.find(key);
        if (cached != rasters_.end()) {
            ++rasterHits_;
            rasterOrder_.remove(key);
            rasterOrder_.push_front(key);
            SendRaster(cached->second.header, cached->second.pixels);
            return;
        }
        ++rasterMisses_;
        cancellation.Check();
        ListEntry& entry = DisplayList(d, stamp, page, annotations, &cancellation.cookie);
        cancellation.Check();
        double prepareMs = MillisSince(prepare);

        int fullW = request.integer("fullWidth"), fullH = request.integer("fullHeight");
        int x = 0, y = 0, w = 0, h = 0;
        if (auto* rect = request.find("rect"); rect && rect->type == Json::Array && rect->items.size() == 4) {
            x = (int)rect->items[0].number; y = (int)rect->items[1].number; w = (int)rect->items[2].number; h = (int)rect->items[3].number;
        }
        double pw = entry.bounds.x1 - entry.bounds.x0, ph = entry.bounds.y1 - entry.bounds.y0;
        if (pw <= 0 || ph <= 0) throw std::runtime_error("empty page");
        if (fullH == 0) {
            fullH = std::max(1, (int)std::lround(fullW * ph / pw));
            x = 0; y = 0; w = fullW; h = fullH;
        }
        if (fullW <= 0 || fullH <= 0 || w <= 0 || h <= 0 || (long long)w * h > 64LL * 1024 * 1024)
            throw std::runtime_error("Invalid or oversized raster request; use viewport crops for very long pages");

        // A page is drawn wider than it is shown: the minimum line width grows by that ratio so a 1 px line stays 1 px on screen.
        double displayWidth = request.num("displayWidth");
        double ratio = displayWidth > 0 ? fullW / displayWidth : (fullW >= 800 ? 1.0 : 0.8);
        fz_set_graphics_min_line_width(ctx_, (float)(minLine_ * std::min(std::max(ratio, 0.5), 4.0)));

        auto start = Clock::now();
        const int channels = alpha ? 4 : 3;
        size_t outputSize = (size_t)w * h * channels;
        // The app already caches immutable WPF images. Shared output avoids both
        // pipe pixel transfer and an intermediate managed byte array in that mode.
        uint8_t* output = request.flag("sharedMemory") && !rasterLimit_ ? sharedRaster_.Acquire(outputSize) : nullptr;
        bool shared = output != nullptr;
        if (!shared) sharedRaster_.Reset();
        std::vector<uint8_t> out;
        if (shared) std::memset(output, alpha ? 0x00 : 0xFF, outputSize);
        else { out.assign(outputSize, alpha ? 0x00 : 0xFF); output = out.data(); }
        int px = 0, py = 0, pwid = 0, phei = 0, stride = 0;
        bool bandedUsed = false;
        int cellForLearning = 0;
        bool wholeForLearning = false;
        bool failed = false;
        std::string message;
        fz_var(failed);
        fz_pixmap* pix = nullptr;
        fz_var(pix);
        fz_try(ctx_) {
            fz_matrix scale = fz_scale((float)(fullW / pw), (float)(fullH / ph));
            fz_rect clip = { (float)(entry.bounds.x0 + x * pw / fullW), (float)(entry.bounds.y0 + y * ph / fullH),
                             (float)(entry.bounds.x0 + (x + w) * pw / fullW), (float)(entry.bounds.y0 + (y + h) * ph / fullH) };
            // The page is moved so that its bounds start at the origin (PyMuPDF's display list works in page space from 0,0).
            fz_matrix toOrigin = fz_translate(-entry.bounds.x0, -entry.bounds.y0);
            fz_matrix m = fz_concat(toOrigin, scale);
            fz_rect pixelRect = fz_transform_rect(clip, m);
            fz_irect bbox = fz_round_rect(pixelRect);
            // Draw into the wire buffer when the transformed crop has exact pixel bounds.
            // MuPDF borrows this memory; out stays alive until after fz_drop_pixmap.
            bool direct = bbox.x0 == x && bbox.y0 == y && bbox.x1 == x+w && bbox.y1 == y+h;
            if (direct) pix = fz_new_pixmap_with_bbox_and_data(ctx_, fz_device_bgr(ctx_), bbox, nullptr, alpha ? 1 : 0, output);
            else {
                pix = fz_new_pixmap_with_bbox(ctx_, fz_device_bgr(ctx_), bbox, nullptr, alpha ? 1 : 0);
                if (alpha) fz_clear_pixmap(ctx_, pix);
                else fz_clear_pixmap_with_value(ctx_, pix, 0xFF);
            }
            bool banded = false;
            const bool wholePage = x == 0 && y == 0 && w >= fullW && h >= fullH;
            const int cell = std::clamp((int)(((long long)y + h / 2) * 8 / std::max(1, fullH)), 0, 7) * 8 +
                             std::clamp((int)(((long long)x + w / 2) * 8 / std::max(1, fullW)), 0, 7);
            cellForLearning = cell; wholeForLearning = wholePage;
#ifdef XT_PATCHED_MUPDF
            {
                // Bands pay off when the render is expensive, not when it is large: a sparse viewport
                // tile of 1 Mpx takes 5 ms, a dense full page of 1 Mpx takes 150 ms. After the first
                // render of a page the cost per megapixel is known and decides; before that the area does.
                const double mpx = (double)w * (double)h / 1e6;
                // The first render of a whole page has no history, but the time its list took to build is a
                // good predictor: rendering a page costs about 1.5 x that per megapixel (measured on dense CAD).
                double known = wholePage ? entry.fullCost : entry.cellCost[cell];
                if (known <= 0 && wholePage && entry.buildMs > 0) known = 1.5 * entry.buildMs;
                const bool heavy = known > 0 ? known * mpx >= bandMinMilliseconds_
                                             : (size_t)w * (size_t)h >= bandMinPixels_;
                banded = direct && renderThreads_ > 1 && h >= 128 && mpx >= 0.25 && heavy;
            }
            if (banded) {
                // Exceptions must not cross MuPDF frames; a failure comes back as one message.
                std::string bandError;
                try {
                    RenderBands(entry.list, m, bbox, clip, output, channels, alpha != 0,
                        (float)(minLine_ * std::min(std::max(ratio, 0.5), 4.0)), &cancellation.cookie);
                } catch (const std::exception& e) { bandError = e.what(); }
                if (!bandError.empty()) fz_throw(ctx_, cancellation.cookie.abort ? FZ_ERROR_ABORT : FZ_ERROR_GENERIC, "%s", bandError.c_str());
            }
#endif
            bandedUsed = banded;
            if (!banded) {
                fz_device* dev = fz_new_draw_device(ctx_, m, pix);
                fz_try(ctx_) {
                    fz_matrix identity = { 1, 0, 0, 1, 0, 0 };
                    fz_run_display_list(ctx_, entry.list, dev, identity, clip, &cancellation.cookie); // scissor is in list space
                    fz_close_device(ctx_, dev);
                    if (cancellation.cookie.abort) fz_throw(ctx_, FZ_ERROR_ABORT, "render cancelled");
                }
                fz_always(ctx_) { fz_drop_device(ctx_, dev); }
                fz_catch(ctx_) { fz_rethrow(ctx_); }
                if (gamma_ > 1.0f) fz_gamma_pixmap(ctx_, pix, gamma_);
            }
            px = pix->x; py = pix->y; pwid = pix->w; phei = pix->h; stride = (int)pix->stride;
            // Keep the requested global pixel origin: copy the overlap into the exact w x h picture (white elsewhere).
            int left = std::max(x, px), right = std::min(x + w, px + pwid);
            int top = std::max(y, py), bottom = std::min(y + h, py + phei);
            const uint8_t* samples = pix->samples;
            if (!direct && right > left)
                for (int row = top; row < bottom; ++row) {
                    const uint8_t* src = samples + (size_t)(row - py) * stride + (size_t)(left - px) * pix->n;
                    uint8_t* dst = output + ((size_t)(row - y) * w + (left - x)) * channels;
                    if (pix->n == channels) std::memcpy(dst, src, (size_t)(right - left) * channels);
                    else for (int col = 0; col < right - left; ++col) std::memcpy(dst + col * channels, src + col * pix->n, channels);
                }
        }
        fz_always(ctx_) { if (pix) fz_drop_pixmap(ctx_, pix); }
        fz_catch(ctx_) { failed = true; message = fz_caught_message(ctx_); }
        if (failed) throw std::runtime_error(cancellation.cookie.abort ? "render cancelled" : message);
        cancellation.Check();
        double renderMs = MillisSince(start);
        {
            // Remember the single-thread cost per megapixel of this page. A banded render used several
            // cores; about 0.8 of them were useful work on this machine.
            double mpx = (double)w * (double)h / 1e6;
            if (mpx > 0.05) {
                double serial = renderMs;
#ifdef XT_PATCHED_MUPDF
                if (bandedUsed) serial = renderMs * std::min(renderThreads_, 4) * 0.8;
#endif
                double& slot = wholeForLearning ? entry.fullCost : entry.cellCost[cellForLearning];
                slot = slot > 0 ? 0.5 * slot + 0.5 * serial / mpx : serial / mpx;
            }
        }

        std::string transport = shared ? ",\"sharedMemory\":" + JsonQuote(sharedRaster_.Name()) +
            ",\"sharedCapacity\":" + std::to_string(sharedRaster_.Capacity()) : "";
        char header[512];
        int n = std::snprintf(header, sizeof header,
            "{\"width\":%d,\"height\":%d,\"stride\":%d,\"format\":\"%s\",\"length\":%zu,\"prepareMs\":%.2f,\"renderMs\":%.2f%s}\n",
            w, h, w * channels, alpha ? "bgra" : "bgr", outputSize, prepareMs, renderMs, transport.c_str());
        std::string reply(header, (size_t)n);
        if (shared) {
            ++sharedFrames_;
            std::fwrite(reply.data(), 1, reply.size(), stdout); std::fflush(stdout);
            return;
        }
        // Oversized renders are sent but never retained; evict before inserting.
        if (rasterLimit_ && out.size() <= rasterLimit_) {
            TrimRasters(rasterLimit_ - out.size());
            auto inserted = rasters_.emplace(key, RasterEntry{ stamp, reply, std::move(out) }).first;
            rasterOrder_.push_front(key);
            rasterBytes_ += inserted->second.pixels.size();
            SendRaster(inserted->second.header, inserted->second.pixels);
        } else SendRaster(reply, out);
    }
};

int wmain(int argc, wchar_t** argv) {
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stdin), _O_BINARY);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    Worker worker;
    if (argc == 3 && std::wstring(argv[1]) == L"--ocr") return worker.RunJob(argv[2], true);
    if (argc == 3 && std::wstring(argv[1]) == L"--textedit") return worker.RunJob(argv[2], false);
    worker.Run();
    return 0;
}
