# SlcToSvgxConverter
Convert Materialise SLC slices into SVGX print files for legacy LCD/DLP resin printers.
# SLC to SVGX Converter
A small Windows tool that converts **Materialise SLC** slice files into **SVGX** print files, so that
legacy LCD/DLP resin printers can be driven by a professional slicing workflow instead of their
aging, semi-commercial bundled slicers.
![Screenshot](docs/screenshot.png)
---
## Why this exists
Many older LCD/DLP machines only accept a single proprietary print container: `svgx` (a
`DLP-II 1.1` file holding BMP thumbnails plus one SVG with a group per layer). The slicing software
shipped with those machines is:
- **semi-commercial and abandoned** — no updates, limited model/material support, licensing quirks;
- **sometimes broken on real jobs** — e.g. it writes each inner contour (hole) as its own `<path>`,
  and firmware that fills paths one by one then fills the holes solid;
- **useless for anything the vendor did not anticipate** — no way to combine multiple parts, no
  support for models that intersect (supports penetrating the part), no control over output details.
Professional slicing tools — **Materialise Magics' slicing module** in particular — handle all of that
correctly, but they output **SLC**, which the printer cannot read.
This tool is the missing bridge:
```
STL / STEP / any CAD
        │  Materialise Magics (slicing module)
        ▼
      *.slc                    ← correct geometry, correct contours, correct winding
        │  SLC to SVGX Converter
        ▼
      *.svgx                   ← what the legacy printer actually eats
```
Slice properly, convert, print.
---
## Features
- **Reads SLC** (Materialise format, `-SLCVER 2.0`, MM and INCH), including per-layer contours and
  gaps.
- **Writes SVGX in the vendor-native layout** — verified field-by-field against files produced by the
  original software (see *Format notes* below), including 128×128 and 410×410 BMP thumbnails.
- **Layer preview** — black background, white solid fill, mouse-wheel zoom, drag to pan, slider and
  prev/next buttons to step through layers, one-key reset.
- **Live layer read-out** — layer index, Z height, net area, contour counts (outer / holes).
- **Opens `.svgx` from other tools** for inspection (including files with different unit conventions).
- **Merge several SLC files** into one job: layers are aligned by **absolute Z** and resampled to the
  finest layer height, so a part sliced separately from its supports can be printed together.
  Contours are never discarded — holes stay holes.
- **Remembers everything** — every parameter and the last opened file are restored on start-up.
- **Command line / drag & drop** — `SlcToSvgxConverter.exe a.slc b.slc` loads and merges them directly.
---
## Usage
1. Slice your model in **Materialise Magics** (or any slicer that can export SLC) at the layer height
   you want to print.
2. Launch `SlcToSvgxConverter.exe`, click `...` and pick the `.slc` file
   (or select several files to merge them).
3. Check the parameters in the right-hand panels:
   | Group | Meaning |
   |---|---|
   | `Resolution X/Y`, `Build size (mm)` | LCD pixel grid and physical screen size |
   | `Layer height (mm)` | read-only, taken from the SLC |
   | `Base layer count`, `Base exposure time (s)` | bottom layers and their exposure |
   | `Exposure time (s)` | normal layer exposure |
   | `Transition layer count` / `Transition type` | transition-layer settings (see limitations) |
   | `Light PWM`, `Base light PWM`, `Image gray level`, `Anti-aliasing` | machine-side parameters, remembered only |
   | `Base lift distance` / `Lift distance`, `... speed` | peel/lift profile, remembered only |
4. Press `Start`. The file is written next to the source as
   `<name>_<layer height>_<base time>_<yyyy_MM_dd_HH_mm_ss>.svgx`.
5. Copy it to the printer.
`Center on bounding box` (on by default) moves the model's bounding-box centre to the origin — this is
what the original slicer does, and it produces the familiar `-85 … 85` coordinate range. Uncheck it to
keep raw SLC coordinates.
---
## Format notes (reverse-engineered)
These were recovered by comparing files from the vendor's own software, and are the reason the output
is accepted by the machine.
### Container
| Offset | Content |
|---|---|
| 0 | ASCII magic `DLP-II 1.1\n` |
| 11 | 5 zero bytes |
| 16 | `uint32` thumbnail offset (28), `uint32` preview offset, `uint32` SVG offset |
| 28 | BMP #1 — 128×128, 24 bpp |
| — | BMP #2 — 410×410, 24 bpp |
| SVG offset | UTF-8 SVG document, to end of file |
### SVG document
```xml
<svg … >
  <printparams materialname="…" layerheight="0.03" volume="34.8957"
               layercount="117" lightintensity="1">
    <projectiontime attachlayer="4" buildinlayer="8" attachtime="50" basetime="3"/>
    <projectionadjust x="100" y="100" />
    <printrange minx="-85" miny="-50" minz="0" maxx="85" maxy="50" maxz="3.51" />
  </printparams>
  <g id="background"></g>
  <g id="layer-0" area="169.997" perimeter="54">
    <path d="M 85 50 L -85 50 … Z M … Z" style="fill:white" fill-rule="evenodd"/>
  </g>
  …
</svg>
```
Key observations:
- **Coordinates are millimetres, origin at the screen centre, +Y up.**
- **`area` is in cm² and `perimeter` in cm** (not mm) — a detail that is easy to get wrong.
- **`volume` = Σ `area` (cm²) × layer height (cm)**, in cm³.
- **One `<path>` per island, holes as extra sub-paths of the *same* path** (`fill-rule="evenodd"`).
  This matters: firmware fills each `<path>` independently, so a hole written as a separate path is
  simply painted solid. SLC gives holes the opposite winding (outer CCW, holes CW); the converter keeps
  that winding and groups every hole with its island.
- There is **no Z value per layer** — Z is `layer index × layerheight`, and `layerheight` is global.
### SLC input
ASCII header terminated by `0x0D 0x0A 0x1A`, 256 reserved bytes, a sample table (1-byte count,
then 4 floats per entry: min Z, layer thickness, …), then per layer
`float minZ, uint32 contourCount`, and per contour `uint32 vertexCount, uint32 gapCount,
vertexCount × (float x, float y)`. The file ends with `float maxZ` and `uint32 0xFFFFFFFF`.
Specification: outer contours CCW, inner contours CW.
---
## Limitations
- **Exposure is global per file.** `attachtime` / `basetime` / `attachlayer` / `buildinlayer` are single
  values; SVGX has no per-layer or per-region exposure. Transition layers are therefore not encoded —
  changing `Transition layer count` produces a functionally identical file (verified: two files sliced
  with 5 and 12 transition layers were byte-identical).
- **`area` / `perimeter` are computed geometrically**, while the vendor's software measures them from
  the rasterised mask. Expect ~0.1–0.5 % difference in the statistics; it does not affect printing.
- **No G-code output.** The printers in question consume `svgx` directly; there is no user-accessible
  G-code channel.
- Tested against one family of legacy machines; other firmware revisions may expect other field sets.
---
## Build
- Visual Studio 2022, **.NET Framework 4.7.2**, WinForms
- Open `SlcToSvgxConverter.sln`, build `Debug` or `Release`
- Command line: `MSBuild SlcToSvgxConverter.csproj /t:Rebuild /p:Configuration=Release`
No third-party dependencies.
---
## Disclaimer
Unofficial community tool. Not affiliated with, endorsed by, or supported by any printer or slicing
software vendor. Reverse-engineered file-format details are provided for interoperability; use at your
own risk and verify output before committing a long print.
