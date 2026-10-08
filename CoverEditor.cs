using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace VnPresence;

internal static class CoverImages
{
    internal const int Size = 512;
    internal const int MaxUploadBytes = 1280 * 1024;
    internal static Uri? UploadEndpoint => Endpoint(Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "CoverUploadEndpoint")?.Value);
    internal static Uri? Endpoint(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/upload" ? uri : null;
    internal static Uri? HostedCover(string? value) => UploadEndpoint is Uri endpoint &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == endpoint.GetLeftPart(UriPartial.Authority) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/covers/[a-f0-9]{64}\.(png|jpg)$") ? uri : null;

    internal static RectangleF Crop(Size image, float centerX, float centerY, float zoom)
    {
        var side = Math.Min(image.Width, image.Height) / Math.Max(1, Math.Min(4, zoom));
        return new(Math.Max(0, Math.Min(image.Width - side, centerX - side / 2)),
            Math.Max(0, Math.Min(image.Height - side, centerY - side / 2)), side, side);
    }
    internal static RectangleF ImageBounds(Size image, Size viewport)
    {
        var scale = Math.Min((float)viewport.Width / image.Width, (float)viewport.Height / image.Height);
        var width = image.Width * scale; var height = image.Height * scale;
        return new((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }
    internal static Bitmap Preview(Image source, Size viewport)
    {
        var bounds = ImageBounds(source.Size, viewport);
        return new Bitmap(source, Math.Max(1, (int)bounds.Width), Math.Max(1, (int)bounds.Height));
    }
    internal static Bitmap Blur(Image source, RectangleF? bounds = null)
    {
        // ponytail: downsample and smooth back up for a strong blur without a filter dependency.
        using var small = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, new Rectangle(0, 0, 16, 16));
        }
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.Clear(Color.Transparent);
            if (bounds is RectangleF area) graphics.SetClip(area);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(small, new Rectangle(0, 0, result.Width, result.Height), 0, 0, 16, 16, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }
    internal static Bitmap Render(Image source, bool fit, RectangleF crop, int size = Size, bool blur = false)
    {
        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY);
        var area = fit ? new RectangleF(0, 0, source.Width, source.Height) : crop;
        var target = fit ? ImageBounds(source.Size, new(size, size)) : new RectangleF(0, 0, size, size);
        graphics.DrawImage(source, Rectangle.Round(target), area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel, attributes);
        if (blur)
        {
            graphics.Dispose();
            var blurred = Blur(output, fit ? target : null);
            output.Dispose();
            return blurred;
        }
        return output;
    }
    internal static byte[] Encode(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        if (stream.Length > MaxUploadBytes) throw new InvalidDataException("Image is too large to upload.");
        return stream.ToArray();
    }
    internal static async Task<VnImage> Upload(HttpClient http, Uri endpoint, byte[] bytes, CancellationToken token, string? gameId = null, string? ownerKey = null)
    {
        if (Endpoint(endpoint.AbsoluteUri) == null || bytes.Length == 0 || bytes.Length > MaxUploadBytes)
            throw new InvalidDataException("Invalid cover upload.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60)); token = timeout.Token;
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        if (gameId != null) request.Headers.Add("X-VNDB-ID", gameId);
        if (ownerKey != null) request.Headers.Add("X-Cover-Owner", ownerKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                (System.Net.HttpStatusCode)429 => "Too many uploads. Try again later.",
                System.Net.HttpStatusCode.ServiceUnavailable => "Cover hosting has reached its upload limit. Try again later.",
                _ => "Cover upload failed. Your saved cover has not changed."
            });
        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4097]; var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, length, buffer.Length - length, token);
            if (read == 0) break;
            length += read;
        }
        if (length > 4096) throw new InvalidDataException("Invalid upload response.");
        using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
        var value = json.RootElement.GetProperty("url").GetString() ?? "";
        if (!VnImage.ValidCustomUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var image) ||
            image.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority) ||
            !System.Text.RegularExpressions.Regex.IsMatch(image.AbsolutePath, @"^/covers/[a-f0-9]{64}\.png$") ||
            image.Query.Length > 0 || image.Fragment.Length > 0)
            throw new InvalidDataException("The upload service returned an invalid image URL.");
        return new(value, null);
    }
}

