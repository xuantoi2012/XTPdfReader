// Native text run extraction and removal; replacement glyphs remain the C# writer's responsibility.
#pragma once
namespace nativeedit {
struct Run { std::string text, font; fz_rect box{}; fz_point origin{}; float size = 0; unsigned color = 0; int flags = 0; };
inline std::string RectJson(fz_rect r) {
    char b[128]; std::snprintf(b, sizeof b, "[%.6f,%.6f,%.6f,%.6f]", r.x0, r.y0, r.x1, r.y1); return b;
}
inline fz_matrix Derotation(fz_rect bounds, int rotation) {
    float w = bounds.x1 - bounds.x0, h = bounds.y1 - bounds.y0;
    if (rotation == 90) return { 0, -1, 1, 0, 0, w };
    if (rotation == 180) return { -1, 0, 0, -1, w, h };
    if (rotation == 270) return { 0, 1, -1, 0, h, 0 };
    return { 1, 0, 0, 1, 0, 0 };
}
inline int Rotation(fz_context* ctx, pdf_page* page) {
    int r = pdf_to_int(ctx, pdf_dict_get_inheritable(ctx, page->obj, PDF_NAME(Rotate))) % 360;
    return r < 0 ? r + 360 : r;
}
// Match PyMuPDF's repaired glyph boxes for fonts whose ascent/descent total is less than one em.
inline fz_rect CharBox(fz_context* ctx, fz_stext_line* line, fz_stext_char* ch, fz_matrix derotate) {
    fz_quad quad = fz_transform_quad(ch->quad, derotate);
    float asc = fz_font_ascender(ctx, ch->font), dsc = fz_font_descender(ctx, ch->font);
    if (!line->wmode && asc - dsc + FLT_EPSILON < 1) {
        if (asc < 1e-3f) { asc = .9f; dsc = -.1f; }
        float total = asc - dsc;
        asc *= ch->size / total; dsc *= ch->size / total;
        fz_point origin = fz_transform_point(ch->origin, derotate);
        fz_point dir = fz_transform_vector(line->dir, derotate);
        fz_matrix unrotate = {dir.x, -dir.y, dir.y, dir.x, 0, 0};
        fz_matrix rotate = {dir.x, dir.y, -dir.y, dir.x, 0, 0};
        if (dir.x == -1) { unrotate.d = 1; rotate.d = 1; }
        quad = fz_transform_quad(quad, fz_translate(-origin.x, -origin.y));
        quad = fz_transform_quad(quad, unrotate);
        bool flipped = dir.x == 1 && quad.ul.y > 0;
        quad.ul.y = quad.ur.y = flipped ? asc : -asc;
        quad.ll.y = quad.lr.y = flipped ? dsc : -dsc;
        if (quad.ll.x < 0) quad.ll.x = quad.ul.x = 0;
        if (quad.lr.x - quad.ll.x < FLT_EPSILON) {
            int glyph = fz_encode_character(ctx, ch->font, ch->c);
            if (glyph) quad.lr.x = quad.ur.x = quad.ll.x + fz_advance_glyph(ctx, ch->font, glyph, line->wmode) * ch->size;
        }
        quad = fz_transform_quad(quad, rotate);
        quad = fz_transform_quad(quad, fz_translate(origin.x, origin.y));
    }
    fz_rect box = fz_rect_from_quad(quad);
    if (line->wmode && box.y1 < box.y0 + ch->size) box.y0 = box.y1 - ch->size;
    return box;
}
inline std::vector<Run> Runs(fz_context* ctx, fz_page* page, fz_matrix derotate) {
    fz_stext_page* text = nullptr; bool failed = false; std::string error; std::vector<Run> runs;
    fz_var(text); fz_var(failed);
    fz_try(ctx) {
        fz_stext_options opts{};
        opts.flags = FZ_STEXT_PRESERVE_LIGATURES | FZ_STEXT_PRESERVE_WHITESPACE | FZ_STEXT_CLIP | FZ_STEXT_USE_CID_FOR_UNKNOWN_UNICODE;
        text = fz_new_stext_page_from_page(ctx, page, &opts);
        for (auto* b = text->first_block; b; b = b->next) if (b->type == FZ_STEXT_BLOCK_TEXT)
            for (auto* l = b->u.t.first_line; l; l = l->next) {
                std::vector<Run> spans;
                Run* span = nullptr;
                for (auto* c = l->first_char; c; c = c->next) {
                    fz_rect box = CharBox(ctx, l, c, derotate);
                    std::string font = fz_font_name(ctx, c->font);
                    if (font.size() > 7 && font[6] == '+') font.erase(0, 7);
                    unsigned color = c->argb & 0xFFFFFF;
                    int flags = (fz_font_is_italic(ctx, c->font) ? 2 : 0) | (fz_font_is_serif(ctx, c->font) ? 4 : 0) |
                        (fz_font_is_monospaced(ctx, c->font) ? 8 : 0) | (fz_font_is_bold(ctx, c->font) ? 16 : 0);
                    auto dir = fz_transform_vector(l->dir, derotate);
                    auto origin = fz_transform_point(c->origin, derotate);
                    auto firstOrigin = fz_transform_point(l->first_char->origin, derotate);
                    if (l->wmode == 0 && dir.x == 1 && dir.y == 0 && origin.y < firstOrigin.y - c->size * .1f) flags |= 1;
                    bool same = span && span->font == font && span->size == c->size && span->color == color && span->flags == flags;
                    if (!same) {
                        spans.push_back({ "", font, box, fz_transform_point(c->origin, derotate), c->size, color, flags });
                        span = &spans.back();
                    }
                    nativeocr::Utf8(span->text, c->c); span->box = fz_union_rect(span->box, box);
                }
                Run* current = nullptr;
                for (auto& s : spans) {
                    if (s.text.find_first_not_of(" \t\r\n") == std::string::npos) {
                        if (current) { current->text += s.text; current->box.x1 = std::max(current->box.x1, s.box.x1); }
                        continue;
                    }
                    bool same = current && current->font == s.font && std::abs(current->size - s.size) < .05 &&
                        current->color == s.color && s.box.x0 - current->box.x1 < s.size * .6;
                    if (same) { current->text += s.text; current->box = fz_union_rect(current->box, s.box); }
                    else { runs.push_back(s); current = &runs.back(); }
                }
            }
    }
    fz_always(ctx) { fz_drop_stext_page(ctx, text); }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) throw std::runtime_error(error);
    for (auto& r : runs) while (!r.text.empty() && (r.text.back() == ' ' || r.text.back() == '\t' || r.text.back() == '\n' || r.text.back() == '\r')) r.text.pop_back();
    return runs;
}

