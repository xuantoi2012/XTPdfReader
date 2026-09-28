# -*- coding: utf-8 -*-
"""Bộ UI v3: thêm màn hình cho các tính năng mới (Find, Measure, Compare, Print, Export, Layer presets,
Comments, Stamps, Start screen, thông báo + command palette, tùy chọn lưu khi ghép)."""
import os, json
HERE = os.path.dirname(os.path.abspath(__file__))
G = {"__file__": os.path.join(HERE, "gen_ui2.py")}
exec(compile(open(G["__file__"], encoding="utf-8").read(), "gen_ui2", "exec"), G)
globals().update(G)
LANG = "en"; G["LANG"] = "en"

ORANGE = "#d9640a"   # removed / measure accent (khác sáng-tối so với xanh, không dựa vào đỏ/xanh lá)
BLUE = "#2563eb"

# ── helpers ─────────────────────────────────────────────────────────────────
def viewer_wrap(content, extra=""):
    return (f'<div style="flex:1 1 auto;min-width:0;background:var(--view);overflow:hidden;display:flex;flex-direction:column;align-items:center;gap:16px;padding:22px 0;position:relative">{content}{extra}'
            f'<div style="position:absolute;right:0;top:0;bottom:0;width:12px;background:var(--panel);border-left:1px solid var(--border)"><div style="margin:8px 2px;height:120px;border-radius:4px;background:var(--border)"></div></div></div>')
def page_box(v, overlay="", w=560, h=396, opacity=1):
    return (f'<div style="position:relative;width:{w}px;height:{h}px;background:#ffffff;box-shadow:0 2px 10px rgba(0,0,0,0.25);flex-shrink:0">'
            f'<div style="opacity:{opacity}">{cad(v, w, h, 0.5)}</div>{overlay}</div>')
def checkbox(label, on=True, hint=""):
    box = f'<span class="cb on">{ic("check", 12, 3)}</span>' if on else '<span class="cb"></span>'
    h = f'<div class="lbl" style="margin-top:2px;line-height:1.4">{hint}</div>' if hint else ""
    return f'<div style="display:flex;gap:10px;align-items:flex-start;padding:5px 0">{box}<div><div>{label}</div>{h}</div></div>'
def radio(label, on=False, hint=""):
    dot = ('<span style="width:16px;height:16px;border-radius:50%;border:5px solid var(--accent);background:#ffffff;flex-shrink:0;margin-top:1px"></span>' if on
           else '<span style="width:16px;height:16px;border-radius:50%;border:1.5px solid var(--muted);flex-shrink:0;margin-top:1px"></span>')
    h = f'<div class="lbl" style="margin-top:2px;line-height:1.4">{hint}</div>' if hint else ""
    return f'<div style="display:flex;gap:10px;align-items:flex-start;padding:5px 0">{dot}<div><div>{label}</div>{h}</div></div>'
def field(text, w=None, right=""):
    ws = f"width:{w}px;" if w else "flex:1 1 auto;"
    return f'<div class="field" style="{ws}justify-content:space-between;color:var(--text)"><span>{text}</span>{right}</div>'
def labeled(label, control, gap=6):
    return f'<div style="display:flex;flex-direction:column;gap:{gap}px"><div class="lbl" style="font-size:12px">{label}</div>{control}</div>'
def dlg(title, body, w, h, footer):
    return (f'<div class="win" style="width:{w}px;height:{h}px;display:flex;flex-direction:column;background:var(--surface);border:1px solid var(--border);border-radius:10px;overflow:hidden;--accent:{{{{accent}}}};{tok(LIGHT)}">'
            f'<div style="height:52px;flex-shrink:0;display:flex;align-items:center;padding:0 12px 0 20px;border-bottom:1px solid var(--border);font-size:15px;font-weight:600">{title}<div style="flex:1 1 auto"></div>'
            f'<button class="sbtn" aria-label="Close">{ic("close", 16, 1.9)}</button></div>'
            f'<div style="flex:1 1 auto;min-height:0;display:flex">{body}</div>'
            f'<div style="height:64px;flex-shrink:0;display:flex;align-items:center;gap:10px;padding:0 20px;background:var(--panel);border-top:1px solid var(--border)">{footer}</div></div>')
