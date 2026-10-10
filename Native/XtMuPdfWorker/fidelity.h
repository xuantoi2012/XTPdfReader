// Step 1C: what the Python worker does to a document in memory (never saved) so that the pictures are the ones the owner expects.
//  - layers: the hidden OCGs are switched off through MuPDF's own layer index (found by probing, see LayerIndexByXref in the Python worker)
//  - unembedded TrueType fonts are given the matching font file of Windows (read from the font registry)
//  - unsigned signature widgets and sticky-note icons that have an appearance stream are turned into stamps so that they are drawn where the file says
#pragma once
#define NOMINMAX
#include <mupdf/fitz.h>
#include <mupdf/pdf.h>
#include <windows.h>
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstdint>
#include <fstream>
#include <map>
#include <set>
#include <string>
#include <vector>

struct DocExtras {
    std::map<std::string, int> fontStreams;            // font file -> object number of the stream added to the document
    std::set<int> fontPages;                           // pages whose fonts were looked at
    std::set<int> stampPages;                          // pages whose widgets / notes were turned into stamps
    std::set<int> notePages;                           // pages whose notes were turned into stamps (needs the annotations flag)
    std::map<int, std::set<int64_t>> signatures;       // page -> object numbers of the signature stamps
};

namespace fidelity {

// ── Windows fonts ───────────────────────────────────────────────────────────

inline std::string Narrow(const std::wstring& w) {
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}

inline std::string Lower(std::string s) { for (auto& c : s) c = (char)std::tolower((unsigned char)c); return s; }

inline bool EndsWith(const std::string& s, const std::string& suffix) { return s.size() >= suffix.size() && s.compare(s.size() - suffix.size(), suffix.size(), suffix) == 0; }

// Same normalisation as font_key() of the Python worker.
inline std::string FontKey(std::string name) {
    // "ABCDEF+Name": subset prefix
    if (name.size() > 7 && name[6] == '+') {
        bool upper = true;
        for (int i = 0; i < 6; ++i) if (!(name[i] >= 'A' && name[i] <= 'Z')) upper = false;
        if (upper) name = name.substr(7);
    }
    std::string low = Lower(name);
    const std::string tt = "(truetype)";
    // trailing " (TrueType)" (registry names)
    size_t end = low.find_last_not_of(" \t");
    low = low.substr(0, end == std::string::npos ? 0 : end + 1);
    if (EndsWith(low, tt)) { low.resize(low.size() - tt.size()); }
    auto eraseAll = [](std::string& s, const std::string& what) { for (size_t p; (p = s.find(what)) != std::string::npos;) s.erase(p, what.size()); };
    eraseAll(low, "psmt");
    eraseAll(low, "ps-");
    if (EndsWith(low, "mt")) low.resize(low.size() - 2);
    std::string key;
    for (char c : low) if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) key += c;
    if (EndsWith(key, "regular")) key.resize(key.size() - 7);
    return key;
}

