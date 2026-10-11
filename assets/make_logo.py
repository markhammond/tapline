"""Writes the Tapline logo (logo.svg, beside this script; or the path given): ridge lines, back to front, each a spline
through a few heights as the sample's are, the nearer hiding those behind, fainter as they recede — drawn in
currentColor. Python 3, nothing to install: python3 assets/make_logo.py"""
import pathlib, sys

GRID = 21                                       # heights across a line, as the sample's frames have theirs (39), splined between

LINES = [  # back to front: from which grid point, its heights (units) — between them, the line flat
    (8, [0.5, 2.5, 1.5, 3.5, 1.0, 0.3]),
    (6, [1.0, 5.0, 2.5, 7.0, 3.0, 6.5, 2.0, 1.0]),
    (5, [1.5, 9.0, 4.0, 10.0, 6.0, 11.0, 5.0, 7.0, 4.0, 2.0, 0.5]),
    (5, [2.0, 6.0, 14.0, 8.0, 13.0, 7.0, 12.0, 6.0, 8.0, 3.0, 1.0]),
    # A small peak; then its hump, grid points 4 to 16 (~4/7 of the line): a bump on its rise at half its height, the
    # top, then down a long slope.
    (2, [3.0, 0.0, 1.0, 12.0, 10.0, 21.0, 12.0, 17.0, 9.0, 13.0, 7.0, 8.0, 4.5, 2.5, 0.8]),
]


def ridge(line, y, x0, x1):
    """A line through its heights, a Catmull-Rom spline (as the sample's), never below its base (its Béziers' control
    points kept at or above it: the curve within their hull)."""
    first, heights = line
    hs = [0.0] * GRID
    hs[first:first + len(heights)] = heights
    k0, k1 = first - 1, first + len(heights)       # from the flat point before to the one after
    pts = [(x0 + (x1 - x0) * k / (GRID - 1), y - hs[k]) for k in range(GRID)]
    f = lambda v: f"{v:.1f}".rstrip("0").rstrip(".")
    d = f"M{f(x0)} {f(y)}H{f(pts[k0][0])}"
    for k in range(k0, k1):
        p0 = pts[max(k - 1, 0)]; p1 = pts[k]; p2 = pts[k + 1]; p3 = pts[min(k + 2, GRID - 1)]
        c1 = (p1[0] + (p2[0] - p0[0]) / 6, min(y, p1[1] + (p2[1] - p0[1]) / 6))
        c2 = (p2[0] - (p3[0] - p1[0]) / 6, min(y, p2[1] - (p3[1] - p1[1]) / 6))
        d += f"C{f(c1[0])} {f(c1[1])} {f(c2[0])} {f(c2[1])} {f(p2[0])} {f(p2[1])}"
    return d + f"H{f(x1)}"


def build(size=64, x0=7.0, x1=57.0, top=17.0, gap=8.0, stroke=1.45, casing=0.85, faint=0.3):
    n = len(LINES)
    ds = [ridge(line, top + i * gap, x0, x1) for i, line in enumerate(LINES)]
    defs = [f'    <path id="l{i}" d="{d}"/>' for i, d in enumerate(ds)]
    # Each line hides what is behind it: a line's mask is blacked out under every line in front of it (and a casing
    # either side of that line's stroke) — the page shows through, whatever it is.
    masks = []
    for j in range(n - 1):
        # (a line filled: closed along its base, what its peaks rise over; stroked: the casing; below its base, a rect)
        cover = "".join(f'<use href="#l{k}"/><rect y="{top + k * gap:g}" width="{size}" height="{size - top - k * gap:g}"/>' for k in range(j + 1, n))
        masks.append(f'    <mask id="m{j}" maskUnits="userSpaceOnUse" x="0" y="0" width="{size}" height="{size}">'
                     f'<rect width="{size}" height="{size}" fill="#fff"/>'
                     f'<g fill="#000" stroke="#000" stroke-width="{stroke + 2 * casing:g}">{cover}</g></mask>')
    uses = []
    for i in range(n):
        o = faint + (1 - faint) * i / (n - 1)
        attrs = (f' stroke-opacity="{o:.2f}"' if o < 1 else "") + (f' mask="url(#m{i})"' if i < n - 1 else "")
        uses.append(f'  <use href="#l{i}"{attrs}/>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}">\n'
            f'  <title>Tapline</title>\n'
            # As an image (its own document: GitHub's README), black or white with the viewer's scheme (light-dark(), black
            # where unsupported; Chromium follows the scheme only where the document declares both); inline in a page, not
            # its root: the page's colour (currentColor).
            f'  <style>svg:root{{color-scheme:light dark;color:#000;color:light-dark(#000,#fff)}}</style>\n  <defs>\n' + "\n".join(defs + masks) + '\n  </defs>\n'
            f'  <g fill="none" stroke="currentColor" stroke-width="{stroke:g}" stroke-linecap="round" stroke-linejoin="round">\n'
            + "\n".join("  " + u for u in uses) + '\n  </g>\n</svg>\n')


if __name__ == "__main__":
    out = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else pathlib.Path(__file__).with_name("logo.svg")
    out.write_text(build())
