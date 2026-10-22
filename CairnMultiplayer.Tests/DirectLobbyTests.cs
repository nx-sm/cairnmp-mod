using System.Diagnostics;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking;
using Xunit;

namespace CairnMultiplayer.Tests;

/// <summary>
/// Drives a real host and guest over loopback UDP. Each test takes its own port so xUnit
/// can run them side by side.
/// </summary>
public sealed class DirectLobbyTests
{
    private static DirectLobby Lobby(string name, int port, string password = "")
        => new(new DirectLobbySettings
        {
            Port = () => port,
            Password = () => password,
            PlayerName = () => name,
        });

    private static void PumpUntil(Func<bool> done, params DirectLobby[] lobbies)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting for the lobbies");
            foreach (var lobby in lobbies) lobby.Pump(0.01f);
            Thread.Sleep(10);
        }
    }

    private static (DirectLobby host, DirectLobby guest) Connect(int port, string hostPassword = "", string guestPassword = "")
    {
        var host = Lobby("Host", port, hostPassword);
        var guest = Lobby("Guest", port, guestPassword);
        Assert.True(host.CreateLobby(new HostConfig { LobbyName = "Test climb", MaxPlayers = 4, Mode = MultiplayerMode.RopeTeam }).Result);
        var join = guest.JoinByCode($"127.0.0.1:{port}");
        PumpUntil(() => join.IsCompleted && host.Members.Count == 2, host, guest);
        Assert.True(join.Result);
        return (host, guest);
    }

    [Fact]
    public void GuestJoinsAndBothSeeTheSameRoster()
    {
        var (host, guest) = Connect(27531);
        using var _h = host;
        using var _g = guest;

        Assert.True(host.IsHost);
        Assert.False(guest.IsHost);
        Assert.True(guest.IsInLobby);
        Assert.Equal("Test climb", guest.CurrentLobbyName);
        Assert.Equal(host.LocalPeerId, guest.HostPeerId);
        Assert.Equal(4, guest.MaxMembers);
        Assert.Equal(new[] { "Host", "Guest" }, guest.Members.Select(m => m.Name));
        Assert.Equal(new[] { "Host", "Guest" }, host.Members.Select(m => m.Name));
        Assert.True(guest.Members.Single(m => m.Name == "Guest").IsSelf);
        Assert.True(guest.Members.Single(m => m.Name == "Host").IsHost);
        Assert.True(host.IsMember(guest.LocalPeerId));
        Assert.True(guest.IsMember(host.LocalPeerId));
    }

    [Fact]
    public void PayloadsTravelBothWaysReliableAndUnreliable()
    {
        var (host, guest) = Connect(27532);
        using var _h = host;
        using var _g = guest;

        var big = Enumerable.Range(0, 4000).Select(i => (byte)i).ToArray();
        Assert.True(guest.Send(host.LocalPeerId, new byte[] { 7, 1, 2 }, reliable: false));
        Assert.True(guest.Send(host.LocalPeerId, big, reliable: false)); // promoted to reliable
        Assert.True(host.Send(guest.LocalPeerId, new byte[] { 9, 9 }, reliable: true));

        var atHost = new List<(ulong, byte[])>();
        var atGuest = new List<(ulong, byte[])>();
        PumpUntil(() =>
        {
            while (host.TryReceive(out var f, out var p)) atHost.Add((f, p));
            while (guest.TryReceive(out var f, out var p)) atGuest.Add((f, p));
            return atHost.Count == 2 && atGuest.Count == 1;
        }, host, guest);

        Assert.All(atHost, m => Assert.Equal(guest.LocalPeerId, m.Item1));
        Assert.Contains(atHost, m => m.Item2.SequenceEqual(new byte[] { 7, 1, 2 }));
        Assert.Contains(atHost, m => m.Item2.SequenceEqual(big));
        Assert.Equal(host.LocalPeerId, atGuest[0].Item1);
        Assert.Equal(new byte[] { 9, 9 }, atGuest[0].Item2);

        // A guest has no route to anyone but the host.
        Assert.False(guest.Send(12345, new byte[] { 1 }, reliable: true));
    }

    [Fact]
    public void StartSignalReachesGuestsAndLatecomers()
    {
        var (host, guest) = Connect(27533);
        using var _h = host;
        using var _g = guest;
        var started = new List<ServerStartGame>();
        guest.OnStartRequested += started.Add;

        var start = new ServerStartGame { Difficulty = 2, SkipTutorials = true, Mode = (byte)MultiplayerMode.RopeTeam, ExtraConstraints = 5 };
        Assert.True(host.BroadcastStart(start));
        PumpUntil(() => started.Count == 1, host, guest);
        Assert.Equal(2, started[0].Difficulty);
        Assert.Equal(5, started[0].ExtraConstraints);
        Assert.True(started[0].SkipTutorials);

        using var late = Lobby("Late", 27533);
        var lateStarts = new List<ServerStartGame>();
        late.OnStartRequested += lateStarts.Add;
        var join = late.JoinByCode("127.0.0.1:27533");
        PumpUntil(() => join.IsCompleted && lateStarts.Count == 1 && guest.Members.Count == 3, host, guest, late);
        Assert.True(join.Result);
        Assert.Equal(2, lateStarts[0].Difficulty);
    }

    [Fact]
    public void WrongPasswordIsRefusedWithAReason()
    {
        using var host = Lobby("Host", 27534, "secret");
        using var guest = Lobby("Guest", 27534, "nope");
        var errors = new List<string>();
        guest.OnLobbyError += errors.Add;
        Assert.True(host.CreateLobby(new HostConfig()).Result);
        var join = guest.JoinByCode("127.0.0.1:27534");
        PumpUntil(() => join.IsCompleted, host, guest);

        Assert.False(join.Result);
        Assert.False(guest.IsInLobby);
        Assert.Contains("Wrong lobby password.", errors);
        Assert.Single(host.Members);
    }

    [Fact]
    public void GuestLeavingAndHostClosingAreBothNoticed()
    {
        var (host, guest) = Connect(27535);
        using var _h = host;
        using var _g = guest;

        guest.Leave();
        PumpUntil(() => host.Members.Count == 1, host, guest);

        var rejoin = guest.JoinByCode("127.0.0.1:27535");
        PumpUntil(() => rejoin.IsCompleted && host.Members.Count == 2, host, guest);
        var left = false;
        string error = null;
        guest.OnLobbyLeft += () => left = true;
        guest.OnLobbyError += e => error = e;
        host.Leave();
        PumpUntil(() => left, guest);
        Assert.False(guest.IsInLobby);
        Assert.Equal("The host closed the lobby.", error);
    }

    [Fact]
    public void UnreachableHostTimesOutInsteadOfHanging()
    {
        using var guest = Lobby("Guest", 27536);
        var join = guest.JoinByCode("127.0.0.1:27599");
        var clock = Stopwatch.StartNew();
        while (!join.IsCompleted)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
            guest.Pump(1f); // advance the 20 s join timeout quickly
            Thread.Sleep(10);
        }
        Assert.False(join.Result);
        Assert.False(guest.IsInLobby);
    }

    [Theory]
    [InlineData("100.64.1.2", "100.64.1.2", 27420)]
    [InlineData("100.64.1.2:7777", "100.64.1.2", 7777)]
    [InlineData(" desktop.tail1234.ts.net:9000 ", "desktop.tail1234.ts.net", 9000)]
    [InlineData("[fd7a::1]:8000", "fd7a::1", 8000)]
    [InlineData("fd7a:115c::1", "fd7a:115c::1", 27420)]
    public void ParsesAddresses(string code, string host, int port)
    {
        Assert.True(DirectLobby.LooksLikeAddress(code));
        Assert.True(DirectLobby.TryParseAddress(code, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("ABCD-EFGH")]
    [InlineData("ABCDEFGH")]
    public void SteamRoomCodesAreNotAddresses(string code) => Assert.False(DirectLobby.LooksLikeAddress(code));

    [Theory]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("bad host:1")]
    public void RejectsMalformedAddresses(string code) => Assert.False(DirectLobby.TryParseAddress(code, out _, out _));
}
