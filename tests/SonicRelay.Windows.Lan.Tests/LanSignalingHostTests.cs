using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Lan.Identity;
using SonicRelay.Windows.Lan.Pairing;
using SonicRelay.Windows.Lan.Trust;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanSignalingHostTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-host-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
    private readonly RecordingSink sink = new();
    private LanIdentity identity = null!;
    private LanPairingService pairing = null!;
    private LanTrustStore trust = null!;
    private LanSignalingHost host = null!;

    public async Task InitializeAsync()
    {
        identity = await new LanIdentityStore(new PassThroughProtector(), directory).LoadOrCreateAsync();
        pairing = new LanPairingService(identity.Pin, time);
        trust = new LanTrustStore(new PassThroughProtector(), Path.Combine(directory, "trust.dat"), time);
        host = new LanSignalingHost(identity, pairing, trust, sink,
            new LanHostOptions { BindAddress = IPAddress.Loopback, PublisherName = "DESKTOP-01" }, time);
        await host.StartAsync(port: null);
    }

    public async Task DisposeAsync()
    {
        await host.StopAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private Uri BaseUri => new($"https://127.0.0.1:{host.Port}/");

    private HttpClient Client(string? pin = null) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && LanIdentity.ComputePin(certificate.RawData) == (pin ?? identity.Pin),
    })
    { BaseAddress = BaseUri };

    private static object PairBody(LanPairingWindow window, string viewer = "viewer-1") => new
    {
        pid = window.PairingId,
        sec = System.Buffers.Text.Base64Url.EncodeToString(window.Secret),
        viewerDeviceId = viewer,
        viewerName = "Pixel",
        platform = "android",
    };

    private async Task<ClientWebSocket> ConnectAsync(string viewer, string credential)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && LanIdentity.ComputePin(certificate.GetRawCertData()) == identity.Pin;
        socket.Options.SetRequestHeader("Authorization", $"SonicRelay-Lan {credential}");
        socket.Options.SetRequestHeader("X-SonicRelay-Device", viewer);
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{host.Port}/lan/v1/signaling"), CancellationToken.None);
        return socket;
    }

    private static async Task<WebSocketCloseStatus?> ReadCloseAsync(ClientWebSocket socket)
    {
        var buffer = new byte[1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return result.CloseStatus;
        }
    }

    [Fact]
    public async Task Pair_ValidSecret_ReturnsCredential()
    {
        var window = pairing.Open();
        using var client = Client();

        var response = await client.PostAsJsonAsync("lan/v1/pair", PairBody(window));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var credential = body.GetProperty("credential").GetString()!;
        Assert.Equal(identity.PublisherDeviceId.ToString("D"), body.GetProperty("publisherDeviceId").GetString());
        Assert.Equal("DESKTOP-01", body.GetProperty("publisherName").GetString());
        Assert.Equal(identity.Pin, body.GetProperty("pin").GetString());
        Assert.NotNull(trust.Authenticate("viewer-1", credential));
    }

    [Fact]
    public async Task Pair_Replay_Returns400()
    {
        var window = pairing.Open();
        using var client = Client();
        await client.PostAsJsonAsync("lan/v1/pair", PairBody(window));

        var replay = await client.PostAsJsonAsync("lan/v1/pair", PairBody(window, "attacker"));

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("pairing_invalid", await replay.Content.ReadAsStringAsync());
        Assert.Null(trust.Devices.SingleOrDefault(device => device.ViewerDeviceId == "attacker"));
    }

    [Fact]
    public async Task Pair_Expired_Returns410()
    {
        var window = pairing.Open();
        time.Advance(TimeSpan.FromSeconds(121));
        using var client = Client();

        var response = await client.PostAsJsonAsync("lan/v1/pair", PairBody(window));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("pairing_expired", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pair_RateLimit_After5Failures_Returns429()
    {
        pairing.Open();
        using var client = Client();
        var wrong = new { pid = Guid.NewGuid(), sec = "AAAAAAAAAAAAAA", viewerDeviceId = "v", viewerName = "n", platform = "p" };
        for (var i = 0; i < 5; i++) await client.PostAsJsonAsync("lan/v1/pair", wrong);

        var blocked = await client.PostAsJsonAsync("lan/v1/pair", PairBody(pairing.Open()));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        time.Advance(TimeSpan.FromSeconds(61));
        var allowed = await client.PostAsJsonAsync("lan/v1/pair", PairBody(pairing.Open()));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Pair_MalformedBody_Returns400()
    {
        using var client = Client();

        var response = await client.PostAsync("lan/v1/pair", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Signaling_ValidCredential_ReachesSink()
    {
        var credential = await trust.AddAsync("viewer-1", "Pixel", "android");

        using var socket = await ConnectAsync("viewer-1", credential);

        var connection = await sink.WaitAsync();
        Assert.Equal("viewer-1", connection.ViewerDeviceId);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task Signaling_BadCredential_Closes4401()
    {
        await trust.AddAsync("viewer-1", "Pixel", "android");

        using var socket = await ConnectAsync("viewer-1", "wrong");

        Assert.Equal((WebSocketCloseStatus)4401, await ReadCloseAsync(socket));
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task Signaling_Revoked_Closes4403()
    {
        var credential = await trust.AddAsync("viewer-1", "Pixel", "android");
        await trust.RevokeAsync("viewer-1");

        using var socket = await ConnectAsync("viewer-1", credential);

        Assert.Equal((WebSocketCloseStatus)4403, await ReadCloseAsync(socket));
    }

    [Fact]
    public async Task UnknownPath_404()
    {
        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("lan/v1/info")).StatusCode);
    }

    [Fact]
    public async Task WrongPin_ClientRejectsHandshake()
    {
        using var client = Client(pin: LanIdentity.ComputePin([1]));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("lan/v1/pair"));
    }

    [Fact]
    public async Task Start_PortInUse_ReportsBindFailed()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var other = new LanSignalingHost(identity, pairing, trust, sink,
            new LanHostOptions { BindAddress = IPAddress.Loopback, PublisherName = "D" }, time);

        await other.StartAsync(busy);

        Assert.Equal(LanHostState.BindFailed, other.State);
        Assert.NotNull(other.Error);
        await other.StopAsync();
    }

    [Fact]
    public async Task Stop_ClosesListener()
    {
        var port = host.Port!.Value;
        await host.StopAsync();

        Assert.Equal(LanHostState.Stopped, host.State);
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(IPAddress.Loopback, port));
    }

    private sealed class RecordingSink : ILanConnectionSink
    {
        private readonly TaskCompletionSource<LanPeerConnection> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;

        public int Count => count;

        public async Task AcceptAsync(LanPeerConnection connection, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            first.TrySetResult(connection);
            var buffer = new byte[1024];
            try
            {
                while (connection.Socket.State == WebSocketState.Open)
                    await connection.Socket.ReceiveAsync(buffer, cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
            }
        }

        public Task<LanPeerConnection> WaitAsync() => first.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
