"""gen_music.py - Paw Noon original score: music_title, music_standoff, sting_win/lose/round.

Key/tempo family: A minor (harmonic-minor colour, E major dominant), 75 bpm (beat = 0.8 s,
bar = 3.2 s). Countdown ticks toll E3 / F3 / G#3 and the DRAW clang resolves to A.
Loops are rendered with a tail which is folded back onto the start (wrap_tail), and all
master processing after the fold is circular, so the loop seam is mathematically seamless.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pn_dsp import *          # noqa
from pn_inst import *         # noqa

BEAT = 0.8
BAR = 4 * BEAT
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Assets', 'PawNoon',
                   'Resources', 'Audio', 'Music')

CHORDS = {  # guitar voicings: bass, alt bass, n2, n3, n4
    'Am': [45, 52, 57, 60, 64], 'G': [43, 50, 55, 59, 62], 'F': [41, 48, 53, 57, 60],
    'E': [40, 47, 52, 56, 59], 'C': [48, 43, 55, 60, 64], 'Dm': [50, 45, 57, 62, 65],
    'A': [45, 52, 57, 61, 64],
}
ROOT = {'Am': 33, 'G': 31, 'F': 29, 'E': 28, 'C': 36, 'Dm': 26, 'A': 33}


class Mix:
    def __init__(self, seconds, tail, loop=True):
        self.L = ns(seconds)
        self.n = self.L + ns(tail)
        self.loop = loop
        self.bus = {}

    def add(self, bus, mono, t0, p=0.0, g=1.0):
        b = self.bus.setdefault(bus, np.zeros((2, self.n)))
        a = ns(t0)
        if self.loop:
            a %= self.L
        a = max(a, 0)
        k = min(len(mono), self.n - a)
        b[:, a:a + k] += pan(mono[:k], p) * g

    def get(self, bus):
        return self.bus.get(bus, np.zeros((2, self.n)))


def hum(r, ms=10):
    return r.normal(0, ms / 1000.0)


def master(x, loop, target_lufs, r=None, **_legacy):
    """Clean master (clean-production pass). The former tape chain - pre-gain into tanh tape
    saturation, 13.5 kHz tape low-pass, an 800-9000 Hz hiss bed at -62 dB and 0.25 ms wow -
    is gone. Now: 28 Hz high-pass (DC/subsonic), 16 kHz guard low-pass, loudness to target,
    soft true-peak limiter (decoded OGG <= -1 dBTP). Floating point end to end."""
    return master_clean(x, loop, target_lufs)


def guitar_bus(m, r, wet_send, ir, spring, mixdur):
    out = np.zeros((2, m.n))
    if 'nyl' in m.bus:
        g = m.get('nyl')
        bir = body_ir(r)
        g = np.stack([np.convolve(g[c], bir)[:m.n] for c in range(2)])
        g = eq(g, [('hp', 70, 0.7, 0), ('peak', 3200, 1.0, 2.0), ('lp', 7500, 0.7, 0)])
        out += g
        wet_send.append((g, 0.35, ir))
    if 'twg' in m.bus:
        t = twang_amp(m.get('twg'))
        trem = 1 - 0.38 * (0.5 + 0.5 * np.sin(2 * np.pi * 5.0 * tt(m.n)))   # 16ths at 75 bpm
        t = t * trem
        out += t
        wet_send.append((t, 0.5, spring))
        wet_send.append((t, 0.25, ir))
    return out


def render(m, r, ir, spring, sends_extra=()):
    sends = []
    dry = guitar_bus(m, r, sends, ir, spring, m.n)
    for name, wet in [('lead', 0.45), ('drone', 0.35), ('bell', 1.3), ('perc', 0.3),
                      ('tom', 0.9), ('harm', 0.9), ('fx', 0.6)]:
        if name in m.bus:
            b = m.get(name)
            dry += b
            sends.append((b, wet, ir))
    wetsum = np.zeros((2, m.n))
    for b, w, irr in sends:
        wetsum += w * convolve(b, irr, m.n)
    return dry + wetsum


# =============================================================== TITLE
TITLE_CHORDS = ['Am', 'Am', 'F', 'E', 'Am', 'G', 'F', 'E', 'Am', 'C', 'Dm', 'E',
                'Am', 'F', 'G', 'E', 'Am', 'G', 'F', 'E', 'Dm', 'E', 'Am', 'E']

HOOK_A = {5: [(0, 1.5, 76), (1.5, .5, 81), (2, 1, 84), (3, .5, 83), (3.5, .5, 81)],
          6: [(0, 2.5, 83), (2.5, .5, 79), (3, .5, 81), (3.5, .5, 83)],
          7: [(0, 1.5, 84), (1.5, .5, 81), (2, 2, 77)],
          8: [(0, 1, 80), (1, .5, 77), (1.5, 2.5, 76)],
          9: [(0, 1, 81), (1, .5, 84), (1.5, 1.5, 88), (3, .5, 86), (3.5, .5, 84)],
          10: [(0, 1.5, 86), (1.5, .5, 84), (2, 2, 79)],
          11: [(0, 1, 77), (1, .5, 81), (1.5, 1.5, 86), (3, .5, 84), (3.5, .5, 83)],
          12: [(0, 1.5, 83), (1.5, .5, 80), (2, 2, 76)],
          13: [(0, 1.75, 81)],
          17: [(0, 1.5, 76), (1.5, .5, 81), (2, 1, 84), (3, .5, 83), (3.5, .5, 81)],
          18: [(0, 2, 83), (2, .5, 86), (2.5, .5, 83), (3, 1, 79)],
          19: [(0, 1.5, 84), (1.5, .5, 81), (2, 1, 77), (3, .5, 76), (3.5, .5, 77)],
          20: [(0, 3, 80), (3, .5, 83), (3.5, .5, 86)],
          21: [(0, 1.5, 88), (1.5, .5, 86), (2, 1, 84), (3, 1, 81)],
          22: [(0, 1, 83), (1, 1, 80), (2, 2, 76)],
          23: [(0, 2.75, 81)]}

TWANG_T = {13: [(0, 1, 45), (1, .5, 52), (1.5, 2.5, 57)],
           14: [(0, 1.5, 53), (1.5, .5, 52), (2, 2, 48)],
           15: [(0, 1.5, 50), (1.5, .5, 47), (2, 2, 43)],
           16: [(0, 1, 40), (1, .5, 44), (1.5, .5, 47), (2, 2, 52)],
           17: [(0, 2, 45)], 19: [(0, 2, 41)], 21: [(0, 2, 38)], 22: [(0, 2, 40)],
           24: [(0, 2, 40), (2, 1, 47), (3, 1, 44)]}


def bar_t(b, beat=0.0):
    return (b - 1) * BAR + beat * BEAT


def music_title(seed=101):
    r = rng(seed)
    nbars = len(TITLE_CHORDS)
    m = Mix(nbars * BAR, 12.0, loop=True)
    ir = musical_ir(r, t60=2.8, predelay=0.03)
    spring = spring_ir(r)
    # ---- nylon guitar
    for b, ch in enumerate(TITLE_CHORDS, start=1):
        v = CHORDS[ch]
        nxt = bar_t(b + 1)
        if b in (1, 2, 3, 4, 23, 24):           # sparse intro/outro: quarters
            pat = [(0, v[0], .85), (1, v[3], .5), (2, v[1], .7), (3, v[4], .5)]
        elif 13 <= b <= 16:                     # strummed on 1 and 3 under the twang
            pat = []
            for beat in (0, 2):
                for i, s in enumerate([v[0], v[2], v[3], v[4]]):
                    pat.append((beat + i * 0.025 / BEAT, s, 0.45 - 0.04 * i))
            pat.append((3.5, v[3], 0.3))
        else:
            pat = [(0, v[0], 1.0), (.5, v[3], .55), (1, v[2], .5), (1.5, v[4], .62),
                   (2, v[1], .85), (2.5, v[3], .5), (3, v[2], .48), (3.5, v[3], .52)]
        for beat, note, vel in pat:
            t0 = bar_t(b, beat) + hum(r, 7)
            dur = min(4.0, nxt - t0 + 0.12)
            y = nylon(note, dur + 0.1, r, vel * r.uniform(0.9, 1.05), release_at=dur)
            m.add('nyl', y, t0, -0.3 + 0.12 * (note - 50) / 14, 0.32)
    # ---- whistle lead
    notes = []
    for b, ph in HOOK_A.items():
        for beat, d, mi in ph:
            notes.append((bar_t(b, beat) + hum(r, 6), d * BEAT * 0.97, mi, 0.75 + 0.1 * (d > 1)))
    w = voice_line(notes, m.n, r, 'whistle', vib_depth=0.22, scoop=0.6, fall=0.6)
    m.add('lead', w, 0, 0.12, 0.21)
    # ---- twang baritone
    for b, ph in TWANG_T.items():
        for beat, d, mi in ph:
            t0 = bar_t(b, beat) + hum(r, 5)
            y = twang(mi, d * BEAT + 1.2, r, 0.85, release_at=d * BEAT + 0.05)
            m.add('twg', y, t0, 0.3, 0.30 if b <= 16 else 0.20)
    # ---- low bowed drone/bass following chord roots
    dn = []
    for b, ch in enumerate(TITLE_CHORDS, start=1):
        dn.append((bar_t(b) + 0.02, BAR - 0.05, ROOT[ch] + 12, 0.55 if b in (1, 2, 23, 24) else 0.7))
    d = voice_line(dn, m.n, r, 'bowed', glide_ms=120, scoop=0, vib_depth=0.06, att_ms=350,
                   rel_ms=500)
    m.add('drone', bw(d, 'highpass', 75, 2), 0, -0.1, 0.09)
    d2 = voice_line([(t0, du, mi - 12, v) for t0, du, mi, v in dn], m.n, r, 'bowed',
                    glide_ms=120, scoop=0, vib_depth=0.04, att_ms=400, rel_ms=600, bright=0.6)
    m.add('drone', bw(d2, 'highpass', 50, 2), 0, 0.1, 0.035)
    # ---- church bell (distant)
    for t0, f, g in [(bar_t(1), 220.0, 0.10), (bar_t(23), 220.0, 0.06)]:
        y = bell(f, 7.0, r, bright=0.7)
        y = bw(y, 'lowpass', 2800, 2)
        m.add('bell', y, t0, -0.45, g)
    # ---- percussion
    for b in range(9, 23):
        if b in (13, 14, 15, 16) or b < 9:
            continue
        lvl = 0.5 if b < 13 else 0.8
        for e in range(8):
            v = (0.55 if e % 2 else 0.9) * lvl * r.uniform(0.85, 1.1)
            m.add('perc', shaker_grain(r, v), bar_t(b, e * 0.5) + hum(r, 6), 0.35, 0.05)
    for b in range(13, 23):
        for beat in (1, 3):
            m.add('perc', brush_swish(r, r.uniform(0.8, 1.0)), bar_t(b, beat) - 0.04, -0.15, 0.06)
    for b, beat, f, g in [(13, 0, 72, 0.22), (16, 3.5, 85, 0.12), (17, 0, 70, 0.22),
                          (22, 2, 80, 0.14), (22, 3, 72, 0.16)]:
        m.add('tom', tom(f, 1.5, r), bar_t(b, beat), 0.0, g)
    mix = render(m, r, ir, spring)
    mix = wrap_tail(mix, m.L)
    return master(mix, True, -16.0, r)


# =============================================================== STANDOFF
def music_standoff(seed=202):
    r = rng(seed)
    nb = 16
    m = Mix(nb * BAR, 12.0, loop=True)
    ir = musical_ir(r, t60=3.6, predelay=0.04)
    spring = spring_ir(r, length=3.0)
    L = nb * BAR
    # drone: A1/A2 pedal held all loop (two overlapping long notes so swells never stop)
    for octv, g, p in [(33, 0.06, -0.2), (45, 0.10, 0.2)]:
        notes = [(0.0, L / 2 + 0.6, octv, 0.8), (L / 2, L / 2 + 0.6, octv, 0.8)]
        d = voice_line(notes, m.n, r, 'bowed', glide_ms=200, scoop=0, vib_depth=0.05,
                       att_ms=1500, rel_ms=1500, bright=1.2)
        sw = 0.75 + 0.25 * np.sin(2 * np.pi * 4 * tt(m.n) / L + (0 if octv == 33 else 1.3))
        m.add('drone', bw(d * sw, 'highpass', 45, 2), 0, p, g)
    # fifth (E) swells in bars 5-8 and 13-16, b2 (Bb) shadow in bars 11-12
    for t0, du, mi, g in [(bar_t(5), 4 * BAR, 40, 0.06), (bar_t(13), 4 * BAR, 40, 0.06),
                          (bar_t(11), 2 * BAR, 46, 0.035)]:
        d = voice_line([(t0, du, mi, 0.8)], m.n, r, 'bowed', scoop=0, vib_depth=0.07,
                       att_ms=2500, rel_ms=1800)
        m.add('drone', d, 0, 0.0, g)
    # natural harmonics, sparse
    for b, beat, mi, p in [(2, 2, 88, -0.5), (3, 3, 81, 0.5), (6, 1, 76, -0.4), (8, 0, 88, 0.45),
                           (10, 2, 82, -0.5), (12, 1, 81, 0.5), (14, 3, 88, -0.35), (16, 1, 76, 0.4)]:
        m.add('harm', harmonic_ping(mi, 4.0, r, 0.6), bar_t(b, beat) + hum(r, 20), p, 0.07)
    # baritone twang tolls
    for b, beat, mi, g in [(1, 0.25, 45, 0.30), (9, 0, 40, 0.30), (13, 2, 41, 0.22), (14, 2, 40, 0.25)]:
        m.add('twg', twang(mi, 5.0, r, 0.8), bar_t(b, beat), 0.25, g)
    # distant harmonica call quoting the title hook
    call = [(bar_t(5, 0), 1.6, 76, 0.6), (bar_t(5, 2), 0.75, 81, 0.6), (bar_t(5, 3), 2.3, 84, 0.7),
            (bar_t(6, 2), 0.75, 83, 0.6), (bar_t(6, 3), 2.2, 81, 0.6)]
    h = voice_line(call, m.n, r, 'harmonica', vib_depth=0.12, scoop=0.8, att_ms=60, rel_ms=200, fall=0.4)
    h = bw(h, 'lowpass', 3500, 2)
    m.add('lead', h, 0, -0.15, 0.05)
    # far bell, rattlesnake sizzle, bowed cymbal swells leading into bar 9 and bar 1
    m.add('bell', bw(bell(165.0, 8.0, r, bright=0.5), 'lowpass', 2000, 2), bar_t(1, 0.5), 0.45, 0.07)
    m.add('fx', rattle(2.8, r), bar_t(11, 1), 0.5, 0.015)
    for b in (7, 15):
        m.add('fx', bowed_cymbal(2 * BAR, r), bar_t(b), -0.3, 0.012)
    m.bus['drone'] = eq(m.get('drone'), [('peak', 220, 0.8, 3.0), ('peak', 650, 0.9, 3.0)])
    mix = render(m, r, ir, spring)
    mix = wrap_tail(mix, m.L)
    return master(mix, True, -20.0, r, drive=1.1)


# =============================================================== STINGS
def finish_sting(m, r, ir, spring, target, fade=0.6):
    x = render(m, r, ir, spring)
    x = master(x, False, target, r)
    # trim trailing silence and fade
    env = np.max(np.abs(x), axis=0)
    above = np.nonzero(env > db2a(-60))[0]
    end = min(x.shape[-1], int(above[-1]) + 1)
    x = x[:, :end] * ramp_out(end, fade * 1000)
    return x


def sting_win(seed=303):
    r = rng(seed)
    m = Mix(6.0, 0.0, loop=False)
    ir = musical_ir(r, t60=2.4)
    spring = spring_ir(r)
    for i, mi in enumerate([40, 45, 49, 52]):
        d = 0.13 if i < 3 else 1.6
        m.add('twg', twang(mi, d + 1.0, r, 0.95, release_at=d), i * 0.13, 0.25, 0.35)
    notes = [(0.52, 0.17, 76, .8), (0.69, 0.17, 81, .8), (0.86, 0.17, 85, .85), (1.03, 1.55, 88, .95)]
    w = voice_line(notes, m.n, r, 'whistle', vib_depth=0.28, scoop=0.7, fall=0.0)
    m.add('lead', w, 0, 0.1, 0.22)
    for i, s in enumerate([45, 52, 57, 61, 64, 69]):
        m.add('nyl', nylon(s, 3.0, r, 0.9 - 0.05 * i), 1.03 + 0.018 * i, -0.3, 0.35)
    m.add('bell', bell(440.0, 4.0, r, bright=1.0), 1.03, -0.4, 0.06)
    m.add('tom', tom(70, 1.5, r), 1.03, 0, 0.5)
    m.add('perc', shaker_grain(r, 1.0), 1.03, 0.35, 0.08)
    return finish_sting(m, r, ir, spring, -15.0)


def sting_lose(seed=404):
    r = rng(seed)
    m = Mix(6.0, 0.0, loop=False)
    ir = musical_ir(r, t60=2.6)
    spring = spring_ir(r)
    for i, mi in enumerate([45, 44, 43]):
        d = 0.32 if i < 2 else 1.8
        m.add('twg', twang(mi, d + 1.2, r, 0.9 - 0.1 * i, release_at=d), i * 0.34, 0.25, 0.35)
    notes = [(0.05, 0.3, 88, .7), (0.4, 0.3, 84, .65), (0.75, 1.2, 81, .6)]
    w = voice_line(notes, m.n, r, 'whistle', glide_ms=160, vib_depth=0.45, vib_rate=4.2,
                   vib_delay=0.1, scoop=0.0, fall=7.0)
    m.add('lead', w, 0, 0.1, 0.2)
    for i, s in enumerate([33, 40, 45, 48]):
        m.add('nyl', nylon(s, 3.0, r, 0.6), 2.15 + 0.03 * i, -0.2, 0.4)
    m.add('perc', brush_swish(r, 1.0, 0.5), 2.1, 0.3, 0.08)
    return finish_sting(m, r, ir, spring, -16.0)


def sting_round(seed=505):
    r = rng(seed)
    m = Mix(3.5, 0.0, loop=False)
    ir = musical_ir(r, t60=1.8)
    spring = spring_ir(r)
    for i, mi in enumerate([45, 52, 57]):
        m.add('twg', twang(mi, 2.5, r, 1.0, release_at=1.1), 0.012 * i, 0.15, 0.33)
    m.add('tom', tom(68, 1.4, r), 0.0, 0.0, 0.45)
    m.add('nyl', nylon(69, 2.0, r, 0.6), 0.0, -0.3, 0.2)
    return finish_sting(m, r, ir, spring, -16.0, fade=0.4)


TRACKS = {'music_title': music_title, 'music_standoff': music_standoff,
          'sting_win': sting_win, 'sting_lose': sting_lose, 'sting_round': sting_round}


def main(names=None):
    for name, fn in TRACKS.items():
        if names and name not in names:
            continue
        x = fn()
        write_ogg(os.path.join(OUT, name + '.ogg'), x, q=8)
        print(f'{name}: {x.shape[-1] / SR:.2f}s lufs={lufs(x):.1f} peak={a2db(peak(x)):.2f}dB')


if __name__ == '__main__':
    main(sys.argv[1:])
