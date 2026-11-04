"""Smoke-test a published CLI from a working directory outside the checkout."""

import json
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

ALLOWED_VALUE_TYPES = {
    "String",
    "InputFile",
    "OutputFile",
    "InputFolder",
    "Integer",
    "Decimal",
    "Boolean",
    "Choice",
}

REQUIRED_MODULES = {"cd-da", "dvd-data", "bluray-data"}
REQUIRED_COMMANDS = {"cd-generate", "solve", "bd-generate", "selftest"}

# Mirrors the C# DiscModuleCapabilities flag names so the snapshot cannot silently
# drift to a value the Swift client cannot interpret.
ALLOWED_CAPABILITIES = {
    "RawTrackGeneration",
    "DataImageGeneration",
    "GeometryPreview",
    "EncodedOutputPreview",
    "HybridFilesystem",
    "ErrorCorrectionEncoding",
}


def check_catalog(executable: Path, cwd: Path) -> None:
    completed = subprocess.run(
        [executable, "catalog", "--json"], cwd=cwd, check=True, capture_output=True)
    payload = completed.stdout
    if payload.startswith(b"\xef\xbb\xbf"):
        raise ValueError("catalog --json wrote a UTF-8 BOM")
    if not payload.endswith(b"\n"):
        raise ValueError("catalog --json did not end with a newline")

    catalog = json.loads(payload.decode("utf-8"))
    if catalog.get("schemaVersion") != 1:
        raise ValueError(f"catalog schemaVersion: {catalog.get('schemaVersion')!r}, expected 1")
    if not catalog.get("toolVersion"):
        raise ValueError("catalog toolVersion is missing or empty")

    modules = catalog.get("modules") or []
    module_ids = {module["id"] for module in modules}
    missing_modules = REQUIRED_MODULES - module_ids
    if missing_modules:
        raise ValueError(f"catalog is missing modules: {sorted(missing_modules)}")

    command_names = set()
    option_count = 0
    for module in modules:
        if module.get("family") not in {"CompactDisc", "Dvd", "BluRay"}:
            raise ValueError(f"{module['id']}: unexpected family {module.get('family')!r}")
        for command in module["commands"]:
            command_names.add(command["name"])
            capabilities = command.get("capabilities")
            if not isinstance(capabilities, list) or not all(
                    isinstance(name, str) for name in capabilities):
                raise ValueError(f"{command['name']}: capabilities must be a list of strings")
            if len(set(capabilities)) != len(capabilities):
                raise ValueError(f"{command['name']}: capabilities contains duplicates")
            unknown_capabilities = set(capabilities) - ALLOWED_CAPABILITIES
            if unknown_capabilities:
                raise ValueError(
                    f"{command['name']}: unknown capabilities {sorted(unknown_capabilities)}")
            for option in command["options"]:
                option_count += 1
                label = f"{command['name']} --{option.get('name')}"
                for key in ("name", "displayName", "valueType", "required",
                            "defaultValue", "unit", "choices"):
                    if key not in option:
                        raise ValueError(f"{label}: option is missing the {key!r} key")

                value_type = option["valueType"]
                if value_type not in ALLOWED_VALUE_TYPES:
                    raise ValueError(f"{label}: unexpected valueType {value_type!r}")

                if value_type == "Boolean" and option["defaultValue"] not in ("true", "false"):
                    raise ValueError(
                        f"{label}: Boolean defaultValue must be 'true' or 'false', "
                        f"found {option['defaultValue']!r}")

                if value_type == "Choice":
                    choices = option["choices"]
                    if not isinstance(choices, list) or not choices:
                        raise ValueError(f"{label}: Choice option has no choices")
                    if option["defaultValue"] not in choices:
                        raise ValueError(
                            f"{label}: Choice defaultValue {option['defaultValue']!r} "
                            f"is not one of {choices}")

    missing_commands = REQUIRED_COMMANDS - command_names
    if missing_commands:
        raise ValueError(f"catalog is missing commands: {sorted(missing_commands)}")
    if "catalog" in command_names:
        raise ValueError("catalog must not be registered as a module command")

    summary = subprocess.run(
        [executable, "catalog"], cwd=cwd, check=True, capture_output=True,
        text=True, encoding="utf-8").stdout
    for module_id in sorted(REQUIRED_MODULES):
        if module_id not in summary:
            raise ValueError(f"human-readable catalog does not mention module {module_id!r}")

    rejected = subprocess.run(
        [executable, "catalog", "--xml"], cwd=cwd, capture_output=True, text=True,
        encoding="utf-8")
    if rejected.returncode != 1:
        raise ValueError(f"catalog --xml exited {rejected.returncode}, expected 1")
    if "error:" not in rejected.stderr:
        raise ValueError("catalog --xml did not write an 'error:' line to stderr")

    print(f"catalog: {len(modules)} modules, {len(command_names)} commands, "
          f"{option_count} options")


