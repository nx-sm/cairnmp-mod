using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Networking.Authoritative;

namespace CairnMultiplayerMod.Internal.Networking;

internal sealed partial class NetworkManager
{
    private const double SteamReliablePresenceIntervalSeconds = 1.0;

    private ILobby _steamLobby;
    private bool _steamTransportActive;
    private ulong _steamLocalId;
    private ulong _steamHostId;
    private double _lastReliablePresenceSentAt;
    private double _lastSteamMemberRefreshAt;
    private double _lastSteamSendFailLogAt;
    private double _lastSteamPacketDropLogAt;
    private int _droppedSteamPackets;
    private readonly Dictionary<ulong, int> _steamPlayerIds = new();
    private readonly Dictionary<int, ulong> _steamIdsByPlayerId = new();
    private readonly Dictionary<ulong, string> _steamPlayerNames = new();
    private readonly Dictionary<ulong, double> _steamReliableForwardTimes = new();
    private readonly HashSet<ulong> _steamMembersPresent = new();
    private readonly PeerBudget _ropeCreationBudget = new(4, 1);
    private readonly List<int> _steamPlayersToRemove = new();
    // Piton state is owned by a transport-independent authority.
    private readonly HashSet<ulong> _steamLoggedFirstClientPlayerFrame = new();
    private readonly HashSet<ulong> _steamLoggedFirstClientClimbotFrame = new();

    public bool IsSteamTransportActive => _steamTransportActive;

    /// <summary>Starts the packet pipe of whichever lobby was entered — Steam or direct.
    /// The "Steam" names below predate direct connect: a peer id is a SteamID64 or a
    /// direct-connect id, and nothing here depends on which.</summary>
    public void StartTransport(ILobby lobby)
    {
        if (lobby == null || !lobby.IsInLobby) return;

        _steamLobby = lobby;
        _steamTransportActive = true;
        _steamLocalId = lobby.LocalPeerId;
        _steamHostId = lobby.HostPeerId;
        _lastReliablePresenceSentAt = 0;
        CurrentLobbyId = lobby.CurrentLobbyKey;
        CurrentRoomCode = lobby.CurrentRoomCode;
        ServerName = string.IsNullOrEmpty(lobby.CurrentLobbyName) ? "Lobby" : lobby.CurrentLobbyName;
        LocalPlayerId = StableSteamPlayerId(_steamLocalId);
        IsHandshakeComplete = lobby.IsHost;

        lobby.OpenTransport();

        RefreshLobbyMembers(lobby);
        InitializeExtensionApi();
        if (lobby.IsHost)
            OnHandshakeAck?.Invoke();
        ModLog.Debug($"[SteamP2P] Transport active. local={_steamLocalId} host={_steamHostId} playerId={LocalPlayerId}");
    }

    public void RefreshLobbyMembers(ILobby lobby)
    {
        if (!_steamTransportActive || lobby == null || !lobby.IsInLobby) return;

        _steamLobby = lobby;
        _steamHostId = lobby.HostPeerId;

        _steamMembersPresent.Clear();
        foreach (var member in lobby.Members)
        {
            if (member.SteamId == 0) continue;
            _steamMembersPresent.Add(member.SteamId);
            _steamPlayerNames[member.SteamId] = string.IsNullOrEmpty(member.Name) ? $"Player{member.SteamId}" : member.Name;
            var playerId = StableSteamPlayerId(member.SteamId);
            _steamPlayerIds[member.SteamId] = playerId;
            _steamIdsByPlayerId[playerId] = member.SteamId;

            if (member.SteamId == _steamLocalId) continue;

            lobby.AcceptPeer(member.SteamId);

            if (!_remotePlayers.ContainsKey(playerId))
            {
                var rp = new RemotePlayer
                {
                    Id = playerId,
                    Name = _steamPlayerNames[member.SteamId],
                    State = PlayerState.Connecting,
                };
                _remotePlayers[playerId] = rp;
                OnPlayerJoined?.Invoke(playerId, rp.Name);
            }
            else
            {
                _remotePlayers[playerId].Name = _steamPlayerNames[member.SteamId];
            }
        }

        _steamPlayersToRemove.Clear();
        foreach (var kv in _steamIdsByPlayerId)
        {
            if (kv.Value == _steamLocalId) continue;
            if (!_steamMembersPresent.Contains(kv.Value)) _steamPlayersToRemove.Add(kv.Key);
        }

        foreach (var playerId in _steamPlayersToRemove)
        {
            _ropeCreationBudget.Remove(playerId);
            NotifyExtensionPeerLeft(playerId);
            if (_steamIdsByPlayerId.TryGetValue(playerId, out var steamId))
            {
                lobby.ClosePeer(steamId);
                _steamPlayerIds.Remove(steamId);
                _steamPlayerNames.Remove(steamId);
                _manifestHandledPeers.Remove(steamId);
            }
            _steamIdsByPlayerId.Remove(playerId);
            if (lobby.IsHost) _pitonAuthority?.RemovePlayer(playerId, KeepPitonsOfLeavers());
            if (_remotePlayers.Remove(playerId))
                OnPlayerLeft?.Invoke(playerId);
        }
    }

