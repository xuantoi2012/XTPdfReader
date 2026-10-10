// Drawn object discovery and deferred removal. Coordinates are displayed page points.
#pragma once
namespace nativeobjects {
struct Segment { bool curve; fz_point a, b, c, d; };
struct Path {
    std::vector<Segment> segments;
    fz_rect box{}; bool haveBox = false, closed = false, filled = false, shapeItem = false;
    float width = 0;
    fz_path* raw = nullptr; fz_stroke_state* stroke = nullptr; fz_matrix ctm{};
    fz_colorspace* fillCs = nullptr; fz_colorspace* strokeCs = nullptr;
    float fillColor[FZ_MAX_COLORS]{}, strokeColor[FZ_MAX_COLORS]{};
    float fillAlpha = 1, strokeAlpha = 1; int evenOdd = 0;
};
struct Capture {
    fz_context* ctx; std::vector<Path> paths; std::vector<fz_rect> images; fz_matrix derotate{1,0,0,1,0,0};
    explicit Capture(fz_context* c) : ctx(c) {}
    ~Capture() { for (auto& p : paths) { fz_drop_path(ctx, p.raw); fz_drop_stroke_state(ctx, p.stroke); fz_drop_colorspace(ctx, p.fillCs); fz_drop_colorspace(ctx, p.strokeCs); } }
};
struct Device { fz_device super; Capture* capture; };
struct Walker { Path* path; fz_matrix ctm; fz_matrix derotate; fz_point first{}, last{}; int lines = 0; };
inline void Include(Path& path, fz_point p) {
    if (!path.haveBox) { path.box = { p.x, p.y, p.x, p.y }; path.haveBox = true; }
    else { path.box.x0 = std::min(path.box.x0, p.x); path.box.y0 = std::min(path.box.y0, p.y); path.box.x1 = std::max(path.box.x1, p.x); path.box.y1 = std::max(path.box.y1, p.y); }
}
inline void Move(fz_context*, void* arg, float x, float y) {
    auto& w = *(Walker*)arg; w.lines = 0; w.first = w.last = fz_transform_point_xy(x, y, w.ctm);
    if (!w.path->haveBox) Include(*w.path, w.last);
}
inline void Line(fz_context*, void* arg, float x, float y) {
    auto& w = *(Walker*)arg; fz_point p = fz_transform_point_xy(x, y, w.ctm);
    w.path->segments.push_back({ false, w.last, p, {}, {} }); Include(*w.path, p); w.last = p;
    if (++w.lines == 4) {
        auto& start = w.path->segments[w.path->segments.size()-4].a;
        if (start.x == p.x && start.y == p.y) { w.path->shapeItem = true; w.lines = 0; }
    }
}
inline void Curve(fz_context*, void* arg, float x1, float y1, float x2, float y2, float x3, float y3) {
    auto& w = *(Walker*)arg; w.lines = 0; w.path->shapeItem = true;
    auto b = fz_transform_point_xy(x1, y1, w.ctm), c = fz_transform_point_xy(x2, y2, w.ctm), d = fz_transform_point_xy(x3, y3, w.ctm);
    w.path->segments.push_back({ true, w.last, b, c, d }); Include(*w.path, b); Include(*w.path, c); Include(*w.path, d); w.last = d;
}
inline void Close(fz_context*, void* arg) {
    auto& w = *(Walker*)arg; w.path->closed = true;
    if (w.lines == 3) {
        auto& a = w.path->segments[w.path->segments.size()-3]; auto& b = w.path->segments.back();
        auto ll = fz_transform_point(a.a, w.derotate), lr = fz_transform_point(a.b, w.derotate);
        auto ur = fz_transform_point(b.a, w.derotate), ul = fz_transform_point(b.b, w.derotate);
        if (ll.y == lr.y && ll.x == ul.x && ur.y == ul.y && ur.x == lr.x) w.path->shapeItem = true;
    }
    if (w.last.x != w.first.x || w.last.y != w.first.y) w.path->segments.push_back({ false, w.last, w.first, {}, {} });
    w.last = w.first; w.lines = 0;
}
inline Path Geometry(fz_context* ctx, const fz_path* path, fz_matrix m, fz_matrix derotate) {
    Path result; Walker state{ &result, m, derotate }; fz_path_walker walker{};
    walker.moveto = Move; walker.lineto = Line; walker.curveto = Curve; walker.closepath = Close;
    fz_walk_path(ctx, path, &walker, &state); return result;
}
inline bool Same(const Path& a, const Path& b) {
    if (a.segments.size() != b.segments.size()) return false;
    for (size_t i = 0; i < a.segments.size(); ++i) {
        auto& x = a.segments[i]; auto& y = b.segments[i];
        if (x.curve != y.curve || x.a.x != y.a.x || x.a.y != y.a.y || x.b.x != y.b.x || x.b.y != y.b.y ||
            x.c.x != y.c.x || x.c.y != y.c.y || x.d.x != y.d.x || x.d.y != y.d.y) return false;
    }
    return true;
}
inline void Fill(fz_context* ctx, fz_device* dev, const fz_path* raw, int evenOdd, fz_matrix m, fz_colorspace* cs, const float* color, float alpha, fz_color_params) {
    auto& capture = *(((Device*)dev)->capture); Path p = Geometry(ctx, raw, m, capture.derotate);
    if (p.segments.empty()) return;
    p.raw = fz_keep_path(ctx, raw); p.ctm = m; p.filled = true; p.fillAlpha = alpha; p.evenOdd = evenOdd;
    p.fillCs = fz_keep_colorspace(ctx, cs); if (cs) std::copy(color, color + fz_colorspace_n(ctx, cs), p.fillColor);
    capture.paths.push_back(std::move(p));
}
inline void Stroke(fz_context* ctx, fz_device* dev, const fz_path* raw, const fz_stroke_state* stroke, fz_matrix m, fz_colorspace* cs, const float* color, float alpha, fz_color_params) {
    auto& capture = *(((Device*)dev)->capture); Path p = Geometry(ctx, raw, m, capture.derotate);
    if (p.segments.empty()) return;
    if (!capture.paths.empty() && capture.paths.back().filled && !capture.paths.back().stroke && Same(capture.paths.back(), p)) {
        auto& last = capture.paths.back();
        last.stroke = fz_keep_stroke_state(ctx, stroke); last.strokeCs = fz_keep_colorspace(ctx, cs); last.strokeAlpha = alpha;
        last.width = stroke->linewidth * std::sqrt(std::abs(m.a * m.d - m.b * m.c));
        if (cs) std::copy(color, color + fz_colorspace_n(ctx, cs), last.strokeColor);
    } else {
        p.raw = fz_keep_path(ctx, raw); p.ctm = m; p.stroke = fz_keep_stroke_state(ctx, stroke);
        p.strokeCs = fz_keep_colorspace(ctx, cs); p.strokeAlpha = alpha;
        p.width = stroke->linewidth * std::sqrt(std::abs(m.a * m.d - m.b * m.c));
        if (cs) std::copy(color, color + fz_colorspace_n(ctx, cs), p.strokeColor);
        capture.paths.push_back(std::move(p));
    }
}
inline void Image(fz_context*, fz_device* dev, fz_image*, fz_matrix m, float, fz_color_params) {
    ((Device*)dev)->capture->images.push_back(fz_transform_rect({ 0, 0, 1, 1 }, m));
}
inline void ImageMask(fz_context* ctx, fz_device* dev, fz_image* image, fz_matrix m, fz_colorspace*, const float*, float alpha, fz_color_params params) { Image(ctx, dev, image, m, alpha, params); }
inline void Read(fz_context* ctx, fz_page* page, Capture& capture) {
    Device* dev = nullptr; bool failed = false; std::string error; fz_var(dev); fz_var(failed);
    fz_try(ctx) {
        capture.derotate = nativeedit::Derotation(fz_bound_page(ctx, page), nativeedit::Rotation(ctx, pdf_page_from_fz_page(ctx, page)));
        dev = fz_new_derived_device(ctx, Device); dev->capture = &capture;
        dev->super.fill_path = Fill; dev->super.stroke_path = Stroke; dev->super.fill_image = Image; dev->super.fill_image_mask = ImageMask;
        fz_matrix identity = { 1, 0, 0, 1, 0, 0 }; fz_run_page(ctx, page, &dev->super, identity, nullptr); fz_close_device(ctx, &dev->super);
    }
    fz_always(ctx) { fz_drop_device(ctx, (fz_device*)dev); }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) throw std::runtime_error(error);
}
inline bool IsLine(const Path& p) {
    return !p.filled && !p.shapeItem;
}
inline fz_point Bezier(const Segment& s, double t) {
    double u = 1 - t; return { (float)(u*u*u*s.a.x + 3*u*u*t*s.b.x + 3*u*t*t*s.c.x + t*t*t*s.d.x), (float)(u*u*u*s.a.y + 3*u*u*t*s.b.y + 3*u*t*t*s.c.y + t*t*t*s.d.y) };
}
inline double Distance(fz_point p, fz_point a, fz_point b) {
    double dx = b.x - a.x, dy = b.y - a.y, d = dx*dx + dy*dy;
    double t = d == 0 ? 0 : std::clamp(((p.x-a.x)*dx + (p.y-a.y)*dy)/d, 0.0, 1.0);
    return std::hypot(p.x - a.x - t*dx, p.y - a.y - t*dy);
}
inline bool Near(const Path& path, fz_point point, double reach) {
    for (auto& s : path.segments) {
        if (!s.curve) { if (Distance(point, s.a, s.b) <= reach) return true; }
        else { auto a = s.a; for (int i = 1; i <= 16; ++i) { auto b = Bezier(s, i/16.0); if (Distance(point, a, b) <= reach) return true; a = b; } }
    }
    return false;
}
inline bool Contains(fz_rect outer, fz_rect inner) { return outer.x0 <= inner.x0 && outer.y0 <= inner.y0 && outer.x1 >= inner.x1 && outer.y1 >= inner.y1; }
inline bool Contains(fz_rect outer, fz_point p) { return outer.x0 <= p.x && outer.y0 <= p.y && outer.x1 >= p.x && outer.y1 >= p.y; }
inline double Area(fz_rect r) { return (r.x1 - r.x0) * (r.y1 - r.y0); }
inline std::string ObjectJson(const Capture& capture, int index, bool image) {
    fz_rect r = image ? capture.images[index] : capture.paths[index].box;
    std::string kind = image ? "image" : IsLine(capture.paths[index]) ? "line" : "shape";
    std::string out = "{\"kind\":" + JsonQuote(kind) + ",\"index\":" + std::to_string(index) + ",\"bbox\":" + nativeedit::RectJson(r) + ",\"width\":" + std::to_string(image ? 0.f : capture.paths[index].width) + ",\"paths\":[";
    if (!image) {
        int points = 0; bool first = true;
        for (auto& s : capture.paths[index].segments) {
            if (!first) out += ','; first = false; out += '[';
            int steps = s.curve ? 8 : 1;
            for (int i = 0; i <= steps; ++i) {
                auto p = s.curve ? Bezier(s, i/(double)steps) : i ? s.b : s.a;
                char b[80]; std::snprintf(b, sizeof b, "%s[%.2f,%.2f]", i ? "," : "", p.x, p.y); out += b;
            }
            out += ']'; points += steps + 1; if (points >= 600) break;
        }
    }
    return out + "]}";
}

