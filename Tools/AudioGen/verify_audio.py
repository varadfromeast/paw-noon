"""verify_audio.py - measure every delivered audio file and render spectrograms.

    python3 Tools/AudioGen/verify_audio.py [out_dir]   (default ../work/audio)

Checks: format, duration, sample/true-ish peak, RMS, integrated LUFS, max short-term (100 ms)
loudness, DC offset, clipping, SFX onset at sample 0, and loop seam continuity on decoded OGGs.
Noise-floor check (music + loops): median spectral level in the 6-16 kHz band during the quietest
10 % of STFT frames; music_* files must be below NOISE_FLOOR_MAX_DB (-70 dBFS) and every music/loop
file must have a 4x-oversampled true peak <= -1.0 dBTP.
Writes metrics.json and spectrogram PNGs. Exit status 1 if any music/loop check fails.
"""
import json
import os
import sys

import numpy as np
from scipy.signal import resample_poly

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from pn_dsp import (SR, decode, lufs, short_max, a2db, peak, bw, read_wav)  # noqa

AUDIO = os.path.normpath(os.path.join(HERE, '..', '..', 'Assets', 'PawNoon', 'Resources', 'Audio'))
NOISE_FLOOR_MAX_DB = -70.0
TP_MAX_DB = -1.0
LOOPING = {'music_title', 'music_standoff', 'music_duel_a', 'music_duel_hold', 'amb_wind', 'amb_duel', 'amb_town', 'heartbeat', 'tumbleweed_roll'}


def seam(x):
    """Compare the 50 ms around the loop point with typical sample-to-sample motion."""
    w = int(0.05 * SR)
    jump = np.max(np.abs(x[:, 0] - x[:, -1]))
    d = np.abs(np.diff(x, axis=1))
    typ = np.percentile(d, 99.9)
    joined = np.concatenate([x[:, -w:], x[:, :w]], axis=1)
    hp = np.abs(bw(joined, 'highpass', 7000, 4))
    mid = hp[:, w - 96:w + 96].max()
    ref = np.percentile(hp[:, :w - 200], 99.5) + 1e-9
    rms_l = a2db(np.sqrt(np.mean(x[:, -w:] ** 2)))
    rms_f = a2db(np.sqrt(np.mean(x[:, :w] ** 2)))
    return {'jump_vs_p999_step': float(jump / (typ + 1e-12)), 'hf_seam_vs_p995': float(mid / ref),
            'rms_last50_db': float(rms_l), 'rms_first50_db': float(rms_f)}


def noise_floor(x, sr, band=(6000.0, 16000.0), quiet_frac=0.10, nfft=2048, hop=512):
    """Noise floor between notes, read off the spectrogram.

    STFT of the mono sum (Hann, 2048 pt = 23.4 Hz bins at 48 kHz, hop 512). Levels are dBFS per bin,
    calibrated so a full-scale sine reads 0 dBFS. Frames are ranked by broadband power; in the
    quietest `quiet_frac` of them, the median bin level inside `band` is the noise floor.
    Also returns the band-integrated power in those frames (a stricter figure: all the
    6-16 kHz energy summed, median over the quiet frames)."""
    mono = np.asarray(x, float).mean(axis=0)
    win = np.hanning(nfft)
    # amplitude-spectrum calibration: 2|X| / sum(win) -> a full-scale sine reads 1 (0 dBFS)
    f = np.fft.rfftfreq(nfft, 1 / sr)
    X = np.abs(np.fft.rfft(np.lib.stride_tricks.sliding_window_view(mono, nfft)[::hop] * win, axis=1))
    A = 2.0 * X / win.sum()
    P = A ** 2                                           # (frames, bins), FS sine -> 1
    frame_db = 10 * np.log10(P.sum(axis=1) + 1e-30)
    k = max(1, int(round(quiet_frac * len(frame_db))))
    quiet = np.argsort(frame_db)[:k]
    sel = (f >= band[0]) & (f < band[1])
    lv = 10 * np.log10(P[np.ix_(quiet, sel)] + 1e-30)
    band_pow = 10 * np.log10(P[np.ix_(quiet, sel)].sum(axis=1) / 1.5 + 1e-30)   # /1.5: Hann ENBW
    return {'nf_median_bin_dbfs': round(float(np.median(lv)), 1),
            'nf_band_int_dbfs': round(float(np.median(band_pow)), 1),
            'nf_quiet_frames': int(k), 'nf_quiet_frame_level_dbfs': round(float(np.median(frame_db[quiet])), 1)}


