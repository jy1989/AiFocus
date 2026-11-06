using System.IO;
using System.Text.Json;

namespace AiCockpit;

public sealed class SessionPreference
{
    public string Title { get; set; } = "";
    public string Workspace { get; set; } = "";
    public bool Favorite { get; set; }
    public bool NeedsAttention { get; set; }
    /// <summary>Last time the user opened this session from AI Cockpit; replies older than this count as seen.</summary>
    public DateTime? SeenUtc { get; set; }
}

public sealed class SessionStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiCockpit", "sessions.json");
    private readonly Dictionary<string, SessionPreference> _preferences = new(StringComparer.Ordinal);
    public SessionStore()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, SessionPreference>>(File.ReadAllText(_path));
                if (data != null) foreach (var item in data) _preferences[item.Key] = item.Value;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Load preferences: " + ex.Message); }
    }
    public void Apply(SessionEntry session)
    {
        if (!_preferences.TryGetValue(session.PersistenceKey, out var p)) return;
        session.CustomTitle = p.Title;
        session.Workspace = p.Workspace;
        session.Favorite = p.Favorite;
        session.NeedsAttention = p.NeedsAttention;
    }
    public void Save(SessionEntry session)
    {
        _preferences.TryGetValue(session.PersistenceKey, out var previous);
        _preferences[session.PersistenceKey] = new SessionPreference
        {
            Title = session.CustomTitle,
            Workspace = session.Workspace,
            Favorite = session.Favorite,
            NeedsAttention = session.NeedsAttention,
            SeenUtc = previous?.SeenUtc
        };
        Write();
    }
    public DateTime SeenUtc(string key) =>
        _preferences.TryGetValue(key, out var p) && p.SeenUtc is { } seen ? seen : DateTime.MinValue;
    public void MarkSeen(string key, DateTime utc)
    {
        if (!_preferences.TryGetValue(key, out var p)) _preferences[key] = p = new SessionPreference();
        p.SeenUtc = utc;
        Write();
    }
    private void Write()
    {
        var parent = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(parent);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_preferences, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
