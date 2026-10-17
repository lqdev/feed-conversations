using System.Net;
using System.Net.Sockets;

namespace FeedConversations.Api.Networking;

public interface IAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken token);
}
public sealed class SystemAddressResolver : IAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) => Dns.GetHostAddressesAsync(host, token);
}
public interface IRemoteConnector
{
    ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken token);
}
public sealed class SocketRemoteConnector : IRemoteConnector
{
    public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
