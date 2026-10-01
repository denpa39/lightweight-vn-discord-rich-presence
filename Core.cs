using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace VnPresence;

public record VnTitle(string Lang, string Title, bool Main, string? Latin = null);
public record VnImage(string Url, string? Thumbnail)
{
    public static bool ValidCustomUrl(string value) => value.Length <= 300 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
}
public record CoverChoice(string Label, VnImage? Image)
{
    public override string ToString() => Label;
}
public record Developer(string Name, string? Id = null);
public record Novel(string Id, string Title, string? Alttitle, string Olang, List<VnTitle> Titles, VnImage? Image, List<Developer>? Developers = null)
{
    [JsonIgnore] public string? Brand => Developers is { Count: > 0 }
        ? string.Join(", ", Developers.Select(d => d.Name)) : null;
    [JsonIgnore] public string ActivityLine => NativeTitle + (Brand == null ? "" : "\n" + Brand);
    [JsonIgnore] public string GameUrl => "https://vndb.org/" + Id;
    [JsonIgnore] public string? BrandUrl => Developers is { Count: 1 } &&
        System.Text.RegularExpressions.Regex.IsMatch(Developers[0].Id ?? "", @"^p\d+$")
        ? "https://vndb.org/" + Developers[0].Id : Developers is { Count: > 1 } ? GameUrl : null;
    [JsonIgnore] public string NativeTitle => Titles.FirstOrDefault(t => t.Lang == Olang)?.Title
        ?? Titles.FirstOrDefault(t => t.Main)?.Title ?? Alttitle ?? Title;
    public override string ToString() => $"{NativeTitle}  ·  {Title}  ({Id})";
}
public record GameLink(string Exe, Novel Novel, string? CustomProgress = null, string? RemoveProgressText = null, VnImage? Cover = null, VnImage? CustomCover = null, string? WindowTitlePrefix = null)
{
    public string? Progress(string caption)
    {
        if (CustomProgress != null) return string.IsNullOrWhiteSpace(CustomProgress) ? null : CustomProgress.Trim();
        var progress = Detection.Progress(caption, Novel, WindowTitlePrefix);
        if (progress != null && !string.IsNullOrWhiteSpace(RemoveProgressText))
        {
            foreach (var phrase in RemoveProgressText.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                progress = progress.Replace(phrase, " ", StringComparison.OrdinalIgnoreCase);
            progress = System.Text.RegularExpressions.Regex.Replace(progress, @"\s+", " ");
            progress = progress.Trim(' ', '-', '–', '—', ':', ',');
        }
        return string.IsNullOrWhiteSpace(progress) ? null : progress;
    }
    public override string ToString() => $"{Novel.NativeTitle}  —  {Exe}";
}
public record RunningGame(int Pid, string Exe, string Caption, long Started)
{
    public override string ToString() => $"{Caption}  [{Path.GetFileName(Exe)} · {Pid}]";
}
public static class WindowsStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "VN Presence";
    public static bool Enabled
    {
        get { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(Name) is string; }
    }
    public static string Command(string exe) => $"\"{exe}\" --background";
    public static void Set(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) key.SetValue(Name, Command(Environment.ProcessPath ?? throw new IOException("Application path is unavailable.")));
        else key.DeleteValue(Name, false);
    }
}
public class Settings
{
    // Public application identifier, shared by all installations. Not a credential.
    public const string DefaultClientId = "1554580268285698068";
    public string ClientId { get; set; } = DefaultClientId;
    public string? ProfileUrl { get; set; }
    public bool GuideDismissed { get; set; }
    public static string? NormalizeProfileUrl(string? value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value?.Trim() ?? "", @"^(?:https://vndb\.org/)?(u[1-9][0-9]*)/?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? "https://vndb.org/" + match.Groups[1].Value.ToLowerInvariant() : null;
    }
    public List<GameLink> Games { get; set; } = [];
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VnPresence");
    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    public static Settings Load()
    {
        var settings = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Settings are empty.") : new();
        if (string.IsNullOrWhiteSpace(settings.ClientId)) settings.ClientId = DefaultClientId;
        return settings;
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
public sealed class VndbClient
{
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, List<Novel>> cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextRequest;
    public static string NormalizeSearch(string query) =>
        System.Text.RegularExpressions.Regex.Replace(query.Trim(), @"\bwo\b", "o", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public async Task<List<Novel>> Search(string query)
    {
        query = query.Trim();
        if (cache.TryGetValue(query, out var cached)) return cached;
        if (DateTime.UtcNow < nextRequest) throw new InvalidOperationException("Please wait a few seconds before searching again.");
        nextRequest = DateTime.UtcNow.AddSeconds(3);
        var id = System.Text.RegularExpressions.Regex.Match(query, @"^(?:https?://vndb\.org/)?(v\d+)/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var normalized = NormalizeSearch(query);
        object filters = new[] { id.Success ? "id" : "search", "=", id.Success ? id.Groups[1].Value.ToLowerInvariant() : query };
        if (!id.Success && normalized != query)
            filters = new object[] { "or", filters, new[] { "search", "=", normalized } };
        using var response = await Http.PostAsJsonAsync("https://api.vndb.org/kana/vn", new
        {
            filters,
            sort = id.Success || normalized != query ? "id" : "searchrank",
            fields = "title,alttitle,olang,titles.lang,titles.title,titles.main,titles.latin,image.url,image.thumbnail,developers.id,developers.name",
            results = 30
        });
        if ((int)response.StatusCode == 429)
        {
            nextRequest = DateTime.UtcNow.AddMinutes(1);
            throw new InvalidOperationException("VNDB is rate limiting requests. Try again in a minute.");
        }
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<VnResponse>();
        return cache[query] = data?.Results ?? [];
    }
    private record VnResponse(List<Novel> Results);
    public async Task<List<CoverChoice>> Covers(string vnId, CancellationToken token)
    {
        var covers = new List<CoverChoice>();
        for (var page = 1; ; page++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, (nextRequest - DateTime.UtcNow).TotalSeconds)), token);
            nextRequest = DateTime.UtcNow.AddSeconds(3);
            using var response = await Http.PostAsJsonAsync("https://api.vndb.org/kana/release", new
            {
                filters = new object[] { "vn", "=", new[] { "id", "=", vnId } },
                fields = "id,title,released,images.url,images.thumbnail,images.type,images.vn",
                sort = "released", results = 100, page
            }, token);
            response.EnsureSuccessStatusCode();
            using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            foreach (var release in data.RootElement.GetProperty("results").EnumerateArray())
                foreach (var image in release.GetProperty("images").EnumerateArray())
                {
                    var type = image.GetProperty("type").GetString();
                    var imageVn = image.GetProperty("vn").GetString();
                    if (type is not ("pkgfront" or "dig") || (imageVn != null && imageVn != vnId)) continue;
                    var url = image.GetProperty("url").GetString()!;
                    if (covers.Any(c => c.Image?.Url == url)) continue;
                    covers.Add(new($"{release.GetProperty("released").GetString()} · {release.GetProperty("title").GetString()} ({release.GetProperty("id").GetString()}) · {type}",
                        new(url, image.GetProperty("thumbnail").GetString())));
                }
            if (!data.RootElement.GetProperty("more").GetBoolean()) return covers;
        }
    }
}
public static class Detection
{
    public static string? Progress(string caption, Novel novel, string? prefix = null)
    {
        var titles = novel.Titles.SelectMany(t => new[] { t.Title, t.Latin })
            .Concat(new[] { novel.Title, novel.Alttitle })
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!)
            .SelectMany(t => new[] { t, t.Split('~', '～')[0].Trim() })
            .Distinct().OrderByDescending(t => t.Length);
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(prefix) ? titles : new[] { prefix.Trim() };
        foreach (var title in candidates)
        {
            var pattern = System.Text.RegularExpressions.Regex.Escape(title)
                .Replace(@"\ o\ ", @"\ (?:o|wo)\ ").Replace(@"\ wo\ ", @"\ (?:o|wo)\ ");
            var match = System.Text.RegularExpressions.Regex.Match(caption, "^" + pattern + @"(?=$|[\s,:\-–—～~])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var rest = caption[match.Length..].Trim(' ', '-', '–', '—', ':', ',');
            rest = System.Text.RegularExpressions.Regex.Replace(rest, @"^R18\b\s*[-–—:]?\s*", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(' ', '-', '–', '—');
            return rest.Length == 0 ? null : rest;
        }
        return null;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags,
        System.Text.StringBuilder path, ref uint size);
    public static string? ExecutablePath(int pid)
    {
        using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return null;
        var path = new System.Text.StringBuilder(32768);
        var size = (uint)path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
    }
    public static int ForegroundPid() { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return (int)pid; }
    public static List<RunningGame> Scan(bool windowsOnly)
    {
        var games = new List<RunningGame>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || (windowsOnly && string.IsNullOrWhiteSpace(process.MainWindowTitle))) continue;
                    var path = ExecutablePath(process.Id);
                    if (path != null) games.Add(new(process.Id, path, process.MainWindowTitle,
                        new DateTimeOffset(process.StartTime).ToUnixTimeSeconds()));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return games;
    }
    public static (RunningGame Process, GameLink Link)? Choose(List<RunningGame> running, List<GameLink> links, int foreground, int previous)
    {
        var matches = (from p in running from l in links where string.Equals(p.Exe, l.Exe, StringComparison.OrdinalIgnoreCase)
                       orderby p.Pid == foreground descending, p.Pid == previous descending, p.Started descending
                       select (p, l)).ToList();
        return matches.Count > 0 ? matches[0] : null;
    }
}
