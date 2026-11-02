# Fractal Explorer

A fast, interactive Mandelbrot set explorer for Windows (C# / .NET 10, WinForms).

## Features

- **Smooth zoom** with the mouse wheel, centred on the cursor
- **Pan** by dragging — only newly exposed pixels are computed
- **Fullscreen** (F11, Alt+Enter, or double-click)
- **Fast**: multi-threaded (cores − 2), AVX2 SIMD, interior fill (Mariani–Silver), and
  progressive rendering with an instant low-res preview
- **Palettes**: 12 built-ins, random palette generator, live hover preview, animated colour
  cycling, Fractint `.map` import/export

Press **H** in the app for the full list of keys.

## Building

Open `Fractal.sln` in Rider or Visual Studio, or run:

```
dotnet run --project Fractal
```
