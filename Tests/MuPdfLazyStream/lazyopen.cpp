// Experiment: can MuPDF read a PDF on a slow network share the way Foxit does (only the blocks it needs, a few big
// round trips) instead of a local copy of the whole file?  The document is opened through a custom fz_stream that is
// backed by a block cache over ReadFile.  Compared with fz_open_document(path) (MuPDF's own stdio reads).
//
//   lazyopen <file.pdf> direct
//   lazyopen <file.pdf> lazy  [blockKB=256] [nocache=0|1] [readahead=0..n blocks]
//
// Steps timed: open + page count, first page bounds, render page 1 at 1200 px, bounds of every page.
#include <mupdf/fitz.h>
#include <windows.h>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>
#include <vector>

using clock_type = std::chrono::steady_clock;
static double seconds_since(clock_type::time_point t) { return std::chrono::duration<double>(clock_type::now() - t).count(); }

struct LazyFile {
    HANDLE handle = INVALID_HANDLE_VALUE;
    int64_t size = 0;
    size_t block = 256 * 1024;
    bool nocache = false;
    int readahead = 0;
    std::unordered_map<int64_t, uint8_t*> blocks;
    // statistics
    int64_t fetches = 0, bytes = 0;
    double fetch_seconds = 0;
    int64_t lastBlock = -2;
};

static bool fetch_block(LazyFile* f, int64_t index)
{
    if (f->blocks.count(index)) return true;
    int64_t offset = index * (int64_t)f->block;
    if (offset >= f->size) return false;
    size_t want = (size_t)std::min<int64_t>((int64_t)f->block, f->size - offset);
    // NO_BUFFERING needs a sector aligned offset, length and buffer: round the length up, the file end returns less.
    size_t alloc = (want + 4095) & ~(size_t)4095;
    auto* data = (uint8_t*)_aligned_malloc(alloc, 4096);
    LARGE_INTEGER at; at.QuadPart = offset;
    OVERLAPPED ov{}; ov.Offset = at.LowPart; ov.OffsetHigh = (DWORD)at.HighPart;
    DWORD got = 0;
    auto t = clock_type::now();
    BOOL ok = ReadFile(f->handle, data, (DWORD)alloc, &got, &ov);
    if (!ok && GetLastError() != ERROR_HANDLE_EOF) { _aligned_free(data); return false; }
    f->fetch_seconds += seconds_since(t);
    f->fetches++;
    f->bytes += got;
    if (got < want) { _aligned_free(data); return false; }
    f->blocks[index] = data;
    return true;
}

static bool ensure_block(LazyFile* f, int64_t index)
{
    bool sequential = index == f->lastBlock + 1;
    if (!fetch_block(f, index)) return false;
    f->lastBlock = index;
    // A forward read-ahead only when the reads are going forward (a content stream); random object reads do not trigger it.
    if (sequential)
        for (int i = 1; i <= f->readahead; ++i) fetch_block(f, index + i);
    return true;
}

struct LazyStream { LazyFile* file; int64_t at; };   // `at` = file position of stm->wp, like the stdio stream of MuPDF

static int lazy_next(fz_context* ctx, fz_stream* stm, size_t)
{
    auto* s = (LazyStream*)stm->state;
    LazyFile* f = s->file;
    if (s->at >= f->size) { stm->rp = stm->wp = nullptr; return EOF; }
    int64_t index = s->at / (int64_t)f->block;
    if (!ensure_block(f, index)) fz_throw(ctx, FZ_ERROR_SYSTEM, "read error at %lld", (long long)s->at);
    uint8_t* block = f->blocks[index];
    size_t inside = (size_t)(s->at - index * (int64_t)f->block);
    size_t avail = (size_t)std::min<int64_t>((int64_t)f->block, f->size - index * (int64_t)f->block) - inside;
    stm->rp = block + inside;
    stm->wp = stm->rp + avail;
    s->at += (int64_t)avail;
    stm->pos = s->at;
    return *stm->rp++;
}

static void lazy_seek(fz_context* ctx, fz_stream* stm, int64_t offset, int whence)
{
    auto* s = (LazyStream*)stm->state;
    int64_t target = whence == SEEK_SET ? offset : whence == SEEK_CUR ? (stm->pos - (stm->wp - stm->rp)) + offset : s->file->size + offset;
    if (target < 0) target = 0;
    if (target > s->file->size) target = s->file->size;
    s->at = target;
    stm->pos = target;
    stm->rp = stm->wp = nullptr;
}

static void lazy_drop(fz_context* ctx, void* state) { delete (LazyStream*)state; }

