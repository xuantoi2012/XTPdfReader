# -*- coding: utf-8 -*-
"""Sinh bộ artboard XT PDF Reader (sáng = chuẩn, tối = biến thể) từ một bộ token."""
import json, os, html

OUT = os.path.join(os.path.dirname(__file__), "ui")
PROJ = os.path.join(OUT, "project")
os.makedirs(PROJ, exist_ok=True)

LIGHT = dict(bg="#f1f4f8", surface="#ffffff", panel="#f7f9fb", border="#dbe1e8", text="#1b2733", muted="#566373",
             hover="#e9eef4", view="#dfe5ec", chip="#e9eef4", sel="color-mix(in srgb, var(--accent) 12%, #ffffff)",
             shadow="0 10px 30px rgba(20,32,50,0.18)", accent="#2563eb", warn="#b45309")
DARK = dict(bg="#171a1f", surface="#20242b", panel="#1c2026", border="#2f353e", text="#e8ebf0", muted="#a3acb9",
            hover="#2a3039", view="#0f1114", chip="#2a3039", sel="color-mix(in srgb, var(--accent) 24%, #20242b)",
            shadow="0 12px 34px rgba(0,0,0,0.55)", accent="#6ea8ff", warn="#f0a35e")

ICONS = {
 "hand": '<path d="M8 13V6a1.5 1.5 0 0 1 3 0v5M11 11V4.5a1.5 1.5 0 0 1 3 0V11M14 11.2V6a1.5 1.5 0 0 1 3 0v7"/><path d="M17 12v-2a1.5 1.5 0 0 1 3 0v6a6 6 0 0 1-6 6h-2a6 6 0 0 1-5.2-3L5 14.5C4.3 13.3 5.4 12 6.6 12.6L8 13.3"/>',
 "undo": '<path d="M7 7 3 11l4 4"/><path d="M3 11h11a6 6 0 0 1 0 12h-3"/>',
 "redo": '<path d="M17 7l4 4-4 4"/><path d="M21 11H10a6 6 0 0 0 0 12h3"/>',
 "scroll": '<rect x="6" y="3.5" width="12" height="6" rx="1"/><rect x="6" y="14.5" width="12" height="6" rx="1"/>',
 "fitw": '<path d="M3 12h4M17 12h4M8 7l-4 5 4 5M16 7l4 5-4 5"/>',
 "fitp": '<path d="M9 4H5a1 1 0 0 0-1 1v4M15 4h4a1 1 0 0 1 1 1v4M9 20H5a1 1 0 0 1-1-1v-4M15 20h4a1 1 0 0 0 1-1v-4"/>',
 "rotl": '<path d="M9 4 6 7l3 3"/><path d="M6 7h9a5 5 0 0 1 0 10H9"/>',
 "rotr": '<path d="M15 4l3 3-3 3"/><path d="M18 7H9a5 5 0 0 0 0 10h6"/>',
 "type": '<path d="M5 6V4h14v2M12 4v16M9 20h6"/>',
 "note": '<path d="M4 5h16v10H9l-4 4V5Z"/>',
 "hl": '<path d="M4 20l3.5-1 9-9-2.5-2.5-9 9Z"/><path d="M14 5l2.5 2.5L19 5l-2.5-2.5Z"/>',
 "merge": '<rect x="4" y="4" width="10" height="10" rx="1.5"/><rect x="10" y="10" width="10" height="10" rx="1.5"/>',
 "pages": '<rect x="5" y="3" width="12" height="16" rx="1.5"/><path d="M9 19v1.5A1.5 1.5 0 0 0 10.5 22h8A1.5 1.5 0 0 0 20 20.5v-13A1.5 1.5 0 0 0 18.5 6H17"/>',
 "bookmark": '<path d="M6 3h12v18l-6-4-6 4Z"/>',
 "layers": '<path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 13 9 5 9-5"/>',
 "settings": '<circle cx="12" cy="12" r="3"/><path d="M19.4 13.5a7.6 7.6 0 0 0 0-3l2-1.4-2-3.4-2.3.6a7.6 7.6 0 0 0-2.6-1.5L14 2h-4l-.5 2.3a7.6 7.6 0 0 0-2.6 1.5l-2.3-.6-2 3.4 2 1.4a7.6 7.6 0 0 0 0 3l-2 1.4 2 3.4 2.3-.6a7.6 7.6 0 0 0 2.6 1.5L10 22h4l.5-2.3a7.6 7.6 0 0 0 2.6-1.5l2.3.6 2-3.4Z"/>',
 "close": '<path d="M5 5l14 14M19 5 5 19"/>',
 "plus": '<path d="M12 5v14M5 12h14"/>',
 "min": '<path d="M5 12h14"/>',
 "max": '<rect x="5" y="5" width="14" height="14" rx="1.5"/>',
 "chevd": '<path d="m6 9 6 6 6-6"/>',
 "chevr": '<path d="m9 6 6 6-6 6"/>',
 "chevl": '<path d="m15 6-6 6 6 6"/>',
 "search": '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m20 20-4.8-4.8"/>',
 "eye": '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
 "trash": '<path d="M5 7h14M9 7V5h6v2M7 7l1 13h8l1-13"/>',
 "copy": '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/>',
 "cut": '<circle cx="6" cy="7" r="2.5"/><circle cx="6" cy="17" r="2.5"/><path d="M8 8.5 20 18M8 15.5 20 6"/>',
 "paste": '<rect x="6" y="5" width="12" height="16" rx="2"/><path d="M9 5V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v1"/>',
 "dup": '<rect x="4" y="4" width="11" height="11" rx="2"/><rect x="9" y="9" width="11" height="11" rx="2"/>',
 "up": '<path d="M12 19V5M6 11l6-6 6 6"/>',
 "down": '<path d="M12 5v14M6 13l6 6 6-6"/>',
 "top": '<path d="M5 4h14M12 20V8M6 14l6-6 6 6"/>',
 "bottom": '<path d="M5 20h14M12 4v12M6 10l6 6 6-6"/>',
 "goto": '<path d="M5 12h14M13 6l6 6-6 6"/>',
 "extract": '<path d="M7 3h7l4 4v14H7Z"/><path d="M11 17l3-3-3-3M14 14H7"/>',
 "insert": '<path d="M7 3h7l4 4v14H7Z"/><path d="M12 11v6M9 14h6"/>',
 "file": '<path d="M7 3h7l4 4v14H7Z"/><path d="M14 3v4h4"/>',
 "folder": '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/>',
 "save": '<path d="M5 4h11l3 3v13H5Z"/><path d="M8 4v5h7V4M8 20v-6h8v6"/>',
 "tray": '<path d="M3 13v6h18v-6M3 13l3-8h12l3 8M3 13h5l1 3h6l1-3h5"/>',
 "grip": '<circle cx="9" cy="6" r="1.2"/><circle cx="15" cy="6" r="1.2"/><circle cx="9" cy="12" r="1.2"/><circle cx="15" cy="12" r="1.2"/><circle cx="9" cy="18" r="1.2"/><circle cx="15" cy="18" r="1.2"/>',
 "check": '<path d="m5 12 4.5 4.5L19 7"/>',
}

