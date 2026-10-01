using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace VnPresence;

internal static class SelfTests
{
    public static void Run()
    {
        var report = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); report.Add("PASS " + name); }
        try
        {
            Check(new Settings().ClientId == Settings.DefaultClientId, "Fresh install uses shared Discord application");
            Check(WindowsStartup.Command(@"C:\Apps with spaces\VN Presence.exe") == "\"C:\\Apps with spaces\\VN Presence.exe\" --background",
                "Windows startup quotes the executable path and launches into the tray");
            Check(new Settings().ProfileUrl == null && new Settings().Games.Count == 0, "Fresh installs contain no profile or saved game data");
            Check(!new Settings().GuideDismissed &&
                JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { GuideDismissed = true }))?.GuideDismissed == true,
                "Guide appears on first launch and its dismissal survives saving");
            Check(VnImage.ValidCustomUrl("https://example.com/cover.png") && VnImage.ValidCustomUrl("http://example.com/cover.jpg") &&
                !VnImage.ValidCustomUrl("file:///C:/cover.png") && !VnImage.ValidCustomUrl("javascript:alert(1)") &&
                !VnImage.ValidCustomUrl("https://user:pass@example.com/cover.png") &&
                !VnImage.ValidCustomUrl("https://example.com/" + new string('a', 300)), "Custom cover accepts web URLs within Discord's length limit");
            Check(MainForm.CenterSquare(new(10, 20, 100, 200)) == new System.Drawing.RectangleF(10, 70, 100, 100) &&
                MainForm.CenterSquare(new(10, 20, 200, 100)) == new System.Drawing.RectangleF(60, 20, 100, 100),
                "Crop overlay centers a square on portrait and landscape covers");
            Check(string.Equals(Detection.ExecutablePath(Environment.ProcessId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                "Limited-access process query returns the full executable path");
            Check(Detection.ExecutablePath(-1) == null, "Inaccessible process lookup safely returns no path");
            Check(VndbClient.NormalizeSearch("Sayonara wo Oshiete") == "Sayonara o Oshiete", "Romanized English search normalization");
            var vn = new Novel("v17", "Ever17", "Fallback", "ja", [new("en", "English title", false), new("ja", "ネイティブタイトル", true)], new("https://example.com/cover.jpg", null), [new("CRAFTWORK", "p526")]);
            Check(vn.NativeTitle == "ネイティブタイトル", "Original language selected over English/romanization");
            Check((vn with { Titles = [] }).NativeTitle == "Fallback", "Missing native title fallback");
            Check((vn with { Titles = [], Alttitle = null }).NativeTitle == "Ever17", "Missing alternate title fallback");
            var links = new List<GameLink> { new(@"C:\Games\one\game.exe", vn), new(@"C:\Games\two\game.exe", vn with { Id = "v18" }) };
            var processes = new List<RunningGame> { new(1, @"c:\games\one\GAME.exe", "one", 100), new(2, @"C:\Games\two\game.exe", "two", 200) };
            Check(Detection.Choose(processes, links, 1, 2)?.Link.Novel.Id == "v17", "Foreground and case-insensitive full path matching");
            Check(Detection.Choose(processes, links, 99, 1)?.Process.Pid == 1, "Alt-tab retains previous running game");
            Check(Detection.Choose([processes[1]], links, 99, 1)?.Process.Pid == 2, "Closed game switches to remaining game");
            Check(Detection.Choose([], links, 0, 1) == null, "All games closed clears presence");
            Check(Detection.Choose([new(3, @"C:\Other\game.exe", "wrong", 0)], links, 3, 0) == null, "Same executable filename in another folder does not match");
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordRpc.Activity(links[0], 100)));
            Check(payload.RootElement.GetProperty("name").GetString() == "ネイティブタイトル" && payload.RootElement.GetProperty("details").GetString() == "CRAFTWORK", "Discord sends title and brand on separate lines");
            Check(payload.RootElement.GetProperty("assets").GetProperty("large_image").GetString() == vn.Image!.Url, "Discord sends VNDB artwork URL");
            var customCover = links[0] with { Cover = new("https://example.com/release.jpg", null) };
            using var coverPayload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordRpc.Activity(customCover, 100)));
            Check(coverPayload.RootElement.GetProperty("assets").GetProperty("large_image").GetString() == customCover.Cover!.Url &&
                coverPayload.RootElement.GetProperty("assets").GetProperty("large_url").GetString() == vn.GameUrl &&
                JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(customCover))?.Cover == customCover.Cover,
                "Release cover overrides artwork, preserves game link, and survives saving");
            Check(payload.RootElement.GetProperty("assets").GetProperty("large_url").GetString() == "https://vndb.org/v17" && payload.RootElement.GetProperty("details_url").GetString() == "https://vndb.org/p526", "Cover links to VN and brand links to its producer");
            Check((vn with { Developers = [new("Other brand", "p123")] }).BrandUrl == "https://vndb.org/p123", "Producer link follows VN metadata");
            Check((vn with { Developers = [new("Old cached brand")] }).BrandUrl == null, "Missing producer IDs do not generate broken links");
            Check((vn with { Developers = [new("One", "p1"), new("Two", "p2")] }).BrandUrl == vn.GameUrl,
                "Multiple brands link to the VN page listing all producers");
            Check(!payload.RootElement.TryGetProperty("state", out _) && !payload.RootElement.TryGetProperty("buttons", out _), "Missing progress field is omitted, not sent as null");
            Check(Settings.NormalizeProfileUrl(" https://vndb.org/u12345/ ") == "https://vndb.org/u12345" &&
                Settings.NormalizeProfileUrl("u12345") == "https://vndb.org/u12345" &&
                Settings.NormalizeProfileUrl("https://vndb.org.evil.example/u12345") == null &&
                Settings.NormalizeProfileUrl("https://vndb.org/v17") == null, "Profile links accept only VNDB user pages");
            using var profilePayload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordRpc.Activity(links[0], 100, profileUrl: "https://vndb.org/u12345")));
            Check(profilePayload.RootElement.GetProperty("buttons")[0].GetProperty("url").GetString() == "https://vndb.org/u12345" &&
                profilePayload.RootElement.GetProperty("buttons")[0].GetProperty("label").GetString() == "VNDB profile",
                "Configured profile adds the correct activity button");
            Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { ProfileUrl = "https://vndb.org/u12345" }))?.ProfileUrl == "https://vndb.org/u12345",
                "Profile preference survives saving and loading");
            Novel Named(string title) => vn with { Title = title, Alttitle = null, Titles = [] };
            var automatic = new GameLink("game.exe", Named("Kanon"));
            var alternatePrefix = automatic with { WindowTitlePrefix = "サクラノ詩" };
            Check(alternatePrefix.Progress("サクラノ詩 Re:Graphic 10th Anniversary edition - Ver1.0.0 [第Ⅰ章]") == "Re:Graphic 10th Anniversary edition - Ver1.0.0 [第Ⅰ章]" &&
                alternatePrefix.Progress("サクラノ詩別作品 Chapter 1") == null &&
                (alternatePrefix with { WindowTitlePrefix = "" }).Progress("Kanon January 7th") == "January 7th" &&
                JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(alternatePrefix))?.WindowTitlePrefix == "サクラノ詩",
                "Window title prefix overrides VNDB matching, respects boundaries, and persists");
            var adjusted = automatic with { RemoveProgressText = "Ver2.0.2" };
            var multipleRemovals = automatic with { RemoveProgressText = " Ver1.0.0 ; R18 ;; " };
            Check(multipleRemovals.Progress("Kanon wordVer1.0.0wordR18end") == "word word end" &&
                multipleRemovals.Progress("Kanon word  Ver1.0.0   word") == "word word",
                "Removing phrases keeps surrounding words separated without duplicate spaces");
            Check(multipleRemovals.Progress("Kanon Ver1.0.0 - Chapter 2 - R18") == "Chapter 2" &&
                multipleRemovals.Progress("Kanon ver1.0.0 - Chapter 3 - r18") == "Chapter 3" &&
                (automatic with { RemoveProgressText = "foo, bar" }).Progress("Kanon foo, bar - Chapter 1") == "Chapter 1",
                "Semicolon-separated removal handles multiple phrases and keeps commas literal");
            Check(adjusted.Progress("Kanon - Ver2.0.2, January 7th") == "January 7th" &&
                adjusted.Progress("Kanon - ver2.0.2, January 8th") == "January 8th" &&
                adjusted.Progress("Kanon - Ver2.0.2") == null &&
                JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(adjusted))?.RemoveProgressText == "Ver2.0.2",
                "Automatic adjustments remove literal text, keep updates, hide empty details, and persist");
            var hidden = automatic with { CustomProgress = "" };
            var custom = automatic with { CustomProgress = " My chapter " };
            Check(automatic.Progress("Kanon January 7th") == "January 7th" && hidden.Progress("Kanon January 7th") == null &&
                custom.Progress("Kanon January 7th") == "My chapter", "Per-game third line supports automatic, hidden, and custom text");
            Check(JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(hidden))?.CustomProgress == "" &&
                JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(custom))?.CustomProgress == custom.CustomProgress,
                "Third-line preferences survive saving and loading");
            Check(Detection.Progress("Kanon January 7th, Thursday", Named("Kanon")) == "January 7th, Thursday" &&
                Detection.Progress("Sayonara wo Oshiete Day 10, The girl in the schoolyard", Named("Sayonara o Oshiete ~Comment te Dire Adieu~")) == "Day 10, The girl in the schoolyard" &&
                Detection.Progress("Amatsutsumi R18 - Hotaru, Chapter 13 -", Named("Amatsutsumi")) == "Hotaru, Chapter 13" &&
                Detection.Progress("Amatsutsumi R18 - Hotaru, Epilogue 2 -", Named("Amatsutsumi")) == "Hotaru, Epilogue 2" &&
                Detection.Progress("Amatsutsumi R18 - Hotaru, After Story -", Named("Amatsutsumi")) == "Hotaru, After Story" &&
                Detection.Progress("Rewrite", Named("Rewrite")) == null &&
                Detection.Progress("RewriteAnotherGame", Named("Rewrite")) == null,
                "Arbitrary window text after native or romanized game title, without matching another title prefix");
            using var progressPayload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordRpc.Activity(links[0], 100, "Hotaru, Chapter 13")));
            Check(progressPayload.RootElement.GetProperty("state").GetString() == "Hotaru, Chapter 13", "Progress occupies third activity line");
            Check(payload.RootElement.GetProperty("timestamps").GetProperty("start").GetInt64() == 100 &&
                progressPayload.RootElement.GetProperty("timestamps").GetProperty("start").GetInt64() == 100,
                "Chapter updates retain the original game process start time");
            var roundtrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Games = links }));
            Check(roundtrip?.Games[0].Novel.NativeTitle == vn.NativeTitle, "Saved links preserve Unicode metadata");
            TestRpc().GetAwaiter().GetResult();
            report.Add("PASS Discord IPC handshake, fragmented frames, ping/pong, update, clear, and error response");
            var live = new VndbClient().Search("v17").GetAwaiter().GetResult();
            Check(live.Count == 1 && live[0].Id == "v17" && live[0].Titles.Count > 0 && live[0].Image != null, "Live VNDB API fields and artwork retrieval");
            report.Add("Live native title: " + live[0].NativeTitle);
            var releaseCovers = new VndbClient().Covers("v4", CancellationToken.None).GetAwaiter().GetResult();
            Check(releaseCovers.Count > 1 && releaseCovers.All(c => c.Image?.Url.StartsWith("https://") == true),
                "Live VNDB release covers are available for CLANNAD");
            var romanized = new VndbClient().Search("Sayonara wo Oshiete").GetAwaiter().GetResult();
            Check(romanized.Any(n => n.Id == "v1200"), "Live romanized search finds Sayonara wo Oshiete");
            var english = new VndbClient().Search("The House in Fata Morgana").GetAwaiter().GetResult();
            Check(english.Any(n => n.Id == "v12402"), "Live English title search finds The House in Fata Morgana");
            report.Add("Visible windows detected: " + Detection.Scan(true).Count);
        }
        catch (Exception e) { report.Add("FAIL " + e); Environment.ExitCode = 1; }
        File.WriteAllLines("self-test-results.txt", report);
    }
    private static async Task TestRpc()
    {
        var prefix = "vn-presence-test-" + Guid.NewGuid() + "-";
        using var server = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        async Task<JsonDocument> Read()
        {
            var header = new byte[8]; await server.ReadExactlyAsync(header, ct);
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
            await server.ReadExactlyAsync(body, ct); return JsonDocument.Parse(body);
        }
        async Task Send(int opcode, object value)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(value); var header = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header, opcode); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
            await server.WriteAsync(header.AsMemory(0, 3), ct); await server.WriteAsync(header.AsMemory(3), ct);
            await server.WriteAsync(body, ct); await server.FlushAsync(ct);
        }
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(ct);
            using var handshake = await Read();
            if (handshake.RootElement.GetProperty("client_id").GetString() != "123456789012345678") throw new Exception("Bad handshake");
            await Send(1, new { evt = "READY" });
            using var update = await Read();
            await Send(3, new { ping = true });
            using var pong = await Read();
            if (!pong.RootElement.GetProperty("ping").GetBoolean()) throw new Exception("Bad pong");
            await Send(1, new { nonce = update.RootElement.GetProperty("nonce").GetString(), data = new { } });
            using var clear = await Read();
            if (clear.RootElement.GetProperty("args").GetProperty("activity").ValueKind != JsonValueKind.Null) throw new Exception("Bad clear");
            await Send(1, new { nonce = clear.RootElement.GetProperty("nonce").GetString(), data = new { } });
            using var error = await Read();
            await Send(1, new { nonce = error.RootElement.GetProperty("nonce").GetString(), evt = "ERROR", data = new { message = "Test rejection" } });
        }, ct);
        using var rpc = new DiscordRpc(prefix);
        await rpc.Connect("123456789012345678", ct);
        await rpc.Update(new { details = "テスト" }, ct);
        await rpc.Update(null, ct);
        try { await rpc.Update(null, ct); throw new Exception("Expected Discord error"); }
        catch (IOException e) when (e.Message.Contains("Test rejection")) { }
        await serverTask;
    }
}
