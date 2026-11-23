using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>Explicit opt-in Relay session and NGO pose synchronization. No cloud requests in Awake/Start.</summary>
public sealed class FuchuOnlineConnection : MonoBehaviour
{
    public const int ProtocolVersion = 6;
    private const string PoseMessage = "Fuchu.Preview.Pose.v6";
    private const string RoundMessage = "Fuchu.Match.Round.v6";
    private const string SnapshotMessage = "Fuchu.Preview.Snapshot.v6";
    private const int MaxPlayers = 5;
    private const float SendInterval = 0.05f;
    private const double MaxSessionSeconds = 900.0;
    private static readonly Color[] Colors = { new Color(0.08f, 0.42f, 1f), Color.red,
        Color.yellow, Color.green, new Color(0.65f, 0.18f, 0.95f) };
    private static readonly string[] ColorNames = { "青", "赤", "黄", "緑", "紫" };
    private readonly Dictionary<ulong, PeerPose> poses = new Dictionary<ulong, PeerPose>();
    private readonly Dictionary<ulong, FuchuOnlineAvatar> avatars = new Dictionary<ulong, FuchuOnlineAvatar>();
    private readonly List<ulong> stale = new List<ulong>();
    private FuchuWalkController player;
    private MeshRenderer localVisual;
    private Color offlineColor;
    private NetworkManager network;
    private ISession session;
    private FuchuOnlineBuildingCatalog buildingCatalog;
    private FuchuOnlineTerritorySync territorySync;
    private FuchuOnlineMatch match;
    private FuchuOnlineSimulation simulation;
    private bool dedicated, direct, directSelected;
    private string serverAddress = "127.0.0.1", serverPort = "7777";
    private TaskCompletionSource<bool> directCompletion;
    private ulong controlPeer = ulong.MaxValue;
    private bool savedBackground, backgroundSaved;
    internal bool DirectConnection => direct;
    internal bool CanStartMatch => network != null && (network.IsHost || direct && !dedicated && network.LocalClientId == controlPeer);
    private ulong StartPeer
    {
        get
        {
            if (!network.IsServer) return controlPeer;
            if (network.IsHost) return network.LocalClientId;
            ulong first = ulong.MaxValue;
            foreach (ulong peer in poses.Keys) if (peer < first) first = peer;
            return first;
        }
    }
    private bool showedResults;
    internal double ServerTime => network != null && network.IsListening ? network.ServerTime.Time : 0;
    internal bool MatchPlaying => territorySync != null && territorySync.Ready && territorySync.State.Playing && ServerTime < territorySync.State.MatchDeadline;
    internal double RemainingSeconds => territorySync != null && territorySync.State.MatchStarted ? Math.Max(0, territorySync.State.MatchDeadline - ServerTime) : 300;
    internal bool MatchStarted => territorySync != null && territorySync.State.MatchStarted;
    internal bool MatchFinished => territorySync != null && territorySync.State.MatchFinished;
    private double MatchStartAt => territorySync != null ? territorySync.State.MatchStartAt : 0;
    internal int Countdown => TerritoryReady && MatchStartAt > 0 && !MatchFinished
        ? (int)Math.Ceiling(Math.Clamp(MatchStartAt - ServerTime, 0, FuchuOnlineTerritoryState.CountdownSeconds)) : 0;
    internal bool ShowingGo => MatchPlaying && ServerTime - MatchStartAt < 0.75;
    internal bool CanScheduleMatch => CanStartMatch && TerritoryReady && !busy &&
        (direct || Time.realtimeSinceStartupAsDouble - connectedAt <
            MaxSessionSeconds - FuchuOnlineTerritoryState.MatchSeconds - FuchuOnlineTerritoryState.CountdownSeconds);
    private double observedMatchStart;
    internal FuchuOnlineTerritoryState DisplayState => territorySync?.State;
    internal int LocalSlot => territorySync != null && network != null ? territorySync.State.SlotOf(network.LocalClientId) : -1;
    internal double LocalCoverage => (LocalGrowth?.Shape.ClaimedSquareMeters ?? 0.0) / (Math.PI * 1000.0 * 1000.0);
    internal string RoomCode => direct ? serverAddress + ":" + serverPort : session?.Code ?? "";
    internal void CopyRoomCode() => GUIUtility.systemCopyBuffer = RoomCode;
    internal static Color DisplayColor(int slot) => Colors[slot];
    internal static string DisplayColorName(int slot) => ColorNames[slot];
    internal bool TryGetBuildingOwner(string id, out int owner)
    {
        owner = -1;
        if (!TerritoryReady || string.IsNullOrEmpty(id) || !buildingCatalog.Index.ContainsKey(id)) return false;
        owner = territorySync.State.OwnerOf(id);
        return true;
    }
    internal bool LocalSlowed => LocalGrowth != null && ServerTime < LocalGrowth.SlowUntil;
    internal FuchuOnlineTerritoryState.Participant LocalGrowth
    {
        get
        {
            if (territorySync == null || network == null) return null;
            int slot = territorySync.State.SlotOf(network.LocalClientId);
            return slot >= 0 ? territorySync.State.Players[slot] : null;
        }
    }
    internal bool TerritoryReady => territorySync != null && territorySync.Ready;
    private bool busy;
    private bool leaving;
    private bool destroyed;
    private bool handlersRegistered;
    private bool pageOpen;
    private bool costsConfirmed;
    private bool placedNearHost;
    private string joinCode = "";
    private string status = "未接続";
    private float nextSend;
    private uint poseSequence;
    private uint localResetEpoch;
    private uint snapshotSequence;
    private uint receivedSnapshot;
    private int snapshotGeneration;
    private double connectedAt;

