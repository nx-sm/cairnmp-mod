using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Internal.Networking
{
    /// <summary>
    /// Steam P2P packet pipe. It used to live in NetworkManager; it sits here so that
    /// NetworkManager holds no Steamworks type and still loads on a build without Steam.
    /// </summary>
    internal sealed partial class SteamLobbyManager
    {
        private const int SteamGameplayChannel = 0;

        private bool _transportOpen;
        private Callback<P2PSessionRequest_t> _p2pSessionRequestCallback;
        private Callback<P2PSessionConnectFail_t> _p2pSessionConnectFailCallback;

        /// <summary>Steam stays available while its deferred init can still succeed.</summary>
        public bool IsAvailable => IsSteamIntegrationAvailable && (_isInitialized || _initAttempts < InitMaxAttempts);

        public string UnavailableReason => IsSteamIntegrationAvailable
            ? "Steam could not start for Cairn. A Steam account that does not own Cairn is refused by Steam."
            : SteamUnavailableReason;

        public ulong LocalPeerId
        {
            get
            {
                try { return SteamUser.GetSteamID().m_SteamID; }
                catch (Exception exception)
                {
                    ModLog.SuppressedException("steam-p2p.read-local-id", exception);
                    return 0;
                }
            }
        }

        public ulong HostPeerId => HostSteamId;

        public string CurrentLobbyKey => IsInLobby ? CurrentLobbyId.m_SteamID.ToString() : "";

        public void OpenTransport()
        {
            _transportOpen = true;
            try { SteamNetworking.AllowP2PPacketRelay(true); }
            catch (Exception ex) { ModLog.Warning($"[SteamP2P] Allow relay failed: {ex.Message}"); }

            try
            {
                _p2pSessionRequestCallback ??= Callback<P2PSessionRequest_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<P2PSessionRequest_t>.DispatchDelegate>(
                        new Action<P2PSessionRequest_t>(OnP2PSessionRequest)));
                _p2pSessionConnectFailCallback ??= Callback<P2PSessionConnectFail_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<P2PSessionConnectFail_t>.DispatchDelegate>(
                        new Action<P2PSessionConnectFail_t>(OnP2PSessionConnectFail)));
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[SteamP2P] Callback registration failed: {ex.Message}");
            }
        }

        public void CloseTransport()
        {
            _transportOpen = false;
            try { _p2pSessionRequestCallback?.Dispose(); }
            catch (Exception exception) { ModLog.SuppressedException("steam-p2p.dispose-session-request-callback", exception); }
            try { _p2pSessionConnectFailCallback?.Dispose(); }
            catch (Exception exception) { ModLog.SuppressedException("steam-p2p.dispose-connect-fail-callback", exception); }
            _p2pSessionRequestCallback = null;
            _p2pSessionConnectFailCallback = null;
        }

        public bool IsMember(ulong peerId)
        {
            if (peerId == 0 || !IsInLobby) return false;

            // Reads Steam directly to cover the short delay before OnMembersChanged.
            try
            {
                var count = SteamMatchmaking.GetNumLobbyMembers(CurrentLobbyId);
                for (int i = 0; i < count; i++)
                {
                    var member = SteamMatchmaking.GetLobbyMemberByIndex(CurrentLobbyId, i);
                    if (member.m_SteamID == peerId) return true;
                }
            }
            catch (Exception exception) { ModLog.SuppressedException("steam-p2p.close-lobby-members", exception); }

            foreach (var member in _members)
                if (member.SteamId == peerId) return true;

            return false;
        }

        public void AcceptPeer(ulong peerId)
        {
            try { SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(peerId)); }
            catch (Exception exception) { ModLog.SuppressedException("steam-p2p.accept-peer", exception); }
        }

        public void ClosePeer(ulong peerId)
        {
            try { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peerId)); }
            catch (Exception exception) { ModLog.SuppressedException("steam-p2p.close-session", exception); }
        }

        public bool Send(ulong peerId, byte[] payload, bool reliable)
        {
            var data = new Il2CppStructArray<byte>(payload.Length);
            for (int i = 0; i < payload.Length; i++)
                data[i] = payload[i];

            var forceReliable = !reliable && payload.Length > 1100;
            var sendType = (reliable || forceReliable)
                ? EP2PSend.k_EP2PSendReliable
                : EP2PSend.k_EP2PSendUnreliableNoDelay;
            return SteamNetworking.SendP2PPacket(new CSteamID(peerId), data, (uint)payload.Length, sendType, SteamGameplayChannel);
        }

        public bool TryReceive(out ulong fromPeerId, out byte[] payload)
        {
            fromPeerId = 0;
            payload = null;
            if (!SteamNetworking.IsP2PPacketAvailable(out var packetSize, SteamGameplayChannel) || packetSize == 0)
                return false;

            var buffer = new Il2CppStructArray<byte>((long)packetSize);
            if (!SteamNetworking.ReadP2PPacket(buffer, packetSize, out var bytesRead, out var remoteId, SteamGameplayChannel))
                return false;

            payload = new byte[bytesRead];
            for (int i = 0; i < bytesRead; i++)
                payload[i] = buffer[i];
            fromPeerId = remoteId.m_SteamID;
            return true;
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t evt)
        {
            var remoteId = evt.m_steamIDRemote;
            if (!_transportOpen || remoteId.m_SteamID == 0) return;
            if (!IsMember(remoteId.m_SteamID))
            {
                ModLog.Warning($"[SteamP2P] Rejected session request from non-lobby peer {remoteId.m_SteamID}");
                return;
            }

            try
            {
                SteamNetworking.AcceptP2PSessionWithUser(remoteId);
                ModLog.Debug($"[SteamP2P] Accepted session request from {remoteId.m_SteamID}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[SteamP2P] Accept session failed for {remoteId.m_SteamID}: {ex.Message}");
            }
        }

        private void OnP2PSessionConnectFail(P2PSessionConnectFail_t evt)
        {
            ModLog.Warning($"[SteamP2P] Session failed remote={evt.m_steamIDRemote.m_SteamID} error={evt.m_eP2PSessionError}");
        }
    }
}