def check_streaming(executable: Path, work: Path) -> None:
    """The CLI can pipe payloads to stdout for macOS streaming burns."""

    def stream(*args: str) -> subprocess.CompletedProcess:
        completed = subprocess.run(
            [executable, *args, "--output", "-"], cwd=work, capture_output=True)
        if completed.returncode != 0:
            raise ValueError(
                f"{args[0]} --output - failed with {completed.returncode}: "
                f"{completed.stderr.decode('utf-8', 'replace')}")
        return completed

    cd_stream = stream("cd-generate", "--input", "input image.png", "--sectors", "2")
    if len(cd_stream.stdout) != 2 * 2352:
        raise ValueError(f"cd-generate --output - wrote {len(cd_stream.stdout)} bytes, expected 4704")
    if b"CD progress" in cd_stream.stdout or b"CD track complete" in cd_stream.stdout:
        raise ValueError("cd-generate --output - mixed log text into the binary stream")
    if b"CD progress" not in cd_stream.stderr:
        raise ValueError("cd-generate --output - did not write progress to stderr")

    subprocess.run(
        [executable, "cd-generate", "--input", "input image.png",
         "--output", "stream.raw", "--sectors", "2"],
        cwd=work, check=True, capture_output=True)
    if (work / "stream.raw").read_bytes() != cd_stream.stdout:
        raise ValueError("stdout CD-DA stream differs from the .raw file output")

    bd_stream = stream("bd-generate", "--input", "input image.png", "--total-sectors", "1024")
    if len(bd_stream.stdout) != 1024 * 2048:
        raise ValueError(f"bd-generate --output - wrote {len(bd_stream.stdout)} bytes, expected 2097152")
    if b"full-area drawing" in bd_stream.stdout or b"BD 2097152" in bd_stream.stdout:
        raise ValueError("bd-generate --output - mixed log text into the binary stream")
    if b"full-area drawing" not in bd_stream.stderr:
        raise ValueError("bd-generate --output - did not write its summary to stderr")
    if (work / "bd.iso").read_bytes() != bd_stream.stdout:
        raise ValueError("stdout BD ISO stream differs from the ISO file output")

    if (work / "-").exists():
        raise ValueError('"--output -" created a file literally named "-"')

    dvd_stream = subprocess.run(
        [executable, "solve", "--image", "input image.png", "--iso-output", "-",
         "--total-sectors", "160", "--fill-sectors", "160",
         "--fast-output", "true", "--algorithm", "dispersion"],
        cwd=work, capture_output=True)
    if dvd_stream.returncode != 0:
        raise ValueError(
            f"solve --iso-output - failed with {dvd_stream.returncode}: "
            f"{dvd_stream.stderr.decode('utf-8', 'replace')}")
    if len(dvd_stream.stdout) != 160 * 2048:
        raise ValueError(
            f"solve --iso-output - wrote {len(dvd_stream.stdout)} bytes, expected {160 * 2048}")
    if b"Running DVD" in dvd_stream.stdout or b"DVD command solve completed" in dvd_stream.stdout:
        raise ValueError("solve --iso-output - mixed log text into the binary stream")
    if b"DVD command solve completed" not in dvd_stream.stderr:
        raise ValueError("solve --iso-output - did not write its summary to stderr")

    subprocess.run(
        [executable, "solve", "--image", "input image.png", "--iso-output", "dvd-stream.iso",
         "--total-sectors", "160", "--fill-sectors", "160",
         "--fast-output", "true", "--algorithm", "dispersion"],
        cwd=work, check=True, capture_output=True)
    if (work / "dvd-stream.iso").read_bytes() != dvd_stream.stdout:
        raise ValueError("stdout DVD stream differs from the ISO file output")
    if (work / "-").exists():
        raise ValueError('"--iso-output -" created a file literally named "-"')

    # Hybrid DVD (data-dir) streams the ISO9660/Joliet prefix, the raw drawing
    # interval and the file suffix through one stdout pipe.
    dvd_files = work / "dvd-files"
    (dvd_files / "sub").mkdir(parents=True, exist_ok=True)
    (dvd_files / "a.txt").write_text("hello", encoding="utf-8")
    (dvd_files / "sub" / "b.txt").write_text("world", encoding="utf-8")
    dvd_hybrid = subprocess.run(
        [executable, "solve", "--image", "input image.png", "--iso-output", "-",
         "--total-sectors", "160", "--data-dir", str(dvd_files),
         "--file-position", "inner", "--fast-output", "true", "--algorithm", "dispersion"],
        cwd=work, capture_output=True)
    if dvd_hybrid.returncode != 0:
        raise ValueError(
            f"hybrid solve --iso-output - failed with {dvd_hybrid.returncode}: "
            f"{dvd_hybrid.stderr.decode('utf-8', 'replace')}")
    if len(dvd_hybrid.stdout) != 160 * 2048:
        raise ValueError(
            f"hybrid solve stdout wrote {len(dvd_hybrid.stdout)} bytes, expected {160 * 2048}")
    if b"hybrid ISO" not in dvd_hybrid.stderr:
        raise ValueError("hybrid solve stdout did not report its layout on stderr")
    if b"DVD command solve completed" in dvd_hybrid.stdout:
        raise ValueError("hybrid solve mixed the CLI summary into stdout")
    if not any(dvd_hybrid.stdout[:32768]) and not any(dvd_hybrid.stdout[32768:]):
        raise ValueError("hybrid solve stdout is empty")


