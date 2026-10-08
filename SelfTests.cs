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
            Check(VotePicker.TryVote("5.6", out var decimalVote) && decimalVote == 56 &&
                VotePicker.TryVote("10", out var topVote) && topVote == 100 && VotePicker.TryVote("", out var noVote) && noVote == null &&
                !VotePicker.TryVote("5.66", out _) && !VotePicker.TryVote("11", out _) && !VotePicker.TryVote("bad", out _),
                "Vote picker accepts decimal ratings and clearing, rejecting invalid scores");
            TestAccount().GetAwaiter().GetResult();
            report.Add("PASS VNDB account authentication, label changes, voting and encrypted token storage");
            Check(ActivityPreview.FormatTime(3752) == "1:02:32" && ActivityPreview.FormatTime(59) == "0:59" &&
                ActivityPreview.FormatTime(3600) == "1:00:00" && ActivityPreview.FormatTime(90061) == "25:01:01",
                "Preview timer shows hours after sixty minutes");
            Check(WindowsStartup.Command(@"C:\Apps with spaces\VN Presence.exe") == "\"C:\\Apps with spaces\\VN Presence.exe\" --background",
                "Windows startup quotes the executable path and launches into the tray");
            Check(new Settings().ProfileUrl == null && new Settings().Games.Count == 0, "Fresh installs contain no profile or saved game data");
            var sorted = new Settings { Games = [
                new GameLink(@"C:\Games\z.exe", new Novel("v1","Zebra",null,"en",[],null), LastPlayed: 20),
                new GameLink(@"C:\Games\a.exe", new Novel("v2","Alpha",null,"en",[],null), LastPlayed: 30),
                new GameLink(@"C:\Games\n.exe", new Novel("v3","Never played",null,"en",[],null))] };
            Check(sorted.SortedGames().First().Novel.Id == "v1", "Added sort preserves the original game order");
            sorted.GameSortOrder = "Alphabetical";
            Check(sorted.SortedGames().Select(g=>g.Novel.Id).SequenceEqual(["v2","v3","v1"]), "Alphabetical sort uses displayed titles");
            sorted.GameSortOrder = "Last played";
            var restoredSort = Settings.ReadBackup(sorted.Export());
            Check(restoredSort.GameSortOrder == "Last played" && restoredSort.SortedGames().Select(g=>g.Novel.Id).SequenceEqual(["v2","v1","v3"]),
                "Last played puts recent games first and keeps dates and sort preference in backups");
            var profileSettings = new Settings { ProfileUrl = "https://vndb.org/u12345", ShowProfileButton = false };
            Check(profileSettings.ActivityProfileUrl == null && Settings.ReadBackup(profileSettings.Export()).ShowProfileButton == false &&
                profileSettings.ProfileUrl == "https://vndb.org/u12345", "Hiding the profile button retains its URL and survives export/import");
            profileSettings.ShowProfileButton = true;
            Check(profileSettings.ActivityProfileUrl == profileSettings.ProfileUrl, "Showing the profile button restores the saved user URL");
            var linked = new Settings { Games = [new GameLink(@"C:\Games\game.exe", new Novel("v1", "Game", null, "en", [], null))] };
            Check(linked.HasGame(@"C:\Other\game.exe", "v1") && linked.HasGame(@"c:\games\GAME.exe", "v2") &&
                !linked.HasGame(@"C:\Other\game.exe", "v2") && linked.Games.Count == 1,
                "Linking rejects an existing VNDB game or executable without replacing its settings");
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
            using (var source = new Bitmap(100, 200))
            {
                using (var graphics = Graphics.FromImage(source))
                { graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Blue, 0, 100, 100, 100); }
                var area = CoverImages.Crop(source.Size, 50, 100, 1);
                using var fitted = CoverImages.Render(source, true, area);
                using var smallPreview = CoverImages.Preview(source, new Size(50, 50));
                Check(smallPreview.Size == new Size(25, 50), "Cover previews retain aspect ratio and only allocate display-sized bitmaps");
                Check(fitted.Size == new Size(512, 512) && fitted.GetPixel(0, 256).A == 0 &&
                    fitted.GetPixel(256, 20).R > 240 && fitted.GetPixel(256, 490).B > 240,
                    "Fit pads a portrait square without removing the top or bottom");
                using var blurred = CoverImages.Render(source, true, area, blur: true);
                Check(blurred.GetPixel(0, 256).A == 0 && blurred.GetPixel(256, 250).R > 0 &&
                    blurred.GetPixel(256, 250).B > 0 && source.GetPixel(50, 99).R == 255,
                    "Blur softens cover detail, preserves transparent padding, and leaves the original unchanged");
                using var cropped = CoverImages.Render(source, false, CoverImages.Crop(source.Size, 50, 150, 1));
                Check(cropped.GetPixel(256, 256).B > 240 && CoverImages.Crop(source.Size, -100, 500, 2) == new RectangleF(0, 150, 50, 50),
                    "Crop supports moving and zooming and stays inside the image");
                using var landscape = new Bitmap(200, 100);
                using (var graphics = Graphics.FromImage(landscape)) graphics.Clear(Color.Red);
                using var wideFit = CoverImages.Render(landscape, true, CoverImages.Crop(landscape.Size, 100, 50, 1));
                Check(wideFit.GetPixel(256, 0).A == 0 && wideFit.GetPixel(20, 256).R > 240,
                    "Fit pads a landscape without removing its sides");
                var bytes = CoverImages.Encode(fitted);
                using var stream = new MemoryStream(bytes); using var decoded = Image.FromStream(stream);
                Check(decoded.Size == new Size(512, 512) && bytes.Length <= CoverImages.MaxUploadBytes &&
                    ((Bitmap)decoded).GetPixel(0, 256).A == 0 && ((Bitmap)decoded).GetPixel(256, 20).A == 255,
                    "PNG round trip preserves transparent padding and opaque cover pixels");
                Directory.CreateDirectory("release"); File.WriteAllBytes("release/cover-test.png", bytes);
                using (var opaque = new Bitmap(512, 512, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    opaque.Save("release/cover-test.jpg", System.Drawing.Imaging.ImageFormat.Jpeg);
                TestCoverUpload(bytes).GetAwaiter().GetResult();
                report.Add("PASS Cover upload sends PNG pixels and rejects invalid responses and failed requests");
            }
            Check(string.Equals(Detection.ExecutablePath(Program.ProcessId), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase),
                "Limited-access process query returns the full executable path");
            Check(Detection.ExecutablePath(-1) == null, "Inaccessible process lookup safely returns no path");
            Check(DiscordRpc.Short(new string('a', 121)).Length == 120 &&
                DiscordRpc.Short(string.Concat(Enumerable.Repeat("😀", 121))) == string.Concat(Enumerable.Repeat("😀", 120)),
                "Discord text limit preserves complete Unicode characters");
            Check(VndbClient.NormalizeSearch("Sayonara wo Oshiete") == "Sayonara o Oshiete", "Romanized English search normalization");
            Check(VndbClient.SearchTitle("HimaNatsu: Of Churches, Sunflowers, and Long Summers Ver1.0.0 - Big Sis Yomi - R18") ==
                "HimaNatsu: Of Churches, Sunflowers, and Long Summers" &&
                VndbClient.SearchTitle("NUKITASHI - Ver2.0.2") == "NUKITASHI" &&
                VndbClient.SearchTitle("Amatsutsumi R18 - Hotaru, Chapter 13 -") == "Amatsutsumi" &&
                VndbClient.SearchTitle("Sayonara wo Oshiete Day 10, Library") == "Sayonara wo Oshiete" &&
                VndbClient.SearchTitle("Kanon January 7th, Thursday") == "Kanon" &&
                VndbClient.SearchTitle("Summer Pockets Reflection Blue 7 May") == "Summer Pockets Reflection Blue",
                "Search strips recognized versions, ratings, and progress suffixes");
            Check(VndbClient.SearchTitle("Fate/stay night - Heaven's Feel") == "Fate/stay night - Heaven's Feel" &&
                VndbClient.SearchTitle("Re:write - long-time-no-see") == "Re:write - long-time-no-see" &&
                VndbClient.SearchTitle("https://vndb.org/v17") == "https://vndb.org/v17" &&
                VndbClient.SearchTitle("CLANNAD Full Voice") == "CLANNAD Full Voice" &&
                VndbClient.SearchTitle("Ver1.0.0") == "Ver1.0.0" &&
                VndbClient.SearchTitle("Title - Ver1.0.0") == VndbClient.SearchTitle(VndbClient.SearchTitle("Title - Ver1.0.0")),
                "Search cleanup preserves subtitles, URLs, edition names, and nonempty input");
            var vn = new Novel("v17", "Ever17", "Fallback", "ja", [new("en", "English title", false), new("ja", "ネイティブタイトル", true)], new("https://example.com/cover.jpg", null), [new("CRAFTWORK", "p526")]);
            Check(vn.NativeTitle == "ネイティブタイトル", "Original language selected over English/romanization");
            Check((vn with { Titles = [] }).NativeTitle == "Fallback", "Missing native title fallback");
            Check((vn with { Titles = [], Alttitle = null }).NativeTitle == "Ever17", "Missing alternate title fallback");
            var links = new List<GameLink> { new(@"C:\Games\one\game.exe", vn), new(@"C:\Games\two\game.exe", vn with { Id = "v18" }) };
            Check(Detection.Scan(false, []).Count == 0 &&
                Detection.Scan(false, [new(@"C:\NeverRuns\vn-presence-nonexistent-test.exe", vn)]).Count == 0,
                "Background detection skips empty libraries and unrelated executables");
            var visible = Detection.Scan(true);
            if (visible.Count > 0)
            {
                var target = visible[0];
                var filtered = Detection.Scan(false, [new(target.Exe, vn)]);
                Check(filtered.Any(p => p.Pid == target.Pid && p.Started == target.Started) &&
                    filtered.All(p => string.Equals(Path.GetFileName(p.Exe), Path.GetFileName(target.Exe), StringComparison.OrdinalIgnoreCase)),
                    "Filtered detection retains live matching processes and their original start times");
            }
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
            Check(!payload.RootElement.TryGetProperty("state", out _) && payload.RootElement.GetProperty("buttons").GetArrayLength() == 1 &&
                payload.RootElement.GetProperty("buttons")[0].GetProperty("label").GetString() == "View on VNDB" &&
                payload.RootElement.GetProperty("buttons")[0].GetProperty("url").GetString() == links[0].Novel.GameUrl,
                "Missing progress is omitted and the game page button appears without a profile");
            Check(Settings.NormalizeProfileUrl(" https://vndb.org/u12345/ ") == "https://vndb.org/u12345" &&
                Settings.NormalizeProfileUrl("u12345") == "https://vndb.org/u12345" &&
                Settings.NormalizeProfileUrl("https://vndb.org.evil.example/u12345") == null &&
                Settings.NormalizeProfileUrl("https://vndb.org/v17") == null, "Profile links accept only VNDB user pages");
            using var profilePayload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordRpc.Activity(links[0], 100, profileUrl: "https://vndb.org/u12345")));
            Check(profilePayload.RootElement.GetProperty("buttons").GetArrayLength() == 2 &&
                profilePayload.RootElement.GetProperty("buttons")[1].GetProperty("url").GetString() == "https://vndb.org/u12345" &&
                profilePayload.RootElement.GetProperty("buttons")[1].GetProperty("label").GetString() == "VNDB profile",
                "Configured profile adds the correct activity button");
            using var namedProfile = JsonDocument.Parse(JsonSerializer.Serialize(DiscordRpc.Activity(links[0], 100,
                profileUrl: "https://vndb.org/u12345", username: "den-pa")));
            Check(namedProfile.RootElement.GetProperty("buttons")[1].GetProperty("label").GetString() == "Visit den-pa’s VNDB profile" &&
                DiscordRpc.ProfileLabel(new string('x', 80)).Length <= 32 &&
                DiscordRpc.ProfileLabel(string.Concat(Enumerable.Repeat("😀", 20))).Length <= 32 &&
                DiscordRpc.ProfileLabel(" ") == "VNDB profile", "Profile labels use the connected username and fit Discord's button limit");
            Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { ProfileUrl = "https://vndb.org/u12345" }))?.ProfileUrl == "https://vndb.org/u12345",
                "Profile preference survives saving and loading");
            Novel Named(string title) => vn with { Title = title, Alttitle = null, Titles = [] };
            var automatic = new GameLink("game.exe", Named("Kanon"));
            var trimmedEdges = automatic with { TrimEdges = "-()" };
            Check(trimmedEdges.Progress("Kanon (- long-time-no-see -)") == "long-time-no-see" &&
                trimmedEdges.Progress("Kanon (-　long-time-no-see　-)") == "long-time-no-see" &&
                trimmedEdges.Progress("Kanon (- -)") == null &&
                automatic.Progress("Kanon - long-time-no-see -") == "- long-time-no-see -" &&
                JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(trimmedEdges))?.TrimEdges == "-()",
                "Edge trimming preserves internal punctuation, handles whitespace, and persists");
            var alternatePrefix = automatic with { WindowTitlePrefix = "サクラノ詩" };
            Check(alternatePrefix.Progress("サクラノ詩 Re:Graphic 10th Anniversary edition - Ver1.0.0 [第Ⅰ章]") == "Re:Graphic 10th Anniversary edition - Ver1.0.0 [第Ⅰ章]" &&
                alternatePrefix.Progress("サクラノ詩別作品 Chapter 1") == null &&
                (alternatePrefix with { WindowTitlePrefix = "" }).Progress("Kanon January 7th") == "January 7th" &&
                JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(alternatePrefix))?.WindowTitlePrefix == "サクラノ詩",
                "Window title prefix overrides VNDB matching, respects boundaries, and persists");
            var adjusted = automatic with { RemoveProgressText = "Ver2.0.2; -; ," };
            var multipleRemovals = automatic with { RemoveProgressText = " Ver1.0.0 ; R18 ;; " };
            Check(multipleRemovals.Progress("Kanon wordVer1.0.0wordR18end") == "word word end" &&
                multipleRemovals.Progress("Kanon word  Ver1.0.0   word") == "word word",
                "Removing phrases keeps surrounding words separated without duplicate spaces");
            Check(multipleRemovals.Progress("Kanon Ver1.0.0 - Chapter 2 - R18") == "- Chapter 2 -" &&
                multipleRemovals.Progress("Kanon ver1.0.0 - Chapter 3 - r18") == "- Chapter 3 -" &&
                (automatic with { RemoveProgressText = "foo, bar" }).Progress("Kanon foo, bar - Chapter 1") == "- Chapter 1",
                "Semicolon-separated removal handles multiple phrases and keeps commas literal");
            Check(adjusted.Progress("Kanon - Ver2.0.2, January 7th") == "January 7th" &&
                adjusted.Progress("Kanon - ver2.0.2, January 8th") == "January 8th" &&
                adjusted.Progress("Kanon - Ver2.0.2") == null &&
                JsonSerializer.Deserialize<GameLink>(JsonSerializer.Serialize(adjusted))?.RemoveProgressText == adjusted.RemoveProgressText,
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
                Detection.Progress("Amatsutsumi R18 - Hotaru, Chapter 13 -", Named("Amatsutsumi")) == "R18 - Hotaru, Chapter 13 -" &&
                Detection.Progress("Amatsutsumi R18 - Hotaru, Epilogue 2 -", Named("Amatsutsumi")) == "R18 - Hotaru, Epilogue 2 -" &&
                Detection.Progress("Amatsutsumi R18 - Hotaru, After Story -", Named("Amatsutsumi")) == "R18 - Hotaru, After Story -" &&
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
            var exportSettings = new Settings { Games = [links[0] with { CustomProgress = "Chapter 2", TrimEdges = "-()",
                RemoveProgressText = "R18", WindowTitlePrefix = "Game", Cover = vn.Image, CustomCover = vn.Image }],
                ProfileUrl = "https://vndb.org/u12345", GuideDismissed = true };
            var imported = Settings.ReadBackup(exportSettings.Export());
            Check(imported.Games[0].CustomProgress == "Chapter 2" && imported.Games[0].TrimEdges == "-()" &&
                imported.Games[0].RemoveProgressText == "R18" && imported.Games[0].WindowTitlePrefix == "Game" &&
                imported.Games[0].Cover == vn.Image && imported.Games[0].CustomCover == vn.Image &&
                imported.ProfileUrl == exportSettings.ProfileUrl && !imported.GuideDismissed,
                "Export/import retains game customizations and profile without device-specific preferences");
            var rejectedBackup = false;
            try { Settings.ReadBackup("{\"Games\":[null]}"); } catch (InvalidDataException) { rejectedBackup = true; }
            Check(rejectedBackup && Settings.ReadBackup(new Settings().Export()).Games.Count == 0,
                "Import rejects invalid games and accepts an empty backup");
            TestRpc().GetAwaiter().GetResult();
            report.Add("PASS Discord IPC handshake, fragmented frames, ping/pong, update, clear, and error response");
            var live = new VndbClient().Search("v17").GetAwaiter().GetResult();
            Check(live.Count == 1 && live[0].Id == "v17" && live[0].Titles.Count > 0 && live[0].Image != null, "Live VNDB API fields and artwork retrieval");
            report.Add("Live native title: " + live[0].NativeTitle);
            var previewClient = new VndbClient();
            var previewUrl = live[0].Image!.Thumbnail ?? live[0].Image!.Url;
            var firstPreview = previewClient.PreviewImage(previewUrl, CancellationToken.None).GetAwaiter().GetResult();
            var secondPreview = previewClient.PreviewImage(previewUrl, CancellationToken.None).GetAwaiter().GetResult();
            Check(firstPreview.Length > 0 && ReferenceEquals(firstPreview, secondPreview), "Repeated image preview reuses downloaded bytes");
            var releaseCovers = new VndbClient().Covers("v4", CancellationToken.None).GetAwaiter().GetResult();
            Check(releaseCovers.Count > 1 && releaseCovers.All(c => c.Image?.Url.StartsWith("https://") == true),
                "Live VNDB release covers are available for CLANNAD");
            var romanized = new VndbClient().Search("Sayonara wo Oshiete").GetAwaiter().GetResult();
            Check(romanized.Any(n => n.Id == "v1200"), "Live romanized search finds Sayonara wo Oshiete");
            var english = new VndbClient().Search("The House in Fata Morgana").GetAwaiter().GetResult();
            Check(english.Any(n => n.Id == "v12402"), "Live English title search finds The House in Fata Morgana");
            var windowSearch = new VndbClient().Search("HimaNatsu: Of Churches, Sunflowers, and Long Summers Ver1.0.0 - Big Sis Yomi - R18")
                .GetAwaiter().GetResult();
            Check(windowSearch.Any(n => n.Title.Contains("Himawari", StringComparison.OrdinalIgnoreCase) ||
                n.Titles.Any(t => t.Title.Contains("HimaNatsu", StringComparison.OrdinalIgnoreCase))),
                "Live VNDB search finds HimaNatsu from its full window caption");
            report.Add("Visible windows detected: " + Detection.Scan(true).Count);
        }
        catch (Exception e) { report.Add("FAIL " + e); Environment.ExitCode = 1; }
        File.WriteAllLines("self-test-results.txt", report);
    }
    internal static void TestCropEditor(CoverEditor editor, Size image)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var canvas = editor.Controls.OfType<PictureBox>().Single(p => p.TabStop);
        var crop = editor.Controls.OfType<RadioButton>().Single(r => r.Text == "Crop");
        var zoom = editor.Controls.OfType<TrackBar>().Single();
        var reset = editor.Controls.OfType<Button>().Single(b => b.Text == "Reset");
        void Mouse(string name, Point point, int delta = 0) => typeof(Control)
            .GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(canvas, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, delta)]);
        bool Same(RectangleF a, RectangleF b) => Math.Abs(a.X - b.X) < .01 && Math.Abs(a.Y - b.Y) < .01 && Math.Abs(a.Width - b.Width) < .01;
        crop.Checked = true; reset.PerformClick(); zoom.Value = 20;
        var bounds = CoverImages.ImageBounds(image, canvas.ClientSize); var before = editor.CropArea;
        var start = Point.Round(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        var end = new Point(start.X + 20, start.Y + 20); var far = new Point(start.X + 10000, start.Y + 10000);
        Mouse("OnMouseDown", start); Mouse("OnMouseMove", far); Mouse("OnMouseMove", end);
        var expected = CoverImages.Crop(image, before.X + before.Width / 2 + 20 * image.Width / bounds.Width,
            before.Y + before.Height / 2 + 20 * image.Height / bounds.Height, 2);
        Check(Same(editor.CropArea, expected), "Drag must follow the full-cover scale and recover after reaching an edge.");
        Mouse("OnMouseMove", far); Mouse("OnMouseUp", far);
        var edge = editor.CropArea; zoom.Value = 10; zoom.Value = 20;
        Check(Same(editor.CropArea, edge), "Zoom out and back must preserve the chosen position.");
        editor.Controls.OfType<RadioButton>().Single(r => r.Text == "Fit whole cover").Checked = true; crop.Checked = true;
        Check(Same(editor.CropArea, edge), "Switching Fit/Crop must preserve the crop position.");
        Mouse("OnMouseWheel", end, 120); Check(zoom.Value == 22, "Mouse wheel must zoom the crop.");
        Mouse("OnMouseDown", Point.Round(new(bounds.X + bounds.Width - 1, bounds.Y + bounds.Height - 1)));
        canvas.Capture = false; var released = editor.CropArea; Mouse("OnMouseMove", start);
        Check(Same(editor.CropArea, released), "Losing mouse capture must end a drag.");
        reset.PerformClick(); Check(zoom.Value == 10 && Same(editor.CropArea, CoverImages.Crop(image, image.Width / 2f, image.Height / 2f, 1)),
            "Reset must restore the centered crop and zoom.");
        zoom.Value = 20;
    }
    private sealed class AccountHandler : HttpMessageHandler
    {
        internal bool ReadOnly;
        internal JsonElement Saved;
        internal int Saves;
        internal string? SavedPath;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Headers.Authorization?.Scheme != "Token" || request.RequestUri?.Host != "api.vndb.org") throw new Exception("Account authorization must go only to VNDB");
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Patch)
            {
                Saves++; SavedPath = path;
                Saved = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
                return new(System.Net.HttpStatusCode.NoContent);
            }
            var json = path.EndsWith("authinfo") ? JsonSerializer.Serialize(new { id = "u1", username = "Test", permissions = ReadOnly ? new[] { "listread" } : new[] { "listread", "listwrite" } })
                : path.EndsWith("ulist_labels") ? "{\"labels\":[{\"id\":0,\"label\":\"No label\",\"private\":false},{\"id\":2,\"label\":\"Finished\",\"private\":false},{\"id\":7,\"label\":\"Voted\",\"private\":false},{\"id\":10,\"label\":\"Mine\",\"private\":true}]}"
                : "{\"results\":[{\"id\":\"v1\",\"vote\":55,\"labels\":[{\"id\":1},{\"id\":7},{\"id\":10}]}]}";
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
    private static async Task TestAccount()
    {
        const string token = "hsoo-ybws4-j8yb9-qxkw-5obay-px8to-bfyk";
        var encrypted = AccountToken.Protect(token);
        if (encrypted.Contains(token) || AccountToken.Unprotect(encrypted) != token || new Settings { VndbTokenProtected = encrypted }.Export().Contains("VndbToken")) throw new Exception("Token encryption or export isolation failed");
        using var handler = new AccountHandler(); using var http = new HttpClient(handler);
        var account = new VndbAccount(token, http); await account.Connect(CancellationToken.None);
        var labels = await account.Labels(CancellationToken.None); var entry = await account.Read("v1", CancellationToken.None);
        if (account.UserId != "u1" || labels.Count != 2 || !labels[1].Private || entry.Vote != 55) throw new Exception("Account fields were parsed incorrectly");
        await account.Save("v1", null, entry.Labels, [2, 10], CancellationToken.None);
        if (handler.Saved.GetProperty("vote").ValueKind != JsonValueKind.Null ||
            handler.Saved.GetProperty("labels_set")[0].GetInt32() != 2 || handler.Saved.GetProperty("labels_unset").GetArrayLength() != 1 ||
            handler.Saved.GetProperty("labels_unset")[0].GetInt32() != 1) throw new Exception("Label update must preserve custom labels and exclude virtual labels");
        handler.ReadOnly = true;
        try { await account.Connect(CancellationToken.None); throw new Exception("Read-only token was accepted"); }
        catch (InvalidOperationException) { }
        try { VndbAccount.Changes(50, [], [1, 2]); throw new Exception("Conflicting progress labels accepted"); }
        catch (InvalidOperationException) { }
    }
    internal static void TestAutoSave(VndbAccountPanel panel)
    {
        using var handler = new AccountHandler(); using var http = new HttpClient(handler);
        var account = new VndbAccount("hsoo-ybws4-j8yb9-qxkw-5obay-px8to-bfyk", http); account.Connect(CancellationToken.None).GetAwaiter().GetResult();
        void Field(string name, object value) => typeof(VndbAccountPanel).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, value);
        var list = (CheckedListBox)typeof(VndbAccountPanel).GetField("labels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        var picker = (VotePicker)typeof(VndbAccountPanel).GetField("vote", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        Field("account", account); Field("vnId", "v1"); Field("loadedId", "v1"); Field("original", new[] {1,10}); Field("loading", true);
        var form = panel.FindForm()!; var previousSize = form.Size;
        form.Size = form.MinimumSize; form.PerformLayout();
        list.Items.Clear();
        foreach (var name in new[] {"Playing","Finished","Stalled","Dropped","Wishlist","Blacklist"}) list.Items.Add(name);
        if(list.GetItemRectangle(5).Bottom > list.ClientSize.Height) throw new Exception("All six standard labels must fit at the minimum window size");
        form.Size = previousSize; form.PerformLayout();
        list.Items.Clear(); list.Items.Add(new VndbAccount.Label(1,"Playing",false),true);
        list.Items.Add(new VndbAccount.Label(2,"Finished",false)); list.Items.Add(new VndbAccount.Label(10,"Mine",true),true);
        picker.Value=55; Field("loading", false); Application.DoEvents();
        var connection = (Label)typeof(VndbAccountPanel).GetField("connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        connection.Text = "Connected as Test";
        if(handler.Saves!=0) throw new Exception("Loading VNDB values must not auto-save");
        list.SetItemChecked(1,true); Application.DoEvents();
        if(handler.Saves!=1 || list.GetItemChecked(0) || !list.GetItemChecked(2) ||
            handler.Saved.GetProperty("labels_set")[0].GetInt32()!=2 || handler.Saved.GetProperty("labels_unset")[0].GetInt32()!=1)
            throw new Exception("Label auto-save must combine progress changes and retain custom labels");
        var message = (Label)typeof(VndbAccountPanel).GetField("message", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        var profileToggle = (CheckBox)typeof(VndbAccountPanel).GetField("showProfile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        if(connection.Text != "Connected as Test" || message.Text != "Saved to VNDB." || connection.Parent!.Top + connection.Top >= message.Top ||
            connection.Parent != profileToggle.Parent || profileToggle.Left <= connection.Left)
            throw new Exception("Connection and save status must remain on separate lines");
        picker.Value=80;
        panel.SelectGame(new GameLink("test.exe", new Novel("v2","Test",null,"en",[],null,[])));
        Application.DoEvents();
        if(handler.Saves!=2 || handler.SavedPath!="/kana/ulist/v1" || handler.Saved.GetProperty("vote").GetInt32()!=80)
            throw new Exception("Switching games must save the pending vote to the original game");
    }
    private sealed class CoverUploadHandler : HttpMessageHandler
    {
        internal string Url = "https://covers.example/covers/" + new string('a', 64) + ".png";
        internal System.Net.HttpStatusCode Status = System.Net.HttpStatusCode.OK;
        internal byte[]? Bytes;
        internal string? GameId;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || request.Content?.Headers.ContentType?.MediaType != "image/png")
                throw new Exception("Upload must contain only PNG pixels");
            Bytes = await request.Content.ReadAsByteArrayAsync();
            GameId = request.Headers.TryGetValues("X-VNDB-ID", out var values) ? values.Single() : null;
            return new(Status) { Content = new StringContent(JsonSerializer.Serialize(new { url = Url })) };
        }
    }
    private static async Task TestCoverUpload(byte[] bytes)
    {
        if (CoverImages.Endpoint("http://covers.example/upload") != null ||
            CoverImages.Endpoint("https://user:password@covers.example/upload") != null ||
            CoverImages.Endpoint("https://covers.example/upload?key=secret") != null)
            throw new Exception("Upload endpoint validation");
        using var handler = new CoverUploadHandler(); using var http = new HttpClient(handler);
        var endpoint = new Uri("https://covers.example/upload");
        var result = await CoverImages.Upload(http, endpoint, bytes, CancellationToken.None, "v17");
        if (handler.GameId != "v17") throw new Exception("Uploaded covers must identify their game for reuse");
        if (CoverImages.UploadEndpoint is Uri configured)
        {
            var valid = new Uri(configured, "/covers/" + new string('a', 64) + ".png").AbsoluteUri;
            if (CoverImages.HostedCover(valid) == null || CoverImages.HostedCover(valid + "?key=secret") != null ||
                CoverImages.HostedCover("https://other.example/covers/" + new string('a', 64) + ".png") != null)
                throw new Exception("Hosted cover signals must stay on our service");
        }
        if (result.Url != handler.Url || !bytes.SequenceEqual(handler.Bytes!)) throw new Exception("Wrong cover upload");
        handler.Url = "https://other.example/covers/" + new string('a', 64) + ".png";
        try { await CoverImages.Upload(http, endpoint, bytes, CancellationToken.None); throw new Exception("Accepted another host"); }
        catch (InvalidDataException) { }
        handler.Status = (System.Net.HttpStatusCode)429;
        try { await CoverImages.Upload(http, endpoint, bytes, CancellationToken.None); throw new Exception("Accepted failed upload"); }
        catch (HttpRequestException) { }
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
            await server.WriteAsync(header, 0, 3, ct); await server.WriteAsync(header, 3, header.Length - 3, ct);
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