def ic(name, size=18, sw=1.7):
    return (f'<svg width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="{sw}" '
            f'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" style="flex-shrink:0">{ICONS[name]}</svg>')

CSS = """
  * { box-sizing: border-box; }
  body { margin: 0; }
  .win { font-family: "Segoe UI", system-ui, sans-serif; font-size: 12.5px; color: var(--text); background: var(--bg); -webkit-font-smoothing: antialiased; }
  button { font-family: inherit; font-size: inherit; color: inherit; background: none; border: none; padding: 0; cursor: pointer; }
  button:focus-visible, input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
  .tbtn { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; width: 60px; height: 48px; border-radius: 6px; color: var(--text); flex-shrink: 0; }
  .tbtn span { font-size: 11px; line-height: 1; white-space: nowrap; color: var(--muted); }
  .tbtn:hover { background: var(--hover); }
  .tbtn.on { background: var(--sel); color: var(--accent); }
  .tbtn.on span { color: var(--accent); }
  .sep { width: 1px; align-self: stretch; margin: 6px 6px; background: var(--border); flex-shrink: 0; }
  .primary { display: flex; align-items: center; gap: 7px; height: 34px; padding: 0 14px; border-radius: 6px; background: var(--accent); color: #ffffff; font-weight: 600; white-space: nowrap; }
  .ghost { display: flex; align-items: center; gap: 6px; height: 30px; padding: 0 10px; border-radius: 6px; border: 1px solid var(--border); background: var(--surface); white-space: nowrap; }
  .ghost:hover { background: var(--hover); }
  .tab { display: flex; align-items: center; gap: 8px; height: 34px; padding: 0 8px 0 12px; color: var(--muted); white-space: nowrap; border-bottom: 2px solid transparent; }
  .tab.active { color: var(--text); background: var(--surface); border-bottom-color: var(--accent); }
  .tab .x { width: 20px; height: 20px; border-radius: 4px; display: flex; align-items: center; justify-content: center; }
  .tab .x:hover { background: var(--hover); }
  .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--warn); flex-shrink: 0; }
  .rail { width: 64px; flex-shrink: 0; display: flex; flex-direction: column; align-items: center; gap: 2px; padding: 8px 0; background: var(--panel); border-right: 1px solid var(--border); }
  .ritem { width: 56px; height: 54px; border-radius: 6px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; color: var(--muted); position: relative; }
  .ritem span { font-size: 11px; line-height: 1; }
  .ritem:hover { background: var(--hover); color: var(--text); }
  .ritem.on { color: var(--accent); background: var(--sel); }
  .pact { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; height: 46px; border-radius: 6px; color: var(--text); flex: 1 1 0; min-width: 0; }
  .pact span { font-size: 11px; line-height: 1; color: var(--muted); white-space: nowrap; }
  .pact:hover { background: var(--hover); }
  .thumb { display: flex; flex-direction: column; align-items: center; gap: 6px; padding: 8px 8px 6px; border-radius: 6px; border: 1.5px solid transparent; position: relative; }
  .thumb:hover { background: var(--hover); }
  .thumb.sel { background: var(--sel); border-color: var(--accent); }
  .thumb .num { font-size: 11px; color: var(--muted); }
  .thumb.sel .num { color: var(--accent); font-weight: 600; }
  .paper { background: #ffffff; border: 1px solid #cfd6de; border-radius: 2px; overflow: hidden; display: block; }
  .paper svg { display: block; }
  .chipx { display: inline-flex; align-items: center; gap: 6px; height: 26px; padding: 0 10px; border-radius: 13px; background: var(--chip); color: var(--text); font-size: 12px; }
  .menu { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; box-shadow: var(--shadow); padding: 5px; display: flex; flex-direction: column; }
  .mi { display: flex; align-items: center; gap: 10px; height: 30px; padding: 0 10px; border-radius: 5px; white-space: nowrap; }
  .mi .k { margin-left: auto; padding-left: 22px; color: var(--muted); font-size: 11.5px; }
  .mi.hot { background: var(--sel); }
  .mi.danger { color: #c0392b; }
  .msep { height: 1px; background: var(--border); margin: 4px 6px; }
  .status { height: 30px; flex-shrink: 0; display: flex; align-items: center; padding: 0 12px; gap: 10px; background: var(--panel); border-top: 1px solid var(--border); color: var(--muted); font-size: 12px; }
  .sbtn { width: 26px; height: 26px; border-radius: 5px; display: flex; align-items: center; justify-content: center; color: var(--muted); }
  .sbtn:hover { background: var(--hover); color: var(--text); }
  .pagebox { width: 42px; height: 22px; border-radius: 4px; background: var(--surface); border: 1px solid var(--border); color: var(--text); text-align: center; font-size: 12px; }
  .hdr { display: flex; align-items: center; justify-content: space-between; padding: 0 14px; height: 40px; font-weight: 600; font-size: 13px; flex-shrink: 0; }
  .count { font-weight: 400; font-size: 11.5px; color: var(--muted); background: var(--chip); border-radius: 9px; padding: 1px 8px; }
  .field { display: flex; align-items: center; gap: 8px; height: 32px; padding: 0 10px; border-radius: 6px; border: 1px solid var(--border); background: var(--surface); color: var(--muted); }
  .cb { width: 16px; height: 16px; border-radius: 4px; border: 1.5px solid var(--muted); display: flex; align-items: center; justify-content: center; flex-shrink: 0; color: #ffffff; }
  .cb.on { background: var(--accent); border-color: var(--accent); }
  .cb.mixed { background: var(--accent); border-color: var(--accent); }
  .row { display: flex; align-items: center; gap: 9px; height: 30px; padding: 0 10px; border-radius: 5px; }
  .row:hover { background: var(--hover); }
  .lbl { font-size: 11px; color: var(--muted); }
"""

