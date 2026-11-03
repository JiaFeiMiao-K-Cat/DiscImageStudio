# Experimental macOS desktop

`DiscImageStudio.Desktop` is an Avalonia shell for the existing .NET 10 optical modules.
It follows the Windows workspaces (Home → Design → Preview → Export → Burn, plus Calibration),
uses the same `.discproj` schema, presets, tone profiles, calibration records and encoders,
and leaves the WPF application and IMAPI backend in place.

## Build and run

Supported packaging targets: macOS 14+, Apple Silicon (`osx-arm64`) and Intel (`osx-x64`).
Development requires the .NET 10 SDK. Recipients of the self-contained app do not need .NET.

```sh
dotnet run --project src/DiscImageStudio.Desktop -c Release
bash packaging/macos/package.sh osx-arm64
# Intel: bash packaging/macos/package.sh osx-x64
open 'artifacts/macos-osx-arm64/Disc Image Studio.app'
```

The script refuses to overwrite an existing app directory. Choose a fresh second argument
for repeated builds. `DOTNET_EXECUTABLE` can point to a private SDK installation.
The app has a local ad-hoc signature. A public release still needs the maintainer's Apple
Developer ID signing and notarization; this PR does not claim to supply those credentials.
CI uploads app ZIPs, SHA-256 files and actual native-window screenshots as build artifacts.

## Workflow

1. Create a CD, DVD or BD project from the shared preset catalogue, or open a `.discproj`.
   Layer images remain external references. When moving projects between machines, import
   images again if their absolute paths differ. Saving does not collect image assets.
2. Import images, select a layer, edit visibility, position, scale and rotation, and reorder
   layers. Drag moves the selected layer; the wheel scales it; Shift+wheel rotates it.
   The composite uses the same longest-side fit, millimetre origin and clockwise rotation
   convention as Windows. Edit tone on the design page; inspect encoded tone in Preview.
3. Preview either the current design or a generated artifact. Set measured radii and sampling
   resolution. ISO/track sidecars supply artifact sector counts, interleave and protected
   file extents. Yellow areas represent filesystem/data, not image payload. Keep sidecars
   beside artifacts when moving them. Export the preview to PNG.
4. Export CD WAV+CUE/RAW, CD data ISO, DVD ISO or single-layer BD-R ISO. A nonempty data
   folder selects a mixed data disc, with files at the inner or outer ring. Final generation
   uses an 8192 px composite. Output extensions are validated before invoking an encoder.
5. On macOS, Burn writes an already generated WAV or ISO through the system `drutil`
   (DiscRecording). Select a recorder, inspect the disc, then confirm the exact file/device.
   Only blank CD/DVD media with sufficient reported capacity are accepted; no erase,
   append or overburn options are used. The media is checked again after confirmation.
   WAV is converted losslessly to a single big-endian PCM AIFF track in the private cache;
   no volume normalization, compressed audio or image filesystem reconstruction is used.
   Writing cannot be canceled or interrupted from the UI. No real medium was written during
   development: physical fidelity remains unverified until tested on a recorder and CD-R.
6. Calibration generates the shared `target_slim` pattern and a frozen generation record.
   Burn that artifact, load its photograph, mark at least five distributed points on each
   physical outer edge and centre-hole edge, then rectify. Trace one radial arm from inside
   out (at least six points, preferably twenty). Wheel zooms, right-drag pans, Alt-click deletes
   a point and dragging corrects it. Fit, select a candidate, inspect the whole orange
   prediction, explicitly confirm the shape, then apply. Excessive RMS/max error blocks apply.
   Core owns perspective correction, automatic search expansion and DVD cubic pitch fitting.
   Embedded-photo calibration records can be saved and reopened with either desktop shell.

Cmd/Ctrl+N, O and S create, open and save. Unsaved project changes are guarded on close/open.
The toolbar also supports Save As. Themes and language selection are saved in
`Environment.SpecialFolder.LocalApplicationData/DiscImageStudio/desktop-settings.json`;
the existing translation catalogue is shared, with new Mac-specific diagnostic text currently
falling back to Chinese. Temporary files live in a unique per-process cache directory and
only that directory is removed at normal exit.

## Differences from the Windows desktop

This is an experimental port, **not a claim of complete feature or hardware parity**.

| Area | macOS desktop |
| --- | --- |
| CD/DVD/BD generation, hybrid file layouts, common tone and project schema | Shared production modules |
| Design, artifact preview, export, calibration | Implemented in the portable shell |
| GUI controls | Avalonia with the same workspace order and panel structure, native macOS window chrome |
| Streaming IMAPI writes | Windows only; Mac first exports a complete artifact |
| BD-R writing | Export supported; use external BD-R software. The Mac burn button explicitly refuses BD projects |
| Physical CD/DVD recording | Experimental; software checks cover blank-media/capacity refusal and exact PCM preservation, not real discs |
| Settings | Appearance/language; Windows cache-directory configuration and complete translated Mac UI are follow-ups |
| Calibration picking | Editable points on outer/hole boundaries; uses the same Core photo model, rather than WPF ellipse handles |
| Installer signing | Ad-hoc local signature only; no notarized public installer |

## Validation

```sh
dotnet build src/DiscImageStudio.Desktop -c Release
dotnet run --project tests/DiscImageStudio.DesktopTests -c Release
dotnet run --project src/DvdImageSolver -c Release -- selftest
# Native screenshots on macOS (launches and closes a test window):
dotnet run --project src/DiscImageStudio.Desktop -c Release -- --ui-snapshot /tmp/disc-ui
```

The console regression suite checks project/thumbnail round trips, asymmetric layer
orientation, real module generation and preview for all disc families, both mixed-file
placements and file payload integrity, protected-region overlays, CUE links, exact
WAV→AIFF sample bytes, blank/over-capacity refusal, actual `drutil` discovery on macOS,
Core calibration/session compatibility, and six real page renders under Avalonia.Headless/Skia.
These tests never start a burn. `--ui-snapshot` uses a disposable fixture and does not save
projects, recent-file entries or settings. Run it into a new output directory.

The existing Windows solution build, architecture suite, DVD selftest, release-tooling
checks and WPF snapshot remain in CI. The added macOS matrix runs portable tests, packages
self-contained Intel/ARM64 apps, verifies signatures and renders the extracted app entry point.