    private void PumpSteamTransport()
    {
        if (!_steamTransportActive) return;

        if (_steamLobby == null || !_steamLobby.IsInLobby)
        {
            StopSteamTransport();
            return;
        }

        if (ShouldRefreshSteamMembers())
            RefreshLobbyMembers(_steamLobby);
        try
        {
            int guard = 0;
            // The pipe can close mid-loop (a packet that makes us leave), hence the re-check.
            while (guard++ < 256 && _steamLobby != null && _steamLobby.TryReceive(out var remoteId, out var payload))
            {
                // One bad packet must not end the pump: a truncated payload would otherwise
                // strand every packet queued behind it, frame after frame.
                try { ProcessSteamPacket(remoteId, payload); }
                catch (Exception ex) { LogDroppedSteamPacket(remoteId, payload, ex); }
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"[SteamP2P] Pump failed: {ex}");
        }
    }

    private void ProcessSteamPacket(ulong remoteId, byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;

        if (!IsSteamLobbyMember(remoteId))
        {
            ModLog.Warning($"[SteamP2P] Ignored packet from non-lobby peer {remoteId}");
            return;
        }

        _steamLobby.AcceptPeer(remoteId);

        var id = (PacketId)payload[0];
        if (_steamLobby != null && _steamLobby.IsHost)
        {
            ProcessSteamHostPacket(remoteId, id, payload);
            return;
        }

        // Clients accept authoritative Server* traffic only from the lobby owner.
        if (remoteId != _steamHostId)
        {
            ModLog.Warning($"[SteamP2P] Ignored non-host packet {id} from {remoteId}");
            return;
        }

        ProcessPacket(payload);
    }

    private void ProcessSteamHostPacket(ulong remoteSteamId, PacketId id, byte[] payload)
    {
        using var ms = new MemoryStream(payload, 1, payload.Length - 1, writable: false);
        using var r = new BinaryReader(ms);

        if (HandleExtensionHostPacket(remoteSteamId, id, r))
            return;
        if (!IsExtensionPeerAdmitted(remoteSteamId))
            return;

        switch (id)
        {
            case PacketId.ClientPlayerState:
                {
                    var pkt = new ClientPlayerState();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.YawDeg)) break;

                    var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                    ApplyRemotePlayerState(playerId, pkt);
                    var reliablePresence = ShouldForwardReliablePresence(remoteSteamId);
                    BroadcastSteamServerPacket(PacketId.ServerPlayerState, new ServerPlayerState
                    {
                        PlayerId = playerId,
                        X = pkt.X,
                        Y = pkt.Y,
                        Z = pkt.Z,
                        YawDeg = pkt.YawDeg,
                        SceneName = pkt.SceneName,
                        State = pkt.State,
                    }, exceptSteamId: remoteSteamId, reliable: reliablePresence);
                    break;
                }
            case PacketId.ClientBoneState:
                {
                    var pkt = new ClientBoneState();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidBoneState(pkt.BoneCount, pkt.Positions, pkt.Rotations)) break;