def check_png(path: Path, size: int, height=None) -> None:
    expected_height = size if height is None else height
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path.name} is not a PNG file")
    width = int.from_bytes(data[16:20], "big")
    actual_height = int.from_bytes(data[20:24], "big")
    if (width, actual_height) != (size, expected_height):
        raise ValueError(
            f"{path.name}: expected {size}x{expected_height}, found {width}x{actual_height}")
    if len(data) <= 100:
        raise ValueError(f"{path.name}: PNG payload is empty")


def check_previews(executable: Path, work: Path) -> None:
    """bd-preview / bd-preview-readback render real PNGs for the preview page."""
    summary = subprocess.run(
        [executable, "bd-preview", "--input", "input image.png",
         "--output", "bd-preview.png", "--total-sectors", "1024", "--size", "128"],
        cwd=work, check=True, capture_output=True, text=True, encoding="utf-8").stdout
    if "BD design projection" not in summary:
        raise ValueError("bd-preview did not print its summary")
    check_png(work / "bd-preview.png", 128)

    subprocess.run(
        [executable, "bd-preview-readback", "--iso", "bd.iso",
         "--output", "bd-readback.png", "--size", "128"],
        cwd=work, check=True, capture_output=True)
    check_png(work / "bd-readback.png", 128)

    missing_sidecar = subprocess.run(
        [executable, "bd-preview-readback", "--iso", "input image.png",
         "--output", "bd-missing.png", "--size", "64"],
        cwd=work, capture_output=True, text=True, encoding="utf-8")
    if missing_sidecar.returncode != 1 or "error:" not in missing_sidecar.stderr:
        raise ValueError("bd-preview-readback accepted an ISO without a metadata sidecar")

    bad_size = subprocess.run(
        [executable, "bd-preview", "--input", "input image.png",
         "--output", "bd-small.png", "--size", "32"],
        cwd=work, capture_output=True, text=True, encoding="utf-8")
    if bad_size.returncode != 1 or "error:" not in bad_size.stderr:
        raise ValueError("bd-preview accepted --size below 64")

    print("bd-preview: 128x128 design projection and 128x128 read-back PNG written")


