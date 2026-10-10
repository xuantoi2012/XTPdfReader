// OCR jobs run in a separate native process. Same JSON-lines contract as OcrWorker.py.
#pragma once
#include <mupdf/fitz.h>
#include <algorithm>
#include <cmath>
#include <map>
#include <string>
#include <vector>

namespace nativeocr {
struct Word { std::string text; double x0, y0, x1, y1; };
using Lines = std::vector<std::vector<Word>>;
inline void Emit(const std::string& json) { std::fwrite(json.data(), 1, json.size(), stdout); std::fputc('\n', stdout); std::fflush(stdout); }
inline void Utf8(std::string& s, unsigned c) {
    if (c < 128) s += (char)c;
    else if (c < 2048) { s += (char)(192 | (c >> 6)); s += (char)(128 | (c & 63)); }
    else if (c < 65536) { s += (char)(224 | (c >> 12)); s += (char)(128 | ((c >> 6) & 63)); s += (char)(128 | (c & 63)); }
    else { s += (char)(240 | (c >> 18)); s += (char)(128 | ((c >> 12) & 63)); s += (char)(128 | ((c >> 6) & 63)); s += (char)(128 | (c & 63)); }
}

// MuPDF calls are caught before returning to C++; no native error escapes a job.
inline fz_pixmap* RenderGray(fz_context* ctx, fz_page* page, fz_rect clip, double scale) {
    fz_pixmap* pix = nullptr; fz_device* dev = nullptr;
    bool failed = false; std::string error;
    fz_var(pix); fz_var(dev); fz_var(failed);
    fz_try(ctx) {
        fz_matrix m = fz_scale((float)scale, (float)scale);
        pix = fz_new_pixmap_with_bbox(ctx, fz_device_gray(ctx), fz_round_rect(fz_transform_rect(clip, m)), nullptr, 0);
        fz_clear_pixmap_with_value(ctx, pix, 255);
        dev = fz_new_draw_device(ctx, m, pix);
        fz_matrix identity = { 1, 0, 0, 1, 0, 0 };
        fz_run_page(ctx, page, dev, identity, nullptr);
        fz_close_device(ctx, dev);
    }
    fz_always(ctx) { fz_drop_device(ctx, dev); }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) { fz_drop_pixmap(ctx, pix); throw std::runtime_error(error); }
    return pix;
}

inline bool Unicolor(fz_pixmap* p) {
    unsigned char first = p->samples[0];
    for (int y = 0; y < p->h; ++y)
        for (int x = 0; x < p->w; ++x) if (p->samples[y * p->stride + x] != first) return false;
    return true;
}

inline Lines Recognize(fz_context* ctx, fz_pixmap* gray, double dpi, int margin, const Json& job) {
    fz_pixmap* rgb = nullptr; fz_buffer* buffer = nullptr; fz_output* output = nullptr;
    fz_stream* stream = nullptr; fz_document* doc = nullptr; fz_page* page = nullptr; fz_stext_page* text = nullptr;
    Lines lines; bool failed = false; std::string error;
    fz_var(rgb); fz_var(buffer); fz_var(output); fz_var(stream); fz_var(doc); fz_var(page); fz_var(text); fz_var(failed);
    fz_try(ctx) {
        // The Python reference places the gray tile on a white scratch page and OCRs its RGB render.
        rgb = fz_new_pixmap(ctx, fz_device_rgb(ctx), gray->w + 2 * margin, gray->h + 2 * margin, nullptr, 0);
        fz_clear_pixmap_with_value(ctx, rgb, 255);
        for (int y = 0; y < gray->h; ++y)
            for (int x = 0; x < gray->w; ++x) {
                unsigned char value = gray->samples[y * gray->stride + x];
                auto* pixel = rgb->samples + (y + margin) * rgb->stride + (x + margin) * 3;
                pixel[0] = pixel[1] = pixel[2] = value;
            }
        fz_set_pixmap_resolution(ctx, rgb, (int)std::lround(dpi), (int)std::lround(dpi));
        fz_pdfocr_options opts;
        fz_init_pdfocr_options(ctx, &opts);
        std::string language = job.str("language", "vie"), datadir = job.str("tessdata");
        if (language.size() >= sizeof opts.language || datadir.size() >= sizeof opts.datadir)
            fz_throw(ctx, FZ_ERROR_ARGUMENT, "OCR language or tessdata path too long");
        std::memcpy(opts.language, language.c_str(), language.size() + 1);
        std::memcpy(opts.datadir, datadir.c_str(), datadir.size() + 1);
        buffer = fz_new_buffer(ctx, 4096);
        output = fz_new_output_with_buffer(ctx, buffer);
        fz_write_pixmap_as_pdfocr(ctx, output, rgb, &opts);
        fz_close_output(ctx, output);
        stream = fz_open_buffer(ctx, buffer);
        doc = fz_open_document_with_stream(ctx, ".pdf", stream);
        page = fz_load_page(ctx, doc, 0);
        fz_stext_options stOpts{};
        text = fz_new_stext_page_from_page(ctx, page, &stOpts);
        double k = dpi / 72.0;
        for (auto* b = text->first_block; b; b = b->next) {
            if (b->type != FZ_STEXT_BLOCK_TEXT) continue;
            for (auto* l = b->u.t.first_line; l; l = l->next) {
                std::vector<Word> words; Word word{}; double previous = 0; bool havePrevious = false;
                auto flush = [&]() { if (!word.text.empty()) { words.push_back(word); word = Word{}; } };
                for (auto* c = l->first_char; c; c = c->next) {
                    fz_rect r = fz_rect_from_quad(c->quad);
                    if (c->c == ' ' || (havePrevious && r.x0 - previous > 0.3)) {
                        flush();
                        if (c->c == ' ') { previous = r.x1; havePrevious = true; continue; }
                    }
                    if (word.text.empty()) word = Word{ "", r.x0 * k, r.y0 * k, r.x1 * k, r.y1 * k };
                    else { word.x0 = std::min(word.x0, r.x0 * k); word.y0 = std::min(word.y0, r.y0 * k); word.x1 = std::max(word.x1, r.x1 * k); word.y1 = std::max(word.y1, r.y1 * k); }
                    Utf8(word.text, (unsigned)c->c); previous = r.x1; havePrevious = true;
                }
                flush(); if (!words.empty()) lines.push_back(std::move(words));
            }
        }
    }
    fz_always(ctx) {
        fz_drop_stext_page(ctx, text); fz_drop_page(ctx, page); fz_drop_document(ctx, doc);
        fz_drop_stream(ctx, stream); fz_drop_output(ctx, output); fz_drop_buffer(ctx, buffer); fz_drop_pixmap(ctx, rgb);
    }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) throw std::runtime_error(error);
    return lines;
}