def btn_ghost(t_): return f'<button class="ghost">{t_}</button>'
def btn_primary(t_, icn=None): return f'<button class="primary">{ic(icn, 16, 1.9) if icn else ""}{t_}</button>'
def panel_shell(title, count, body, width=320, footer=""):
    c = f'<span class="count">{count}</span>' if count != "" else ""
    return (f'<div style="width:{width}px;flex-shrink:0;display:flex;flex-direction:column;background:var(--surface);border-right:1px solid var(--border)">'
            f'<div class="hdr"><span>{title}</span>{c}</div>{body}{footer}</div>')
def out(name, title, inner, w, h):
    write(name, doc(title, inner, w, h, LIGHT["accent"], lang="en"))

def over(win, overlay):
    win = win.replace('<div class="win" style="', '<div class="win" style="position:relative;', 1)
    return win[:-6] + overlay + "</div>"

boards = {}
def board(name, x, y, w, h, title):
    boards[name] = {"x": x, "y": y, "w": w, "h": h, "title": title}

# ───────────────────────────── FIND ─────────────────────────────
def mark(t_, on=False):
    st = "background:rgba(255,196,0,.55);border-radius:2px;padding:0 1px;"
    if on: st += "outline:2px solid var(--accent);"
    return f'<span style="{st}">{t_}</span>'
def res_group(page, n, lines):
    rows = ''.join(f'<div class="row" style="height:auto;min-height:34px;align-items:flex-start;padding:6px 10px;line-height:1.45;{"background:var(--sel)" if hot else ""}"><span style="color:var(--muted);width:14px;flex-shrink:0">{ic("chevr", 12, 2)}</span><span>{txt}</span></div>' for txt, hot in lines)
    return (f'<div style="padding:8px 4px 0"><div style="display:flex;align-items:center;gap:8px;padding:0 10px;height:26px;font-weight:600;font-size:12px;color:var(--muted)">'
            f'Page {page}<span class="count">{n}</span></div>{rows}</div>')
find_body = (
    f'<div style="padding:0 12px 10px;display:flex;flex-direction:column;gap:10px">'
    f'<div class="field" style="background:var(--surface);color:var(--text)">{ic("search", 16)}<span style="flex:1 1 auto">manhole</span><span style="color:var(--muted)">{ic("close", 14, 2)}</span></div>'
    f'<div style="display:flex;gap:8px"><span class="chipx"><span class="cb" style="width:14px;height:14px"></span>Match case</span><span class="chipx"><span class="cb on" style="width:14px;height:14px">{ic("check", 10, 3)}</span>Whole word</span></div>'
    f'{seg(("This file", "All open files (3)"), 0)}</div>'
    f'<div style="padding:0 14px 8px;color:var(--muted);font-size:12px">24 results on 9 pages</div>'
    f'<div style="margin:0 12px 8px;display:flex;gap:10px;padding:10px 12px;border-radius:8px;background:var(--sel);color:var(--text);font-size:12px;line-height:1.5">'
    f'<span style="color:var(--accent);margin-top:1px">{ic("info", 16)}</span><span>128 of 281 pages have no searchable text (their text is drawn as lines). Results cover the other 153 pages.</span></div>'
    f'<div style="flex:1 1 auto;overflow:hidden;border-top:1px solid var(--border)">'
    + res_group(12, 3, [(f"…install {mark('manhole', True)} MH-04 at km 0+320…", True), (f"…{mark('manhole')} cover D600, class D400…", False), (f"…adjacent {mark('manhole')} MH-05…", False)])
    + res_group(13, 2, [(f"…{mark('manhole')} schedule, see sheet 41…", False), (f"…precast {mark('manhole')} base slab…", False)])
    + res_group(41, 6, [(f"…{mark('Manhole')} MH-01 to MH-06 table…", False), (f"…top level of {mark('manhole')} MH-02…", False)])
    + '</div>')
hl = ''.join(f'<div style="position:absolute;left:{x}px;top:{y}px;width:{w}px;height:{h_}px;background:rgba(255,196,0,.5);border-radius:2px;{"outline:2px solid var(--accent);" if on else ""}"></div>'
             for x, y, w, h_, on in ((118, 84, 52, 11, True), (302, 130, 48, 11, False), (206, 208, 50, 11, False), (394, 262, 46, 11, False)))
findbar = (f'<div style="position:absolute;right:26px;top:14px;z-index:5;display:flex;align-items:center;gap:6px;padding:6px 8px 6px 12px;background:var(--surface);border:1px solid var(--border);border-radius:8px;box-shadow:var(--shadow)">'
           f'<span style="min-width:96px">manhole</span><span style="color:var(--muted);font-size:12px;padding:0 6px">1 / 24</span>'
           f'<button class="sbtn" aria-label="Previous match">{ic("up2", 15, 2)}</button><button class="sbtn" aria-label="Next match">{ic("down2", 15, 2)}</button><button class="sbtn" aria-label="Close find">{ic("close", 14, 2)}</button></div>')
