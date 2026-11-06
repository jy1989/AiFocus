using System.IO;

namespace AiFocus;

/// <summary>Per-user data folder, %LOCALAPPDATA%\AiFocus.</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiFocus");

    /// <summary>Demo instances keep their own settings so they never touch the real ones.</summary>
    public static void UseDemoFolder() => DataDir += "-demo";

    public static string File(string name) => Path.Combine(DataDir, name);

    /// <summary>
    /// The app used to be called AI Cockpit: carry settings, favourites and to-do marks over from
    /// %LOCALAPPDATA%\AiCockpit the first time AI Focus runs.
    /// </summary>
    public static void MigrateFromAiCockpit()
    {
        var legacy = Path.Combine(Path.GetDirectoryName(DataDir)!, "AiCockpit");
        if (Directory.Exists(DataDir) || !Directory.Exists(legacy)) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            foreach (var name in new[] { "settings.json", "sessions.json" })
            {
                var source = Path.Combine(legacy, name);
                if (System.IO.File.Exists(source)) System.IO.File.Copy(source, File(name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Migrate AiCockpit data: " + ex.Message);
        }
    }
}
