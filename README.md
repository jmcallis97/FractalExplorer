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

`PerturbationLab` is a console test bench for the deep zoom maths: it checks perturbation
against brute-force full-precision iteration and the app renderer's deep mode
(`dotnet run -c Release --project PerturbationLab -- <image folder> [app|diag|probe]`).
