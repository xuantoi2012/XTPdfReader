# -*- coding: utf-8 -*-
"""Bộ UI v2: tiếng Anh mặc định (+ bản tiếng Việt của cửa sổ chính), cửa sổ Ghép dạng cửa sổ con."""
import os, json, re
HERE = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(HERE, "gen_ui.py"), encoding="utf-8").read()
head = src[:src.index("# ── khung chung")]
ns = {"__file__": os.path.join(HERE, "gen_ui.py")}
exec(compile(head, "gen_ui_head", "exec"), ns)
globals().update(ns)
for old in os.listdir(PROJ):
    if old.endswith(".dc.html"):
        os.remove(os.path.join(PROJ, old))

def viewer(sheets=(0, 1)):
    pages = ""
    for v in sheets:
        pages += (f'<div style="width:560px;height:396px;background:#ffffff;box-shadow:0 2px 10px rgba(0,0,0,0.25);flex-shrink:0">'
                  f'{cad(v, 560, 396, 0.5)}</div>')
    return (f'<div style="flex:1 1 auto;min-width:0;background:var(--view);overflow:hidden;display:flex;flex-direction:column;align-items:center;gap:16px;padding:22px 0;position:relative">{pages}'
            f'<div style="position:absolute;right:0;top:0;bottom:0;width:12px;background:var(--panel);border-left:1px solid var(--border)"><div style="margin:8px 2px;height:120px;border-radius:4px;background:var(--border)"></div></div></div>')

def doc(title, inner, w, h, accent, lang="vi"):
    props = json.dumps({"accent": {"editor": "color", "default": accent, "options": ["#2563eb", "#0f8b6d", "#7c4dff", "#d9463b"]},
                        "$preview": {"width": w, "height": h}}, ensure_ascii=False)
    props = props.replace("&", "&amp;").replace("'", "&#39;")
    return f'''<!doctype html>
<html lang="{lang}">
<head>
<meta charset="utf-8">
<title>{title}</title>
<script src="./support.js"></script>
</head>
<body>
<x-dc>
<helmet>
<style>{CSS}</style>
</helmet>
{inner}
</x-dc>
<script type="text/x-dc" data-dc-script data-props='{props}'>
class Component extends DCLogic {{
  renderVals() {{
    return {{ accent: this.props.accent ?? '{accent}' }};
  }}
}}
</script>
</body>
</html>
'''

