using System;
using System.Collections.Generic;
using System.IO;
using Clipper2Lib;
using UnityEngine;

/// <summary>Host-owned game state. No cameras, tile streaming, mesh rendering or service APIs.</summary>
internal sealed class FuchuOnlineTerritoryState
{
    internal const int PlayerCount = 5;
    internal const double CountdownSeconds = 3;
    internal const double MatchSeconds = 300;
    internal const int MaxTrailPoints = 2048;
    internal const int MaxSnapshotBytes = 4 * 1024 * 1024;
    internal sealed class Participant
    {
        internal bool Active;
        internal bool IsNpc;
        internal ulong Peer;
        internal readonly FuchuTerritoryShape Shape = new FuchuTerritoryShape();
        internal readonly List<Vector2> Trail = new List<Vector2>();
        internal uint TrailGeneration;
        internal int ClaimOrder;
        internal double BuildingPoints, MovementPoints, ToughnessPoints, StaminaCapacityPoints;
        internal double Stamina = FuchuWalkController.BaseStamina;
        internal float SprintBonus;
        internal uint AcknowledgedPose;
        internal Vector3 Position;
        internal bool HasPosition;
        internal Vector3 SpawnPosition, CorrectionPosition;
        internal uint CorrectionVersion;
        internal double SlowUntil;
        internal double MaxStamina => FuchuWalkController.BaseStamina + StaminaCapacityPoints;
        internal int SpeedLevel => 1 + (int)Math.Min(30.0, Math.Floor(MovementPoints / FuchuWalkController.PointsPerSprintBonus));
        internal int ToughnessLevel => 1 + (int)Math.Min(5.0, Math.Floor(ToughnessPoints / 500.0));
    }
    internal struct TrailEvent
    {
        internal byte Slot;
        internal uint Generation;
        internal Vector2 Point;
        internal bool HasPoint;
    }
    internal sealed class RecoveryItem
    {
        internal Vector3 Position;
        internal bool Active;
        internal uint Version;
        internal double Fraction;
        internal float Size;
    }
    internal readonly List<RecoveryItem> Items = new List<RecoveryItem>(300);
    internal uint GameplaySequence;
    internal bool MatchStarted, MatchFinished;
    internal double MatchStartAt;
    internal double MatchDeadline;
    internal bool Playing => MatchStarted && !MatchFinished;
    internal void StartMatch(double serverTime)
    {
        if (MatchStartAt > 0) return;
        MatchStartAt = serverTime + CountdownSeconds;
        MatchDeadline = MatchStartAt + MatchSeconds;
        Revision++;
    }
    internal void TickMatch(double serverTime)
    {
        if (MatchStartAt <= 0 || serverTime < MatchStartAt || MatchFinished) return;
        MatchStarted = true;
        if (serverTime < MatchDeadline) return;
        MatchFinished = true;
        for (int i = 0; i < PlayerCount; i++) ClearTrail(i);
        Revision++;
    }
    internal void ClaimNpcLoop(int slot, List<Vector2> polygon)
    {
        if (!Playing || !Players[slot].IsNpc) return;
        if (Players[slot].Shape.AdditionalSquareMeters(polygon) >= 2.0 && Players[slot].Shape.TryClaimLoop(polygon, out _))
            ApplyClaim(slot);
        ClearTrail(slot);
    }
    private void ApplyClaim(int slot)
    {
        Paths64 claim = Players[slot].Shape.LastClaim;
        for (int i = 0; i < Players.Length; i++) if (i != slot) Players[i].Shape.Remove(claim);
        Revision++; Players[slot].ClaimOrder = Revision; UpdateOwners();
    }
    internal static ulong NpcPeer(int slot) => ulong.MaxValue - (ulong)slot;

    private List<Vector3> recoveryLocations;
    private System.Random recoveryRandom;
    internal readonly Participant[] Players = new Participant[PlayerCount];
    internal readonly List<TrailEvent> TrailEvents = new List<TrailEvent>();
    internal readonly FuchuOnlineBuildingCatalog Catalog;
    internal readonly byte[] Owners;
    internal Vector2 Center { get; }
    internal int Revision { get; private set; }
    internal uint Seed { get; private set; }

