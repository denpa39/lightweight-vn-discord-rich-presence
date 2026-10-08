using System.Text.Json;

namespace VnPresence;

internal sealed class ActivityPreview : UserControl
{
    private readonly PictureBox picture = new() { Size = new Size(100, 100), Location = new Point(12, 36) };
    private readonly Label title = new() { Font = new Font(Program.UiFontName, 9, FontStyle.Bold) };
    private readonly LinkLabel brand = new() { LinkColor = Color.Gainsboro, ActiveLinkColor = Color.White, VisitedLinkColor = Color.Gainsboro, LinkBehavior = LinkBehavior.HoverUnderline };
    private readonly Label details = new();
    private readonly Label elapsed = new() { ForeColor = Color.FromArgb(59, 165, 93) };
    private readonly Button profile = new() { Text = "VNDB profile", FlatStyle = FlatStyle.Flat, Height = 28, BackColor = Color.FromArgb(47, 49, 54) };
    private readonly Button viewGame = new() { Text = "View on VNDB", FlatStyle = FlatStyle.Flat, Height = 28, BackColor = Color.FromArgb(47, 49, 54) };
    internal JsonElement Activity { get; private set; }
    private long started;
    private string? gameUrl, brandUrl, profileUrl;

    public ActivityPreview()
    {
        Height = 216; Dock = DockStyle.Top; BackColor = Color.FromArgb(30, 31, 34); ForeColor = Color.Gainsboro;
        Font = new Font(Program.UiFontName, 9);
        Controls.Add(new Label { Text = "Playing", Location = new Point(12, 10), AutoSize = true });
        Controls.Add(picture);
        using (var corners = new System.Drawing.Drawing2D.GraphicsPath())
        {
            corners.AddArc(0, 0, 16, 16, 180, 90); corners.AddArc(84, 0, 16, 16, 270, 90);
            corners.AddArc(84, 84, 16, 16, 0, 90); corners.AddArc(0, 84, 16, 16, 90, 90);
            corners.CloseFigure(); picture.Region = new Region(corners);
        }
        foreach (var label in new Label[] { title, brand, details, elapsed })
        { label.AutoEllipsis = true; label.Height = 21; Controls.Add(label); }
        Controls.Add(viewGame); Controls.Add(profile);
        Resize += (_, _) =>
        {
            LayoutText();
            viewGame.SetBounds(12, 144, Math.Max(1, Width - 24), 28);
            profile.SetBounds(12, 178, Math.Max(1, Width - 24), 28);
        };
        picture.Click += (_, _) => Open(gameUrl);
        brand.LinkClicked += (_, _) => Open(brandUrl);
        profile.Click += (_, _) => Open(profileUrl);
        viewGame.Click += (_, _) => Open(gameUrl);
        ShowActivity(null, 0, null, null);
    }
    private static void Open(string? url)
    {
        if (url == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(e.Message, "VN Presence"); }
    }
    internal void ShowActivity(GameLink? link, long start, string? progress, string? userProfile, string? username = null)
    {
        Activity = link == null ? default : JsonSerializer.SerializeToElement(DiscordRpc.Activity(link, start, progress, userProfile, username));
        profile.Text = DiscordRpc.ProfileLabel(username);
        string Field(string name) => Activity.ValueKind == JsonValueKind.Object && Activity.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        title.Text = link == null ? "Select a game" : Field("name");
        brand.Text = Field("details"); details.Text = Field("state");
        gameUrl = link?.Novel.GameUrl; brandUrl = link?.Novel.BrandUrl;
        picture.Cursor = gameUrl == null ? Cursors.Default : Cursors.Hand;
        brand.Links.Clear(); if (brandUrl != null) brand.Links.Add(0, brand.Text.Length);
        profileUrl = Settings.NormalizeProfileUrl(userProfile);
        var hasProfile = link != null && profileUrl != null;
        profile.Visible = hasProfile; viewGame.Visible = link != null;
        started = start; UpdateTime();
        LayoutText();
    }
    private void LayoutText()
    {
        var y = 36;
        foreach (var label in new Label[] { title, brand, details, elapsed })
        {
            label.Visible = label.Text.Length > 0;
            label.SetBounds(124, y, Math.Max(1, Width - 136), 21);
            if (label.Text.Length > 0) y += 22;
        }
    }
    internal void UpdateTime()
    {
        var seconds = started > 0 ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - started) : 0;
        elapsed.Text = Activity.ValueKind != JsonValueKind.Object || started == 0 ? "" : "◷  " + FormatTime(seconds);
    }
    internal static string FormatTime(long seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60}:{seconds % 60:00}";
    internal void SetCover(Image? source)
    {
        var previous = picture.Image;
        picture.Image = null;
        if (source != null)
        {
            var bitmap = new Bitmap(100, 100);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, 100, 100), MainForm.CenterSquare(new RectangleF(0, 0, source.Width, source.Height)), GraphicsUnit.Pixel);
            picture.Image = bitmap;
        }
        previous?.Dispose();
    }
    protected override void Dispose(bool disposing) { if (disposing) { picture.Image?.Dispose(); title.Font.Dispose(); } base.Dispose(disposing); }
}