out("Find.dc.html", "Find in document", window(rail("search") + panel_shell("Find", "", find_body, 340) + viewer_wrap(page_box(2, hl), findbar), LIGHT, active="find"), W, H)
board("Find.dc.html", 0, 2700, W, H, "8. Find — panel, results, on-page highlights")

# ───────────────────────────── COMMENTS ─────────────────────────────
def crd(icn, who, date, text, status, on=False, color="#d97706"):
    st = (f'<span class="chipx" style="height:20px;font-size:11px;background:var(--sel);color:var(--accent)">Open</span>' if status == "Open"
          else f'<span class="chipx" style="height:20px;font-size:11px">Resolved</span>')
    return (f'<div style="margin:4px 8px;padding:10px 12px;border-radius:8px;border:1.5px solid {"var(--accent)" if on else "var(--border)"};background:{"var(--sel)" if on else "var(--surface)"};display:flex;flex-direction:column;gap:6px">'
            f'<div style="display:flex;align-items:center;gap:8px"><span style="color:{color}">{ic(icn, 16)}</span><span style="font-weight:600">{who}</span><span class="lbl">{date}</span><div style="flex:1 1 auto"></div>{st}</div>'
            f'<div style="line-height:1.45">{text}</div></div>')
cm_body = (f'<div style="padding:0 12px 10px;display:flex;flex-direction:column;gap:10px">{seg(("All 14", "Open 9", "Resolved 5"), 1)}'
           f'<div style="display:flex;gap:8px">{field("All types", None, ic("chevd", 15, 2))}{field("All authors", None, ic("chevd", 15, 2))}</div></div>'
           f'<div style="flex:1 1 auto;overflow:hidden;border-top:1px solid var(--border);padding-top:4px">'
           f'<div class="lbl" style="padding:6px 14px 2px;font-weight:600">Page 3</div>'
           + crd("comment", "Tran Xuan Toi", "28 Sep", "Check culvert diameter against the hydraulic table.", "Open", True)
           + crd("hl", "Le Minh", "27 Sep", "Highlighted: manhole spacing exceeds 50 m.", "Open", False, "#ca8a04")
           + '<div class="lbl" style="padding:8px 14px 2px;font-weight:600">Page 12</div>'
           + crd("type", "Tran Xuan Toi", "26 Sep", "Revised per review comment 14.", "Resolved")
           + crd("comment", "Nguyen An", "26 Sep", "Missing dimension between MH-04 and MH-05.", "Open") + '</div>')
cm_foot = f'<div style="display:flex;gap:8px;padding:10px 12px;border-top:1px solid var(--border)"><button class="ghost" style="flex:1 1 auto;justify-content:center">{ic("export", 15)}Export summary…</button></div>'
co2 = (f'<div style="position:absolute;left:110px;top:120px;width:150px;height:40px;background:rgba(255,196,0,.35);border:1.5px solid var(--accent);border-radius:3px"></div>'
       f'<div style="position:absolute;left:236px;top:104px;width:26px;height:26px;border-radius:13px 13px 13px 3px;background:#d97706;color:#ffffff;display:flex;align-items:center;justify-content:center">{ic("comment", 14, 2)}</div>')
out("Comments.dc.html", "Comments panel", window(rail("comment") + panel_shell("Comments", 14, cm_body, 340, cm_foot) + viewer_wrap(page_box(2, co2)), LIGHT), W, H)
board("Comments.dc.html", 1360, 2700, W, H, "11. Comments — list, filters, export summary")

# ───────────────────────────── STAMPS ─────────────────────────────
def stamp(text, sub, color, w=104):
    return (f'<button style="display:flex;flex-direction:column;align-items:center;justify-content:center;gap:2px;width:{w}px;height:64px;border:2px solid {color};border-radius:6px;color:{color};'
            f'font-weight:700;letter-spacing:.03em;background:var(--surface)"><span style="font-size:12px">{text}</span><span style="font-size:9.5px;font-weight:500;letter-spacing:0">{sub}</span></button>')
