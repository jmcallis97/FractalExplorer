# Fractal Explorer

A fast, interactive Mandelbrot set explorer for Windows (C# / .NET 10, WinForms).

## Features

- **Smooth zoom** with the mouse wheel, centred on the cursor
- **Pan** by dragging — only newly exposed pixels are computed
- **Fullscreen** (F11, Alt+Enter, or double-click)
- **Fast**: multi-threaded (cores − 2), AVX2 SIMD, interior fill (Mariani–Silver), and
  progressive rendering with an instant low-res preview
- **Deep zoom** to about 10²⁸⁷× using perturbation theory: one reference orbit is computed in
  arbitrary precision and every pixel is iterated in fast doubles relative to it (with
  rebasing to avoid glitches). Switches on automatically once pixels are smaller than 10⁻¹³ (about 3×10¹⁰× zoom) (X key: auto / always /
  off). Ctrl+C copies the location in full precision, with the iteration limit, colour mapping
  and density; Ctrl+V goes there.
- **Snap** (N): finds the exact point the view is circling (the eye of an endless spiral or a
  minibrot's nucleus) and offers to zoom in to it (animated), jump there, or centre on it.
  Shift+N zooms straight in. **Ctrl+click** snaps at the cursor instead and works on any
  structure, whatever its shape: it finds the minibrot the structure is built around by box
  period detection (Ctrl+Shift+click zooms straight in). A pasted location with a `Snap` line (or `Snap: 1e100` to choose
  where to stop) does the same after going there.
- **Palettes**: 12 built-ins, random palette generator, live hover preview, animated colour
  cycling, Fractint `.map` import/export

Press **H** in the app for the full list of keys.

**Places you've been:** a view counts as a place once you stay on it for 5 seconds, or change its
colours. When you move on, it's added to a list of up to 100 places, with its palette, mapping,
density and iteration limit, and a thumbnail of what was on screen. Press **L** for the gallery:
click a place to go back; the × on a hovered place (or Delete) removes it, and Ctrl+Z undoes;
right-click to pin or copy it; Ctrl+wheel or +/- resizes the thumbnails. **Backspace** goes back
to the last place, and **Delete** in the main window forgets the place you're on. The list is kept between sessions in `%LocalAppData%\Fractal\Places`, or in the
folder named by the `FRACTAL_PLACES` environment variable if it is set.

## Building

Open `Fractal.sln` in Rider or Visual Studio, or run:

```
dotnet run --project Fractal
```

The solution has four projects:

| Project | What it is |
| --- | --- |
| `Fractal.Core` | The engine, with no UI: plain and deep-zoom rendering on CPU and GPU, snap, locations, palettes and colouring. `Mandelbrot.cs` holds the rules every program shares (bailout, zoom range, deep-zoom switch-over, iteration limit). |
| `Fractal` | The WinForms explorer. `MainForm` ties together `ViewState`, `RenderPipeline`, `ColourState`, `DiveController` and `Overlay`. |
| `FractalVideo` | Zoom videos (below). |
| `PerturbationLab` | Test bench (below). |

## Zoom videos

`FractalVideo` renders a straight dive into a location: press Ctrl+C in the app, paste into a
text file, then

```
dotnet run -c Release --project FractalVideo -- location.txt dive.mp4 --size 3840x2160 --speed 0.5
```

It renders one keyframe per doubling of zoom at twice the video resolution (on the GPU when
there is one) and builds every frame from the two nearest keyframes, so frames are
anti-aliased and only a few hundred fractal renders are needed. Requires ffmpeg
(`winget install Gyan.FFmpeg`). Run it without arguments to see the options.

`FractalVideo --snap location.txt` finds the exact point a view is centred on, to ~320 digits:
the eye of an endless spiral (a Misiurewicz point: the orbit lands on a repelling cycle) or a
minibrot's nucleus. It reads the pattern off the orbit of the current centre, then solves for
the point with Newton's method, and writes `location.snapped.txt` (zoom 1e285 for a spiral,
framing the minibrot otherwise), ready for a dive to the limit of the deep-zoom maths.

## Test bench

`PerturbationLab` is a console test bench for the deep zoom maths: it checks perturbation
against brute-force full-precision iteration and the app renderer's deep mode
(`dotnet run -c Release --project PerturbationLab -- <image folder> [app|diag|probe]`).