def tok(theme):
    t = theme
    return (f"--bg:{t['bg']};--surface:{t['surface']};--panel:{t['panel']};--border:{t['border']};--text:{t['text']};"
            f"--muted:{t['muted']};--hover:{t['hover']};--view:{t['view']};--chip:{t['chip']};--sel:{t['sel']};"
            f"--shadow:{t['shadow']};--warn:{t['warn']};")

# ── hình trang CAD giả (nét vẽ, không phải nội dung thật) ─────────────────────
def cad(v, w=112, h=80, sw=0.7):
    g = "#6b7785"; r = "#c2453b"; b = "#3b6fd4"
    body = {
     0: f'<path d="M6 60 C30 44 52 60 76 40 S100 30 108 22" stroke="{g}" fill="none"/><path d="M6 66 C30 50 52 66 76 46 S100 36 108 28" stroke="{g}" fill="none"/><path d="M14 20h34M14 26h22" stroke="{b}"/><circle cx="90" cy="60" r="7" stroke="{r}" fill="none"/>',
     1: ''.join(f'<path d="M8 {14+i*8}h96" stroke="{g}"/>' for i in range(6)) + ''.join(f'<path d="M{8+j*24} 14v40" stroke="{g}"/>' for j in range(5)),
     2: ''.join(f'<path d="M{16+i*22} 22v26M{10+i*22} 48h12" stroke="{g}"/><circle cx="{16+i*22}" cy="18" r="3" stroke="{b}" fill="none"/>' for i in range(4)) + f'<path d="M8 60h96" stroke="{r}"/>',
     3: f'<rect x="10" y="16" width="30" height="20" stroke="{g}" fill="none"/><rect x="48" y="24" width="40" height="28" stroke="{g}" fill="none"/><path d="M10 48h30M92 20l12 12" stroke="{b}"/><path d="M12 62h60" stroke="{r}"/>',
    }[v % 4]
    return (f'<svg width="{w}" height="{h}" viewBox="0 0 112 80" fill="none" stroke-width="{sw}" xmlns="http://www.w3.org/2000/svg">'
            f'<rect x="3" y="3" width="106" height="74" stroke="#4b5563" stroke-width="{sw*1.4}"/>{body}'
            f'<rect x="72" y="66" width="37" height="11" stroke="#4b5563" stroke-width="{sw}"/><path d="M84 66v11M96 66v11" stroke="#9aa3ae" stroke-width="{sw}"/></svg>')

def paper(v, w=112, h=80):
    return f'<span class="paper" style="width:{w}px;height:{h}px">{cad(v, w, h)}</span>'

# ── khung chung ────────────────────────────────────────────────────────────────
def titlebar(title="XT PDF Reader", sub=""):
    ctl = ''.join(f'<button aria-label="{a}" style="width:40px;height:30px;display:flex;align-items:center;justify-content:center;color:var(--muted)">{ic(i, 13, 1.6)}</button>'
                  for a, i in (("Thu nhỏ", "min"), ("Phóng to", "max"), ("Đóng", "close")))
    s = f'<span style="color:var(--muted);font-weight:400">— {sub}</span>' if sub else ""
    return (f'<div style="height:34px;flex-shrink:0;display:flex;align-items:center;gap:10px;padding:0 0 0 12px;background:var(--surface);border-bottom:1px solid var(--border)">'
            f'<div style="width:14px;height:14px;border-radius:3px;background:#d9463b"></div>'
            f'<span style="font-weight:600;font-size:12.5px">{title}</span>{s}<div style="flex:1 1 auto"></div>{ctl}</div>')

def tb(icon, label, on=False, title=None):
    return f'<button class="tbtn{" on" if on else ""}" aria-label="{title or label}" title="{title or label}">{ic(icon, 20)}<span>{label}</span></button>'

def toolbar(merge=True):
    g1 = tb("hand", "Tay") + tb("undo", "Hoàn tác") + tb("redo", "Làm lại")
    g2 = tb("scroll", "Cuộn liên tục", True) + tb("fitp", "Vừa trang") + tb("fitw", "Vừa rộng") + tb("rotl", "Xoay trái") + tb("rotr", "Xoay phải")
    g3 = tb("type", "Typewriter") + tb("note", "Ghi chú") + tb("hl", "Highlight")
    sep = '<div class="sep"></div>'
    m = f'<button class="primary" aria-label="Ghép file">{ic("merge", 17, 1.9)}Ghép file</button>' if merge else ""
    return (f'<div style="height:60px;flex-shrink:0;display:flex;align-items:center;gap:2px;padding:0 12px;background:var(--surface);border-bottom:1px solid var(--border)">'
            f'{g1}{sep}{g2}{sep}{g3}<div style="flex:1 1 auto"></div>{m}</div>')