pop = (f'<div style="position:absolute;left:690px;top:100px;z-index:12;width:400px;background:var(--surface);border:1px solid var(--border);border-radius:10px;box-shadow:var(--shadow);padding:14px;display:flex;flex-direction:column;gap:12px">'
       f'<div style="display:flex;align-items:center;font-weight:600;font-size:14px">Stamps<div style="flex:1 1 auto"></div>{seg(("Standard", "Mine"), 1)}</div>'
       f'<div style="display:grid;grid-template-columns:repeat(3, minmax(0, 1fr));gap:10px">'
       + stamp("APPROVED", "Tran Xuan Toi · 28/09/2026", "#0f8b6d") + stamp("REVIEWED", "28/09/2026", "#2563eb") + stamp("REJECTED", "28/09/2026", "#c0392b")
       + stamp("FOR INFO", "", "#6b7280") + stamp("DRAFT", "", "#d9640a") + stamp("ĐÃ THẨM ĐỊNH", "Ký bởi: T.X.Tới", "#c0392b")
       + f'</div><div style="display:flex;gap:8px"><button class="ghost" style="flex:1 1 auto;justify-content:center">{ic("plus", 15, 2)}New stamp…</button>'
       f'<button class="ghost" style="flex:1 1 auto;justify-content:center">{ic("folder", 15)}Import image…</button></div>'
       f'<div style="border-top:1px solid var(--border);padding-top:10px">{checkbox("Add my name and today’s date", True)}'
       f'<div style="display:flex;align-items:center;gap:10px;padding:5px 0"><span style="width:60px">Opacity</span><div style="flex:1 1 auto;height:4px;border-radius:2px;background:var(--border);position:relative"><div style="position:absolute;left:70%;top:-5px;width:14px;height:14px;border-radius:50%;background:var(--accent)"></div></div><span style="width:34px;text-align:right">85%</span></div>'
       f'<div class="lbl" style="margin-top:6px;line-height:1.5">Click on the page to place · hold Shift to repeat · right-click a stamp to place it on a page range</div></div></div>')
so = (f'<div style="position:absolute;left:330px;top:250px;transform:rotate(-8deg);border:3px solid #c0392b;border-radius:6px;color:#c0392b;font-weight:700;padding:6px 14px;text-align:center;background:rgba(255,255,255,.6)">'
      f'<div style="font-size:16px;letter-spacing:.04em">ĐÃ THẨM ĐỊNH</div><div style="font-size:10px;font-weight:500">Ký bởi: T.X.Tới · 28/09/2026</div></div>')
out("Stamps.dc.html", "Stamps", over(window(rail("pages") + page_panel() + viewer_wrap(page_box(1, so)), LIGHT, active="stamp"), pop), W, H)
board("Stamps.dc.html", 2720, 2700, W, H, "12. Stamps — library, custom stamps, placement")

# ───────────────────────────── NOTIFICATION + COMMAND PALETTE ─────────────────────────────
banner = (f'<div style="height:40px;flex-shrink:0;display:flex;align-items:center;gap:12px;padding:0 16px;background:color-mix(in srgb, #f5b301 22%, var(--surface));border-bottom:1px solid var(--border)">'
          f'<span style="color:#b45309">{ic("alert", 18, 2)}</span><span><b>This file was changed on disk</b> by another user (14:32). What you see may be out of date.</span>'
          f'<div style="flex:1 1 auto"></div><button class="ghost" style="height:26px">Reload</button><button class="ghost" style="height:26px">Compare with new version</button><button class="ghost" style="height:26px">Ignore</button></div>')
def cmd(icn, label, key="", hot=False, group=None):
    g = f'<div class="lbl" style="padding:8px 12px 2px;font-weight:600;letter-spacing:.04em;text-transform:uppercase">{group}</div>' if group else ""
    return (f'{g}<div class="mi{" hot" if hot else ""}" style="height:36px;margin:0 6px">{ic(icn, 17)}<span>{label}</span><span class="k">{key}</span></div>')
palette = (f'<div style="position:absolute;left:50%;transform:translateX(-50%);top:110px;z-index:20;width:620px;background:var(--surface);border:1px solid var(--border);border-radius:12px;box-shadow:0 20px 60px rgba(20,32,50,.35);overflow:hidden">'
           f'<div style="display:flex;align-items:center;gap:10px;height:52px;padding:0 16px;border-bottom:1px solid var(--border)">{ic("search", 18)}<span style="font-size:15px">merge</span><div style="flex:1 1 auto"></div><span class="chipx" style="height:22px;font-size:11px">Ctrl+K</span></div>'
           f'<div style="padding:4px 0 8px">' + cmd("merge", "Merge files…", "Ctrl+M", True, "Commands") + cmd("layers", "Merge same-name layers when saving", "") + cmd("merge", "Send selected pages to Merge window", "")
           + cmd("file", "02. QUYEN 2.1 - GT.pdf", "", False, "Open files") + cmd("file", "Volume 3 - Concept drawings.pdf", "")
           + f'</div><div style="display:flex;gap:16px;padding:8px 16px;border-top:1px solid var(--border);background:var(--panel);color:var(--muted);font-size:11.5px"><span>↑↓ navigate</span><span>Enter run</span><span>Esc close</span></div></div>')