inline std::string RunJson(const Run& run, fz_pixmap* picture, double pageWidth) {
    unsigned bg = 0xFFFFFF;
    if (picture) {
        double k = picture->w / std::max(1.0, pageWidth);
        int x = (int)std::clamp((run.box.x0 - 2) * k, 0.0, (double)picture->w - 1);
        int y = (int)std::clamp((run.box.y0 + run.box.y1) / 2 * k, 0.0, (double)picture->h - 1);
        auto* p = picture->samples + y * picture->stride + x * picture->n;
        bg = (p[0] << 16) | (p[1] << 8) | p[2];
    }
    char attrs[200]; std::snprintf(attrs, sizeof attrs, ",\"origin\":[%.6f,%.6f],\"size\":%.6f,\"color\":%u,\"flags\":%d,\"bg\":%u}", run.origin.x, run.origin.y, run.size, run.color, run.flags, bg);
    return "{\"text\":" + JsonQuote(run.text) + ",\"bbox\":" + RectJson(run.box) + ",\"font\":" + JsonQuote(run.font) + attrs;
}

inline void TextPage(fz_context* ctx, fz_document* doc, int number, const Json& job) {
    fz_page* page = nullptr; fz_pixmap* picture = nullptr; bool failed = false; std::string error;
    fz_rect bounds{}; int rotation = 0;
    fz_var(page); fz_var(picture); fz_var(failed); fz_var(rotation);
    fz_try(ctx) {
        page = fz_load_page(ctx, doc, number - 1); bounds = fz_bound_page(ctx, page);
        if (auto* pp = pdf_page_from_fz_page(ctx, page)) rotation = Rotation(ctx, pp);
    }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) { fz_drop_page(ctx, page); throw std::runtime_error(error); }
    try {
        auto runs = Runs(ctx, page, Derotation(bounds, rotation));
        if (!runs.empty()) {
            fz_try(ctx) { picture = fz_new_pixmap_from_page(ctx, page, fz_scale(30.f / 72, 30.f / 72), fz_device_rgb(ctx), 0); }
            fz_catch(ctx) { picture = nullptr; } // white is the same fallback as Python
        }
        std::string out = "["; bool first = true; bool area = job.str("mode") == "area";
        const Json* rect = job.find("rect");
        if (area && (!rect || rect->items.size() != 4)) throw std::runtime_error("invalid text area");
        double w = bounds.x1 - bounds.x0, h = bounds.y1 - bounds.y0;
        for (auto& run : runs) {
            double cx = (run.box.x0 + run.box.x1) / 2, cy = (run.box.y0 + run.box.y1) / 2;
            if (area && (cx < rect->items[0].number * w || cx > rect->items[2].number * w || cy < rect->items[1].number * h || cy > rect->items[3].number * h)) continue;
            if (!first) out += ','; first = false; out += RunJson(run, picture, w);
        }
        char fields[200]; std::snprintf(fields, sizeof fields, ",\"page\":%d,\"width\":%.6f,\"height\":%.6f,\"rotation\":%d,\"hasText\":%s,\"runs\":", number, w, h, rotation, runs.empty() ? "false" : "true");
        nativeocr::Emit("{\"type\":" + JsonQuote(area ? "area" : "runs") + fields + out + "]}");
    } catch (...) { fz_drop_pixmap(ctx, picture); fz_drop_page(ctx, page); throw; }
    fz_drop_pixmap(ctx, picture); fz_drop_page(ctx, page);
}