def check_presets(executable: Path, work: Path) -> None:
    completed = subprocess.run(
        [executable, "presets", "--json"], cwd=work, check=True, capture_output=True)
    if completed.stdout.startswith(b"\xef\xbb\xbf"):
        raise ValueError("presets --json wrote a UTF-8 BOM")
    document = json.loads(completed.stdout.decode("utf-8"))
    if document.get("schemaVersion") != 4:
        raise ValueError(f"presets schemaVersion: {document.get('schemaVersion')!r}, expected 4")
    for key in ("cdPresets", "dvdPresets", "bluRayPresets"):
        presets = document.get(key)
        if not isinstance(presets, list) or not presets:
            raise ValueError(f"presets --json has no {key}")
        for field in ("id", "displayName", "description", "innerRadiusMm", "outerRadiusMm"):
            if field not in presets[0]:
                raise ValueError(f"{key}[0] is missing {field!r}")

    user_path = work / "user-presets.json"
    user_path.write_text(json.dumps({
        "schemaVersion": 4,
        "cdPresets": [{
            "id": "user-test-cd",
            "displayName": "User test CD",
            "description": "user preset for the CLI smoke test",
            "sectors": 1000,
            "innerRadiusMm": 25.0,
            "outerRadiusMm": 50.0,
            "linearVelocityMmPerSecond": 1200,
        }],
        "dvdPresets": [],
        "bluRayPresets": [],
        "disabledCdPresetIds": ["cd-80"],
    }), encoding="utf-8")
    merged = subprocess.run(
        [executable, "presets", "--json", "--path", str(user_path)],
        cwd=work, check=True, capture_output=True)
    merged_document = json.loads(merged.stdout.decode("utf-8"))
    cd_ids = {preset["id"] for preset in merged_document["cdPresets"]}
    if "user-test-cd" not in cd_ids:
        raise ValueError("presets --path did not merge the user preset")
    if "cd-80" in cd_ids:
        raise ValueError("presets --path did not apply disabledCdPresetIds")
    if "cd-80" not in merged_document.get("disabledCdPresetIds", []):
        raise ValueError("presets --json did not report disabledCdPresetIds")

    missing_path = subprocess.run(
        [executable, "presets", "--json", "--path", str(work / "no-such-presets.json")],
        cwd=work, check=True, capture_output=True)
    if not json.loads(missing_path.stdout.decode("utf-8")).get("cdPresets"):
        raise ValueError("presets --path fell back to an empty catalog when the user file is missing")

    bad_path = work / "bad-presets.json"
    bad_path.write_text("{ not json", encoding="utf-8")
    rejected = subprocess.run(
        [executable, "presets", "--json", "--path", str(bad_path)],
        cwd=work, capture_output=True, text=True, encoding="utf-8")
    if rejected.returncode != 1 or "error:" not in rejected.stderr:
        raise ValueError("presets --json accepted a corrupt user preset file")

    summary = subprocess.run(
        [executable, "presets"], cwd=work, check=True, capture_output=True,
        text=True, encoding="utf-8").stdout
    if "CD presets" not in summary or "Blu-ray presets" not in summary:
        raise ValueError("human-readable presets summary does not group by family")

    print(f"presets: {len(document['cdPresets'])} CD, {len(document['dvdPresets'])} DVD, "
          f"{len(document['bluRayPresets'])} Blu-ray")


def write_gray_png(path: Path, width: int, height: int, value: int) -> None:
    """Minimal dependency-free grayscale PNG, used as a >2000 px calibration photo."""
    row = bytes([value]) * width
    raw = b"".join(b"\x00" + row for _ in range(height))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff))

    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 6))
        + chunk(b"IEND", b""))


def ring_points(center_x: float, center_y: float, radius: float, count: int = 8) -> str:
    import math
    return ";".join(
        f"{center_x + radius * math.cos(-math.pi / 2 + 2 * math.pi * index / count):.3f},"
        f"{center_y + radius * math.sin(-math.pi / 2 + 2 * math.pi * index / count):.3f}"
        for index in range(count))