def tabs(active=0, names=("03. QUYEN 2.2 - TCTC.pdf", "02. QUYEN 2.1 - GT.pdf", "Tập 3 - Bản vẽ cơ sở.pdf"), dirty=(0,), hover=None):
    out = ""
    for i, n in enumerate(names):
        d = '<span class="dot" title="Có thay đổi chưa lưu"></span>' if i in dirty else ""
        extra = ""
        if hover == i:
            extra = "outline:2px dashed var(--accent);outline-offset:-3px;background:var(--sel);color:var(--text);"
        out += (f'<div class="tab{" active" if i == active else ""}" style="{extra}">{ic("file", 15)}<span>{n}</span>{d}'
                f'<button class="x" aria-label="Đóng file">{ic("close", 11, 2)}</button></div>')
    return (f'<div style="height:36px;flex-shrink:0;display:flex;align-items:flex-end;gap:1px;padding:0 8px;background:var(--panel);border-bottom:1px solid var(--border)">'
            f'{out}<button class="sbtn" style="margin:0 0 4px 4px" aria-label="Mở thêm file">{ic("plus", 16)}</button></div>')

def rail(active="pages"):
    items = (("pages", "Trang"), ("bookmark", "Dấu trang"), ("layers", "Layer"))
    out = ""
    for k, lab in items:
        out += f'<button class="ritem{" on" if k == active else ""}" aria-label="{lab}">{ic(k, 20)}<span>{lab}</span></button>'
    out += '<div style="flex:1 1 auto"></div>'
    out += f'<button class="ritem{" on" if active == "settings" else ""}" aria-label="Cài đặt">{ic("settings", 20)}<span>Cài đặt</span></button>'
    return f'<div class="rail">{out}</div>'

def statusbar(page="3", total="281", zoom="74%", path=r"P:\01-PIP\032-Tinh lo 991 noi dai\...\03. QUYEN 2.2.pdf", extra=""):
    return (f'<div class="status"><span style="max-width:380px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">{path}</span>{extra}'
            f'<div style="flex:1 1 auto"></div>'
            f'<button class="sbtn" aria-label="Trang trước">{ic("chevl", 15, 2)}</button><input class="pagebox" value="{page}" aria-label="Số trang" readonly><span>/ {total}</span>'
            f'<button class="sbtn" aria-label="Trang sau">{ic("chevr", 15, 2)}</button>'
            f'<div style="flex:1 1 auto"></div>'
            f'<button class="sbtn" aria-label="Thu nhỏ">{ic("min", 15, 2)}</button><span style="width:44px;text-align:center;color:var(--text)">{zoom}</span>'
            f'<button class="sbtn" aria-label="Phóng to">{ic("plus", 15, 2)}</button></div>')

def viewer(sheets=(0, 1)):
    pages = ""
    for v in sheets:
        pages += (f'<div style="width:560px;height:396px;background:#ffffff;box-shadow:0 2px 10px rgba(0,0,0,0.25);flex-shrink:0">'
                  f'{cad(v, 560, 396, 0.5)}</div>')
    return (f'<div style="flex:1 1 auto;min-width:0;background:var(--view);overflow:hidden;display:flex;flex-direction:column;align-items:center;gap:16px;padding:22px 0;position:relative">{pages}'
            f'<div style="position:absolute;right:0;top:0;bottom:0;width:12px;background:var(--panel);border-left:1px solid var(--border)"><div style="margin:8px 2px;height:120px;border-radius:4px;background:var(--border)"></div></div></div>')

def thumb_grid(sel=(2, 3, 4), count=10, start=1, cols=2, w=112, h=80, insert_after=None):
    cells = ""
    for i in range(count):
        n = start + i
        s = " sel" if (i in sel) else ""
        cells += f'<div class="thumb{s}">{paper(i, w, h)}<span class="num">{n}</span></div>'
        if insert_after == i:
            pass
    return f'<div style="display:grid;grid-template-columns:repeat({cols}, minmax(0, 1fr));gap:2px;padding:8px 10px">{cells}</div>'

def page_panel(sel=(2, 3, 4), insert=None, width=288, count=10):
    acts = (("insert", "Chèn ▾"), ("trash", "Xóa"), ("rotl", "Xoay trái"), ("rotr", "Xoay phải"), ("extract", "Trích xuất"))
    a = ''.join(f'<button class="pact" aria-label="{l}">{ic(i, 19)}<span>{l}</span></button>' for i, l in acts)
    n = len(sel)
    selbar = (f'<div style="display:flex;align-items:center;gap:8px;padding:0 14px;height:32px;background:var(--sel);color:var(--accent);font-size:12px;font-weight:600;flex-shrink:0">'
              f'Đã chọn {n} trang<div style="flex:1 1 auto"></div><span style="font-weight:400;color:var(--muted)">Ctrl+C sao chép · Ctrl+V dán</span></div>') if n else ""
    cells = ""
    for i in range(count):
        s = " sel" if i in sel else ""
        cells += f'<div class="thumb{s}" style="position:relative">{paper(i, 112, 80)}<span class="num">{i + 1}</span>'
        if insert is not None and i == insert:
            cells += (f'<div style="position:absolute;top:6px;bottom:6px;right:-3px;width:4px;border-radius:2px;background:var(--accent);z-index:3"></div>')
        cells += '</div>'
    grid = f'<div style="display:grid;grid-template-columns:repeat(2, minmax(0, 1fr));gap:2px;padding:8px 10px;overflow:hidden;flex:1 1 auto;align-content:start">{cells}</div>'
    return (f'<div style="width:{width}px;flex-shrink:0;display:flex;flex-direction:column;background:var(--surface);border-right:1px solid var(--border)">'
            f'<div class="hdr"><span>Trang</span><span class="count">281</span></div>'
            f'<div style="display:flex;gap:2px;padding:0 10px 8px;border-bottom:1px solid var(--border);flex-shrink:0">{a}</div>{selbar}{grid}</div>')

