"""Base (idle, frame 0) parts of Blackpaw, front view. Cell 64x64, feet soles on row 60,
pivot [32,61]. All maps are FILL ONLY; outlines are added by bkit.compose.
Character spans y 13 (ear-tip outline) .. 60 (soles) => 48 px.
"""
from bkit import Part, pixels

# ---------------------------------------------------------------- hat (x21..43, y15..21)
HAT = Part("""
........KKp.Kp4........
.......KKKKppp44.......
.......KKKKppo4p.......
.......KKKKpppp4.......
Kp.....NBBBbbbby.....pK
KKKKpppppppppppp333334p
.KKKKKKKKKKKKKKKKKKKKK.
""", x=21, y=15, name='hat')

# ---------------------------------------------------------------- ears (behind hat)
EAR_L = Part("""
3...
3m..
3m3.
3mm3
33m3
3333
""", x=24, y=14, name='earL')

EAR_R = Part("""
...2
..m2
.3m2
3mm2
3m32
3332
""", x=37, y=14, name='earR')

# ---------------------------------------------------------------- head / face (x22..42, y20..31)
HEAD_ROWS = [
    "....4444444444444....",  # y20 (under brim; rounded skull when hat pops)
    "...444444444444444...",  # y21
    "..44444444444444444..",  # y22
    "..44444444444444442..",  # y23 forehead in brim shadow
    "..44kkk4443444kkk22..",  # y24 lids
    "..44eekkk333kkkee22..",  # y25 eyes
    "..4433333333333322...",  # y26
    ".44333332nnn23332222.",  # y27 nose
    "4433333222p222322222.",  # y28 cheek tufts
    ".4333332kk2kk2322222.",  # y29 mouth
    "..443333222j233222...",  # y30 chin + fang
    "...444444444444444...",  # y31
]
HEAD = Part(HEAD_ROWS, x=22, y=20, name='head')

# scar across his left eye (screen right) + whiskers, painted after the head
SCAR = [(39, 23, 'char0'), (38, 24, 'rockFar2'), (38, 26, 'char0'), (37, 27, 'char0')]
WHISK = [(20, 27, 'char0'), (21, 27, 'char0'), (20, 29, 'char1'), (21, 29, 'char1'),
         (43, 27, 'char0'), (44, 27, 'char0'), (43, 29, 'char0'), (44, 29, 'char0')]

# ---------------------------------------------------------------- bandana
BANDANA = Part("""
dRRRRdddddRRrrR
.dRRRRdddRRRrr.
..dRRRRiRRRrr..
...dRRRRRRrr...
....dRRRRRr....
.....dRRRr.....
......dRr......
.......d.......
""", x=25, y=32, name='bandana')

BANDANA_TIPS = Part("""
RRr...
dRRrr.
.ddRRr
...dd.
""", x=40, y=30, name='btips')

# ---------------------------------------------------------------- torso (coat body + vest)
TORSO = Part("""
...LLllllllllCC..
.LLLlllllllllCCc.
zLLLllllllllllCCc
zLLLCTTTTTttLClCc
zLLLCTTTTTttLCllC
zLLLCTTTTTttLCllC
zLLlCTTTbtttLCllC
zLLlCTTTTtttLCllC
zLLlCTTTbtBtLCllC
zLLlCTTTTttBLCllC
zLLlCTTTbtttLCllC
zLLlCTTTTtttLCllC
zLLlCTTTTtttLCllC
""", x=24, y=33, name='torso')

# ---------------------------------------------------------------- arms
ARM_R = Part("""
....LLl
...LLll
...LLll
..LLll.
..LLll.
.LLll..
.LLll..
.cCCc..
.4333..
.4332..
..43...
""", x=16, y=35, name='armR')   # his right = screen left (gun arm)

ARM_L = Part("""
llC....
llCc...
llCc...
.llCc..
.llCc..
..llCc.
..llCc.
..cCCc.
..2223.
..2232.
...22..
""", x=40, y=35, name='armL')

# ---------------------------------------------------------------- belt + holster
BELT = Part("""
LBLBLLLybbLLLBLBLL
LLLLLLLbBNLLLLLLLL
""", x=23, y=45, name='belt')

HOLSTER = Part("""
ji....
.iI...
.GiI..
cCCCl.
cCCCl.
.cCCl.
.cCCl.
.cCl..
.cCl..
..l...
""", x=21, y=42, name='holster')

# ---------------------------------------------------------------- legs / boots
LEGS = Part("""
..4SSSSSSSSSs..
..4SSSSSSSSSs..
..4SSSS4SSSSs..
..4SSS4.4SSSs..
.4SSSs...4SSSs.
.4SSSs...4SSSs.
.4SSSs...4SSSs.
.4SSSs...4SSSs.
""", x=25, y=46, name='legs')

BOOT_L = Part("""
.cCCc.
.lCCl.
.lCCl.
.lCCl.
.BbCCl
lCCCCc
LLLLLL
""", x=24, y=54, name='bootL')

BOOT_R = Part("""
.cCCc.
.lCCl.
.lCCl.
.lCCl.
lCCbB.
lCCCCc
LLLLLL
""", x=34, y=54, name='bootR')

# ---------------------------------------------------------------- coat skirt panels
SKIRT_L = Part("""
....zLLLlD
....zLLLlD
...zLLLLlD
...zLLLLD.
..zLLLLLD.
..zLLLLLD.
.zLLLLLLD.
.zLLLLLLD.
.zLLLLLLD.
zLLLLLLD..
zLL..LLD..
""", x=19, y=46, name='skirtL')

SKIRT_R = Part("""
DlllCC....
DlllClC...
DlllClC...
.DlllClC..
.DlllClC..
.DllllClC.
.DllllClC.
.DllllCllC
.DllllCllC
..DlllClCC
..Dll..CCl
""", x=36, y=46, name='skirtR')

# ---------------------------------------------------------------- tail (behind legs)
TAIL = Part("""
...23.......
..2334......
..34........
.334........
.34.........
.334........
..334.......
...3344.....
....33344...
......33344.
""", x=12, y=42, name='tail')
