using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Reliable state transfer and host-only walking/ownership decisions, separate from session UI.</summary>
internal sealed class FuchuOnlineTerritorySync : IDisposable
{
    private const string StateMessage = "Fuchu.Territory.State.v5";
    private const string TrailMessage = "Fuchu.Territory.Trail.v5";
    private const string RequestMessage = "Fuchu.Territory.Request.v5";
    private const string GameplayMessage = "Fuchu.Gameplay.v5";
    private const int ChunkSize = 4096;
    private const int MaxCompressed = 2 * 1024 * 1024;
    private readonly NetworkManager network;
    private readonly FuchuWalkController player;
    private readonly FuchuOnlineSimulation simulation;
    private readonly Color[] colors;
    private readonly FuchuOnlineBuildingCatalog catalog;
    private readonly Vector2 center;
    private readonly Dictionary<ulong, float> requests = new Dictionary<ulong, float>();
    private readonly uint offlineSeed;
    private FuchuOnlineTerritoryView view;
    private bool registered;
    private bool started;
    private bool forceFull = true;
    private int sentRevision = -1;
    private byte[] outgoing;
    private int outgoingOffset;
    private uint outgoingTransfer;
    private uint incomingTransfer;
    private uint appliedTransfer;
    private byte[] incoming;
    private int incomingOffset;
    private float incomingAt;
    private float nextRequest;
    private float nextTrail;
    private float nextGameplay;
    private float nextRespawn;
    private readonly uint[] sentItemVersions = new uint[300];
    private readonly List<int> changedItems = new List<int>(300);
    private readonly List<Vector3> roadLocations;
    private readonly FuchuOnlineTerritoryState.Participant[] receivedGrowth = new FuchuOnlineTerritoryState.Participant[5];
    internal bool Ready { get; private set; }
    internal FuchuOnlineTerritoryState State { get; private set; }

