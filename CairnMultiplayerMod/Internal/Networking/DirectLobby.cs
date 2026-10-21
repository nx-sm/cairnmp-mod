using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CairnMultiplayerMod.Internal.Networking
{
    internal sealed class DirectLobbySettings
    {
        public Func<int> Port { get; init; } = () => DirectLobby.DefaultPort;
        public Func<string> Password { get; init; } = () => "";
        public Func<string> PlayerName { get; init; } = () => "Player";

        /// <summary>How long a connection lost without a goodbye is retried (guest) or its
        /// seat kept (host). Zero turns reconnection off.</summary>
        public Func<int> ReconnectSeconds { get; init; } = () => DirectLobby.DefaultReconnectSeconds;

        /// <summary>Silence after which a connection counts as lost.</summary>
        public int DisconnectTimeoutMs { get; init; } = 15000;
    }

    /// <summary>
    /// A lobby over plain UDP (LiteNetLib), for players who cannot use Steam matchmaking —
    /// a GOG copy of Cairn has no Steam licence, so Steam refuses it a lobby. The host
    /// listens on a port; guests join with "address:port" in the join-code field.
    ///
    /// The topology is the one NetworkManager already assumes: guests talk to the host
    /// only, and the host relays. Channel 0 carries NetworkManager's payloads untouched;
    /// channel 1 carries this lobby's own control messages (roster, start signal), which
    /// stand in for Steam lobby data.
    ///
    /// A connection that drops without a goodbye (time-out, network loss, a crash) is not
    /// the end of the session: the guest keeps dialling the host and the host keeps the
    /// guest's seat, both for <see cref="DirectLobbySettings.ReconnectSeconds"/>. A guest
    /// comes back under the same peer id, so it gets its own seat and player id back.
    ///
    /// LiteNetLib queues its socket events until <see cref="Pump"/> calls PollEvents, so
    /// every callback here runs on the Unity thread, like Steam's.
    /// </summary>
    internal sealed class DirectLobby : ILobby
    {
        public const int DefaultPort = 27420;
        public const int DefaultReconnectSeconds = 300;
        /// <summary>High byte of every direct peer id, so one can never equal a SteamID64
        /// (those start 0x01).</summary>
        public const byte PeerIdPrefix = 0xD1;

        private const string AppKey = "CairnMP-direct";
        private const byte GameChannel = 0;
        private const byte ControlChannel = 1;
        private const int MaxNameLength = 64;
        private const float JoinTimeoutSeconds = 20f;
        private const double RedialDelaySeconds = 2.0;
        private const string ReconnectingSuffix = " (reconnecting…)";

        private enum Control : byte
        {
            Welcome = 1,
            Roster = 2,
            Start = 3,
        }

        private readonly DirectLobbySettings _settings;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _net;
        private readonly List<LobbyMember> _members = new();
        private readonly Queue<(ulong from, byte[] payload)> _inbox = new();

        // Host: guests in join order. A seat outlives its connection while the guest has
        // a reconnection deadline.
        private readonly List<ulong> _seats = new();
        private readonly Dictionary<ulong, string> _seatNames = new();
        private readonly Dictionary<ulong, double> _seatDeadlines = new();
        private readonly Dictionary<ulong, NetPeer> _peersById = new();
        private readonly Dictionary<NetPeer, ulong> _idsByPeer = new();

        // Guest: where the host is, to dial it again.
        private NetPeer _hostPeer;
        private string _hostAddress = "";
        private int _hostPort;
        private bool _reconnecting;
        private double _reconnectDeadline;
        private double _nextRedialAt;

        private TaskCompletionSource<bool> _joinTcs;
        private float _joinElapsed;
        private string _lastStartNonce = "";
        private string _hostStartNonce = "";
        private ServerStartGame _hostStart;
        private bool _hasHostStart;
        private MultiplayerMode _mode = MultiplayerMode.RopeTeam;
        private bool _disposed;

        public DirectLobby(DirectLobbySettings settings)
        {
            _settings = settings ?? new DirectLobbySettings();
            _net = new NetManager(_listener)
            {
                ChannelsCount = 2,
                DisconnectTimeout = _settings.DisconnectTimeoutMs,
                AutoRecycle = true,
                UnconnectedMessagesEnabled = false,
            };
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += (endPoint, error) =>
                ModLog.Warning($"[Direct] Socket error {error} ({endPoint}).");
            LocalPeerId = NewPeerId();
        }

        public bool IsAvailable => !_disposed;
        public string UnavailableReason => _disposed ? "Direct connect is shutting down." : "";

        public bool IsInLobby { get; private set; }
        public bool IsHost { get; private set; }
        public ulong LocalPeerId { get; private set; }
        public ulong HostPeerId { get; private set; }
        public string CurrentLobbyKey => IsInLobby ? $"direct:{CurrentRoomCode}" : "";
        public string CurrentRoomCode { get; private set; } = "";
        public string CurrentLobbyName { get; private set; } = "";
        public IReadOnlyList<LobbyMember> Members => _members;
        public string LocalPersonaName => Clip(_settings.PlayerName?.Invoke(), "Player");
        public int MaxMembers { get; private set; }
        public MultiplayerModeRules CurrentModeRules
            => IsInLobby ? MultiplayerModes.RulesFor(_mode) : MultiplayerModes.RopeTeam;

        /// <summary>True while a guest is dialling a host it lost.</summary>
        public bool IsReconnecting => _reconnecting;

        public event Action OnLobbyEntered;
        public event Action<string> OnLobbyError;
        public event Action OnLobbyLeft;
        public event Action OnMembersChanged;
        public event Action<ServerStartGame> OnStartRequested;
        public event Action<string> OnConnectionNotice;
        public event Action<ulong> OnPeerReconnected;

        private double ReconnectWindow => Math.Max(0, _settings.ReconnectSeconds?.Invoke() ?? DefaultReconnectSeconds);

        private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public void Pump(float dt)
        {
            if (_disposed || !_net.IsRunning) return;
            _net.PollEvents();

            if (IsHost)
            {
                ExpireSeats();
                return;
            }

            if (_reconnecting)
            {
                PumpReconnect();
                return;
            }

            if (_joinTcs == null) return;
            _joinElapsed += Math.Max(0f, dt);
            if (_joinElapsed >= JoinTimeoutSeconds)
                FailJoin("The host did not answer. Check the address, that the host's port is open " +
                         "(or that you are both on the same Tailscale/ZeroTier network), and try again.");
        }

        public Task<bool> CreateLobby(HostConfig cfg)
        {
            if (_disposed) return Task.FromResult(false);
            if (_joinTcs != null)
                return Busy();
            if (IsInLobby) Leave();

            cfg ??= new HostConfig();
            var port = _settings.Port?.Invoke() ?? DefaultPort;
            if (port <= 0 || port > 65535) port = DefaultPort;
            if (!_net.Start(port))
            {
                OnLobbyError?.Invoke($"Could not open UDP port {port}. Another program (or a second Cairn) " +
                                     "may be using it; change [DirectConnect] Port in MelonPreferences.cfg.");
                return Task.FromResult(false);
            }

            LocalPeerId = NewPeerId();
            IsHost = true;
            IsInLobby = true;
            HostPeerId = LocalPeerId;
            MaxMembers = Math.Max(2, Math.Min(cfg.MaxPlayers, 16));
            _mode = cfg.Mode;
            var hostName = Clip(cfg.PlayerName, LocalPersonaName);
            CurrentLobbyName = Clip(cfg.LobbyName, $"{hostName}'s lobby");
            CurrentRoomCode = FormatRoomCode(BestLocalAddress(), port);
            _hasHostStart = false;
            _hostStartNonce = "";
            RebuildHostMembers();

            ModLog.Info($"[Direct] Hosting '{CurrentLobbyName}' on UDP {port}; share '{CurrentRoomCode}'.");
            OnLobbyEntered?.Invoke();
            OnMembersChanged?.Invoke();
            return Task.FromResult(true);
        }

        public Task<bool> JoinByCode(string code)
        {
            if (_disposed) return Task.FromResult(false);
            if (_joinTcs != null) return _joinTcs.Task;
            if (!TryParseAddress(code, out var host, out var port))
            {
                OnLobbyError?.Invoke($"'{code}' is not an address. Use the host's IP, for example 100.64.1.2:{DefaultPort}.");
                return Task.FromResult(false);
            }
            if (IsInLobby) Leave();

            if (!_net.IsRunning && !_net.Start())
            {
                OnLobbyError?.Invoke("Could not open a UDP socket.");
                return Task.FromResult(false);
            }

            LocalPeerId = NewPeerId();
            _hostAddress = host;
            _hostPort = port;
            _joinTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _joinElapsed = 0f;
            var task = _joinTcs.Task;
            if (!Dial(out var error))
                FailJoin(error);
            else
                ModLog.Info($"[Direct] Connecting to {host}:{port}...");
            return task;
        }

        /// <summary>Sends the hello. A redial reuses <see cref="LocalPeerId"/>, which is how
        /// the host recognises a returning guest.</summary>
        private bool Dial(out string error)
        {
            error = "";
            var hello = new NetDataWriter();
            hello.Put(AppKey);
            hello.Put(Protocol.Version);
            hello.Put(Protocol.GameVersion);
            hello.Put(LocalPeerId);
            hello.Put(LocalPersonaName);
            hello.Put(_settings.Password?.Invoke() ?? "");
            try
            {
                _hostPeer = _net.Connect(_hostAddress, _hostPort, hello);
                if (_hostPeer != null) return true;
                error = $"Could not reach {_hostAddress}:{_hostPort}.";
            }
            catch (Exception ex)
            {
                _hostPeer = null;
                error = $"Could not reach {_hostAddress}:{_hostPort}: {ex.Message}";
            }
            return false;
        }

        public Task<bool> JoinById(ulong lobbyId64)
        {
            OnLobbyError?.Invoke("Direct-connect lobbies are joined by address, not from the browser.");
            return Task.FromResult(false);
        }

        public Task<List<LobbyEntry>> RequestLobbyList() => Task.FromResult(new List<LobbyEntry>());

        public void Leave()
        {
            var wasInLobby = IsInLobby;
            var join = _joinTcs;
            _joinTcs = null;

            if (IsHost && _net.IsRunning)
            {
                var bye = new NetDataWriter();
                bye.Put("The host closed the lobby.");
                _net.DisconnectAll(bye.Data, 0, bye.Length);
            }
            // Stopping sends every peer a disconnect: that is the goodbye that tells the
            // other side not to wait for us.
            if (_net.IsRunning) _net.Stop();

            ResetState();
            join?.TrySetResult(false);
            if (wasInLobby) OnLobbyLeft?.Invoke();
        }

        public bool BroadcastStart(ServerStartGame start)
        {
            if (!IsInLobby)
            {
                OnLobbyError?.Invoke("Not connected to a lobby.");
                return false;
            }
            if (!IsHost)
            {
                OnLobbyError?.Invoke("Only the host can start the lobby.");
                return false;
            }

            _hostStartNonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            _hostStart = start;
            _hasHostStart = true;
            var w = new NetDataWriter();
            w.Put((byte)Control.Start);
            WriteStart(w, _hostStartNonce, start);
            foreach (var peer in _peersById.Values)
                if (peer.ConnectionState == ConnectionState.Connected)
                    peer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
            ModLog.Info($"[Direct] Start broadcast nonce={_hostStartNonce} mode={(MultiplayerMode)start.Mode}.");
            RaiseStart(_hostStartNonce, start);
            return true;
        }

        public void OpenTransport() { }

        public void CloseTransport() => _inbox.Clear();

        public bool IsMember(ulong peerId)
        {
            if (!IsInLobby || peerId == 0) return false;
            if (peerId == LocalPeerId) return true;
            foreach (var member in _members)
                if (member.SteamId == peerId) return true;
            return false;
        }

        public void AcceptPeer(ulong peerId) { }

        /// <summary>Called once a member is gone for good: the host frees the seat.</summary>
        public void ClosePeer(ulong peerId)
        {
            if (!IsHost) return;
            if (_peersById.TryGetValue(peerId, out var peer))
                peer.Disconnect();
        }

        public bool Send(ulong peerId, byte[] payload, bool reliable)
        {
            if (!IsInLobby || payload == null || payload.Length == 0) return false;

            NetPeer peer;
            if (IsHost)
            {
                if (!_peersById.TryGetValue(peerId, out peer)) return false;
            }
            else
            {
                // Guests only ever reach the host; everything else is relayed by it.
                if (peerId != HostPeerId || _hostPeer == null) return false;
                peer = _hostPeer;
            }

            if (peer.ConnectionState != ConnectionState.Connected) return false;
            var method = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
            // Same rule as the Steam path: an unreliable frame too big for one datagram goes
            // reliable instead of being refused.
            if (!reliable && payload.Length > peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
                method = DeliveryMethod.ReliableOrdered;
            peer.Send(payload, GameChannel, method);
            return true;
        }

        public bool TryReceive(out ulong fromPeerId, out byte[] payload)
        {
            if (_inbox.Count == 0)
            {
                fromPeerId = 0;
                payload = null;
                return false;
            }
            (fromPeerId, payload) = _inbox.Dequeue();
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            Leave();
            _disposed = true;
        }

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (!IsHost || !IsInLobby)
            {
                request.Reject();
                return;
            }

            string appKey, modVersion, name, password;
            int protocol;
            ulong peerId;
            try
            {
                appKey = request.Data.GetString(64);
                protocol = request.Data.GetInt();
                modVersion = request.Data.GetString(64);
                peerId = request.Data.GetULong();
                name = request.Data.GetString(MaxNameLength * 2);
                password = request.Data.GetString(256);
            }
            catch (Exception)
            {
                Reject(request, "Not a CairnMP client.");
                return;
            }

            if (!string.Equals(appKey, AppKey, StringComparison.Ordinal))
            {
                Reject(request, "Not a CairnMP client.");
                return;
            }
            if (protocol != Protocol.Version)
            {
                Reject(request, $"Protocol {protocol} is incompatible with the host's protocol {Protocol.Version}.");
                return;
            }
            if (!string.Equals(modVersion, Protocol.GameVersion, StringComparison.OrdinalIgnoreCase))
            {
                Reject(request, $"Mod version {modVersion} is incompatible with the host's version {Protocol.GameVersion}.");
                return;
            }
            var expected = _settings.Password?.Invoke() ?? "";
            if (expected.Length > 0 && !string.Equals(password, expected, StringComparison.Ordinal))
            {
                Reject(request, "Wrong lobby password.");
                return;
            }
            if ((peerId >> 56) != PeerIdPrefix || peerId == LocalPeerId)
            {
                Reject(request, "Player id collision. Try joining again.");
                return;
            }

            var returning = _seats.Contains(peerId);
            if (!returning)
            {
                if (_peersById.ContainsKey(peerId))
                {
                    Reject(request, "Player id collision. Try joining again.");
                    return;
                }
                if (1 + _seats.Count >= MaxMembers)
                {
                    Reject(request, "Lobby is full.");
                    return;
                }
            }
            else if (_peersById.TryGetValue(peerId, out var stale))
            {
                // The guest noticed the drop before we did: its old connection is still
                // waiting out its time-out here. Forget it so its end is not read as a leave.
                _idsByPeer.Remove(stale);
                _peersById.Remove(peerId);
                _seatDeadlines.TryAdd(peerId, Now() + ReconnectWindow);
            }

            var peer = request.Accept();
            _idsByPeer[peer] = peerId;
            _peersById[peerId] = peer;
            _seatNames[peerId] = Clip(name, $"Player{_seats.Count + 1}");
        }

        private static void Reject(ConnectionRequest request, string reason)
        {
            ModLog.Warning($"[Direct] Refused {request.RemoteEndPoint}: {reason}");
            var w = new NetDataWriter();
            w.Put(reason);
            request.Reject(w);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            if (!IsHost)
            {
                ModLog.Info("[Direct] Connected to host; waiting for the lobby roster.");
                return;
            }
            if (!_idsByPeer.TryGetValue(peer, out var id)) return;

            var returned = _seatDeadlines.Remove(id);
            if (!_seats.Contains(id)) _seats.Add(id);
            RebuildHostMembers();
            ModLog.Info($"[Direct] {_seatNames[id]} {(returned ? "reconnected" : "joined")} from {peer}.");

            var welcome = new NetDataWriter();
            welcome.Put((byte)Control.Welcome);
            welcome.Put(CurrentLobbyName);
            welcome.Put(CurrentRoomCode);
            welcome.Put((byte)_mode);
            welcome.Put(MaxMembers);
            welcome.Put(HostPeerId);
            WriteRoster(welcome);
            welcome.Put(_hasHostStart);
            if (_hasHostStart) WriteStart(welcome, _hostStartNonce, _hostStart);
            peer.Send(welcome, ControlChannel, DeliveryMethod.ReliableOrdered);

            BroadcastRoster(except: peer);
            OnMembersChanged?.Invoke();
            if (returned)
            {
                OnConnectionNotice?.Invoke($"{_seatNames[id]} reconnected.");
                OnPeerReconnected?.Invoke(id);
            }
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (IsHost)
            {
                HostLostPeer(peer, info);
                return;
            }

            if (peer != _hostPeer) return;
            _hostPeer = null;
            var reason = ReadReason(info);

            if (_joinTcs != null && !_reconnecting)
            {
                FailJoin(reason);
                return;
            }
            if (!IsInLobby) return;

            if (!IsTransient(info.Reason) || ReconnectWindow <= 0)
            {
                ModLog.Warning($"[Direct] Lost the host: {reason}");
                Leave();
                OnLobbyError?.Invoke(reason);
                return;
            }

            if (!_reconnecting)
            {
                _reconnecting = true;
                _reconnectDeadline = Now() + ReconnectWindow;
                ModLog.Warning($"[Direct] Lost the host ({reason}); reconnecting for up to {ReconnectWindow:0} s.");
                OnConnectionNotice?.Invoke($"Connection to the host lost. Reconnecting for up to {DescribeWindow()}...");
            }
            _nextRedialAt = Now() + RedialDelaySeconds;
        }

        private void HostLostPeer(NetPeer peer, DisconnectInfo info)
        {
            // A peer replaced by its own reconnection is no longer mapped: ignore its end.
            if (!_idsByPeer.TryGetValue(peer, out var id)) return;
            _idsByPeer.Remove(peer);
            _peersById.Remove(id);
            if (!IsInLobby) return;

            _seatNames.TryGetValue(id, out var name);
            if (!_seats.Contains(id))
            {
                _seatNames.Remove(id);
                return; // accepted but never arrived
            }

            if (IsTransient(info.Reason) && ReconnectWindow > 0)
            {
                _seatDeadlines.TryAdd(id, Now() + ReconnectWindow);
                ModLog.Warning($"[Direct] {name} dropped ({info.Reason}); keeping the seat for {ReconnectWindow:0} s.");
                OnConnectionNotice?.Invoke($"{name} lost connection. Waiting up to {DescribeWindow()} for them to reconnect.");
            }
            else
            {
                RemoveSeat(id);
                ModLog.Info($"[Direct] {name} left ({info.Reason}).");
            }

            RebuildHostMembers();
            BroadcastRoster(except: null);
            OnMembersChanged?.Invoke();
        }

        private void ExpireSeats()
        {
            if (_seatDeadlines.Count == 0) return;
            var now = Now();
            List<ulong> expired = null;
            foreach (var (id, deadline) in _seatDeadlines)
                if (now >= deadline) (expired ??= new List<ulong>()).Add(id);
            if (expired == null) return;

            foreach (var id in expired)
            {
                var name = _seatNames.TryGetValue(id, out var n) ? n : "A player";
                RemoveSeat(id);
                ModLog.Info($"[Direct] {name} did not reconnect in time; seat released.");
                OnConnectionNotice?.Invoke($"{name} did not reconnect.");
            }
            RebuildHostMembers();
            BroadcastRoster(except: null);
            OnMembersChanged?.Invoke();
        }

        private void RemoveSeat(ulong id)
        {
            _seats.Remove(id);
            _seatNames.Remove(id);
            _seatDeadlines.Remove(id);
        }

        private void PumpReconnect()
        {
            var now = Now();
            if (now >= _reconnectDeadline)
            {
                ModLog.Warning("[Direct] Gave up reconnecting to the host.");
                Leave();
                OnLobbyError?.Invoke($"Could not reconnect to the host within {DescribeWindow()}.");
                return;
            }
            if (_hostPeer != null || now < _nextRedialAt) return;

            _nextRedialAt = now + RedialDelaySeconds;
            if (!Dial(out var error))
                ModLog.Debug($"[Direct] Redial failed: {error}");
        }

        /// <summary>A goodbye or a refusal is final; anything else may be a network blip.</summary>
        private static bool IsTransient(DisconnectReason reason)
            => reason is not (DisconnectReason.RemoteConnectionClose
                or DisconnectReason.DisconnectPeerCalled
                or DisconnectReason.ConnectionRejected
                or DisconnectReason.InvalidProtocol);

        private string DescribeWindow()
        {
            var seconds = ReconnectWindow;
            return seconds >= 60 && seconds % 60 == 0
                ? $"{seconds / 60:0} minute{(seconds == 60 ? "" : "s")}"
                : $"{seconds:0} seconds";
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (channel == GameChannel)
            {
                ulong from;
                if (IsHost)
                {
                    if (!_idsByPeer.TryGetValue(peer, out from)) return;
                }
                else
                {
                    if (peer != _hostPeer || !IsInLobby) return;
                    from = HostPeerId;
                }
                var payload = reader.GetRemainingBytes();
                if (payload.Length > 0) _inbox.Enqueue((from, payload));
                return;
            }

            if (channel != ControlChannel || IsHost || peer != _hostPeer) return;
            try
            {
                switch ((Control)reader.GetByte())
                {
                    case Control.Welcome: HandleWelcome(reader); break;
                    case Control.Roster:
                        ReadRoster(reader);
                        OnMembersChanged?.Invoke();
                        break;
                    case Control.Start:
                        var (nonce, start) = ReadStart(reader);
                        RaiseStart(nonce, start);
                        break;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[Direct] Dropped a malformed control message: {ex.Message}");
            }
        }

        private void HandleWelcome(NetPacketReader reader)
        {
            var lobbyName = reader.GetString(128);
            var roomCode = reader.GetString(128);
            var mode = MultiplayerModes.Parse(reader.GetByte()).Mode;
            var maxMembers = reader.GetInt();
            var hostPeerId = reader.GetULong();

            var wasReconnecting = _reconnecting;
            _reconnecting = false;
            if (wasReconnecting && hostPeerId != HostPeerId)
            {
                // The host restarted and is hosting again at the same address: our old
                // session is gone. Close it, then enter the new one as a fresh join.
                ModLog.Info("[Direct] The host is back with a new lobby; joining it.");
                var dial = _hostPeer;
                var address = (_hostAddress, _hostPort);
                var peerId = LocalPeerId;
                ResetState();
                OnLobbyLeft?.Invoke();
                (_hostPeer, (_hostAddress, _hostPort), LocalPeerId) = (dial, address, peerId);
                wasReconnecting = false;
            }

            CurrentLobbyName = lobbyName;
            CurrentRoomCode = roomCode;
            _mode = mode;
            MaxMembers = maxMembers;
            HostPeerId = hostPeerId;
            ReadRoster(reader);
            var hasStart = reader.GetBool();
            var pendingStart = hasStart ? ReadStart(reader) : default;

            IsHost = false;
            IsInLobby = true;

            if (wasReconnecting)
            {
                ModLog.Info($"[Direct] Reconnected to '{CurrentLobbyName}'.");
                OnMembersChanged?.Invoke();
                OnConnectionNotice?.Invoke("Reconnected to the host.");
                OnPeerReconnected?.Invoke(HostPeerId);
                if (hasStart) RaiseStart(pendingStart.nonce, pendingStart.start);
                return;
            }

            ModLog.Info($"[Direct] Entered '{CurrentLobbyName}' ({_members.Count} members).");
            var join = _joinTcs;
            _joinTcs = null;
            OnLobbyEntered?.Invoke();
            OnMembersChanged?.Invoke();
            if (hasStart) RaiseStart(pendingStart.nonce, pendingStart.start);
            join?.TrySetResult(true);
        }

        private void RebuildHostMembers()
        {
            _members.Clear();
            _members.Add(new LobbyMember { SteamId = LocalPeerId, Name = LocalPersonaName, IsHost = true, IsSelf = true });
            foreach (var id in _seats)
            {
                var name = _seatNames.TryGetValue(id, out var n) ? n : "Player";
                if (_seatDeadlines.ContainsKey(id)) name += ReconnectingSuffix;
                _members.Add(new LobbyMember { SteamId = id, Name = name });
            }
        }

        private void WriteRoster(NetDataWriter w)
        {
            w.Put(_members.Count);
            foreach (var member in _members)
            {
                w.Put(member.SteamId);
                w.Put(member.Name);
            }
        }

        private void ReadRoster(NetDataReader r)
        {
            var count = r.GetInt();
            if (count < 0 || count > 16) throw new InvalidOperationException($"bad roster size {count}");
            _members.Clear();
            for (int i = 0; i < count; i++)
            {
                var id = r.GetULong();
                var name = Clip(r.GetString(MaxNameLength * 2), $"Player{i + 1}");
                _members.Add(new LobbyMember
                {
                    SteamId = id,
                    Name = name,
                    IsHost = id == HostPeerId,
                    IsSelf = id == LocalPeerId,
                });
            }
        }

        private void BroadcastRoster(NetPeer except)
        {
            var w = new NetDataWriter();
            w.Put((byte)Control.Roster);
            WriteRoster(w);
            foreach (var peer in _peersById.Values)
                if (peer != except && peer.ConnectionState == ConnectionState.Connected)
                    peer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
        }

        private static void WriteStart(NetDataWriter w, string nonce, ServerStartGame start)
        {
            w.Put(nonce);
            w.Put(start.Difficulty);
            w.Put(start.SkipTutorials);
            w.Put(start.SkipPractice);
            w.Put(start.AssistEnabled);
            w.Put(start.Mode);
            w.Put(start.ExtraConstraints);
        }

        private static (string nonce, ServerStartGame start) ReadStart(NetDataReader r)
        {
            var nonce = r.GetString(64);
            var start = new ServerStartGame
            {
                Difficulty = r.GetInt(),
                SkipTutorials = r.GetBool(),
                SkipPractice = r.GetBool(),
                AssistEnabled = r.GetBool(),
                Mode = r.GetByte(),
                ExtraConstraints = r.GetInt(),
            };
            return (nonce, start);
        }

        private void RaiseStart(string nonce, ServerStartGame start)
        {
            if (string.IsNullOrEmpty(nonce) || string.Equals(nonce, _lastStartNonce, StringComparison.Ordinal))
                return;
            _lastStartNonce = nonce;
            _mode = MultiplayerModes.Parse(start.Mode).Mode;
            OnStartRequested?.Invoke(start);
        }

        private void FailJoin(string reason)
        {
            var join = _joinTcs;
            _joinTcs = null;
            if (_net.IsRunning) _net.Stop();
            ResetState();
            ModLog.Warning($"[Direct] Join failed: {reason}");
            OnLobbyError?.Invoke(reason);
            join?.TrySetResult(false);
        }

        private Task<bool> Busy()
        {
            OnLobbyError?.Invoke("Another lobby operation is already running.");
            return Task.FromResult(false);
        }

        private void ResetState()
        {
            IsInLobby = false;
            IsHost = false;
            HostPeerId = 0;
            MaxMembers = 0;
            CurrentRoomCode = "";
            CurrentLobbyName = "";
            _mode = MultiplayerMode.RopeTeam;
            _hostPeer = null;
            _hostAddress = "";
            _hostPort = 0;
            _reconnecting = false;
            _hasHostStart = false;
            _hostStartNonce = "";
            _lastStartNonce = "";
            _members.Clear();
            _seats.Clear();
            _seatNames.Clear();
            _seatDeadlines.Clear();
            _peersById.Clear();
            _idsByPeer.Clear();
            _inbox.Clear();
        }

        private static string ReadReason(DisconnectInfo info)
        {
            try
            {
                if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0
                    && info.AdditionalData.TryGetString(out var text) && !string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("direct.read-disconnect-reason", exception);
            }

            return info.Reason switch
            {
                DisconnectReason.Timeout => "The connection to the host timed out.",
                DisconnectReason.ConnectionFailed => "Could not reach the host. Check the address and that its UDP port is open.",
                DisconnectReason.HostUnreachable or DisconnectReason.NetworkUnreachable => "The host's network is unreachable.",
                DisconnectReason.UnknownHost => "Unknown host name.",
                DisconnectReason.ConnectionRejected => "The host refused the connection.",
                DisconnectReason.RemoteConnectionClose => "The host closed the lobby.",
                _ => $"Disconnected from the host ({info.Reason}).",
            };
        }

        private static ulong NewPeerId()
        {
            Span<byte> bytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(bytes);
            var id = BitConverter.ToUInt64(bytes);
            return ((ulong)PeerIdPrefix << 56) | (id & 0x00FF_FFFF_FFFF_FFFFUL) | 1UL;
        }

        private static string Clip(string value, string fallback)
        {
            var s = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return s.Length <= MaxNameLength ? s : s.Substring(0, MaxNameLength);
        }

        /// <summary>A Steam room code never holds '.' or ':', so either one marks an address.</summary>
        public static bool LooksLikeAddress(string code)
            => !string.IsNullOrWhiteSpace(code) && (code.Contains('.') || code.Contains(':'));

        /// <summary>Accepts "host", "host:port", "[v6]:port" and a bare IPv6 address.</summary>
        public static bool TryParseAddress(string code, out string host, out int port)
        {
            host = "";
            port = DefaultPort;
            if (string.IsNullOrWhiteSpace(code)) return false;
            var s = code.Trim();

            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                var close = s.IndexOf(']');
                if (close < 2) return false;
                host = s.Substring(1, close - 1);
                var rest = s.Substring(close + 1);
                if (rest.Length > 0 && !(rest.StartsWith(":", StringComparison.Ordinal)
                                         && TryPort(rest.Substring(1), out port)))
                    return false;
                return true;
            }

            var colons = s.Count(c => c == ':');
            if (colons == 1)
            {
                var at = s.IndexOf(':');
                host = s.Substring(0, at);
                return IsHostName(host) && TryPort(s.Substring(at + 1), out port);
            }

            host = s;
            if (colons > 1) return IPAddress.TryParse(s, out _);
            return IsHostName(host);
        }

        private static bool IsHostName(string host)
            => host.Length > 0 && host.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-');

        private static bool TryPort(string text, out int port)
            => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
               && port > 0 && port <= 65535;

        private static string FormatRoomCode(IPAddress address, int port)
        {
            if (address == null) return $"YOUR-IP:{port}";
            return address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"
                : $"{address}:{port}";
        }

        /// <summary>
        /// The address worth sharing: a Tailscale (100.64/10), Radmin (26/8) or ZeroTier
        /// address first, since those reach a friend without port forwarding; then a LAN one.
        /// </summary>
        internal static IPAddress BestLocalAddress()
        {
            var candidates = new List<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var zeroTier = nic.Description?.IndexOf("ZeroTier", StringComparison.OrdinalIgnoreCase) >= 0
                                   || nic.Name?.StartsWith("zt", StringComparison.OrdinalIgnoreCase) == true;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var a = unicast.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        if (zeroTier) return a;
                        candidates.Add(a);
                    }
                }
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("direct.list-interfaces", exception);
            }

            if (candidates.Count == 0)
            {
                try
                {
                    candidates.AddRange(Dns.GetHostAddresses(Dns.GetHostName())
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)));
                }
                catch (Exception exception)
                {
                    ModLog.SuppressedException("direct.resolve-own-host", exception);
                }
            }

            return candidates.OrderBy(Rank).FirstOrDefault();
        }

        private static int Rank(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return 0; // Tailscale / CGNAT overlay
            if (b[0] == 26) return 1;                                  // Radmin VPN
            if (b[0] == 169 && b[1] == 254) return 9;                  // link-local: never useful
            if (b[0] == 192 && b[1] == 168) return 3;
            if (b[0] == 10) return 3;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 3;
            return 2;                                                  // public address
        }
    }
}