def layer_panel(width=300):
    def row(depth, name, state, chip="", open_=None):
        pad = 10 + depth * 18
        cb = {"on": f'<span class="cb on">{ic("check", 12, 3)}</span>', "off": '<span class="cb"></span>',
              "mixed": f'<span class="cb mixed">{ic("min", 12, 3)}</span>'}[state]
        arrow = ""
        if open_ is not None:
            arrow = ic("chevd" if open_ else "chevr", 14, 2)
        else:
            arrow = '<span style="width:14px"></span>'
        ch = f'<span class="lbl" style="margin-left:auto;background:var(--chip);border-radius:8px;padding:1px 7px">{chip}</span>' if chip else ""
        col = "var(--text)" if state != "off" else "var(--muted)"
        return f'<div class="row" style="padding-left:{pad}px;color:{col}">{arrow}{cb}<span style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{name}</span>{ch}</div>'
    rows = (row(0, "1. Bản đồ nền", "mixed", "", True) + row(1, "1.1 Địa hình (đường đồng mức)", "on") + row(1, "1.2 Ranh giới, cọc mốc", "on", "3 file") +
            row(1, "1.3 Ảnh nền vệ tinh", "off") +
            row(0, "2. Giao thông", "on", "", True) + row(1, "2.1 Tim tuyến đường", "on", "3 file") + row(1, "2.2 Mép đường / Lề đường", "on") +
            row(1, "2.3 Mặt đường", "on") + row(1, "2.4 Biển báo, an toàn giao thông", "on") +
            row(0, "3. Thoát nước", "mixed", "", True) + row(1, "3.1 Cống thoát nước", "on", "2 file") + row(1, "3.2 Hố ga, ga thu nước", "on") + row(1, "3.3 Hướng thoát nước", "off") +
            row(0, "4. Hạ tầng kỹ thuật", "on", "", False) + row(0, "5. Ghi chú, kích thước", "on", "", False) +
            row(0, "6. Chú thích khác", "off", "", False))
    return (f'<div style="width:{width}px;flex-shrink:0;display:flex;flex-direction:column;background:var(--surface);border-right:1px solid var(--border)">'
            f'<div class="hdr"><span>Layer</span><span class="count">46</span></div>'
            f'<div style="padding:0 12px 10px;display:flex;gap:8px"><div class="field" style="flex:1 1 auto">{ic("search", 16)}<span>Tìm layer…</span></div>'
            f'<button class="ghost" aria-label="Mở rộng tất cả" style="width:34px;padding:0;justify-content:center">{ic("chevd", 16)}</button></div>'
            f'<div style="display:flex;gap:8px;padding:0 12px 8px"><button class="ghost" style="font-size:12px">Bật tất cả</button><button class="ghost" style="font-size:12px">Tắt tất cả</button></div>'
            f'<div style="flex:1 1 auto;overflow:hidden;padding:0 4px;border-top:1px solid var(--border)">{rows}</div>'
            f'<div style="padding:10px 14px;border-top:1px solid var(--border);color:var(--muted);font-size:11.5px;line-height:1.5">Layer cùng tên từ nhiều file được gộp thành một. Nhãn “3 file” cho biết layer có ở 3 file nguồn.</div></div>')

def shell(body, theme, w, h, accent, mid_title="", merge=True, tab_kwargs=None, dark=False, is_layers=False):
    tk = tab_kwargs or {}
    return f'''<div class="win" style="width:{w}px;height:{h}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(theme)}">
{titlebar()}{toolbar(merge)}{tabs(**tk)}<div style="flex:1 1 auto;min-height:0;display:flex">{body}</div></div>'''

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

W, H = 1280, 820

# 1 + 2: cửa sổ chính, panel Trang
def main_body(theme):
    return rail("pages") + page_panel(sel=(2, 3, 4)) + viewer((0, 1))

def main_win(theme, accent):
    inner = f'''<div class="win" style="width:{W}px;height:{H}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(theme)}">
{titlebar()}{toolbar()}{tabs()}<div style="flex:1 1 auto;min-height:0;display:flex">{main_body(theme)}</div>{statusbar()}</div>'''
    return inner

write("Main.dc.html", doc("XT PDF Reader — cửa sổ chính (sáng)", main_win(LIGHT, LIGHT["accent"]), W, H, LIGHT["accent"]))
write("MainDark.dc.html", doc("XT PDF Reader — cửa sổ chính (tối)", main_win(DARK, DARK["accent"]), W, H, DARK["accent"]))

# 3: Layer
inner = f'''<div class="win" style="width:{W}px;height:{H}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(LIGHT)}">
{titlebar()}{toolbar()}{tabs()}<div style="flex:1 1 auto;min-height:0;display:flex">{rail("layers")}{layer_panel()}{viewer((0, 3))}</div>{statusbar()}</div>'''
write("Layers.dc.html", doc("XT PDF Reader — panel Layer", inner, W, H, LIGHT["accent"]))