internal sealed class CoverEditor : Form
{
    private sealed class CropPreview : PictureBox
    {
        internal CropPreview() { SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, true); TabStop = true; }
    }
    internal VnImage? UploadedImage { get; private set; }
    private readonly Image source;
    private readonly PictureBox preview = new CropPreview { Left = 10, Top = 10, Width = 360, Height = 360, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly RadioButton fit = new() { Text = "Fit whole cover", Left = 390, Top = 16, Width = 170, Checked = true };
    private readonly RadioButton crop = new() { Text = "Crop", Left = 390, Top = 46, Width = 170 };
    private readonly TrackBar zoom = new() { Left = 380, Top = 140, Width = 185, Minimum = 10, Maximum = 40, Value = 10, Enabled = false };
    private readonly Label status = new() { Left = 10, Top = 410, Width = 550, Height = 40 };
    private readonly Button upload = new() { Text = "Upload && use", Left = 350, Top = 460, Width = 115 };
    private readonly CancellationTokenSource cancel = new();
    private float centerX, centerY;
    private Point? drag;
    private PointF dragCenter;
    private bool uploading, resourcesDisposed;
    private readonly CheckBox blur = new() { Text = "Blur cover", Left = 390, Top = 230, AutoSize = true };
    private readonly Bitmap overview;
    internal RectangleF CropArea => CoverImages.Crop(source.Size, centerX, centerY, zoom.Value / 10f);
    internal CoverEditor(Image source, string? gameId = null, Settings? settings = null)
    {
        this.source = source;
        centerX = source.Width / 2f; centerY = source.Height / 2f;
        Text = "Adjust cover"; Icon = Program.AppIcon; Font = new Font(Program.UiFontName, 9); ClientSize = new(580, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        var close = new Button { Text = "Cancel", Left = 475, Top = 460, Width = 90, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save image...", Left = 10, Top = 460, Width = 110 };
        var reset = new Button { Text = "Reset", Left = 390, Top = 186, Width = 75 };
        Controls.AddRange([preview, fit, crop, zoom, status, upload, close, save, reset, blur,
            new Label { Text = "Drag to move\nScroll to zoom", Left = 390, Top = 80, Width = 180, Height = 32 },
            new Label { Text = "Zoom", Left = 390, Top = 122, Width = 170 }]);
        // Cache a small overview once. Dragging never resizes the full source image.
        var scale = Math.Min(1, 512f / Math.Max(source.Width, source.Height));
        overview = new Bitmap(source, Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        preview.Image = new Bitmap(overview);
        blur.CheckedChanged += (_, _) =>
        {
            preview.Image?.Dispose();
            preview.Image = blur.Checked ? CoverImages.Blur(overview) : new Bitmap(overview);
            DrawPreview();
        };
        var checker = new Bitmap(16, 16);
        using (var background = Graphics.FromImage(checker))
        {
            background.Clear(Color.LightGray);
            background.FillRectangle(Brushes.WhiteSmoke, 0, 0, 8, 8);
            background.FillRectangle(Brushes.WhiteSmoke, 8, 8, 8, 8);
        }
        preview.BackgroundImage = checker;
        preview.BackgroundImageLayout = ImageLayout.Tile;
        preview.Paint += (_, e) =>
        {
            if (fit.Checked) return;
            var bounds = CoverImages.ImageBounds(source.Size, preview.ClientSize);
            var area = CropArea; var factor = bounds.Width / source.Width;
            var square = new RectangleF(bounds.X + area.X * factor, bounds.Y + area.Y * factor, area.Width * factor, area.Height * factor);
            using var outside = new Region(bounds); outside.Exclude(square);
            using var shade = new SolidBrush(Color.FromArgb(150, Color.Black)); e.Graphics.FillRegion(shade, outside);
            using var border = new Pen(Color.White, 2); e.Graphics.DrawRectangle(border, square.X, square.Y, square.Width, square.Height);
        };
        status.Text = CoverImages.UploadEndpoint == null ? "Hosting is not connected yet. You can preview or save the image." : "";
        upload.Enabled = CoverImages.UploadEndpoint != null;
        fit.CheckedChanged += (_, _) => { zoom.Enabled = crop.Checked; DrawPreview(); };
        crop.CheckedChanged += (_, _) => { zoom.Enabled = crop.Checked; DrawPreview(); };
        zoom.ValueChanged += (_, _) => DrawPreview();
        reset.Click += (_, _) => { drag = null; preview.Capture = false; centerX = source.Width / 2f; centerY = source.Height / 2f; zoom.Value = 10; DrawPreview(); };
        preview.MouseDown += (_, e) =>
        {
            preview.Focus();
            if (!crop.Checked || e.Button != MouseButtons.Left) return;
            var bounds = CoverImages.ImageBounds(source.Size, preview.ClientSize);
            if (!bounds.Contains(e.Location)) return;
            var point = new PointF((e.X - bounds.X) * source.Width / bounds.Width, (e.Y - bounds.Y) * source.Height / bounds.Height);
            if (!CropArea.Contains(point)) SetCenter(point.X, point.Y);
            var area = CropArea; dragCenter = new(area.X + area.Width / 2, area.Y + area.Height / 2);
            drag = e.Location; preview.Capture = true;
        };
        preview.MouseUp += (_, _) => { drag = null; preview.Capture = false; };
        preview.MouseCaptureChanged += (_, _) => { if (!preview.Capture) drag = null; };
        preview.MouseMove += (_, e) =>
        {
            if (drag is not Point start || !crop.Checked) return;
            var bounds = CoverImages.ImageBounds(source.Size, preview.ClientSize);
            SetCenter(dragCenter.X + (e.X - start.X) * source.Width / bounds.Width,
                dragCenter.Y + (e.Y - start.Y) * source.Height / bounds.Height);
        };
        preview.MouseWheel += (_, e) =>
        {
            if (!crop.Checked || uploading) return;
            drag = null; preview.Capture = false;
            zoom.Value = Math.Max(zoom.Minimum, Math.Min(zoom.Maximum, zoom.Value + Math.Sign(e.Delta) * 2));
        };
        preview.PreviewKeyDown += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) e.IsInputKey = true; };
        preview.KeyDown += (_, e) =>
        {
            if (!crop.Checked) return;
            var area = CropArea; var x = area.X + area.Width / 2; var y = area.Y + area.Height / 2; var step = area.Width / 50;
            if (e.KeyCode == Keys.Left) x -= step; else if (e.KeyCode == Keys.Right) x += step;
            else if (e.KeyCode == Keys.Up) y -= step; else if (e.KeyCode == Keys.Down) y += step; else return;
            e.Handled = true; e.SuppressKeyPress = true; SetCenter(x, y);
        };
        save.Click += (_, _) =>
        {
            using var file = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "cover.png" };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            try { using var output = CoverImages.Render(source, fit.Checked, CropArea, blur: blur.Checked); File.WriteAllBytes(file.FileName, CoverImages.Encode(output)); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Save image"); }
        };
        upload.Click += async (_, _) =>
        {
            if (uploading || CoverImages.UploadEndpoint is not Uri endpoint) return;
            drag = null; preview.Capture = false;
            uploading = true; upload.Enabled = false; blur.Enabled = fit.Enabled = crop.Enabled = zoom.Enabled = preview.Enabled = save.Enabled = reset.Enabled = false;
            status.Text = "Uploading…";
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
                using var output = CoverImages.Render(source, fit.Checked, CropArea, blur: blur.Checked);
                UploadedImage = await CoverImages.Upload(http, endpoint, CoverImages.Encode(output), cancel.Token, gameId, settings?.CoverOwnerKey(true));
                if (!cancel.IsCancellationRequested) DialogResult = DialogResult.OK;
            }
            catch (Exception error) { if (!cancel.IsCancellationRequested) status.Text = error is OperationCanceledException ? "Upload timed out. Try again." : error.Message; }
            finally
            {
                if (!cancel.IsCancellationRequested)
                { uploading = false; upload.Enabled = blur.Enabled = fit.Enabled = crop.Enabled = preview.Enabled = save.Enabled = reset.Enabled = true; zoom.Enabled = crop.Checked; }
            }
        };
        FormClosing += (_, _) => cancel.Cancel();
        CancelButton = close; DrawPreview();
    }
    private void DrawPreview()
    {
        preview.Cursor = crop.Checked ? Cursors.SizeAll : Cursors.Default;
        preview.Invalidate();
    }
    private void SetCenter(float x, float y)
    {
        var area = CoverImages.Crop(source.Size, x, y, zoom.Value / 10f);
        var nextX = area.X + area.Width / 2; var nextY = area.Y + area.Height / 2;
        if (nextX == centerX && nextY == centerY) return;
        centerX = nextX; centerY = nextY;
        DrawPreview();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true; cancel.Cancel(); cancel.Dispose();
            preview.Image?.Dispose(); preview.Image = null;
            overview.Dispose();
            preview.BackgroundImage?.Dispose(); preview.BackgroundImage = null;
        }
        base.Dispose(disposing);
    }
}
