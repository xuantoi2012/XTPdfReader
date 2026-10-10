// A small JSON reader for the worker's request lines (objects, arrays, strings, numbers, true/false/null).
// Strings are decoded to UTF-8 (\uXXXX and surrogate pairs included: .NET escapes non-ASCII file names that way).
#pragma once
#include <cstdio>
#include <cstdlib>
#include <string>
#include <utility>
#include <vector>

struct Json {
    enum Type { Null, Bool, Number, String, Array, Object } type = Null;
    bool boolean = false;
    double number = 0;
    std::string text;
    std::vector<Json> items;
    std::vector<std::pair<std::string, Json>> members;

    const Json* find(const char* key) const {
        for (const auto& m : members) if (m.first == key) return &m.second;
        return nullptr;
    }
    bool has(const char* key) const { auto* v = find(key); return v && v->type != Null; }
    double num(const char* key, double fallback = 0) const { auto* v = find(key); return v && v->type == Number ? v->number : fallback; }
    int integer(const char* key, int fallback = 0) const { return (int)num(key, fallback); }
    bool flag(const char* key, bool fallback = false) const { auto* v = find(key); return v && v->type == Bool ? v->boolean : fallback; }
    std::string str(const char* key, const std::string& fallback = "") const { auto* v = find(key); return v && v->type == String ? v->text : fallback; }
};

class JsonReader {
public:
    explicit JsonReader(const std::string& s) : p_(s.c_str()), end_(s.c_str() + s.size()) {}
    bool parse(Json& out) { skip(); if (!value(out)) return false; skip(); return true; }

private:
    const char* p_;
    const char* end_;

    void skip() { while (p_ < end_ && (*p_ == ' ' || *p_ == '\t' || *p_ == '\r' || *p_ == '\n')) ++p_; }

    static void appendUtf8(std::string& s, unsigned cp) {
        if (cp < 0x80) s += (char)cp;
        else if (cp < 0x800) { s += (char)(0xC0 | (cp >> 6)); s += (char)(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) { s += (char)(0xE0 | (cp >> 12)); s += (char)(0x80 | ((cp >> 6) & 0x3F)); s += (char)(0x80 | (cp & 0x3F)); }
        else { s += (char)(0xF0 | (cp >> 18)); s += (char)(0x80 | ((cp >> 12) & 0x3F)); s += (char)(0x80 | ((cp >> 6) & 0x3F)); s += (char)(0x80 | (cp & 0x3F)); }
    }

    bool hex4(unsigned& v) {
        if (end_ - p_ < 4) return false;
        v = 0;
        for (int i = 0; i < 4; ++i) {
            char c = *p_++;
            v <<= 4;
            if (c >= '0' && c <= '9') v |= c - '0';
            else if (c >= 'a' && c <= 'f') v |= c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') v |= c - 'A' + 10;
            else return false;
        }
        return true;
    }

    bool string(std::string& out) {
        if (p_ >= end_ || *p_ != '"') return false;
        ++p_;
        while (p_ < end_ && *p_ != '"') {
            char c = *p_++;
            if (c != '\\') { out += c; continue; }
            if (p_ >= end_) return false;
            char e = *p_++;
            switch (e) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    unsigned cp;
                    if (!hex4(cp)) return false;
                    if (cp >= 0xD800 && cp < 0xDC00 && end_ - p_ >= 6 && p_[0] == '\\' && p_[1] == 'u') {
                        p_ += 2;
                        unsigned low;
                        if (!hex4(low)) return false;
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    }
                    appendUtf8(out, cp);
                    break;
                }
                default: return false;
            }
        }
        if (p_ >= end_) return false;
        ++p_;
        return true;
    }

    bool literal(const char* word) {
        size_t n = std::char_traits<char>::length(word);
        if ((size_t)(end_ - p_) < n || std::string(p_, n) != word) return false;
        p_ += n;
        return true;
    }

    bool value(Json& v) {
        skip();
        if (p_ >= end_) return false;
        char c = *p_;
        if (c == '{') {
            ++p_; v.type = Json::Object; skip();
            if (p_ < end_ && *p_ == '}') { ++p_; return true; }
            for (;;) {
                skip();
                std::string key;
                if (!string(key)) return false;
                skip();
                if (p_ >= end_ || *p_++ != ':') return false;
                Json child;
                if (!value(child)) return false;
                v.members.emplace_back(std::move(key), std::move(child));
                skip();
                if (p_ >= end_) return false;
                if (*p_ == ',') { ++p_; continue; }
                if (*p_ == '}') { ++p_; return true; }
                return false;
            }
        }
        if (c == '[') {
            ++p_; v.type = Json::Array; skip();
            if (p_ < end_ && *p_ == ']') { ++p_; return true; }
            for (;;) {
                Json child;
                if (!value(child)) return false;
                v.items.push_back(std::move(child));
                skip();
                if (p_ >= end_) return false;
                if (*p_ == ',') { ++p_; continue; }
                if (*p_ == ']') { ++p_; return true; }
                return false;
            }
        }
        if (c == '"') { v.type = Json::String; return string(v.text); }
        if (c == 't') { v.type = Json::Bool; v.boolean = true; return literal("true"); }
        if (c == 'f') { v.type = Json::Bool; v.boolean = false; return literal("false"); }
        if (c == 'n') { v.type = Json::Null; return literal("null"); }
        char* stop = nullptr;
        v.number = std::strtod(p_, &stop);
        if (stop == p_) return false;
        v.type = Json::Number;
        p_ = stop;
        return true;
    }
};

// Writes a JSON string literal (UTF-8 passes through, control characters and quotes are escaped).
inline std::string JsonQuote(const std::string& s) {
    std::string out = "\"";
    for (unsigned char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) { char buf[8]; std::snprintf(buf, sizeof buf, "\\u%04x", c); out += buf; }
                else out += (char)c;
        }
    }
    return out + "\"";
}
