"""make_title_preview.py - first 40 s of music_title as a 192 kbps MP3 preview.

    python3 Tools/AudioGen/make_title_preview.py [out.mp3]  (default ../work/share/paw_noon_title_clean.mp3)

Renders the title straight from the generator in floating point (no OGG generation loss),
fades the last 2 s, and encodes once with LAME at 192 kbps CBR.
"""
import os
import subprocess
import sys
import tempfile

import numpy as np
from scipy.io import wavfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from pn_dsp import SR, ns, ramp_out, soft_limit_tp, lufs, true_peak_env, a2db  # noqa
from gen_music import music_title  # noqa

SECS = 40.0


def main(out):
    x = music_title()[:, :ns(SECS)]
    x = x * ramp_out(x.shape[1], 2000)
    x = soft_limit_tp(x, -1.5)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with tempfile.TemporaryDirectory() as td:
        w = os.path.join(td, 'title.wav')
        wavfile.write(w, SR, np.ascontiguousarray(x.T.astype(np.float32)))
        subprocess.run(['ffmpeg', '-nostdin', '-y', '-v', 'error', '-i', w, '-c:a', 'libmp3lame',
                        '-b:a', '192k', '-map_metadata', '-1', out], check=True)
    print(out, f'lufs={lufs(x):.1f} tp={a2db(true_peak_env(x).max()):.2f} dBTP')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else
         os.path.normpath(os.path.join(HERE, '..', '..', '..', 'work', 'share', 'paw_noon_title_clean.mp3')))