static fz_stream* open_lazy(fz_context* ctx, LazyFile* f)
{
    auto* s = new LazyStream{ f, 0 };
    fz_stream* stm = fz_new_stream(ctx, s, lazy_next, lazy_drop);
    stm->seek = lazy_seek;
    return stm;
}

static fz_rect bound_page(fz_context* ctx, fz_document* doc, int number)
{
    fz_page* page = fz_load_page(ctx, doc, number);
    fz_rect r = fz_bound_page(ctx, page);
    fz_drop_page(ctx, page);
    return r;
}

int main(int argc, char** argv)
{
    if (argc < 3) { std::puts("usage: lazyopen <file.pdf> direct|lazy [blockKB] [nocache] [readahead]"); return 2; }
    std::string path = argv[1], mode = argv[2];
    fz_context* ctx = fz_new_context(nullptr, nullptr, 256 << 20);
    if (!ctx) return 3;
    fz_register_document_handlers(ctx);

    LazyFile file;
    if (mode == "lazy") {
        if (argc > 3) file.block = (size_t)std::atoi(argv[3]) * 1024;
        if (argc > 4) file.nocache = std::atoi(argv[4]) != 0;
        if (argc > 5) file.readahead = std::atoi(argv[5]);
        DWORD flags = FILE_FLAG_RANDOM_ACCESS | FILE_FLAG_OVERLAPPED * 0 | (file.nocache ? FILE_FLAG_NO_BUFFERING : 0);
        file.handle = CreateFileA(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, flags, nullptr);
        if (file.handle == INVALID_HANDLE_VALUE) { std::printf("cannot open (%lu)\n", GetLastError()); return 4; }
        LARGE_INTEGER size; GetFileSizeEx(file.handle, &size); file.size = size.QuadPart;
        std::printf("lazy: block %zu KB, nocache=%d, readahead=%d, file %.1f MB\n", file.block / 1024, file.nocache, file.readahead, file.size / 1e6);
    } else std::printf("direct (fz_open_document on the path)\n");

    fz_document* doc = nullptr;
    int pages = 0;
    int failed = 0;
    fz_var(doc); fz_var(pages); fz_var(failed);
    auto t0 = clock_type::now();
    fz_try(ctx) {
        if (mode == "lazy") {
            fz_stream* stm = open_lazy(ctx, &file);
            doc = fz_open_document_with_stream(ctx, ".pdf", stm);   // stm is taken over by the document
            fz_drop_stream(ctx, stm);
        } else doc = fz_open_document(ctx, path.c_str());
        pages = fz_count_pages(ctx, doc);
    } fz_catch(ctx) { std::printf("open failed: %s\n", fz_caught_message(ctx)); failed = 1; }
    if (failed) return 5;
    std::printf("open + page count (%d pages): %.2fs   fetches %lld  %.1f MB\n", pages, seconds_since(t0), (long long)file.fetches, file.bytes / 1e6);

    auto t1 = clock_type::now();
    fz_try(ctx) {
        fz_rect r = bound_page(ctx, doc, 0);
        std::printf("first page bounds %.0f x %.0f: %.2fs   fetches %lld  %.1f MB\n", r.x1 - r.x0, r.y1 - r.y0, seconds_since(t1), (long long)file.fetches, file.bytes / 1e6);
    } fz_catch(ctx) { std::printf("bounds failed: %s\n", fz_caught_message(ctx)); }

    auto t2 = clock_type::now();
    fz_try(ctx) {
        fz_rect r = bound_page(ctx, doc, 0);
        float zoom = 1200.0f / (r.x1 - r.x0);
        fz_pixmap* pix = fz_new_pixmap_from_page_number(ctx, doc, 0, fz_scale(zoom, zoom), fz_device_rgb(ctx), 0);
        std::printf("render page 1 (%d x %d): %.2fs   fetches %lld  %.1f MB\n", pix->w, pix->h, seconds_since(t2), (long long)file.fetches, file.bytes / 1e6);
        fz_drop_pixmap(ctx, pix);
    } fz_catch(ctx) { std::printf("render failed: %s\n", fz_caught_message(ctx)); }

    auto t3 = clock_type::now();
    int done = 0;
    fz_try(ctx) {
        for (int i = 0; i < pages; ++i) { bound_page(ctx, doc, i); ++done; }
    } fz_catch(ctx) { std::printf("sizes failed at %d: %s\n", done, fz_caught_message(ctx)); }
    std::printf("bounds of %d pages: %.2fs   fetches %lld  %.1f MB   (time inside ReadFile %.2fs)\n", done, seconds_since(t3), (long long)file.fetches, file.bytes / 1e6, file.fetch_seconds);

    fz_drop_document(ctx, doc);
    fz_drop_context(ctx);
    return 0;
}
