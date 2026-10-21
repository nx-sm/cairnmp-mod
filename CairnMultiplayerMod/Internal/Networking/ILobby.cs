using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking
{
    internal sealed class LobbyMember
    {
        /// <summary>The member's peer id: a SteamID64 in a Steam lobby, a random id with the
        /// <see cref="DirectLobby.PeerIdPrefix"/> high byte in a direct-connect lobby.</summary>
        public ulong SteamId { get; init; }
        public string Name { get; init; } = "";
        public bool IsHost { get; init; }
        public bool IsSelf { get; init; }
    }

    /// <summary>
    /// A lobby and the packet pipe between its members. Steam matchmaking and direct connect
    /// both implement it, so NetworkManager and the panels never touch a Steamworks type —
    /// a build of Cairn without Steamworks (GOG) must still load every one of them.
    ///
    /// Everything here runs on the Unity thread: implementations deliver events from Pump.
    /// </summary>
    internal interface ILobby : IDisposable
    {
        /// <summary>False once this lobby kind can no longer create or join anything.</summary>
        bool IsAvailable { get; }
        string UnavailableReason { get; }

        bool IsInLobby { get; }
        bool IsHost { get; }
        ulong LocalPeerId { get; }
        ulong HostPeerId { get; }
        string CurrentLobbyKey { get; }
        string CurrentRoomCode { get; }
        string CurrentLobbyName { get; }
        IReadOnlyList<LobbyMember> Members { get; }
        string LocalPersonaName { get; }
        int MaxMembers { get; }
        MultiplayerModeRules CurrentModeRules { get; }

        event Action OnLobbyEntered;
        event Action<string> OnLobbyError;
        event Action OnLobbyLeft;
        event Action OnMembersChanged;
        event Action<ServerStartGame> OnStartRequested;

        /// <summary>Something worth telling the player about the connection, such as a
        /// drop being retried.</summary>
        event Action<string> OnConnectionNotice;

        /// <summary>A connection lost without a goodbye came back. On the host the id is the
        /// returning guest's; on a guest it is the host's. The session must re-admit.</summary>
        event Action<ulong> OnPeerReconnected;

        void Pump(float dt);
        Task<bool> CreateLobby(HostConfig cfg);
        Task<bool> JoinByCode(string code);
        Task<bool> JoinById(ulong lobbyId64);
        Task<List<LobbyEntry>> RequestLobbyList();
        void Leave();
        bool BroadcastStart(ServerStartGame start);

        void OpenTransport();
        void CloseTransport();
        bool IsMember(ulong peerId);
        void AcceptPeer(ulong peerId);
        void ClosePeer(ulong peerId);
        bool Send(ulong peerId, byte[] payload, bool reliable);
        bool TryReceive(out ulong fromPeerId, out byte[] payload);
    }
}