notif_win = window(rail("pages") + page_panel() + viewer_wrap(page_box(0) + page_box(1)), LIGHT, tabs_html=tabs() + banner)
out("Notify.dc.html", "Notifications and command palette", over(notif_win, palette), W, H)
board("Notify.dc.html", 0, 3600, W, H, "13. File changed on disk + command palette (Ctrl+K)")

# ───────────────────────────── LAYER PRESETS ─────────────────────────────
def preset_row(name, sub, on=False):
    return (f'<div class="mi" style="height:44px;background:{"var(--sel)" if on else "transparent"}">{ic("check", 16, 2.4) if on else "<span style=\"width:16px\"></span>"}'
            f'<div style="display:flex;flex-direction:column;gap:2px"><span style="font-weight:{600 if on else 400}">{name}</span><span class="lbl">{sub}</span></div></div>')
pbar = (f'<div style="padding:0 12px 10px;display:flex;flex-direction:column;gap:8px"><div style="display:flex;gap:8px;align-items:center"><span class="lbl" style="font-size:12px;width:34px">View</span>'
        f'{field("Drainage plan", None, ic("chevd", 15, 2))}<button class="ghost" style="width:34px;padding:0;justify-content:center" aria-label="Save preset">{ic("save", 16)}</button></div>'
        f'<div style="display:flex;gap:8px"><button class="ghost" style="font-size:12px">{ic("eye", 14)}Isolate selected</button><button class="ghost" style="font-size:12px">Reset</button></div></div>')
lp = layer_panel(300)
lp = lp.replace('<span class="count">46</span></div>', '<span class="count">46</span></div>' + pbar, 1)
lp = lp.replace('</div></div>', '</div></div>', 1)
lp_foot = f'<div style="padding:10px 12px;border-top:1px solid var(--border)"><button class="primary" style="width:100%;justify-content:center">{ic("export", 16, 1.9)}Export PDF with this view…</button></div>'
lp = lp[:-6] + lp_foot + "</div>"
pmenu = (f'<div class="menu" style="position:absolute;left:96px;top:210px;z-index:12;width:300px">' + preset_row("All layers", "46 visible") + preset_row("Drainage plan", "12 visible · current", True)
         + preset_row("Print set A3", "31 visible · used by Export") + preset_row("Utilities only", "9 visible") + '<div class="msep"></div>'
         + f'<div class="mi">{ic("save", 16)}<span>Save current as new view…</span></div><div class="mi">{ic("settings", 16)}<span>Manage views…</span></div></div>')
out("LayerViews.dc.html", "Layer views", over(window(rail("layers") + lp + viewer_wrap(page_box(3)), LIGHT), pmenu), W, H)
board("LayerViews.dc.html", 2720, 900, W, H, "3b. Layers — saved views (presets) and export")

# ───────────────────────────── PRINT ─────────────────────────────
pr_left = (f'<div style="width:400px;flex-shrink:0;padding:18px 20px;display:flex;flex-direction:column;gap:14px;border-right:1px solid var(--border);overflow:hidden">'
           f'{labeled("Printer", field("HP DesignJet T1700 (A0 plotter)", None, ic("chevd", 15, 2)))}'
           f'<div><div class="lbl" style="font-size:12px;margin-bottom:4px">Pages</div>{radio("All pages (281)", True)}{radio("Current page (3)")}{radio("Range")}'
           f'<div style="margin-left:26px;margin-top:2px">{field("1-12, 40, 55-60", None)}</div></div>'
           f'<div style="display:flex;gap:10px">{labeled("Paper", field("A3 (297 × 420 mm)", 190, ic("chevd", 15, 2)))}{labeled("Copies", field("1", 60))}</div>'
           f'{labeled("Scale", seg(("Fit to paper", "Actual size", "Custom %"), 0))}'
           f'{labeled("Color", seg(("Color", "Grayscale", "Black lines"), 1))}'
           f'<div>{checkbox("Use current layer view", True, "Hidden layers are not printed (View: Drainage plan).")}{checkbox("Auto-rotate each page to fit the paper", True)}</div></div>')
