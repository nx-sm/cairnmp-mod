using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;

namespace CairnMultiplayerMod.Internal.Networking;

internal sealed partial class NetworkManager
{
    private void HandleHandshakeAck(BinaryReader reader)
    {
        var packet = new ServerHandshakeAck();
        packet.Deserialize(reader);
        LocalPlayerId = packet.AssignedPlayerId;
        ServerName = packet.ServerName;
        IsHandshakeComplete = true;
        ModLog.Info($"[CairnMP] Handshake OK. id={packet.AssignedPlayerId} server='{packet.ServerName}'");
        OnHandshakeAck?.Invoke();
    }

    private void HandleHandshakeReject(BinaryReader reader)
    {
        var packet = new ServerHandshakeReject();
        packet.Deserialize(reader);
        LastError = packet.Reason;
        ModLog.Error($"[CairnMP] Handshake rejected: {packet.Reason}");
        OnHandshakeRejected?.Invoke(packet.Reason);
    }

    private void HandlePlayerJoined(BinaryReader reader)
    {
        var packet = new ServerPlayerJoined();
        packet.Deserialize(reader);
        _remotePlayers[packet.PlayerId] = new RemotePlayer
        {
            Id = packet.PlayerId,
            Name = packet.PlayerName,
        };
        ModLog.Info($"[CairnMP] Player joined: [{packet.PlayerId}] {packet.PlayerName}");
        OnPlayerJoined?.Invoke(packet.PlayerId, packet.PlayerName);
    }

    private void HandlePlayerLeft(BinaryReader reader)
    {
        var packet = new ServerPlayerLeft();
        packet.Deserialize(reader);
        if (_remotePlayers.Remove(packet.PlayerId))
            ModLog.Info($"[CairnMP] Player left: {packet.PlayerId}");
        OnPlayerLeft?.Invoke(packet.PlayerId);
    }

    private void HandleStartGame(BinaryReader reader)
    {
        var packet = new ServerStartGame();
        packet.Deserialize(reader);
        ModLog.Debug($"[CairnMP] StartGame: difficulty={(GameDifficulty)packet.Difficulty} skipTut={packet.SkipTutorials} skipPra={packet.SkipPractice} assist={packet.AssistEnabled}");
        OnStartGameReceived?.Invoke(packet);
    }

    private void HandleHostGameSettings(BinaryReader reader)
    {
        var packet = new ServerHostGameSettings();
        packet.Deserialize(reader);
        ModLog.Info($"[CairnMP] Host settings received: difficulty={packet.Difficulty} constraints={packet.Constraints} assist={packet.AssistEnabled}");
        OnHostGameSettings?.Invoke(packet);
    }

    /// <summary>Host only: tells every joined player what the host chose for the new game.</summary>
    public void BroadcastHostGameSettings(ServerHostGameSettings settings)
    {
        if (!IsHandshakeComplete || _steamLobby == null || !_steamLobby.IsHost) return;
        // Kept for whoever joins after the launch: they start the same game.
        _lastHostSettings = settings;
        BroadcastSteamServerPacket(PacketId.ServerHostGameSettings, settings, exceptSteamId: 0, reliable: true);
        ModLog.Info($"[CairnMP] Sent the host's settings to everyone: difficulty={settings.Difficulty} constraints={settings.Constraints}");
    }

    public event System.Action<ServerHostGameSettings> OnHostGameSettings;

    private ServerHostGameSettings? _lastHostSettings;
}
