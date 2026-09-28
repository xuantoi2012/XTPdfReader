# -*- coding: utf-8 -*-
"""Chuyển ICONS của mockup (SVG path/rect/circle, lưới 24×24) thành Resources/UiIcons.xaml (Geometry).
Chạy:  python docs/ui-mockup/gen_xaml_icons.py   (từ thư mục XTPdfReader)"""
import os, re
here = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(here, "gen_ui.py"), encoding="utf-8").read()
ns = {"__file__": os.path.join(here, "gen_ui.py")}
exec(compile(src[:src.index("# ── khung chung")], "gen_ui_head", "exec"), ns)
ICONS = ns["ICONS"]
src2 = open(os.path.join(here, "gen_ui2.py"), encoding="utf-8").read()
m = re.search(r"ICONS\.update\((\{.*?\n\})\)", src2, re.S)
exec("ICONS.update(" + m.group(1) + ")", {"ICONS": ICONS})

def num(a, k, d=0.0):
    m = re.search(r'\b' + k + r'="([-\d.]+)"', a)
    return float(m.group(1)) if m else d

def fmt(v):
    s = ("%.3f" % v).rstrip("0").rstrip(".")
    return s if s else "0"

def convert(svg):
    parts = []
    for tag, attrs in re.findall(r"<(path|rect|circle)\b([^>]*?)/?>", svg):
        if tag == "path":
            d = re.search(r'\bd="([^"]*)"', attrs).group(1).strip()
            # "m x y a b …" đầu path = moveto tuyệt đối rồi lineto tương đối; đổi thành "M x y l a b …" để nối được sau hình khác
            mm = re.match(r"m\s*(-?[\d.]+)[\s,]*(-?[\d.]+)(.*)$", d, re.S)
            if mm:
                rest = mm.group(3).strip()
                d = f"M{mm.group(1)},{mm.group(2)}" + (f" l{rest}" if rest and (rest[0].isdigit() or rest[0] in "-.") else f" {rest}")
            parts.append(d)
        elif tag == "circle":
            cx, cy, r = num(attrs, "cx"), num(attrs, "cy"), num(attrs, "r")
            parts.append(f"M{fmt(cx - r)},{fmt(cy)} A{fmt(r)},{fmt(r)} 0 1 1 {fmt(cx + r)},{fmt(cy)} A{fmt(r)},{fmt(r)} 0 1 1 {fmt(cx - r)},{fmt(cy)} Z")
        else:
            x, y, w, h, rx = num(attrs, "x"), num(attrs, "y"), num(attrs, "width"), num(attrs, "height"), num(attrs, "rx")
            if rx <= 0:
                parts.append(f"M{fmt(x)},{fmt(y)} H{fmt(x + w)} V{fmt(y + h)} H{fmt(x)} Z")
            else:
                parts.append(f"M{fmt(x + rx)},{fmt(y)} H{fmt(x + w - rx)} A{fmt(rx)},{fmt(rx)} 0 0 1 {fmt(x + w)},{fmt(y + rx)} "
                             f"V{fmt(y + h - rx)} A{fmt(rx)},{fmt(rx)} 0 0 1 {fmt(x + w - rx)},{fmt(y + h)} H{fmt(x + rx)} "
                             f"A{fmt(rx)},{fmt(rx)} 0 0 1 {fmt(x)},{fmt(y + h - rx)} V{fmt(y + rx)} A{fmt(rx)},{fmt(rx)} 0 0 1 {fmt(x + rx)},{fmt(y)} Z")
    return " ".join(parts)

out = ['<!-- Sinh tự động từ docs/ui-mockup/gen_xaml_icons.py — ĐỪNG sửa tay. Icon nét mảnh, lưới 24×24 (vẽ bằng Stroke, xem UiStyles.xaml). -->',
       '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
       '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">']
for name in sorted(ICONS):
    out.append(f'    <Geometry x:Key="Ui.Icon.{name}">{convert(ICONS[name])}</Geometry>')
out.append('</ResourceDictionary>')
target = os.path.join(here, "..", "..", "Resources", "UiIcons.xaml")
open(target, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
print("icons:", len(ICONS))