    internal FuchuOnlineTerritoryState(FuchuOnlineBuildingCatalog catalog, Vector2 center, uint seed)
    {
        Catalog = catalog; Center = center; Seed = seed;
        Owners = new byte[catalog.Entries.Count];
        for (int i = 0; i < Players.Length; i++) Players[i] = new Participant();
        for (int i = 0; i < Owners.Length; i++) Owners[i] = 255;
    }

    internal FuchuOnlineTerritoryState CreateNextRound() =>
        new FuchuOnlineTerritoryState(Catalog, Center, Seed) { Revision = Revision + 1 };

    internal void Bind(int slot, ulong peer, bool npc = false)
    {
        Participant p = Players[slot];
        p.Active = true; p.IsNpc = npc; p.Peer = peer; p.ClaimOrder = 0;
        p.CorrectionVersion = 0; p.SlowUntil = 0;
        p.Stamina = FuchuWalkController.BaseStamina; p.SprintBonus = 0f; p.AcknowledgedPose = 0; p.HasPosition = false;
        p.Shape.ReplaceFromAuthority(new Paths64());
        ClearTrail(slot);
        Revision++;
        UpdateOwners();
    }
    internal void Release(int slot)
    {
        Players[slot].Active = false;
        Players[slot].Shape.ReplaceFromAuthority(new Paths64());
        ClearTrail(slot);
        Revision++;
        UpdateOwners();
    }
    internal int OwnerOf(string id) => Catalog.Index.TryGetValue(id, out int index) && Owners[index] != 255 ? Owners[index] : -1;
    internal int SlotOf(ulong peer)
    {
        for (int i = 0; i < Players.Length; i++) if (Players[i].Active && !Players[i].IsNpc && Players[i].Peer == peer) return i;
        return -1;
    }
    internal int OwnedBuildings(int slot)
    {
        int count = 0;
        foreach (byte owner in Owners) if (owner == slot) count++;
        return count;
    }
    internal void ClearTrail(int slot)
    {
        Participant p = Players[slot];
        p.Trail.Clear(); p.Shape.ClearOpenTrail(); p.TrailGeneration++;
        TrailEvents.Add(new TrailEvent { Slot = (byte)slot, Generation = p.TrailGeneration });
    }
    internal void Record(int slot, Vector2 point, bool reset = false, bool evaluateClaims = true)
    {
        Participant p = Players[slot];
        if (!p.Active || !Playing) return;
        if (reset || (point - Center).sqrMagnitude > 1000000f || p.Trail.Count >= MaxTrailPoints) ClearTrail(slot);
        if ((point - Center).sqrMagnitude > 1000000f) return;
        if (p.Trail.Count > 0 && (p.Trail[p.Trail.Count - 1] - point).sqrMagnitude < 0.09f) return;
        if (evaluateClaims && p.Shape.TryClaimWalkStep(p.Trail, point, 0.6f, out _))
        {
            ApplyClaim(slot);
            ClearTrail(slot);
        }
        p.Trail.Add(point);
        TrailEvents.Add(new TrailEvent { Slot = (byte)slot, Generation = p.TrailGeneration, Point = point, HasPoint = true });
    }
    internal void ApplyTrail(TrailEvent step)
    {
        Participant p = Players[step.Slot];
        if (step.Generation < p.TrailGeneration) return;
        if (step.Generation != p.TrailGeneration || !step.HasPoint) p.Trail.Clear();
        p.TrailGeneration = step.Generation;
        if (step.HasPoint && p.Trail.Count < MaxTrailPoints) p.Trail.Add(step.Point);
    }
    private void UpdateOwners()
    {
        for (int b = 0; b < Catalog.Entries.Count; b++)
        {
            Owners[b] = 255;
            int order = -1;
            for (int p = 0; p < Players.Length; p++)
                if (Players[p].Active && Players[p].ClaimOrder > order && Players[p].Shape.Contains(Catalog.Entries[b].Position))
                { Owners[b] = (byte)p; order = Players[p].ClaimOrder; }
        }
        RecalculateGrowth();
    }

