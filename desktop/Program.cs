namespace Jodu.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var startMinimized = args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(startMinimized));
    }
}
