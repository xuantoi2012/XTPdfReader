#include <mupdf/fitz.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>

struct Session {
    std::mutex mutexes[FZ_LOCK_MAX];
    fz_locks_context locks{};
    fz_context* context = nullptr;
    fz_context* clones[2]{};
    fz_document* document = nullptr;
    fz_display_list* list = nullptr;
    fz_rect bounds{};
    int threads = 1;
    char error[512]{};
};
static thread_local char open_error[512]{};
static void lock_context(void* user, int lock) { static_cast<Session*>(user)->mutexes[lock].lock(); }
static void unlock_context(void* user, int lock) { static_cast<Session*>(user)->mutexes[lock].unlock(); }
#define API extern "C" __declspec(dllexport)

API void mv_close(Session* s) {
    if (!s) return;
    for (auto clone : s->clones) if (clone) fz_drop_context(clone);
    if (s->context) {
        fz_drop_display_list(s->context, s->list);
        fz_drop_document(s->context, s->document);
        fz_drop_context(s->context);
    }
    delete s;
}

API const char* mv_error(Session* s) { return s ? s->error : open_error; }

API Session* mv_open(const char* path, int threads) {
    auto s = new Session();
    s->threads = std::clamp(threads, 1, 2);
    s->locks = {s, lock_context, unlock_context};
    s->context = fz_new_context(nullptr, &s->locks, 128 << 20);
    if (!s->context) { std::snprintf(open_error, sizeof(open_error), "Context creation failed"); delete s; return nullptr; }
    int failed = 0;
    fz_var(failed);
    fz_try(s->context) {
        fz_register_document_handlers(s->context);
        s->document = fz_open_document(s->context, path);
        s->list = fz_new_display_list_from_page_number(s->context, s->document, 0);
        s->bounds = fz_bound_display_list(s->context, s->list);
        for (int i = 0; i < s->threads; ++i) {
            s->clones[i] = fz_clone_context(s->context);
            if (!s->clones[i]) fz_throw(s->context, FZ_ERROR_SYSTEM, "Context clone failed");
        }
    }
    fz_catch(s->context) {
        std::snprintf(open_error, sizeof(open_error), "%s", fz_caught_message(s->context));
        failed = 1;
    }
    if (failed) { mv_close(s); return nullptr; }
    return s;
}

static int render_band(Session* s, int index, fz_matrix matrix, fz_rect clip,
    fz_irect bbox, unsigned char* storage, char* error) {
    auto ctx = s->clones[index];
    fz_pixmap* pix = nullptr;
    fz_device* device = nullptr;
    int failed = 0;
    fz_var(pix); fz_var(device); fz_var(failed);
    fz_try(ctx) {
        pix = fz_new_pixmap_with_bbox_and_data(ctx, fz_device_rgb(ctx), bbox, nullptr, 0, storage);
        fz_clear_pixmap_with_value(ctx, pix, 255);
        device = fz_new_draw_device_with_bbox(ctx, matrix, pix, &bbox);
        // Both bands use the full-page matrix; clip only restricts traversal.
        fz_matrix identity = {1, 0, 0, 1, 0, 0};
        fz_run_display_list(ctx, s->list, device, identity, clip, nullptr);
        fz_close_device(ctx, device);
    }
    fz_always(ctx) {
        fz_drop_device(ctx, device);
        fz_drop_pixmap(ctx, pix);
    }
    fz_catch(ctx) {
        std::snprintf(error, 512, "%s", fz_caught_message(ctx));
        failed = 1;
    }
    return failed;
}

API int mv_render(Session* s, int full_width, int full_height, int x, int y,
    int width, int height, unsigned char* output) {
    if (!s || !output || full_width <= 0 || full_height <= 0 || width <= 0 || height <= 0 ||
        x < 0 || y < 0 || static_cast<long long>(x) + width > full_width ||
        static_cast<long long>(y) + height > full_height ||
        static_cast<long long>(width) * height * 3 > 128 * 1024 * 1024) return -1;
    auto sx = full_width / (s->bounds.x1 - s->bounds.x0);
    auto sy = full_height / (s->bounds.y1 - s->bounds.y0);
    fz_matrix matrix = {sx, 0, 0, sy, -s->bounds.x0 * sx, -s->bounds.y0 * sy};
    fz_rect clip = {s->bounds.x0 + x / sx, s->bounds.y0 + y / sy,
        s->bounds.x0 + (x + width) / sx, s->bounds.y0 + (y + height) / sy};
    try {
        int count = height < 2 ? 1 : s->threads;
        std::vector<unsigned char> storage[2];
        fz_irect boxes[2]{};
        int tops[2]{}, bottoms[2]{}, failed[2]{};
        char errors[2][512]{};
        for (int i = 0; i < count; ++i) {
            tops[i] = y + height * i / count;
            bottoms[i] = y + height * (i + 1) / count;
            boxes[i] = {x, std::max(y, tops[i] - 8), x + width, std::min(y + height, bottoms[i] + 8)};
            storage[i].resize(static_cast<size_t>(width) * (boxes[i].y1 - boxes[i].y0) * 3);
        }
        auto run = [&](int i) {
            fz_rect band_clip = {clip.x0, s->bounds.y0 + boxes[i].y0 / sy,
                clip.x1, s->bounds.y0 + boxes[i].y1 / sy};
            failed[i] = render_band(s, i, matrix, band_clip, boxes[i], storage[i].data(), errors[i]);
        };
        if (count == 2) {
            std::thread second(run, 1);
            run(0);
            second.join();
        } else run(0);
        for (int i = 0; i < count; ++i) {
            if (failed[i]) { std::snprintf(s->error, sizeof(s->error), "%s", errors[i]); return -1; }
            std::memcpy(output + static_cast<size_t>(tops[i] - y) * width * 3,
                storage[i].data() + static_cast<size_t>(tops[i] - boxes[i].y0) * width * 3,
                static_cast<size_t>(bottoms[i] - tops[i]) * width * 3);
        }
    } catch (const std::exception& error) {
        std::snprintf(s->error, sizeof(s->error), "%s", error.what());
        return -1;
    }
    return 0;
}