    private void RecalculateGrowth()
    {
        foreach (Participant p in Players)
            p.BuildingPoints = p.MovementPoints = p.ToughnessPoints = p.StaminaCapacityPoints = 0.0;
        for (int i = 0; i < Owners.Length; i++)
        {
            if (Owners[i] == 255) continue;
            Participant p = Players[Owners[i]];
            var building = Catalog.Entries[i];
            p.BuildingPoints += building.Points;
            switch (FuchuBuildingEffects.GetEffect(Seed, building.Id))
            {
                case FuchuBuildingEffects.EffectKind.Movement: p.MovementPoints += building.Points / 10.0; break;
                case FuchuBuildingEffects.EffectKind.Toughness: p.ToughnessPoints += building.Points / 10.0; break;
                case FuchuBuildingEffects.EffectKind.StaminaCapacity: p.StaminaCapacityPoints += building.Points; break;
            }
        }
        foreach (Participant p in Players)
        {
            p.Stamina = Math.Min(p.Stamina, p.MaxStamina);
            p.SprintBonus = Mathf.Min(p.SprintBonus, p.SpeedLevel - 1);
        }
    }

    internal void InitializeRecoveryItems(List<Vector3> locations)
    {
        if (locations == null || locations.Count == 0) throw new InvalidOperationException("回復アイテムの道路配置データがありません。");
        recoveryLocations = locations;
        recoveryRandom = new System.Random(unchecked((int)Seed));
        Items.Clear();
        for (int i = 0; i < 300; i++)
        {
            var item = new RecoveryItem { Fraction = i < 180 ? 0.1 : i < 270 ? 0.5 : 1.0, Size = i < 180 ? 0.45f : i < 270 ? 0.8f : 1.2f };
            Items.Add(item);
            Respawn(item);
        }
    }
    private void Respawn(RecoveryItem item)
    {
        int start = recoveryRandom.Next(recoveryLocations.Count);
        for (int i = 0; i < recoveryLocations.Count; i++)
        {
            Vector3 point = recoveryLocations[(start + i) % recoveryLocations.Count];
            if (item.Version > 0 && (point - item.Position).sqrMagnitude < 100f) continue;
            bool occupied = false;
            foreach (Participant p in Players)
                if (p.Active && p.HasPosition && (point - p.Position).sqrMagnitude < 100f) { occupied = true; break; }
            if (occupied) continue;
            foreach (RecoveryItem other in Items)
                if (other != item && other.Active && (point - other.Position).sqrMagnitude < 16f) { occupied = true; break; }
            if (occupied) continue;
            item.Position = point; item.Active = true; item.Version++;
            return;
        }
    }
    internal void RetryRecoveryRespawns()
    {
        if (recoveryLocations == null) return;
        foreach (RecoveryItem item in Items) if (!item.Active) Respawn(item);
    }
    internal void ProcessMovement(int slot, Vector3 position, bool sprint, double elapsed, bool reset, uint acknowledgedPose, float baseSprintSpeed, float speedMultiplier = 1f)
    {
        Participant p = Players[slot];
        if (!p.Active) return;
        if (!p.HasPosition) p.SpawnPosition = position;
        Vector3 previous = p.HasPosition && !reset ? p.Position : position;
        Vector3 movement = position - previous;
        Vector3 horizontal = new Vector3(movement.x, 0f, movement.z);
        double time = Math.Min(2.0, Math.Max(0.0, elapsed));
        time = Math.Min(time, horizontal.magnitude / Math.Max(0.1, (baseSprintSpeed + p.SprintBonus) * speedMultiplier));
        bool running = Playing && !reset && sprint && horizontal.magnitude > 0.01f && p.Stamina > 0.0;
        if (running)
        {
            p.SprintBonus = Mathf.MoveTowards(p.SprintBonus, p.SpeedLevel - 1, (float)time * FuchuWalkController.SprintBonusPerSecond);
            p.Stamina = Math.Max(0.0, p.Stamina - FuchuWalkController.SprintStaminaPerSecond * time);
        }
        else p.SprintBonus = 0f;
        p.Position = position; p.HasPosition = true; p.AcknowledgedPose = acknowledgedPose;
        if (!Playing) return;
        // One host processes all contacts. There is no client-supplied item ID or recovery amount.
        Vector3 from = previous + Vector3.up * 0.9f, to = position + Vector3.up * 0.9f;
        Vector3 contactMovement = to - from;
        foreach (RecoveryItem item in Items)
        {
            if (!item.Active) continue;
            var bounds = new Bounds(item.Position + Vector3.up * (item.Size * 0.5f + 0.15f), Vector3.one * item.Size);
            bounds.Expand(new Vector3(0.7f, 1.8f, 0.7f));
            bool touched = bounds.Contains(from) || bounds.Contains(to);
            if (!touched && contactMovement.sqrMagnitude > 0.000001f)
                touched = bounds.IntersectRay(new Ray(from, contactMovement.normalized), out float distance) && distance <= contactMovement.magnitude;
            if (!touched) continue;
            item.Active = false; item.Version++;
            p.Stamina = Math.Min(p.MaxStamina, p.Stamina + p.MaxStamina * item.Fraction);
            Respawn(item);
        }
    }