                    var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                    if (_remotePlayers.TryGetValue(playerId, out var rp))
                    {
                        rp.BoneCount = pkt.BoneCount;
                        rp.BonePositions = pkt.Positions;
                        rp.BoneRotations = pkt.Rotations;
                    }
                    BroadcastSteamServerPacket(PacketId.ServerBoneState, new ServerBoneState
                    {
                        PlayerId = playerId,
                        BoneCount = pkt.BoneCount,
                        Positions = pkt.Positions,
                        Rotations = pkt.Rotations,
                    }, exceptSteamId: remoteSteamId, reliable: true);
                    break;
                }
            case PacketId.ClientPlayerFrame:
                {
                    var pkt = new ClientPlayerFrame();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidNetFrame(pkt.Frame, requirePosition: true, out var rejectReason))
                    {
                        LogRejectedNetFrame($"steam-client-player-{remoteSteamId}", $"client player steam={remoteSteamId}", rejectReason);
                        break;
                    }

                    var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                    var playerName = _steamPlayerNames.TryGetValue(remoteSteamId, out var knownName)
                        ? knownName
                        : $"Player{playerId}";
                    if (_steamLoggedFirstClientPlayerFrame.Add(remoteSteamId))
                        ModLog.Debug($"[NetSync] First ClientPlayerFrame received from steam={remoteSteamId} id={playerId} name='{playerName}' positions={FrameVectorCount(pkt.Frame.Positions)} eulers={FrameVectorCount(pkt.Frame.Eulers)} flags=0x{pkt.Frame.Flags:X2}");
                    ApplyRemotePlayerFrame(playerId, playerName, pkt.Frame);
                    BroadcastSteamServerPacket(PacketId.ServerPlayerFrame, new ServerPlayerFrame
                    {
                        PlayerId = playerId,
                        PlayerName = playerName,
                        Frame = pkt.Frame,
                    }, exceptSteamId: remoteSteamId, reliable: false);
                    break;
                }
            case PacketId.ClientClimbotFrame:
                {
                    var pkt = new ClientClimbotFrame();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidNetFrame(pkt.Frame, requirePosition: true, out var rejectReason))
                    {
                        LogRejectedNetFrame($"steam-client-climbot-{remoteSteamId}", $"client climbot steam={remoteSteamId}", rejectReason);
                        break;
                    }

                    var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                    if (_steamLoggedFirstClientClimbotFrame.Add(remoteSteamId))
                        ModLog.Debug($"[NetSync] First ClientClimbotFrame received from steam={remoteSteamId} id={playerId} positions={FrameVectorCount(pkt.Frame.Positions)} eulers={FrameVectorCount(pkt.Frame.Eulers)} flags=0x{pkt.Frame.Flags:X2}");
                    ApplyRemoteClimbotFrame(playerId, pkt.Frame);
                    BroadcastSteamServerPacket(PacketId.ServerClimbotFrame, new ServerClimbotFrame
                    {
                        PlayerId = playerId,
                        Frame = pkt.Frame,
                    }, exceptSteamId: remoteSteamId, reliable: false);
                    break;
                }
            case PacketId.ClientRopeClip:
                {
                    var pkt = new ClientRopeClip();
                    pkt.Deserialize(r);
                    var fromId = EnsureSteamRemotePlayer(remoteSteamId);
                    CommitRopeClip(fromId, pkt.TargetPlayerId, pkt.Clip);
                    break;
                }
            case PacketId.ClientPitonPlaced:
            case PacketId.ClientPitonRemoved:
            case PacketId.ClientPitonRecovered:
                Pitons.OnClientPacket(EnsureSteamRemotePlayer(remoteSteamId), id, r);
                break;
            case PacketId.ClientFeatureStream:
                {
                    var pkt = new ClientFeatureStream();
                    pkt.Deserialize(r);
                    var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                    var outPkt = new ServerFeatureStream
                    {
                        FromPlayerId = playerId,
                        Channel = pkt.Channel,
                        Payload = pkt.Payload,
                    };
                    OnFeatureStream?.Invoke(playerId, pkt.Channel, pkt.Payload);
                    BroadcastSteamServerPacket(PacketId.ServerFeatureStream, outPkt,
                        exceptSteamId: remoteSteamId, reliable: pkt.Reliable);
                    break;
                }
            case PacketId.ClientDisconnect:
                {
                    var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                    _manifestHandledPeers.Add(remoteSteamId);
                    _pitonAuthority?.RemovePlayer(playerId, KeepPitonsOfLeavers());
                    NotifyExtensionPeerLeft(playerId);
                    if (_remotePlayers.Remove(playerId))
                        OnPlayerLeft?.Invoke(playerId);
                    BroadcastSteamServerPacket(PacketId.ServerPlayerLeft,
                        new ServerPlayerLeft { PlayerId = playerId }, exceptSteamId: remoteSteamId, reliable: true);
                    break;
                }
        }
    }

    private void SendSteamPlayerState(float x, float y, float z, float yaw, string sceneName, PlayerState state)
    {
        if (!IsHandshakeComplete) return;

        var reliablePresence = ShouldSendReliablePresence();
        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerPlayerState, new ServerPlayerState
            {
                PlayerId = LocalPlayerId,
                X = x,
                Y = y,
                Z = z,
                YawDeg = yaw,
                SceneName = sceneName ?? "",
                State = state,
            }, exceptSteamId: 0, reliable: reliablePresence);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPlayerState, new ClientPlayerState
        {
            X = x,
            Y = y,
            Z = z,
            YawDeg = yaw,
            SceneName = sceneName ?? "",
            State = state,
        }, reliable: reliablePresence);
    }

    private void SendSteamPlayerFrame(NetFrameData frame)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerPlayerFrame, new ServerPlayerFrame
            {
                PlayerId = LocalPlayerId,
                PlayerName = _steamPlayerNames.TryGetValue(_steamLocalId, out var name) ? name : "Player",
                Frame = frame,
            }, exceptSteamId: 0, reliable: false);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPlayerFrame, new ClientPlayerFrame { Frame = frame }, reliable: false);
    }

    private void SendSteamClimbotFrame(NetFrameData frame)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerClimbotFrame, new ServerClimbotFrame
            {
                PlayerId = LocalPlayerId,
                Frame = frame,
            }, exceptSteamId: 0, reliable: false);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientClimbotFrame, new ClientClimbotFrame { Frame = frame }, reliable: false);
    }

    private void SendSteamPitonPlaced(ClientPitonPlaced piton)
    {
        if (!IsHandshakeComplete) return;
        if (!PacketValidation.IsValidPitonPayload(
                piton.PosX, piton.PosY, piton.PosZ,
                piton.RotX, piton.RotY, piton.RotZ, piton.RotW,
                piton.Quality, piton.PitonHp, piton.ItemId)) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            Pitons.HandleHostPitonPlaced(LocalPlayerId, piton);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPitonPlaced, piton, reliable: true);
    }

    private void SendSteamPitonRemoved(uint pitonId)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            Pitons.HandleHostPitonRemoved(LocalPlayerId, pitonId);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPitonRemoved, new ClientPitonRemoved { PitonId = pitonId }, reliable: true);
    }

    private void SendSteamPitonRecovered(uint pitonId)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            Pitons.HandleHostPitonRecovered(LocalPlayerId, pitonId);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPitonRecovered, new ClientPitonRecovered { PitonId = pitonId }, reliable: true);
    }

    private void SendSteamFeatureStream(ushort channel, byte[] payload, bool reliable)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerFeatureStream, new ServerFeatureStream
            {
                FromPlayerId = LocalPlayerId,
                Channel = channel,
                Payload = payload,
            }, exceptSteamId: 0, reliable);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientFeatureStream,
            new ClientFeatureStream { Channel = channel, Reliable = reliable, Payload = payload }, reliable);
    }

    private void SendSteamRopeClip(int targetPlayerId, bool clip)
    {
        if (!IsHandshakeComplete || targetPlayerId == LocalPlayerId) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            CommitRopeClip(LocalPlayerId, targetPlayerId, clip);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientRopeClip,
            new ClientRopeClip { TargetPlayerId = targetPlayerId, Clip = clip }, reliable: true);
    }

    /// <summary>Host only: judges a rope request and tells everyone.</summary>
    private void CommitRopeClip(int requester, int target, bool clip)
    {
        if (!RopeRequests.IsAllowed(requester, target, clip, _ropeLinks(), IsAdmittedPlayer)) return;
        if (clip && LeadAndFollow()
            && !RopeOrder.WithinReach(PlayerPosition(requester), PlayerPosition(target)))
        {
            ModLog.Info($"[RopeCouple] Refused a rope between {requester} and {target}: too far apart");
            return;
        }
        if (clip && !_ropeCreationBudget.Take(requester)) return;

        OnRopeClip?.Invoke(requester, target, clip);
        BroadcastSteamServerPacket(PacketId.ServerRopeClip,
            new ServerRopeClip { FromPlayerId = requester, TargetPlayerId = target, Clip = clip },
            exceptSteamId: 0, reliable: true);
    }

    private void SendSteamPacketToHost(PacketId id, IPacket packet, bool reliable)
    {
        if (_steamHostId == 0 || _steamHostId == _steamLocalId) return;
        SendSteamPayload(_steamHostId, BuildPayload(id, packet), reliable);
    }

    private bool IsAdmittedPlayer(int playerId)
        => _enabledExtensionsByPlayer.ContainsKey(playerId)
           && _steamIdsByPlayerId.TryGetValue(playerId, out var steamId)
           && _steamMembersPresent.Contains(steamId);

    public bool SendTeleportToPlayer(int targetPlayerId, float x, float y, float z, float yaw)
    {
        if (!_steamTransportActive || _steamLobby == null || !_steamLobby.IsHost) return false;
        if (!_steamIdsByPlayerId.TryGetValue(targetPlayerId, out var steamId) || steamId == _steamLocalId)
            return false;

        var pkt = new ServerTeleport { X = x, Y = y, Z = z, Yaw = yaw };
        SendSteamPayload(steamId, BuildPayload(PacketId.ServerTeleport, pkt), reliable: true);
        ModLog.Debug($"[CairnMP] Sent ServerTeleport to player {targetPlayerId} (steam={steamId})");
        return true;
    }

    private void SendSteamSnapshotTo(ulong steamId)
    {
        if (steamId == 0 || steamId == _steamLocalId)
            return;

        var target = steamId;

        Pitons.SendSnapshotTo(EnsureSteamRemotePlayer(steamId));

        foreach (var (a, b) in _ropeLinks())
            SendSteamPayload(target, BuildPayload(PacketId.ServerRopeClip,
                new ServerRopeClip { FromPlayerId = a, TargetPlayerId = b, Clip = true }), reliable: true);
    }

    private void BroadcastSteamServerPacket(PacketId id, IPacket packet, ulong exceptSteamId, bool reliable)
    {
        var payload = BuildPayload(id, packet);
        foreach (var member in _steamLobby.Members)
        {
            if (member.SteamId == 0 || member.SteamId == _steamLocalId || member.SteamId == exceptSteamId) continue;
            if (_steamLobby.IsHost && !IsExtensionPeerAdmitted(member.SteamId)) continue;
            SendSteamPayload(member.SteamId, payload, reliable);
        }
    }

    private bool SendSteamPayload(ulong target, byte[] payload, bool reliable)
    {
        if (!_steamTransportActive || _steamLobby == null || payload == null || payload.Length == 0) return false;

        try
        {
            bool ok = _steamLobby.Send(target, payload, reliable);
            if (!ok && ShouldLogSteamSendFailure())
                ModLog.Warning($"[SteamP2P] Send failed target={target} size={payload.Length} reliable={reliable}");
            return ok;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[SteamP2P] Send exception target={target}: {ex.Message}");
            return false;
        }
    }

    private bool ShouldSendReliablePresence()
    {
        var now = NowSeconds();
        if (now - _lastReliablePresenceSentAt < SteamReliablePresenceIntervalSeconds)
            return false;

        _lastReliablePresenceSentAt = now;
        return true;
    }

    private bool ShouldRefreshSteamMembers()
    {
        var now = NowSeconds();
        if (now - _lastSteamMemberRefreshAt < 1.0)
            return false;

        _lastSteamMemberRefreshAt = now;
        return true;
    }

    private bool ShouldLogSteamSendFailure()
    {
        var now = NowSeconds();
        if (now - _lastSteamSendFailLogAt < 2.0)
            return false;

        _lastSteamSendFailLogAt = now;
        return true;
    }

    private void LogDroppedSteamPacket(ulong remoteSteamId, byte[] payload, Exception exception)
    {
        _droppedSteamPackets++;
        var now = NowSeconds();
        if (now - _lastSteamPacketDropLogAt < 2.0)
            return;

        _lastSteamPacketDropLogAt = now;
        var id = payload.Length > 0 ? ((PacketId)payload[0]).ToString() : "(empty)";
        ModLog.Warning($"[SteamP2P] Dropped packet {id} from {remoteSteamId} " +
                       $"(size={payload.Length}, dropped so far={_droppedSteamPackets}): " +
                       $"{exception.GetType().Name}: {exception.Message}");
    }

    private bool ShouldForwardReliablePresence(ulong sourceSteamId)
    {
        var now = NowSeconds();
        if (_steamReliableForwardTimes.TryGetValue(sourceSteamId, out var last) &&
            now - last < SteamReliablePresenceIntervalSeconds)
        {
            return false;
        }

        _steamReliableForwardTimes[sourceSteamId] = now;
        return true;
    }

    private static double NowSeconds() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    private int EnsureSteamRemotePlayer(ulong steamId)
    {
        var playerId = StableSteamPlayerId(steamId);
        if (!_steamPlayerIds.ContainsKey(steamId))
        {
            _steamPlayerIds[steamId] = playerId;
            _steamIdsByPlayerId[playerId] = steamId;
        }

        if (!_remotePlayers.ContainsKey(playerId))
        {
            var name = _steamPlayerNames.TryGetValue(steamId, out var knownName)
                ? knownName
                : $"Player{playerId}";
            _remotePlayers[playerId] = new RemotePlayer
            {
                Id = playerId,
                Name = name,
                State = PlayerState.Connecting,
            };
            OnPlayerJoined?.Invoke(playerId, name);
        }

        return playerId;
    }

    private void ApplyRemotePlayerState(int playerId, ClientPlayerState pkt)
    {
        if (!PacketValidation.IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.YawDeg)) return;
        if (!_remotePlayers.TryGetValue(playerId, out var p)) return;
        p.X = pkt.X;
        p.Y = pkt.Y;
        p.Z = pkt.Z;
        p.YawDeg = pkt.YawDeg;
        p.SceneName = pkt.SceneName;
        p.State = pkt.State;
        p.LastUpdateTime = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
    }

    private static byte[] BuildPayload(PacketId id, IPacket packet)
    {
        var frame = PacketCodec.Frame(id, packet);
        var payload = new byte[Math.Max(0, frame.Length - 2)];
        Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
        return payload;
    }

    private static int StableSteamPlayerId(ulong steamId)
    {
        var folded = (uint)(steamId ^ (steamId >> 32));
        var id = (int)(folded & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }

    private bool IsSteamLobbyMember(ulong steamId)
    {
        if (steamId == 0) return false;
        if (steamId == _steamLocalId) return true;
        if (_steamLobby == null || !_steamLobby.IsInLobby) return false;

        return _steamLobby.IsMember(steamId);
    }

    private void StopSteamTransport()
    {
        foreach (var steamId in _steamPlayerIds.Keys)
        {
            if (steamId == 0 || steamId == _steamLocalId) continue;
            _steamLobby?.ClosePeer(steamId);
        }
        ResetSteamTransportState();
    }

    private void ResetSteamTransportState()
    {
        _ropeCreationBudget.Clear();
        ResetExtensionApi();
        CloseLobbyTransport();
        _steamTransportActive = false;
        _steamLobby = null;
        _steamLocalId = 0;
        _steamHostId = 0;
        _steamPlayerIds.Clear();
        _steamIdsByPlayerId.Clear();
        _steamPlayerNames.Clear();
        _steamReliableForwardTimes.Clear();
        _steamMembersPresent.Clear();
        _steamPlayersToRemove.Clear();
        _pitonAuthority?.Reset();
        _pitonAuthority = null;
        _steamLoggedFirstClientPlayerFrame.Clear();
        _steamLoggedFirstClientClimbotFrame.Clear();
        _lastReliablePresenceSentAt = 0;
        _lastSteamMemberRefreshAt = 0;
        _lastSteamSendFailLogAt = 0;
    }

    private void CloseLobbyTransport()
    {
        try { _steamLobby?.CloseTransport(); }
        catch (Exception exception) { ModLog.SuppressedException("lobby.close-transport", exception); }
    }
}
