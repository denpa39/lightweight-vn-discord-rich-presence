namespace VnPresence;

internal sealed class GuideForm : Form
{
    private static readonly (string Title, string Body)[] Pages =
    [
        ("Connect Discord", "1. Open Discord on your PC.\n\n2. Go to Settings → Activity Privacy.\n\n3. Turn on Share your detected activities.\n\nKeep Discord and VN Presence running while you play."),
        ("Add a game", "1. Start the game, then press Add...\n\n2. In the new window, click your game in the top list. If it isn't there, use Refresh or Browse... to its .exe.\n\n3. Check the search title. Version and progress text may be removed automatically. Edit it if needed, or paste a VNDB URL. Press Search.\n\n4. Click the matching result below, then Link.\n\nThe window closes and the game is saved. Games and executables already linked can't be added twice."),
        ("Your games", "Select a saved game before using Details... or Cover...\n\nUse the dropdown above the game list, on the right, to choose Added, Alphabetical or Last played. Your choice is remembered. Last played puts recent games first; tracking starts with this version.\n\nMoved its folder? More... → Change executable... updates the path and keeps your settings.\n\nRemove asks you to confirm the title before unlinking it. No is selected by default.\n\nPlaying two games? The one you're using takes priority. Switching away keeps the last game on Discord."),
        ("Discord preview", "Select a game to see its card on the right.\n\nIt shows the cover crop, native title, brand, details and activity buttons. View on VNDB opens its game page. Your profile button is optional.\n\nStart the game to see automatic details and the elapsed timer. Chapter changes keep the timer running.\n\nLive means that game is being shared. Game not running or Not shared means you're only previewing it.\n\nThe bottom status shows whether the app is connected to Discord."),
        ("Show game details", "Pick a game → Details...\n\nAutomatic uses text from the game's window title:\n\nKanon January 7th, Thursday\n→ January 7th, Thursday\n\nCustom shows whatever you type. Leave it blank to hide the details line.\n\nResult shows what you'll get. Press Save to apply."),
        ("Fix a title mismatch", "Automatic showing nothing? Start the game and open Details...\n\nCopy the game name from Current window title into Window title prefix. Use only the fixed part before the date or chapter.\n\nExample window:\nMy Game Special Edition — Chapter 2\n\nPrefix:\nMy Game Special Edition\n\nLeave the field blank to use VNDB's names."),
        ("Remove unwanted text", "In Details... → Automatic:\n\nRemove text deletes phrases wherever they appear. Separate them with ;\n\nVer1.0.0; R18\n\nTrim from edges removes characters only at the ends. Enter - to get:\n\n- long-time-no-see -\n→ long-time-no-see\n\nUse -() to trim outer dashes and parentheses. Don't put - in Remove text if you want to keep internal hyphens. Check Result, then Save."),
        ("Choose a cover", "Select a game → Cover...\n\nClick a cover in the list to preview it. Default uses the game's main VNDB cover; releases offer other editions.\n\nThe white square shows the part Discord is likely to keep. Dark areas may be cropped.\n\nPress Save to use the selected cover."),
        ("Community covers", "Open Cover... and look for Community cover in the list. These are covers users have uploaded for the selected game.\n\nClick one to preview it, then Save. You reuse the existing image without another upload.\n\nNo community covers yet? Choose a release cover and use Adjust... → Upload & use to share your version.\n\nHosted images stay while storage has room. When it fills, the least recently used covers are removed first, only enough to make room. Images do not expire after a fixed number of days."),
        ("Fit or crop a cover", "Cover... → select a cover → Adjust...\n\nFit whole cover keeps the entire image and fills the extra space with transparency. The checkerboard is only a preview.\n\nCrop keeps the area inside the white square. Drag to move it. Scroll or use Zoom to resize it; arrow keys also move it. Reset recenters the crop and resets zoom.\n\nPress Upload & use, then Save in the Cover window to apply it.\n\nSave image... saves a PNG on your PC instead. It doesn't change your Discord cover.\n\nTo start over, select the original cover before opening Adjust... again."),
        ("Blur a cover", "Cover... → select a cover → Adjust...\n\nTick Blur cover for artwork you want to obscure. The preview blurs so you can check it.\n\nPress Upload & use, then Save in the Cover window. The blur is applied before upload, so Discord receives the blurred image.\n\nSave image... also saves the blurred version. Transparent padding stays transparent.\n\nBlur is optional; the app does not detect NSFW artwork automatically. Uploaded images are public."),
        ("Use your own image", "Cover... → Custom image...\n\nPaste a direct image URL, not a webpage or a file on your PC. The image must be publicly accessible.\n\nSave, check the preview, then Save in the cover picker.\n\nWant to change it? Open Custom image... again. Clear the URL to remove it."),
        ("Connect VNDB", "Press Connect VNDB below the preview.\n\nClick Create token on VNDB and log in on the website. Create a token and tick both permissions:\nAccess private items on my list\nAdd/remove/edit items on my list\n\nPaste the token into the app and press Connect. You don't give the app your password.\n\nYour profile URL is filled in automatically. Uncheck Show VNDB profile button if you don't want it on Discord.\n\nWindows encrypts the saved token for your user. Disconnect removes the token; your profile button preference stays saved."),
        ("Profile button", "Connect VNDB to use your account's profile link automatically. There is no URL to enter.\n\nThe Show VNDB profile button checkbox beside Connected as [username] adds a button named Visit [username]’s VNDB profile to your Discord activity. Uncheck it to hide the button while keeping your account connected.\n\nThe preview shows the same button. Other people can see it on Discord; Discord hides activity buttons from their owner."),
        ("Labels and votes", "Select a saved game to load its VNDB labels and vote.\n\nTick or untick a label to save the change automatically. Playing, Finished, Stalled and Dropped are mutually exclusive; choosing one clears the others.\n\nOpen Vote to choose a rating. For a decimal, type a value from 1.0 to 10.0, such as 5.6, and press Enter. Typing does not filter the list.\n\nChoose Remove vote to clear an existing score. This also saves automatically.\n\nConnected as [username] stays on its own line. Under it, Saving to VNDB changes to Saved to VNDB when the update succeeds. There is no Save button.\n\nIf you see Not saved, the update failed; change the value again to retry. Wait for Saved to VNDB before quitting. Refresh reloads changes made on the website."),
        ("Back up your settings", "More... → Export settings... saves your games, customizations, and profile to a JSON file.\n\nOn another PC, choose More... → Import settings... and pick that file. Import replaces the saved games and profile after you confirm.\n\nIf game folders differ, use Change executable... afterward.\n\nThe export contains your profile and game paths. Keep it private. Your VNDB token is excluded."),
        ("Keep it running", "Minimize or X hides the app in the system tray without a message. It keeps running and sharing your game.\n\nDouble-click the tray icon to reopen it. To quit, right-click the icon → Exit.\n\nPause stops sharing until you press Resume.\n\nStart with Windows opens the app in the tray when you sign in. Keep the .exe in the same folder.\n\nYour settings stay saved across app versions. More... → Guide opens this guide again.")
    ];

    public GuideForm()
    {
        Text = "VN Presence guide"; Icon = Program.AppIcon; Font = new Font(Program.UiFontName, 9);
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
