# -*- coding: utf-8 -*-
"""The Sandbox's placeholder sounds (docs/design/11, TODO F4).

The same bargain as `make_placeholder_art.py`: the repository ships no audio it does not own, so the
sounds are *generated* — a few hundred bytes of arithmetic each, deliberately crude, and enough to
prove the pipeline end to end. Every one is a 22.05 kHz mono 16-bit WAV, which is what
`SoundEffect.FromStream` reads (05 §3.2).

Music (issue #325) is made the same way and encoded to Ogg Vorbis with ffmpeg, because music streams and
only Ogg Vorbis streams: a few seconds each at the lowest quality, a dozen kilobytes or so.

Run:  python games/Sandbox/tools/make_placeholder_audio.py   (ffmpeg with libvorbis on the PATH for the music)
"""
import math
import os
import random
import shutil
import struct
import subprocess
import tempfile
import wave

RATE = 22050
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'content', 'audio')


def write(name, samples):
    """One mono 16-bit WAV, clipped and written."""
    path = os.path.join(OUT, name + '.wav')
    with wave.open(path, 'wb') as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        frames = bytearray()
        for s in samples:
            v = int(max(-1.0, min(1.0, s)) * 32000)
            frames += struct.pack('<h', v)
        f.writeframes(bytes(frames))
    print('%-22s %5.2fs  %6d bytes' % (name, len(samples) / float(RATE), len(samples) * 2))


def write_ogg(name, samples):
    """One mono Ogg Vorbis file in content/music, through a temporary WAV and ffmpeg."""
    folder = os.path.join(OUT, '..', 'music')
    if not os.path.isdir(folder):
        os.makedirs(folder)
    if shutil.which('ffmpeg') is None:
        print('%-22s skipped: no ffmpeg' % name)
        return
    with tempfile.TemporaryDirectory() as temp:
        wav = os.path.join(temp, name + '.wav')
        with wave.open(wav, 'wb') as f:
            f.setnchannels(1)
            f.setsampwidth(2)
            f.setframerate(RATE)
            f.writeframes(b''.join(struct.pack('<h', int(max(-1.0, min(1.0, v)) * 32000)) for v in samples))
        path = os.path.join(folder, name + '.ogg')
        subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', wav, '-c:a', 'libvorbis', '-q:a', '0',
                        '-map_metadata', '-1', '-fflags', '+bitexact', '-flags:a', '+bitexact', path], check=True)
    print('%-22s %5.2fs  %6d bytes' % (name + '.ogg', len(samples) / float(RATE), os.path.getsize(path)))


