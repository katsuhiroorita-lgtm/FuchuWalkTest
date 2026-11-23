using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Host simulation of road actors and NPCs; clients only interpolate their projections.</summary>
internal sealed class FuchuOnlineMatch : IDisposable
{
    private const string WorldMessage = "Fuchu.World.v6";
    internal sealed class Actor
    {
        internal readonly Transform Root, Visual;
        internal readonly byte Kind;
        internal readonly Vector3 Size;
        internal readonly bool WasActive;
        internal Vector3 Previous;
        internal byte Touching;
        internal Actor(Transform root, Transform visual, byte kind, Vector3 size)
        {
            Root = root; Visual = visual; Kind = kind; Size = size;
            WasActive = root.gameObject.activeSelf; Previous = root.position;
        }
    }
    private sealed class Projection
    {
        internal Transform Root;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal uint Sequence;
        internal byte Kind;
        internal bool Active;
    }
    private readonly FuchuOnlineSimulation simulation;
    private readonly NetworkManager network;
    private readonly FuchuOnlineTerritorySync sync;
    private readonly Color[] colors;
    private readonly List<Actor> actors = new List<Actor>();
    private readonly Dictionary<byte, Actor> templates = new Dictionary<byte, Actor>();
    private readonly Dictionary<ushort, Projection> projections = new Dictionary<ushort, Projection>();
    private readonly List<Behaviour> sources = new List<Behaviour>();
    private readonly FuchuBattleNpc[] bots = new FuchuBattleNpc[5];
    private readonly double[] nextCarHit = new double[5];
    private readonly Vector3[] previousPlayers = new Vector3[5];
    private readonly bool[] hadPlayer = new bool[5];
    private float nextWorldSend;
    private uint worldSequence;
    private bool wasPlaying;
    internal int NpcCount { get { int n = 0; foreach (var bot in bots) if (bot != null) n++; return n; } }
    internal FuchuBattleNpc Bot(int slot) => bots[slot];

