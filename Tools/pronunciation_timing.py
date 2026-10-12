#!/usr/bin/env python3
"""Write Packages/org.realitydeslab.body-oracle/Resources/PronunciationTiming.json: when each clip voices its pronunciation.

Each clip says its pronunciation, then a spoken "repeat" cue shared by every clip, then repeats the
pronunciation. Voiced segments are found from 10 ms energy windows; a segment counts as the
pronunciation when its spectrum matches the clip's most typical segment, otherwise as a cue.
"clipN" lists the pronunciations and "clipN/cue" the cues: KaraokeWipe sweeps the text once per
pronunciation and returns it to white during a cue. Requires macOS afconvert and numpy.
"""
import json
import re
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
RATE = 16000


def load(mp3: Path) -> np.ndarray:
    with tempfile.TemporaryDirectory() as directory:
        wav = Path(directory) / "clip.wav"
        subprocess.run(["afconvert", "-f", "WAVE", "-d", f"LEI16@{RATE}", "-c", "1", str(mp3), str(wav)], check=True)
        with wave.open(str(wav)) as reader:
            return np.frombuffer(reader.readframes(reader.getnframes()), dtype="<i2").astype(float) / 32768


def voiced(samples: np.ndarray, gap: float = 0.15, shortest: float = 0.08) -> list:
    window = RATE // 100
    energy = np.array([np.mean(samples[i:i + window] ** 2) for i in range(0, len(samples), window)])
    loud = energy > energy.max() * 0.01  # a tenth of the peak amplitude
    segments, start = [], None
    for index, on in enumerate(np.append(loud, False)):
        if on and start is None:
            start = index
        elif not on and start is not None:
            if segments and (start - segments[-1][1]) * 0.01 < gap:
                segments[-1][1] = index
            else:
                segments.append([start, index])
            start = None
    return [(a * 0.01, b * 0.01) for a, b in segments if (b - a) * 0.01 >= shortest]


def spectrum(samples: np.ndarray) -> np.ndarray:
    magnitude = np.abs(np.fft.rfft(samples * np.hanning(len(samples)), 4096))
    return magnitude / np.linalg.norm(magnitude)


def main() -> None:
    timing = {}
    clips = sorted((ROOT / "Packages/org.realitydeslab.body-oracle/Audios").glob("*.mp3"), key=lambda p: int(re.findall(r"\d+", p.stem)[0]))
    for clip in clips:
        samples = load(clip)
        segments = voiced(samples)
        spectra = [spectrum(samples[int(a * RATE):int(b * RATE)]) for a, b in segments]
        similarity = np.array([[s @ t for t in spectra] for s in spectra])
        typical = int(similarity.sum(axis=1).argmax())
        spoken = [[round(a, 2), round(b, 2)] for (a, b), s in zip(segments, similarity[typical]) if s > 0.9]
        others = [(a, b) for (a, b), s in zip(segments, similarity[typical]) if s <= 0.9]
        # The "repeat" cue: the first sound after the first pronunciation longer than 0.1 s (shorter
        # ones are syllable tails), with any fragment that follows it within 0.3 s.
        cues = []
        for a, b in others:
            if cues and a - cues[-1][1] < 0.3:
                cues[-1][1] = round(b, 2)
            elif not cues and spoken and a >= spoken[0][1] and b - a > 0.1:
                cues.append([round(a, 2), round(b, 2)])
        timing[clip.stem] = spoken
        timing[clip.stem + "/cue"] = cues
        print(f"{clip.stem}: spoken {spoken} cue {cues}")
    output = ROOT / "Packages/org.realitydeslab.body-oracle/Resources/PronunciationTiming.json"
    lines = [f"  {json.dumps(name)}: {json.dumps(spans)}" for name, spans in timing.items()]
    output.write_text("{\n" + ",\n".join(lines) + "\n}\n")
    print("Wrote", output.relative_to(ROOT))


if __name__ == "__main__":
    main()