pr_prev = (f'<div style="flex:1 1 auto;min-width:0;background:var(--view);display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;padding:20px">'
           f'<div style="position:relative;width:400px;height:283px;background:#ffffff;box-shadow:0 2px 12px rgba(0,0,0,.28)"><div style="position:absolute;inset:12px;border:1px dashed #b8c0cb"></div>{cad(0, 400, 283, 0.5)}</div>'
           f'<div style="display:flex;align-items:center;gap:8px;color:var(--muted)"><button class="sbtn">{ic("chevl", 15, 2)}</button>Page 1 of 281 · A3 landscape<button class="sbtn">{ic("chevr", 15, 2)}</button></div></div>')
out("Print.dc.html", "Print", dlg("Print", pr_left + pr_prev, 1000, 640, f'{btn_ghost("Page setup…")}<div style="flex:1 1 auto"></div>{btn_ghost("Cancel")}{btn_primary("Print", "print")}'), 1000, 640)
board("Print.dc.html", 0, 4500, 1000, 640, "14. Print — range, paper, scale, layer view")

# ───────────────────────────── EXPORT / SPLIT ─────────────────────────────
def size_row(name, dims, n, out_name, on=True):
    box = f'<span class="cb on">{ic("check", 12, 3)}</span>' if on else '<span class="cb"></span>'
    return (f'<div class="row" style="height:40px;border-bottom:1px solid var(--border);border-radius:0;gap:12px">{box}<span style="width:44px;font-weight:600">{name}</span><span class="lbl" style="width:110px">{dims}</span>'
            f'<span style="width:70px">{n} pages</span><span style="flex:1 1 auto;color:var(--muted)">{out_name}</span></div>')
ex_left = (f'<div style="width:400px;flex-shrink:0;padding:18px 20px;display:flex;flex-direction:column;gap:14px;border-right:1px solid var(--border);overflow:hidden">'
           f'{labeled("Layers", "")}<div style="margin-top:-8px">{radio("Keep all layers", False, "Result stays editable with the same layer list.")}{radio("Only the layers of the current view, flattened", True, "View: Print set A3 · smaller file, layers cannot be toggled later.")}</div>'
           f'{labeled("File size", seg(("Original", "Balanced", "Smallest"), 1))}<div class="lbl" style="margin-top:-6px">About 165 MB → 98 MB (images re-compressed, drawings stay vector).</div>'
           f'{labeled("Split into several files", field("By page size", None, ic("chevd", 15, 2)))}'
           f'{labeled("Save to", field("P:\\01-PIP\\032-Tinh lo 991 noi dai\\…\\PRINT\\", None, f"<span class=lbl>Browse…</span>"))}</div>')
ex_right = (f'<div style="flex:1 1 auto;min-width:0;padding:18px 20px;display:flex;flex-direction:column;gap:10px"><div style="display:flex;align-items:center"><span style="font-weight:600">Files that will be created</span><div style="flex:1 1 auto"></div><span class="lbl">Page sizes found in this document</span></div>'
            f'<div style="border:1px solid var(--border);border-radius:8px;overflow:hidden">'
            + size_row("A1", "594 × 841 mm", 120, "03. QUYEN 2.2 - A1.pdf") + size_row("A3", "297 × 420 mm", 158, "03. QUYEN 2.2 - A3.pdf") + size_row("A4", "210 × 297 mm", 3, "03. QUYEN 2.2 - A4.pdf", False) +
            f'</div><div class="lbl" style="line-height:1.5">Other split options: every N pages, at each top-level bookmark, or by a custom page range list. Unchecked sizes are skipped.</div></div>')
out("Export.dc.html", "Export and split", dlg("Export PDF", ex_left + ex_right, 1000, 640, f'<span class="lbl">2 files · about 61 MB</span><div style="flex:1 1 auto"></div>{btn_ghost("Cancel")}{btn_primary("Export", "export")}'), 1000, 640)
board("Export.dc.html", 1100, 4500, 1000, 640, "15. Export — flattened layer view, optimize size, split by page size")

# ───────────────────────────── START SCREEN ─────────────────────────────
def recent(name, path, meta, pinned=False, v=0):
    st = '<span style="color:var(--accent)">' + ic("star", 16, 2) + "</span>" if pinned else f'<span style="color:var(--border)">{ic("star", 16, 2)}</span>'
    return (f'<div style="display:flex;align-items:center;gap:14px;padding:10px 12px;border-radius:8px;border:1px solid var(--border);background:var(--surface)">{paper(v, 84, 60)}'
            f'<div style="flex:1 1 auto;min-width:0"><div style="font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{name}</div>'
            f'<div class="lbl" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:2px">{path}</div><div class="lbl" style="margin-top:2px">{meta}</div></div>{st}</div>')