    internal FuchuOnlineMatch(FuchuOnlineSimulation context, NetworkManager manager, FuchuOnlineTerritorySync territory, Color[] palette)
    {
        simulation = context; network = manager; sync = territory; colors = palette;
        foreach (var traffic in UnityEngine.Object.FindObjectsByType<FuchuTrafficSpawner>(FindObjectsSortMode.None))
        { traffic.CollectOnlineActors(actors); sources.Add(traffic); }
        foreach (var residents in UnityEngine.Object.FindObjectsByType<FuchuResidentSpawner>(FindObjectsSortMode.None))
        { residents.CollectOnlineActors(actors); sources.Add(residents); }
        if (network.IsServer) foreach (var source in sources) source.enabled = false;
        if (actors.Count > 512) throw new InvalidOperationException("オンライン道路オブジェクトの上限は512個です。");
        foreach (var actor in actors)
        {
            if (!templates.ContainsKey(actor.Kind)) templates.Add(actor.Kind, actor);
            if (!network.IsServer) actor.Root.gameObject.SetActive(false);
        }
        network.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessage, ReceiveWorld);
        if (network.IsServer)
            for (int slot = 0; slot < 5; slot++) if (!sync.State.Players[slot].Active) FillSlot(slot);
    }
    internal void HumanJoined(int slot)
    {
        if (bots[slot] != null)
        {
            bots[slot].enabled = false;
            UnityEngine.Object.Destroy(bots[slot].gameObject);
            bots[slot] = null;
        }
        nextCarHit[slot] = 0; hadPlayer[slot] = false;
        foreach (var actor in actors) actor.Touching &= (byte)~(1 << slot);
    }
    internal void ResetRound()
    {
        for (int slot = 0; slot < bots.Length; slot++) HumanJoined(slot);
        foreach (var actor in actors) actor.Root.gameObject.SetActive(actor.WasActive);
    }
    internal void FillSlot(int slot)
    {
        if (!network.IsServer || sync.State.MatchFinished) return;
        HumanJoined(slot);
        sync.BindNpc((byte)slot);
        var starts = new List<Vector3> { simulation.Spawn };
        foreach (var bot in bots) if (bot != null) starts.Add(bot.transform.localPosition);
        bots[slot] = simulation.CreateNpc(slot, colors[slot], sync.State, starts);
        var owner = sync.State.Players[slot];
        owner.Position = owner.SpawnPosition = bots[slot].transform.localPosition; owner.HasPosition = true;
        nextCarHit[slot] = 0; hadPlayer[slot] = false;
    }
    internal void Tick()
    {
        if (!network.IsServer)
        {
            float blend = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            int created = 0;
            foreach (var item in projections.Values)
            {
                if (item.Root == null)
                {
                    if (created >= 8 || !templates.TryGetValue(item.Kind, out Actor template)) continue;
                    created++;
                    var obj = new GameObject("Online Road Actor");
                    obj.transform.SetParent(simulation.Root, false);
                    var visual = UnityEngine.Object.Instantiate(template.Visual.gameObject, obj.transform, false);
                    visual.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                    item.Root = obj.transform;
                    item.Root.SetLocalPositionAndRotation(item.Position, item.Rotation);
                }
                item.Root.gameObject.SetActive(item.Active);
                item.Root.localPosition = Vector3.Lerp(item.Root.localPosition, item.Position, blend);
                item.Root.localRotation = Quaternion.Slerp(item.Root.localRotation, item.Rotation, blend);
            }
            return;
        }
        bool playing = sync.State.Playing;
        if (playing != wasPlaying)
        {
            foreach (var source in sources) source.enabled = playing;
            wasPlaying = playing;
        }
        if (playing) DetectContacts();
        if (Time.unscaledTime < nextWorldSend) return;
        nextWorldSend = Time.unscaledTime + 0.1f;
        worldSequence++;
        // Independent batches fit below the transport MTU; losses repair on the next snapshot.
        for (int offset = 0; offset < actors.Count; offset += 40)
        {
            int count = Math.Min(40, actors.Count - offset);
            using (var writer = new FastBufferWriter(900, Allocator.Temp))
            {
                writer.WriteValueSafe(worldSequence); writer.WriteValueSafe((byte)count);
                for (int i = offset; i < offset + count; i++)
                {
                    var actor = actors[i];
                    writer.WriteValueSafe((ushort)i); writer.WriteValueSafe(actor.Kind);
                    writer.WriteValueSafe((byte)(actor.Root.gameObject.activeSelf ? 1 : 0));
                    writer.WriteValueSafe(simulation.Root.InverseTransformPoint(actor.Root.position));
                    writer.WriteValueSafe((Quaternion.Inverse(simulation.Root.rotation) * actor.Visual.rotation).eulerAngles.y);
                }
                foreach (ulong id in network.ConnectedClientsIds)
                    if (id != network.LocalClientId)
                        network.CustomMessagingManager.SendNamedMessage(WorldMessage, id, writer, NetworkDelivery.UnreliableSequenced);
            }
        }
    }
    private void DetectContacts()
    {
        double now = network.ServerTime.Time;
        for (int slot = 0; slot < 5; slot++)
        {
            var p = sync.State.Players[slot];
            if (!p.Active || !p.HasPosition) continue;
            Vector3 center = simulation.Root.TransformPoint(p.Position + Vector3.up * 0.9f);
            Vector3 previous = hadPlayer[slot] ? previousPlayers[slot] : center;
            if ((center - previous).sqrMagnitude > 100f) previous = center;
            foreach (var actor in actors)
            {
                if (!actor.Root.gameObject.activeSelf) continue;
                Vector3 to = actor.Visual.InverseTransformPoint(center);
                // Test relative motion so moving vehicles also sweep over stationary players.
                Vector3 from = actor.Visual.InverseTransformPoint(previous + actor.Root.position - actor.Previous);
                var contact = new Bounds(Vector3.up * actor.Size.y * 0.5f, actor.Size + new Vector3(0.7f, 1.8f, 0.7f));
                Vector3 movement = to - from;
                bool touched = contact.Contains(to) || contact.Contains(from);
                if (!touched && movement.sqrMagnitude > 0.000001f)
                    touched = contact.IntersectRay(new Ray(from, movement.normalized), out float distance) && distance <= movement.magnitude;
                byte bit = (byte)(1 << slot);
                if (!touched) { actor.Touching &= (byte)~bit; continue; }
                if (actor.Kind >= 3)
                {
                    if (p.ToughnessLevel > 1) actor.Root.gameObject.SetActive(false);
                    else if ((actor.Touching & bit) == 0)
                    { p.Stamina = Math.Max(0, p.Stamina - p.MaxStamina * 0.01); p.SlowUntil = now + 1.0; }
                    actor.Touching |= bit;
                    continue;
                }
                if (now < nextCarHit[slot]) continue;
                int required = actor.Kind + 3;
                if (p.ToughnessLevel >= required)
                {
                    actor.Root.gameObject.SetActive(false);
                    if (p.ToughnessLevel == required) p.SlowUntil = now + 1.0;
                }
                else
                {
                    nextCarHit[slot] = now + 1.5;
                    p.Position = p.CorrectionPosition = p.SpawnPosition;
                    p.CorrectionVersion++; p.SlowUntil = 0; p.SprintBonus = 0;
                    sync.State.ClearTrail(slot);
                    previous = center = simulation.Root.TransformPoint(p.Position + Vector3.up * 0.9f);
                    break;
                }
            }
            previousPlayers[slot] = center; hadPlayer[slot] = true;
        }
        foreach (var actor in actors) actor.Previous = actor.Root.position;
    }
    private void ReceiveWorld(ulong sender, FastBufferReader reader)
    {
        if (network.IsServer || sender != NetworkManager.ServerClientId || reader.Length - reader.Position < 5) return;
        reader.ReadValueSafe(out uint sequence); reader.ReadValueSafe(out byte count);
        if (count > 40 || reader.Length - reader.Position != count * 20) return;
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ushort id); reader.ReadValueSafe(out byte kind); reader.ReadValueSafe(out byte active);
            reader.ReadValueSafe(out Vector3 position); reader.ReadValueSafe(out float yaw);
            if (id >= 512 || active > 1 || !templates.TryGetValue(kind, out Actor template) ||
                !Finite(position.x) || !Finite(position.y) || !Finite(position.z) || !Finite(yaw) ||
                Mathf.Abs(position.x) > 10000 || Mathf.Abs(position.y) > 1000 || Mathf.Abs(position.z) > 10000) continue;
            if (!projections.TryGetValue(id, out Projection projection))
            {
                projection = new Projection { Kind = kind };
                projections.Add(id, projection);
            }
            if (sequence <= projection.Sequence) continue;
            projection.Sequence = sequence; projection.Position = position; projection.Rotation = Quaternion.Euler(0, yaw, 0);
            projection.Active = active != 0;
        }
    }
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    public void Dispose()
    {
        if (network != null && network.CustomMessagingManager != null)
            network.CustomMessagingManager.UnregisterNamedMessageHandler(WorldMessage);
        foreach (var bot in bots) if (bot != null) UnityEngine.Object.Destroy(bot.gameObject);
        foreach (var projection in projections.Values) if (projection.Root != null) UnityEngine.Object.Destroy(projection.Root.gameObject);
        foreach (var actor in actors) if (actor.Root != null) actor.Root.gameObject.SetActive(actor.WasActive);
        foreach (var source in sources) if (source != null) source.enabled = false;
    }
}