    internal FuchuOnlineTerritorySync(NetworkManager manager, FuchuOnlineSimulation context,
        FuchuOnlineBuildingCatalog buildings, Color[] palette)
    {
        network = manager; simulation = context; player = context.Player; catalog = buildings; colors = palette;
        Vector3 origin = context.Center;
        center = new Vector2(origin.x, origin.z);
        offlineSeed = player != null ? player.BuildingEffects.Seed : 0;
        for (int i = 0; i < receivedGrowth.Length; i++) receivedGrowth[i] = new FuchuOnlineTerritoryState.Participant();
        roadLocations = context.RecoveryLocations;
        if (roadLocations.Count == 0) throw new InvalidOperationException("回復アイテムの道路配置データがありません。");
        State = new FuchuOnlineTerritoryState(catalog, center, (uint)UnityEngine.Random.Range(1, int.MaxValue));
    }
    internal void Register()
    {
        if (registered) return;
        network.CustomMessagingManager.RegisterNamedMessageHandler(StateMessage, ReceiveState);
        network.CustomMessagingManager.RegisterNamedMessageHandler(TrailMessage, ReceiveTrail);
        network.CustomMessagingManager.RegisterNamedMessageHandler(RequestMessage, ReceiveRequest);
        network.CustomMessagingManager.RegisterNamedMessageHandler(GameplayMessage, ReceiveGameplay);
        registered = true;
    }
    internal void StartView()
    {
        if (network.IsServer) State.InitializeRecoveryItems(roadLocations);
        started = true;
        Ready = network.IsServer;
        if (!simulation.Dedicated)
        {
            var obj = new GameObject("Online Territory Projection");
            obj.transform.SetParent(simulation.Root, false);
            view = obj.AddComponent<FuchuOnlineTerritoryView>();
            view.Initialize(player, State, colors);
            if (Ready) player.BuildingEffects.SetSessionSeed(State.Seed);
        }
        nextRequest = 0f;
    }
    internal void Bind(byte slot, ulong peer)
    {
        if (!network.IsServer) return;
        State.Bind(slot, peer);
        forceFull = true;
    }
    internal void BindNpc(byte slot)
    {
        State.Bind(slot, FuchuOnlineTerritoryState.NpcPeer(slot), true); forceFull = true;
    }
    internal void Release(byte slot)
    {
        if (!network.IsServer) return;
        State.Release(slot);
        forceFull = true;
    }
    internal void Record(byte slot, Vector3 position, bool reset, bool sprint, double elapsed, uint acknowledgedPose)
    {
        if (!network.IsServer) return;
        State.TickMatch(network.ServerTime.Time);
        State.Record(slot, new Vector2(position.x, position.z), reset);
        State.ProcessMovement(slot, position, sprint, elapsed, reset, acknowledgedPose, simulation.SprintSpeed, network.ServerTime.Time < State.Players[slot].SlowUntil ? 0.5f : 1f);
    }
    internal void Tick()
    {
        if (!started) return;
        if (!network.IsServer)
        {
            if ((!Ready || incoming != null && Time.unscaledTime - incomingAt > 5f) && Time.unscaledTime >= nextRequest)
            {
                incoming = null;
                incomingOffset = 0;
                using (var writer = new FastBufferWriter(1, Allocator.Temp))
                {
                    writer.WriteValueSafe((byte)5);
                    network.CustomMessagingManager.SendNamedMessage(RequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
                }
                nextRequest = Time.unscaledTime + 2f;
            }
            return;
        }
        State.TickMatch(network.ServerTime.Time);
        if (State.Playing && Time.unscaledTime >= nextRespawn)
        {
            nextRespawn = Time.unscaledTime + 0.5f;
            State.RetryRecoveryRespawns();
        }
        if (outgoing == null && (forceFull || sentRevision != State.Revision))
        {
            if (network.ConnectedClientsIds.Count <= (network.IsHost ? 1 : 0))
            {
                sentRevision = State.Revision; forceFull = false; State.TrailEvents.Clear();
            }
            else
            {
                using (var stream = new MemoryStream())
                {
                    byte[] state = State.Serialize();
                    for (int i = 0; i < State.Items.Count; i++) sentItemVersions[i] = State.Items[i].Version;
                    using (var compressor = new DeflateStream(stream, System.IO.Compression.CompressionLevel.Fastest, true)) compressor.Write(state, 0, state.Length);
                    outgoing = stream.ToArray();
                }
                if (outgoing.Length > MaxCompressed) throw new InvalidOperationException("領域の通信データが上限を超えました。");
                outgoingTransfer++;
                outgoingOffset = 0;
                sentRevision = State.Revision;
                forceFull = false;
                // These trail points are already included in the full snapshot.
                State.TrailEvents.Clear();
            }
        }
        if (outgoing != null)
        {
            // At most 16 KiB per peer per frame; do not enqueue the whole city in one frame.
            for (int part = 0; part < 4 && outgoing != null; part++)
            {
                int count = Mathf.Min(ChunkSize, outgoing.Length - outgoingOffset);
                using (var writer = new FastBufferWriter(ChunkSize + 16, Allocator.Temp))
                {
                    writer.WriteValueSafe(outgoingTransfer);
                    writer.WriteValueSafe(outgoing.Length);
                    writer.WriteValueSafe(outgoingOffset);
                    writer.WriteValueSafe(count);
                    writer.WriteBytesSafe(outgoing, count, outgoingOffset);
                    Broadcast(StateMessage, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }
                outgoingOffset += count;
                if (outgoingOffset == outgoing.Length) outgoing = null;
            }
            return;
        }
        // Gameplay deltas follow the full state on the same reliable ordered channel.
        if (sentRevision != State.Revision || forceFull) return;
        if (Time.unscaledTime >= nextGameplay)
        {
            nextGameplay = Time.unscaledTime + 0.1f;
            SendGameplay();
        }
        if (Time.unscaledTime < nextTrail || State.TrailEvents.Count == 0) return;
        nextTrail = Time.unscaledTime + 0.05f;
        int events = Mathf.Min(128, State.TrailEvents.Count);
        using (var writer = new FastBufferWriter(4096, Allocator.Temp))
        {
            writer.WriteValueSafe(State.Revision);
            writer.WriteValueSafe((ushort)events);
            for (int i = 0; i < events; i++)
            {
                var step = State.TrailEvents[i];
                writer.WriteValueSafe(step.Slot);
                writer.WriteValueSafe(step.Generation);
                writer.WriteValueSafe((byte)(step.HasPoint ? 1 : 0));
                writer.WriteValueSafe(step.Point);
            }
            Broadcast(TrailMessage, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }
        State.TrailEvents.RemoveRange(0, events);
    }
    private void Broadcast(string name, FastBufferWriter writer, NetworkDelivery delivery)
    {
        foreach (ulong id in network.ConnectedClientsIds)
            if (id != network.LocalClientId) network.CustomMessagingManager.SendNamedMessage(name, id, writer, delivery);
    }
    private void ApplyLocalGrowth()
    {
        if (player == null || !network.IsClient) return;
        int slot = State.SlotOf(network.LocalClientId);
        if (slot >= 0) player.ApplyOnlineGrowth(State.Players[slot]);
    }
    private void SendGameplay()
    {
        State.GameplaySequence++;
        changedItems.Clear();
        for (int i = 0; i < State.Items.Count; i++)
            if (State.Items[i].Version != sentItemVersions[i]) changedItems.Add(i);
        using (var stream = new MemoryStream())
        using (var binary = new BinaryWriter(stream))
        {
            binary.Write(State.Revision); binary.Write(State.GameplaySequence);
            State.WriteMatch(binary);
            for (int i = 0; i < 5; i++) FuchuOnlineTerritoryState.WriteGrowth(binary, State.Players[i]);
            binary.Write(changedItems.Count);
            foreach (int id in changedItems)
            {
                binary.Write(id); FuchuOnlineTerritoryState.WriteItem(binary, State.Items[id]);
            }
            byte[] bytes = stream.ToArray();
            using (var writer = new FastBufferWriter(bytes.Length, Allocator.Temp))
            {
                writer.WriteBytesSafe(bytes);
                Broadcast(GameplayMessage, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }
        foreach (int id in changedItems) sentItemVersions[id] = State.Items[id].Version;
        ApplyLocalGrowth();
    }
    private void ReceiveGameplay(ulong sender, FastBufferReader reader)
    {
        if (!Ready || network.IsServer || sender != NetworkManager.ServerClientId) return;
        int length = reader.Length - reader.Position;
        if (length < 382 || length > 7000) { Ready = false; nextRequest = 0; return; }
        var bytes = new byte[length]; reader.ReadBytesSafe(ref bytes, length);
        try
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var binary = new BinaryReader(stream))
            {
                int revision = binary.ReadInt32(); uint sequence = binary.ReadUInt32();
                if (revision != State.Revision) { Ready = false; nextRequest = 0; return; }
                if (sequence <= State.GameplaySequence) return;
                bool matchStarted = binary.ReadBoolean(), matchFinished = binary.ReadBoolean();
                double matchDeadline = binary.ReadDouble();
                if (double.IsNaN(matchDeadline) || double.IsInfinity(matchDeadline) || matchDeadline < 0 || matchDeadline > 1e12 ||
                    matchFinished && !matchStarted || matchStarted && matchDeadline <= 0) throw new InvalidDataException("Invalid match clock");
                for (int i = 0; i < 5; i++)
                    FuchuOnlineTerritoryState.ReadGrowth(binary, receivedGrowth[i]);
                int count = binary.ReadInt32();
                if (count < 0 || count > 300) throw new InvalidDataException("Invalid item delta count");
                var changes = new List<(int Id, FuchuOnlineTerritoryState.RecoveryItem Item)>(count);
                var seen = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    int id = binary.ReadInt32();
                    if (id < 0 || id >= State.Items.Count || !seen.Add(id)) throw new InvalidDataException("Invalid item ID");
                    var item = new FuchuOnlineTerritoryState.RecoveryItem();
                    FuchuOnlineTerritoryState.ReadItem(binary, item);
                    changes.Add((id, item));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Invalid gameplay payload");
                State.MatchStarted = matchStarted; State.MatchFinished = matchFinished; State.MatchDeadline = matchDeadline;
                for (int i = 0; i < 5; i++)
                {
                    var target = State.Players[i]; var source = receivedGrowth[i];
                    target.BuildingPoints = source.BuildingPoints; target.MovementPoints = source.MovementPoints;
                    target.ToughnessPoints = source.ToughnessPoints; target.StaminaCapacityPoints = source.StaminaCapacityPoints;
                    target.Stamina = source.Stamina; target.SprintBonus = source.SprintBonus; target.AcknowledgedPose = source.AcknowledgedPose;
                    target.SlowUntil = source.SlowUntil; target.CorrectionVersion = source.CorrectionVersion; target.CorrectionPosition = source.CorrectionPosition;
                }
                foreach (var change in changes)
                {
                    var item = State.Items[change.Id];
                    if (change.Item.Version < item.Version) continue;
                    item.Version = change.Item.Version; item.Active = change.Item.Active; item.Position = change.Item.Position;
                }
                State.GameplaySequence = sequence;
                ApplyLocalGrowth();
            }
        }
        catch (Exception exception)
        {
            Ready = false; nextRequest = 0;
            Debug.LogWarning("成長・回復の再同期を要求します: " + exception.Message);
        }
    }

    private void ReceiveRequest(ulong sender, FastBufferReader reader)
    {
        if (!network.IsServer || !network.ConnectedClients.ContainsKey(sender) || reader.Length - reader.Position != 1) return;
        reader.ReadValueSafe(out byte version);
        if (version != 5 || requests.TryGetValue(sender, out float previous) && Time.unscaledTime - previous < 2f) return;
        requests[sender] = Time.unscaledTime;
        forceFull = true;
    }
    private void ReceiveState(ulong sender, FastBufferReader reader)
    {
        if (!started || network.IsServer || sender != NetworkManager.ServerClientId || reader.Length - reader.Position < 16) return;
        reader.ReadValueSafe(out uint transfer);
        reader.ReadValueSafe(out int total);
        reader.ReadValueSafe(out int offset);
        reader.ReadValueSafe(out int count);
        if (transfer <= appliedTransfer || total <= 0 || total > MaxCompressed || count <= 0 || count > ChunkSize ||
            offset < 0 || offset > total - count || reader.Length - reader.Position != count) return;
        if (offset == 0)
        {
            if (transfer < incomingTransfer) return;
            incomingTransfer = transfer; incoming = new byte[total]; incomingOffset = 0; incomingAt = Time.unscaledTime;
        }
        if (incoming == null || transfer != incomingTransfer || total != incoming.Length || offset != incomingOffset) return;
        reader.ReadBytesSafe(ref incoming, count, offset);
        incomingOffset += count;
        incomingAt = Time.unscaledTime;
        if (incomingOffset != incoming.Length) return;
        try
        {
            using (var packed = new MemoryStream(incoming, false))
            using (var decompressor = new DeflateStream(packed, CompressionMode.Decompress))
            using (var expanded = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (expanded.Length + read > FuchuOnlineTerritoryState.MaxSnapshotBytes) throw new InvalidDataException("State too large");
                    expanded.Write(buffer, 0, read);
                }
                var next = FuchuOnlineTerritoryState.Deserialize(expanded.ToArray(), catalog, center);
                if (next.Revision < State.Revision) return;
                State = next;
                player.BuildingEffects.SetSessionSeed(next.Seed);
                view.BindState(next);
                Ready = true;
                ApplyLocalGrowth();
                appliedTransfer = transfer;
            }
        }
        catch (Exception exception)
        {
            Ready = false;
            Debug.LogWarning("領域の再同期を要求します: " + exception.Message);
        }
        finally { incoming = null; incomingOffset = 0; }
    }
    private void ReceiveTrail(ulong sender, FastBufferReader reader)
    {
        if (!Ready || network.IsServer || sender != NetworkManager.ServerClientId || reader.Length - reader.Position < 6) return;
        reader.ReadValueSafe(out int revision);
        reader.ReadValueSafe(out ushort count);
        if (count > 128 || reader.Length - reader.Position != count * 14) return;
        if (revision != State.Revision) { Ready = false; nextRequest = 0; return; }
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out byte slot);
            reader.ReadValueSafe(out uint generation);
            reader.ReadValueSafe(out byte hasPoint);
            reader.ReadValueSafe(out Vector2 point);
            if (slot >= 5 || hasPoint > 1 || float.IsNaN(point.x) || float.IsNaN(point.y) ||
                Math.Abs(point.x) > 10000 || Math.Abs(point.y) > 10000) { Ready = false; nextRequest = 0; return; }
            State.ApplyTrail(new FuchuOnlineTerritoryState.TrailEvent { Slot = slot, Generation = generation, Point = point, HasPoint = hasPoint != 0 });
        }
    }
    public void Dispose()
    {
        if (registered && network != null && network.CustomMessagingManager != null)
        {
            network.CustomMessagingManager.UnregisterNamedMessageHandler(StateMessage);
            network.CustomMessagingManager.UnregisterNamedMessageHandler(TrailMessage);
            network.CustomMessagingManager.UnregisterNamedMessageHandler(RequestMessage);
            network.CustomMessagingManager.UnregisterNamedMessageHandler(GameplayMessage);
        }
        if (view != null) UnityEngine.Object.Destroy(view.gameObject);
        if (player != null && player.BuildingEffects != null) player.BuildingEffects.SetSessionSeed(offlineSeed);
        outgoing = incoming = null;
        State.TrailEvents.Clear();
    }
}
