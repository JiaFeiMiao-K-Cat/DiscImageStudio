"""Smoke-test a published CLI from a working directory outside the checkout."""

import json
import shutil
import subprocess
import sys
import tempfile
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


def check_png(path: Path, size: int) -> None:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path.name} is not a PNG file")
    width = int.from_bytes(data[16:20], "big")
    height = int.from_bytes(data[20:24], "big")
    if (width, height) != (size, size):
        raise ValueError(f"{path.name}: expected {size}x{size}, found {width}x{height}")
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
        for command in ("catalog", "presets", "bd-preview", "bd-preview-readback"):
            if command not in help_text:
                raise ValueError(f"--help does not mention the {command!r} subcommand")

        check_catalog(executable, work)
        check_presets(executable, work)

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
        check_streaming(executable, work)


if __name__ == "__main__":
    check_package(Path(sys.argv[1]).resolve(strict=True))
