"""Pawtruth part-compositing kit.

Parts are hand-authored ASCII pixel maps of FILL colors only (1x, no AA). Each part gets an
automatic 1px 4-connected outline when composed (like a sticker), so parts layered in z-order
produce clean internal separation lines. Special characters:
    '.'  transparent
    ','  transparent AND suppresses this part's outline at that pixel (seamless joins)
    'o'  hole: forced transparent after composition (e.g. bullet hole you can see through)
Every other character maps to a palette name via LEGEND.
"""
import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'common'))
from pixkit import P, PA, check_palette, upscale, strip, save_png  # noqa: F401
from PIL import Image
import numpy as np

LEGEND = {
    'k': 'ink', 'K': 'ink2', 'p': 'shadowPurp',
    '1': 'char0', '2': 'char1', '3': 'char2', '4': 'char3',
    'm': 'rockFar3', 'n': 'rockFar2', 'M': 'rockFar1',
    'e': 'eyeAmber', 'E': 'brass2', 'w': 'sun0', 'W': 'white',
    'y': 'brass0', 'b': 'brass1', 'B': 'brass2', 'N': 'brass3',
    'r': 'red0', 'R': 'red1', 'd': 'red2', 'D': 'red3',
    'c': 'leath0', 'C': 'leath1', 'l': 'leath2', 'L': 'leath3', 'z': 'wood5', 'Z': 'wood4',
    't': 'paintTeal', 'T': 'paintTealD',
    'g': 'steel0', 'G': 'steel1', 's': 'steel2', 'S': 'steel3',
    'i': 'cream1', 'I': 'cream2', 'j': 'cream0',
    'f': 'wood1', 'F': 'wood2', 'u': 'wood0',
    'h': 'haze', 'v': 'cloud2', 'V': 'cloud3', 'x': 'sky5',
    'q': 'sigBlue', 'a': 'sand0', 'A': 'sand1',
}

W = H = 64


class Part:
    """A fill-only pixel map with a default placement (x, y = top-left in cell coords)."""

    def __init__(self, rows, x=0, y=0, outline='ink', lit=None, name='', no_outline=False):
        if isinstance(rows, str):
            rows = [r for r in rows.strip('\n').split('\n')]
        w = max(len(r) for r in rows)
        self.rows = [r.ljust(w, '.') for r in rows]
        self.x, self.y = x, y
        self.w, self.h = w, len(rows)
        self.outline = outline      # palette name for outline
        self.lit = lit              # palette name for outline on lit (right/top) side, or None
        self.name = name
        self.no_outline = no_outline
        for r in self.rows:
            for ch in r:
                if ch not in '.,o' and ch not in LEGEND:
                    raise ValueError(f'part {name}: unknown char {ch!r}')

    def flipped(self):
        return Part([r[::-1] for r in self.rows], self.x, self.y, self.outline, self.lit,
                    self.name + '_fx', self.no_outline)

    def recolor(self, mapping):
        rows = [''.join(mapping.get(c, c) for c in r) for r in self.rows]
        return Part(rows, self.x, self.y, self.outline, self.lit, self.name, self.no_outline)

    def at(self, dx=0, dy=0):
        return (self, dx, dy)


class Canvas:
    def __init__(self, w=W, h=H):
        self.w, self.h = w, h
        self.col = np.zeros((h, w, 4), np.uint8)
        self.holes = []

    def _put(self, x, y, rgba):
        if 0 <= x < self.w and 0 <= y < self.h:
            self.col[y, x] = rgba

    def draw(self, part, dx=0, dy=0, outline=True):
        ox, oy = part.x + dx, part.y + dy
        mask = np.zeros((part.h + 2, part.w + 2), bool)
        block = np.zeros((part.h + 2, part.w + 2), bool)
        for j, r in enumerate(part.rows):
            for i, ch in enumerate(r):
                if ch == ',':
                    block[j + 1, i + 1] = True
                elif ch not in '.o':
                    mask[j + 1, i + 1] = True
        if outline and not part.no_outline:
            m = mask
            nb = np.zeros_like(m)
            nb[1:, :] |= m[:-1, :]   # part pixel above  -> outline below part
            nb[:-1, :] |= m[1:, :]   # part pixel below  -> outline above part (lit/top)
            nb[:, 1:] |= m[:, :-1]   # part pixel left   -> outline right of part (lit)
            nb[:, :-1] |= m[:, 1:]   # part pixel right  -> outline left of part
            ring = nb & ~m & ~block
            # lit side: outline pixel whose part-neighbour is to its left or below it
            litside = np.zeros_like(m)
            litside[:, 1:] |= m[:, :-1]
            litside[:-1, :] |= m[1:, :]
            shadeside = np.zeros_like(m)
            shadeside[:, :-1] |= m[:, 1:]
            shadeside[1:, :] |= m[:-1, :]
            for j, i in zip(*np.nonzero(ring)):
                cname = part.outline
                if part.lit and litside[j, i] and not shadeside[j, i]:
                    cname = part.lit
                self._put(ox + i - 1, oy + j - 1, PA[cname])
        for j, r in enumerate(part.rows):
            for i, ch in enumerate(r):
                if ch in '.,':
                    continue
                if ch == 'o':
                    self.holes.append((ox + i, oy + j))
                    continue
                self._put(ox + i, oy + j, PA[LEGEND[ch]])

    def px(self, x, y, cname):
        if cname is None:
            self._put(x, y, (0, 0, 0, 0))
        else:
            self._put(x, y, PA[cname])

    def image(self):
        a = self.col.copy()
        for (x, y) in self.holes:
            if 0 <= x < self.w and 0 <= y < self.h:
                a[y, x] = (0, 0, 0, 0)
        return Image.fromarray(a, 'RGBA')


def compose(items, w=W, h=H):
    """items: list of Part | (Part, dx, dy) | (Part, dx, dy, outline_bool) | callable(canvas)."""
    cv = Canvas(w, h)
    for it in items:
        if it is None:
            continue
        if callable(it):
            it(cv)
            continue
        if isinstance(it, Part):
            cv.draw(it)
        else:
            cv.draw(*it)
    return cv.image()


def pixels(spec):
    """Return a callable that paints explicit pixels: spec = [(x, y, 'paletteName'|None), ...]."""
    def f(cv):
        for x, y, c in spec:
            cv.px(x, y, c)
    return f
