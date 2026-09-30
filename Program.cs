using System.Diagnostics;

namespace VnPresence;

internal static class Program
{
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
            var tabs = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
            for (var i = 1; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save($"ui-preview-{i}.png");
            }
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
    private readonly NotifyIcon tray;
    private bool busy, paused, closing;
    private int currentPid;
    private string? publishedKey;
    private DateTime lastSent;
    private string? selectedExe;

    public MainForm(bool previewOnly = false, bool startInTray = false)
    {
        Text = "VN Presence";
        ClientSize = new Size(660, 460);
        MinimumSize = new Size(600, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 9);
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        Icon = SystemIcons.Application;
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
        var current = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        current.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        current.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        current.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        current.Controls.Add(now); current.Controls.Add(startup); current.Controls.Add(pauseButton); root.Controls.Add(current);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        root.Controls.Add(tabs); root.Controls.Add(status);
        var gamesTab = new TabPage("Games") { BackColor = SystemColors.Control, Padding = new Padding(6) };
        var addTab = new TabPage("Add game") { BackColor = SystemColors.Control, Padding = new Padding(6) };
        tabs.TabPages.AddRange([gamesTab, addTab]);
        var gamesPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        gamesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gamesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        gamesPanel.Controls.Add(library);
        gamesPanel.Controls.Add(Row(Button("Add...", () => { RefreshRunning(); tabs.SelectedTab = addTab; }), Button("Details...", EditProgress), Button("Cover...", EditCover), Button("Remove", Remove), Button("Profile...", EditProfile)));
        gamesTab.Controls.Add(gamesPanel);
        library.FormattingEnabled = true;
        library.Format += (_, e) => { if (e.ListItem is GameLink link) e.Value = link.Novel.NativeTitle; };
        var addPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
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
        linkRow.Controls.Add(Button("Link", () => { if (Link()) tabs.SelectedTab = gamesTab; }));
        linkRow.Controls.Add(selection); addPanel.Controls.Add(linkRow);
        addTab.Controls.Add(addPanel);
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
        query.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = Search(); } };
        tray = new NotifyIcon { Icon = Icon, Text = "VN Presence", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open VN Presence", null, (_, _) => Restore());
        menu.Items.Add("Exit", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, _) => { closing = true; timer.Stop(); shutdown.Cancel(); discord.Dispose(); tray.Dispose(); cover.Image?.Dispose(); };
        timer.Tick += async (_, _) => await Tick();
        RefreshLibrary(); RefreshRunning();
        if (!previewOnly) Shown += async (_, _) => { if (startInTray) Hide(); timer.Start(); await Tick(); };
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
    private void Error(string message) { if (!closing) MessageBox.Show(this, message, "VN Presence", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
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
        selectedExe = game.Exe; query.Text = game.Caption; selection.Text = Path.GetFileName(selectedExe);
    }
    private void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = "Game executable (*.exe)|*.exe", Title = "Choose the game itself, not its launcher" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        running.ClearSelected();
        selectedExe = dialog.FileName; query.Text = Path.GetFileNameWithoutExtension(selectedExe); selection.Text = Path.GetFileName(selectedExe);
    }
    private async Task Search()
    {
        if (!searchButton.Enabled || string.IsNullOrWhiteSpace(query.Text)) return;
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
            var bytes = await VndbClient.Http.GetByteArrayAsync(novel.Image.Thumbnail ?? novel.Image.Url, shutdown.Token);
            if (closing || version != previewVersion) return;
            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            cover.Image = new Bitmap(source);
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
        settings.Games.RemoveAll(g => string.Equals(g.Exe, selectedExe, StringComparison.OrdinalIgnoreCase));
        settings.Games.Add(new(selectedExe, novel)); settings.Save(); RefreshLibrary(); publishedKey = null;
        status.Text = "Linked " + novel.NativeTitle; selectedExe = null; _ = Tick();
        return true;
    }
    private void Remove()
    {
        if (library.SelectedItem is not GameLink link) return;
        settings.Games.Remove(link); settings.Save(); RefreshLibrary(); _ = Tick();
    }
    private void EditProgress()
    {
        if (library.SelectedItem is not GameLink link) { Error("Select a game first."); return; }
        using var dialog = new Form { Text = "Details", ClientSize = new Size(360, 330), Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false };
        var mode = new ComboBox { Left = 10, Top = 10, Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
        mode.Items.AddRange(["Automatic", "Custom", "Hidden"]);
        var text = new TextBox { Left = 10, Top = 40, Width = 340, MaxLength = 120, Text = link.CustomProgress ?? "" };
        var removeLabel = new Label { Left = 10, Top = 70, Width = 340, Text = "Remove text from automatic details (optional)" };
        var remove = new TextBox { Left = 10, Top = 94, Width = 340, Text = link.RemoveProgressText ?? "" };
        var prefixLabel = new Label { Left = 10, Top = 125, Width = 340, Text = "Window title prefix (blank = VNDB titles)" };
        var prefix = new TextBox { Left = 10, Top = 149, Width = 340, Text = link.WindowTitlePrefix ?? "" };
        var windowLabel = new Label { Left = 10, Top = 180, Width = 340, Text = "Current window title (select text to copy)" };
        var windowTitle = Detection.Scan(true).FirstOrDefault(game => string.Equals(game.Exe, link.Exe, StringComparison.OrdinalIgnoreCase))?.Caption;
        var windowText = new TextBox { Left = 10, Top = 204, Width = 340, ReadOnly = true,
            Text = windowTitle ?? "", PlaceholderText = "Game is not running" };
        var resultLabel = new Label { Left = 10, Top = 235, Width = 340, Text = "Result" };
        var resultText = new TextBox { Left = 10, Top = 259, Width = 340, ReadOnly = true };
        void UpdateResult()
        {
            var draft = link with { CustomProgress = mode.SelectedIndex == 0 ? null : mode.SelectedIndex == 2 ? "" : text.Text,
                RemoveProgressText = remove.Text, WindowTitlePrefix = prefix.Text };
            var result = draft.Progress(windowText.Text);
            resultText.Text = result == null ? "" : DiscordRpc.Short(result);
            resultText.PlaceholderText = mode.SelectedIndex == 0 && windowTitle == null ? "Start the game to preview" : "Hidden / no details";
        }
        mode.SelectedIndexChanged += (_, _) => UpdateResult();
        text.TextChanged += (_, _) => UpdateResult();
        remove.TextChanged += (_, _) => UpdateResult();
        prefix.TextChanged += (_, _) => UpdateResult();
        mode.SelectedIndexChanged += (_, _) => { text.Enabled = mode.SelectedIndex == 1; remove.Enabled = prefix.Enabled = mode.SelectedIndex == 0; };
        mode.SelectedIndex = link.CustomProgress == null ? 0 : string.IsNullOrWhiteSpace(link.CustomProgress) ? 2 : 1;
        var save = new Button { Text = "Save", Left = 194, Top = 296, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 275, Top = 296, Width = 75, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([mode, text, removeLabel, remove, prefixLabel, prefix, windowLabel, windowText, resultLabel, resultText, save, cancel]); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        UpdateResult();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var updated = link with { CustomProgress = mode.SelectedIndex == 0 ? null : mode.SelectedIndex == 2 ? "" : text.Text.Trim(),
            RemoveProgressText = string.IsNullOrWhiteSpace(remove.Text) ? null : remove.Text.Trim(),
            WindowTitlePrefix = string.IsNullOrWhiteSpace(prefix.Text) ? null : prefix.Text.Trim() };
        settings.Games[settings.Games.IndexOf(link)] = updated;
        settings.Save(); RefreshLibrary(); library.SelectedItem = updated; publishedKey = null; _ = Tick();
    }
    private void EditProfile()
    {
        using var dialog = new Form { Text = "VNDB profile", ClientSize = new Size(360, 110), Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false };
        var label = new Label { Left = 10, Top = 10, Width = 340, Text = "Profile URL (leave blank to hide the button)" };
        var text = new TextBox { Left = 10, Top = 40, Width = 340, Text = settings.ProfileUrl ?? "" };
        var save = new Button { Text = "Save", Left = 194, Top = 76, Width = 75 };
        var cancel = new Button { Text = "Cancel", Left = 275, Top = 76, Width = 75, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            var url = Settings.NormalizeProfileUrl(text.Text);
            if (!string.IsNullOrWhiteSpace(text.Text) && url == null)
            { MessageBox.Show(dialog, "Enter your VNDB profile URL or user ID (u followed by numbers).", "VNDB profile"); return; }
            try { settings.ProfileUrl = url; settings.Save(); dialog.DialogResult = DialogResult.OK; }
            catch (Exception e) { MessageBox.Show(dialog, e.Message, "VNDB profile"); }
        };
        dialog.Controls.AddRange([label, text, save, cancel]); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK) { publishedKey = null; _ = Tick(); }
    }
    private void EditCover()
    {
        if (library.SelectedItem is not GameLink link) { Error("Select a game first."); return; }
        using var dialog = new Form { Text = "Cover", ClientSize = new Size(610, 320), Font = Font,
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
        var message = new Label { Left = 110, Top = 288, Width = 325, Text = "Loading release covers…" };
        var customImage = link.CustomCover ?? (link.Cover is { Thumbnail: null } ? link.Cover : null);
        CoverChoice? customChoice = customImage == null ? null : new("Custom image", customImage);
        var custom = new Button { Text = "Custom image...", Left = 10, Top = 285, Width = 100 };
        custom.Click += (_, _) =>
        {
            using var input = new Form { Text = "Custom image", ClientSize = new Size(440, 110), Font = Font,
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
                var index = customChoice == null ? -1 : choices.Items.IndexOf(customChoice);
                customImage = new(value, null);
                customChoice = new("Custom image", customImage);
                choices.ClearSelected();
                if (index < 0) index = choices.Items.Add(customChoice);
                else choices.Items[index] = customChoice;
                choices.SelectedIndex = index;
                input.DialogResult = DialogResult.OK;
            };
            input.Controls.AddRange([label, url, add, dismiss]); input.AcceptButton = add; input.CancelButton = dismiss;
            input.ShowDialog(dialog);
        };
        var save = new Button { Text = "Save", Left = 444, Top = 285, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 525, Top = 285, Width = 75, DialogResult = DialogResult.Cancel };
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
                var bytes = await VndbClient.Http.GetByteArrayAsync(image.Thumbnail ?? image.Url, cancelLoad.Token);
                if (cancelLoad.IsCancellationRequested || currentVersion != version) return;
                using var stream = new MemoryStream(bytes); using var source = Image.FromStream(stream);
                preview.Image = new Bitmap(source);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ArgumentException)
            { if (!cancelLoad.IsCancellationRequested) message.Text = "Preview unavailable"; }
        };
        choices.SelectedIndex = link.Cover == null ? 0 : 1;
        dialog.Shown += async (_, _) =>
        {
            try
            {
                var images = await vndb.Covers(link.Novel.Id, cancelLoad.Token);
                if (cancelLoad.IsCancellationRequested) return;
                foreach (var image in images) choices.Items.Add(image);
                message.Text = images.Count == 0 ? "No release covers found" : "Square = estimated Discord crop";
            }
            catch (Exception e) { if (!cancelLoad.IsCancellationRequested) message.Text = "VNDB: " + e.Message; }
        };
        dialog.FormClosing += (_, _) => cancelLoad.Cancel();
        dialog.Controls.AddRange([choices, preview, message, custom, save, cancel]); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        var result = dialog.ShowDialog(this);
        preview.Image?.Dispose(); preview.Image = null;
        if (result != DialogResult.OK || choices.SelectedItem is not CoverChoice selected) return;
        var updated = link with { Cover = selected.Image, CustomCover = customImage };
        settings.Games[settings.Games.IndexOf(link)] = updated; settings.Save(); RefreshLibrary(); library.SelectedItem = updated;
        publishedKey = null; _ = Tick();
    }
    private void RefreshLibrary() { library.Items.Clear(); foreach (var game in settings.Games) library.Items.Add(game); }
    internal static RectangleF CenterSquare(RectangleF image)
    {
        var side = Math.Min(image.Width, image.Height);
        return new(image.X + (image.Width - side) / 2, image.Y + (image.Height - side) / 2, side, side);
    }
    private async Task Tick()
    {
        if (busy || closing) return;
        busy = true;
        try
        {
            var foreground = Detection.ForegroundPid();
            var games = settings.Games.ToList();
            var processes = await Task.Run(() => Detection.Scan(false));
            if (closing) return;
            var match = paused ? null : Detection.Choose(processes, games, foreground, currentPid);
            currentPid = match?.Process.Pid ?? 0;
            now.Text = paused ? "Paused" : match?.Link.Novel.NativeTitle ?? "Idle";
            if (string.IsNullOrEmpty(settings.ClientId)) settings.ClientId = Settings.DefaultClientId;
            var progress = match == null ? null : match.Value.Link.Progress(match.Value.Process.Caption);
            var key = match == null ? "idle" : $"{match.Value.Process.Pid}:{match.Value.Process.Started}:{match.Value.Link.Novel.Id}:{progress}";
            if (key == publishedKey && DateTime.UtcNow - lastSent < TimeSpan.FromSeconds(20)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await discord.Connect(settings.ClientId, timeout.Token);
            await discord.Update(match == null ? null : DiscordRpc.Activity(match.Value.Link, match.Value.Process.Started, progress, settings.ProfileUrl), timeout.Token);
            if (closing) return;
            publishedKey = key; lastSent = DateTime.UtcNow;
            status.Text = match == null ? "Connected" : "Connected — sharing";
            WriteStatus(true, match?.Link.Novel.ActivityLine, status.Text);
        }
        catch (Exception e)
        {
            discord.Dispose(); publishedKey = null;
            if (!closing) status.Text = e is OperationCanceledException ? "Discord timed out. Retrying…" : e.Message;
            if (!closing) WriteStatus(false, null, status.Text);
        }
        finally { busy = false; }
    }
    private static void WriteStatus(bool connected, string? title, string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.DirectoryPath);
            File.WriteAllText(Path.Combine(Settings.DirectoryPath, "status.json"),
                System.Text.Json.JsonSerializer.Serialize(new { updated = DateTimeOffset.UtcNow, connected, title, message }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
