using System.IO;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AiFocus;

public sealed class BrowserTab
{
    public int Id { get; set; }
    public int WindowId { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Favicon as a PNG data URL (extension v0.3+).</summary>
    public string Icon { get; set; } = "";
    public string Provider { get; set; } = "";
    /// <summary>Active tab of the OS-foreground browser window.</summary>
    public bool Focused { get; set; }
    /// <summary>Null when no content script reports for this tab (old extension, page not yet loaded).</summary>
    public bool? Generating { get; set; }
}
public sealed class BrowserSnapshot
{
    public string Type { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Browser { get; set; } = "";
    public List<BrowserTab> Tabs { get; set; } = new();
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
}
public sealed class BrowserHub : IDisposable
{
    public static string PipeName => "AiFocus-" + Environment.UserName;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, BrowserSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, PipeClient> _clients = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private sealed class PipeClient
    {
        public required NamedPipeServerStream Pipe { get; init; }
        public required StreamWriter Writer { get; init; }
        public readonly SemaphoreSlim WriteLock = new(1);
        public async Task SendAsync(string text)
        {
            await WriteLock.WaitAsync();
            try { await Writer.WriteLineAsync(text); }
            finally { WriteLock.Release(); }
        }
    }
    public BrowserHub() => _ = Task.Run(AcceptLoop);

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleClient(connected), _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Browser pipe listener: " + ex.Message);
                try { await Task.Delay(1000, _stop.Token); } catch { break; }
            }
            finally { server?.Dispose(); }
        }
    }
    private async Task HandleClient(NamedPipeServerStream pipe)
    {
        string? clientProfile = null;
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        {
            var client = new PipeClient { Pipe = pipe, Writer = writer };
            try
            {
                while (!_stop.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) break;
                    if (line.Length > 1024 * 1024) continue;
                    BrowserSnapshot? snapshot = null;
                    try { snapshot = JsonSerializer.Deserialize<BrowserSnapshot>(line, _json); }
                    catch (JsonException) { /* Ignore malformed payloads. */ }
                    if (snapshot?.Type != "snapshot" ||
                        string.IsNullOrWhiteSpace(snapshot.ProfileId) ||
                        snapshot.ProfileId.Length > 120) continue;
                    snapshot.ReceivedAtUtc = DateTime.UtcNow;
                    snapshot.Tabs = snapshot.Tabs.Take(500).Where(t => t.Url.Length < 4096).ToList();
                    clientProfile = snapshot.ProfileId;
                    _clients[clientProfile] = client;
                    _snapshots[clientProfile] = snapshot;
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            finally
            {
                if (clientProfile != null && _clients.TryGetValue(clientProfile, out var current)
                    && ReferenceEquals(current, client))
                {
                    _clients.TryRemove(clientProfile, out _);
                    _snapshots.TryRemove(clientProfile, out _);
                }
            }
        }
    }

    public IReadOnlyList<(string profile, string browser, BrowserTab tab)> CurrentTabs()
    {
        var now = DateTime.UtcNow;
        return _snapshots.Values
            .Where(s => now - s.ReceivedAtUtc < TimeSpan.FromMinutes(3))
            .SelectMany(s => s.Tabs.Select(t => (s.ProfileId, s.Browser, t)))
            .ToList();
    }
    public async Task<bool> ActivateAsync(SessionEntry session)
    {
        if (session.ProfileId == null || !_clients.TryGetValue(session.ProfileId, out var client)) return false;
        try
        {
            await client.SendAsync(JsonSerializer.Serialize(new
            {
                type = "activate", tabId = session.BrowserTabId, windowId = session.BrowserWindowId
            }));
            return true;
        }
        catch (IOException) { return false; }
    }
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
