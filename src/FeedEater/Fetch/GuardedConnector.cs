using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace FeedEater.Fetch;

public sealed class UnsafeAddressException(string host) : Exception($"{host} resolves to an address a fetch may not connect to");

/// <summary>
/// The connect callback of the fetcher's handler. It resolves the name once, refuses the whole fetch if any answer is not public,
/// and connects to the address it validated, so the handler never does a second lookup that could answer differently.
/// </summary>
public sealed class GuardedConnector(
    Func<string, CancellationToken, Task<IPAddress[]>> resolve, Func<IPAddress, int, CancellationToken, Task<Stream>> connect)
{
    public static GuardedConnector Default { get; } = new(
        async (host, ct) => await Dns.GetHostAddressesAsync(host, ct),
        async (address, port, ct) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        });

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct) =>
        await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);

    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (port is not (80 or 443))
        {
            throw new UnsafeAddressException($"{host}:{port}");
        }

        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await resolve(host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IpGuard.IsPublic(a)))
        {
            throw new UnsafeAddressException(host);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            try
            {
                return await connect(address, port, ct);
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
