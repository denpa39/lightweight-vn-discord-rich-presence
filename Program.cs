using System.Text.Json;

namespace VnPresence;

internal static class Program
{
    internal static readonly int ProcessId = Environment.ProcessId;
    internal static readonly Icon AppIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    internal static readonly string UiFontName = ChooseFont();
    private static string ChooseFont()
    {
        using var font = new Font("Meiryo UI", 9);
        return font.Name == "Meiryo UI" ? "Meiryo UI" : "Yu Gothic UI";
    }
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { SelfTests.Run(); return; }
        using var mutex = new Mutex(true, args.Contains("--ui-test") ? "VnPresence.Desktop.Preview" : "VnPresence.Desktop.Singleton", out var first);
        if (!first) { MessageBox.Show("VN Presence is already running. Look for VN Presence in the system tray."); return; }
        if (!args.Contains("--ui-test"))
        {
            Log("Started");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unhandled: " + e.ExceptionObject);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        }
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new Font(UiFontName, 9));
        if (args.Contains("--ui-test"))
        {
            using var form = new MainForm(previewOnly: true);
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save("ui-preview.png");
            var root = form.Controls.OfType<TableLayoutPanel>().Single();
            if (root.Controls.OfType<TabControl>().Any()) throw new InvalidOperationException("The main window must not have tabs.");
            var content = root.Controls.OfType<TableLayoutPanel>().Single(p => p.Controls.OfType<TableLayoutPanel>().Any());
            var preview = content.Controls.OfType<TableLayoutPanel>().SelectMany(p => p.Controls.OfType<ActivityPreview>()).Single();
            var sampleLink = new GameLink("preview.exe", new Novel("v1", "Preview game", null, "en", [], null, [new Developer("Brand", "p1")]), "Chapter 1");
            preview.ShowActivity(sampleLink, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120, sampleLink.Progress(""), "u1", "den-pa");
            if (preview.Activity.GetProperty("state").GetString() != "Chapter 1" ||
                preview.Activity.GetProperty("buttons")[1].GetProperty("label").GetString() != "Visit den-pa’s VNDB profile")
                throw new InvalidOperationException("Preview must use Discord activity data, including details and the viewer's profile button.");
            using var sampleImage = new Bitmap(100, 200);
            using (var graphics = Graphics.FromImage(sampleImage)) { graphics.Clear(Color.SteelBlue); graphics.FillRectangle(Brushes.Gold, 0, 50, 100, 100); }
            preview.SetCover(sampleImage);
            var previewPicture = preview.Controls.OfType<PictureBox>().Single();
            if (((Bitmap)previewPicture.Image!).GetPixel(50, 0).ToArgb() != Color.Gold.ToArgb())
                throw new InvalidOperationException("Preview must show the center square crop.");
            using (var previewBitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(previewBitmap, new Rectangle(Point.Empty, previewBitmap.Size)); previewBitmap.Save("ui-preview-activity.png"); }
            var previewHeight = preview.Height;
            preview.ShowActivity(sampleLink, 0, null, "");
            if (preview.Height != previewHeight) throw new InvalidOperationException("Hiding the profile button must not move the account controls.");
            if (preview.Activity.GetProperty("buttons").GetArrayLength() != 1 ||
                preview.Activity.GetProperty("buttons")[0].GetProperty("url").GetString() != sampleLink.Novel.GameUrl)
                throw new InvalidOperationException("A blank profile must leave only the game page button.");
            var votePicker = content.Controls.OfType<TableLayoutPanel>().SelectMany(p => p.Controls.OfType<VndbAccountPanel>())
                .Single().Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<FlowLayoutPanel>()
                .SelectMany(p => p.Controls.OfType<VotePicker>()).Single();
            votePicker.TestDropdown();
            SelfTests.TestAutoSave(content.Controls.OfType<TableLayoutPanel>().SelectMany(p => p.Controls.OfType<VndbAccountPanel>()).Single());
            var addButton = content.Controls.OfType<TableLayoutPanel>().Single(p => p.Controls.OfType<ListBox>().Any())
                .Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == "Add...");
            for (var i = 0; i < 2; i++)
            {
                form.BeginInvoke(new Action(() =>
                {
                    var dialog = form.OwnedForms.Single(f => f.Text == "Add game");
                    if (!dialog.Modal || !dialog.Visible) throw new InvalidOperationException("Add game must open as a modal window.");
                    using var addBitmap = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(addBitmap, new Rectangle(Point.Empty, addBitmap.Size));
                    addBitmap.Save("ui-preview-add-game.png");
                    dialog.Close();
                }));
                addButton.PerformClick();
                if (!form.Visible || form.IsDisposed) throw new InvalidOperationException("Closing Add game must keep the main window open.");
            }
            using var guide = new GuideForm();
            guide.ShowInTaskbar = false; guide.Opacity = 0; guide.Show();
            var guideSections = guide.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ListBox>().Single();
            using var guideBitmap = new Bitmap(guide.Width, guide.Height);
            for (var i = 0; i < guideSections.Items.Count; i++)
            {
                guideSections.SelectedIndex = i; Application.DoEvents();
                guide.DrawToBitmap(guideBitmap, new Rectangle(Point.Empty, guideBitmap.Size));
                guideBitmap.Save($"ui-preview-guide-{i}.png");
            }
            var coverArgument = Array.IndexOf(args, "--ui-test-cover");
            using var sampleCover = coverArgument >= 0 && coverArgument + 1 < args.Length ?
                new Bitmap(args[coverArgument + 1]) : new Bitmap(200, 400);
            if (coverArgument < 0)
                using (var graphics = Graphics.FromImage(sampleCover))
                { graphics.Clear(Color.SteelBlue); graphics.FillRectangle(Brushes.Coral, 0, 180, 200, 220); }
            using var editor = new CoverEditor(sampleCover);
            editor.ShowInTaskbar = false; editor.Opacity = 0; editor.Show(); Application.DoEvents();
            if (!editor.Controls.OfType<PictureBox>().Single(p => p.TabStop).Focus()) throw new InvalidOperationException("Cover preview must accept keyboard focus.");
            using var editorBitmap = new Bitmap(editor.Width, editor.Height);
            editor.DrawToBitmap(editorBitmap, new Rectangle(Point.Empty, editorBitmap.Size));
            editorBitmap.Save("ui-preview-cover-fit.png");
            editor.Controls.OfType<RadioButton>().Single(r => r.Text == "Crop").Checked = true;
            editor.Controls.OfType<TrackBar>().Single().Value = 20; Application.DoEvents();
            SelfTests.TestCropEditor(editor, sampleCover.Size);
            editor.DrawToBitmap(editorBitmap, new Rectangle(Point.Empty, editorBitmap.Size));
            editorBitmap.Save("ui-preview-cover-crop.png"); editor.Close();
            using var wideCover = new Bitmap(1200, 500);
            using var wideEditor = new CoverEditor(wideCover);
            wideEditor.ShowInTaskbar = false; wideEditor.Opacity = 0; wideEditor.Show(); Application.DoEvents();
            SelfTests.TestCropEditor(wideEditor, wideCover.Size); wideEditor.Close();
            form.Close(); Application.DoEvents();
            if (form.IsDisposed || form.Visible) throw new InvalidOperationException("Closing the main window must hide it without disposing it.");
            return;
        }
        try { Application.Run(new MainForm(startInTray: args.Contains("--background"))); Log("Closed normally"); }
        catch (Exception error)
        {
            Log("Crash: " + error);
            MessageBox.Show(error.Message + "\n\nDetails saved to " + Path.Combine(Settings.DirectoryPath, "app.log"),
                "VN Presence error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.DirectoryPath);
            File.AppendAllText(Path.Combine(Settings.DirectoryPath, "app.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class MainForm : Form
{
    private readonly Settings settings = Settings.Load();
    private readonly VndbClient vndb = new();
    private readonly DiscordRpc discord = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly TextBox query = new() { Dock = DockStyle.Fill, PlaceholderText = "Title or VNDB URL" };
    private readonly ListBox running = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly ListBox results = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly ListBox library = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Ready", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label now = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Idle", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label selection = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "" };
    private readonly PictureBox cover = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    private int previewVersion;
    private readonly Button searchButton;
    private readonly Button pauseButton;
    private readonly Form addDialog;
    private readonly ActivityPreview activityPreview = new();
    private readonly VndbAccountPanel accountPanel;
    private readonly Label previewStatus = new() { AutoSize = true, Text = "Discord preview" };
    private List<RunningGame> previewProcesses = [];
    private string? activityImageUrl;
    private int activityImageVersion;
    private readonly Dictionary<string, DateTime> coverUseAfter = new();
    private readonly NotifyIcon tray;
    private bool busy, paused, closing, exiting;
    private int currentPid;
    private readonly ComboBox sortOrder = new() { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Right, AccessibleName = "Sort games" };
    private string? publishedKey;
    private DateTime lastSent;
    private string? selectedExe;

    public MainForm(bool previewOnly = false, bool startInTray = false)
    {
        Text = "VN Presence";
        ClientSize = new Size(980, 640);
        MinimumSize = new Size(940, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font(Program.UiFontName, 9);
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        Icon = Program.AppIcon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(root);
        pauseButton = Button("Pause", () => { paused = !paused; pauseButton!.Text = paused ? "Resume" : "Pause"; _ = Tick(); });
        var startup = new CheckBox { Text = "Start with Windows", Dock = DockStyle.Fill, Checked = WindowsStartup.Enabled };
        startup.CheckedChanged += (_, _) =>
        {
            try { WindowsStartup.Set(startup.Checked); }
            catch (Exception e) { startup.Checked = WindowsStartup.Enabled; Error(e.Message); }
        };
        var current = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(14, 0, 6, 0) };
        current.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        current.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        current.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        current.Controls.Add(now); current.Controls.Add(startup); current.Controls.Add(pauseButton); root.Controls.Add(current);
        var gamesPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
        content.Controls.Add(gamesPanel);
        var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewPanel.Controls.Add(previewStatus); previewPanel.Controls.Add(activityPreview);
        accountPanel = new VndbAccountPanel(settings, previewOnly); previewPanel.Controls.Add(accountPanel);
        content.Controls.Add(previewPanel); root.Controls.Add(content); root.Controls.Add(status);
        gamesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        gamesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gamesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        sortOrder.Items.AddRange(["Added", "Alphabetical", "Last played"]);
        sortOrder.SelectedItem = sortOrder.Items.Contains(settings.GameSortOrder) ? settings.GameSortOrder : "Added";
        sortOrder.SelectionChangeCommitted += (_, _) =>
        {
            var previous = settings.GameSortOrder;
            try { settings.GameSortOrder = (string)sortOrder.SelectedItem!; settings.Save(); RefreshLibrary(); }
            catch (Exception e) { settings.GameSortOrder = previous; sortOrder.SelectedItem = previous; Error(e.Message); }
        };
        gamesPanel.Controls.Add(sortOrder); gamesPanel.Controls.Add(library);
        var moreMenu = new ContextMenuStrip();
        void MoreItem(string title, Action action) => moreMenu.Items.Add(title, null, (_, _) => { try { action(); } catch (Exception e) { Error(e.Message); } });
        MoreItem("Change executable...", EditExecutable);
        MoreItem("Export settings...", ExportSettings);
        MoreItem("Import settings...", ImportSettings);
        moreMenu.Items.Add(new ToolStripSeparator());
        MoreItem("Guide", ShowGuide);
        var more = Button("More...", () => { });
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height));
        gamesPanel.Controls.Add(Row(Button("Add...", AddGame), Button("Details...", EditProgress), Button("Cover...", EditCover), Button("Remove", Remove), more));
        library.FormattingEnabled = true;
        library.Format += (_, e) => { if (e.ListItem is GameLink link) e.Value = link.Novel.NativeTitle; };
        addDialog = new Form { Text = "Add game", ClientSize = new Size(660, 430), MinimumSize = new Size(600, 400), Font = Font,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, Icon = Icon };
        var addPanel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 5 };
        addPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        addPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        addPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        addPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        addPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        addPanel.Controls.Add(running);
        addPanel.Controls.Add(Row(Button("Refresh", RefreshRunning), Button("Browse...", Browse)));
        searchButton = Button("Search", () => _ = Search());
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        searchRow.Controls.Add(query); searchRow.Controls.Add(searchButton);
        addPanel.Controls.Add(searchRow);
        var resultPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        resultPanel.Controls.Add(results); resultPanel.Controls.Add(cover);
        addPanel.Controls.Add(resultPanel);
        var linkRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        linkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        linkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        linkRow.Controls.Add(Button("Link", () => { if (Link()) addDialog.DialogResult = DialogResult.OK; }));
        linkRow.Controls.Add(selection); addPanel.Controls.Add(linkRow);
        addDialog.Controls.Add(addPanel);
        foreach (var list in new[] { running, results, library })
        {
            list.IntegralHeight = false;
            list.HorizontalScrollbar = false;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Font.Height + 6;
            list.DrawItem += (_, e) =>
            {
                if (e.Index < 0) return;
                e.DrawBackground();
                var item = list.Items[e.Index];
                var text = item switch
                {
                    RunningGame game => Path.GetFileName(game.Exe) + " — " + game.Caption,
                    GameLink link => link.Novel.NativeTitle,
                    Novel novel => novel.NativeTitle + "  (" + novel.Id + ")",
                    _ => item.ToString() ?? ""
                };
                var bounds = Rectangle.Inflate(e.Bounds, -4, 0);
                TextRenderer.DrawText(e.Graphics, text, list.Font, bounds, e.ForeColor,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                e.DrawFocusRectangle();
            };
        }
        running.SelectedIndexChanged += (_, _) => { if (running.SelectedItem is RunningGame) UseWindow(); };
        results.SelectedIndexChanged += async (_, _) => await Preview();
        library.SelectedIndexChanged += (_, _) => { UpdateActivityPreview(); accountPanel.SelectGame(library.SelectedItem as GameLink); };
        accountPanel.ProfileChanged += () => { UpdateActivityPreview(); publishedKey = null; _ = Tick(); };
        query.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = Search(); } };
        tray = new NotifyIcon { Icon = Icon, Text = "VN Presence", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open VN Presence", null, (_, _) => Restore());
        menu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            closing = true; timer.Stop(); shutdown.Cancel(); discord.Dispose(); tray.Dispose(); cover.Image?.Dispose(); addDialog.Dispose(); moreMenu.Dispose();
        };
        timer.Tick += async (_, _) => await Tick();
        RefreshLibrary();
        if (!previewOnly) Shown += async (_, _) =>
        {
            if (startInTray) Hide();
            timer.Start();
            if (!startInTray && !settings.GuideDismissed) ShowGuide();
            await Tick();
        };
    }
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        row.Controls.AddRange(controls); return row;
    }
    private Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(75, 25), FlatStyle = FlatStyle.Standard, UseVisualStyleBackColor = true };
        button.Click += (_, _) => { try { action(); } catch (Exception e) { Error(e.Message); } }; return button;
    }
    private void Error(string message) { if (!closing) MessageBox.Show(addDialog.Visible ? addDialog : this, message, "VN Presence", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    private void AddGame() { RefreshRunning(); addDialog.ShowDialog(this); }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); if (!settings.GuideDismissed) ShowGuide(); }
    private void ShowGuide()
    {
        using var guide = new GuideForm();
        guide.ShowDialog(this);
        try { settings.GuideDismissed = true; settings.Save(); }
        catch (Exception e) { Error(e.Message); }
    }
    private void RefreshRunning()
    {
        var selected = (running.SelectedItem as RunningGame)?.Pid;
        if (selected != null) { selectedExe = null; selection.Text = ""; }
        running.Items.Clear();
        foreach (var game in Detection.Scan(true).OrderBy(g => g.Caption)) running.Items.Add(game);
        for (var i = 0; i < running.Items.Count; i++) if (((RunningGame)running.Items[i]).Pid == selected) running.SelectedIndex = i;
    }
    private void UseWindow()
    {
        if (running.SelectedItem is not RunningGame game) return;
        selectedExe = game.Exe;
        query.Text = settings.Games.FirstOrDefault(link => string.Equals(link.Exe, game.Exe, StringComparison.OrdinalIgnoreCase))?.Novel.NativeTitle
            ?? VndbClient.SearchTitle(game.Caption);
        selection.Text = Path.GetFileName(selectedExe);
    }
    private void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = "Game executable (*.exe)|*.exe", Title = "Choose the game itself, not its launcher" };
        if (dialog.ShowDialog(addDialog) != DialogResult.OK) return;
        running.ClearSelected();
        selectedExe = dialog.FileName; query.Text = Path.GetFileNameWithoutExtension(selectedExe); selection.Text = Path.GetFileName(selectedExe);
    }
    private async Task Search()
    {
        if (!searchButton.Enabled || string.IsNullOrWhiteSpace(query.Text)) return;
        query.Text = VndbClient.SearchTitle(query.Text);
        searchButton.Enabled = false; searchButton.Text = "Searching…";
        try
        {
            var novels = await vndb.Search(query.Text);
            if (closing) return;
            results.Items.Clear();
            foreach (var novel in novels) results.Items.Add(novel);
            if (novels.Count == 0) Error("No matches. Try a shorter title or paste the game's VNDB URL.");
            else results.SelectedIndex = 0;
        }
        catch (Exception e) { Error("VNDB search failed: " + e.Message); }
        finally { if (!closing) { searchButton.Enabled = true; searchButton.Text = "Search"; } }
    }
    private async Task Preview()
    {
        var version = ++previewVersion;
        cover.Image?.Dispose(); cover.Image = null;
        if (results.SelectedItem is not Novel novel) return;
        if (novel.Image == null) return;
        try
        {
            var bytes = await vndb.PreviewImage(novel.Image.Thumbnail ?? novel.Image.Url, shutdown.Token);
            if (closing || version != previewVersion) return;
            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            cover.Image = CoverImages.Preview(source, cover.ClientSize);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ArgumentException)
        {
            // A missing preview should not prevent linking the game.
        }
    }
    private bool Link()
    {
        if (selectedExe == null && running.SelectedItem is RunningGame game) selectedExe = game.Exe;
        if (selectedExe == null || results.SelectedItem is not Novel novel) { Error("Choose a game executable and a VNDB search result first."); return false; }
        if (settings.HasGame(selectedExe, novel.Id)) { Error("That game or executable is already linked."); return false; }
        var added = new GameLink(selectedExe, novel);
        settings.Games.Add(added); settings.Save(); RefreshLibrary(); library.SelectedItem = added; publishedKey = null;
        status.Text = "Linked " + novel.NativeTitle; selectedExe = null; _ = Tick();
        return true;
    }
    private void Remove()
    {
        if (library.SelectedItem is not GameLink link) return;
        if (MessageBox.Show(this, $"Remove {link.Novel.NativeTitle} from your games?", "Remove game",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        settings.Games.Remove(link); settings.Save(); RefreshLibrary(); _ = Tick();
    }
    private void EditExecutable()
    {
        if (library.SelectedItem is not GameLink link) { Error("Select a game first."); return; }
        using var dialog = new OpenFileDialog { Filter = "Game executable (*.exe)|*.exe", Title = "Change game executable", FileName = link.Exe };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (settings.Games.Any(g => g != link && string.Equals(g.Exe, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
        { Error("That executable is already linked to another game."); return; }
        var index = settings.Games.IndexOf(link);
        var updated = link with { Exe = dialog.FileName };
        settings.Games[index] = updated;
        try { settings.Save(); } catch { settings.Games[index] = link; throw; }
        RefreshLibrary(); library.SelectedItem = updated; publishedKey = null; _ = Tick();
    }
    private void ExportSettings()
    {
        using var dialog = new SaveFileDialog { Filter = "VN Presence settings (*.json)|*.json", FileName = "VN Presence settings.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, settings.Export());
        status.Text = "Settings exported";
    }
    private void ImportSettings()
    {
        using var dialog = new OpenFileDialog { Filter = "VN Presence settings (*.json)|*.json", Title = "Import settings" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(dialog.FileName).Length > 10 * 1024 * 1024) { Error("Settings file is too large."); return; }
        var backup = Settings.ReadBackup(File.ReadAllText(dialog.FileName));
        if (MessageBox.Show(this, $"Replace your {settings.Games.Count} saved games and profile with this export ({backup.Games.Count} games)?",
            "Import settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var oldGames = settings.Games; var oldProfile = settings.ProfileUrl; var oldShowProfile = settings.ShowProfileButton; var oldSort = settings.GameSortOrder;
        settings.Games = backup.Games; settings.ProfileUrl = backup.ProfileUrl; settings.ShowProfileButton = backup.ShowProfileButton; settings.GameSortOrder = backup.GameSortOrder;
        try { settings.Save(); } catch { settings.Games = oldGames; settings.ProfileUrl = oldProfile; settings.ShowProfileButton = oldShowProfile; settings.GameSortOrder = oldSort; throw; }
        accountPanel.RefreshProfileChoice();
        RefreshLibrary(); currentPid = 0; publishedKey = null; _ = Tick();
    }
    private void EditProgress()
    {
        if (library.SelectedItem is not GameLink link) { Error("Select a game first."); return; }
        using var dialog = new Form { Text = "Details", Icon = Program.AppIcon, ClientSize = new Size(360, 385), Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false };
        var mode = new ComboBox { Left = 10, Top = 10, Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
        mode.Items.AddRange(["Automatic", "Custom"]);
        var text = new TextBox { Left = 10, Top = 40, Width = 340, MaxLength = 120, Text = link.CustomProgress ?? "" };
        var removeLabel = new Label { Left = 10, Top = 70, Width = 340, Text = "Remove text (separate phrases with ;)" };
        var remove = new TextBox { Left = 10, Top = 94, Width = 340, Text = link.RemoveProgressText ?? "" };
        var prefixLabel = new Label { Left = 10, Top = 125, Width = 340, Text = "Window title prefix (blank = VNDB titles)" };
        var prefix = new TextBox { Left = 10, Top = 149, Width = 340, Text = link.WindowTitlePrefix ?? "" };
        var edgesLabel = new Label { Left = 10, Top = 180, Width = 340, Text = "Trim from edges (characters, e.g. - or -())" };
        var edges = new TextBox { Left = 10, Top = 204, Width = 340, Text = link.TrimEdges ?? "" };
        var windowLabel = new Label { Left = 10, Top = 235, Width = 340, Text = "Current window title (select text to copy)" };
        var windowTitle = Detection.Scan(true).FirstOrDefault(game => string.Equals(game.Exe, link.Exe, StringComparison.OrdinalIgnoreCase))?.Caption;
        var windowText = new TextBox { Left = 10, Top = 259, Width = 340, ReadOnly = true,
            Text = windowTitle ?? "", PlaceholderText = "Game is not running" };
        var resultLabel = new Label { Left = 10, Top = 290, Width = 340, Text = "Result" };
        var resultText = new TextBox { Left = 10, Top = 314, Width = 340, ReadOnly = true };
        void UpdateResult()
        {
            var draft = link with { CustomProgress = mode.SelectedIndex == 0 ? null : text.Text,
                RemoveProgressText = remove.Text, WindowTitlePrefix = prefix.Text, TrimEdges = edges.Text };
            var result = draft.Progress(windowText.Text);
            resultText.Text = result == null ? "" : DiscordRpc.Short(result);
            resultText.PlaceholderText = mode.SelectedIndex == 0 && windowTitle == null ? "Start the game to preview" : "No details";
        }
        mode.SelectedIndexChanged += (_, _) => UpdateResult();
        text.TextChanged += (_, _) => UpdateResult();
        remove.TextChanged += (_, _) => UpdateResult();
        prefix.TextChanged += (_, _) => UpdateResult();
        edges.TextChanged += (_, _) => UpdateResult();
        mode.SelectedIndexChanged += (_, _) => { text.Enabled = mode.SelectedIndex == 1; remove.Enabled = prefix.Enabled = edges.Enabled = mode.SelectedIndex == 0; };
        mode.SelectedIndex = link.CustomProgress == null ? 0 : 1;
        var save = new Button { Text = "Save", Left = 194, Top = 351, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 275, Top = 351, Width = 75, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([mode, text, removeLabel, remove, prefixLabel, prefix, edgesLabel, edges, windowLabel, windowText, resultLabel, resultText, save, cancel]); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        UpdateResult();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var updated = link with { CustomProgress = mode.SelectedIndex == 0 ? null : text.Text.Trim(),
            RemoveProgressText = string.IsNullOrWhiteSpace(remove.Text) ? null : remove.Text.Trim(),
            WindowTitlePrefix = string.IsNullOrWhiteSpace(prefix.Text) ? null : prefix.Text.Trim(),
            TrimEdges = string.IsNullOrWhiteSpace(edges.Text) ? null : edges.Text.Trim() };
        settings.Games[settings.Games.IndexOf(link)] = updated;
        settings.Save(); RefreshLibrary(); library.SelectedItem = updated; publishedKey = null; _ = Tick();
    }
    private void EditCover()
    {
        if (library.SelectedItem is not GameLink link) { Error("Select a game first."); return; }
        using var dialog = new Form { Text = "Cover", Icon = Program.AppIcon, ClientSize = new Size(610, 355), Font = Font,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog };
        using var cancelLoad = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var choices = new ListBox { Left = 10, Top = 10, Width = 370, Height = 265, IntegralHeight = false, HorizontalScrollbar = true };
        var preview = new PictureBox { Left = 390, Top = 10, Width = 210, Height = 265, SizeMode = PictureBoxSizeMode.Zoom };
        preview.Paint += (_, e) =>
        {
            if (preview.Image is not Image image) return;
            var scale = Math.Min((float)preview.ClientSize.Width / image.Width, (float)preview.ClientSize.Height / image.Height);
            var width = image.Width * scale; var height = image.Height * scale;
            var x = (preview.ClientSize.Width - width) / 2; var y = (preview.ClientSize.Height - height) / 2;
            var crop = CenterSquare(new RectangleF(x, y, width, height));
            var side = crop.Width;
            using var shade = new SolidBrush(Color.FromArgb(150, Color.Black));
            e.Graphics.FillRectangle(shade, x, y, width, crop.Top - y);
            e.Graphics.FillRectangle(shade, x, crop.Bottom, width, y + height - crop.Bottom);
            e.Graphics.FillRectangle(shade, x, crop.Top, crop.Left - x, side);
            e.Graphics.FillRectangle(shade, crop.Right, crop.Top, x + width - crop.Right, side);
            using var border = new Pen(Color.White, 2);
            e.Graphics.DrawRectangle(border, crop.X, crop.Y, crop.Width, crop.Height);
        };
        var message = new Label { Left = 10, Top = 322, Width = 590, Text = "Loading release covers…" };
        var customImage = link.CustomCover ?? (link.Cover is { Thumbnail: null } ? link.Cover : null);
        CoverChoice? customChoice = customImage == null ? null : new("Custom image", customImage);
        void SetCustomImage(VnImage image)
        {
            var index = customChoice == null ? -1 : choices.Items.IndexOf(customChoice);
            customImage = image; customChoice = new("Custom image", image);
            choices.ClearSelected();
            if (index < 0) index = choices.Items.Add(customChoice);
            else choices.Items[index] = customChoice;
            choices.SelectedIndex = index;
        }
        var custom = new Button { Text = "Custom image...", Left = 10, Top = 285, Width = 100 };
        custom.Click += (_, _) =>
        {
            using var input = new Form { Text = "Custom image", Icon = Program.AppIcon, ClientSize = new Size(440, 110), Font = Font,
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false };
            var label = new Label { Left = 10, Top = 10, Width = 420, Text = "Direct image URL (leave blank to remove)" };
            var url = new TextBox { Left = 10, Top = 40, Width = 420, MaxLength = 300, Text = customImage?.Url ?? "" };
            var add = new Button { Text = "Save", Left = 274, Top = 76, Width = 75 };
            var dismiss = new Button { Text = "Cancel", Left = 355, Top = 76, Width = 75, DialogResult = DialogResult.Cancel };
            add.Click += (_, _) =>
            {
                var value = url.Text.Trim();
                if (value.Length == 0)
                {
                    var wasSelected = choices.SelectedItem is CoverChoice selectedChoice && selectedChoice == customChoice;
                    if (customChoice != null) choices.Items.Remove(customChoice);
                    customImage = null; customChoice = null;
                    if (wasSelected || choices.SelectedIndex < 0) choices.SelectedIndex = 0;
                    input.DialogResult = DialogResult.OK;
                    return;
                }
                if (!VnImage.ValidCustomUrl(value))
                { MessageBox.Show(input, "Enter a direct HTTP or HTTPS image URL (up to 300 characters).", "Image URL"); return; }
                SetCustomImage(new(value, null));
                input.DialogResult = DialogResult.OK;
            };
            input.Controls.AddRange([label, url, add, dismiss]); input.AcceptButton = add; input.CancelButton = dismiss;
            input.ShowDialog(dialog);
        };
        var save = new Button { Text = "Save", Left = 444, Top = 285, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 525, Top = 285, Width = 75, DialogResult = DialogResult.Cancel };
        var adjust = new Button { Text = "Adjust...", Left = 120, Top = 285, Width = 85 };
        adjust.Click += async (_, _) =>
        {
            if (choices.SelectedItem is not CoverChoice choice || (choice.Image ?? link.Novel.Image) is not VnImage image)
            { message.Text = "Select a cover first."; return; }
            adjust.Enabled = choices.Enabled = custom.Enabled = save.Enabled = false;
            message.Text = "Loading full cover…";
            try
            {
                var bytes = await vndb.PreviewImage(image.Url, cancelLoad.Token);
                if (cancelLoad.IsCancellationRequested) return;
                using var stream = new MemoryStream(bytes); using var source = Image.FromStream(stream);
                if ((long)source.Width * source.Height > 25000000) throw new InvalidDataException("Cover is too large to edit.");
                using var editor = new CoverEditor(source, link.Novel.Id);
                if (editor.ShowDialog(dialog) == DialogResult.OK && editor.UploadedImage is VnImage uploaded)
                    SetCustomImage(uploaded);
                message.Text = "Square = estimated Discord crop";
            }
            catch (Exception error) { if (!cancelLoad.IsCancellationRequested) message.Text = error.Message; }
            finally { if (!cancelLoad.IsCancellationRequested) adjust.Enabled = choices.Enabled = custom.Enabled = save.Enabled = true; }
        };
        choices.Items.Add(new CoverChoice("Default", null));
        if (link.Cover != null && link.Cover != customImage) choices.Items.Add(new CoverChoice("Current cover", link.Cover));
        if (customChoice != null) choices.Items.Add(customChoice);
        var version = 0;
        choices.SelectedIndexChanged += async (_, _) =>
        {
            var currentVersion = ++version;
            preview.Image?.Dispose(); preview.Image = null;
            if (choices.SelectedItem is not CoverChoice choice || (choice.Image ?? link.Novel.Image) is not VnImage image) return;
            try
            {
                var bytes = await vndb.PreviewImage(image.Thumbnail ?? image.Url, cancelLoad.Token);
                if (cancelLoad.IsCancellationRequested || currentVersion != version) return;
                using var stream = new MemoryStream(bytes); using var source = Image.FromStream(stream);
                preview.Image = CoverImages.Preview(source, preview.ClientSize);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ArgumentException)
            { if (!cancelLoad.IsCancellationRequested) message.Text = "Preview unavailable"; }
        };
        choices.SelectedIndex = link.Cover == null ? 0 : 1;
        dialog.Shown += async (_, _) =>
        {
            async Task LoadShared()
            {
                if (CoverImages.UploadEndpoint is not Uri endpoint) return;
                try
                {
                    var url = new Uri(endpoint, "/gallery/" + link.Novel.Id);
                    var json = await VndbClient.Http.GetStringAsync(url, cancelLoad.Token);
                    using var data = JsonDocument.Parse(json);
                    var number = 0;
                    foreach (var item in data.RootElement.EnumerateArray().Take(40))
                        if (CoverImages.HostedCover(item.GetProperty("url").GetString()) is Uri image && !cancelLoad.IsCancellationRequested)
                            choices.Items.Add(new CoverChoice("Community cover " + ++number, new VnImage(image.AbsoluteUri, null)));
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
                { if (!cancelLoad.IsCancellationRequested) message.Text = "Community covers unavailable"; }
            }
            var shared = LoadShared();
            try
            {
                var images = await vndb.Covers(link.Novel.Id, cancelLoad.Token);
                if (cancelLoad.IsCancellationRequested) return;
                foreach (var image in images) choices.Items.Add(image);
                message.Text = images.Count == 0 ? "No release covers found" : "Square = estimated Discord crop";
            }
            catch (Exception e) { if (!cancelLoad.IsCancellationRequested) message.Text = "VNDB: " + e.Message; }
            await shared;
        };
        dialog.FormClosing += (_, _) => cancelLoad.Cancel();
        dialog.Controls.AddRange([choices, preview, message, custom, adjust, save, cancel]); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        var result = dialog.ShowDialog(this);
        preview.Image?.Dispose(); preview.Image = null;
        if (result != DialogResult.OK || choices.SelectedItem is not CoverChoice selected) return;
        var updated = link with { Cover = selected.Image, CustomCover = customImage };
        _ = MarkCoverUsed(updated);
        settings.Games[settings.Games.IndexOf(link)] = updated; settings.Save(); RefreshLibrary(); library.SelectedItem = updated;
        publishedKey = null; _ = Tick();
    }
    private void RefreshLibrary()
    {
        sortOrder.SelectedItem = sortOrder.Items.Contains(settings.GameSortOrder) ? settings.GameSortOrder : "Added";
        var selectedId = (library.SelectedItem as GameLink)?.Novel.Id;
        library.BeginUpdate();
        try
        {
            library.Items.Clear(); foreach (var game in settings.SortedGames()) library.Items.Add(game);
            library.SelectedItem = settings.Games.FirstOrDefault(g => g.Novel.Id == selectedId);
        }
        finally { library.EndUpdate(); }
    }
    internal static RectangleF CenterSquare(RectangleF image)
    {
        var side = Math.Min(image.Width, image.Height);
        return new(image.X + (image.Width - side) / 2, image.Y + (image.Height - side) / 2, side, side);
    }
    private async void UpdateActivityPreview()
    {
        var link = library.SelectedItem as GameLink ?? settings.Games.FirstOrDefault(g => previewProcesses.Any(p => p.Pid == currentPid && string.Equals(p.Exe, g.Exe, StringComparison.OrdinalIgnoreCase)));
        var process = link == null ? null : previewProcesses.FirstOrDefault(p => string.Equals(p.Exe, link.Exe, StringComparison.OrdinalIgnoreCase));
        activityPreview.ShowActivity(link, process?.Started ?? 0, link?.Progress(process?.Caption ?? ""), settings.ActivityProfileUrl, accountPanel.ProfileUsername);
        previewStatus.Text = "Discord preview" + (link == null ? "" : paused ? " · Paused" : process == null ? " · Game not running" : process.Pid == currentPid ? " · Live" : " · Not shared");
        var image = link?.Cover ?? link?.Novel.Image;
        var url = image?.Thumbnail ?? image?.Url;
        if (url == activityImageUrl) return;
        activityImageUrl = url;
        var version = ++activityImageVersion;
        activityPreview.SetCover(null);
        if (url == null) return;
        try
        {
            var bytes = await vndb.PreviewImage(url, shutdown.Token);
            if (closing || version != activityImageVersion) return;
            using var stream = new MemoryStream(bytes); using var source = Image.FromStream(stream);
            activityPreview.SetCover(source);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ArgumentException)
        { if (!closing && version == activityImageVersion) { activityImageUrl = null; previewStatus.Text += " · cover unavailable"; } }
    }
    private async Task Tick()
    {
        if (busy || closing) return;
        busy = true;
        try
        {
            var foreground = Detection.ForegroundPid();
            var games = settings.Games.ToList();
            var processes = paused || games.Count == 0 ? new List<RunningGame>() : await Task.Run(() => Detection.Scan(false, games));
            if (closing) return;
            var match = paused ? null : Detection.Choose(processes, games, foreground, currentPid);
            if (match != null && match.Value.Process.Pid != currentPid)
            {
                var index = settings.Games.FindIndex(g => g.Novel.Id == match.Value.Link.Novel.Id);
                if (index >= 0)
                {
                    var original = settings.Games[index];
                    settings.Games[index] = original with { LastPlayed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    try { settings.Save(); } catch { settings.Games[index] = original; throw; }
                    RefreshLibrary();
                }
            }
            currentPid = match?.Process.Pid ?? 0;
            previewProcesses = processes;
            UpdateActivityPreview();
            now.Text = paused ? "Paused" : match?.Link.Novel.NativeTitle ?? "Idle";
            if (string.IsNullOrEmpty(settings.ClientId)) settings.ClientId = Settings.DefaultClientId;
            var progress = match == null ? null : match.Value.Link.Progress(match.Value.Process.Caption);
            var key = match == null ? "idle" : $"{match.Value.Process.Pid}:{match.Value.Process.Started}:{match.Value.Link.Novel.Id}:{progress}";
            if (key == publishedKey && DateTime.UtcNow - lastSent < TimeSpan.FromSeconds(20)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await discord.Connect(settings.ClientId, timeout.Token);
            await discord.Update(match == null ? null : DiscordRpc.Activity(match.Value.Link, match.Value.Process.Started, progress, settings.ActivityProfileUrl, accountPanel.ProfileUsername), timeout.Token);
            if (closing) return;
            if (match != null) _ = MarkCoverUsed(match.Value.Link);
            publishedKey = key; lastSent = DateTime.UtcNow;
            status.Text = "Connected to Discord" + (paused ? " · Sharing paused" : match == null ? "" : " · Activity shared");
        }
        catch (Exception e)
        {
            discord.Dispose(); publishedKey = null;
            if (!closing) status.Text = e is OperationCanceledException ? "Discord timed out. Retrying…" : e.Message;
        }
        finally { busy = false; }
    }
    private async Task MarkCoverUsed(GameLink link)
    {
        var image = link.Cover ?? link.Novel.Image;
        if (CoverImages.HostedCover(image?.Url) is not Uri url ||
            coverUseAfter.TryGetValue(url.AbsoluteUri, out var next) && DateTime.UtcNow < next) return;
        coverUseAfter[url.AbsoluteUri] = DateTime.UtcNow.AddHours(1);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-VNDB-ID", link.Novel.Id);
            using var response = await VndbClient.Http.SendAsync(request, shutdown.Token);
            if (response.IsSuccessStatusCode) coverUseAfter[url.AbsoluteUri] = DateTime.UtcNow.AddDays(1);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
    }
}