def check_calibration(executable: Path, work: Path) -> None:
    """Full calibration command group: target, photo, fit, coarse, session."""
    repo = Path(__file__).resolve().parents[1]
    target_png = work / "calib-target.png"
    base_session = work / "calib-base.calibration.json"
    subprocess.run(
        [executable, "calibration-target", "--family", "cd", "--r0", "24.5", "--r1", "56.8",
         "--sectors", "359849", "--density", "1200",
         "--output", str(target_png), "--session", str(base_session)],
        cwd=work, check=True)
    check_png(target_png, 2400)
    document = json.loads(base_session.read_text(encoding="utf-8"))
    if document["target"]["schemaVersion"] != 6:
        raise ValueError("calibration-target did not write a schema 6 session")
    if document["target"]["parameters"]["kind"] != "Cd":
        raise ValueError("calibration-target session has the wrong disc kind")
    if document["observations"] or document["photo"] is not None:
        raise ValueError("calibration-target session skeleton is not empty")

    coarse_png = work / "calib-coarse.png"
    p0_path = work / "calib-p0.json"
    subprocess.run(
        [executable, "calibration-coarse-target", "--family", "dvd", "--r0", "24", "--r1", "58",
         "--sectors", "2295104", "--density", "133.3",
         "--output", str(coarse_png), "--json", str(p0_path)],
        cwd=work, check=True)
    check_png(coarse_png, 2400)
    p0 = json.loads(p0_path.read_text(encoding="utf-8"))
    for key in ("family", "r0", "r1", "sectors", "density",
                "pitchLinear", "pitchQuadratic", "pitchCubic",
                "drawingStart", "drawingEnd", "outputPath"):
        if key not in p0:
            raise ValueError(f"calibration-coarse-target P0 is missing {key!r}")

    wide = work / "calib-wide.png"
    write_gray_png(wide, 2100, 100, 120)
    wide_resized = work / "calib-wide-resized.png"
    wide_json = work / "calib-wide.json"
    subprocess.run(
        [executable, "calibration-photo", "--photo", str(wide), "--disc-size", "80",
         "--resized-out", str(wide_resized), "--json", str(wide_json)],
        cwd=work, check=True)
    resized = json.loads(wide_json.read_text(encoding="utf-8"))
    if not resized["succeeded"] or (resized["photoWidth"], resized["photoHeight"]) != (2000, 95):
        raise ValueError(f"calibration-photo did not scale to 2000 px: {resized}")
    check_png(wide_resized, 2000, 95)

    outer = ring_points(127.5, 127.5, 100)
    hole = ring_points(127.5, 127.5, 12)
    resized_photo = work / "calib-photo.png"
    rectified = work / "calib-rectified.png"
    photo_json = work / "calib-photo.json"
    subprocess.run(
        [executable, "calibration-photo", "--photo", "input image.png", "--disc-size", "120",
         "--outer", outer, "--hole", hole, "--size", "400",
         "--resized-out", str(resized_photo), "--rectified-out", str(rectified),
         "--json", str(photo_json)],
        cwd=work, check=True)
    photo = json.loads(photo_json.read_text(encoding="utf-8"))
    if not photo["succeeded"]:
        raise ValueError(f"calibration-photo failed: {photo['message']}")
    if photo["rectifiedSize"] != 400 or len(photo["transformDiscToSource"]) != 9:
        raise ValueError("calibration-photo result is missing the rectified size or transform")
    if photo["photoWidth"] != 256 or photo["photoHeight"] != 256:
        raise ValueError("calibration-photo reported the wrong photo size")
    check_png(rectified, 400)

    failed_photo = work / "calib-photo-failed.json"
    failed = subprocess.run(
        [executable, "calibration-photo", "--photo", "input image.png", "--disc-size", "120",
         "--outer", "10,10;20,20;30,30;40,40;50,50", "--hole", hole,
         "--resized-out", str(work / "calib-failed.png"),
         "--rectified-out", str(work / "calib-failed-rectified.png"),
         "--json", str(failed_photo)],
        cwd=work, check=True, capture_output=True)
    failed_document = json.loads(failed_photo.read_text(encoding="utf-8"))
    if failed.returncode != 0 or failed_document["succeeded"]:
        raise ValueError("calibration-photo did not report degenerate contours as a failed result")
    if not failed_document.get("message") or not failed_document.get("resizedPhotoPath"):
        raise ValueError("calibration-photo failure did not keep its message/resized photo path")
    if (failed_document.get("photoWidth"), failed_document.get("photoHeight")) != (256, 256):
        raise ValueError("calibration-photo failure lost the resized photo dimensions")

    usage = subprocess.run(
        [executable, "calibration-target", "--family", "xx", "--r0", "24", "--r1", "58",
         "--sectors", "100", "--density", "1", "--output", "x.png", "--session", "x.json"],
        cwd=work, capture_output=True, text=True, encoding="utf-8")
    if usage.returncode != 2:
        raise ValueError(f"calibration parameter errors must exit 2, got {usage.returncode}")

    points = ";".join(f"{radius},0" for radius in (26, 29, 32, 35, 38, 41, 44, 47, 50, 53, 56))
    session_path = work / "calib-session.calibration.json"
    subprocess.run(
        [executable, "calibration-session-write", "--session", str(base_session),
         "--photo", str(resized_photo), "--disc-size", "120",
         "--outer", outer, "--hole", hole, "--points", points,
         "--output", str(session_path)],
        cwd=work, check=True)
    summary_path = work / "calib-summary.json"
    subprocess.run(
        [executable, "calibration-session-read", "--session", str(session_path),
         "--json", str(summary_path)],
        cwd=work, check=True)
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    if (summary["family"], summary["sectors"], summary["density"]) != ("cd", 359849, 1200):
        raise ValueError(f"calibration-session-read returned the wrong geometry: {summary}")
    if not summary["hasPhoto"] or not summary["rectificationConfirmed"]:
        raise ValueError("calibration-session-read lost the photo state")
    if (summary["tracePointCount"], summary["photoWidth"], summary["photoHeight"]) != (11, 256, 256):
        raise ValueError("calibration-session-read returned the wrong trace/photo counts")

    windows_session = repo / "docs/examples/dvd-calibration/calibration-f2f851f7.calibration.json"
    example_summary = work / "calib-example-summary.json"
    subprocess.run(
        [executable, "calibration-session-read", "--session", str(windows_session),
         "--json", str(example_summary)],
        cwd=work, check=True)
    example = json.loads(example_summary.read_text(encoding="utf-8"))
    if (example["family"], example["sectors"]) != ("dvd", 2297888):
        raise ValueError("calibration-session-read cannot read a Windows example session")

    fit_path = work / "calib-fit.json"
    subprocess.run(
        [executable, "calibration-fit", "--session", str(session_path), "--points", points,
         "--json", str(fit_path), "--auto-range", "true", "--fit-pitch", "true"],
        cwd=work, check=True)
    fit = json.loads(fit_path.read_text(encoding="utf-8"))
    if not fit["succeeded"] or fit["bestIndex"] != 0 or not fit["candidates"]:
        raise ValueError(f"calibration-fit did not produce a candidate: {fit}")
    candidate = fit["candidates"][0]
    for key in ("innerRadiusMm", "outerRadiusMm", "pitchLinear", "pitchQuadratic", "pitchCubic",
                "rotationRadians", "rmsErrorMm", "maxErrorMm", "outlierCount",
                "sectors", "invertMapping", "predicted"):
        if key not in candidate:
            raise ValueError(f"calibration-fit candidate is missing {key!r}")
    if len(candidate["predicted"]) < 10:
        raise ValueError("calibration-fit candidate has no predicted polyline")
    predicted_point = candidate["predicted"][0]
    if not isinstance(predicted_point, dict) or "x" not in predicted_point or "y" not in predicted_point:
        raise ValueError("predicted points must be {\"x\": ..., \"y\": ...} objects")
    updated = json.loads(session_path.read_text(encoding="utf-8"))
    traces = updated["photo"]["traces"]
    if len(traces) != 1 or len(traces[0]["points"]) != 11:
        raise ValueError("calibration-fit did not write the traces back to the session")

    # In-place updates: the base session is loaded before the same path is written, so the
    # target survives, and --confirmed false stores an unconfirmed photo with no traces.
    inplace = work / "calib-inplace.calibration.json"
    shutil.copyfile(session_path, inplace)
    before = json.loads(inplace.read_text(encoding="utf-8"))
    subprocess.run(
        [executable, "calibration-session-write", "--session", str(inplace),
         "--photo", str(resized_photo), "--disc-size", "120",
         "--outer", outer, "--hole", hole, "--points", "",
         "--confirmed", "false", "--output", str(inplace)],
        cwd=work, check=True)
    after = json.loads(inplace.read_text(encoding="utf-8"))
    if after["target"]["id"] != before["target"]["id"]:
        raise ValueError("calibration-session-write lost the target when updating in place")
    if after["photo"]["rectificationConfirmed"]:
        raise ValueError("--confirmed false did not store an unconfirmed photo")
    if after["photo"]["traces"] != [{"armIndex": 0, "points": []}]:
        raise ValueError("--confirmed false with empty points did not clear the traces")

    inplace_summary = work / "calib-inplace-summary.json"
    subprocess.run(
        [executable, "calibration-session-read", "--session", str(inplace),
         "--json", str(inplace_summary)],
        cwd=work, check=True)
    inplace_doc = json.loads(inplace_summary.read_text(encoding="utf-8"))
    if inplace_doc["rectificationConfirmed"] is not False or inplace_doc["tracePointCount"] != 0:
        raise ValueError("in-place unconfirmed session summary is wrong")

    subprocess.run(
        [executable, "calibration-session-write", "--session", str(inplace),
         "--photo", str(resized_photo), "--disc-size", "120",
         "--outer", outer, "--hole", hole, "--points", "",
         "--confirmed", "true", "--output", str(inplace)],
        cwd=work, check=True)
    confirmed_doc = json.loads(inplace.read_text(encoding="utf-8"))
    if not confirmed_doc["photo"]["rectificationConfirmed"]:
        raise ValueError("--confirmed true was not stored")
    if confirmed_doc["photo"]["traces"] != [{"armIndex": 0, "points": []}]:
        raise ValueError("--confirmed true with empty points did not store an empty trace")
    if confirmed_doc["target"]["parameters"]["innerRadiusMm"] != 24.5:
        raise ValueError("in-place update changed the target geometry")

    radii = [26.0 + (56.0 - 26.0) * index / 39 for index in range(40)]
    import math
    generated_inner, generated_outer = 24.0, 58.0
    true_inner, true_outer, rotation = 23.8, 57.9, 0.3
    track_length = 2295104 * 38688 * 133.3 * 1e-6
    growth_generated = math.pi * (generated_outer ** 2 - generated_inner ** 2) / track_length
    growth_actual = math.pi * (true_outer ** 2 - true_inner ** 2) / track_length
    coarse_points = []
    for radius in radii:
        source_radius = math.sqrt(generated_inner ** 2
                                  + (radius ** 2 - true_inner ** 2)
                                  * (generated_outer ** 2 - generated_inner ** 2)
                                  / (true_outer ** 2 - true_inner ** 2))
        delta = 2 * math.pi * ((source_radius - generated_inner) / growth_generated
                               - (radius - true_inner) / growth_actual)
        angle = rotation - delta
        coarse_points.append(f"{radius * math.cos(angle):.9f},{radius * math.sin(angle):.9f}")
    coarse_fit_path = work / "calib-coarse-fit.json"
    subprocess.run(
        [executable, "calibration-coarse-fit", "--family", "dvd", "--r0", "24", "--r1", "58",
         "--sectors", "2295104", "--density", "133.3",
         "--points", ";".join(coarse_points), "--json", str(coarse_fit_path)],
        cwd=work, check=True)
    coarse_fit = json.loads(coarse_fit_path.read_text(encoding="utf-8"))
    if not coarse_fit["succeeded"] or coarse_fit["bestIndex"] < 0 or not coarse_fit["candidates"]:
        raise ValueError(f"calibration-coarse-fit failed on synthetic points: {coarse_fit}")
    best = coarse_fit["candidates"][coarse_fit["bestIndex"]]
    inner_error = abs(best["innerRadiusMm"] - true_inner)
    outer_error = abs(best["outerRadiusMm"] - true_outer)
    if inner_error > 0.02 or outer_error > 0.02:
        raise ValueError(
            f"calibration-coarse-fit recovered r0/r1 with {inner_error:.4f}/{outer_error:.4f} mm error")
    if len(best["predicted"]) < 10:
        raise ValueError("calibration-coarse-fit candidate has no predicted polyline")
    if not isinstance(best["predicted"][0], dict) or "x" not in best["predicted"][0]:
        raise ValueError("coarse predicted points must be {\"x\": ..., \"y\": ...} objects")

    too_few = work / "calib-coarse-too-few.json"
    subprocess.run(
        [executable, "calibration-coarse-fit", "--family", "dvd", "--r0", "24", "--r1", "58",
         "--sectors", "2295104", "--density", "133.3",
         "--points", "30,0;31,0;32,0", "--json", str(too_few)],
        cwd=work, check=True)
    if json.loads(too_few.read_text(encoding="utf-8"))["succeeded"]:
        raise ValueError("calibration-coarse-fit accepted fewer than eight points")

    print(f"calibration: target 2400px, photo resized 2000px/rectified 400px, "
          f"fit {len(fit['candidates'])} candidates, coarse recovery error "
          f"{inner_error:.4f}/{outer_error:.4f} mm")



