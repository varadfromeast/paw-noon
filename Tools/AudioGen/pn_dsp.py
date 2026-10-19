"""pn_dsp.py - shared DSP helpers for the Paw Noon audio generators.

numpy + scipy only. Everything is deterministic (seeded RNGs) so the generators
reproduce the delivered files exactly on the same toolchain.

Conventions
- SR = 48000. Mono signals are 1-D float64 arrays, stereo signals are (2, n).
- "circular=True" variants treat the signal as one period of a loop, so the
  processed result is itself seamlessly loopable.
"""
import os
import subprocess
import tempfile

import numpy as np
from scipy import signal
from scipy.io import wavfile
from scipy.ndimage import minimum_filter1d, uniform_filter1d

SR = 48000


# ---------------------------------------------------------------- basics
def rng(seed):
    return np.random.default_rng(seed)


def ns(sec):
    """seconds -> samples"""
    return int(round(sec * SR))


def tt(n):
    return np.arange(n) / SR


def db2a(db):
    return 10.0 ** (np.asarray(db, dtype=float) / 20.0)


def a2db(a):
    return 20.0 * np.log10(np.maximum(np.abs(a), 1e-12))


def mtof(m):
    return 440.0 * 2.0 ** ((np.asarray(m, dtype=float) - 69.0) / 12.0)


def peak(x):
    return float(np.max(np.abs(x))) if x.size else 0.0


def pad_to(x, n):
    if x.shape[-1] >= n:
        return x[..., :n]
    w = [(0, 0)] * (x.ndim - 1) + [(0, n - x.shape[-1])]
    return np.pad(x, w)


# ---------------------------------------------------------------- filters
def biquad(kind, f, q=0.7071, gain_db=0.0, sr=SR):
    """RBJ cookbook biquad. kind: lp hp bp peak lowshelf highshelf notch ap"""
    f = float(np.clip(f, 5.0, 0.49 * sr))
    w0 = 2 * np.pi * f / sr
    cw, sw = np.cos(w0), np.sin(w0)
    alpha = sw / (2 * q)
    A = 10 ** (gain_db / 40.0)
    if kind == 'lp':
        b = [(1 - cw) / 2, 1 - cw, (1 - cw) / 2]; a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == 'hp':
        b = [(1 + cw) / 2, -(1 + cw), (1 + cw) / 2]; a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == 'bp':
        b = [alpha, 0, -alpha]; a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == 'notch':
        b = [1, -2 * cw, 1]; a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == 'ap':
        b = [1 - alpha, -2 * cw, 1 + alpha]; a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == 'peak':
        b = [1 + alpha * A, -2 * cw, 1 - alpha * A]; a = [1 + alpha / A, -2 * cw, 1 - alpha / A]
    elif kind in ('lowshelf', 'highshelf'):
        sq = 2 * np.sqrt(A) * alpha
        if kind == 'lowshelf':
            b = [A * ((A + 1) - (A - 1) * cw + sq), 2 * A * ((A - 1) - (A + 1) * cw),
                 A * ((A + 1) - (A - 1) * cw - sq)]
            a = [(A + 1) + (A - 1) * cw + sq, -2 * ((A - 1) + (A + 1) * cw),
                 (A + 1) + (A - 1) * cw - sq]
        else:
            b = [A * ((A + 1) + (A - 1) * cw + sq), -2 * A * ((A - 1) + (A + 1) * cw),
                 A * ((A + 1) + (A - 1) * cw - sq)]
            a = [(A + 1) - (A - 1) * cw + sq, 2 * ((A - 1) - (A + 1) * cw),
                 (A + 1) - (A - 1) * cw - sq]
    else:
        raise ValueError(kind)
    b = np.array(b, float) / a[0]
    a = np.array(a, float) / a[0]
    return b, a


def _run(b, a, x, circular):
    if circular:
        n = x.shape[-1]
        xx = np.concatenate([x, x], axis=-1)
        return signal.lfilter(b, a, xx, axis=-1)[..., n:]
    return signal.lfilter(b, a, x, axis=-1)


def filt(x, kind, f, q=0.7071, gain_db=0.0, circular=False):
    b, a = biquad(kind, f, q, gain_db)
    return _run(b, a, x, circular)


