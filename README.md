# Fractal Explorer

A fast, interactive Mandelbrot set explorer for Windows (C# / .NET 10, WinForms).

## Features

- **Smooth zoom** with the mouse wheel, centred on the cursor
- **Pan** by dragging — only newly exposed pixels are computed
- **Fullscreen** (F11, Alt+Enter, or double-click)
- **Fast**: multi-threaded (cores − 2), AVX2 SIMD, interior fill (Mariani–Silver), and
  progressive rendering with an instant low-res preview
- **Deep zoom** to about 10²⁹⁰× using perturbation theory: one reference orbit is computed in
  arbitrary precision and every pixel is iterated in fast doubles relative to it (with
  rebasing to avoid glitches). Switches on automatically past 10¹³× (X key: auto / always /
  off). Ctrl+C copies the location in full precision.
- **Palettes**: 12 built-ins, random palette generator, live hover preview, animated colour
  cycling, Fractint `.map` import/export

Press **H** in the app for the full list of keys.

## Building

Open `Fractal.sln` in Rider or Visual Studio, or run:

```
dotnet run --project Fractal
```

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

## Test bench

`PerturbationLab` is a console test bench for the deep zoom maths: it checks perturbation
against brute-force full-precision iteration and the app renderer's deep mode
(`dotnet run -c Release --project PerturbationLab -- <image folder> [app|diag|probe]`).
