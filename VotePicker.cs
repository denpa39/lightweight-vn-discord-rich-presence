using System.Globalization;
using System.Runtime.InteropServices;

namespace VnPresence;

internal sealed class VotePicker : Button
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr parameter, string text);
    private readonly ToolStripDropDown popup = new() { Padding = Padding.Empty, AutoSize = false };
    private readonly TextBox input = new() { Dock = DockStyle.Top, PlaceholderText = "Vote (1.0–10.0)" };
    private readonly ListBox choices = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private static readonly string[] Names = ["worst ever", "awful", "bad", "weak", "so-so", "decent", "good", "very good", "excellent", "masterpiece"];
    private record Option(int? Vote, string Name) { public override string ToString() => Name; }
    private int? value;
    internal event Action? ValueChanged;
    internal int? Value
    {
        get => value;
        set
        {
            var changed = this.value != value;
            this.value = value; Text = (value == null ? "Vote" : (value.Value / 10m).ToString("0.#", CultureInfo.InvariantCulture)) + "  ▾";
            if (changed) ValueChanged?.Invoke();
        }
    }
    internal static bool TryVote(string text, out int? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
            number < 1 || number > 10 || number * 10 != decimal.Truncate(number * 10)) return false;
        result = (int)(number * 10); return true;
    }
    internal VotePicker()
    {
        Width = 155; Height = 25; Value = null;
        var panel = new Panel { Width = 190 };
        choices.ItemHeight = Font.Height + 4;
        panel.Controls.Add(choices); panel.Controls.Add(input);
        var host = new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false };
        popup.Items.Add(host);
        void FitList()
        {
            var rowHeight = choices.Items.Count == 0 ? choices.ItemHeight : choices.GetItemRectangle(0).Height;
            if (rowHeight <= 0) rowHeight = choices.ItemHeight;
            var height = Math.Max(1, choices.Items.Count) * rowHeight;
            host.Size = new Size(190, input.PreferredHeight + height);
            panel.Size = host.Size; popup.Size = new Size(host.Width + 2, host.Height + 2);
            panel.PerformLayout(); popup.PerformLayout();
            if (choices.Items.Count > 0) choices.TopIndex = 0;
        }
        popup.Opened += (_, _) => FitList();
        void FillList()
        {
            choices.Items.Clear();
            for (var i = 10; i >= 1; i--)
                choices.Items.Add(new Option(i * 10, $"{i} ({Names[i - 1]})"));
            if (Value != null) choices.Items.Add(new Option(null, "Remove vote"));
            FitList();
        }
        void Commit(int? selected) { Value = selected; popup.Close(); Focus(); }
        input.GotFocus += (_, _) => SendMessageW(input.Handle, 0x1501, new IntPtr(1), input.PlaceholderText);
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            { e.SuppressKeyPress = true; if (TryVote(input.Text.Trim(), out var selected)) Commit(selected); else System.Media.SystemSounds.Beep.Play(); }
            if (e.KeyCode == Keys.Down && choices.Items.Count > 0) { choices.SelectedIndex = 0; choices.Focus(); e.SuppressKeyPress = true; }
        };
        choices.MouseClick += (_, e) => { var index = choices.IndexFromPoint(e.Location); if (index >= 0) Commit(((Option)choices.Items[index]).Vote); };
        choices.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && choices.SelectedItem is Option selected) { Commit(selected.Vote); e.SuppressKeyPress = true; } };
        Click += (_, _) => { input.Text = ""; FillList(); popup.Show(this, new Point(0, Height)); input.Focus(); };
    }
    internal void TestDropdown()
    {
        Enabled = true; Value = 90; PerformClick();
        if (choices.Items.Count != 11 || choices.GetItemRectangle(10).Bottom > choices.ClientSize.Height)
            throw new InvalidOperationException("All ten ratings and the empty vote must fit without scrolling.");
        if (((Option)choices.Items[10]).Name != "Remove vote") throw new InvalidOperationException("An existing vote must have a clear removal option.");
        if (choices.ClientSize.Height - choices.GetItemRectangle(10).Bottom > 2)
            throw new InvalidOperationException("The dropdown must not leave blank space below its options.");
        if (popup.Height < input.Height + choices.GetItemRectangle(10).Bottom)
            throw new InvalidOperationException("The popup must display the whole list.");
        if (choices.GetItemRectangle(0).Height <= 0 || popup.Height < 100)
            throw new InvalidOperationException("The rendered list must have visible rows.");
        using var bitmap = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save("ui-preview-vote.png");
        var originalSize = popup.Size;
        input.Text = "5";
        if (choices.Items.Count != 11 || popup.Size != originalSize) throw new InvalidOperationException("Typing must not filter or resize the rating list.");
        input.Text = "5.6";
        if (choices.Items.Count != 11 || popup.Size != originalSize) throw new InvalidOperationException("A decimal vote must leave all ratings visible.");
        typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(input, [new KeyEventArgs(Keys.Enter)]);
        if (Value != 56 || popup.Visible) throw new InvalidOperationException("Enter must choose the decimal vote and close the dropdown.");
        Value = null;
        PerformClick();
        if (choices.Items.Count != 10 || choices.Items.Cast<Option>().Any(o => o.Vote == null))
            throw new InvalidOperationException("An empty vote must not offer a selectable placeholder.");
        popup.Close();
    }
    protected override void Dispose(bool disposing) { if (disposing) popup.Dispose(); base.Dispose(disposing); }
}
