namespace Fractal;

static class Program
{
    /// <param name="args">Optional: a text file holding a location copied with Ctrl+C.</param>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? location = args.Length > 0 && File.Exists(args[0]) ? File.ReadAllText(args[0]) : null;
        Application.Run(new MainForm(location));
    }
}