def write(name, text):
    with open(os.path.join(PROJ, name), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


ICONS.update({
 "stamp": '<path d="M6 21h12M5 17h14v-2.5A2.5 2.5 0 0 0 16.5 12H15V8a3 3 0 1 0-6 0v4H7.5A2.5 2.5 0 0 0 5 14.5Z"/>',
 "ruler": '<path d="M3 17 17 3l4 4L7 21Z"/><path d="M7 13l2 2M10 10l2 2M13 7l2 2"/>',
 "compare": '<rect x="3" y="5" width="8" height="14" rx="1.5"/><rect x="13" y="5" width="8" height="14" rx="1.5"/><path d="M11 9h2M11 15h2"/>',
 "print": '<path d="M7 9V3h10v6M7 17H5a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2h-2"/><rect x="7" y="14" width="10" height="7"/>',
 "export": '<path d="M12 3v12M7 8l5-5 5 5M5 21h14"/>',
 "info": '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.5"/>',
 "clock": '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
 "alert": '<path d="M12 3 2 20h20Z"/><path d="M12 10v5M12 17.5v.5"/>',
 "filter": '<path d="M4 5h16l-6 8v6l-4-2v-4Z"/>',
 "star": '<path d="m12 3 2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9Z"/>',
 "checkc": '<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>',
 "split": '<path d="M12 3v18M8 7 4 12l4 5M16 7l4 5-4 5"/>',
 "cmd": '<path d="M9 6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3Z"/>',
 "up2": '<path d="m6 15 6-6 6 6"/>',
 "down2": '<path d="m6 9 6 6 6-6"/>',
 "workspace": '<rect x="3" y="4" width="8" height="7" rx="1.2"/><rect x="13" y="4" width="8" height="7" rx="1.2"/><rect x="3" y="13" width="8" height="7" rx="1.2"/><rect x="13" y="13" width="8" height="7" rx="1.2"/>',
 "comment": '<path d="M4 5h16v11H10l-5 4v-4H4Z"/><path d="M8 9h8M8 12h5"/>',
})

LANG = "en"
VI = {
 "Stamp": "Đóng dấu", "Measure": "Đo", "Find": "Tìm", "Compare": "So sánh", "Print": "In", "Comments": "Chú thích",
 "Hand": "Tay", "Undo": "Hoàn tác", "Redo": "Làm lại", "Continuous": "Cuộn liên tục", "Fit page": "Vừa trang", "Fit width": "Vừa rộng",
 "Rotate left": "Xoay trái", "Rotate right": "Xoay phải", "Typewriter": "Typewriter", "Note": "Ghi chú", "Highlight": "Highlight",
 "Merge files": "Ghép file", "Pages": "Trang", "Bookmarks": "Dấu trang", "Layers": "Layer", "Settings": "Cài đặt",
 "Insert ▾": "Chèn ▾", "Delete": "Xóa", "Extract": "Trích xuất", "3 pages selected": "Đã chọn 3 trang",
 "Ctrl+C copy · Ctrl+V paste": "Ctrl+C sao chép · Ctrl+V dán", "Unsaved changes": "Có thay đổi chưa lưu",
 "Close file": "Đóng file", "Open another file": "Mở thêm file", "Previous page": "Trang trước", "Next page": "Trang sau",
 "Zoom out": "Thu nhỏ", "Zoom in": "Phóng to", "Page number": "Số trang", "Minimize": "Thu nhỏ", "Maximize": "Phóng to", "Close": "Đóng",
}
def t(s):
    return VI.get(s, s) if LANG == "vi" else s

def titlebar(title="XT PDF Reader", sub=""):
    ctl = ''.join(f'<button aria-label="{t(a)}" style="width:40px;height:30px;display:flex;align-items:center;justify-content:center;color:var(--muted)">{ic(i, 13, 1.6)}</button>'
                  for a, i in (("Minimize", "min"), ("Maximize", "max"), ("Close", "close")))
    s = f'<span style="color:var(--muted);font-weight:400">— {sub}</span>' if sub else ""
    return (f'<div style="height:34px;flex-shrink:0;display:flex;align-items:center;gap:10px;padding:0 0 0 12px;background:var(--surface);border-bottom:1px solid var(--border)">'
            f'<div style="width:14px;height:14px;border-radius:3px;background:#d9463b"></div>'
            f'<span style="font-weight:600;font-size:12.5px">{title}</span>{s}<div style="flex:1 1 auto"></div>{ctl}</div>')

def tb(icon, label, on=False):
    return f'<button class="tbtn{" on" if on else ""}" aria-label="{t(label)}" title="{t(label)}">{ic(icon, 20)}<span>{t(label)}</span></button>'

def toolbar(merge=True, active=None):
    g1 = tb("hand", "Hand", active == "hand") + tb("undo", "Undo") + tb("redo", "Redo")
    g2 = tb("scroll", "Continuous", True) + tb("fitp", "Fit page") + tb("fitw", "Fit width") + tb("rotl", "Rotate left") + tb("rotr", "Rotate right")
    g3 = (tb("type", "Typewriter") + tb("note", "Note") + tb("hl", "Highlight") + tb("stamp", "Stamp", active == "stamp"))
    g4 = tb("search", "Find", active == "find") + tb("print", "Print", active == "print")
    sep = '<div class="sep"></div>'
    m = f'<button class="primary" style="margin-left:6px" aria-label="{t("Merge files")}">{ic("merge", 17, 1.9)}{t("Merge files")}</button>' if merge else ""
    return (f'<div style="height:60px;flex-shrink:0;display:flex;align-items:center;gap:2px;padding:0 12px;background:var(--surface);border-bottom:1px solid var(--border)">'
            f'{g1}{sep}{g2}{sep}{g3}<div style="flex:1 1 auto"></div>{g4}{sep}{m}</div>')

def tabs(active=0, names=("03. QUYEN 2.2 - TCTC.pdf", "02. QUYEN 2.1 - GT.pdf", "Volume 3 - Concept drawings.pdf"), dirty=(0,), hover=None):
    out = ""
    for i, n in enumerate(names):
        d = f'<span class="dot" title="{t("Unsaved changes")}"></span>' if i in dirty else ""
        extra = ""
        tip = ""
        if hover == i:
            extra = "outline:2px dashed var(--accent);outline-offset:-3px;background:var(--sel);color:var(--text);position:relative;"
            tip = ('<div style="position:absolute;left:8px;top:40px;z-index:20;background:var(--text);color:var(--surface);border-radius:6px;padding:6px 10px;font-size:11.5px;white-space:nowrap">'
                   'Hold here to switch to this file</div>')
        out += (f'<div class="tab{" active" if i == active else ""}" style="{extra}">{ic("file", 15)}<span>{n}</span>{d}'
                f'<button class="x" aria-label="{t("Close file")}">{ic("close", 11, 2)}</button>{tip}</div>')
    return (f'<div style="height:36px;flex-shrink:0;display:flex;align-items:flex-end;gap:1px;padding:0 8px;background:var(--panel);border-bottom:1px solid var(--border);position:relative">'
            f'{out}<button class="sbtn" style="margin:0 0 4px 4px" aria-label="{t("Open another file")}">{ic("plus", 16)}</button></div>')

def rail(active="pages"):
    items = (("pages", "Pages"), ("bookmark", "Bookmarks"), ("layers", "Layers"), ("comment", "Comments"), ("search", "Find"))
    out = ""
    for k, lab in items:
        out += f'<button class="ritem{" on" if k == active else ""}" aria-label="{t(lab)}">{ic(k, 20)}<span>{t(lab)}</span></button>'
    out += '<div style="flex:1 1 auto"></div>'
    out += f'<button class="ritem{" on" if active == "settings" else ""}" aria-label="{t("Settings")}">{ic("settings", 20)}<span>{t("Settings")}</span></button>'
    return f'<div class="rail">{out}</div>'

def statusbar(page="3", total="281", zoom="74%", path=r"P:\01-PIP\032-Tinh lo 991 noi dai\...\03. QUYEN 2.2.pdf", extra=""):
    return (f'<div class="status"><span style="max-width:380px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">{path}</span>{extra}'
            f'<div style="flex:1 1 auto"></div>'
            f'<button class="sbtn" aria-label="{t("Previous page")}">{ic("chevl", 15, 2)}</button><input class="pagebox" value="{page}" aria-label="{t("Page number")}" readonly><span>/ {total}</span>'
            f'<button class="sbtn" aria-label="{t("Next page")}">{ic("chevr", 15, 2)}</button>'
            f'<div style="flex:1 1 auto"></div>'
            f'<button class="sbtn" aria-label="{t("Zoom out")}">{ic("min", 15, 2)}</button><span style="width:44px;text-align:center;color:var(--text)">{zoom}</span>'
            f'<button class="sbtn" aria-label="{t("Zoom in")}">{ic("plus", 15, 2)}</button></div>')

def page_panel(sel=(2, 3, 4), insert=None, width=288, count=10):
    acts = (("insert", "Insert ▾"), ("trash", "Delete"), ("rotl", "Rotate left"), ("rotr", "Rotate right"), ("extract", "Extract"))
    a = ''.join(f'<button class="pact" aria-label="{t(l)}">{ic(i, 19)}<span>{t(l)}</span></button>' for i, l in acts)
    n = len(sel)
    selbar = (f'<div style="display:flex;align-items:center;gap:8px;padding:0 14px;height:32px;background:var(--sel);color:var(--accent);font-size:12px;font-weight:600;flex-shrink:0">'
              f'{t("3 pages selected")}<div style="flex:1 1 auto"></div><span style="font-weight:400;color:var(--muted)">{t("Ctrl+C copy · Ctrl+V paste")}</span></div>') if n else ""
    cells = ""
    for i in range(count):
        s = " sel" if i in sel else ""
        cells += f'<div class="thumb{s}" style="position:relative">{paper(i, 112, 80)}<span class="num">{i + 1}</span>'
        if insert is not None and i == insert:
            cells += '<div style="position:absolute;top:6px;bottom:6px;right:-3px;width:4px;border-radius:2px;background:var(--accent);z-index:3"></div>'
        cells += '</div>'
    grid = f'<div style="display:grid;grid-template-columns:repeat(2, minmax(0, 1fr));gap:2px;padding:8px 10px;overflow:hidden;flex:1 1 auto;align-content:start">{cells}</div>'
    return (f'<div style="width:{width}px;flex-shrink:0;display:flex;flex-direction:column;background:var(--surface);border-right:1px solid var(--border)">'
            f'<div class="hdr"><span>{t("Pages")}</span><span class="count">281</span></div>'
            f'<div style="display:flex;gap:2px;padding:0 10px 8px;border-bottom:1px solid var(--border);flex-shrink:0">{a}</div>{selbar}{grid}</div>')

def layer_panel(width=300):
    def row(depth, name, state, chip="", open_=None):
        pad = 10 + depth * 18
        cb = {"on": f'<span class="cb on">{ic("check", 12, 3)}</span>', "off": '<span class="cb"></span>',
              "mixed": f'<span class="cb mixed">{ic("min", 12, 3)}</span>'}[state]
        arrow = ic("chevd" if open_ else "chevr", 14, 2) if open_ is not None else '<span style="width:14px"></span>'
        ch = f'<span class="lbl" style="margin-left:auto;background:var(--chip);border-radius:8px;padding:1px 7px">{chip}</span>' if chip else ""
        col = "var(--text)" if state != "off" else "var(--muted)"
        return f'<div class="row" style="padding-left:{pad}px;color:{col}">{arrow}{cb}<span style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{name}</span>{ch}</div>'
    rows = (row(0, "1. Base map", "mixed", "", True) + row(1, "1.1 Terrain (contours)", "on") + row(1, "1.2 Boundaries, markers", "on", "3 files") +
            row(1, "1.3 Satellite imagery", "off") +
            row(0, "2. Traffic", "on", "", True) + row(1, "2.1 Road centerline", "on", "3 files") + row(1, "2.2 Curb / shoulder", "on") +
            row(1, "2.3 Carriageway", "on") + row(1, "2.4 Signs, traffic safety", "on") +
            row(0, "3. Drainage", "mixed", "", True) + row(1, "3.1 Culverts", "on", "2 files") + row(1, "3.2 Manholes, inlets", "on") + row(1, "3.3 Flow direction", "off") +
            row(0, "4. Utilities", "on", "", False) + row(0, "5. Notes, dimensions", "on", "", False) + row(0, "6. Other notes", "off", "", False))
    return (f'<div style="width:{width}px;flex-shrink:0;display:flex;flex-direction:column;background:var(--surface);border-right:1px solid var(--border)">'
            f'<div class="hdr"><span>Layers</span><span class="count">46</span></div>'
            f'<div style="padding:0 12px 10px;display:flex;gap:8px"><div class="field" style="flex:1 1 auto">{ic("search", 16)}<span>Search layers…</span></div>'
            f'<button class="ghost" aria-label="Expand all" style="width:34px;padding:0;justify-content:center">{ic("chevd", 16)}</button></div>'
            f'<div style="display:flex;gap:8px;padding:0 12px 8px"><button class="ghost" style="font-size:12px">Show all</button><button class="ghost" style="font-size:12px">Hide all</button></div>'
            f'<div style="flex:1 1 auto;overflow:hidden;padding:0 4px;border-top:1px solid var(--border)">{rows}</div>'
            f'<div style="padding:10px 14px;border-top:1px solid var(--border);color:var(--muted);font-size:11.5px;line-height:1.5">Layers with the same name from several files are merged into one. “3 files” shows how many source files contain the layer.</div></div>')

W, H = 1280, 820
def window(body, theme, w=W, h=H, status=True, tabs_html=None, top_extra="", active=None):
    return (f'<div class="win" style="width:{w}px;height:{h}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(theme)}">'
            f'{titlebar()}{toolbar(True, active)}{tabs_html if tabs_html is not None else tabs()}<div style="flex:1 1 auto;min-height:0;display:flex">{body}</div>{statusbar() if status else ""}</div>')

# 1/2/2b: cửa sổ chính
write("Main.dc.html", doc("XT PDF Reader — main window (light)", window(rail("pages") + page_panel() + viewer((0, 1)), LIGHT), W, H, LIGHT["accent"], lang="en"))
write("MainDark.dc.html", doc("XT PDF Reader — main window (dark)", window(rail("pages") + page_panel() + viewer((0, 1)), DARK), W, H, DARK["accent"], lang="en"))
LANG = "en"

# 3: Layers
write("Layers.dc.html", doc("XT PDF Reader — Layers panel", window(rail("layers") + layer_panel() + viewer((0, 3)), LIGHT), W, H, LIGHT["accent"], lang="en"))

# 4: Settings
def setting_row(label, hint, control):
    hint_html = f'<div style="color:var(--muted);font-size:12px;margin-top:3px;line-height:1.45">{hint}</div>' if hint else ""
    return (f'<div style="display:flex;align-items:center;gap:24px;padding:14px 0;border-bottom:1px solid var(--border)">'
            f'<div style="flex:1 1 auto;min-width:0"><div style="font-size:13px">{label}</div>{hint_html}</div>{control}</div>')
def seg(items, active):
    o = "".join(f'<div style="height:30px;padding:0 14px;display:flex;align-items:center;border-radius:5px;{"background:var(--accent);color:#ffffff;font-weight:600" if i == active else "color:var(--muted)"}">{x}</div>' for i, x in enumerate(items))
    return f'<div style="display:flex;gap:2px;padding:2px;border-radius:7px;background:var(--chip);flex-shrink:0">{o}</div>'
def sel_(text, w=170):
    return f'<div class="field" style="width:{w}px;justify-content:space-between;color:var(--text);flex-shrink:0">{text}{ic("chevd", 15, 2)}</div>'
def toggle(on=True):
    bg = "var(--accent)" if on else "var(--border)"; left = "18px" if on else "2px"
    return f'<div style="width:38px;height:22px;border-radius:11px;background:{bg};position:relative;flex-shrink:0"><div style="position:absolute;top:2px;left:{left};width:18px;height:18px;border-radius:50%;background:#ffffff"></div></div>'
def swatches():
    cols = ["#2563eb", "#0f8b6d", "#7c4dff", "#d9463b"]
    return '<div style="display:flex;gap:10px;flex-shrink:0">' + ''.join(
        f'<div style="width:26px;height:26px;border-radius:50%;background:{c};{"outline:2px solid var(--text);outline-offset:2px" if i == 0 else ""}"></div>' for i, c in enumerate(cols)) + '</div>'
nav = ""
for i, x in enumerate(("Appearance", "Display", "Performance & memory", "Integration")):
    nav += f'<div class="row" style="height:36px;{"background:var(--sel);color:var(--accent);font-weight:600" if i == 0 else "color:var(--text)"}">{x}</div>'
sec = lambda x: f'<div style="font-size:15px;font-weight:600;margin:22px 0 2px">{x}</div>'
content = ('<div style="font-size:20px;font-weight:600;margin-top:6px">Settings</div>'
           + sec("Appearance")
           + setting_row("Theme", "Light is the default. Dark is easier on the eyes for drawings with dark backgrounds.", seg(("Light", "Dark", "System"), 0))
           + setting_row("Accent color", "Used for the main button, the selected item and the drop marker.", swatches())
           + sec("Display")
           + setting_row("Default view mode", "Applies to files opened for the first time.", sel_("Continuous scroll"))
           + setting_row("Zoom when opening a file", "", sel_("Fit width"))
           + sec("Performance & memory")
           + setting_row("Files kept warm", "Recently used files keep their memory for fast scrolling and zoom. Older files are released; reopening stays fast thanks to the disk cache.", sel_("2 files", 110))
           + setting_row("File cache on disk", "Files opened over a network are cached in the temp folder and deleted when the file is closed. Using 852 MB.", '<button class="ghost" style="flex-shrink:0">Clear cache</button>')
           + sec("Integration")
           + setting_row("Use this app when “View PDF” is pressed in pdfFactory", "", toggle(True)))
sbody = (f'<div style="width:236px;flex-shrink:0;background:var(--panel);border-right:1px solid var(--border);padding:16px 12px;display:flex;flex-direction:column;gap:2px">{nav}</div>'
         f'<div style="flex:1 1 auto;min-width:0;background:var(--surface);overflow:hidden;padding:20px 48px"><div style="max-width:760px">{content}</div></div>')
write("Settings.dc.html", doc("XT PDF Reader — settings", window(rail("settings") + sbody, LIGHT), W, H, LIGHT["accent"], lang="en"))

# 5: Page panel ops
PW, PH = 780, 660
def menu_items():
    def mi(icn, x, k="", hot=False, danger=False):
        return f'<div class="mi{" hot" if hot else ""}{" danger" if danger else ""}">{ic(icn, 16)}<span>{x}</span><span class="k">{k}</span></div>'
    return (mi("copy", "Copy", "Ctrl+C") + mi("cut", "Cut", "Ctrl+X") + mi("paste", "Paste after", "Ctrl+V") + mi("paste", "Paste before", "Ctrl+Shift+V") +
            '<div class="msep"></div>' +
            f'<div class="mi hot">{ic("goto", 16)}<span>Move</span><span class="k">{ic("chevr", 14, 2)}</span></div>' +
            mi("dup", "Duplicate", "Ctrl+D") + mi("rotl", "Rotate left", "Ctrl+L") + mi("rotr", "Rotate right", "Ctrl+R") + mi("extract", "Extract…") +
            mi("merge", "Send to Merge window") +
            '<div class="msep"></div>' + mi("trash", "Delete 3 pages", "Del", danger=True))
def submenu_items():
    def mi(icn, x, k=""):
        return f'<div class="mi">{ic(icn, 16)}<span>{x}</span><span class="k">{k}</span></div>'
    return (mi("top", "Move to start", "Ctrl+Shift+Home") + mi("up", "Move up one", "Alt+↑") + mi("down", "Move down one", "Alt+↓") +
            mi("bottom", "Move to end", "Ctrl+Shift+End") + '<div class="msep"></div>' + mi("goto", "Move to position…"))
ghost = ('<div style="position:absolute;left:150px;top:300px;z-index:6;pointer-events:none">'
         f'<div style="position:relative;width:118px;height:88px"><span class="paper" style="position:absolute;left:8px;top:8px;opacity:.7">{cad(2)}</span>'
         f'<span class="paper" style="position:absolute;left:4px;top:4px;opacity:.85">{cad(1)}</span><span class="paper" style="position:absolute;left:0;top:0;box-shadow:0 6px 16px rgba(0,0,0,.3)">{cad(0)}</span>'
         '<div style="position:absolute;right:-8px;top:-8px;min-width:24px;height:24px;border-radius:12px;background:var(--accent);color:#ffffff;font-weight:600;display:flex;align-items:center;justify-content:center">3</div></div>'
         '<div style="margin-top:10px;background:var(--text);color:var(--surface);border-radius:6px;padding:5px 9px;font-size:11.5px;white-space:nowrap;width:max-content">Move 3 pages · hold Ctrl to copy</div></div>')
inner = (f'<div class="win" style="width:{PW}px;height:{PH}px;display:flex;flex-direction:column;overflow:hidden;position:relative;--accent:{{{{accent}}}};{tok(LIGHT)}">'
         f'{tabs(active=0, hover=1)}<div style="flex:1 1 auto;min-height:0;display:flex;position:relative">{rail("pages")}{page_panel(sel=(2, 3, 4), insert=6, count=8)}'
         f'<div style="flex:1 1 auto;background:var(--view)"></div>'
         f'<div style="position:absolute;left:246px;top:150px;z-index:8;display:flex;gap:6px;align-items:flex-start"><div class="menu" style="width:322px">{menu_items()}</div>'
         f'<div class="menu" style="width:262px;margin-top:150px">{submenu_items()}</div></div>{ghost}</div></div>')
write("PageOps.dc.html", doc("Pages panel — context menu and drag & drop", inner, PW, PH, LIGHT["accent"], lang="en"))

# 6: Merge workspace — cửa sổ con
def docwin(name, pages, x, y, w, h, target=False, ghostpages=False, insert_at=None, sel=(), cols=4, v0=0, count=12):
    cells = ""
    tw = int((w - 32 - (cols - 1) * 4) / cols) - 12
    th = int(tw * 0.71)
    for i in range(count):
        s = " sel" if i in sel else ""
        mark = ""
        if insert_at is not None and i == insert_at:
            mark = '<div style="position:absolute;top:6px;bottom:6px;left:-4px;width:4px;border-radius:2px;background:var(--accent);z-index:3"></div>'
        cells += f'<div class="thumb{s}" style="padding:6px 6px 4px">{mark}{paper(v0 + i, tw, th)}<span class="num">{i + 1}</span></div>'
    border = "2px solid var(--accent)" if target else "1px solid var(--border)"
    hdr_bg = "var(--sel)" if target else "var(--panel)"
    hbtn = lambda icn, label: f'<button class="sbtn" aria-label="{label}" title="{label}">{ic(icn, 15, 1.9)}</button>'
    return (f'<div style="position:absolute;left:{x}px;top:{y}px;width:{w}px;height:{h}px;display:flex;flex-direction:column;background:var(--surface);border:{border};border-radius:8px;overflow:hidden;box-shadow:0 4px 16px rgba(20,32,50,0.12)">'
            f'<div style="height:40px;flex-shrink:0;display:flex;align-items:center;gap:8px;padding:0 6px 0 12px;background:{hdr_bg};border-bottom:1px solid var(--border)" title="Double-click to maximize / restore">'
            f'<span style="color:var(--muted)">{ic("grip", 15)}</span><span style="font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;min-width:0">{name}</span>'
            f'<span class="count" style="flex-shrink:0">{pages} pages</span><div style="flex:1 1 auto"></div>'
            f'<div class="field" style="height:26px;width:84px;padding:0 8px;font-size:12px;flex-shrink:0">Go to…</div>'
            f'{hbtn("save", "Save")}{hbtn("min", "Minimize to dock")}{hbtn("max", "Maximize")}{hbtn("close", "Close")}</div>'
            f'<div style="flex:1 1 auto;overflow:hidden;position:relative"><div style="display:grid;grid-template-columns:repeat({cols}, minmax(0, 1fr));gap:4px;padding:10px 16px">{cells}</div>'
            f'<div style="position:absolute;right:0;top:0;bottom:0;width:12px;background:var(--panel);border-left:1px solid var(--border)"><div style="margin:6px 2px;height:60px;border-radius:4px;background:var(--border)"></div></div></div></div>')
def layout_btn(kind, on=False):
    boxes = {"1": '<rect x="4" y="5" width="16" height="14" rx="1.5"/>',
             "2": '<rect x="3.5" y="5" width="7.5" height="14" rx="1.5"/><rect x="13" y="5" width="7.5" height="14" rx="1.5"/>',
             "3": '<rect x="3" y="5" width="5" height="14" rx="1.2"/><rect x="9.5" y="5" width="5" height="14" rx="1.2"/><rect x="16" y="5" width="5" height="14" rx="1.2"/>',
             "4": '<rect x="3.5" y="4.5" width="7.5" height="6.5" rx="1.2"/><rect x="13" y="4.5" width="7.5" height="6.5" rx="1.2"/><rect x="3.5" y="13" width="7.5" height="6.5" rx="1.2"/><rect x="13" y="13" width="7.5" height="6.5" rx="1.2"/>',
             "free": '<rect x="3" y="4" width="11" height="9" rx="1.5"/><rect x="9" y="10" width="12" height="9" rx="1.5"/>'}[kind]
    lab = {"1": "1 window", "2": "2 side by side", "3": "3 columns", "4": "2 × 2 grid", "free": "Free"}[kind]
    st = "background:var(--sel);color:var(--accent);" if on else "color:var(--text);"
    return (f'<button aria-label="{lab}" title="{lab}" style="width:36px;height:32px;border-radius:6px;display:flex;align-items:center;justify-content:center;{st}">'
            f'<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" aria-hidden="true">{boxes}</svg></button>')
mtool = (f'<div style="height:52px;flex-shrink:0;display:flex;align-items:center;gap:8px;padding:0 12px;background:var(--surface);border-bottom:1px solid var(--border)">'
         f'{tb("undo", "Undo")}{tb("redo", "Redo")}<div class="sep"></div>'
         f'<span class="lbl" style="margin-right:2px">Layout</span><div style="display:flex;gap:2px;padding:2px;border-radius:8px;background:var(--chip)">{layout_btn("1")}{layout_btn("2", True)}{layout_btn("3")}{layout_btn("4")}{layout_btn("free")}</div>'
         f'<div class="sep"></div><div style="display:flex;align-items:center;gap:8px;color:var(--muted)">{ic("pages", 16)}Page size<div style="width:100px;height:4px;border-radius:2px;background:var(--border);position:relative"><div style="position:absolute;left:44px;top:-5px;width:14px;height:14px;border-radius:50%;background:var(--accent)"></div></div></div>'
         f'<div class="sep"></div><span class="chipx"><span class="cb on" style="width:14px;height:14px">{ic("check", 10, 3)}</span>Merge same-name layers</span>'
         f'<div style="flex:1 1 auto"></div><button class="primary">{ic("save", 16, 1.9)}Save all</button></div>')
WSW, WSH = W, H - 34 - 52 - 30 - 46   # vùng làm việc
gap = 12
ww = (WSW - gap * 3) // 2
hh = WSH - gap * 2
left = docwin("03. QUYEN 2.2 - TCTC.pdf", 281, gap, gap, ww, hh, sel=(2, 3), cols=4, v0=0, count=16)
right = docwin("02. QUYEN 2.1 - GT.pdf", 158, gap * 2 + ww, gap, ww, hh, target=True, insert_at=6, cols=4, v0=1, count=16)
mghost = ('<div style="position:absolute;left:%dpx;top:%dpx;z-index:9;pointer-events:none">' % (gap * 2 + ww - 150, 300) +
          f'<div style="position:relative;width:132px;height:100px"><span class="paper" style="position:absolute;left:10px;top:10px;opacity:.7">{cad(3, 118, 84)}</span>'
          f'<span class="paper" style="position:absolute;left:5px;top:5px;opacity:.85">{cad(2, 118, 84)}</span><span class="paper" style="position:absolute;left:0;top:0;box-shadow:0 8px 20px rgba(0,0,0,.3)">{cad(1, 118, 84)}</span>'
          '<div style="position:absolute;right:0;top:-8px;min-width:26px;height:26px;border-radius:13px;background:var(--accent);color:#ffffff;font-weight:600;display:flex;align-items:center;justify-content:center">2</div></div>'
          '<div style="margin-top:10px;background:var(--text);color:var(--surface);border-radius:6px;padding:5px 9px;font-size:11.5px;white-space:nowrap;width:max-content">Move 2 pages · hold Ctrl to copy</div></div>')
def dock_chip(name, n, temp=False):
    st = "border:1.5px dashed var(--accent);background:var(--sel);" if temp else "border:1px solid var(--border);background:var(--surface);"
    return (f'<div style="display:flex;align-items:center;gap:8px;height:30px;padding:0 10px;border-radius:15px;{st}flex-shrink:0">{ic("file", 15)}'
            f'<span style="white-space:nowrap">{name}</span><span class="lbl">{n} pages</span></div>')
dock = (f'<div style="height:46px;flex-shrink:0;display:flex;align-items:center;gap:10px;padding:0 12px;background:var(--panel);border-top:1px solid var(--border)">'
        f'<span style="display:flex;align-items:center;gap:6px;font-weight:600">{ic("tray", 16)}Dock</span>'
        f'{dock_chip("Temp 1", 4, True)}{dock_chip("Volume 3 - Concept drawings.pdf", 120)}'
        f'<span class="lbl" style="margin-left:6px">Drop pages on a chip to add them · drag pages onto empty space to create a temp window</span></div>')
mstatus = ('<div class="status"><span>2 windows shown · 1 minimized · maximum 4 shown at once</span><div style="flex:1 1 auto"></div>'
           '<span>Drag = move · Ctrl+drag = copy · drag a window header onto another file to insert all its pages</span></div>')
mwork = (f'<div style="flex:1 1 auto;min-height:0;position:relative;background:var(--view);overflow:hidden">{left}{right}{mghost}</div>')
inner = (f'<div class="win" style="width:{W}px;height:{H}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(LIGHT)}">'
         f'{titlebar("XT PDF Reader", "Merge files")}{mtool}{mwork}{dock}{mstatus}</div>')
write("Merge.dc.html", doc("XT PDF Reader — Merge files workspace", inner, W, H, LIGHT["accent"], lang="en"))

# 7: Bố cục & giới hạn
LW, LH = 780, 520
def lay_card(kind, title, sub, on=False):
    boxes = {"1": [(0, 0, 100, 100)], "2": [(0, 0, 49, 100), (51, 0, 49, 100)], "3": [(0, 0, 32, 100), (34, 0, 32, 100), (68, 0, 32, 100)],
             "4": [(0, 0, 49, 49), (51, 0, 49, 49), (0, 51, 49, 49), (51, 51, 49, 49)], "free": [(0, 0, 58, 60), (30, 34, 66, 62)]}[kind]
    bx = ''.join(f'<div style="position:absolute;left:{x}%;top:{y}%;width:{w}%;height:{h}%;border:1.5px solid {"var(--accent)" if on else "var(--muted)"};border-radius:3px;background:{"var(--sel)" if on else "var(--surface)"}"></div>' for x, y, w, h in boxes)
    return (f'<div style="display:flex;flex-direction:column;gap:8px;width:130px;flex-shrink:0"><div style="position:relative;height:84px;border-radius:8px;border:{"2px solid var(--accent)" if on else "1px solid var(--border)"};background:var(--panel);padding:8px"><div style="position:relative;width:100%;height:100%">{bx}</div></div>'
            f'<div style="font-weight:600">{title}</div><div class="lbl" style="line-height:1.4">{sub}</div></div>')
rules = ''.join(f'<div style="display:flex;gap:10px;align-items:flex-start;line-height:1.5"><span style="color:var(--accent);margin-top:3px">{ic("check", 15, 2.4)}</span><span>{r}</span></div>' for r in (
    "At most 4 windows are shown at once. Opening a fifth minimizes the least recently used one to the dock.",
    "A window can't be smaller than 320 × 240 and can't be dragged or resized outside the workspace.",
    "Free mode keeps your positions; switching to a tiled layout arranges the windows and remembers the free layout.",
    "Double-click a header to maximize; double-click again to return to the previous size and position.",
    "Drag a window to a screen edge to snap it (left, right, corners); a preview shows where it will land."))
inner = (f'<div class="win" style="width:{LW}px;height:{LH}px;display:flex;flex-direction:column;padding:28px 32px;gap:22px;background:var(--surface);--accent:{{{{accent}}}};{tok(LIGHT)}">'
         f'<div style="font-size:18px;font-weight:600">Layouts and limits</div>'
         f'<div style="display:flex;gap:18px">{lay_card("1", "1 window", "One file fills the workspace")}{lay_card("2", "2 side by side", "Copy or move between two files", True)}{lay_card("3", "3 columns", "Two sources and a result")}{lay_card("4", "2 × 2 grid", "Four files at once")}{lay_card("free", "Free", "Move and resize freely")}</div>'
         f'<div style="display:flex;flex-direction:column;gap:10px;padding-top:6px;border-top:1px solid var(--border)"><div style="padding-top:14px;font-weight:600">Rules</div>{rules}</div></div>')
write("Layouts.dc.html", doc("Merge files — layouts and limits", inner, LW, LH, LIGHT["accent"], lang="en"))

PW_, PH_, LW_, LH_ = PW, PH, LW, LH