    internal static void WriteGrowth(BinaryWriter writer, Participant p)
    {
        writer.Write(p.BuildingPoints); writer.Write(p.MovementPoints); writer.Write(p.ToughnessPoints); writer.Write(p.StaminaCapacityPoints);
        writer.Write(p.Stamina); writer.Write(p.SprintBonus); writer.Write(p.AcknowledgedPose);
        writer.Write(p.SlowUntil); writer.Write(p.CorrectionVersion);
        writer.Write(p.CorrectionPosition.x); writer.Write(p.CorrectionPosition.y); writer.Write(p.CorrectionPosition.z);
    }
    internal static void ReadGrowth(BinaryReader reader, Participant p)
    {
        p.BuildingPoints = ReadPositive(reader); p.MovementPoints = ReadPositive(reader);
        p.ToughnessPoints = ReadPositive(reader); p.StaminaCapacityPoints = ReadPositive(reader);
        p.Stamina = ReadPositive(reader); p.SprintBonus = reader.ReadSingle(); p.AcknowledgedPose = reader.ReadUInt32();
        p.SlowUntil = ReadPositive(reader); p.CorrectionVersion = reader.ReadUInt32();
        p.CorrectionPosition = ReadPosition(reader);
        if (p.Stamina > p.MaxStamina || float.IsNaN(p.SprintBonus) || p.SprintBonus < 0 || p.SprintBonus > p.SpeedLevel - 1)
            throw new InvalidDataException("Invalid growth");
    }
    private static double ReadPositive(BinaryReader reader)
    {
        double value = reader.ReadDouble();
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1e12) throw new InvalidDataException("Invalid points");
        return value;
    }

    internal byte[] Serialize()
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(FuchuOnlineConnection.ProtocolVersion); writer.Write(Revision); writer.Write(Seed); writer.Write(GameplaySequence);
            WriteMatch(writer);
            writer.Write(Catalog.Hash);
            for (int slot = 0; slot < Players.Length; slot++)
            {
                Participant p = Players[slot];
                writer.Write(p.Active); writer.Write(p.IsNpc); writer.Write(p.Peer); writer.Write(p.TrailGeneration);
                WriteGrowth(writer, p);
                Paths64 paths = p.Shape.CopyPaths();
                writer.Write(paths.Count);
                foreach (Path64 path in paths)
                {
                    writer.Write(path.Count);
                    foreach (Point64 point in path) { writer.Write(point.X); writer.Write(point.Y); }
                }
                writer.Write(p.Trail.Count);
                foreach (Vector2 point in p.Trail) { writer.Write(point.x); writer.Write(point.y); }
            }
            writer.Write(Owners.Length); writer.Write(Owners);
            writer.Write(Items.Count);
            foreach (RecoveryItem item in Items) WriteItem(writer, item);
            if (stream.Length > MaxSnapshotBytes) throw new InvalidOperationException("領域データが同期上限を超えました。");
            return stream.ToArray();
        }
    }
    internal static FuchuOnlineTerritoryState Deserialize(byte[] bytes, FuchuOnlineBuildingCatalog catalog, Vector2 center)
    {
        if (bytes.Length > MaxSnapshotBytes) throw new InvalidDataException("State is too large");
        var state = new FuchuOnlineTerritoryState(catalog, center, 0);
        using (var stream = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(stream))
        {
            if (reader.ReadInt32() != FuchuOnlineConnection.ProtocolVersion) throw new InvalidDataException("Protocol mismatch");
            state.Revision = reader.ReadInt32();
            if (state.Revision < 0) throw new InvalidDataException("Invalid revision");
            state.Seed = reader.ReadUInt32(); state.GameplaySequence = reader.ReadUInt32();
            ReadMatch(reader, out state.MatchStarted, out state.MatchFinished, out state.MatchStartAt, out state.MatchDeadline);
            if (reader.ReadString() != catalog.Hash) throw new InvalidDataException("Building catalogue mismatch");
            int total = 0;
            for (int i = 0; i < PlayerCount; i++)
            {
                Participant p = state.Players[i];
                p.Active = reader.ReadBoolean(); p.IsNpc = reader.ReadBoolean(); p.Peer = reader.ReadUInt64(); p.TrailGeneration = reader.ReadUInt32();
                ReadGrowth(reader, p);
                int contours = ReadCount(reader, 8192);
                var paths = new Paths64(contours);
                for (int j = 0; j < contours; j++)
                {
                    int vertices = ReadCount(reader, 65536); total += vertices;
                    if (total > 150000) throw new InvalidDataException("Too many vertices");
                    var path = new Path64(vertices);
                    for (int k = 0; k < vertices; k++)
                    {
                        long x = reader.ReadInt64(), y = reader.ReadInt64();
                        if (Math.Abs((double)x) > 10000000 || Math.Abs((double)y) > 10000000) throw new InvalidDataException("Invalid point");
                        path.Add(new Point64(x, y));
                    }
                    paths.Add(path);
                }
                p.Shape.ReplaceFromAuthority(paths);
                int trail = ReadCount(reader, MaxTrailPoints);
                for (int j = 0; j < trail; j++)
                {
                    float x = reader.ReadSingle(), y = reader.ReadSingle();
                    if (float.IsNaN(x) || float.IsNaN(y) || Math.Abs(x) > 10000 || Math.Abs(y) > 10000) throw new InvalidDataException("Invalid trail");
                    p.Trail.Add(new Vector2(x, y));
                }
            }
            if (reader.ReadInt32() != state.Owners.Length) throw new InvalidDataException("Invalid building count");
            for (int i = 0; i < state.Owners.Length; i++)
            {
                byte owner = reader.ReadByte();
                if (owner != 255 && (owner >= PlayerCount || !state.Players[owner].Active)) throw new InvalidDataException("Invalid owner");
                state.Owners[i] = owner;
            }
            int itemCount = ReadCount(reader, 300);
            if (itemCount != 300) throw new InvalidDataException("Invalid item count");
            for (int i = 0; i < itemCount; i++)
            {
                var item = new RecoveryItem { Fraction = i < 180 ? 0.1 : i < 270 ? 0.5 : 1.0, Size = i < 180 ? 0.45f : i < 270 ? 0.8f : 1.2f };
                ReadItem(reader, item);
                state.Items.Add(item);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes");
        }
        return state;
    }
    internal static void WriteItem(BinaryWriter writer, RecoveryItem item)
    {
        writer.Write(item.Version); writer.Write(item.Active);
        writer.Write(item.Position.x); writer.Write(item.Position.y); writer.Write(item.Position.z);
    }
    internal static void ReadItem(BinaryReader reader, RecoveryItem item)
    {
        item.Version = reader.ReadUInt32(); item.Active = reader.ReadBoolean();
        float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || Math.Abs(x) > 10000 || Math.Abs(y) > 1000 || Math.Abs(z) > 10000)
            throw new InvalidDataException("Invalid item position");
        item.Position = new Vector3(x, y, z);
    }

    internal void WriteMatch(BinaryWriter writer)
    {
        writer.Write(MatchStarted); writer.Write(MatchFinished); writer.Write(MatchStartAt); writer.Write(MatchDeadline);
    }
    internal static void ReadMatch(BinaryReader reader, out bool started, out bool finished, out double startAt, out double deadline)
    {
        started = reader.ReadBoolean();
        finished = reader.ReadBoolean();
        startAt = ReadPositive(reader);
        deadline = ReadPositive(reader);
        if (finished && !started || startAt == 0 && (started || deadline != 0) ||
            startAt > 0 && deadline != startAt + MatchSeconds) throw new InvalidDataException("Invalid match clock");
    }
    internal static Vector3 ReadPosition(BinaryReader reader)
    {
        float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || Math.Abs(x) > 10000 || Math.Abs(y) > 1000 || Math.Abs(z) > 10000)
            throw new InvalidDataException("Invalid position");
        return new Vector3(x, y, z);
    }
    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidDataException("Invalid count");
        return value;
    }
}
