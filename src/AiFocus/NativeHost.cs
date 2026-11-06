using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AiFocus;

/// <summary>
/// Registers the browser bridge for Chrome and Edge at startup, so installing the extension is just
/// "Load unpacked". Possible because the extension's manifest carries a fixed "key", which pins its
/// ID regardless of the folder it is loaded from. Per-user registry only; no admin rights.
/// </summary>
public static class NativeHost
{
    public const string Name = "com.aifocus.bridge";
    /// <summary>Derived from the "key" in extension/manifest.json.</summary>
    public const string ExtensionId = "gocjfjagegiifclaphpmcfamjlbjdgjj";

    private static readonly string[] RegistryPaths =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\" + Name,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + Name
    };

    public static void EnsureRegistered()
    {
        try
        {
            var bridge = FindBridge();
            if (bridge == null)
            {
                Log.Write("native host: AiFocus.Bridge.exe not found, browser extension cannot connect");
                return;
            }
            // Keep origins registered earlier (an extension loaded before the fixed key existed has a
            // folder-derived ID) so it keeps working until it is reloaded.
            var origins = new SortedSet<string>(StringComparer.Ordinal) { $"chrome-extension://{ExtensionId}/" };
            foreach (var path in RegistryPaths) foreach (var origin in RegisteredOrigins(path)) origins.Add(origin);

            var manifestPath = AppPaths.File("native-host.json");
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = Name,
                ["description"] = "AI Focus browser bridge",
                ["path"] = bridge,
                ["type"] = "stdio",
                ["allowed_origins"] = origins.ToArray()
            }, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(AppPaths.DataDir);
            if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != json) File.WriteAllText(manifestPath, json);

            foreach (var path in RegistryPaths)
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                if (key.GetValue("") as string != manifestPath) key.SetValue("", manifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            Log.Write("native host registration failed: " + ex.Message);
        }
    }

    private static IEnumerable<string> RegisteredOrigins(string registryPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        if (key?.GetValue("") is not string manifest || !File.Exists(manifest)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("allowed_origins", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(o => o.GetString()).OfType<string>().ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return Array.Empty<string>(); }
    }

    /// <summary>Next to the app, or (running from the repository) the newest build under src\AiFocus.Bridge\bin.</summary>
    private static string? FindBridge()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "AiFocus.Bridge.exe");
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "AiFocus.Bridge", "bin");
            if (!Directory.Exists(bin)) continue;
            return Directory.EnumerateFiles(bin, "AiFocus.Bridge.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }
}