inline void Query(fz_context* ctx, fz_document* doc, int number, const Json& job) {
    fz_page* page = nullptr; fz_rect bounds{}; int rotation = 0; bool failed = false; std::string error;
    fz_var(page); fz_var(failed); fz_var(rotation);
    fz_try(ctx) { page = fz_load_page(ctx, doc, number-1); bounds = fz_bound_page(ctx, page); rotation = nativeedit::Rotation(ctx, pdf_page_from_fz_page(ctx, page)); }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) { fz_drop_page(ctx, page); throw std::runtime_error(error); }
    try {
        Capture capture(ctx); Read(ctx, page, capture);
        std::string mode = job.str("mode"); bool pick = mode == "pick", area = mode == "areaObjects";
        fz_rect box{}; fz_point point{ (float)job.num("x"), (float)job.num("y") };
        if (!pick) {
            auto* r = job.find("rect"); if (!r || r->items.size() != 4) throw std::runtime_error("invalid object area");
            double w = area ? bounds.x1-bounds.x0 : 1, h = area ? bounds.y1-bounds.y0 : 1;
            box = { (float)(r->items[0].number*w), (float)(r->items[1].number*h), (float)(r->items[2].number*w), (float)(r->items[3].number*h) };
        }
        struct Hit { int index; bool image; double area; }; std::vector<Hit> hits;
        for (int i = 0; i < (int)capture.paths.size(); ++i) {
            auto& p = capture.paths[i]; auto r = p.box;
            bool include = false;
            if (pick) {
                double reach = job.num("tol", 2.5) + p.width / 2;
                include = point.x >= r.x0-reach && point.x <= r.x1+reach && point.y >= r.y0-reach && point.y <= r.y1+reach && (p.filled || Near(p, point, reach));
            } else if (!area) include = Contains(box, r);
            else {
                bool inside = Contains(box, r), centre = Contains(box, fz_point{ (r.x0+r.x1)/2, (r.y0+r.y1)/2 });
                bool touch = r.x1 >= box.x0-.01 && r.x0 <= box.x1+.01 && r.y1 >= box.y0-.01 && r.y0 <= box.y1+.01;
                include = touch && (inside || centre || job.flag("touch")) && (inside || Area(r) <= 3*Area(box));
            }
            if (include) hits.push_back({ i, false, Area(r) });
            if (hits.size() >= 5000) break;
        }
        for (int i = 0; i < (int)capture.images.size(); ++i) {
            auto r = capture.images[i]; bool include = false;
            if (pick) include = Contains(r, point);
            else if (!area) include = Contains(box, r);
            else {
                bool inside = Contains(box, r), centre = Contains(box, fz_point{ (r.x0+r.x1)/2, (r.y0+r.y1)/2 });
                bool touch = r.x1 >= box.x0-.01 && r.x0 <= box.x1+.01 && r.y1 >= box.y0-.01 && r.y0 <= box.y1+.01;
                include = (inside || centre || (job.flag("touch") && touch)) && (inside || Area(r) <= .5*Area(bounds));
            }
            if (include) hits.push_back({ i, true, Area(r) });
        }
        if (pick) std::stable_sort(hits.begin(), hits.end(), [](const Hit& a, const Hit& b) { return a.image == b.image ? a.area < b.area : a.image < b.image; });
        std::string objects = "["; size_t limit = std::min(hits.size(), (size_t)(pick ? 8 : 5000));
        for (size_t i = 0; i < limit; ++i) { if (i) objects += ','; objects += ObjectJson(capture, hits[i].index, hits[i].image); }
        char fields[160]; std::snprintf(fields, sizeof fields, ",\"page\":%d,\"width\":%.6f,\"height\":%.6f,\"rotation\":%d,\"objects\":", number, bounds.x1-bounds.x0, bounds.y1-bounds.y0, rotation);
        nativeocr::Emit("{\"type\":" + JsonQuote(area ? "areaObjects" : "pick") + fields + objects + "]}");
    } catch (...) { fz_drop_page(ctx, page); throw; }
    fz_drop_page(ctx, page);
}