    internal bool Exploring { get; private set; }
    internal bool MenuVisible => !dedicated && pageOpen && (!Exploring || player.MenuOpen);
    internal bool IsBusy => busy;
    private string Protocol => $"FuchuWalkMatch-v{ProtocolVersion}|" + Application.version + "|" + buildingCatalog.Hash;
    private int PlayerCount
    {
        get
        {
            if (network != null && network.IsHost) return network.ConnectedClientsIds.Count;
            int count = 0;
            if (territorySync != null) foreach (var p in territorySync.State.Players) if (p.Active && !p.IsNpc) count++;
            return count;
        }
    }

    private sealed class PeerPose
    {
        internal Vector3 Position;
        internal float Yaw;
        internal byte Slot;
        internal uint Sequence;
        internal uint ResetEpoch;
        internal double LastPacket;
        internal double LastRecord;
        internal Vector3 LastPosition;
        internal bool HasRecord;
    }

    internal void Initialize(FuchuWalkController controller, MeshRenderer renderer)
    {
        player = controller;
        localVisual = renderer;
        offlineColor = renderer.sharedMaterials[1].color;
    }

    internal void OpenMenu() { pageOpen = true; status = "未接続：接続方法を選択してください"; }

    private void CreateNetworkManager(FuchuOnlineSimulation context = null)
    {
        simulation = context ?? new FuchuOnlineSimulation(player);
        Vector3 center = simulation.Center;
        buildingCatalog = new FuchuOnlineBuildingCatalog(new Vector2(center.x, center.z));
        if (NetworkManager.Singleton != null) throw new InvalidOperationException("別のNetworkManagerが既に存在します。");
        savedBackground = Application.runInBackground; backgroundSaved = true;
        Application.runInBackground = true;
        var obj = new GameObject("Fuchu Online NetworkManager");
        network = obj.AddComponent<NetworkManager>();
        var transport = obj.AddComponent<UnityTransport>();
        network.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = transport,
            ProtocolVersion = ProtocolVersion,
            ConnectionApproval = true,
            ConnectionData = Encoding.UTF8.GetBytes(Protocol),
            EnableSceneManagement = false,
            ForceSamePrefabs = false,
            PlayerPrefab = null,
            AutoSpawnPlayerPrefabClientSide = false,
            TickRate = 20
        };
        simulation.Network = network;
        territorySync = new FuchuOnlineTerritorySync(network, simulation, buildingCatalog, Colors);
        network.ConnectionApprovalCallback = ApproveConnection;
        network.OnServerStarted += RegisterHandlers;
        network.OnClientStarted += RegisterHandlers;
        network.OnClientConnectedCallback += OnConnected;
        network.OnClientDisconnectCallback += OnDisconnected;
        network.OnClientStopped += OnClientStopped;
    }

    private void ApproveConnection(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        bool compatible = request.Payload != null && Encoding.UTF8.GetString(request.Payload) == Protocol;
        bool finished = territorySync != null && territorySync.State.MatchFinished;
        response.Approved = compatible && !finished && network.ConnectedClientsIds.Count < MaxPlayers;
        response.CreatePlayerObject = false;
        response.Pending = false;
        response.Reason = finished ? "この部屋の試合は終了しています。新しい部屋を作成してください。" : compatible ? "部屋の定員は5人です。" : "接続確認用のバージョンが異なります。同じビルドを使用してください。";
    }

    private void RegisterHandlers()
    {
        if (handlersRegistered || network == null || network.CustomMessagingManager == null) return;
        network.CustomMessagingManager.RegisterNamedMessageHandler(PoseMessage, ReceivePose);
        network.CustomMessagingManager.RegisterNamedMessageHandler(SnapshotMessage, ReceiveSnapshot);
        network.CustomMessagingManager.RegisterNamedMessageHandler(RoundMessage, ReceiveRoundAction);
        territorySync.Register();
        handlersRegistered = true;
    }

    private void OnConnected(ulong id)
    {
        if (!network.IsServer && id == network.LocalClientId) directCompletion?.TrySetResult(true);
        if (!network.IsServer || dedicated && id == NetworkManager.ServerClientId || poses.ContainsKey(id)) return;
        if (territorySync.State.MatchFinished)
        { network.DisconnectClient(id, "この試合は終了しています。"); return; }
        byte slot = 0;
        for (; slot < MaxPlayers; slot++)
        {
            bool used = false;
            foreach (PeerPose pose in poses.Values) if (pose.Slot == slot) { used = true; break; }
            if (!used) break;
        }
        if (slot >= MaxPlayers) { network.DisconnectClient(id, "部屋の定員は5人です。"); return; }
        poses[id] = new PeerPose { Slot = slot, Position = simulation.Spawn,
            Yaw = player != null ? player.transform.localEulerAngles.y : 0 };
        match?.HumanJoined(slot);
        territorySync.Bind(slot, id);
        if (dedicated) Debug.Log($"[FuchuServer] CONNECT peer={id} slot={slot} humans={poses.Count}");
    }

    private void OnDisconnected(ulong id)
    {
        if (network == null || leaving || destroyed) return;
        if (!network.IsServer && id == network.LocalClientId)
            directCompletion?.TrySetException(new InvalidOperationException("接続が終了しました: " + network.DisconnectReason));
        if (network.IsServer)
        {
            bool finished = territorySync.State.MatchFinished;
            if (poses.TryGetValue(id, out PeerPose removed) && !finished) territorySync.Release(removed.Slot);
            poses.Remove(id);
            if (removed != null && !finished) match?.FillSlot(removed.Slot);
            if (dedicated && removed != null)
            {
                Debug.Log($"[FuchuServer] DISCONNECT peer={id} humans={poses.Count} replacedByNpc={!finished}");
                if (poses.Count == 0 && MatchStartAt > 0) ResetRound();
            }
            if (avatars.TryGetValue(id, out FuchuOnlineAvatar avatar)) { avatar.Dispose(); avatars.Remove(id); }
        }
        else if (id == network.LocalClientId && !busy)
            _ = LeaveAsync(direct ? "専用サーバーとの接続が切れました。" : "ホストとの接続が切れました。部屋を終了します。");
    }

    private void OnClientStopped(bool wasHost)
    {
        if (!leaving && !busy && !destroyed && Exploring)
            _ = LeaveAsync(wasHost ? "部屋を終了しました。" : "接続が切れました。部屋を終了します。");
    }

    internal void StartDedicated(FuchuOnlineSimulation context, string bindAddress, ushort port)
    {
        dedicated = direct = true; simulation = context;
        serverAddress = bindAddress; serverPort = port.ToString();
        try
        {
            CreateNetworkManager(context);
            network.GetComponent<UnityTransport>().SetConnectionData("127.0.0.1", port, bindAddress);
            if (!network.StartServer()) throw new InvalidOperationException("UDPポートを開けません。同じポートのサーバーが動いていないか確認してください。");
            Exploring = true;
            territorySync.StartView();
            match = new FuchuOnlineMatch(simulation, network, territorySync, Colors);
            Debug.Log($"[FuchuServer] LISTENING {bindAddress}:{port} protocol={ProtocolVersion} slots=5 NPC={match.NpcCount}");
        }
        catch { Exploring = false; DestroyNetworkManager(); enabled = false; throw; }
    }
    internal async Task ConnectDirectAsync()
    {
        if (busy || Exploring) return;
        serverAddress = serverAddress.Trim(); serverPort = serverPort.Trim();
        if (!FuchuLocalServer.IsIPv4(serverAddress) || serverAddress == "0.0.0.0" || !ushort.TryParse(serverPort, out ushort port) || port == 0)
        { status = "接続先IPv4と1～65535のポートを入力してください。"; return; }
        busy = true; direct = true; status = "専用サーバーへ接続しています…";
        try
        {
            CreateNetworkManager();
            network.GetComponent<UnityTransport>().SetConnectionData(serverAddress, port);
            directCompletion = new TaskCompletionSource<bool>();
            if (!network.StartClient()) throw new InvalidOperationException("クライアントを開始できませんでした。");
            Task finished = await Task.WhenAny(directCompletion.Task, Task.Delay(15000));
            if (destroyed) return;
            if (finished != directCompletion.Task) throw new TimeoutException("接続先のサーバーが応答しません。起動状態・IPv4・UDPポートを確認してください。");
            await directCompletion.Task;
            if (network == null || !network.IsConnectedClient) throw new InvalidOperationException("接続が終了しました。");
            Exploring = true; connectedAt = Time.realtimeSinceStartupAsDouble;
            player.EnterOnlineExploration(); territorySync.StartView();
            match = new FuchuOnlineMatch(simulation, network, territorySync, Colors);
            showedResults = false;
            status = "接続しました。最初の参加者が試合開始を操作します。";
        }
        catch (Exception exception)
        {
            Debug.LogWarning("専用サーバー接続: " + exception.Message);
            status = "接続できませんでした：" + exception.Message;
            await CleanupSessionAsync();
            if (Exploring && !destroyed)
            {
                Exploring = false; player.ExitOnlineExploration(); FuchuBattleMatch.Tint(localVisual, offlineColor);
            }
        }
        finally { busy = false; }
    }
    private void RequestMatchStart()
    {
        if (MatchStartAt == 0) RequestRoundAction(1);
    }

    internal void RequestRematch()
    {
        if (MatchFinished) RequestRoundAction(2);
    }

    private void RequestRoundAction(byte action)
    {
        if (!CanScheduleMatch) return;
        if (network.IsServer) ApplyRoundAction(action);
        else using (var writer = new FastBufferWriter(9, Allocator.Temp))
        {
            writer.WriteValueSafe(action);
            writer.WriteValueSafe(MatchStartAt);
            network.CustomMessagingManager.SendNamedMessage(RoundMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
        }
    }
    private void ReceiveRoundAction(ulong sender, FastBufferReader reader)
    {
        if (!dedicated || !network.IsServer || sender != StartPeer || !poses.ContainsKey(sender) || reader.Length - reader.Position != 9) return;
        reader.ReadValueSafe(out byte action);
        reader.ReadValueSafe(out double expectedStart);
        if (expectedStart == MatchStartAt) ApplyRoundAction(action);
    }

    private void ApplyRoundAction(byte action)
    {
        if (action == 2 && MatchFinished) ResetRound();
        if (action != 1 || MatchStartAt > 0) return;
        territorySync.State.StartMatch(ServerTime);
        if (dedicated)
            Debug.Log($"[FuchuServer] MATCH_SCHEDULED at={MatchStartAt:F3}, humans={poses.Count}, NPC={match.NpcCount}");
    }

    private void ResetRound()
    {
        var previous = territorySync.State;
        territorySync.ResetRound();
        match.ResetRound();
        foreach (var pair in poses)
        {
            var pose = pair.Value;
            territorySync.Bind(pose.Slot, pair.Key);
            var participant = territorySync.State.Players[pose.Slot];
            participant.CorrectionVersion = previous.Players[pose.Slot].CorrectionVersion + 1;
            participant.CorrectionPosition = participant.SpawnPosition = participant.Position = simulation.Spawn;
            participant.HasPosition = true;
            pose.HasRecord = false;
            pose.Position = simulation.Spawn;
        }
        for (int slot = 0; slot < MaxPlayers; slot++)
            if (!territorySync.State.Players[slot].Active) match.FillSlot(slot);
    }

    internal async Task ConnectAsync(bool create)
    {
        if (busy || Exploring || !costsConfirmed) return;
        if (!create && string.IsNullOrWhiteSpace(joinCode)) { status = "参加コードを入力してください。"; return; }
        if (string.IsNullOrEmpty(Application.cloudProjectId)) { status = "Project Settings > ServicesでUnity Cloud Projectを設定してください。"; return; }
        direct = false;
        busy = true;
        status = create ? "部屋を作成しています…" : "部屋へ接続しています…";
        try
        {
            CreateNetworkManager();
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetProfile("fuchu" + Guid.NewGuid().ToString("N").Substring(0, 12));
                await UnityServices.InitializeAsync(options);
            }
            if (destroyed) return;
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            if (destroyed) return;
            ISession connected = create
                ? await MultiplayerService.Instance.CreateSessionAsync(new SessionOptions
                    { MaxPlayers = MaxPlayers, IsPrivate = true, Name = "Fuchu territory sync" }.WithRelayNetwork())
                : await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode.Trim().ToUpperInvariant());
            if (destroyed) { await connected.LeaveAsync(); return; }
            session = connected;
            if (network == null || !network.IsConnectedClient)
                throw new InvalidOperationException("通信接続が完了しませんでした。同じビルド・サービス設定を確認してください。");
            Exploring = true;
            connectedAt = Time.realtimeSinceStartupAsDouble;
            player.EnterOnlineExploration();
            territorySync.StartView();
            match = new FuchuOnlineMatch(simulation, network, territorySync, Colors);
            showedResults = false;
            if (network.IsHost) FuchuBattleMatch.Tint(localVisual, Colors[0]);
            status = "接続しました。ホストが「5分間の試合を開始」を押すと試合が始まります。";
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            status = "接続できませんでした：" + exception.Message;
            await CleanupSessionAsync();
            if (Exploring && !destroyed)
            {
                Exploring = false;
                player.ExitOnlineExploration();
                FuchuBattleMatch.Tint(localVisual, offlineColor);
            }
        }
        finally { busy = false; }
    }

    internal async Task LeaveAsync(string message = "退出しました。")
    {
        if (leaving || busy || destroyed) return;
        leaving = true;
        busy = true;
        try { await CleanupSessionAsync(); }
        finally
        {
            Exploring = false;
            if (!destroyed)
            {
                player.ExitOnlineExploration();
                FuchuBattleMatch.Tint(localVisual, offlineColor);
                costsConfirmed = false;
                pageOpen = true;
                status = message;
            }
            busy = false;
            leaving = false;
        }
    }

    private async Task CleanupSessionAsync()
    {
        ISession previous = session;
        session = null;
        try { if (previous != null) await previous.LeaveAsync(); }
        catch (Exception exception) { Debug.LogWarning("セッション退出: " + exception.Message); }
        finally { DestroyNetworkManager(); }
    }

    private void DestroyNetworkManager()
    {
        foreach (FuchuOnlineAvatar avatar in avatars.Values) avatar.Dispose();
        avatars.Clear();
        poses.Clear();
        match?.Dispose(); match = null;
        territorySync?.Dispose();
        territorySync = null;
        if (network != null)
        {
            network.OnServerStarted -= RegisterHandlers;
            network.OnClientStarted -= RegisterHandlers;
            network.OnClientConnectedCallback -= OnConnected;
            network.OnClientDisconnectCallback -= OnDisconnected;
            network.OnClientStopped -= OnClientStopped;
            network.ConnectionApprovalCallback = null;
            if (network.CustomMessagingManager != null && handlersRegistered)
            {
                network.CustomMessagingManager.UnregisterNamedMessageHandler(PoseMessage);
                network.CustomMessagingManager.UnregisterNamedMessageHandler(SnapshotMessage);
                network.CustomMessagingManager.UnregisterNamedMessageHandler(RoundMessage);
            }
            network.Shutdown();
            Destroy(network.gameObject);
            network = null;
        }
        handlersRegistered = false;
        receivedSnapshot = poseSequence = snapshotSequence = 0;
        placedNearHost = false;
        localResetEpoch = 0;
        observedMatchStart = 0;
        controlPeer = ulong.MaxValue;
        directCompletion = null;
        if (backgroundSaved && !dedicated) Application.runInBackground = savedBackground;
        backgroundSaved = false;
    }

    internal bool TryGetBuildingPoints(string id, out double points)
    {
        points = 0.0;
        if (buildingCatalog == null || !buildingCatalog.Index.TryGetValue(id, out int index)) return false;
        points = buildingCatalog.Entries[index].Points;
        return true;
    }

    internal void ResetWalkingTrail() => localResetEpoch++;

    private void Update()
    {
        if (!Exploring || leaving || network == null || !network.IsListening || !network.IsServer && !network.IsConnectedClient) return;
        if (!direct && Time.realtimeSinceStartupAsDouble - connectedAt >= MaxSessionSeconds)
        { _ = LeaveAsync("接続は15分で自動退出しました。"); return; }
        try
        {
            territorySync.Tick(); match?.Tick();
            if (TerritoryReady && MatchStartAt != observedMatchStart)
            {
                observedMatchStart = MatchStartAt;
                showedResults = false;
                if (observedMatchStart == 0)
                {
                    pageOpen = true;
                    player?.ShowOnlineLobby();
                }
                else if (!MatchFinished) player?.ContinueRun();
            }
            if (territorySync.State.MatchFinished && !showedResults)
            {
                showedResults = true;
                if (player != null) player.ShowOnlineResults();
                else Debug.Log("[FuchuServer] MATCH_FINISHED: 確保面積と順位を確定しました。");
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (dedicated) { DestroyNetworkManager(); enabled = false; if (Application.isBatchMode) Application.Quit(1); }
            else _ = LeaveAsync("領域同期を継続できないため退出します。");
            return;
        }
        foreach (FuchuOnlineAvatar avatar in avatars.Values) avatar.Tick(Time.unscaledDeltaTime);
        if (!network.IsServer && !placedNearHost) return;
        if (Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + SendInterval;
        Vector3 position = player != null ? player.transform.localPosition : simulation.Spawn;
        float yaw = player != null ? player.transform.localEulerAngles.y : 0;
        if (network.IsServer)
        {
            if (network.IsHost)
            {
                if (!poses.TryGetValue(network.LocalClientId, out PeerPose own)) { OnConnected(network.LocalClientId); own = poses[network.LocalClientId]; }
                if (own.ResetEpoch != localResetEpoch) { own.HasRecord = false; own.ResetEpoch = localResetEpoch; }
                own.Sequence = ++poseSequence;
                player.RegisterOnlinePoseSent(poseSequence);
                if (LocalGrowth == null || player.OnlineCorrectionVersion == LocalGrowth.CorrectionVersion)
                    RecordHostPosition(own, position, player.OnlineSprintRequested);
                if (LocalGrowth != null) player.ApplyOnlineGrowth(LocalGrowth);
                own.Position = player.transform.localPosition;
                own.Yaw = yaw;
            }
            using (var writer = new FastBufferWriter(256, Allocator.Temp))
            {
                writer.WriteValueSafe(++snapshotSequence);
                writer.WriteValueSafe(StartPeer);
                writer.WriteValueSafe(simulation.Spawn); writer.WriteValueSafe(0f);
                writer.WriteValueSafe((byte)(poses.Count + (match?.NpcCount ?? 0)));
                foreach (var pair in poses)
                {
                    writer.WriteValueSafe(pair.Key);
                    writer.WriteValueSafe(pair.Value.Slot);
                    var authoritative = territorySync.State.Players[pair.Value.Slot];
                    Vector3 accepted = authoritative.HasPosition ? authoritative.Position : pair.Value.Position;
                    writer.WriteValueSafe(accepted);
                    writer.WriteValueSafe(pair.Value.Yaw);
                    if (!dedicated && pair.Key != network.LocalClientId) SetAvatar(pair.Key, pair.Value.Slot, accepted, pair.Value.Yaw);
                }
                if (match != null)
                    for (byte slot = 0; slot < 5; slot++)
                    {
                        var bot = match.Bot(slot);
                        if (bot == null) continue;
                        writer.WriteValueSafe(FuchuOnlineTerritoryState.NpcPeer(slot));
                        writer.WriteValueSafe(slot); writer.WriteValueSafe(bot.transform.localPosition);
                        writer.WriteValueSafe(bot.transform.localEulerAngles.y);
                    }
                network.CustomMessagingManager.SendNamedMessageToAll(SnapshotMessage, writer, NetworkDelivery.UnreliableSequenced);
            }
        }
        else
        {
            using (var writer = new FastBufferWriter(32, Allocator.Temp))
            {
                writer.WriteValueSafe(position);
                writer.WriteValueSafe(yaw);
                writer.WriteValueSafe(++poseSequence);
                writer.WriteValueSafe(localResetEpoch);
                writer.WriteValueSafe(player.OnlineCorrectionVersion);
                writer.WriteValueSafe((byte)(player.OnlineSprintRequested ? 1 : 0));
                player.RegisterOnlinePoseSent(poseSequence);
                network.CustomMessagingManager.SendNamedMessage(PoseMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.UnreliableSequenced);
            }
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private bool ValidPose(Vector3 position, float yaw)
    {
        Vector3 center = simulation.Center;
        return Finite(position.x) && Finite(position.y) && Finite(position.z) && Finite(yaw)
            && Mathf.Abs(position.x - center.x) < 1300f && Mathf.Abs(position.z - center.z) < 1300f && Mathf.Abs(position.y) < 1000f;
    }

    private void RecordHostPosition(PeerPose pose, Vector3 position, bool sprint)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        double elapsed = now - pose.LastRecord;
        Vector3 delta = position - pose.LastPosition;
        delta.y = 0f;
        // Teleports (including F5) and long packet gaps can never create a connecting claim.
        bool reset = !pose.HasRecord || elapsed > 2.0 || delta.magnitude > 45f * elapsed + 0.75f;
        territorySync.Record(pose.Slot, position, reset, sprint, elapsed, pose.Sequence);
        pose.HasRecord = true;
        pose.LastRecord = now;
        pose.LastPosition = position;
    }

    private void ReceivePose(ulong sender, FastBufferReader reader)
    {
        if (network == null || !network.IsServer || reader.Length - reader.Position != 29 || !poses.TryGetValue(sender, out PeerPose pose)) return;
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out float yaw);
        reader.ReadValueSafe(out uint sequence);
        reader.ReadValueSafe(out uint resetEpoch);
        reader.ReadValueSafe(out uint correctionVersion);
        reader.ReadValueSafe(out byte sprint);
        if (sprint > 1 || correctionVersion != territorySync.State.Players[pose.Slot].CorrectionVersion) return;
        if (!ValidPose(position, yaw) || sequence <= pose.Sequence || Time.realtimeSinceStartupAsDouble - pose.LastPacket < 0.02) return;
        if (resetEpoch != pose.ResetEpoch) { pose.HasRecord = false; pose.ResetEpoch = resetEpoch; }
        pose.Sequence = sequence;
        RecordHostPosition(pose, position, sprint != 0);
        pose.Position = position;
        pose.Yaw = yaw;
        pose.LastPacket = Time.realtimeSinceStartupAsDouble;
    }

    private void ReceiveSnapshot(ulong sender, FastBufferReader reader)
    {
        if (!Exploring || leaving || network == null || network.IsServer || sender != NetworkManager.ServerClientId || reader.Length - reader.Position < 29) return;
        reader.ReadValueSafe(out uint sequence);
        reader.ReadValueSafe(out ulong startPeer);
        reader.ReadValueSafe(out Vector3 spawn); reader.ReadValueSafe(out float spawnYaw);
        reader.ReadValueSafe(out byte count);
        if (sequence <= receivedSnapshot || count > MaxPlayers || reader.Length - reader.Position != count * 25) return;
        if (!ValidPose(spawn, spawnYaw)) return;
        receivedSnapshot = sequence; controlPeer = startPeer;
        if (!placedNearHost)
        {
            player.PlaceOnlineNear(spawn, spawnYaw);
            placedNearHost = true;
        }
        snapshotGeneration++;
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ulong id);
            reader.ReadValueSafe(out byte slot);
            reader.ReadValueSafe(out Vector3 position);
            reader.ReadValueSafe(out float yaw);
            if (slot >= Colors.Length || !ValidPose(position, yaw)) continue;
            if (id == network.LocalClientId) { FuchuBattleMatch.Tint(localVisual, Colors[slot]); continue; }
            SetAvatar(id, slot, position, yaw);
            avatars[id].SeenInSnapshot = snapshotGeneration;
        }
        stale.Clear();
        foreach (var pair in avatars) if (pair.Value.SeenInSnapshot != snapshotGeneration) stale.Add(pair.Key);
        foreach (ulong id in stale) { avatars[id].Dispose(); avatars.Remove(id); }
    }

    private void SetAvatar(ulong id, byte slot, Vector3 position, float yaw)
    {
        if (avatars.TryGetValue(id, out FuchuOnlineAvatar avatar)) avatar.SetTarget(position, yaw);
        else avatars.Add(id, new FuchuOnlineAvatar(player.transform.parent, id, Colors[slot], position, yaw));
    }

    internal void DrawMenu(GUIStyle heading, GUIStyle text)
    {
        GUI.Label(new Rect(24, 18, 592, 32), "オンライン対戦", heading);
        GUI.Label(new Rect(24, 58, 592, 52), "領域・成長・回復・NPC・道路上の接触をホストが管理します。\n不足人数をNPCで補い、5人で5分間対戦します。", text);
        GUI.Label(new Rect(24, 117, 592, 76), status, text);
        bool previous = GUI.enabled;
        GUI.enabled = previous && !busy;
        if (Exploring)
        {
            GUI.Label(new Rect(24, 208, 592, 28), $"{(direct ? "接続先" : "参加コード")}：{RoomCode}　参加者：{PlayerCount} / {MaxPlayers}", heading);
            if (GUI.Button(new Rect(24, 250, 280, 38), direct ? "接続先をコピー" : "参加コードをコピー")) CopyRoomCode();
            GUI.enabled = previous && !busy && TerritoryReady;
            if (GUI.Button(new Rect(24, 302, 280, 44), territorySync.State.MatchFinished ? "結果を見る（Escでメニュー）" : TerritoryReady ? "ゲーム画面へ（Escでメニュー）" : "領域データを同期しています…")) player.ContinueRun();
            GUI.enabled = previous && !busy;
            if (GUI.Button(new Rect(324, 302, 292, 44), "退出する")) _ = LeaveAsync();
            if (CanStartMatch && MatchStartAt == 0)
            {
                bool canStart = CanScheduleMatch;
                GUI.enabled = previous && !busy && canStart;
                if (GUI.Button(new Rect(24, 358, 592, 40), canStart ? "5分間の試合を開始（空き枠はNPC）" : "待機時間の上限です。退出して部屋を作り直してください"))
                    RequestMatchStart();
                GUI.enabled = previous && !busy;
            }
            else
            {
                int remaining = (int)Math.Ceiling(RemainingSeconds);
                GUI.Label(new Rect(24, 362, 592, 28), territorySync.State.MatchFinished ? "試合終了：確定した領域面積で順位を決定" :
                    territorySync.State.MatchStarted ? $"試合中　残り {remaining / 60:00}:{remaining % 60:00}" : direct ? "管理プレイヤー（最初の参加者）の試合開始を待っています" : "ホストの試合開始を待っています", text);
            }
            GUI.Label(new Rect(24, 410, 592, 100), "開始すると全員の画面に3秒のカウントダウンが表示されます。\nWASD移動／マウス視点／Shiftダッシュ／Spaceジャンプ\n" + (direct ? "専用サーバーが進行を管理します。" : "ホスト退出で部屋終了。接続は最大15分で自動退出します。"), text);
        }
        else
        {
            directSelected = GUI.Toolbar(new Rect(24, 205, 592, 30), directSelected ? 1 : 0,
                new[] { "Relay（参加コード）", "専用サーバー（IP接続）" }) == 1;
            if (directSelected)
            {
                GUI.Label(new Rect(24, 248, 592, 38), "同じPCは127.0.0.1、別PCやAWSはサーバーのIPv4を指定します。Relayは使用しません。", text);
                GUI.Label(new Rect(24, 302, 100, 28), "IPv4", text);
                serverAddress = GUI.TextField(new Rect(130, 298, 280, 32), serverAddress, 45);
                GUI.Label(new Rect(424, 302, 60, 28), "ポート", text);
                serverPort = GUI.TextField(new Rect(490, 298, 126, 32), serverPort, 5);
                if (GUI.Button(new Rect(24, 366, 592, 44), "専用サーバーに接続")) _ = ConnectDirectAsync();
            }
            else
            {
                GUI.Label(new Rect(24, 244, 592, 42), "Relay / Lobbyには無料枠と従量課金があります。管理者が契約・利用量を確認してから接続してください。", text);
                costsConfirmed = GUI.Toggle(new Rect(24, 291, 592, 28), costsConfirmed, "管理者が利用枠・料金と接続を確認済み");
                GUI.Label(new Rect(24, 336, 100, 28), "参加コード", text);
                joinCode = GUI.TextField(new Rect(130, 332, 280, 32), joinCode, 16);
                GUI.enabled = previous && !busy && costsConfirmed;
                if (GUI.Button(new Rect(24, 382, 280, 40), "部屋を作る")) _ = ConnectAsync(true);
                if (GUI.Button(new Rect(324, 382, 292, 40), "コードで参加")) _ = ConnectAsync(false);
            }
            GUI.enabled = previous && !busy;
            if (GUI.Button(new Rect(180, 438, 280, 40), "タイトルへ戻る")) { pageOpen = false; costsConfirmed = false; }
        }
        GUI.enabled = previous;
    }

    private void OnDestroy()
    {
        destroyed = true;
        Exploring = false;
        ISession previous = session;
        session = null;
        DestroyNetworkManager();
        if (previous != null) _ = LeaveOnDestroyAsync(previous);
    }

    private static async Task LeaveOnDestroyAsync(ISession previous)
    {
        try { await previous.LeaveAsync(); }
        catch (Exception exception) { Debug.LogWarning("セッション終了: " + exception.Message); }
    }
}