# 4: Cài đặt
def setting_row(label, hint, control):
    return (f'<div style="display:flex;align-items:center;gap:24px;padding:14px 0;border-bottom:1px solid var(--border)">'
            f'<div style="flex:1 1 auto;min-width:0"><div style="font-size:13px">{label}</div><div style="color:var(--muted);font-size:12px;margin-top:3px;line-height:1.45">{hint}</div></div>{control}</div>')
def seg(items, active):
    o = "".join(f'<div style="height:30px;padding:0 14px;display:flex;align-items:center;border-radius:5px;{"background:var(--accent);color:#ffffff;font-weight:600" if i == active else "color:var(--muted)"}">{t}</div>' for i, t in enumerate(items))
    return f'<div style="display:flex;gap:2px;padding:2px;border-radius:7px;background:var(--chip);flex-shrink:0">{o}</div>'
def sel(text, w=170):
    return f'<div class="field" style="width:{w}px;justify-content:space-between;color:var(--text);flex-shrink:0">{text}{ic("chevd", 15, 2)}</div>'
def toggle(on=True):
    bg = "var(--accent)" if on else "var(--border)"; left = "18px" if on else "2px"
    return f'<div style="width:38px;height:22px;border-radius:11px;background:{bg};position:relative;flex-shrink:0"><div style="position:absolute;top:2px;left:{left};width:18px;height:18px;border-radius:50%;background:#ffffff"></div></div>'
def swatches():
    cols = ["#2563eb", "#0f8b6d", "#7c4dff", "#d9463b"]
    return '<div style="display:flex;gap:10px;flex-shrink:0">' + ''.join(
        f'<div style="width:26px;height:26px;border-radius:50%;background:{c};{"outline:2px solid var(--text);outline-offset:2px" if i == 0 else ""}"></div>' for i, c in enumerate(cols)) + '</div>'

nav = ""
for i, t in enumerate(("Giao diện", "Hiển thị", "Hiệu năng và bộ nhớ", "Tích hợp")):
    nav += f'<div class="row" style="height:36px;{"background:var(--sel);color:var(--accent);font-weight:600" if i == 0 else "color:var(--text)"}">{t}</div>'
sec = lambda t: f'<div style="font-size:15px;font-weight:600;margin:22px 0 2px">{t}</div>'
content = (f'<div style="font-size:20px;font-weight:600;margin-top:6px">Cài đặt</div>'
           + sec("Giao diện")
           + setting_row("Chủ đề", "Sáng là mặc định. Tối dịu hơn khi xem bản vẽ nền đen.", seg(("Sáng", "Tối", "Theo hệ thống"), 0))
           + setting_row("Màu nhấn", "Dùng cho nút chính, mục đang chọn và vạch chèn.", swatches())
           + sec("Hiển thị")
           + setting_row("Chế độ xem mặc định", "Áp dụng cho file mở lần đầu.", sel("Cuộn liên tục"))
           + setting_row("Thu phóng khi mở file", "", sel("Vừa chiều rộng"))
           + sec("Hiệu năng và bộ nhớ")
           + setting_row("Số file giữ tài liệu “ấm”", "File dùng gần nhất giữ bộ nhớ để cuộn và zoom nhanh. File cũ hơn được giải phóng, xem lại vẫn nhanh nhờ bộ đệm ổ đĩa.", sel("2 file", 110))
           + setting_row("Bộ đệm file trên ổ đĩa", "File mở qua mạng được đệm vào thư mục tạm, tự xóa khi đóng file. Đang dùng 852 MB.", f'<button class="ghost" style="flex-shrink:0">Xóa bộ đệm</button>')
           + sec("Tích hợp")
           + setting_row("Dùng ứng dụng này khi nhấn “View PDF” trong pdfFactory", "", toggle(True)))
setting_body = (f'<div style="width:236px;flex-shrink:0;background:var(--panel);border-right:1px solid var(--border);padding:16px 12px;display:flex;flex-direction:column;gap:2px">{nav}</div>'
                f'<div style="flex:1 1 auto;min-width:0;background:var(--surface);overflow:hidden;padding:20px 48px"><div style="max-width:760px">{content}</div></div>')
inner = f'''<div class="win" style="width:{W}px;height:{H}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(LIGHT)}">
{titlebar()}{toolbar()}{tabs()}<div style="flex:1 1 auto;min-height:0;display:flex">{rail("settings")}{setting_body}</div>{statusbar(page="", total="281")}</div>'''
write("Settings.dc.html", doc("XT PDF Reader — cài đặt", inner, W, H, LIGHT["accent"]))

# 5: Panel Trang — chuột phải, kéo qua tab, vạch chèn
PW, PH = 780, 660
def menu_items():
    def mi(icn, t, k="", hot=False, danger=False):
        return f'<div class="mi{" hot" if hot else ""}{" danger" if danger else ""}">{ic(icn, 16)}<span>{t}</span><span class="k">{k}</span></div>'
    return (mi("copy", "Sao chép", "Ctrl+C") + mi("cut", "Cắt", "Ctrl+X") + mi("paste", "Dán vào sau", "Ctrl+V") + mi("paste", "Dán vào trước", "Ctrl+Shift+V") +
            '<div class="msep"></div>' +
            f'<div class="mi hot">{ic("goto", 16)}<span>Di chuyển</span><span class="k">{ic("chevr", 14, 2)}</span></div>' +
            mi("dup", "Nhân đôi", "Ctrl+D") + mi("rotl", "Xoay trái", "Ctrl+L") + mi("rotr", "Xoay phải", "Ctrl+R") + mi("extract", "Trích xuất…") +
            mi("tray", "Gửi vào khay tạm (cửa sổ Ghép)") +
            '<div class="msep"></div>' + mi("trash", "Xóa 3 trang", "Del", danger=True))