inline bool HasText(fz_context* ctx, fz_page* page, int minimum) {
    fz_stext_page* text = nullptr; int count = 0; bool failed = false; std::string error;
    fz_var(text); fz_var(count); fz_var(failed);
    fz_try(ctx) {
        fz_stext_options opts{}; text = fz_new_stext_page_from_page(ctx, page, &opts);
        for (auto* b = text->first_block; b; b = b->next) if (b->type == FZ_STEXT_BLOCK_TEXT)
            for (auto* l = b->u.t.first_line; l; l = l->next)
                for (auto* c = l->first_char; c; c = c->next) if (!fz_is_unicode_whitespace(c->c)) ++count;
    }
    fz_always(ctx) { fz_drop_stext_page(ctx, text); }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) throw std::runtime_error(error);
    return count >= minimum;
}

inline std::string PageWords(fz_context* ctx, fz_page* page, fz_rect bounds, const Json& job) {
    double scale = job.integer("dpi", 300) / 72.0;
    double width = (bounds.x1 - bounds.x0) * scale, height = (bounds.y1 - bounds.y0) * scale;
    int tile = job.integer("tile", 3200), overlap = job.integer("overlap", 160), step = tile - overlap;
    if (tile < 64 || tile > 8192 || overlap < 0 || step < 32) throw std::runtime_error("invalid OCR tile/overlap");
    std::string words = "["; bool first = true;
    for (int ty = 0; ty < std::max(1.0, std::ceil(height - overlap)); ty += step)
        for (int tx = 0; tx < std::max(1.0, std::ceil(width - overlap)); tx += step) {
            int tw = std::min(tile, (int)std::ceil(width) - tx), th = std::min(tile, (int)std::ceil(height) - ty);
            if (tw < 8 || th < 8) continue;
            fz_rect clip = { bounds.x0 + (float)(tx / scale), bounds.y0 + (float)(ty / scale), bounds.x0 + (float)((tx + tw) / scale), bounds.y0 + (float)((ty + th) / scale) };
            auto* pix = RenderGray(ctx, page, clip, scale);
            Lines lines;
            try { if (!Unicolor(pix)) lines = Recognize(ctx, pix, job.integer("dpi", 300), 0, job); }
            catch (...) { fz_drop_pixmap(ctx, pix); throw; }
            fz_drop_pixmap(ctx, pix);
            double half = overlap / 2.0;
            double left = tx + (tx > 0 ? half : 0), right = tx + tw - (tx + tw < width - 1 ? half : 0);
            double top = ty + (ty > 0 ? half : 0), bottom = ty + th - (ty + th < height - 1 ? half : 0);
            for (auto& line : lines) for (auto& word : line) {
                double cx = tx + (word.x0 + word.x1) / 2, cy = ty + (word.y0 + word.y1) / 2;
                if (cx < left || cx >= right || cy < top || cy >= bottom || word.text.find_first_not_of("|_~`'\".,;:-") == std::string::npos) continue;
                char box[160];
                std::snprintf(box, sizeof box, ",%.8f,%.8f,%.8f,%.8f]", (tx + word.x0) / width, (ty + word.y0) / height, (tx + word.x1) / width, (ty + word.y1) / height);
                if (!first) words += ','; first = false; words += '[' + JsonQuote(word.text) + box;
            }
        }
    char dimensions[128]; std::snprintf(dimensions, sizeof dimensions, ",\"widthPx\":%.5f,\"heightPx\":%.5f", width, height);
    return words + "]" + dimensions;
}

