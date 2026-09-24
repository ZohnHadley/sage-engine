# -*- coding: utf-8 -*-
"""The Sandbox's placeholder sounds (docs/design/11, TODO F4).

The same bargain as `make_placeholder_art.py`: the repository ships no audio it does not own, so the
sounds are *generated* — a few hundred bytes of arithmetic each, deliberately crude, and enough to
prove the pipeline end to end. Every one is a 22.05 kHz mono 16-bit WAV, which is what
`SoundEffect.FromStream` reads (05 §3.2).

Run:  python games/Sandbox/tools/make_placeholder_audio.py
"""
import math
import os
import random
import struct
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

    # Items and screens.
    write('pickup', tone(660, 0.18, release=0.09, harmonics=(1.0, 0.5, 0.25)))
    write('ui_move', tone(880, 0.05, release=0.03))

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


if __name__ == '__main__':
    main()
