"""build_all.py - regenerate every Paw Noon audio asset (SPEC 4.4).

    python3 Tools/AudioGen/build_all.py

Writes into Assets/PawNoon/Resources/Audio/{Music,Music/Classic,Loops,SFX} and NOTICE.txt.
Requires numpy, scipy and ffmpeg with libvorbis on PATH. Deterministic (seeded); OGG output
is byte-identical on the same ffmpeg/libvorbis build.
The Classic MP3s are copied byte-for-byte from ../work/audio_in/Music when that folder exists
(on other machines the already-delivered copies are left untouched).
"""
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gen_duel    # noqa: E402
import gen_loops   # noqa: E402
import gen_music   # noqa: E402
import gen_sfx     # noqa: E402
import gen_peacemaker  # noqa: E402

AUDIO = os.path.normpath(os.path.join(HERE, '..', '..', 'Assets', 'PawNoon', 'Resources', 'Audio'))
USER_IN = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'work', 'audio_in', 'Music'))

NOTICE = """PAW NOON - audio credits and licenses
=====================================

Gunshots: SFX/sfx_gun_hero_1..3.wav, SFX/sfx_gun_bandit_1..3.wav, SFX/sfx_gun_peacemaker_1..3.wav
  Derived from The Free Firearm Sound Library (Smith & Wesson 642 recordings
  V_22P.wav and V_27P.wav, as prepared in Revolver1-3.wav).
  Recorded by Ben Jaszczak, Brian Nelson, Kevin Heras and Matthew Nanney.
  Source: https://opengameart.org/content/the-free-firearm-sound-library
  License: CC0 1.0 Universal (public domain dedication)
           https://creativecommons.org/publicdomain/zero/1.0/
  Project edits: onset trim, re-pitch, EQ, transient shaping, synthesized sub-thump,
  saturation (hero); band-limiting and early reflections (bandit); both followed by
  synthesized canyon echoes (discrete, progressively darker slapbacks + synthetic
  reverb); limiting and -1 dBFS peak normalisation.
  The prepared source files are kept in Tools/AudioGen/source/.

Classic music: Music/Classic/Lobby.mp3, Music/Classic/Standoff.mp3
  Supplied by the user and copied without modification. No authorship or
  third-party license is inferred; the user is responsible for their rights.

Everything else - Music/music_title, music_standoff, music_duel_a, music_duel_hold,
music_duel_result, sting_win, sting_lose, sting_round; Loops/amb_wind, amb_town, amb_duel, heartbeat; and every other
SFX/*.wav - is original to the Paw Noon project: composed and synthesized from
scratch (Karplus-Strong guitars, additive whistle/harmonica/bowed voices, band-limited
sawtooth strings, bells,
filtered noise, synthetic convolution reverb) by Tools/AudioGen/*.py. No samples
or third-party recordings are used in them.
"""


def main():
    gen_music.main()
    gen_duel.main(['music_duel_a', 'music_duel_hold', 'music_duel_result'])
    gen_loops.main()
    gen_sfx.main()
    gen_peacemaker.main()
    classic = os.path.join(AUDIO, 'Music', 'Classic')
    os.makedirs(classic, exist_ok=True)
    for f in ('Lobby.mp3', 'Standoff.mp3'):
        src = os.path.join(USER_IN, f)
        if os.path.exists(src):
            shutil.copyfile(src, os.path.join(classic, f))
        else:
            print('note: user MP3 source not found, keeping delivered copy:', f)
    with open(os.path.join(AUDIO, 'NOTICE.txt'), 'w', newline='\n') as fh:
        fh.write(NOTICE)
    print('done ->', AUDIO)


if __name__ == '__main__':
    main()