inline void Save(fz_context* ctx, pdf_document* doc, const Json& job) {
    pdf_write_options opts; pdf_init_write_options(ctx, &opts);
    bool inPlace = job.flag("inPlace", true);
    opts.do_incremental = inPlace;
    opts.do_encrypt = PDF_ENCRYPT_KEEP;
    pdf_save_document(ctx, doc, job.str(inPlace ? "path" : "output").c_str(), &opts);
}

inline void Apply(fz_context* ctx, fz_document* doc, const Json& job) {
    auto* pdf = pdf_specifics(ctx, doc);
    if (!pdf) throw std::runtime_error("text editing needs a PDF");
    if (pdf_dict_get(ctx, pdf_trailer(ctx, pdf), PDF_NAME(Encrypt))) throw std::runtime_error("The file is protected by a password; text editing needs an unprotected file.");
    std::map<int, std::vector<const Json*>> byPage;
    if (auto* edits = job.find("edits")) for (auto& e : edits->items) byPage[e.integer("page")].push_back(&e);
    int applied = 0;
    for (auto& edits : byPage) {
        fz_page* page = nullptr; bool failed = false; std::string error;
        fz_var(page); fz_var(failed);
        fz_try(ctx) {
            page = fz_load_page(ctx, doc, edits.first - 1);
            auto* pp = pdf_page_from_fz_page(ctx, page);
            fz_matrix rotate = fz_invert_matrix(Derotation(fz_bound_page(ctx, page), Rotation(ctx, pp)));
            for (auto* e : edits.second) {
                auto* b = e->find("bbox");
                if (!b || b->items.size() != 4) fz_throw(ctx, FZ_ERROR_ARGUMENT, "invalid edit box");
                float shrink = (float)((b->items[3].number - b->items[1].number) * .12);
                fz_rect r = { (float)b->items[0].number + .2f, (float)b->items[1].number + shrink, (float)b->items[2].number - .2f, (float)b->items[3].number - shrink };
                auto* annot = pdf_create_annot(ctx, pp, PDF_ANNOT_REDACT);
                pdf_set_annot_rect(ctx, annot, fz_transform_rect(r, rotate));
                pdf_drop_annot(ctx, annot);
            }
            pdf_redact_options opts{};
            opts.image_method = PDF_REDACT_IMAGE_NONE; opts.line_art = PDF_REDACT_LINE_ART_NONE; opts.text = PDF_REDACT_TEXT_REMOVE;
            pdf_redact_page(ctx, pdf, pp, &opts);
        }
        fz_always(ctx) { fz_drop_page(ctx, page); }
        fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
        if (failed) throw std::runtime_error(error);
        applied += (int)edits.second.size();
    }
    bool failed = false; std::string error; fz_var(failed);
    fz_try(ctx) { Save(ctx, pdf, job); }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) throw std::runtime_error(error);
    nativeocr::Emit("{\"type\":\"done\",\"applied\":" + std::to_string(applied) + '}');
}

inline void RunJob(fz_context* ctx, fz_document* doc, const Json& job) {
    auto mode = job.str("mode");
    if (mode == "runs") TextPage(ctx, doc, job.integer("page"), job);
    else if (mode == "area") { if (auto* pages = job.find("pages")) for (auto& p : pages->items) TextPage(ctx, doc, (int)p.number, job); }
    else if (mode == "apply") Apply(ctx, doc, job);
    else throw std::runtime_error("unsupported native text operation");
}
}
