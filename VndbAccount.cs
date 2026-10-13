using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VnPresence;

internal static class AccountToken
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    // Windows protects this for the current user; exports exclude the token.
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var ok = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    internal static string Protect(string token) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(token), true));
    internal static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
}

internal sealed class VndbAccount
{
    private readonly HttpClient http;
    private readonly string token;
    internal string UserId { get; private set; } = "";
    internal string Username { get; private set; } = "";
    internal record Label(int Id, string Name, bool Private)
    { public override string ToString() => Name + (Private ? " (private)" : ""); }
    internal VndbAccount(string token, HttpClient? http = null) { this.token = token; this.http = http ?? VndbClient.Http; }
    private async Task<JsonElement> Request(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, "https://api.vndb.org/kana/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException((int)response.StatusCode switch
        {
            401 => "VNDB token is invalid or revoked. Connect again.",
            429 => "VNDB request limit reached. Try again in a minute.",
            _ => $"VNDB request failed ({(int)response.StatusCode}). Try again."
        });
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return json.RootElement.Clone();
    }
    internal async Task Connect(CancellationToken cancellation)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(token.Replace("-", ""), "^[ybndrfg8ejkmcpqxot1uwisza345h769]{32}$"))
            throw new InvalidOperationException("Paste a valid VNDB API token.");
        var data = await Request(HttpMethod.Get, "authinfo", null, cancellation);
        var permissions = data.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        if (!permissions.Contains("listread") || !permissions.Contains("listwrite"))
            throw new InvalidOperationException("Enable both token permissions on VNDB: Access private items on my list and Add/remove/edit items on my list.");
        UserId = data.GetProperty("id").GetString()!; Username = data.GetProperty("username").GetString()!;
    }
    internal async Task<List<Label>> Labels(CancellationToken cancellation)
    {
        var data = await Request(HttpMethod.Get, "ulist_labels", null, cancellation);
        return data.GetProperty("labels").EnumerateArray().Where(l => l.GetProperty("id").GetInt32() is not (0 or 7))
            .Select(l => new Label(l.GetProperty("id").GetInt32(), l.GetProperty("label").GetString()!, l.GetProperty("private").GetBoolean())).ToList();
    }
    internal async Task<(int? Vote, int[] Labels, string Notes)> Read(string vnId, CancellationToken cancellation)
    {
        var data = await Request(HttpMethod.Post, "ulist", new { user = UserId, filters = new[] { "id", "=", vnId }, fields = "vote,labels.id,notes", results = 1 }, cancellation);
        var entries = data.GetProperty("results");
        if (entries.GetArrayLength() == 0) return (null, [], "");
        var entry = entries[0];
        return (entry.GetProperty("vote").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("vote").GetInt32(),
            entry.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("id").GetInt32()).ToArray(), entry.GetProperty("notes").GetString() ?? "");
    }
    internal static object Changes(int? vote, int[] original, int[] chosen)
    {
        if (vote != null && (vote < 10 || vote > 100)) throw new ArgumentOutOfRangeException(nameof(vote));
        if (chosen.Count(id => id is >= 1 and <= 4) > 1) throw new InvalidOperationException("Choose one of Playing, Finished, Stalled or Dropped.");
        return new { vote, labels_set = chosen.Except(original).Where(id => id is not (0 or 7)).ToArray(),
            labels_unset = original.Except(chosen).Where(id => id is not (0 or 7)).ToArray() };
    }
    internal Task Save(string vnId, int? vote, int[] original, int[] chosen, CancellationToken cancellation) =>
        Request(HttpMethod.Patch, "ulist/" + vnId, Changes(vote, original, chosen), cancellation);
    internal Task SaveNotes(string vnId, string notes, CancellationToken cancellation) =>
        Request(HttpMethod.Patch, "ulist/" + vnId, new { notes = notes.Length == 0 ? null : notes }, cancellation);
}