def ws_card(name, meta, layout):
    return (f'<div style="width:220px;border:1px solid var(--border);border-radius:8px;padding:12px;background:var(--surface);display:flex;flex-direction:column;gap:8px">'
            f'<div style="display:flex;align-items:center;gap:8px;color:var(--accent)">{ic("workspace", 18)}<span style="font-weight:600;color:var(--text)">{name}</span></div><div class="lbl">{meta}</div>'
            f'<button class="ghost" style="justify-content:center">Restore</button></div>')
start_body = (f'<div style="flex:1 1 auto;min-width:0;background:var(--panel);display:flex;gap:40px;padding:36px 48px;overflow:hidden">'
              f'<div style="width:400px;flex-shrink:0;display:flex;flex-direction:column;gap:18px"><div style="font-size:22px;font-weight:600">XT PDF Reader</div>'
              f'<div style="display:flex;gap:10px"><button class="primary" style="height:38px">{ic("folder", 17, 1.9)}Open file…<span style="opacity:.75;font-weight:400;margin-left:6px">Ctrl+O</span></button><button class="ghost" style="height:38px">Open folder…</button></div>'
              f'<div style="height:150px;border:2px dashed var(--border);border-radius:12px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px;color:var(--muted)">{ic("file", 28)}<span>Drop PDF files here</span></div>'
              f'<div><div style="font-weight:600;margin-bottom:8px">Quick locations</div><div style="display:flex;flex-direction:column;gap:6px">'
              f'<div class="row" style="border:1px solid var(--border);background:var(--surface)">{ic("folder", 16)}<span style="flex:1 1 auto">P:\\ Projects</span><span class="lbl">network</span></div>'
              f'<div class="row" style="border:1px solid var(--border);background:var(--surface)">{ic("folder", 16)}<span style="flex:1 1 auto">Desktop</span></div>'
              f'<div class="row" style="border:1px solid var(--border);background:var(--surface)">{ic("folder", 16)}<span style="flex:1 1 auto">Documents</span></div></div></div></div>'
              f'<div style="flex:1 1 auto;min-width:0;display:flex;flex-direction:column;gap:12px"><div style="display:flex;align-items:center"><span style="font-weight:600;font-size:15px">Recent</span><div style="flex:1 1 auto"></div><button class="ghost" style="height:26px;font-size:12px">Clear list</button></div>'
              + recent("03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf", "P:\\01-PIP\\032-Tinh lo 991 noi dai\\…\\PDF", "2 hours ago · 165 MB · 282 pages", True, 0)
              + recent("02. QUYEN 2.1 - GT.pdf", "P:\\01-PIP\\032-Tinh lo 991 noi dai\\…\\PDF", "Yesterday · 98 MB · 158 pages", True, 1)
              + recent("Volume 3 - Concept drawings.pdf", "C:\\Users\\condu\\Desktop", "Sep 26 · 24 MB · 120 pages", False, 2)
              + f'<div style="font-weight:600;font-size:15px;margin-top:8px">Workspaces</div><div style="display:flex;gap:12px">{ws_card("Volume 2 review", "6 files · 2×2 layout", "")}{ws_card("Merge: full dossier", "3 files · 2 side by side", "")}</div></div></div>')
start_tabs = (f'<div style="height:36px;flex-shrink:0;display:flex;align-items:flex-end;gap:1px;padding:0 8px;background:var(--panel);border-bottom:1px solid var(--border)">'
              f'<div class="tab active">{ic("clock", 15)}<span>Start</span></div><button class="sbtn" style="margin:0 0 4px 4px" aria-label="Open another file">{ic("plus", 16)}</button></div>')
out("Start.dc.html", "Start screen", f'<div class="win" style="width:{W}px;height:{H}px;display:flex;flex-direction:column;overflow:hidden;--accent:{{{{accent}}}};{tok(LIGHT)}">{titlebar()}{start_tabs}<div style="flex:1 1 auto;min-height:0;display:flex">{start_body}</div></div>', W, H)
board("Start.dc.html", 2220, 4500, W, H, "16. Start screen — recent files, pinned, workspaces")