inline std::string Region(fz_context* ctx, fz_page* page, fz_rect bounds, const Json& request, const Json& job) {
    const Json* r = request.find("rect");
    if (!r || r->type != Json::Array || r->items.size() != 4) throw std::runtime_error("invalid OCR region");
    double w = bounds.x1 - bounds.x0, h = bounds.y1 - bounds.y0;
    fz_rect clip = { bounds.x0 + (float)(r->items[0].number * w), bounds.y0 + (float)(r->items[1].number * h),
        bounds.x0 + (float)(r->items[2].number * w), bounds.y0 + (float)(r->items[3].number * h) };
    clip = fz_intersect_rect(clip, bounds);
    if (clip.x1 - clip.x0 < 2 || clip.y1 - clip.y0 < 2) return "";
    double scale = job.integer("dpi", 300) / 72.0;
    if ((clip.y1 - clip.y0) * scale < 70) scale = std::min(6.0, 70.0 / (clip.y1 - clip.y0));
    if ((clip.x1 - clip.x0) * scale > 3400) scale = 3400.0 / (clip.x1 - clip.x0);
    auto* pix = RenderGray(ctx, page, clip, scale);
    Lines lines;
    int height = pix->h;
    try { if (!Unicolor(pix)) lines = Recognize(ctx, pix, scale * 72.0, std::max(24, (int)(height * 0.25)), job); }
    catch (...) { fz_drop_pixmap(ctx, pix); throw; }
    fz_drop_pixmap(ctx, pix);
    std::stable_sort(lines.begin(), lines.end(), [height](const auto& a, const auto& b) {
        auto ay = std::nearbyint(a[0].y0 / std::max(1.0, height * 0.4)), by = std::nearbyint(b[0].y0 / std::max(1.0, height * 0.4));
        return ay == by ? a[0].x0 < b[0].x0 : ay < by;
    });
    std::string result;
    for (auto& line : lines) for (auto& word : line) { if (!result.empty()) result += ' '; result += word.text; }
    auto from = result.find_first_not_of("|_~` "), to = result.find_last_not_of("|_~` ");
    return from == std::string::npos ? "" : result.substr(from, to - from + 1);
}

inline void Run(fz_context* ctx, fz_document* doc, int pageCount, const Json& job) {
    int dpi = job.integer("dpi", 300);
    if (dpi < 72 || dpi > 600) throw std::runtime_error("OCR dpi must be between 72 and 600");
    std::map<int, std::vector<const Json*>> regions;
    bool regionMode = job.str("mode") == "regions";
    std::vector<int> pages;
    if (regionMode) {
        if (auto* requests = job.find("requests")) for (auto& r : requests->items) regions[r.integer("page")].push_back(&r);
        for (auto& r : regions) pages.push_back(r.first);
    } else {
        if (auto* requested = job.find("pages")) for (auto& p : requested->items) pages.push_back((int)p.number);
        if (pages.empty()) for (int p = 0; p < pageCount; ++p) pages.push_back(p);
    }
    Emit("{\"type\":\"start\",\"pageCount\":" + std::to_string(pageCount) + ",\"pages\":" + std::to_string(pages.size()) + "}");
    for (int index : pages) {
        auto start = std::chrono::steady_clock::now(); fz_page* page = nullptr; fz_rect bounds{};
        bool failed = false; std::string error;
        fz_var(page); fz_var(failed);
        fz_try(ctx) { page = fz_load_page(ctx, doc, index); bounds = fz_bound_page(ctx, page); }
        fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
        try {
            if (failed) throw std::runtime_error(error);
            std::string payload;
            if (regionMode) {
                payload = "\"texts\":{"; bool first = true;
                for (auto* r : regions[index]) { if (!first) payload += ','; first = false; payload += JsonQuote(r->str("key")) + ':' + JsonQuote(Region(ctx, page, bounds, *r, job)); }
                payload += '}';
            } else if (job.flag("skipText", true) && HasText(ctx, page, job.integer("minChars", 20))) payload = "\"skipped\":true,\"words\":[]";
            else payload = "\"skipped\":false,\"words\":" + PageWords(ctx, page, bounds, job);
            auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
            Emit("{\"type\":" + JsonQuote(regionMode ? "regions" : "page") + ",\"page\":" + std::to_string(index) + ',' + payload + ",\"ms\":" + std::to_string(ms) + '}');
        } catch (const std::exception& e) { Emit("{\"type\":\"error\",\"page\":" + std::to_string(index) + ",\"message\":" + JsonQuote(e.what()) + '}'); }
        fz_drop_page(ctx, page); fz_shrink_store(ctx, 0);
    }
    Emit("{\"type\":\"done\"}");
}
}