inline void Redraw(fz_context* ctx, pdf_document* doc, pdf_page* page, const Capture& capture, const std::vector<int>& spared) {
    if (spared.empty()) return;
    fz_buffer* content = nullptr; fz_device* dev = nullptr; pdf_obj* resources = nullptr; pdf_obj* stream = nullptr; pdf_obj* contents = nullptr;
    fz_var(content); fz_var(dev); fz_var(resources); fz_var(stream); fz_var(contents);
    fz_try(ctx) {
        fz_rect box; fz_matrix ctm; pdf_page_transform(ctx, page, &box, &ctm);
        resources = pdf_deep_copy_obj(ctx, pdf_page_resources(ctx, page));
        if (!resources) resources = pdf_new_dict(ctx, doc, 4);
        content = fz_new_buffer(ctx, 1024);
        dev = pdf_new_pdf_device(ctx, doc, fz_invert_matrix(ctm), resources, content);
        for (int index : spared) {
            auto& p = capture.paths[index]; fz_color_params params{};
            if (p.filled) fz_fill_path(ctx, dev, p.raw, p.evenOdd, p.ctm, p.fillCs, p.fillColor, p.fillAlpha, params);
            if (p.stroke) fz_stroke_path(ctx, dev, p.raw, p.stroke, p.ctm, p.strokeCs, p.strokeColor, p.strokeAlpha, params);
        }
        fz_close_device(ctx, dev);
        stream = pdf_add_stream(ctx, doc, content, nullptr, 0);
        auto* old = pdf_dict_get(ctx, page->obj, PDF_NAME(Contents));
        contents = pdf_new_array(ctx, doc, 2);
        if (pdf_is_array(ctx, old)) for (int i = 0; i < pdf_array_len(ctx, old); ++i) pdf_array_push(ctx, contents, pdf_array_get(ctx, old, i));
        else if (old) pdf_array_push(ctx, contents, old);
        pdf_array_push(ctx, contents, stream);
        pdf_dict_put(ctx, page->obj, PDF_NAME(Resources), resources);
        pdf_dict_put(ctx, page->obj, PDF_NAME(Contents), contents);
    }
    fz_always(ctx) { fz_drop_device(ctx, dev); fz_drop_buffer(ctx, content); pdf_drop_obj(ctx, resources); pdf_drop_obj(ctx, stream); pdf_drop_obj(ctx, contents); }
    fz_catch(ctx) { fz_rethrow(ctx); }
}

