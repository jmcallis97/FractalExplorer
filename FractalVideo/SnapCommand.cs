using System.Diagnostics;
using System.Globalization;
using Fractal;
using Fractal.Deep;

/// <summary>
/// FractalVideo --snap &lt;location.txt&gt; [--out file] [--zoom 1e285] [--digits 320]
/// Replaces the centre of a location with the exact point it is circling (the eye of an endless
/// spiral, or a minibrot's nucleus), so a dive can go on to the limit of the deep-zoom maths.
/// </summary>
static class SnapCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("""
                Usage: FractalVideo --snap <location.txt> [options]
                  Finds the exact point the view is centred on: the eye of an endless spiral
                  (a Misiurewicz point) or a minibrot. Centre the spiral's eye roughly in the app
                  first, zoomed in far enough that it fills the view.
                  --out file.txt     where to write the result (default: <location>.snapped.txt)
                  --zoom 1e285       zoom to write (default: 1e285 for a spiral, framing for a minibrot)
                  --digits 320       digits of the answer (default: enough for zoom 1e300)
                  --max-iter 200000  how long to follow the orbit
                """);
            return 1;
        }
        string input = args[0];
        string output = Path.ChangeExtension(input, null) + ".snapped.txt";
        double? zoom = null;
        int digits = 320, maxIter = 200_000;
        var inv = CultureInfo.InvariantCulture;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--out": output = Next(); break;
                case "--zoom": zoom = double.Parse(Next(), NumberStyles.Float, inv); break;
                case "--digits": digits = int.Parse(Next(), inv); break;
                case "--max-iter": maxIter = int.Parse(Next(), inv); break;
                default: Console.Error.WriteLine($"Unknown option {a}"); return 1;
            }
        }

        if (!Location.TryParse(File.ReadAllText(input), out var loc, out string err))
        {
            Console.Error.WriteLine($"Can't read location from {input}: {err}");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var r = Snap.Find(loc, Math.Max(maxIter, (loc.Iterations ?? 0) * 4), (int)Math.Ceiling(digits * 3.33), Console.WriteLine, out string why);
        if (r == null)
        {
            Console.Error.WriteLine(why);
            return 1;
        }

        Console.WriteLine();
        double outZoom;
        if (r.Kind == Snap.Kind.Misiurewicz)
        {
            Console.WriteLine($"Found the eye of the spiral: a Misiurewicz point, preperiod {r.Preperiod}, period {r.Period}.");
            Console.WriteLine($"The picture repeats every {r.MultiplierAbs:G6}x of zoom, turned {r.TurnDegrees:0.0} degrees " +
                              $"({Math.Log(2) / Math.Log(r.MultiplierAbs):0.0} repeats per doubling).");
            outZoom = zoom ?? Snap.SuggestedZoom(r);
        }
        else
        {
            Console.WriteLine($"Found a minibrot of period {r.Period}, about {r.MinibrotSize:G3} across.");
            outZoom = zoom ?? Snap.SuggestedZoom(r);
        }
        Console.WriteLine($"It is {r.DistanceInViews:G3} view widths from your centre; Newton took {r.NewtonSteps} steps, {sw.Elapsed.TotalSeconds:0.0} s.");

        var text = $"Re: {r.Re.ToDecimalString(digits)}\nIm: {r.Im.ToDecimalString(digits)}\nZoom: {outZoom.ToString("G6", inv)}\n";
        File.WriteAllText(output, text);
        Console.WriteLine($"Wrote {output} (zoom {outZoom:G3}).");
        return 0;
    }
}
