"""Generate Resources/UiIcons.xaml from Phosphor Icons (https://phosphoricons.com, MIT licence, see PHOSPHOR-LICENSE.txt).

Each icon becomes a filled WPF Geometry on a 24x24 grid ("F1" = nonzero fill, same as SVG):
  Ui.Icon.<key>        regular weight (1.5 px lines at 24 px)
  Ui.Icon.<key>.Tint   the soft duotone layer (drawn under the icon, accent colour, when a tool is active)
  Ui.Icon.<key>.Bold   bold weight, used automatically by small icon buttons (IconSize < 16)
Run: python gen_phosphor_icons.py   (downloads into %TEMP%/phosphor-cache, writes ../../Resources/UiIcons.xaml)
"""
import os
import re
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Resources", "UiIcons.xaml"))
CACHE = os.path.join(os.environ.get("TEMP", "/tmp"), "phosphor-cache")
BASE = "https://raw.githubusercontent.com/phosphor-icons/core/main/assets"
SCALE = 24 / 256

# key -> candidate Phosphor names (first one that exists wins)
ICONS = {
    # reading tools / ribbon
    "eraser": ["eraser"], "hand": ["hand"], "select": ["cursor"], "selecttext": ["cursor-text"], "snapshot": ["camera"],
    "zoomin": ["magnifying-glass-plus"], "zoomout": ["magnifying-glass-minus"],
    "fitp": ["frame-corners"], "fitw": ["arrows-out-line-horizontal", "arrows-horizontal"],
    "scroll": ["rows"], "singlepage": ["file"], "twopage": ["book-open"], "fullscreen": ["arrows-out"],
    "rotl": ["arrow-counter-clockwise"], "rotr": ["arrow-clockwise"],
    "type": ["text-t"], "hl": ["highlighter"], "note": ["chat-teardrop-text"], "callout": ["chat-centered-text"],
    "underline": ["text-underline"], "strike": ["text-strikethrough"], "pencil": ["pencil-simple"],
    "shapes": ["shapes"], "shape_rect": ["rectangle"], "shape_oval": ["circle"], "shape_cloud": ["cloud"],
    "shape_arrow": ["arrow-up-right"], "shape_line": ["line-segment"],
    "stamp": ["stamp"], "merge": ["arrows-merge", "git-merge"], "ruler": ["ruler"],
    "print": ["printer"], "search": ["magnifying-glass"], "settings": ["gear-six", "gear"],
    # file / quick access
    "home": ["house"], "newdoc": ["file-plus"], "folder": ["folder-open"], "save": ["floppy-disk"], "saveas": ["floppy-disk-back"],
    "undo": ["arrow-u-up-left"], "redo": ["arrow-u-up-right"], "export": ["export"],
    # panels
    "pages": ["files"], "bookmark": ["bookmark-simple"], "layers": ["stack"], "comment": ["chat-text"],
    "insert": ["file-plus"], "extract": ["file-arrow-up"], "trash": ["trash"], "split": ["arrows-split"],
    # page menu
    "copy": ["copy"], "cut": ["scissors"], "paste": ["clipboard-text"], "dup": ["copy-simple"],
    "goto": ["arrow-right"], "top": ["arrow-line-up"], "bottom": ["arrow-line-down"], "up": ["arrow-up"], "down": ["arrow-down"],
    # small controls
    "close": ["x"], "plus": ["plus"], "min": ["minus"], "max": ["corners-out"], "check": ["check"], "checkc": ["check-circle"],
    "chevd": ["caret-down"], "chevl": ["caret-left"], "chevr": ["caret-right"], "up2": ["caret-up"], "down2": ["caret-down"],
    "dleft": ["caret-double-left"], "dright": ["caret-double-right"], "grip": ["dots-six-vertical"],
    # misc
    "alert": ["warning-circle"], "info": ["info"], "clock": ["clock"], "cmd": ["command"], "compare": ["columns"],
    "eye": ["eye"], "file": ["file"], "filter": ["funnel"], "star": ["star"], "tray": ["tray"], "workspace": ["squares-four"],
    # ribbon / status bar
    "more": ["dots-three"], "actual": ["number-square-one"], "sidebar": ["sidebar-simple"],
    "first": ["caret-line-left"], "last": ["caret-line-right"], "squiggly": ["wave-sine"], "textbox": ["textbox"],
}
# icons that are often drawn small (≤ 15 px): also emit the bold weight
BOLD = ["close", "plus", "min", "check", "chevd", "chevl", "chevr", "up2", "down2", "dleft", "dright", "search", "star", "trash",
        "folder", "file", "save", "undo", "redo", "copy", "grip", "max", "export", "info", "alert", "first", "last", "more"]
# extra filled weight (e.g. a pinned star)
FILL = ["star"]


def fetch(weight, name):
    suffix = "" if weight == "regular" else "-" + weight
    path = os.path.join(CACHE, weight, name + suffix + ".svg")
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        try:
            with urllib.request.urlopen(f"{BASE}/{weight}/{name}{suffix}.svg", timeout=30) as r:
                data = r.read()
        except Exception:
            return None
        with open(path, "wb") as f:
            f.write(data)
    with open(path, encoding="utf-8") as f:
        return f.read()


