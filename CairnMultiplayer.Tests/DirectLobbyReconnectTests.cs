using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking;
using Xunit;

namespace CairnMultiplayer.Tests;

/// <summary>
/// Drops a live connection without a goodbye by routing it through a UDP relay that can
/// be unplugged — packets simply stop, as on a Wi-Fi drop — then plugs it back in.
/// </summary>
public sealed class DirectLobbyReconnectTests
{
    private static DirectLobby Lobby(string name, int port, int reconnectSeconds = 60)
        => new(new DirectLobbySettings
        {
            Port = () => port,
            PlayerName = () => name,
            ReconnectSeconds = () => reconnectSeconds,
            DisconnectTimeoutMs = 1500,
        });

    private static void PumpUntil(Func<bool> done, params DirectLobby[] lobbies)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "timed out waiting for the lobbies");
            foreach (var lobby in lobbies) lobby.Pump(0.01f);
            Thread.Sleep(10);
        }
    }

    private static (DirectLobby host, DirectLobby guest) ConnectThrough(UdpRelay relay, int hostPort, int reconnectSeconds = 60)
    {
        var host = Lobby("Host", hostPort, reconnectSeconds);
        var guest = Lobby("Guest", hostPort, reconnectSeconds);
        Assert.True(host.CreateLobby(new HostConfig { MaxPlayers = 4 }).Result);
        var join = guest.JoinByCode($"127.0.0.1:{relay.Port}");
        PumpUntil(() => join.IsCompleted && host.Members.Count == 2, host, guest);
        Assert.True(join.Result);
        return (host, guest);
    }

    [Fact]
    public void SilentDropIsRetriedAndTheSameSeatComesBack()
    {
        using var relay = new UdpRelay(27551, 27552);
        var (host, guest) = ConnectThrough(relay, 27552);
        using var _h = host;
        using var _g = guest;
        var guestId = guest.LocalPeerId;
        var hostNotices = new List<string>();
        var guestNotices = new List<string>();
        var hostReconnected = new List<ulong>();
        var guestReconnected = new List<ulong>();
        var guestLeft = false;
        host.OnConnectionNotice += hostNotices.Add;
        guest.OnConnectionNotice += guestNotices.Add;
        host.OnPeerReconnected += hostReconnected.Add;
        guest.OnPeerReconnected += guestReconnected.Add;
        guest.OnLobbyLeft += () => guestLeft = true;

        relay.Cut = true;
        PumpUntil(() => guest.IsReconnecting && host.Members.Any(m => m.Name.EndsWith("(reconnecting…)")), host, guest);
        Assert.True(guest.IsInLobby);
        Assert.Equal(2, host.Members.Count); // the seat is kept, not freed
        Assert.Contains(guestNotices, n => n.StartsWith("Connection to the host lost"));
        Assert.Contains(hostNotices, n => n.StartsWith("Guest lost connection"));

        relay.Cut = false;
        PumpUntil(() => !guest.IsReconnecting && guestReconnected.Count == 1 && hostReconnected.Count == 1, host, guest);

        Assert.False(guestLeft);
        Assert.Equal(guestId, guest.LocalPeerId);
        Assert.Equal(guestId, hostReconnected[0]);
        Assert.Equal(host.LocalPeerId, guestReconnected[0]);
        Assert.Equal(new[] { "Host", "Guest" }, host.Members.Select(m => m.Name));
        Assert.Contains("Reconnected to the host.", guestNotices);
        Assert.Contains("Guest reconnected.", hostNotices);

        // Traffic flows again under the same peer ids.
        Assert.True(guest.Send(host.LocalPeerId, new byte[] { 5 }, reliable: true));
        Assert.True(host.Send(guestId, new byte[] { 6 }, reliable: true));
        ulong fromAtHost = 0, fromAtGuest = 0;
        PumpUntil(() =>
        {
            if (host.TryReceive(out var f, out _)) fromAtHost = f;
            if (guest.TryReceive(out var g, out _)) fromAtGuest = g;
            return fromAtHost != 0 && fromAtGuest != 0;
        }, host, guest);
        Assert.Equal(guestId, fromAtHost);
        Assert.Equal(host.LocalPeerId, fromAtGuest);
    }

    [Fact]
    public void BothSidesGiveUpWhenTheWindowRunsOut()
    {
        using var relay = new UdpRelay(27553, 27554);
        var (host, guest) = ConnectThrough(relay, 27554, reconnectSeconds: 3);
        using var _h = host;
        using var _g = guest;
        string guestError = null;
        var guestLeft = false;
        var hostNotices = new List<string>();
        guest.OnLobbyError += e => guestError = e;
        guest.OnLobbyLeft += () => guestLeft = true;
        host.OnConnectionNotice += hostNotices.Add;

        relay.Cut = true;
        PumpUntil(() => guestLeft && host.Members.Count == 1, host, guest);

        Assert.False(guest.IsInLobby);
        Assert.Equal("Could not reconnect to the host within 3 seconds.", guestError);
        Assert.Contains("Guest did not reconnect.", hostNotices);
        Assert.True(host.IsInLobby);
    }

    [Fact]
    public void GuestFollowsAHostThatRestartedAtTheSameAddress()
    {
        using var relay = new UdpRelay(27555, 27556);
        var (host, guest) = ConnectThrough(relay, 27556);
        using var _g = guest;
        var oldHostId = host.LocalPeerId;
        var left = 0;
        var entered = 0;
        guest.OnLobbyLeft += () => left++;
        guest.OnLobbyEntered += () => entered++;

        // The host "crashes": its goodbye never makes it through.
        relay.Cut = true;
        host.Dispose();
        PumpUntil(() => guest.IsReconnecting, guest);

        using var restarted = Lobby("Host", 27556);
        Assert.True(restarted.CreateLobby(new HostConfig { MaxPlayers = 4 }).Result);
        relay.Cut = false;
        PumpUntil(() => entered == 1 && restarted.Members.Count == 2, restarted, guest);

        Assert.Equal(1, left);
        Assert.True(guest.IsInLobby);
        Assert.False(guest.IsReconnecting);
        Assert.NotEqual(oldHostId, guest.HostPeerId);
        Assert.Equal(restarted.LocalPeerId, guest.HostPeerId);
    }

    [Fact]
    public void ReturningGuestIsAdmittedEvenWhenTheLobbyIsFull()
    {
        using var relay = new UdpRelay(27557, 27558);
        var host = Lobby("Host", 27558);
        var guest = Lobby("Guest", 27558);
        using var _h = host;
        using var _g = guest;
        Assert.True(host.CreateLobby(new HostConfig { MaxPlayers = 2 }).Result);
        var join = guest.JoinByCode($"127.0.0.1:{relay.Port}");
        PumpUntil(() => join.IsCompleted && host.Members.Count == 2, host, guest);

        relay.Cut = true;
        PumpUntil(() => guest.IsReconnecting && host.Members.Any(m => m.Name.EndsWith("(reconnecting…)")), host, guest);

        // A stranger may not take the held seat...
        using var stranger = Lobby("Stranger", 27558);
        string refused = null;
        stranger.OnLobbyError += e => refused = e;
        var strangerJoin = stranger.JoinByCode("127.0.0.1:27558");
        PumpUntil(() => strangerJoin.IsCompleted, host, guest, stranger);
        Assert.False(strangerJoin.Result);
        Assert.Equal("Lobby is full.", refused);

        // ...but its owner may.
        relay.Cut = false;
        PumpUntil(() => !guest.IsReconnecting && host.Members.All(m => !m.Name.EndsWith("(reconnecting…)")), host, guest);
        Assert.Equal(2, host.Members.Count);
    }

    /// <summary>One guest ⇄ one host UDP forwarder with an off switch.</summary>
    private sealed class UdpRelay : IDisposable
    {
        private readonly UdpClient _front;
        private readonly UdpClient _back;
        private readonly IPEndPoint _host;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread[] _threads;
        private IPEndPoint _guest;

        public int Port { get; }
        public volatile bool Cut;

        public UdpRelay(int port, int hostPort)
        {
            Port = port;
            _front = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _back = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _host = new IPEndPoint(IPAddress.Loopback, hostPort);
            // A short receive timeout lets the threads notice Dispose: closing a socket
            // does not wake a blocked receive on every platform.
            _front.Client.ReceiveTimeout = 100;
            _back.Client.ReceiveTimeout = 100;
            _threads = new[]
            {
                new Thread(() => Forward(_front, toHost: true)) { IsBackground = true },
                new Thread(() => Forward(_back, toHost: false)) { IsBackground = true },
            };
            foreach (var thread in _threads) thread.Start();
        }

        private void Forward(UdpClient from, bool toHost)
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var data = from.Receive(ref remote);
                    if (Cut) continue;
                    if (toHost)
                    {
                        _guest = remote;
                        _back.Send(data, data.Length, _host);
                    }
                    else if (_guest != null)
                    {
                        _front.Send(data, data.Length, _guest);
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            foreach (var thread in _threads) thread.Join();
            _front.Dispose();
            _back.Dispose();
        }
    }
}