internal sealed class VndbNotesForm : Form
{
    private readonly VndbAccount account;
    private readonly string vnId;
    private readonly CancellationTokenSource shutdown = new();
    private readonly System.Windows.Forms.Timer delay = new() { Interval = 1000 };
    private readonly TextBox notes;
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button discard = new() { Text = "Close without saving", AutoSize = true, Visible = false };
    private Task<bool>? saving;
    private bool allowClose;
    internal string SavedNotes { get; private set; }
    internal VndbNotesForm(VndbAccount account, string vnId, string initial)
    {
        this.account = account; this.vnId = vnId; SavedNotes = initial.Replace("\r\n", "\n");
        Text = "VNDB notes"; Icon = Program.AppIcon; Font = new Font(Program.UiFontName, 9);
        ClientSize = new Size(640, 360); MinimumSize = new Size(460, 280); StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        notes = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Text = initial };
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var close = new Button { Text = "Save and close", Width = 125, Height = 28 };
        buttons.Controls.AddRange([close, discard]); footer.Controls.Add(status); footer.Controls.Add(buttons);
        root.Controls.Add(new Label { Text = "My notes · saves automatically to VNDB", Dock = DockStyle.Fill }); root.Controls.Add(notes); root.Controls.Add(footer); Controls.Add(root);
        notes.TextChanged += (_, _) => { delay.Stop(); delay.Start(); status.Text = "Changes pending…"; };
        delay.Tick += async (_, _) => { delay.Stop(); await SavePending(); };
        close.Click += (_, _) => Close();
        CancelButton = close;
        discard.Click += (_, _) => { allowClose = true; Close(); };
        FormClosing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true; delay.Stop(); notes.ReadOnly = true; close.Enabled = false;
            // Close after this FormClosing event returns, including when saving completes synchronously.
            if (await SavePending()) { allowClose = true; BeginInvoke(new Action(Close)); }
            else if (!IsDisposed) { notes.ReadOnly = false; close.Enabled = true; }
        };
    }
    internal Task<bool> SavePending() => saving is { IsCompleted: false } ? saving : saving = SaveChanges();
    private async Task<bool> SaveChanges()
    {
        try
        {
            while (notes.Text.Replace("\r\n", "\n") != SavedNotes)
            {
                var text = notes.Text.Replace("\r\n", "\n"); status.Text = "Saving to VNDB…";
                await account.SaveNotes(vnId, text, shutdown.Token); SavedNotes = text;
                if (IsDisposed) return true;
            }
            status.Text = "Saved to VNDB."; discard.Visible = false; return true;
        }
        catch (Exception e) { if (!IsDisposed) { status.Text = "Not saved to VNDB: " + e.Message; discard.Visible = true; } return false; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { delay.Dispose(); shutdown.Cancel(); shutdown.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class VndbAccountPanel : UserControl
{
    private readonly Settings settings;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Button connect = new() { Text = "Connect VNDB", AutoSize = true };
    private readonly Label message = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Connect to edit your labels and vote." };
    private readonly Label connection = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Not connected to VNDB" };
    private readonly CheckedListBox labels = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly VotePicker vote = new();
    private readonly Button editNotes = new() { Text = "Notes...", AutoSize = true };
    private string notes = "";
    private readonly CheckBox showProfile = new() { Text = "Show VNDB profile button", AutoSize = true };
    internal event Action? ProfileChanged;
    private bool updatingProfile;
    private VndbAccount? account;
    internal string? ProfileUsername => account != null && Settings.NormalizeProfileUrl(account.UserId) == settings.ActivityProfileUrl ? account.Username : null;
    private string? vnId;
    private string? loadedId;
    private int[] original = [];
    private int version;
    private bool busy, loading;
    private bool saveQueued;
    internal VndbAccountPanel(Settings settings, bool previewOnly)
    {
        this.settings = settings; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; buttons.Controls.Add(connect);
        var voteRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        voteRow.Controls.Add(new Label { Text = "My vote", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        voteRow.Controls.Add(vote);
        voteRow.Controls.Add(editNotes);
        var labelGroup = new GroupBox { Text = "My labels", Dock = DockStyle.Fill, Padding = new Padding(6) };
        labels.BorderStyle = BorderStyle.None; labelGroup.Controls.Add(labels);
        var connectionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        connectionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); connectionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        connection.TextAlign = ContentAlignment.MiddleLeft;
        showProfile.Anchor = AnchorStyles.Right;
        connectionRow.Controls.Add(connection); connectionRow.Controls.Add(showProfile);
        root.Controls.Add(buttons); root.Controls.Add(labelGroup); root.Controls.Add(voteRow); root.Controls.Add(connectionRow); root.Controls.Add(message); Controls.Add(root);
        showProfile.CheckedChanged += (_, _) =>
        {
            if (updatingProfile) return;
            var previous = settings.ShowProfileButton;
            try { settings.ShowProfileButton = showProfile.Checked; settings.Save(); ProfileChanged?.Invoke(); }
            catch (Exception e) { settings.ShowProfileButton = previous; RefreshProfileChoice(); message.Text = e.Message; }
        };
        RefreshProfileChoice();
        labels.ItemCheck += (_, e) =>
        {
            if (loading) return;
            if (e.NewValue == CheckState.Checked && ((VndbAccount.Label)labels.Items[e.Index]).Id is >= 1 and <= 4)
            {
                loading = true;
                for (var i = 0; i < labels.Items.Count; i++) if (i != e.Index && ((VndbAccount.Label)labels.Items[i]).Id is >= 1 and <= 4) labels.SetItemChecked(i, false);
                loading = false;
            }
            QueueSave();
        };
        vote.ValueChanged += QueueSave;
        editNotes.Click += async (_, _) =>
        {
            if (saveQueued && !await SaveEntry()) return;
            if (account == null || vnId == null || busy || loadedId != vnId) return;
            using var editor = new VndbNotesForm(account, vnId, notes);
            editor.ShowDialog(FindForm()); notes = editor.SavedNotes;
        };
        connect.Click += async (_, _) =>
        {
            if (busy) return;
            if (saveQueued && !await SaveEntry()) return;
            if (account != null)
            {
                var previous = settings.VndbTokenProtected;
                try { settings.VndbTokenProtected = null; settings.Save(); account = null; loadedId = null; labels.Items.Clear(); vote.Value = null; notes = ""; connect.Text = "Connect VNDB"; connection.Text = "Not connected to VNDB"; message.Text = "Disconnected from VNDB."; }
                catch (Exception e) { settings.VndbTokenProtected = previous; message.Text = e.Message; }
                UpdateEnabled(); ProfileChanged?.Invoke(); return;
            }
            using var dialog = new Form { Text = "Connect VNDB", Icon = Program.AppIcon, ClientSize = new Size(390, 210), FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
            var help = new Label { Text = "Create a token and enable both permissions:\n• Access private items on my list\n• Add/remove/edit items on my list\nThen copy the token and paste it below.", Left = 12, Top = 12, Width = 365, Height = 66 };
            var open = new LinkLabel { Text = "Create token on VNDB", Left = 12, Top = 84, Width = 365 };
            open.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://vndb.org/u/tokens") { UseShellExecute = true });
            var text = new TextBox { Left = 12, Top = 114, Width = 365, UseSystemPasswordChar = true };
            var accept = new Button { Text = "Connect", Left = 220, Top = 157, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 302, Top = 157, DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange([help, open, text, accept, cancel]); dialog.AcceptButton = accept; dialog.CancelButton = cancel;
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) await Connect(text.Text.Trim(), true);
        };
        UpdateEnabled();
        if (!previewOnly && settings.VndbTokenProtected != null) HandleCreated += async (_, _) =>
        {
            try { await Connect(AccountToken.Unprotect(settings.VndbTokenProtected), false); }
            catch { message.Text = "Connect VNDB again to restore your account."; }
        };
    }
    private void UpdateEnabled()
    {
        connect.Enabled = !busy;
        labels.Enabled = vote.Enabled = editNotes.Enabled = account != null && vnId != null && !busy && loadedId == vnId;
        showProfile.Enabled = !busy && Settings.NormalizeProfileUrl(settings.ProfileUrl) != null;
    }
    internal void RefreshProfileChoice()
    {
        updatingProfile = true;
        showProfile.Checked = settings.ShowProfileButton && Settings.NormalizeProfileUrl(settings.ProfileUrl) != null;
        updatingProfile = false; UpdateEnabled();
    }
    private async Task Connect(string token, bool remember)
    {
        busy = true; UpdateEnabled(); message.Text = "Connecting to VNDB…";
        try
        {
            var candidate = new VndbAccount(token); await candidate.Connect(shutdown.Token);
            var available = await candidate.Labels(shutdown.Token);
            if (IsDisposed) return;
            var profile = Settings.NormalizeProfileUrl(candidate.UserId) ?? throw new InvalidDataException("VNDB returned an invalid user ID.");
            var oldProfile = settings.ProfileUrl; var oldToken = settings.VndbTokenProtected;
            try
            {
                settings.ProfileUrl = profile;
                if (remember) settings.VndbTokenProtected = AccountToken.Protect(token);
                if (remember || oldProfile != profile) settings.Save();
            }
            catch { settings.ProfileUrl = oldProfile; settings.VndbTokenProtected = oldToken; throw; }
            account = candidate; labels.Items.Clear(); foreach (var label in available) labels.Items.Add(label);
            RefreshProfileChoice(); ProfileChanged?.Invoke();
            connect.Text = "Disconnect"; connection.Text = "Connected as " + account.Username; message.Text = "";
        }
        catch (Exception e) { if (!IsDisposed) message.Text = e is OperationCanceledException ? "VNDB request cancelled." : e.Message; }
        finally { busy = false; if (!IsDisposed) UpdateEnabled(); }
        if (!IsDisposed && account != null) await LoadEntry();
    }
    internal void SelectGame(GameLink? link)
    {
        if (vnId == link?.Novel.Id) return;
        if (saveQueued) _ = SaveEntry();
        vnId = link?.Novel.Id; loadedId = null; version++;
        if (account != null && !busy) _ = LoadEntry(); else UpdateEnabled();
    }
    private async Task LoadEntry()
    {
        if (account == null || busy) return;
        var id = vnId; var requestVersion = version;
        loadedId = null;
        busy = true; UpdateEnabled(); message.Text = "Loading VNDB list…";
        try
        {
            var entry = id == null ? (Vote: (int?)null, Labels: Array.Empty<int>(), Notes: "") : await account.Read(id, shutdown.Token);
            if (IsDisposed || requestVersion != version) return;
            original = entry.Labels; loading = true;
            for (var i = 0; i < labels.Items.Count; i++) labels.SetItemChecked(i, original.Contains(((VndbAccount.Label)labels.Items[i]).Id));
            vote.Value = entry.Vote; notes = entry.Notes; loading = false;
            loadedId = id;
            connection.Text = "Connected as " + account.Username;
            message.Text = "";
        }
        catch (Exception e) { if (!IsDisposed) message.Text = e is OperationCanceledException ? "VNDB request cancelled." : e.Message; }
        finally { busy = false; if (!IsDisposed) { UpdateEnabled(); if (requestVersion != version) _ = LoadEntry(); } }
    }
    private void QueueSave()
    {
        if (loading || busy || account == null || vnId == null || loadedId != vnId || saveQueued || !IsHandleCreated) return;
        saveQueued = true;
        // ItemCheck fires before its check state changes; save after this UI event completes.
        BeginInvoke(new Action(async () => { if (saveQueued && !IsDisposed) await SaveEntry(); }));
    }
    private async Task<bool> SaveEntry()
    {
        if (account == null || vnId == null || loadedId != vnId || busy) return false;
        saveQueued = false;
        var chosen = labels.CheckedItems.Cast<VndbAccount.Label>().Select(l => l.Id).ToArray();
        var id = vnId; var requestVersion = version;
        busy = true; UpdateEnabled(); message.Text = "Saving to VNDB…";
        try { await account.Save(id, vote.Value, original, chosen, shutdown.Token); if (!IsDisposed && requestVersion == version) { original = chosen; message.Text = "Saved to VNDB."; } return true; }
        catch (Exception e) { if (!IsDisposed) { message.Text = "Not saved to VNDB: " + e.Message; if (requestVersion != version) MessageBox.Show(FindForm(), message.Text, "VNDB save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); } return false; }
        finally { busy = false; if (!IsDisposed) { UpdateEnabled(); if (requestVersion != version) _ = LoadEntry(); } }
    }
    protected override void Dispose(bool disposing) { if (disposing) shutdown.Cancel(); base.Dispose(disposing); }
}
