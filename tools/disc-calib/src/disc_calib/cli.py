from __future__ import annotations

import argparse
import sys
from pathlib import Path

import cv2

from .code import decode
from .geometry import DVD, Params
from .pattern import design_code, render


def cmd_pattern(args) -> int:
    spec = design_code(args.inner, args.outer)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(args.out), render(spec))
    args.out.with_suffix(".json").write_text(spec.to_json(), encoding="utf-8")
    print(f"wrote {args.out} and {args.out.with_suffix('.json')}")
    return 0


def cmd_code(args) -> int:
    generated = Params(DVD, args.inner, args.outer, args.sectors, args.channel_bit_nm)
    generated.validate()
    result = decode(args.photo, generated, design_code(args.inner, args.outer), cubic=args.cubic)
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "code.fit.json").write_text(result.to_json(), encoding="utf-8")
    print(f"quality: {result.quality}")
    if result.actual is not None:
        print(f"inner {result.actual.inner:.6f} mm  outer {result.actual.outer:.6f} mm")
    for note in result.notes:
        print(note)
    return 0 if result.quality != "poor" else 2


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="disc-calib")
    commands = parser.add_subparsers(dest="command", required=True)

    pattern = commands.add_parser("pattern")
    pattern.add_argument("--inner", type=float, default=24.0)
    pattern.add_argument("--outer", type=float, default=58.0)
    pattern.add_argument("--out", type=Path, default=Path("target_code.png"))
    pattern.set_defaults(handler=cmd_pattern)

    code = commands.add_parser("code")
    code.add_argument("--photo", type=Path, nargs="+", required=True)
    code.add_argument("--out", type=Path, default=Path("out"))
    code.add_argument("--inner", type=float, required=True)
    code.add_argument("--outer", type=float, required=True)
    code.add_argument("--sectors", type=int, required=True)
    code.add_argument("--channel-bit-nm", type=float, default=133.3)
    code.add_argument("--cubic", action="store_true")
    code.set_defaults(handler=cmd_code)
    return parser


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    return args.handler(args)


if __name__ == "__main__":
    sys.exit(main())