def paths(svg):
    """(d, is_tint) for every <path>; anything else is an error (Phosphor core assets are all paths)."""
    body = re.sub(r"<svg[^>]*>|</svg>", "", svg).strip()
    result = []
    for tag in re.findall(r"<[^/!][^>]*>", body):
        if not tag.startswith("<path"):
            raise ValueError("unsupported element: " + tag[:40])
        d = re.search(r'\sd="([^"]+)"', tag).group(1)
        result.append((d, 'opacity="0.2"' in tag))
    return result


NUM = re.compile(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")


def fmt(v):
    s = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def scale_path(d):
    """Scale an SVG path to the 24 grid; every segment gets an explicit command letter (WPF mini-language)."""
    i, n, out, cmd = 0, len(d), [], None
    counts = {"M": 2, "L": 2, "T": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "A": 7, "Z": 0}

    def skip():
        nonlocal i
        while i < n and d[i] in " ,\t\r\n":
            i += 1

    def number():
        nonlocal i
        skip()
        m = NUM.match(d, i)
        if not m:
            raise ValueError(f"number expected at {i}: {d[i:i + 20]}")
        i = m.end()
        return float(m.group())

    def flag():
        nonlocal i
        skip()
        c = d[i]
        if c not in "01":
            raise ValueError(f"arc flag expected at {i}")
        i += 1
        return c

    while True:
        skip()
        if i >= n:
            break
        if d[i].isalpha():
            cmd = d[i]
            i += 1
        elif cmd is None:
            raise ValueError("path must start with a command")
        up = cmd.upper()
        if up == "Z":
            out.append(cmd)
            cmd = None
            continue
        if up == "A":
            rx, ry, rot = number(), number(), number()
            large, sweep = flag(), flag()
            x, y = number(), number()
            out.append(f"{cmd}{fmt(rx * SCALE)},{fmt(ry * SCALE)} {fmt(rot)} {large} {sweep} {fmt(x * SCALE)},{fmt(y * SCALE)}")
        else:
            vals = [number() * SCALE for _ in range(counts[up])]
            pairs = " ".join(",".join(fmt(v) for v in vals[k:k + 2]) for k in range(0, len(vals), 2))
            out.append(cmd + pairs)
        if up == "M":  # extra pairs after a moveto are linetos
            cmd = "l" if cmd == "m" else "L"
    return " ".join(out)


def geometry(key, d_list):
    return f'    <Geometry x:Key="{key}">F1 {" ".join(scale_path(d) for d in d_list)}</Geometry>'


def resolve(names, weight):
    for name in names:
        svg = fetch(weight, name)
        if svg:
            return name, svg
    return None, None


def main():
    lines, missing = [], []
    for key, names in ICONS.items():
        name, svg = resolve(names, "regular")
        if not svg:
            missing.append(key)
            continue
        lines.append(geometry(f"Ui.Icon.{key}", [d for d, tint in paths(svg)]))
        _, duo = resolve([name], "duotone")
        if duo:
            tint = [d for d, is_tint in paths(duo) if is_tint]
            if tint:
                lines.append(geometry(f"Ui.Icon.{key}.Tint", tint))
        if key in BOLD:
            _, bold = resolve([name], "bold")
            if bold:
                lines.append(geometry(f"Ui.Icon.{key}.Bold", [d for d, _ in paths(bold)]))
        if key in FILL:
            _, filled = resolve([name], "fill")
            if filled:
                lines.append(geometry(f"Ui.Icon.{key}.Fill", [d for d, _ in paths(filled)]))
    if missing:
        sys.exit("missing icons: " + ", ".join(missing))

    logo = ('    <Geometry x:Key="App.Icon.Logo">F0 M6,0 H18 A6,6 0 0 1 24,6 V18 A6,6 0 0 1 18,24 H6 A6,6 0 0 1 0,18 V6 A6,6 0 0 1 6,0 Z '
            'M7,6.3 Q7,5 8.3,5 L16.7,5 Q18,5 17.49,6.19 L17.01,7.31 Q16.5,8.5 15.2,8.5 L12,8.5 Q11,8.5 11,9.5 L11,9.5 Q11,10.5 12,10.5 '
            'L14.2,10.5 Q15.5,10.5 15.06,11.72 L14.74,12.58 Q14.3,13.8 13,13.8 L12.3,13.8 Q11,13.8 11,15.1 L11,17.7 Q11,19 9.7,19 '
            'L8.3,19 Q7,19 7,17.7 Z</Geometry>')
    text = "\n".join([
        "<!-- Generated by docs/ui-mockup/gen_phosphor_icons.py - do not edit by hand.",
        "     Icons: Phosphor Icons (https://phosphoricons.com), Copyright (c) 2023 Phosphor Icons, MIT licence (docs/ui-mockup/PHOSPHOR-LICENSE.txt).",
        "     Filled geometry on a 24x24 grid: draw with Fill (style UiIconPath), not Stroke. -->",
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        *lines,
        "    <!-- App logo: rounded square (accent colour) with a cut-out stylised F; title bar. -->",
        logo,
        "</ResourceDictionary>",
        "",
    ])
    with open(OUT, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(text)
    print(f"wrote {OUT}: {len(lines)} geometries")


if __name__ == "__main__":
    main()