def check_presets_import(executable: Path, work: Path) -> None:
    """User presets can be imported/merged from a schema 4 JSON via the CLI."""
    incoming = work / "incoming-presets.json"
    user = work / "user-presets.json"
    preset = {
        "id": "imported-calibrated-cd",
        "displayName": "Imported calibrated CD",
        "description": "From calibration",
        "sectors": 359845,
        "innerRadiusMm": 24.911275,
        "outerRadiusMm": 57.931155,
        "linearVelocityMmPerSecond": 1200,
    }
    incoming.write_text(json.dumps({
        "schemaVersion": 4,
        "cdPresets": [preset],
        "dvdPresets": [],
        "bluRayPresets": [],
    }), encoding="utf-8")

    completed = subprocess.run(
        [executable, "presets-import", "--file", str(incoming), "--path", str(user)],
        cwd=work, check=True, capture_output=True)
    summary = json.loads(completed.stdout)
    if not summary.get("ok") or summary.get("path") != str(user):
        raise ValueError(f"presets-import returned an unexpected summary: {summary!r}")

    listed = subprocess.run(
        [executable, "presets", "--json", "--path", str(user)],
        cwd=work, check=True, capture_output=True)
    document = json.loads(listed.stdout)
    ids = [item["id"] for item in document["cdPresets"]]
    if "imported-calibrated-cd" not in ids:
        raise ValueError("presets-import did not add the imported CD preset")

    # Importing the same id again replaces it instead of duplicating it.
    subprocess.run(
        [executable, "presets-import", "--file", str(incoming), "--path", str(user)],
        cwd=work, check=True, capture_output=True)
    listed = subprocess.run(
        [executable, "presets", "--json", "--path", str(user)],
        cwd=work, check=True, capture_output=True)
    document = json.loads(listed.stdout)
    if [item["id"] for item in document["cdPresets"]].count("imported-calibrated-cd") != 1:
        raise ValueError("presets-import duplicated an existing preset id")

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

        help_text = subprocess.run(
            [executable, "--help"], cwd=work, check=True, capture_output=True,
            text=True, encoding="utf-8").stdout
        for command in ("catalog", "presets", "bd-preview", "bd-preview-readback",
                        "calibration-target", "calibration-photo", "calibration-fit",
                        "calibration-coarse-target", "calibration-coarse-fit",
                        "calibration-session-write", "calibration-session-read"):
            if command not in help_text:
                raise ValueError(f"--help does not mention the {command!r} subcommand")

        check_catalog(executable, work)
        check_presets(executable, work)
        check_presets_import(executable, work)

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

        check_previews(executable, work)
        check_calibration(executable, work)
        check_streaming(executable, work)


if __name__ == "__main__":
    check_package(Path(sys.argv[1]).resolve(strict=True))