def submenu_items():
    def mi(icn, t, k=""):
        return f'<div class="mi">{ic(icn, 16)}<span>{t}</span><span class="k">{k}</span></div>'
    return (mi("top", "Lên đầu file", "Ctrl+Shift+Home") + mi("up", "Lên 1 vị trí", "Alt+↑") + mi("down", "Xuống 1 vị trí", "Alt+↓") +
            mi("bottom", "Xuống cuối file", "Ctrl+Shift+End") + '<div class="msep"></div>' + mi("goto", "Đến vị trí số…"))
ghost = (f'<div style="position:absolute;left:150px;top:300px;z-index:6;display:flex;flex-direction:column;align-items:flex-start;pointer-events:none">'
         f'<div style="position:relative;width:118px;height:88px"><span class="paper" style="position:absolute;left:8px;top:8px;opacity:.7">{cad(2)}</span>'
         f'<span class="paper" style="position:absolute;left:4px;top:4px;opacity:.85">{cad(1)}</span><span class="paper" style="position:absolute;left:0;top:0;box-shadow:0 6px 16px rgba(0,0,0,.3)">{cad(0)}</span>'
         f'<div style="position:absolute;right:-8px;top:-8px;min-width:24px;height:24px;border-radius:12px;background:var(--accent);color:#ffffff;font-weight:600;display:flex;align-items:center;justify-content:center">3</div></div></div>')
panel = page_panel(sel=(2, 3, 4), insert=6, width=288, count=8)
inner = f'''<div class="win" style="width:{PW}px;height:{PH}px;display:flex;flex-direction:column;overflow:hidden;position:relative;--accent:{{{{accent}}}};{tok(LIGHT)}">
{tabs(active=0, hover=1)}
<div style="flex:1 1 auto;min-height:0;display:flex;position:relative">{rail("pages")}{panel}
<div style="flex:1 1 auto;background:var(--view)"></div>
<div style="position:absolute;left:246px;top:150px;z-index:8;display:flex;gap:6px;align-items:flex-start"><div class="menu" style="width:322px">{menu_items()}</div>
<div class="menu" style="width:262px;margin-top:150px">{submenu_items()}</div></div>
{ghost}</div></div>'''
write("PageOps.dc.html", doc("Panel Trang — chuột phải và kéo thả", inner, PW, PH, LIGHT["accent"]))

# 6: Cửa sổ Ghép file
def src_card(name, pages, open_=False, v=0):
    tiles = ""
    if open_:
        tiles = '<div style="display:grid;grid-template-columns:repeat(3, minmax(0, 1fr));gap:6px;padding:8px 10px 10px">' + ''.join(
            f'<span class="paper" style="width:100%;height:auto;aspect-ratio:1.4">{cad(v + i, 100, 71)}</span>' for i in range(6)) + '</div>'
    return (f'<div style="border:1px solid var(--border);border-radius:8px;background:var(--surface);margin:0 10px 8px;overflow:hidden">'
            f'<div style="display:flex;align-items:center;gap:8px;height:40px;padding:0 10px">{ic("chevd" if open_ else "chevr", 15, 2)}{ic("file", 16)}'
            f'<span style="flex:1 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">{name}</span><span class="count">{pages}</span></div>{tiles}</div>')
sources = (f'<div style="width:272px;flex-shrink:0;display:flex;flex-direction:column;background:var(--panel);border-right:1px solid var(--border)">'
           f'<div class="hdr"><span>Nguồn</span><button class="ghost" style="height:26px;font-size:12px">{ic("plus", 14, 2)}Thêm file</button></div>'
           f'{src_card("02. QUYEN 2.1 - GT.pdf", 158, True, 1)}{src_card("03. QUYEN 2.2 - TCTC.pdf", 281)}{src_card("Tập 3 - Bản vẽ cơ sở.pdf", 120)}'
           f'<div style="margin:0 10px;color:var(--muted);font-size:11.5px;line-height:1.5">Kéo trang hoặc cả file sang vùng kết quả.</div></div>')
cells = ""
for i in range(15):
    s = " sel" if i in (11, 12) else ""
    mark = ""
    if i == 8:
        mark = '<div style="position:absolute;top:6px;bottom:6px;left:-4px;width:4px;border-radius:2px;background:var(--accent);z-index:3"></div>'
    cells += f'<div class="thumb{s}">{mark}{paper(i, 132, 94)}<span class="num">{i + 1}</span></div>'
ghost2 = (f'<div style="position:absolute;left:452px;top:262px;z-index:6;width:150px;height:110px;pointer-events:none">'
          f'<span class="paper" style="position:absolute;left:10px;top:10px;opacity:.7">{cad(3, 132, 94)}</span><span class="paper" style="position:absolute;left:5px;top:5px;opacity:.85">{cad(2, 132, 94)}</span>'
          f'<span class="paper" style="position:absolute;left:0;top:0;box-shadow:0 8px 20px rgba(0,0,0,.3)">{cad(1, 132, 94)}</span>'
          f'<div style="position:absolute;right:2px;top:-6px;min-width:26px;height:26px;border-radius:13px;background:var(--accent);color:#ffffff;font-weight:600;display:flex;align-items:center;justify-content:center">4</div></div>')
result = (f'<div style="flex:1 1 auto;min-width:0;display:flex;flex-direction:column;background:var(--surface);position:relative">'
          f'<div style="display:flex;align-items:center;gap:10px;height:46px;padding:0 16px;border-bottom:1px solid var(--border);flex-shrink:0">'
          f'<span style="font-weight:600;font-size:14px">Bộ hồ sơ đầy đủ.pdf</span><span class="count">128 trang</span><div style="flex:1 1 auto"></div>'
          f'<button class="ghost">{ic("save", 15)}Lưu</button><button class="ghost" aria-label="Xóa nhóm">{ic("trash", 15)}</button></div>'
          f'<div style="flex:1 1 auto;overflow:hidden;position:relative"><div style="display:grid;grid-template-columns:repeat(5, minmax(0, 1fr));gap:4px;padding:12px 16px">{cells}</div>{ghost2}'
          f'<div style="position:absolute;right:0;top:0;bottom:0;width:16px;background:var(--panel);border-left:1px solid var(--border)"><div style="margin:6px 3px;height:70px;border-radius:5px;background:var(--border)"></div></div></div></div>')