inline const std::map<std::string, std::wstring>& SystemFonts() {
    static std::map<std::string, std::wstring> fonts;
    static bool loaded = false;
    if (loaded) return fonts;
    loaded = true;
    wchar_t windir[MAX_PATH] = L"C:\\Windows";
    GetEnvironmentVariableW(L"WINDIR", windir, MAX_PATH);
    for (HKEY root : { HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER }) {
        HKEY key;
        if (RegOpenKeyExW(root, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Fonts", 0, KEY_READ, &key) != ERROR_SUCCESS) continue;
        for (DWORD i = 0;; ++i) {
            wchar_t name[512], data[1024];
            DWORD nameLen = 512, dataLen = sizeof data, type = 0;
            LONG r = RegEnumValueW(key, i, name, &nameLen, nullptr, &type, (LPBYTE)data, &dataLen);
            if (r == ERROR_NO_MORE_ITEMS) break;
            if (r != ERROR_SUCCESS || type != REG_SZ) continue;
            std::wstring file(data);
            if (file.size() < 4 || (file.compare(file.size() - 4, 4, L".ttf") != 0 && file.compare(file.size() - 4, 4, L".TTF") != 0)) continue;
            bool absolute = file.size() > 2 && (file[1] == L':' || file[0] == L'\\');
            if (!absolute) file = std::wstring(windir) + L"\\Fonts\\" + file;
            if (GetFileAttributesW(file.c_str()) == INVALID_FILE_ATTRIBUTES) continue;
            std::string k = FontKey(Narrow(name));
            if (!k.empty() && !fonts.count(k)) fonts[k] = file;
        }
        RegCloseKey(key);
    }
    return fonts;
}

// ── layers ──────────────────────────────────────────────────────────────────

// MuPDF keeps its own layer list whose order is not the /OCGs array order on real CAD files. Probe: for each bit of the index enable the
// layers that have the bit set, and read the hidden state of every OCG. Returns false when the result cannot be trusted.
inline bool LayerIndexByXref(fz_context* ctx, pdf_document* pdf, const std::vector<int>& xrefs, const std::vector<std::string>& names, std::vector<int>& index) {
    int count = pdf_count_layers(ctx, pdf);
    if (count == 0 || count != (int)xrefs.size()) return false;
    std::vector<pdf_obj*> objects;
    bool ok = true;
    index.assign(xrefs.size(), 0);
    fz_try(ctx) {
        for (int x : xrefs) objects.push_back(pdf_new_indirect(ctx, pdf, x, 0));
        int bits = 1;
        while ((1 << bits) < count) ++bits;
        for (int bit = 0; bit < bits; ++bit) {
            for (int i = 0; i < count; ++i) pdf_enable_layer(ctx, pdf, i, (i >> bit) & 1);
            for (size_t k = 0; k < xrefs.size(); ++k)
                if (!pdf_is_ocg_hidden(ctx, pdf, nullptr, "View", objects[k])) index[k] |= 1 << bit;
        }
    }
    fz_always(ctx) {
        for (int i = 0; i < count; ++i) { fz_try(ctx) { pdf_enable_layer(ctx, pdf, i, 1); } fz_catch(ctx) {} }
        for (auto* o : objects) pdf_drop_obj(ctx, o);
    }
    fz_catch(ctx) { ok = false; }
    if (!ok) return false;
    std::set<int> seen;
    for (size_t k = 0; k < xrefs.size(); ++k) {
        if (index[k] >= count || !seen.insert(index[k]).second) return false;
        const char* name = nullptr;
        fz_try(ctx) { name = pdf_layer_name(ctx, pdf, index[k]); }
        fz_catch(ctx) { return false; }
        if (!name || names[k] != name) return false;
    }
    return true;
}

// off = object numbers of the OCGs that must be hidden.
inline void ApplyHiddenLayers(fz_context* ctx, fz_document* doc, const std::set<int>& off) {
    pdf_document* pdf = pdf_specifics(ctx, doc);
    if (!pdf) return;
    std::vector<int> xrefs;
    std::vector<std::string> names;
    pdf_obj* ocgs = nullptr;
    fz_try(ctx) {
        pdf_obj* root = pdf_dict_get(ctx, pdf_trailer(ctx, pdf), PDF_NAME(Root));
        ocgs = pdf_dict_getp(ctx, root, "OCProperties/OCGs");
        int n = pdf_array_len(ctx, ocgs);
        for (int i = 0; i < n; ++i) {
            pdf_obj* item = pdf_array_get(ctx, ocgs, i);
            xrefs.push_back(pdf_to_num(ctx, item));
            pdf_obj* name = pdf_dict_get(ctx, item, PDF_NAME(Name));
            names.push_back(name ? pdf_to_text_string(ctx, name) : "");
        }
    }
    fz_catch(ctx) { return; }
    if (xrefs.empty()) return;
    std::vector<int> index;
    if (!LayerIndexByXref(ctx, pdf, xrefs, names, index)) {
        // Probe failed: the OCG array order (right for files whose layer list is in array order).
        index.resize(xrefs.size());
        for (size_t k = 0; k < xrefs.size(); ++k) index[k] = (int)k;
    }
    int count = 0;
    fz_try(ctx) { count = pdf_count_layers(ctx, pdf); }
    fz_catch(ctx) { return; }
    for (size_t k = 0; k < xrefs.size(); ++k) {
        if (index[k] >= count) continue;
        fz_try(ctx) { pdf_enable_layer(ctx, pdf, index[k], off.count(xrefs[k]) ? 0 : 1); }
        fz_catch(ctx) {}
    }
}

// ── fonts, stamps ───────────────────────────────────────────────────────────

inline void CollectFonts(fz_context* ctx, pdf_obj* resources, int depth, std::set<int>& visited, std::vector<pdf_obj*>& fonts) {
    if (!resources || depth > 4) return;
    pdf_obj* fontDict = pdf_dict_get(ctx, resources, PDF_NAME(Font));
    int n = pdf_dict_len(ctx, fontDict);
    for (int i = 0; i < n; ++i) {
        pdf_obj* font = pdf_dict_get_val(ctx, fontDict, i);
        int num = pdf_to_num(ctx, font);
        if (num > 0 && !visited.insert(num).second) continue;
        fonts.push_back(font);
    }
    pdf_obj* xobjects = pdf_dict_get(ctx, resources, PDF_NAME(XObject));
    int m = pdf_dict_len(ctx, xobjects);
    for (int i = 0; i < m; ++i) {
        pdf_obj* x = pdf_dict_get_val(ctx, xobjects, i);
        int num = pdf_to_num(ctx, x);
        if (num > 0 && !visited.insert(num).second) continue;
        CollectFonts(ctx, pdf_dict_get(ctx, x, PDF_NAME(Resources)), depth + 1, visited, fonts);
    }
}

// Gives the TrueType fonts that are not embedded the font file of the same name from Windows. Page objects only; the file on disk is untouched.
inline void ResolveUnembeddedFonts(fz_context* ctx, fz_document* doc, int page, DocExtras& extras) {
    if (!extras.fontPages.insert(page).second) return;
    pdf_document* pdf = pdf_specifics(ctx, doc);
    if (!pdf) return;
    const auto& system = SystemFonts();
    if (system.empty()) return;
    fz_try(ctx) {
        pdf_obj* pageObj = pdf_lookup_page_obj(ctx, pdf, page);
        pdf_obj* resources = pdf_dict_get_inheritable(ctx, pageObj, PDF_NAME(Resources));
        std::set<int> visited;
        std::vector<pdf_obj*> fonts;
        CollectFonts(ctx, resources, 0, visited, fonts);
        for (pdf_obj* font : fonts) {
            if (!pdf_is_dict(ctx, font)) continue;
            if (pdf_dict_get(ctx, font, PDF_NAME(Subtype)) != PDF_NAME(TrueType)) continue;
            pdf_obj* descriptor = pdf_dict_get(ctx, font, PDF_NAME(FontDescriptor));
            if (!pdf_is_dict(ctx, descriptor)) continue;
            if (pdf_dict_get(ctx, descriptor, PDF_NAME(FontFile)) || pdf_dict_get(ctx, descriptor, PDF_NAME(FontFile2)) || pdf_dict_get(ctx, descriptor, PDF_NAME(FontFile3))) continue;
            pdf_obj* baseFont = pdf_dict_get(ctx, font, PDF_NAME(BaseFont));
            const char* name = baseFont ? pdf_to_name(ctx, baseFont) : nullptr;
            if (!name || !*name) continue;
            auto found = system.find(FontKey(name));
            if (found == system.end()) continue;
            std::string path = Narrow(found->second);
            int stream = 0;
            auto have = extras.fontStreams.find(path);
            if (have != extras.fontStreams.end()) stream = have->second;
            else {
                std::ifstream file(found->second, std::ios::binary);
                std::vector<char> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
                if (bytes.empty()) continue;
                fz_buffer* buf = fz_new_buffer_from_copied_data(ctx, (const unsigned char*)bytes.data(), bytes.size());
                pdf_obj* obj = nullptr;
                fz_try(ctx) {
                    obj = pdf_add_new_dict(ctx, pdf, 1);
                    pdf_dict_put_int(ctx, obj, PDF_NAME(Length1), (int64_t)bytes.size());
                    pdf_update_stream(ctx, pdf, obj, buf, 0);
                    stream = pdf_to_num(ctx, obj);
                }
                fz_always(ctx) { fz_drop_buffer(ctx, buf); pdf_drop_obj(ctx, obj); }
                fz_catch(ctx) { fz_rethrow(ctx); }
                extras.fontStreams[path] = stream;
            }
            if (stream > 0) {
                pdf_obj* ref = pdf_new_indirect(ctx, pdf, stream, 0);
                fz_try(ctx) { pdf_dict_put(ctx, descriptor, PDF_NAME(FontFile2), ref); }
                fz_always(ctx) { pdf_drop_obj(ctx, ref); }
                fz_catch(ctx) { fz_rethrow(ctx); }
            }
        }
    }
    fz_catch(ctx) { /* a font that cannot be replaced is drawn as MuPDF draws it */ }
}

inline bool HasAppearanceStream(fz_context* ctx, pdf_obj* annot) {
    pdf_obj* ap = pdf_dict_get(ctx, annot, PDF_NAME(AP));
    if (!pdf_is_dict(ctx, ap)) return false;
    pdf_obj* n = pdf_dict_get(ctx, ap, PDF_NAME(N));
    return n && pdf_is_indirect(ctx, n);
}

// MuPDF skips unsigned signature widgets even when they have an appearance, and puts a sticky note by its own rules (wrong on rotated pages).
// Their appearance streams are right, so in this private copy they become stamps. Must run before the page is loaded.
inline void TurnIntoStamps(fz_context* ctx, fz_document* doc, int page, bool notes, DocExtras& extras) {
    pdf_document* pdf = pdf_specifics(ctx, doc);
    if (!pdf) return;
    bool doSignatures = extras.stampPages.insert(page).second;
    bool doNotes = notes && extras.notePages.insert(page).second;
    if (!doSignatures && !doNotes) return;
    fz_try(ctx) {
        pdf_obj* pageObj = pdf_lookup_page_obj(ctx, pdf, page);
        pdf_obj* annots = pdf_dict_get(ctx, pageObj, PDF_NAME(Annots));
        int n = pdf_array_len(ctx, annots);
        for (int i = 0; i < n; ++i) {
            pdf_obj* a = pdf_array_get(ctx, annots, i);
            if (!pdf_is_dict(ctx, a)) continue;
            pdf_obj* subtype = pdf_dict_get(ctx, a, PDF_NAME(Subtype));
            if (doSignatures && subtype == PDF_NAME(Widget)) {
                pdf_obj* ft = pdf_dict_get_inheritable(ctx, a, PDF_NAME(FT));
                pdf_obj* v = pdf_dict_get_inheritable(ctx, a, PDF_NAME(V));
                bool unsignedSignature = ft == PDF_NAME(Sig) && (!v || pdf_is_null(ctx, v));
                if (unsignedSignature && HasAppearanceStream(ctx, a)) {
                    extras.signatures[page].insert(pdf_to_num(ctx, a));
                    pdf_dict_put(ctx, a, PDF_NAME(Subtype), PDF_NAME(Stamp));
                }
            } else if (doNotes && subtype == PDF_NAME(Text) && HasAppearanceStream(ctx, a)) {
                pdf_dict_put(ctx, a, PDF_NAME(Subtype), PDF_NAME(Stamp));
            }
        }
    }
    fz_catch(ctx) { /* the page is then drawn as MuPDF draws it */ }
}

}  // namespace fidelity