inline void Delete(fz_context* ctx, fz_document* doc, const Json& job) {
    auto* pdf = pdf_specifics(ctx, doc);
    if (!pdf || pdf_dict_get(ctx, pdf_trailer(ctx, pdf), PDF_NAME(Encrypt))) throw std::runtime_error("Object removal needs an unprotected PDF.");
    std::map<int, std::vector<const Json*>> byPage;
    if (auto* objects = job.find("objects")) for (auto& o : objects->items) byPage[o.integer("page")].push_back(&o);
    int removed = 0, done = 0;
    for (auto& group : byPage) {
        fz_page* page = nullptr; bool failed = false; std::string error; fz_var(page); fz_var(failed);
        fz_try(ctx) { page = fz_load_page(ctx, doc, group.first-1); }
        fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
        if (failed) throw std::runtime_error(error);
        try {
            Capture capture(ctx); Read(ctx, page, capture);
            std::set<int> wanted; std::vector<fz_rect> boxes, images; std::vector<int> spared;
            for (auto* o : group.second) {
                int index = o->integer("index"); bool image = o->str("kind") == "image";
                if (index < 0 || index >= (int)(image ? capture.images.size() : capture.paths.size())) throw std::runtime_error("Object changed; reload the page before removing it.");
                if (image) images.push_back(capture.images[index]);
                else if (wanted.insert(index).second) {
                    auto r = capture.paths[index].box; float reach = std::max(.8f, capture.paths[index].width*10+1);
                    boxes.push_back({ r.x0-reach, r.y0-reach, r.x1+reach, r.y1+reach });
                }
            }
            for (int i = 0; i < (int)capture.paths.size(); ++i) if (!wanted.count(i))
                for (auto b : boxes) if (Contains(b, capture.paths[i].box)) { spared.push_back(i); break; }
            fz_try(ctx) {
                auto* pp = pdf_page_from_fz_page(ctx, page);
                for (auto r : boxes) { auto* a = pdf_create_annot(ctx, pp, PDF_ANNOT_REDACT); pdf_set_annot_rect(ctx, a, r); pdf_drop_annot(ctx, a); }
                for (auto r : images) { auto* a = pdf_create_annot(ctx, pp, PDF_ANNOT_REDACT); pdf_set_annot_rect(ctx, a, r); pdf_drop_annot(ctx, a); }
                pdf_redact_options opts{}; opts.image_method = images.empty() ? PDF_REDACT_IMAGE_NONE : PDF_REDACT_IMAGE_REMOVE;
                opts.line_art = wanted.empty() ? PDF_REDACT_LINE_ART_NONE : PDF_REDACT_LINE_ART_REMOVE_IF_COVERED; opts.text = PDF_REDACT_TEXT_NONE;
                pdf_redact_page(ctx, pdf, pp, &opts);
                Redraw(ctx, pdf, pp, capture, spared);
            }
            fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
            if (failed) throw std::runtime_error(error);
            removed += (int)(wanted.size() + images.size());
        } catch (...) { fz_drop_page(ctx, page); throw; }
        fz_drop_page(ctx, page);
        nativeocr::Emit("{\"type\":\"progress\",\"done\":" + std::to_string(++done) + ",\"total\":" + std::to_string(byPage.size()) + ",\"page\":" + std::to_string(group.first) + '}');
    }
    bool failed = false; std::string error; fz_var(failed);
    fz_try(ctx) { nativeedit::Save(ctx, pdf, job); }
    fz_catch(ctx) { failed = true; error = fz_caught_message(ctx); }
    if (failed) throw std::runtime_error(error);
    nativeocr::Emit("{\"type\":\"done\",\"removed\":" + std::to_string(removed) + '}');
}
inline void RunJob(fz_context* ctx, fz_document* doc, const Json& job) {
    auto mode = job.str("mode");
    if (mode == "delete") Delete(ctx, doc, job);
    else if (mode == "areaObjects") { if (auto* pages = job.find("pages")) for (auto& p : pages->items) Query(ctx, doc, (int)p.number, job); }
    else Query(ctx, doc, job.integer("page"), job);
}
}