def tray_group(name, n, vs, primary=False):
    th = ''.join(f'<span class="paper" style="width:76px;height:54px;flex-shrink:0">{cad(v, 76, 54)}</span>' for v in vs)
    return (f'<div style="display:flex;align-items:center;gap:10px;padding:8px 10px;border:1.5px dashed var(--accent);border-radius:8px;background:var(--sel);flex-shrink:0">'
            f'<span style="color:var(--accent);cursor:grab" title="Kéo cả nhóm">{ic("grip", 18)}</span>'
            f'<div style="display:flex;flex-direction:column;gap:6px"><div style="display:flex;align-items:center;gap:8px"><span style="font-weight:600">{name}</span><span class="lbl">{n} trang</span>'
            f'<div style="flex:1 1 auto"></div><button class="chipx" style="height:22px;font-size:11.5px">Lưu thành file</button></div><div style="display:flex;gap:6px">{th}</div></div></div>')
tray = (f'<div style="height:150px;flex-shrink:0;display:flex;flex-direction:column;background:var(--panel);border-top:1px solid var(--border)">'
        f'<div style="display:flex;align-items:center;gap:8px;height:34px;padding:0 14px;flex-shrink:0">{ic("tray", 17)}<span style="font-weight:600">Khay tạm</span>'
        f'<span class="lbl" style="margin-left:8px">Kéo trang ra vùng trống để tạo nhóm tạm · kéo cả nhóm thả vào vạch chèn</span></div>'
        f'<div style="display:flex;gap:12px;padding:0 14px 12px;overflow:hidden;align-items:stretch">{tray_group("Nhóm tạm 1", 4, (1, 2, 3, 0))}{tray_group("Nhóm tạm 2", 2, (3, 1))}'
        f'<div style="flex:1 1 auto;min-width:120px;border:1.5px dashed var(--border);border-radius:8px;display:flex;align-items:center;justify-content:center;color:var(--muted)">Thả trang vào đây</div></div></div>')
mtool = (f'<div style="height:52px;flex-shrink:0;display:flex;align-items:center;gap:8px;padding:0 12px;background:var(--surface);border-bottom:1px solid var(--border)">'
         f'{tb("undo", "Hoàn tác")}{tb("redo", "Làm lại")}<div class="sep"></div>'
         f'<div style="display:flex;align-items:center;gap:8px;color:var(--muted)">{ic("pages", 16)}Cỡ trang<div style="width:110px;height:4px;border-radius:2px;background:var(--border);position:relative"><div style="position:absolute;left:46px;top:-5px;width:14px;height:14px;border-radius:50%;background:var(--accent)"></div></div></div>'
         f'<div class="sep"></div><span class="chipx"><span class="cb on" style="width:14px;height:14px">{ic("check", 10, 3)}</span>Gộp layer cùng tên</span>'
         f'<div style="flex:1 1 auto"></div><button class="primary">{ic("save", 16, 1.9)}Lưu tất cả</button></div>')
merge_status = (f'<div class="status"><span>Kết quả: 128 trang · 3 nguồn · 46 layer (đã gộp theo tên)</span><div style="flex:1 1 auto"></div>'
                f'<span>Đã chọn 2 trang · Kéo để chèn · Chuột phải: Di chuyển, Nhân đôi, Xóa</span></div>')
inner = f'''<div class="win" style="width:{W}px;height:{H}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(LIGHT)}">
{titlebar("XT PDF Reader", "Ghép file")}{mtool}<div style="flex:1 1 auto;min-height:0;display:flex">{sources}{result}</div>{tray}{merge_status}</div>'''
write("Merge.dc.html", doc("XT PDF Reader — cửa sổ Ghép file", inner, W, H, LIGHT["accent"]))

# canvas.json
canvas = {
 "v": 3, "createdOnFiles": {"v": 1, "at": "2026-09-27T21:00:00Z"},
 "title": "XT PDF Reader — thiết kế UI thống nhất", "launch": {"view": "canvas"}, "pages": [],
 "boards": {
  "Main.dc.html": {"x": 0, "y": 0, "w": W, "h": H, "title": "1. Cửa sổ chính — sáng (mặc định), panel Trang"},
  "MainDark.dc.html": {"x": 1360, "y": 0, "w": W, "h": H, "title": "2. Cửa sổ chính — tối (cùng bảng token)"},
  "Layers.dc.html": {"x": 0, "y": 900, "w": W, "h": H, "title": "3. Panel Layer"},
  "Settings.dc.html": {"x": 1360, "y": 900, "w": W, "h": H, "title": "4. Cài đặt"},
  "PageOps.dc.html": {"x": 0, "y": 1800, "w": PW, "h": PH, "title": "5. Panel Trang — chuột phải, kéo sang tab, vạch chèn"},
  "Merge.dc.html": {"x": 860, "y": 1800, "w": W, "h": H, "title": "6. Cửa sổ Ghép file — khay tạm ghim đáy"},
 },
 "order": ["Main.dc.html", "MainDark.dc.html", "Layers.dc.html", "Settings.dc.html", "PageOps.dc.html", "Merge.dc.html"],
 "notes": {}, "designSystems": []
}
with open(os.path.join(PROJ, "canvas.json"), "w", encoding="utf-8", newline="\n") as f:
    json.dump(canvas, f, ensure_ascii=False, indent=2)
print("ok", sorted(os.listdir(PROJ)))
