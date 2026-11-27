using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SpotDock;
using Xunit;

namespace SpotDock.Tests;

public sealed class BrowserBridgeTests
{
    [Theory]
    [InlineData("open.spotify.com", true)]
    [InlineData("OPEN.SPOTIFY.COM", true)]
    [InlineData("play.spotify.com", true)]
    [InlineData("spotify.com.evil.com", false)]
    [InlineData("notspotify.com", false)]
    [InlineData("youtube.com", false)]
    [InlineData("", false)]
    public void IsSpotifyHost_AllowsOnlySpotifyDomains(string host, bool expected)
    {
        Assert.Equal(expected, BrowserBridge.IsSpotifyHost(host));
    }

    [Fact]
    public async Task Handshake_RejectsNonExtensionOrigin()
    {
        var bridge = new BrowserBridge(port: 0);
        bridge.Start();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", bridge.ListeningPort);
            using var stream = tcp.GetStream();

            string req =
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Origin: https://open.spotify.com\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(req));

            var buf = new byte[256];
            int n = await stream.ReadAsync(buf);
            string resp = Encoding.ASCII.GetString(buf, 0, n);
            Assert.StartsWith("HTTP/1.1 403", resp, StringComparison.Ordinal);
        }
        finally
        {
            bridge.Shutdown();
        }
    }

    [Fact]
    public async Task WebSocket_AcceptsSpotifyState_IgnoresOtherHosts_AndRepliesToPing()
    {
        var bridge = new BrowserBridge(port: 0);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var focused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.Changed += () => changed.TrySetResult();
        bridge.FocusReady += title => focused.TrySetResult(title);
        bridge.Start();

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", bridge.ListeningPort);
            using var stream = tcp.GetStream();

            const string key = "dGhlIHNhbXBsZSBub25jZQ==";
            string req =
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Origin: chrome-extension://abcdef\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(req));

            string headers = await ReadHttpHeadersAsync(stream);
            Assert.StartsWith("HTTP/1.1 101", headers, StringComparison.Ordinal);
            string expectedAccept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            Assert.Contains($"Sec-WebSocket-Accept: {expectedAccept}", headers, StringComparison.Ordinal);

            // Send valid Spotify Web state
            string spotifyJson =
                """{"type":"state","host":"open.spotify.com","playing":true,"title":"Track 1","artist":"Band","art":"https://i.scdn.co/image/1","position":12.5,"duration":200,"canSeek":true,"canNext":true,"canPrev":true,"canLike":true,"liked":true}""";
            await SendMaskedFrameAsync(stream, 0x1, Encoding.UTF8.GetBytes(spotifyJson));
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var cur = bridge.Current;
            Assert.NotNull(cur);
            Assert.Equal("Track 1", cur!.Title);
            Assert.Equal("Band", cur.Artist);
            Assert.True(cur.Playing);
            Assert.True(cur.Liked);

            // Send focused reply
            await SendMaskedFrameAsync(stream, 0x1, Encoding.UTF8.GetBytes("""{"type":"focused","title":"Spotify – Web Player"}"""));
            Assert.Equal("Spotify – Web Player", await focused.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // Send ping and verify pong payload round-trip
            byte[] pingPayload = Encoding.ASCII.GetBytes("hi");
            await SendMaskedFrameAsync(stream, 0x9, pingPayload);
            var (opcode, payload) = await ReadServerFrameAsync(stream);
            Assert.Equal(0xA, opcode);
            Assert.Equal(pingPayload, payload);

            // Non-Spotify host clears state
            var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.Changed += () => cleared.TrySetResult();
            await SendMaskedFrameAsync(stream, 0x1, Encoding.UTF8.GetBytes(
                """{"type":"state","host":"youtube.com","playing":true,"title":"Video"}"""));
            await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(bridge.Current);
        }
        finally
        {
            bridge.Shutdown();
        }
    }

    private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int n = await stream.ReadAsync(buf);
            if (n <= 0) break;
            sb.Append((char)buf[0]);
        }
        return sb.ToString();
    }

    private static async Task SendMaskedFrameAsync(NetworkStream stream, int opcode, byte[] payload)
    {
        byte[] mask = { 0x12, 0x34, 0x56, 0x78 };
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | opcode));
        if (payload.Length < 126)
        {
            ms.WriteByte((byte)(0x80 | payload.Length));
        }
        else
        {
            ms.WriteByte(0x80 | 126);
            ms.WriteByte((byte)(payload.Length >> 8));
            ms.WriteByte((byte)(payload.Length & 0xFF));
        }
        ms.Write(mask, 0, 4);
        var masked = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++) masked[i] = (byte)(payload[i] ^ mask[i & 3]);
        ms.Write(masked, 0, masked.Length);
        await stream.WriteAsync(ms.ToArray());
        await stream.FlushAsync();
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadServerFrameAsync(NetworkStream stream)
    {
        var head = new byte[2];
        await stream.ReadExactlyAsync(head);
        int opcode = head[0] & 0x0F;
        int len = head[1] & 0x7F;
        if (len == 126)
        {
            var ext = new byte[2];
            await stream.ReadExactlyAsync(ext);
            len = (ext[0] << 8) | ext[1];
        }
        var payload = new byte[len];
        if (len > 0) await stream.ReadExactlyAsync(payload);
        return (opcode, payload);
    }
}
