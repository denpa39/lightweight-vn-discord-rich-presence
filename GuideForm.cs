namespace VnPresence;

internal sealed class GuideForm : Form
{
    private static readonly (string Title, string Body)[] Pages =
    [
        ("Discord", "Open Discord desktop.\n\nEnable Settings → Activity Privacy → Share your detected activities.\n\nVN Presence shows the game's native title, brand, cover, and optional details. No token or Developer Portal setup is needed."),
        ("Add a game", "Start your game, then open Add game.\n\n1. Click its window in the upper list. Refresh updates that list; Browse... lets you choose its executable.\n2. Search the English or native title, or paste the game's VNDB URL.\n3. Click a result in the lower list, check the cover, and press Link.\n\nLink each game once. The app detects it automatically afterward."),
        ("Saved games", "Games lists your linked titles. Select a game before changing its Details or Cover.\n\nRemove deletes its saved link. It does not delete the game.\n\nWhen multiple linked games are running, the foreground game takes priority. Switching to another app keeps the previous game active."),
        ("Automatic details", "Select a game → Details... → Automatic.\n\nThe app reads text after the game's name in its window title. Current window title is selectable, so you can copy text from it. Result previews the output as you edit.\n\nRemove text: separate unwanted phrases with ;, for example Ver1.0.0; R18.\n\nWindow title prefix: enter the fixed beginning of the window title if it differs from VNDB. Leave blank to use VNDB titles.\n\nThe game must be running to preview automatic details."),
        ("Custom or hidden details", "Details... → Custom displays the text you enter instead of window text.\n\nHidden removes the detail line.\n\nSave keeps these choices separately for each game. Changing details does not reset the elapsed timer."),
        ("Covers", "Select a game → Cover... to choose a VNDB release cover.\n\nThe white square estimates Discord's crop; shaded areas may be cut off. Default restores the main VNDB cover.\n\nCustom image... accepts a direct public HTTP or HTTPS image URL. It edits one custom image per game. Clear the URL to remove it.\n\nSave in the cover picker applies your choice. Local files cannot be used as Discord covers."),
        ("Profile button", "Profile... adds an optional VNDB profile button to your activity.\n\nEnter your profile URL or u followed by your numeric user ID. Only VNDB user pages are accepted. Leave blank and Save to remove the button.\n\nDiscord shows activity buttons to other users, not in your own view. Your profile and saved games stay in your Windows user's local app-data folder."),
        ("Tray and startup", "Pause stops sharing; Resume starts it again. Activity clears when the game closes.\n\nMinimize keeps the app running in the system tray. Double-click its tray icon to reopen it. Closing the window or choosing Exit in the tray menu stops the app.\n\nStart with Windows launches quietly in the tray when you sign in. Keep the executable in the same folder; uncheck the option to disable it.\n\nReopen this guide anytime using Guide in the Games tab.")
    ];

    public GuideForm()
    {
        Text = "VN Presence guide"; Font = new Font("Tahoma", 9);
        ClientSize = new Size(660, 410); MinimumSize = new Size(650, 440);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 2, RowCount = 2 };
        root.ColumnStyles.Add(new(SizeType.Absolute, 175)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 36));
        var sections = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var page in Pages) sections.Items.Add(page.Title);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 0, 0, 0) };
        content.RowStyles.Add(new(SizeType.Absolute, 32)); content.RowStyles.Add(new(SizeType.Percent, 100));
        var heading = new Label { Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var body = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control, ScrollBars = ScrollBars.Vertical, TabStop = false };
        content.Controls.Add(heading); content.Controls.Add(body);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var next = new Button { Text = "Next", Width = 75 };
        var back = new Button { Text = "Back", Width = 75 };
        var skip = new Button { Text = "Skip", Width = 75, DialogResult = DialogResult.Cancel };
        sections.SelectedIndexChanged += (_, _) =>
        {
            var index = sections.SelectedIndex;
            heading.Text = $"{index + 1} / {Pages.Length} — {Pages[index].Title}";
            body.Text = Pages[index].Body.Replace("\n", Environment.NewLine);
            back.Enabled = index > 0; next.Text = index == Pages.Length - 1 ? "Done" : "Next";
        };
        next.Click += (_, _) => { if (sections.SelectedIndex == Pages.Length - 1) Close(); else sections.SelectedIndex++; };
        back.Click += (_, _) => sections.SelectedIndex--;
        buttons.Controls.AddRange([next, back, skip]);
        root.Controls.Add(sections, 0, 0); root.Controls.Add(content, 1, 0); root.Controls.Add(buttons, 0, 1); root.SetColumnSpan(buttons, 2);
        Controls.Add(root); AcceptButton = next; CancelButton = skip; sections.SelectedIndex = 0;
    }
}