def pad(chords, seconds, swell=4.0, level=0.35):
    """A soft pad: each chord a set of whole-hertz sines (so a loop of whole seconds joins without a click),
    one chord to a span, with a slow swell whose period divides the loop."""
    n = int(seconds * RATE)
    span = n // len(chords)
    out = []
    for i in range(n):
        t = i / float(RATE)
        chord = chords[min(i // span, len(chords) - 1)]
        # Cross-fade the chords over a tenth of a second either side of a change.
        edge = (i % span) / float(RATE)
        v = sum(math.sin(2 * math.pi * f * t) for f in chord) / len(chord)
        if edge < 0.1 and i >= span:
            prev = chords[(i // span) - 1]
            a = edge / 0.1
            v = v * a + (1 - a) * sum(math.sin(2 * math.pi * f * t) for f in prev) / len(prev)
        elif edge < 0.1:
            last = chords[-1]
            a = edge / 0.1
            v = v * a + (1 - a) * sum(math.sin(2 * math.pi * f * t) for f in last) / len(last)
        out.append(v * level * (0.8 + 0.2 * math.sin(2 * math.pi * t / swell)))
    return out


def pulse(seconds, every=0.5, freq=60.0, level=0.6):
    """A soft drum on every beat: the layer that comes in when things get tense."""
    n = int(seconds * RATE)
    beat = int(every * RATE)
    out = []
    for i in range(n):
        t = (i % beat) / float(RATE)
        out.append(math.sin(2 * math.pi * freq * math.exp(-t * 5) * t) * math.exp(-t * 9) * level)
    return out


def envelope(i, n, attack=0.01, release=0.3):
    """Percussive by default: a fast attack and an exponential tail."""
    t = i / float(RATE)
    total = n / float(RATE)
    if t < attack:
        return t / attack
    return math.exp(-(t - attack) / max(release, 1e-4))


def tone(freq, seconds, release=0.3, harmonics=(1.0,), noise=0.0, sweep=0.0):
    n = int(seconds * RATE)
    out = []
    for i in range(n):
        t = i / float(RATE)
        f = freq * (1.0 + sweep * t)
        v = 0.0
        for k, amp in enumerate(harmonics, start=1):
            v += amp * math.sin(2 * math.pi * f * k * t)
        if noise:
            v += noise * (random.random() * 2 - 1)
        out.append(v * envelope(i, n, release=release) / (sum(harmonics) + noise + 1e-6))
    return out


def thud(seconds=0.25, freq=90.0):
    """A low, short body hit: a falling sine with a click of noise on the front."""
    n = int(seconds * RATE)
    out = []
    for i in range(n):
        t = i / float(RATE)
        f = freq * math.exp(-t * 6)
        click = (random.random() * 2 - 1) * math.exp(-t * 90) * 0.6
        out.append((math.sin(2 * math.pi * f * t) + click) * envelope(i, n, release=0.09))
    return out


def whoosh(seconds=0.3):
    """Filtered noise that rises and falls: a swing through the air."""
    n = int(seconds * RATE)
    out = []
    last = 0.0
    for i in range(n):
        t = i / float(RATE)
        white = random.random() * 2 - 1
        last = last * 0.82 + white * 0.18          # one-pole low pass, so it hisses rather than spits
        shape = math.sin(math.pi * min(t / seconds, 1.0))
        out.append(last * shape * 0.9)
    return out


def rustle(seconds, cutoff):
    """A footfall in grass: a short burst of low-passed noise, quick in and quick out."""
    n = int(seconds * RATE)
    out = []
    last = 0.0
    for i in range(n):
        last = last * (1 - cutoff) + (random.random() * 2 - 1) * cutoff
        out.append(last * 1.6 * envelope(i, n, attack=0.004, release=seconds / 4))
    return out


def knock(seconds, freq):
    """A heel on a plank: a damped resonance with a click of noise on the front."""
    n = int(seconds * RATE)
    out = []
    for i in range(n):
        t = i / float(RATE)
        click = (random.random() * 2 - 1) * math.exp(-t * 160) * 0.5
        body = math.sin(2 * math.pi * freq * t) * 0.6 + math.sin(2 * math.pi * freq * 2.7 * t) * 0.25
        out.append((body + click) * envelope(i, n, attack=0.002, release=0.035))
    return out


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)
    random.seed(7)     # the same placeholders every run, so a diff means somebody changed this file

    # Melee: a swing through air, and two variations of a hit so repeats do not sound identical.
    write('swing', whoosh(0.28))
    write('hit_flesh_a', thud(0.22, 95))
    write('hit_flesh_b', thud(0.24, 80))

    # Magic: a fireball leaving the hand, and the burst when it lands.
    write('cast_fire', tone(220, 0.45, release=0.18, harmonics=(1.0, 0.4, 0.2), noise=0.25, sweep=1.6))
    write('boom_fire', tone(70, 0.7, release=0.35, harmonics=(1.0, 0.5), noise=0.7, sweep=-0.4))

    # Thunder: a long low rumble (the weather's lightning cue, issue #311).
    write('thunder', tone(48, 2.6, release=1.9, harmonics=(1.0, 0.6, 0.3), noise=0.85, sweep=-0.35))

    # Items.
    write('pickup', tone(660, 0.18, release=0.09, harmonics=(1.0, 0.5, 0.25)))

    # Rain: filtered noise with a little variation, so a loop does not sound like a fan. Cross-faded at
    # the seam like the campfire, because a click every 2.4 seconds is worse than no rain at all.
    n = int(2.4 * RATE)
    rain = []
    last = 0.0
    for i in range(n):
        white = random.random() * 2 - 1
        last = last * 0.55 + white * 0.45          # brighter than the fire: rain hisses, fire rumbles
        swell = 0.85 + 0.15 * math.sin(2 * math.pi * i / RATE * 0.23)
        rain.append(last * 0.42 * swell)
    fade = int(0.15 * RATE)
    for i in range(fade):
        a = i / float(fade)
        rain[i] = rain[i] * a + rain[n - fade + i] * (1 - a)
    write('rain_loop', rain[:n - fade])

    # Something for a looping source to hum: a campfire.
    n = int(1.5 * RATE)
    fire = []
    last = 0.0
    for i in range(n):
        white = random.random() * 2 - 1
        last = last * 0.93 + white * 0.07
        crackle = (random.random() * 2 - 1) * (0.5 if random.random() < 0.002 else 0.0)
        fire.append((last * 0.5 + crackle) * 0.7)
    # A loop must not click where it joins: cross-fade the last tenth over the first.
    fade = int(0.1 * RATE)
    for i in range(fade):
        a = i / float(fade)
        fire[i] = fire[i] * a + fire[n - fade + i] * (1 - a)
    write('fire_loop', fire[:n - fade])

    # Screens (issue #330; `ui_sounds` in content/data/ui.json): a short dry tick as focus moves and a
    # higher two-partial click when a choice is made. No noise term, so adding them cannot shift the
    # random sequence the sounds above were generated from.
    write('ui_move', tone(520, 0.05, release=0.02, harmonics=(1.0, 0.3)))
    write('ui_select', tone(880, 0.1, release=0.04, harmonics=(1.0, 0.5, 0.2)))
    # Music (issue #325). `wander` is the clearing's: a one-second intro played once, then a four-second body
    # that loops from sample 22050 (its `loopStart`), with a drum layer heard at intensity 0.5 and above.
    # `hearth` is the hut yard's, switched to by a trigger's output; `crypt` is the crypt scene's.
    intro = [v * (i / float(RATE)) for i, v in enumerate(pad([(220, 277, 330)], 1.0))]
    write_ogg('wander', intro + pad([(220, 277, 330), (196, 247, 294)], 4.0))
    write_ogg('wander_drums', [0.0] * RATE + pulse(4.0))
    write_ogg('hearth', pad([(262, 330, 392), (220, 262, 330)], 4.0, level=0.3))
    write_ogg('crypt', pad([(55, 82, 110), (52, 78, 104)], 4.0, swell=2.0, level=0.45))
    # Footsteps (engine issue #327): what a foot sounds like on the Sandbox's two grounds, three of each
    # so a walk is not one sample on repeat (the sound record picks one, with jitter), plus a landing on
    # each and a push off for a jump. Written after the rest, so the seeded noise above stays as it was.
    for name, seconds, cutoff in [('step_grass_a', 0.16, 0.30), ('step_grass_b', 0.18, 0.26), ('step_grass_c', 0.15, 0.34)]:
        write(name, rustle(seconds, cutoff))
    for name, freq in [('step_wood_a', 210.0), ('step_wood_b', 185.0), ('step_wood_c', 235.0)]:
        write(name, knock(0.14, freq))
    write('land_grass', [a + b for a, b in zip(rustle(0.28, 0.22), thud(0.28, 70))])
    write('land_wood', [a * 0.6 + b for a, b in zip(knock(0.3, 140.0), thud(0.3, 85))])
    write('jump', rustle(0.12, 0.4))


if __name__ == '__main__':
    main()
