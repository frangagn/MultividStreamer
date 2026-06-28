using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MultividStreamer.App.Services.Api;

/// <summary>
/// What a discovery reply advertises. Deliberately carries NO secret: just enough
/// for the headset to list this streamer (machine name), reach it (baseUrl/port)
/// and bind/verify a trusted-device token to its stable identity (serverId).
/// </summary>
public sealed record DiscoveryInfo(string ServerId, string MachineName, string BaseUrl, int Port, int ApiVersion);

/// <summary>
/// Tiny UDP responder for LAN auto-discovery. It listens on a fixed UDP port and,
/// only when it receives our exact probe magic, replies to the sender with a small
/// JSON describing this streamer. Replying solely to the magic probe keeps it from
/// being a generic reflection/amplification surface, and the reply never includes
/// the token — trust is still established out-of-band via the pairing code.
/// </summary>
public sealed class DiscoveryResponder
{
    public const int DiscoveryPort = 47830;
    public const string ProbeMagic = "MULTIVID_DISCOVER_V1";
    public const string ReplyMagic = "MULTIVID_STREAMER_V1";

    private readonly Func<DiscoveryInfo> infoProvider;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    private UdpClient? udpClient;
    private CancellationTokenSource? cancellationTokenSource;

    public DiscoveryResponder(Func<DiscoveryInfo> infoProvider)
    {
        this.infoProvider = infoProvider;
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        UdpClient client = new(AddressFamily.InterNetwork);
        // Reuse address so a quick stop/start (or a co-existing local listener)
        // doesn't fail to bind.
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        udpClient = client;
        cancellationTokenSource = new CancellationTokenSource();
        IsRunning = true;
        _ = Task.Run(() => ListenAsync(client, cancellationTokenSource.Token));
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        cancellationTokenSource?.Cancel();
        udpClient?.Dispose();
        udpClient = null;
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
        IsRunning = false;
    }

    private async Task ListenAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Transient socket error: keep listening.
                continue;
            }

            string probe;
            try
            {
                probe = Encoding.UTF8.GetString(received.Buffer);
            }
            catch (Exception)
            {
                continue;
            }

            if (!probe.StartsWith(ProbeMagic, StringComparison.Ordinal))
            {
                continue; // not our probe: stay silent (no reflection surface)
            }

            try
            {
                DiscoveryInfo info = infoProvider();
                byte[] reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    magic = ReplyMagic,
                    serverId = info.ServerId,
                    machineName = info.MachineName,
                    baseUrl = info.BaseUrl,
                    port = info.Port,
                    apiVersion = info.ApiVersion
                }, jsonOptions));

                await client.SendAsync(reply, reply.Length, received.RemoteEndPoint);
            }
            catch (Exception)
            {
                // Sender gone or serialization issue: ignore and keep listening.
            }
        }
    }
}