def eq(x, bands, circular=False):
    """bands: list of (kind, f, q, gain_db)"""
    for band in bands:
        x = filt(x, *band, circular=circular)
    return x


def bw(x, btype, f, order=2, circular=False):
    """Butterworth via SOS. btype lowpass/highpass/bandpass; f scalar or (lo, hi)."""
    if np.ndim(f):
        f = [min(max(v, 5.0), 0.49 * SR) for v in f]
    else:
        f = min(max(f, 5.0), 0.49 * SR)
    sos = signal.butter(order, f, btype=btype, fs=SR, output='sos')
    if circular:
        n = x.shape[-1]
        xx = np.concatenate([x, x], axis=-1)
        return signal.sosfilt(sos, xx, axis=-1)[..., n:]
    return signal.sosfilt(sos, x, axis=-1)


def onepole_lp(x, fc, circular=False):
    p = np.exp(-2 * np.pi * fc / SR)
    return _run([1 - p], [1, -p], x, circular)


def tv_biquad(x, kind, fcurve, q=1.0, gain_db=0.0, hop=64):
    """Time-varying biquad on a mono signal (coefficients updated every hop)."""
    fcurve = np.broadcast_to(np.asarray(fcurve, float), x.shape)
    qcurve = np.broadcast_to(np.asarray(q, float), x.shape)
    y = np.empty_like(x)
    zi = np.zeros(2)
    for s in range(0, len(x), hop):
        m = min(s + hop // 2, len(x) - 1)
        b, a = biquad(kind, fcurve[m], qcurve[m], gain_db)
        y[s:s + hop], zi = signal.lfilter(b, a, x[s:s + hop], zi=zi)
    return y


def fft_shape(x, mag_fn):
    """Zero-phase circular filtering in the frequency domain (exactly periodic)."""
    n = x.shape[-1]
    f = np.fft.rfftfreq(n, 1 / SR)
    return np.fft.irfft(np.fft.rfft(x, axis=-1) * mag_fn(f), n, axis=-1)


# ---------------------------------------------------------------- noise & curves
def shaped_noise(n, r, mag_fn):
    """Periodic coloured noise of length n with spectral magnitude mag_fn(f)."""
    return fft_shape(r.standard_normal(n), mag_fn)


def pink_mag(alpha=1.0, lo=20.0, hi=20000.0):
    def m(f):
        g = 1.0 / np.maximum(f, lo) ** (alpha / 2)
        g[f > hi] *= (hi / f[f > hi]) ** 4
        g[0] = 0
        return g
    return m


def band_mag(lo, hi, slope=4):
    def m(f):
        g = np.ones_like(f)
        g *= 1 / (1 + (lo / np.maximum(f, 1e-3)) ** slope)
        g *= 1 / (1 + (f / hi) ** slope)
        g[0] = 0
        return g
    return m


def smooth_curve(n, rate_hz, r, periodic=True):
    """Band-limited random curve in [-1, 1] (periodic over n samples)."""
    nn = n if periodic else 2 * n
    k = max(1, int(round(rate_hz * nn / SR)))
    spec = np.zeros(nn // 2 + 1, complex)
    w = np.exp(-0.5 * (np.arange(1, k + 1) / (0.6 * k + 1e-9)) ** 2)
    spec[1:k + 1] = (r.standard_normal(k) + 1j * r.standard_normal(k)) * w
    c = np.fft.irfft(spec, nn)[:n]
    c -= c.mean()
    return c / (np.max(np.abs(c)) + 1e-12)


def lp_noise(n, fc, r):
    """Smooth zero-mean noise, unit std, roughly band-limited to fc."""
    x = bw(r.standard_normal(n + 2000), 'lowpass', fc, 2)[2000:]
    return x / (np.std(x) + 1e-12)


# ---------------------------------------------------------------- envelopes
def exp_dec(n, t60):
    return 10.0 ** (-3.0 * tt(n) / t60)


def ramp_in(n, ms):
    k = min(n, max(1, ns(ms / 1000.0)))
    e = np.ones(n)
    e[:k] = 0.5 - 0.5 * np.cos(np.pi * np.arange(k) / k)
    return e


def ramp_out(n, ms):
    k = min(n, max(1, ns(ms / 1000.0)))
    e = np.ones(n)
    e[n - k:] = 0.5 + 0.5 * np.cos(np.pi * np.arange(k) / k)
    return e


def ar(n, att_ms, rel_ms, curve=1.0):
    return ramp_in(n, att_ms) * ramp_out(n, rel_ms) ** curve


def bump(n, center_s, width_s):
    t = tt(n)
    return np.exp(-0.5 * ((t - center_s) / width_s) ** 2)


# ---------------------------------------------------------------- oscillators
def phase_from_freq(f):
    return 2 * np.pi * np.cumsum(f) / SR


def chirp_exp(n, f0, f1, dur=None):
    dur = dur or n / SR
    t = tt(n)
    k = (f1 / f0) ** (1 / dur)
    f = f0 * k ** np.minimum(t, dur)
    return np.sin(phase_from_freq(f)), f


# ---------------------------------------------------------------- dynamics / colour
def sat(x, drive=1.0):
    if drive <= 0:
        return x
    return np.tanh(drive * x) / np.tanh(drive)


def soft_clip_level(x, level):
    """tanh soft clip that is transparent well below `level`."""
    return level * np.tanh(x / level)


def limiter(x, ceiling, hold_ms=6.0, smooth_ms=3.0, circular=False):
    """Look-ahead brickwall-ish limiter (zero-latency offline). Works on mono or (ch, n)."""
    a = np.max(np.abs(np.atleast_2d(x)), axis=0)
    need = np.minimum(1.0, ceiling / np.maximum(a, 1e-12))
    wh = max(1, ns(hold_ms / 1000))
    wsm = max(1, min(wh, ns(smooth_ms / 1000)))
    mode = 'wrap' if circular else 'nearest'
    g = minimum_filter1d(need, size=2 * wh + 1, mode=mode)
    g = uniform_filter1d(g, size=2 * wsm + 1, mode=mode)
    g = np.minimum(g, need)  # guard against float slop
    return x * g


def compress_env(x, thresh_db, ratio, att_ms=5, rel_ms=120):
    """Simple feed-forward compressor (mono), sample loop free via filtering of the level."""
    lvl = np.abs(x)
    env = onepole_lp(lvl, 1000.0 / (2 * np.pi * rel_ms))
    env = np.maximum(env, onepole_lp(lvl, 1000.0 / (2 * np.pi * att_ms)))
    over = a2db(env) - thresh_db
    gdb = np.where(over > 0, -over * (1 - 1 / ratio), 0.0)
    return x * db2a(gdb)


def wow(x, depth_ms, rate_hz, circular=True, phase=0.0):
    """NOT USED by the music since the clean-production pass (kept for reference). Tape wow: modulated delay with linear interpolation. Rate snapped to an integer
    number of cycles per period when circular."""
    n = x.shape[-1]
    if circular:
        cyc = max(1, round(rate_hz * n / SR))
        rate_hz = cyc * SR / n
    t = np.arange(n)
    d = (depth_ms * SR / 1000.0) * (1 + np.sin(2 * np.pi * rate_hz * t / SR + phase)) * 0.5
    pos = t - d
    i0 = np.floor(pos).astype(np.int64)
    fr = pos - i0
    if circular:
        i0m = np.mod(i0, n); i1m = np.mod(i0 + 1, n)
    else:
        i0m = np.clip(i0, 0, n - 1); i1m = np.clip(i0 + 1, 0, n - 1)
    return x[..., i0m] * (1 - fr) + x[..., i1m] * fr


# ---------------------------------------------------------------- space
def pan(mono, p=0.0, width=None):
    """Constant-power pan, p in [-1 (L), 1 (R)] -> (2, n)."""
    th = (p + 1) * np.pi / 4
    return np.stack([mono * np.cos(th), mono * np.sin(th)])


def reverb_ir(r, t60=2.5, t60_hi=None, length=None, predelay=0.02, stereo=True,
              er_taps=8, er_span=0.07, er_gain=0.6, hi_cut=9000.0, lo_cut=120.0,
              diffuse_ms=60.0):
    """Synthetic decaying-noise IR with frequency dependent decay and early reflections.
    Returns (2, n) normalised to unit energy per channel."""
    t60_hi = t60_hi or t60 * 0.45
    length = length or (t60 * 1.15 + predelay)
    n = ns(length)
    bands = [(20, 250), (250, 1000), (1000, 3000), (3000, 7000), (7000, 20000)]
    cents = np.array([120, 500, 1700, 4500, 10000], float)
    w = np.log(cents / 120) / np.log(10000 / 120)
    t60s = t60 * (1 - w) + t60_hi * w
    pre = ns(predelay)
    t = tt(n - pre)
    chans = []
    for ch in range(2 if stereo else 1):
        tail = np.zeros(n - pre)
        for (lo, hi), T in zip(bands, t60s):
            nz = bw(r.standard_normal(n - pre), 'bandpass', (lo, min(hi, 0.45 * SR)), 2)
            tail += nz * 10 ** (-3 * t / T)
        tail *= 1 - np.exp(-t / (diffuse_ms / 1000 / 3))   # build-up of diffuse field
        tail = bw(tail, 'lowpass', hi_cut, 2)
        tail = bw(tail, 'highpass', lo_cut, 1)
        tail /= np.sqrt(np.sum(tail ** 2)) + 1e-12
        ir = np.zeros(n)
        ir[pre:] = tail
        if er_taps:
            er = np.zeros(n)
            times = np.sort(r.uniform(0.004, er_span, er_taps))
            for k, tk in enumerate(times):
                idx = ns(tk)
                if idx < n:
                    er[idx] += r.choice([-1, 1]) * er_gain * (0.85 ** k) / np.sqrt(er_taps)
            er = bw(er, 'lowpass', 6000, 1)
            ir += er
        chans.append(ir / (np.sqrt(np.sum(ir ** 2)) + 1e-12))
    return np.stack(chans)


def musical_ir(r, t60=2.5, t60_hi=None, predelay=0.02, hi_cut=7500.0, lo_cut=120.0, seed=None,
               **legacy_kw):
    """Smooth hall IR for the music (clean-production pass, replaces reverb_ir in the score).

    reverb_ir(r, ...) is still called first and discarded, so the caller's RNG stream advances
    exactly as before and every humanised note/velocity after this call is unchanged.
    The new tail comes from its own RNG and differs from reverb_ir in the ways that matter
    for "fizz":
      - 9 octave-ish decay bands (4th-order) instead of 5, so the decay is spectrally smooth;
      - slow diffuse build-up (cosine fade over ~90 ms) and a further 40 ms delay of the
        content above 2.5 kHz, so the IR has no bright noise burst at its head;
      - air absorption: HF decays faster and the whole tail is low-passed 4th order at hi_cut;
      - early reflections are smooth band-limited pulses (no single-sample clicks);
      - longer tail (1.5 x T60) so it fades below -90 dB instead of being truncated.
    Returns (2, n), unit energy per channel."""
    reverb_ir(r, t60=t60, t60_hi=t60_hi, predelay=predelay, hi_cut=hi_cut, lo_cut=lo_cut, **legacy_kw)
    if seed is None:
        seed = int(t60 * 1000) * 7 + int(predelay * 1e4) + int(hi_cut)
    rr = np.random.default_rng(seed)
    t60_hi = t60_hi or t60 * 0.4
    length = t60 * 1.5 + predelay
    n = ns(length)
    pre = ns(predelay)
    m = n - pre
    t = tt(m)
    edges = [20, 150, 300, 600, 1200, 2400, 4000, 6000, 9000, 16000]
    chans = []
    for ch in range(2):
        lo_part = np.zeros(m)
        hi_part = np.zeros(m)
        src = rr.standard_normal(m)
        for lo, hi in zip(edges[:-1], edges[1:]):
            fc = np.sqrt(lo * hi)
            w = np.clip(np.log(fc / 150) / np.log(9000 / 150), 0, 1)
            T = t60 * (1 - w) + t60_hi * w
            sos = signal.butter(4, (lo, min(hi, 0.45 * SR)), btype='bandpass', fs=SR, output='sos')
            band = signal.sosfiltfilt(sos, src) * 10 ** (-3 * t / T)
            if fc < 2500:
                lo_part += band
            else:
                hi_part += band
        build = 0.5 - 0.5 * np.cos(np.pi * np.clip(t / 0.09, 0, 1))
        hi_build = 0.5 - 0.5 * np.cos(np.pi * np.clip((t - 0.02) / 0.11, 0, 1))
        tail = lo_part * build + hi_part * hi_build
        sos = signal.butter(4, hi_cut, btype='lowpass', fs=SR, output='sos')
        tail = signal.sosfiltfilt(sos, tail)
        tail = bw(tail, 'highpass', lo_cut, 2)
        tail /= np.sqrt(np.sum(tail ** 2)) + 1e-12
        ir = np.zeros(n)
        ir[pre:] = tail
        # early reflections: 12 smooth pulses (Hann 1.2 ms, low-passed) between 6 and 75 ms
        er = np.zeros(n)
        pw = ns(0.0012)
        pulse = np.hanning(pw + 2)[1:-1]
        times = np.sort(rr.uniform(0.006, 0.075, 12))
        for k, tk in enumerate(times):
            a = ns(tk)
            if a + pw < n:
                er[a:a + pw] += rr.choice([-1, 1]) * 0.85 ** k * pulse
        er = signal.sosfiltfilt(signal.butter(4, 4500, btype='lowpass', fs=SR, output='sos'), er)
        er *= 0.45 / (np.sqrt(np.sum(er ** 2)) + 1e-12)
        ir += er
        ir *= ramp_out(n, 200)
        chans.append(ir / (np.sqrt(np.sum(ir ** 2)) + 1e-12))
    return np.stack(chans)


def true_peak_env(x, os_factor=4, circular=False):
    """Per-sample true-peak envelope (max |x| over the oversampled points around each sample),
    max over channels."""
    x = np.atleast_2d(x)
    n = x.shape[-1]
    pad = 256
    if circular:
        xx = np.concatenate([x[:, -pad:], x, x[:, :pad]], axis=1)
    else:
        xx = np.pad(x, ((0, 0), (pad, pad)))
    up = signal.resample_poly(xx, os_factor, 1, axis=1)
    up = np.abs(up).max(axis=0)
    up = up[pad * os_factor:(pad + n) * os_factor].reshape(n, os_factor).max(axis=1)
    return np.maximum(up, np.abs(x).max(axis=0))


def soft_limit_tp(x, ceiling_db=-1.0, circular=False, lookahead_ms=5.0, release_ms=120.0, block=16):
    """Clean, transparent look-ahead peak limiter driven by the 4x-oversampled (true-peak)
    envelope. Gain only ever goes down ahead of a peak (min-filter over the look-ahead,
    then a matching moving average so the gain ramps smoothly) and recovers with a smooth
    exponential release. No saturation, no clipping. Loops are processed circularly."""
    c = db2a(ceiling_db)
    x = np.atleast_2d(np.asarray(x, float)).copy()
    for _ in range(4):
        tp = true_peak_env(x, circular=circular)
        if tp.max() <= c:
            break
        need = np.minimum(1.0, c * 0.995 / np.maximum(tp, 1e-12))
        n = need.size
        nb = -(-n // block)
        nd = np.pad(need, (0, nb * block - n), constant_values=1.0).reshape(nb, block).min(axis=1)
        h = max(1, int(np.ceil(ns(lookahead_ms / 1000) / block)))
        mode = 'wrap' if circular else 'nearest'
        g0 = minimum_filter1d(nd, size=2 * h + 1, mode=mode)
        a = np.exp(-block / (release_ms / 1000 * SR))
        seq = np.concatenate([g0, g0]) if circular else g0
        g = np.empty_like(seq)
        prev = 1.0
        for i, v in enumerate(seq):           # instant down (already looked-ahead), smooth up
            prev = min(v, 1.0 - (1.0 - prev) * a)
            g[i] = prev
        g = g[nb:] if circular else g
        g = uniform_filter1d(g, size=2 * h + 1, mode=mode)
        gs = np.repeat(g, block)[:n]
        gs = uniform_filter1d(gs, size=block, mode=mode)
        x = x * gs
    return x


def master_clean(x, loop, target_lufs, tp_db=-1.0, hp=28.0, guard_lp=16000.0, margin_db=0.5):
    """Clean master: subsonic/DC high-pass, gentle ultrasonic guard, loudness to target,
    soft true-peak limiter at (tp_db - margin_db) so the decoded OGG stays <= tp_db.
    Floating point throughout; no saturation, no noise, no modulation."""
    x = bw(np.asarray(x, float), 'highpass', hp, 2, circular=loop)
    if guard_lp:
        x = bw(x, 'lowpass', guard_lp, 2, circular=loop)
    if loop:
        x = x - x.mean(axis=-1, keepdims=True)          # exact DC removal (loop period)
    for _ in range(4):
        x = x * db2a(target_lufs - lufs(x))
        x = soft_limit_tp(x, tp_db - margin_db, circular=loop)
        if abs(lufs(x) - target_lufs) < 0.15:
            break
    return x


def convolve(x, ir, out_len=None):
    """x mono or (2, n); ir (2, m). mono x -> stereo out. Output length out_len or len(x)."""
    out_len = out_len or x.shape[-1]
    if x.ndim == 1:
        xs = [x, x]
    else:
        xs = [x[0], x[1]]
    out = []
    for c in range(2):
        y = signal.oaconvolve(xs[c], ir[c % ir.shape[0]], mode='full')
        out.append(pad_to(y, out_len))
    return np.stack(out)


def convolve_circular(x, ir):
    """Exact circular convolution for a loop period x (2, n) with ir (2, m), m < n."""
    n = x.shape[-1]
    out = []
    for c in range(2):
        y = signal.oaconvolve(x[c], ir[c % ir.shape[0]], mode='full')
        z = y[:n].copy()
        rest = y[n:]
        while rest.size:
            k = min(n, rest.size)
            z[:k] += rest[:k]
            rest = rest[k:]
        out.append(z)
    return np.stack(out)


def wrap_tail(buf, L):
    """Fold everything after L samples back onto the start (seamless loop)."""
    out = buf[..., :L].copy()
    rest = buf[..., L:]
    while rest.shape[-1]:
        k = min(L, rest.shape[-1])
        out[..., :k] += rest[..., :k]
        rest = rest[..., k:]
    return out


def early_reflections(x, taps, lp=None):
    """taps: list of (delay_s, gain, lowpass_hz or None). mono in/out (length kept + tail)."""
    n = x.shape[-1]
    maxd = max(ns(d) for d, _, _ in taps)
    y = np.concatenate([x, np.zeros(maxd + 1)])
    out = y.copy()
    for d, g, f in taps:
        z = np.zeros_like(y)
        k = ns(d)
        z[k:] = y[:len(y) - k] * g
        if f:
            z = bw(z, 'lowpass', f, 2)
        out += z
    return out


# ---------------------------------------------------------------- loudness (ITU-R BS.1770-4)
_K1 = ([1.53512485958697, -2.69169618940638, 1.19839281085285], [1.0, -1.69065929318241, 0.73248077421585])
_K2 = ([1.0, -2.0, 1.0], [1.0, -1.99004745483398, 0.99007225036621])


def k_weight(x):
    x = np.atleast_2d(x)
    return signal.lfilter(*_K2, signal.lfilter(*_K1, x, axis=-1), axis=-1)


def _block_powers(x, block, hop):
    z = k_weight(x) ** 2
    n = z.shape[-1]
    if n < block:
        z = pad_to(z, block)
        n = block
    cs = np.concatenate([np.zeros((z.shape[0], 1)), np.cumsum(z, axis=-1)], axis=-1)
    starts = np.arange(0, n - block + 1, hop)
    p = (cs[:, starts + block] - cs[:, starts]) / block
    return p.sum(axis=0)


def lufs(x):
    """Integrated loudness (gated). Mono files are measured as a single channel."""
    p = _block_powers(x, ns(0.4), ns(0.1))
    l = -0.691 + 10 * np.log10(np.maximum(p, 1e-20))
    g1 = p[l > -70]
    if not g1.size:
        return -99.0
    rel = -0.691 + 10 * np.log10(g1.mean()) - 10
    g2 = p[(l > -70) & (l > rel)]
    return float(-0.691 + 10 * np.log10(g2.mean()))


def momentary_max(x):
    p = _block_powers(x, ns(0.4), ns(0.01))
    return float(-0.691 + 10 * np.log10(max(p.max(), 1e-20)))


def short_max(x, win=0.1):
    """Max K-weighted loudness over a short window (default 100 ms) - better for tiny SFX."""
    p = _block_powers(x, ns(win), ns(0.005))
    return float(-0.691 + 10 * np.log10(max(p.max(), 1e-20)))


def rms_db(x):
    return float(a2db(np.sqrt(np.mean(np.asarray(x) ** 2))))


def normalize_lufs(x, target, ceiling_db=-1.5, circular=False):
    g = db2a(target - lufs(x))
    y = x * g
    c = db2a(ceiling_db)
    if peak(y) > c:
        y = limiter(y, c * 0.999, circular=circular)
    return y


# ---------------------------------------------------------------- IO
def write_wav16(path, x):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    x = np.clip(np.asarray(x, float), -1.0, 32767 / 32768)
    i16 = np.round(x * 32767.0).astype('<i2')
    if i16.ndim == 2:
        i16 = i16.T
    wavfile.write(path, SR, i16)


def read_wav(path):
    sr, d = wavfile.read(path)
    if d.dtype == np.int16:
        x = d.astype(float) / 32768.0
    elif d.dtype == np.int32:
        x = d.astype(float) / 2147483648.0
    else:
        x = d.astype(float)
    if x.ndim == 2:
        x = x.T
    return sr, x


def write_ogg(path, x, q=8):
    """Stereo float (32-bit, no dither) -> Ogg Vorbis 48 kHz, quality 8 via ffmpeg/libvorbis (bit-exact flags for reproducibility)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    x = np.atleast_2d(x)
    if x.shape[0] == 1:
        x = np.vstack([x, x])
    with tempfile.TemporaryDirectory() as td:
        tmp = os.path.join(td, 'in.wav')
        wavfile.write(tmp, SR, np.ascontiguousarray(x.T.astype(np.float32)))
        cmd = ['ffmpeg', '-nostdin', '-y', '-hide_banner', '-loglevel', 'error',
               '-fflags', '+bitexact', '-i', tmp, '-map_metadata', '-1',
               '-c:a', 'libvorbis', '-q:a', str(q), '-ar', str(SR), '-ac', '2',
               '-flags:a', '+bitexact', '-fflags', '+bitexact', path]
        subprocess.run(cmd, check=True)


def decode(path):
    """Decode any audio file with ffmpeg to float (ch, n) at its native rate."""
    probe = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'a:0', '-show_entries',
                            'stream=sample_rate,channels', '-of', 'csv=p=0', path],
                           capture_output=True, text=True, check=True).stdout.strip().split(',')
    sr, ch = int(probe[0]), int(probe[1])
    raw = subprocess.run(['ffmpeg', '-nostdin', '-v', 'error', '-i', path, '-f', 'f32le',
                          '-acodec', 'pcm_f32le', '-'], capture_output=True, check=True).stdout
    x = np.frombuffer(raw, dtype='<f4').astype(float).reshape(-1, ch).T
    return sr, x


# ---------------------------------------------------------------- SFX finishing
def finalize_sfx(x, peak_db=-1.0, loud_target=None, tail_db=-62.0, fade_ms=25.0,
                 onset_db=-36.0, max_len=None, hp=25.0):
    """Mono SFX mastering: DC-safe highpass, trim so the onset is at sample 0, optional
    limiting toward a short-term loudness target, tail trim + fade, peak normalise."""
    x = np.asarray(x, float)
    x = filt(x, 'hp', hp, 0.6)
    pk = peak(x)
    on = int(np.argmax(np.abs(x) >= pk * db2a(onset_db)))
    x = x[on:]
    k = min(len(x), 6)
    x[:k] *= np.linspace(0.35, 1.0, k)     # 6-sample micro fade, keeps sample-0 onset
    if max_len:
        x = x[:ns(max_len)]
    x = x / (peak(x) + 1e-12)
    if loud_target is not None:
        # raise short-term loudness by limiting peaks (never more than 9 dB of GR)
        for _ in range(12):
            cur = short_max(x, 0.1)
            if cur >= loud_target - 0.3:
                break
            over = min(loud_target - cur, 3.0)
            x = limiter(x, db2a(-over), hold_ms=4, smooth_ms=2)
            x = x / peak(x)
    env = uniform_filter1d(np.abs(x), size=ns(0.01))
    above = np.nonzero(env >= db2a(tail_db))[0]
    end = int(above[-1]) + ns(0.01) if above.size else len(x)
    x = x[:min(end, len(x))]
    x = x * ramp_out(len(x), fade_ms)
    x = x / peak(x) * db2a(peak_db)
    return x
