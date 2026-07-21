namespace Jodu.Desktop.Services;

internal static class AppIcons
{
    public static Icon Load()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (File.Exists(path))
                    return new Icon(path);
            }
            catch
            {
                // try next
            }
        }

        try
        {
            var fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (fromExe is not null)
                return fromExe;
        }
        catch
        {
            // fall through
        }

        return SystemIcons.Application;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ui", "icon.ico");
        yield return Path.Combine(baseDir, "icon.ico");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ui", "public", "icon.ico"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "ui", "public", "icon.ico"));
    }
}
