"""Smoke-test a published CLI from a working directory outside the checkout."""

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def check_package(executable: Path) -> None:
    fixture = Path(__file__).resolve().parents[1] / "src/DiscImageStudio.App/Assets/DiscImageStudio-256.png"
    with tempfile.TemporaryDirectory() as directory:
        work = Path(directory)
        shutil.copyfile(fixture, work / "input image.png")

        def run(*args: str) -> None:
            subprocess.run([executable, *args], cwd=work, check=True)

        def check_size(name: str, expected: int) -> None:
            actual = (work / name).stat().st_size
            if actual != expected:
                raise ValueError(f"{name}: expected {expected} bytes, found {actual}")

        run("--help")
        run("selftest")
        run("cd-generate", "--input", "input image.png", "--output", "track.wav", "--sectors", "2")
        check_size("track.wav", 44 + 2 * 2352)
        if 'FILE "track.wav" WAVE' not in (work / "track.cue").read_text(encoding="utf-8-sig"):
            raise ValueError("CUE does not reference the generated WAV")

        run("solve", "--image", "input image.png", "--iso-output", "dvd.iso",
            "--total-sectors", "160", "--fill-sectors", "160")
        check_size("dvd.iso", 160 * 2048)

        run("bd-generate", "--input", "input image.png", "--output", "bd.iso", "--total-sectors", "1024")
        check_size("bd.iso", 1024 * 2048)


if __name__ == "__main__":
    check_package(Path(sys.argv[1]).resolve(strict=True))