# ───────────────────────────── MERGE SAVE OPTIONS ─────────────────────────────
def src_row(name, n, rng):
    return (f'<div class="row" style="height:38px;border-bottom:1px solid var(--border);border-radius:0;gap:10px">{ic("file", 16)}<span style="flex:1 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">{name}</span>'
            f'<span style="width:74px;text-align:right">{n} pages</span><span class="lbl" style="width:76px;text-align:right">{rng}</span></div>')
def bm_row(depth, name, page, bold=False):
    return f'<div class="row" style="height:28px;padding-left:{8 + depth * 18}px;font-weight:{600 if bold else 400}">{ic("chevr", 13, 2) if bold else "<span style=\"width:13px\"></span>"}{ic("bookmark", 14)}<span style="flex:1 1 auto">{name}</span><span class="lbl">{page}</span></div>'
ms_left = (f'<div style="width:470px;flex-shrink:0;padding:18px 20px;display:flex;flex-direction:column;gap:12px;border-right:1px solid var(--border);overflow:hidden">'
           f'{labeled("File name", field("Complete dossier.pdf", None))}{labeled("Save to", field("P:\\01-PIP\\032-Tinh lo 991 noi dai\\…\\PDF\\", None, "<span class=lbl>Browse…</span>"))}'
           f'<div style="border:1px solid var(--border);border-radius:8px;overflow:hidden">{src_row("02. QUYEN 2.1 - GT.pdf", 60, "1–60")}{src_row("03. QUYEN 2.2 - TCTC.pdf", 40, "61–100")}{src_row("Volume 3 - Concept drawings.pdf", 28, "101–128")}</div>'
           f'<div>{checkbox("Add a bookmark for each source file", True)}{checkbox("Keep bookmarks from the source files", True, "They are nested under the file’s bookmark.")}{checkbox("Merge same-name layers", True, "46 layers instead of 118.")}{checkbox("Add page numbers", False)}{checkbox("Optimize file size", True)}</div></div>')
ms_right = (f'<div style="flex:1 1 auto;min-width:0;padding:18px 20px;display:flex;flex-direction:column;gap:8px"><div style="font-weight:600">Bookmarks in the result</div>'
            f'<div style="border:1px solid var(--border);border-radius:8px;padding:6px;flex:1 1 auto;overflow:hidden">'
            + bm_row(0, "02. QUYEN 2.1 - GT", "1", True) + bm_row(1, "Chapter 1 — Overview", "3") + bm_row(1, "Chapter 2 — Roads", "12")
            + bm_row(0, "03. QUYEN 2.2 - TCTC", "61", True) + bm_row(1, "Drainage plan", "63") + bm_row(1, "Sections", "84")
            + bm_row(0, "Volume 3 - Concept drawings", "101", True) + '</div><div class="lbl">128 pages · about 41 MB</div></div>')
out("MergeSave.dc.html", "Merge save options", dlg("Save merged file", ms_left + ms_right, 1000, 640, f'<div style="flex:1 1 auto"></div>{btn_ghost("Cancel")}{btn_primary("Save", "save")}'), 1000, 640)
board("MergeSave.dc.html", 0, 5340, 1000, 640, "17. Merge files — save options (bookmarks, layers, page numbers)")

# ───────────────────────────── canvas ─────────────────────────────
base = {
 "Main.dc.html": (0, 0, W, H, "1. Main window — light (default), Pages panel"),
 "MainDark.dc.html": (1360, 0, W, H, "2. Main window — dark (same tokens)"),
 "Layers.dc.html": (0, 900, W, H, "3. Layers panel"),
 "Settings.dc.html": (1360, 900, W, H, "4. Settings (with Language)"),
 "PageOps.dc.html": (0, 1800, PW_, PH_, "5. Pages panel — context menu, drag onto a tab"),
 "Merge.dc.html": (860, 1800, W, H, "6. Merge files — one small window per file"),
 "Layouts.dc.html": (2220, 1800, LW_, LH_, "7. Merge files — layouts and limits"),
}
allb = {}
for k, (x, y, w, h, t_) in base.items():
    allb[k] = {"x": x, "y": y, "w": w, "h": h, "title": t_}
allb.update(boards)
canvas = {"v": 3, "createdOnFiles": {"v": 1, "at": "2026-09-27T21:00:00Z"}, "title": "XT PDF Reader — unified UI design (English only)",
          "launch": {"view": "canvas"}, "pages": [], "boards": allb, "order": list(allb.keys()), "notes": {}, "designSystems": []}
with open(os.path.join(PROJ, "canvas.json"), "w", encoding="utf-8", newline="\n") as f:
    json.dump(canvas, f, ensure_ascii=False, indent=2)
print("ok", len(allb), "boards")
