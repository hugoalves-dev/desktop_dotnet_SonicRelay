using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Lan.Tests;

/// <summary>A connected server/client WebSocket pair over loopback TCP.</summary>
internal sealed class SocketPair : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly TcpClient server;

    private SocketPair(TcpClient client, TcpClient server)
    {
        this.client = client;
        this.server = server;
        Client = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, TimeSpan.Zero);
        Server = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, TimeSpan.Zero);
    }

    public WebSocket Client { get; }
    public WebSocket Server { get; }

    public static async Task<SocketPair> CreateAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        await connect;
        return new SocketPair(client, server);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Server.Dispose();
        client.Dispose();
        server.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>The viewer end of a LAN signaling socket, reading envelopes into a queue.</summary>
internal sealed class TestViewer : IAsyncDisposable
{
    private readonly SocketPair pair;
    private readonly BlockingCollection<SignalingMessageEnvelope> inbox = new();
    private readonly Task reader;

    public TestViewer(string id, SocketPair pair, Task serving)
    {
        Id = id;
        this.pair = pair;
        Serving = serving;
        reader = Task.Run(ReadAsync);
    }

    public string Id { get; }
    public Task Serving { get; }
    public WebSocketCloseStatus? CloseStatus { get; private set; }
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SendAsync(SignalingMessageEnvelope envelope) => SendRawAsync(envelope.Serialize());

    public Task SendRawAsync(string json) =>
        pair.Client.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    public SignalingMessageEnvelope Next(string type, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (inbox.TryTake(out var envelope, TimeSpan.FromMilliseconds(50)) && envelope.Type == type) return envelope;
        }

        throw new TimeoutException($"Viewer {Id} never received '{type}'.");
    }

    public bool Received(string type, TimeSpan wait)
    {
        try
        {
            Next(type, wait);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task<WebSocketCloseStatus?> WaitClosedAsync()
    {
        await Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return CloseStatus;
    }

    public async Task DropAsync()
    {
        pair.Client.Abort();
        await Serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[128 * 1024];
        try
        {
            while (true)
            {
                var result = await pair.Client.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = result.CloseStatus;
                    break;
                }

                inbox.Add(SignalingMessageEnvelope.Deserialize(Encoding.UTF8.GetString(buffer, 0, result.Count)));
            }
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException or OperationCanceledException)
        {
        }
        finally
        {
            Closed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await pair.DisposeAsync();
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }
    }
}

/// <summary>Records what the hub delivers to the publisher side.</summary>
internal sealed class RecordingHandler : ISignalingMessageHandler
{
    private readonly BlockingCollection<SignalingMessageEnvelope> received = new();

    public List<SignalingMessageEnvelope> All { get; } = [];

    public Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        lock (All) All.Add(message);
        received.Add(message, cancellationToken);
        return Task.CompletedTask;
    }

    public SignalingMessageEnvelope Next(string type, string? from = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (received.TryTake(out var envelope, TimeSpan.FromMilliseconds(50))
                && envelope.Type == type
                && (from is null || envelope.From == from))
            {
                return envelope;
            }
        }

        throw new TimeoutException($"The publisher never received '{type}' from '{from}'.");
    }

    public bool Received(string type, string? from, TimeSpan wait)
    {
        try
        {
            Next(type, from, wait);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

internal static class Json
{
    public static JsonElement Of(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
