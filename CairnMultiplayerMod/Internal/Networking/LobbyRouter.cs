using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;

namespace CairnMultiplayerMod.Internal.Networking
{
    /// <summary>
    /// The one lobby the rest of the mod sees. It sends each request to Steam matchmaking
    /// or to direct connect, so a Steam copy and a GOG copy can play together:
    ///
    /// - hosting uses direct connect when the host picks "Direct IP", or when Steam cannot
    ///   run at all (no Steamworks in the build, Steam closed, or Cairn not owned on Steam);
    /// - a join code holding '.' or ':' is an address and goes to direct connect;
    /// - everything else (room codes, the browser, Steam invites) stays on Steam.
    /// </summary>
    internal sealed class LobbyRouter : ILobby
    {
        private readonly ILobby _steam;
        private readonly DirectLobby _direct;
        private readonly string _steamLoadFailure = "";
        private ILobby _last;

        public LobbyRouter(DirectLobby direct)
        {
            _direct = direct ?? throw new ArgumentNullException(nameof(direct));
            try
            {
                _steam = CreateSteamLobby();
            }
            catch (Exception ex)
            {
                // A GOG build ships no Steamworks.NET, so the Steam lobby type cannot even
                // load. That is expected there, not a failure.
                _steamLoadFailure = $"{ex.GetType().Name}: {ex.Message}";
                ModLog.Warning($"[Lobby] Steam matchmaking unavailable ({_steamLoadFailure}); direct connect only.");
            }

            Wire(_direct);
            if (_steam != null) Wire(_steam);
            _last = _steam ?? (ILobby)_direct;
        }

        /// <summary>Kept out of the constructor body: merely compiling a method that names
        /// SteamLobbyManager fails when the Steamworks interop assembly does not exist.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ILobby CreateSteamLobby() => new SteamLobbyManager();

        /// <summary>Shown once at startup when Steam is out of the picture.</summary>
        public string StartupNotice => _steam == null
            ? "Steam unavailable: host with Direct IP, or join with the host's IP:port."
            : !_steam.IsAvailable ? $"{_steam.UnavailableReason} Direct IP still works." : "";

        private bool SteamUsable => _steam != null && _steam.IsAvailable;

        private ILobby Current
        {
            get
            {
                if (_direct.IsInLobby) return _direct;
                if (_steam?.IsInLobby == true) return _steam;
                return _last;
            }
        }

        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public bool IsInLobby => Current.IsInLobby;
        public bool IsHost => Current.IsHost;
        public ulong LocalPeerId => Current.LocalPeerId;
        public ulong HostPeerId => Current.HostPeerId;
        public string CurrentLobbyKey => Current.CurrentLobbyKey;
        public string CurrentRoomCode => Current.CurrentRoomCode;
        public string CurrentLobbyName => Current.CurrentLobbyName;
        public IReadOnlyList<LobbyMember> Members => Current.Members;
        public int MaxMembers => Current.MaxMembers;
        public MultiplayerModeRules CurrentModeRules => Current.CurrentModeRules;

        public string LocalPersonaName
        {
            get
            {
                if (SteamUsable)
                {
                    var steamName = _steam.LocalPersonaName;
                    if (!string.IsNullOrWhiteSpace(steamName)) return steamName;
                }
                return _direct.LocalPersonaName;
            }
        }

        public event Action OnLobbyEntered;
        public event Action<string> OnLobbyError;
        public event Action OnLobbyLeft;
        public event Action OnMembersChanged;
        public event Action<ServerStartGame> OnStartRequested;
        public event Action<string> OnConnectionNotice;
        public event Action<ulong> OnPeerReconnected;

        private void Wire(ILobby lobby)
        {
            lobby.OnLobbyEntered += () =>
            {
                // A Steam invite accepted from the overlay can land while we sit in a
                // direct lobby (or the reverse): never be in both.
                var other = ReferenceEquals(lobby, _direct) ? _steam : _direct;
                if (other?.IsInLobby == true) other.Leave();
                _last = lobby;
                OnLobbyEntered?.Invoke();
            };
            lobby.OnLobbyError += error => OnLobbyError?.Invoke(error);
            lobby.OnLobbyLeft += () => OnLobbyLeft?.Invoke();
            lobby.OnMembersChanged += () => { if (ReferenceEquals(lobby, Current)) OnMembersChanged?.Invoke(); };
            lobby.OnStartRequested += start => { if (ReferenceEquals(lobby, Current)) OnStartRequested?.Invoke(start); };
            lobby.OnConnectionNotice += notice => { if (ReferenceEquals(lobby, Current)) OnConnectionNotice?.Invoke(notice); };
            lobby.OnPeerReconnected += peerId => { if (ReferenceEquals(lobby, Current)) OnPeerReconnected?.Invoke(peerId); };
        }

        public void Pump(float dt)
        {
            _steam?.Pump(dt);
            _direct.Pump(dt);
        }

        public Task<bool> CreateLobby(HostConfig cfg)
        {
            var useDirect = cfg?.Visibility == LobbyVisibility.Direct || !SteamUsable;
            return Use(useDirect ? _direct : _steam).CreateLobby(cfg);
        }

        public Task<bool> JoinByCode(string code)
        {
            if (DirectLobby.LooksLikeAddress(code) || _steam == null)
                return Use(_direct).JoinByCode(code);
            return Use(_steam).JoinByCode(code);
        }

        public Task<bool> JoinById(ulong lobbyId64)
        {
            if (_steam == null)
            {
                OnLobbyError?.Invoke("Steam is unavailable. Join with the host's IP:port instead.");
                return Task.FromResult(false);
            }
            return Use(_steam).JoinById(lobbyId64);
        }

        public Task<List<LobbyEntry>> RequestLobbyList()
        {
            if (!SteamUsable)
            {
                OnLobbyError?.Invoke("The lobby browser needs Steam. Join with the host's IP:port instead.");
                return Task.FromResult(new List<LobbyEntry>());
            }
            return _steam.RequestLobbyList();
        }

        /// <summary>Leaves whatever lobby the request is about to replace.</summary>
        private ILobby Use(ILobby lobby)
        {
            var other = ReferenceEquals(lobby, _direct) ? _steam : _direct;
            if (other?.IsInLobby == true) other.Leave();
            _last = lobby;
            return lobby;
        }

        public void Leave() => Current.Leave();
        public bool BroadcastStart(ServerStartGame start) => Current.BroadcastStart(start);
        public void OpenTransport() => Current.OpenTransport();
        public void CloseTransport() => Current.CloseTransport();
        public bool IsMember(ulong peerId) => Current.IsMember(peerId);
        public void AcceptPeer(ulong peerId) => Current.AcceptPeer(peerId);
        public void ClosePeer(ulong peerId) => Current.ClosePeer(peerId);
        public bool Send(ulong peerId, byte[] payload, bool reliable) => Current.Send(peerId, payload, reliable);
        public bool TryReceive(out ulong fromPeerId, out byte[] payload) => Current.TryReceive(out fromPeerId, out payload);

        public void Dispose()
        {
            try { _steam?.Dispose(); }
            catch (Exception exception) { ModLog.SuppressedException("lobby.dispose-steam", exception); }
            _direct.Dispose();
        }
    }
}
