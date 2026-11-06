"""Generates the alarm sounds with the standard library, so Siren ships no third-party audio.

Run: python desktop/tools/make_sounds.py
"""

import math
import struct
import wave
from pathlib import Path

RATE = 22050
OUT = Path(__file__).resolve().parent.parent / "Siren" / "Sounds"


def write(name, samples):
    with wave.open(str(OUT / name), "wb") as file:
        file.setnchannels(1)
        file.setsampwidth(2)
        file.setframerate(RATE)
        file.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s)) * 30000)) for s in samples))


def bell(frequency, t):
    """A soft struck tone: a fundamental, two quiet partials and a fast decay."""
    if t < 0:
        return 0.0
    attack = min(1.0, t / 0.004)
    tone = (math.sin(2 * math.pi * frequency * t)
            + 0.35 * math.sin(2 * math.pi * frequency * 2.01 * t) * math.exp(-t * 6)
            + 0.15 * math.sin(2 * math.pi * frequency * 3.02 * t) * math.exp(-t * 11))
    return attack * tone * math.exp(-t * 3.4) * 0.5


def chime():
    """Three rising notes, then silence. Loops every 2.4 s."""
    notes = ((0.00, 783.99), (0.16, 1046.50), (0.32, 1318.51))
    for i in range(int(RATE * 2.4)):
        t = i / RATE
        yield sum(bell(frequency, t - start) for start, frequency in notes)


def siren():
    """A two-tone wail whose phase stays continuous, so the loop has no click."""
    phase, length = 0.0, 1.2
    for i in range(int(RATE * length)):
        t = i / RATE
        frequency = 760 + 420 * math.sin(2 * math.pi * t / length - math.pi / 2)
        phase += 2 * math.pi * frequency / RATE
        # Odd harmonics give it an edge without clipping.
        yield 0.55 * (math.sin(phase) + 0.3 * math.sin(3 * phase) + 0.12 * math.sin(5 * phase))


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    write("chime.wav", chime())
    write("siren.wav", siren())
    print("wrote", OUT)
