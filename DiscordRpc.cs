using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace VnPresence;

public sealed class DiscordRpc : IDisposable
{
    private readonly string pipePrefix;
    public DiscordRpc(string pipePrefix = "discord-ipc-") { this.pipePrefix = pipePrefix; }
    private NamedPipeClientStream? pipe;
    private string? clientId;
    public bool Connected => pipe?.IsConnected == true;
    public void Dispose() { pipe?.Dispose(); pipe = null; clientId = null; }
    public async Task Connect(string id, CancellationToken token)
    {
        if (Connected && clientId == id) return;
        Dispose();
        for (var i = 0; i < 10; i++)
        {
            var candidate = new NamedPipeClientStream(".", $"{pipePrefix}{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await candidate.ConnectAsync(150, token); pipe = candidate; break; }
            catch (TimeoutException) { candidate.Dispose(); }
            catch { candidate.Dispose(); throw; }
        }
        if (pipe == null) throw new IOException("Open the Discord desktop app. Retrying automatically…");
        try
        {
            await Write(0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = id }), token);
            using var ready = await Read(token);
            if (!ready.RootElement.TryGetProperty("evt", out var evt) || evt.GetString() != "READY")
                throw new IOException("Discord rejected the connection. Check your Application ID.");
            clientId = id;
        }
        catch { Dispose(); throw; }
    }
    public static string Short(string text) => string.Concat(text.EnumerateRunes().Take(120));
    internal static string ProfileLabel(string? username)
    {
        var name = username?.Trim();
        if (string.IsNullOrEmpty(name)) return "VNDB profile";
        const string prefix = "Visit ", suffix = "’s VNDB profile";
        var limit = 32 - prefix.Length - suffix.Length;
        if (name.Length > limit)
        {
            var length = limit - 1;
            if (char.IsHighSurrogate(name[length - 1])) length--;
            name = name[..length] + "…";
        }
        return prefix + name + suffix;
    }
    public static object Activity(GameLink link, long start, string? progress = null, string? profileUrl = null, string? username = null)
    {
        var activity = new Dictionary<string, object>
        {
            ["type"] = 0, ["name"] = Short(link.Novel.NativeTitle), ["status_display_type"] = 0,
            ["timestamps"] = new { start }
        };
        if (link.Novel.Brand != null) activity["details"] = Short(link.Novel.Brand);
        if (link.Novel.BrandUrl != null) activity["details_url"] = link.Novel.BrandUrl;
        if (!string.IsNullOrWhiteSpace(progress)) activity["state"] = Short(progress!);
        var buttons = new[] { new { label = "View on VNDB", url = link.Novel.GameUrl } }.ToList();
        if (Settings.NormalizeProfileUrl(profileUrl) is string url)
            buttons.Add(new { label = ProfileLabel(username), url });
        activity["buttons"] = buttons;
        if ((link.Cover ?? link.Novel.Image) is VnImage image)
            activity["assets"] = new { large_image = image.Url, large_text = Short(link.Novel.NativeTitle), large_url = link.Novel.GameUrl };
        return activity;
    }
    public async Task Update(object? activity, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString();
        await Write(1, JsonSerializer.SerializeToUtf8Bytes(new { cmd = "SET_ACTIVITY", args = new { pid = Program.ProcessId, activity }, nonce }), token);
        while (true)
        {
            using var reply = await Read(token);
            var root = reply.RootElement;
            if (!root.TryGetProperty("nonce", out var n) || n.GetString() != nonce) continue;
            if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                throw new IOException("Discord: " + root.GetProperty("data").GetProperty("message").GetString());
            return;
        }
    }
    private async Task Write(int opcode, byte[] body, CancellationToken token)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
        await pipe!.WriteAsync(header, token);
        await pipe!.WriteAsync(body, token);
        await pipe!.FlushAsync(token);
    }
    private async Task<JsonDocument> Read(CancellationToken token)
    {
        while (true)
        {
            var header = new byte[8];
            await pipe!.ReadExactlyAsync(header, token);
            var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (length < 0 || length > 1024 * 1024) throw new IOException("Invalid Discord frame.");
            var data = new byte[length];
            await pipe!.ReadExactlyAsync(data, token);
            if (opcode == 3) { await Write(4, data, token); continue; }
            if (opcode == 2) throw new IOException("Discord closed the connection. Check the Application ID.");
            if (opcode != 1) continue;
            return JsonDocument.Parse(data);
        }
    }
}
