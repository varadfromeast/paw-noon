"""v2 hand-pixeled revolver gripped by the paw (pixel-level art, 36 x 18 in profile)."""
GUN_V2 = [
    "...kk...............................",
    "..kSSk..............................",
    "..kdSSk.............................",
    "...kdSkkkkkkkkk..................kk.",
    "...kkdkSSSSSSSSkkkkkkkkkkkkkkkkkkSSk",
    "....kdksSSSsSSSSSSSSSSSSSSSSSSSSSSSk",
    "....kdkssssssssswwwwwwwwwwwwwwwwwssk",
    "....kdkssdssdssssssssssssssssssssssk",
    "....kdksdssdssdddddddddddddddddddddk",
    "...kkdkddddddddkkkkkkkkkkkkkkkkkkkk.",
    "..kffkddddddddkksssssssssssssk......",
    ".kfFFfkkkkkkkkkkddddddddddddk.......",
    "kfFFFFfkdddk.kk.kkkkkkkkkkkk........",
    "kfFFFFFfkk.kdk......................",
    "kfFFoFFofk..kk......................",
    "kfffoffofk..........................",
    ".kooooooOk..........................",
    "..kkkkkkk...........................",
]
LEG = {'k': 'ink', 'S': ('steel', 3), 's': ('steel', 2), 'd': ('steel', 1), 'w': ('steel', 5),
       'f': ('fur', 2), 'F': ('fur', 4), 'o': ('fur', 1), 'O': ('fur', 0)}
TIP = (35, 5)
WRIST = (3, 15)


def sheared(step=3, x0=15, pad=10):
    """Barrel rises 1px every `step` columns toward the target (clean line steps)."""
    rows = GUN_V2
    h, w = len(rows), len(rows[0])
    out = [['.'] * w for _ in range(h + pad)]
    for y in range(h):
        for x in range(w):
            up = ((x - x0) // step + 1) if x >= x0 else 0
            if rows[y][x] != '.':
                out[y + pad - up][x] = rows[y][x]
    tip = (TIP[0], TIP[1] + pad - ((TIP[0] - x0) // step + 1))
    return [''.join(r) for r in out], tip, (WRIST[0], WRIST[1] + pad)
