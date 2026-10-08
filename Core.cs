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
public record GameLink(string Exe, Novel Novel, string? CustomProgress = null, string? RemoveProgressText = null, VnImage? Cover = null, VnImage? CustomCover = null, string? WindowTitlePrefix = null, string? TrimEdges = null, long LastPlayed = 0)
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
            progress = progress.Trim();
        }
        if (progress != null && !string.IsNullOrEmpty(TrimEdges))
        {
            var start = 0; var end = progress.Length;
            while (start < end && (char.IsWhiteSpace(progress[start]) || TrimEdges!.IndexOf(progress[start]) >= 0)) start++;
            while (end > start && (char.IsWhiteSpace(progress[end - 1]) || TrimEdges!.IndexOf(progress[end - 1]) >= 0)) end--;
            progress = progress.Substring(start, end - start);
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
        if (enabled) key.SetValue(Name, Command(Application.ExecutablePath));
        else key.DeleteValue(Name, false);
    }
}
public class Settings
{
    // Public application identifier, shared by all installations. Not a credential.
    public const string DefaultClientId = "1554580268285698068";
    public string ClientId { get; set; } = DefaultClientId;
    public string? ProfileUrl { get; set; }
    public bool ShowProfileButton { get; set; } = true;
    [JsonIgnore] public string? ActivityProfileUrl => ShowProfileButton ? NormalizeProfileUrl(ProfileUrl) : null;
    public bool GuideDismissed { get; set; }
    public string? VndbTokenProtected { get; set; }
    public static string? NormalizeProfileUrl(string? value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value?.Trim() ?? "", @"^(?:https://vndb\.org/)?(u[1-9][0-9]*)/?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? "https://vndb.org/" + match.Groups[1].Value.ToLowerInvariant() : null;
    }
    public List<GameLink> Games { get; set; } = [];
    public string GameSortOrder { get; set; } = "Added";
    public IEnumerable<GameLink> SortedGames() => GameSortOrder switch
    {
        "Alphabetical" => Games.OrderBy(g => g.Novel.NativeTitle, StringComparer.CurrentCultureIgnoreCase),
        "Last played" => Games.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Novel.NativeTitle, StringComparer.CurrentCultureIgnoreCase),
        _ => Games
    };
    public bool HasGame(string exe, string novelId) => Games.Any(g =>
        string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(g.Novel.Id, novelId, StringComparison.OrdinalIgnoreCase));
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
        if (File.Exists(FilePath)) File.Replace(FilePath + ".tmp", FilePath, null);
        else File.Move(FilePath + ".tmp", FilePath);
    }
    public string Export() => JsonSerializer.Serialize(new { Games, ProfileUrl, ShowProfileButton, GameSortOrder }, new JsonSerializerOptions { WriteIndented = true });
    public static Settings ReadBackup(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Games", out var games) || games.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Choose a VN Presence settings export.");
        var backup = JsonSerializer.Deserialize<Settings>(json) ?? throw new InvalidDataException("Empty settings file.");
        if (backup.Games == null || backup.Games.Count > 10000) throw new InvalidDataException("Invalid game list.");
        foreach (var game in backup.Games)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Exe) || !Path.IsPathFullyQualified(game.Exe) ||
                !game.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || game.Novel == null ||
                !System.Text.RegularExpressions.Regex.IsMatch(game.Novel.Id ?? "", @"^v[1-9][0-9]*$") ||
                string.IsNullOrWhiteSpace(game.Novel.Title) || game.Novel.Titles == null ||
                game.Novel.Titles.Any(t => t == null || string.IsNullOrWhiteSpace(t.Title)) ||
                (game.Novel.Developers?.Any(d => d == null || string.IsNullOrWhiteSpace(d.Name)) ?? false))
                throw new InvalidDataException("The export contains an invalid game.");
            foreach (var image in new[] { game.Novel.Image, game.Cover, game.CustomCover })
                if (image != null && (!VnImage.ValidCustomUrl(image.Url ?? "") ||
                    (image.Thumbnail != null && !VnImage.ValidCustomUrl(image.Thumbnail))))
                    throw new InvalidDataException("The export contains an invalid image URL.");
        }
        if (!string.IsNullOrWhiteSpace(backup.ProfileUrl) && NormalizeProfileUrl(backup.ProfileUrl) == null)
            throw new InvalidDataException("The export contains an invalid VNDB profile.");
        backup.ProfileUrl = NormalizeProfileUrl(backup.ProfileUrl);
        return backup;
    }
}
public sealed class VndbClient
{
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, List<Novel>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> previewCache = new();
    public async Task<byte[]> PreviewImage(string url, CancellationToken token)
    {
        if (previewCache.TryGetValue(url, out var cached)) return cached;
        using var response = await Http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        const int limit = 8 * 1024 * 1024;
        if (bytes.Length <= limit)
        {
            // ponytail: bounded session cache; no extra files or stale previews across launches.
            while (previewCache.Count > 0 && previewCache.Values.Sum(image => image.Length) + bytes.Length > limit)
                previewCache.Remove(previewCache.Keys.First());
            previewCache[url] = bytes;
        }
        return bytes;
    }
    private DateTime nextRequest;
    public static string NormalizeSearch(string query) =>
        System.Text.RegularExpressions.Regex.Replace(query.Trim(), @"\bwo\b", "o", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public static string SearchTitle(string caption)
    {
        caption = caption.Trim();
        const string months = "January|February|March|April|May|June|July|August|September|October|November|December";
        // Only strip recognizable suffix markers; punctuation inside the title stays intact.
        var match = System.Text.RegularExpressions.Regex.Match(caption,
            @"(?:\s+|[-–—|]\s*)(?:ver(?:sion)?\.?\s*\d+(?:\.\d+)*|v\s*\d+(?:\.\d+)+|R-?18\b|18\+(?=$|\s)|(?:day|chapter|epilogue)\s+\d+\b|(?:" +
            months + @")\s+\d{1,2}(?:st|nd|rd|th)?\b|\d{1,2}\s+(?:" + months + @")\b|\d{1,2}月\d{1,2}日).*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var title = match.Success ? caption[..match.Index].TrimEnd(' ', '-', '–', '—', '|') : caption.Trim();
        return string.IsNullOrWhiteSpace(title) ? caption.Trim() : title;
    }
    public async Task<List<Novel>> Search(string query)
    {
        query = SearchTitle(query);
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
        // ponytail: keep only recent searches; older ones can be fetched again.
        if (cache.Count >= 20) cache.Remove(cache.Keys.First());
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
            using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(), cancellationToken: token);
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
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(prefix) ? titles : new[] { prefix!.Trim() };
        foreach (var title in candidates)
        {
            var pattern = System.Text.RegularExpressions.Regex.Escape(title)
                .Replace(@"\ o\ ", @"\ (?:o|wo)\ ").Replace(@"\ wo\ ", @"\ (?:o|wo)\ ");
            var match = System.Text.RegularExpressions.Regex.Match(caption, "^" + pattern + @"(?=$|[\s,:\-–—～~])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var rest = caption.Substring(match.Length).Trim();
            return rest.Length == 0 ? null : rest;
        }
        return null;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int capacity);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags,
        System.Text.StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        out long created, out long exited, out long kernel, out long user);
    public static string? ExecutablePath(int pid) => ExecutablePath(pid, out _);
    private static string? ExecutablePath(int pid, out long started)
    {
        started = 0;
        using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return null;
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
        started = DateTimeOffset.FromFileTime(created).ToUnixTimeSeconds();
        var path = new System.Text.StringBuilder(32768);
        var size = (uint)path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
    }
    public static int ForegroundPid() { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return (int)pid; }
    private static string WindowTitle(int pid)
    {
        var caption = "";
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != pid || !IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
            var text = new System.Text.StringBuilder(Math.Min(GetWindowTextLength(window), 32767) + 1);
            GetWindowText(window, text, text.Capacity); caption = text.ToString();
            return false;
        }, IntPtr.Zero);
        return caption;
    }
    public static List<RunningGame> Scan(bool windowsOnly, List<GameLink>? links = null)
    {
        var games = new List<RunningGame>();
        if (links?.Count == 0) return games;
        var names = links == null ? null : new HashSet<string>(links.Select(l => Path.GetFileName(l.Exe)), StringComparer.OrdinalIgnoreCase);
        // Only process IDs and filenames; avoid collecting every process's thread information.
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            var pid = (int)entry.Pid;
            if (pid == Program.ProcessId || (names != null && !names.Contains(entry.Name))) continue;
            var caption = WindowTitle(pid);
            if (windowsOnly && string.IsNullOrWhiteSpace(caption)) continue;
            var path = ExecutablePath(pid, out var started);
            if (path != null) games.Add(new(pid, path, caption, started));
        } while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); // ERROR_NO_MORE_FILES
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
