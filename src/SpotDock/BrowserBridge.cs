using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotDock;

/// <summary>A snapshot of what is playing in a browser tab.</summary>
public sealed record BrowserState(
    bool Playing, string Title, string Artist, string Art,
    double Position, double Duration,
    bool CanSeek, bool CanNext, bool CanPrev,
    bool CanLike, bool? Liked, DateTime At);

/// <summary>
/// Receiver for data from the SpotDock Bridge extension for Spotify Web
/// (open.spotify.com / PWA).
///
/// A minimal WebSocket server on top of TcpListener: HttpListener would need
/// a netsh urlacl entry or administrator rights for the same job, while the
/// widget runs as an ordinary user. Listens on loopback only.
/// </summary>
public sealed class BrowserBridge
{
    public const int Port = 45219;

    private static readonly byte[] Guid =
        Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private BrowserState? _state;
    private NetworkStream? _client;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public BrowserBridge(int port = Port) => _port = port;

    /// <summary>The actual TCP port currently bound (equals <see cref="Port"/> in production).</summary>
    public int ListeningPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    /// <summary>The track/state changed.</summary>
    public event Action? Changed;
    /// <summary>Only a new position arrived - the handler must stay light.</summary>
    public event Action? TimelineChanged;

    /// <summary>The extension activated a tab on the "focus" command and sent
    /// its title: bringing the window to the foreground is done by the widget
    /// itself. Raised from the socket thread.</summary>
    public event Action<string>? FocusReady;

    /// <summary>A paused state lives as long as the connection holds: the
    /// browser throttles timers in a quiet background tab and reports from
    /// there arrive once a minute - the widget used to blink off on a timeout.
    /// An open socket is a more honest liveness signal; when it closes the
    /// state is cleared.</summary>
    private static readonly TimeSpan PlayingTtl = TimeSpan.FromSeconds(30);

    /// <summary>The last state; a playing track with no reports for longer
    /// than <see cref="PlayingTtl"/> is considered abandoned - the browser does
    /// not throttle a tab that is making sound, so the source itself went
    /// quiet.</summary>
    public BrowserState? Current
    {
        get
        {
            lock (_gate)
            {
                if (_state == null) return null;
                if (!_state.Playing) return _state;
                return DateTime.UtcNow - _state.At > PlayingTtl ? null : _state;
            }
        }
    }