def measure(path):
    name = os.path.splitext(os.path.basename(path))[0]
    if path.endswith('.wav'):
        sr, x = read_wav(path)
        x = np.atleast_2d(x)
        import wave
        with wave.open(path) as w:
            fmt = f'WAV {w.getframerate()} Hz {8 * w.getsampwidth()}-bit {w.getnchannels()}ch'
    else:
        sr, x = decode(path)
        fmt = f'{os.path.splitext(path)[1][1:].upper()} {sr} Hz {x.shape[0]}ch'
    m = {'file': os.path.relpath(path, AUDIO), 'name': name, 'format': fmt,
         'duration_s': round(x.shape[1] / sr, 3), 'samples': int(x.shape[1])}
    pk = peak(x)
    tp = peak(resample_poly(x, 4, 1, axis=1))
    m.update(peak_dbfs=round(float(a2db(pk)), 2), truepeak_dbfs=round(float(a2db(tp)), 2),
             rms_dbfs=round(float(a2db(np.sqrt(np.mean(x ** 2)))), 2),
             dc=float(np.max(np.abs(x.mean(axis=1)))), clipped=int(np.sum(np.abs(x) >= 0.9999)))
    if sr == SR:
        m['lufs_i'] = round(lufs(x), 1)
        m['st100_max'] = round(short_max(x), 1)
    if name.startswith('sfx_'):
        a = np.max(np.abs(x), axis=0)
        m['onset_ms'] = round(1000 * int(np.argmax(a >= pk * 10 ** (-20 / 20))) / sr, 2)
        m['first_sample'] = round(float(a[0]), 4)
        m['peak_at_ms'] = round(1000 * int(np.argmax(a)) / sr, 1)
        m['end_abs'] = round(float(a[-1]), 5)
    if name in LOOPING:
        m['seam'] = seam(x)
    if name.startswith(('music_', 'sting_')) or os.sep + 'Loops' + os.sep in path:
        if 'Classic' not in path:
            m.update(noise_floor(x, sr))
            fails = []
            if name.startswith('music_') and m['nf_median_bin_dbfs'] >= NOISE_FLOOR_MAX_DB:
                fails.append(f"noise floor {m['nf_median_bin_dbfs']} dBFS >= {NOISE_FLOOR_MAX_DB}")
            if m['truepeak_dbfs'] > TP_MAX_DB:
                fails.append(f"true peak {m['truepeak_dbfs']} dBTP > {TP_MAX_DB}")
            if m['dc'] > 1e-3:
                fails.append(f"DC {m['dc']:.5f}")
            if m['clipped']:
                fails.append(f"{m['clipped']} clipped samples")
            m['checks'] = 'PASS' if not fails else 'FAIL: ' + '; '.join(fails)
    if x.shape[0] == 2 and sr == SR and x.shape[1] > SR:
        mono = x.mean(axis=0, keepdims=True)
        m['mono_lufs_delta'] = round(lufs(mono) - lufs(x) + 3.01, 2)   # 0 = fully mono-compatible
        m['lr_corr'] = round(float(np.corrcoef(x[0], x[1])[0, 1]), 3)
        spec = np.abs(np.fft.rfft(x.mean(axis=0))) ** 2
        f = np.fft.rfftfreq(x.shape[1], 1 / sr)
        tot = spec[(f > 20) & (f < 16000)].sum()
        m['band_2k4k_pct'] = round(100 * float(spec[(f >= 2000) & (f < 4000)].sum() / tot), 2)
    return m, x, sr


def spectrogram(x, sr, title, path, vmin=-100):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from scipy.signal import spectrogram as sg
    mono = x.mean(axis=0)
    nper = 4096 if mono.size > sr * 10 else 1024
    f, t, S = sg(mono, sr, nperseg=nper, noverlap=nper * 3 // 4, scaling='spectrum')
    S = 10 * np.log10(S + 1e-14)
    fig, ax = plt.subplots(2, 1, figsize=(11, 5.5), gridspec_kw={'height_ratios': [3, 1]}, sharex=True)
    ax[0].pcolormesh(t, np.maximum(f, 20), S, shading='auto', vmin=vmin, vmax=S.max(), cmap='magma')
    ax[0].set_yscale('log'); ax[0].set_ylim(30, sr / 2); ax[0].set_ylabel('Hz'); ax[0].set_title(title)
    tt = np.arange(mono.size) / sr
    ax[1].plot(tt, mono, lw=0.3, color='k'); ax[1].set_ylim(-1, 1); ax[1].set_xlabel('s')
    fig.tight_layout(); fig.savefig(path, dpi=80); plt.close(fig)


def main(out_dir, audio_root=None):
    os.makedirs(out_dir, exist_ok=True)
    files = []
    for root, _, fs in os.walk(audio_root or AUDIO):
        files += [os.path.join(root, f) for f in sorted(fs) if f.endswith(('.wav', '.ogg', '.mp3'))]
    files.sort()
    results = []
    spec_names = {'music_title', 'music_standoff', 'music_duel_a', 'music_duel_hold', 'music_duel_result',
                  'sfx_tick_sub', 'sting_win', 'sting_lose', 'sting_round', 'amb_wind',
                  'amb_town', 'heartbeat', 'tumbleweed_roll', 'sfx_draw', 'sfx_tick_3', 'sfx_tick_1',
                  'sfx_gun_hero_1', 'sfx_gun_bandit_1', 'sfx_hit_cat', 'sfx_lightning'}
    for p in files:
        m, x, sr = measure(p)
        results.append(m)
        if m['name'] in spec_names:
            spectrogram(x, sr, m['name'], os.path.join(out_dir, f"spec_{m['name']}.png"))
        print(json.dumps(m))
    with open(os.path.join(out_dir, 'metrics.json'), 'w') as fh:
        json.dump(results, fh, indent=1)
    bad = [m for m in results if m.get('checks', 'PASS') != 'PASS']
    print('\nnoise floor (6-16 kHz, quietest 10% of frames):')
    for m in results:
        if 'nf_median_bin_dbfs' in m:
            print(f"  {m['name']:<18} median bin {m['nf_median_bin_dbfs']:7.1f} dBFS   band-integrated "
                  f"{m['nf_band_int_dbfs']:7.1f} dBFS   TP {m['truepeak_dbfs']:6.2f} dBTP   {m['checks']}")
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, '..', '..', '..', 'work', 'audio'),
                  sys.argv[2] if len(sys.argv) > 2 else None))