    public void Start()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            // The port is taken by another copy of the widget - not fatal,
            // the browser source just will not work in this window
            Diag.Once("bridge-listen", $"Browser bridge could not listen on 127.0.0.1:{_port}: {ex.Message}");
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _listener = null;
            return;
        }
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Shutdown()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        lock (_gate) { _client = null; _state = null; }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ServeAsync(tcp, ct), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                tcp?.Dispose();
                if (ct.IsCancellationRequested) return;
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        NetworkStream? stream = null;
        try
        {
            tcp.NoDelay = true;
            stream = tcp.GetStream();
            if (!await HandshakeAsync(stream, ct)) return;

            lock (_gate) _client = stream; // the last one to connect is the current one

            using var acc = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, ct);
                if (frame == null) break;
                var (opcode, fin, payload) = frame.Value;

                if (opcode == 0x8) break;                       // close
                if (opcode == 0x9)                              // ping -> pong
                {
                    await SendFrameLockedAsync(stream, 0xA, payload, ct);
                    continue;
                }
                if (opcode == 0xA) continue;                    // pong

                acc.Write(payload, 0, payload.Length);
                if (!fin) continue;
                var text = acc.TryGetBuffer(out var seg)
                    ? Encoding.UTF8.GetString(seg.AsSpan())
                    : Encoding.UTF8.GetString(acc.ToArray());
                acc.SetLength(0);
                if (acc.Capacity > 65536) acc.Capacity = 4096;
                Apply(text);
            }
        }
        catch (Exception) { /* the tab/browser closed - perfectly normal */ }
        finally
        {
            // While this client was dropping, a new one may have connected -
            // its state must not be wiped
            bool wasCurrent;
            lock (_gate)
            {
                wasCurrent = stream != null && ReferenceEquals(_client, stream);
                if (wasCurrent) { _client = null; _state = null; }
            }
            if (wasCurrent) Changed?.Invoke();
        }
    }

    private async Task<bool> HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        using var head = new MemoryStream();
        var buf = new byte[1];
        int zeros = 0;
        // The whole header: read until \r\n\r\n, but no more than 8 KB
        while (head.Length < 8192)
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n <= 0) return false;
            head.WriteByte(buf[0]);
            zeros = buf[0] switch
            {
                (byte)'\r' => zeros is 0 or 2 ? zeros + 1 : 1,
                (byte)'\n' => zeros is 1 or 3 ? zeros + 1 : 0,
                _ => 0,
            };
            if (zeros == 4) break;
        }

        string request = head.TryGetBuffer(out var seg)
            ? Encoding.ASCII.GetString(seg.AsSpan())
            : Encoding.ASCII.GetString(head.ToArray());
        string? key = null, origin = null;
        foreach (var line in request.Split("\r\n"))
        {
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            string name = line[..c].Trim();
            string value = line[(c + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
        }

        // Only the extension gets in: any page in the browser can also knock
        // on localhost, but its Origin will be http(s)://...
        bool ok = key != null && origin != null &&
                  origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            var deny = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(deny, ct);
            return false;
        }

        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key!).Concat(Guid).ToArray()));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response, ct);
        return true;
    }

    private static async Task<(int Opcode, bool Fin, byte[] Payload)?> ReadFrameAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var head = new byte[2];
        if (!await ReadExactAsync(stream, head, 2, ct)) return null;
        bool fin = (head[0] & 0x80) != 0;
        int opcode = head[0] & 0x0F;
        bool masked = (head[1] & 0x80) != 0;
        long len = head[1] & 0x7F;

        if (len == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, 2, ct)) return null;
            len = (ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, 8, ct)) return null;
            len = 0;
            for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
        }
        // The extension sends short JSON; anything bigger is junk or not our client
        if (len < 0 || len > 1_000_000) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, 4, ct)) return null;

        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(stream, payload, (int)len, ct)) return null;
        if (masked)
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

        return (opcode, fin, payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static async Task SendFrameAsync(NetworkStream stream, int opcode, byte[] payload, CancellationToken ct)
    {
        byte[] head;
        if (payload.Length < 126)
        {
            head = new byte[2];
            head[0] = (byte)(0x80 | opcode);
            head[1] = (byte)payload.Length; // the server does not mask
        }
        else
        {
            head = new byte[4];
            head[0] = (byte)(0x80 | opcode);
            head[1] = 126;
            head[2] = (byte)(payload.Length >> 8);
            head[3] = (byte)(payload.Length & 0xFF);
        }
        await stream.WriteAsync(head, ct);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    // Bridge debugging: enabled by a bridge-debug.txt file next to the exe,
    // writes the extension's raw messages to bridge.log. Checked once at
    // startup - only a bool comparison remains on the hot path
    private static readonly bool DebugLog =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "bridge-debug.txt"));

    private static void Trace(string line)
    {
        if (!DebugLog) return;
        try
        {
            var dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "bridge.log");
            if (File.Exists(file) && new FileInfo(file).Length > 500_000) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
        }
        catch { }
    }

    private void Apply(string json)
    {
        Trace(json.Length > 8000 ? json[..8000] : json);

        BrowserState? next;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "focused")
            {
                // A reply to "focus", not a state - leave the track alone
                FocusReady?.Invoke(Str(root, "title"));
                return;
            }
            if (type == "idle")
            {
                next = null;
            }
            else if (type == "state")
            {
                string host = Str(root, "host");
                next = IsSpotifyHost(host)
                    ? new BrowserState(
                        Playing: Bool(root, "playing"),
                        Title: Str(root, "title"),
                        Artist: Str(root, "artist"),
                        Art: Str(root, "art"),
                        Position: Num(root, "position"),
                        Duration: Num(root, "duration"),
                        CanSeek: Bool(root, "canSeek"),
                        CanNext: Bool(root, "canNext"),
                        CanPrev: Bool(root, "canPrev"),
                        CanLike: Bool(root, "canLike"),
                        Liked: BoolOrNull(root, "liked"),
                        At: DateTime.UtcNow)
                    : null;
            }
            else return;
        }
        catch (Exception ex)
        {
            Diag.Once("bridge-json", "Browser bridge got malformed JSON: " + ex.Message);
            return;
        }

        BrowserState? prev;
        lock (_gate)
        {
            prev = _state;
            _state = next;
        }

        // The track/state changed - full refresh; otherwise it is only the
        // position, and there is no reason to trigger the heavy path with
        // artwork and UIA
        bool heavy = prev == null || next == null ||
                     prev.Title != next.Title || prev.Artist != next.Artist ||
                     prev.Art != next.Art || prev.Playing != next.Playing ||
                     prev.CanNext != next.CanNext || prev.CanPrev != next.CanPrev ||
                     prev.Liked != next.Liked || prev.CanLike != next.CanLike;
        Trace($"-> playing={next?.Playing} title='{next?.Title}' dur={next?.Duration:F0} heavy={heavy}");
        if (heavy) Changed?.Invoke();
        else TimelineChanged?.Invoke();
    }

    internal static bool IsSpotifyHost(string host) =>
        host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Three states: yes, no, and "the site does not say" (no button).</summary>
    private static bool? BoolOrNull(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) ? null
        : v.ValueKind == JsonValueKind.True ? true
        : v.ValueKind == JsonValueKind.False ? false
        : null;

    private async Task SendFrameLockedAsync(NetworkStream stream, int opcode, byte[] payload, CancellationToken ct)
    {
        // One frame at a time: dragging sliders or receiving pings can overlap
        // with commands, and two interleaved writes on one stream tear frames.
        await _sendGate.WaitAsync(ct);
        try { await SendFrameAsync(stream, opcode, payload, ct); }
        finally { _sendGate.Release(); }
    }

    /// <summary>Sends a command to the extension; goes silently nowhere if no
    /// browser is connected.</summary>
    public void Send(string cmd, double? pos = null)
    {
        NetworkStream? stream;
        lock (_gate) stream = _client;
        if (stream == null) return;

        string json = pos.HasValue
            ? $"{{\"cmd\":\"{cmd}\",\"pos\":{pos.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"
            : $"{{\"cmd\":\"{cmd}\"}}";
        _ = Task.Run(async () =>
        {
            try { await SendFrameLockedAsync(stream, 0x1, Encoding.UTF8.GetBytes(json), CancellationToken.None); }
            catch { lock (_gate) { if (ReferenceEquals(_client, stream)) _client = null; } }
        });
    }
}
