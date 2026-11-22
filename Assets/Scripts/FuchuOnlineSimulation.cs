using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Simulation inputs shared by a Relay host and a player-free dedicated server.</summary>
internal sealed class FuchuOnlineSimulation
{
    internal readonly Transform Root;
    internal readonly Vector3 Center, Spawn;
    internal readonly float WalkingSpeed, SprintSpeed;
    internal readonly FuchuWalkController Player;
    internal readonly bool Dedicated;
    internal readonly List<Vector3> RecoveryLocations;
    internal readonly FuchuResidentSpawner.TriangleNode[] Graph;
    internal readonly List<List<int>> Loops;
    internal NetworkManager Network;
    internal double ServerTime => Network != null ? Network.ServerTime.Time : 0;

    internal FuchuOnlineSimulation(FuchuWalkController player)
    {
        Player = player; Root = player.transform.parent; Spawn = player.transform.localPosition;
        Center = player.BuildingEffects.PlayableCenter;
        WalkingSpeed = player.WalkingSpeed; SprintSpeed = player.BaseSprintSpeed;
        RecoveryLocations = player.RecoveryItems.CopyRoadLocations();
        Graph = player.Battle.OnlineGraph; Loops = player.Battle.OnlineRoutes;
    }
    internal FuchuOnlineSimulation(Transform root, MeshFilter road, Vector3 center, float walkingSpeed, float sprintSpeed)
    {
        Dedicated = true; Root = root; Center = center; WalkingSpeed = walkingSpeed; SprintSpeed = sprintSpeed;
        if (road == null || road.sharedMesh == null || !road.sharedMesh.isReadable)
            throw new InvalidOperationException("専用サーバーには読み取り可能な道路メッシュが必要です。");
        var travel = new List<FuchuResidentSpawner.TriangleNode>();
        var boundary = FuchuResidentSpawner.BuildSidewalkGraph(road.sharedMesh, road.transform, root, center, 995f,
            weldConnections: true, travelNodes: travel);
        Loops = FuchuBattleNpc.FindLoops(boundary);
        Graph = travel.Count > 0 ? travel.ToArray() : boundary;
        if (Graph == null || Graph.Length == 0) throw new InvalidOperationException("道路経路を生成できません。");
        RecoveryLocations = new List<Vector3>(2000);
        var cells = new HashSet<(int, int)>();
        float nearest = float.PositiveInfinity;
        Vector3 spawn = center;
        // The server uses only baked road geometry, without streamed buildings or rendered recovery cubes.
        for (int i = 0; i < Graph.Length; i++)
        {
            Vector3 point = Graph[i].Center;
            float distance = new Vector2(point.x - center.x, point.z - center.z).sqrMagnitude;
            if (Graph[i].Neighbors.Count == 0 || distance > 990f * 990f) continue;
            if (distance < nearest) { nearest = distance; spawn = point + Vector3.up * 0.03f; }
        }
        Spawn = spawn;
        if (float.IsInfinity(nearest)) throw new InvalidOperationException("開始地点となる道路がありません。");
        // Spread candidates across the whole graph, rather than taking the first neighborhood only.
        int stride = Math.Max(1, Graph.Length / 2000);
        for (int pass = 0; pass < stride && RecoveryLocations.Count < 2000; pass++)
            for (int i = pass; i < Graph.Length && RecoveryLocations.Count < 2000; i += stride)
            {
                Vector3 point = Graph[i].Center;
                if (Graph[i].Neighbors.Count == 0 || new Vector2(point.x - center.x, point.z - center.z).sqrMagnitude > 990f * 990f) continue;
                if (cells.Add((Mathf.FloorToInt(point.x / 3f), Mathf.FloorToInt(point.z / 3f)))) RecoveryLocations.Add(point);
            }
        if (RecoveryLocations.Count < 300) throw new InvalidOperationException("回復アイテムの道路配置候補が不足しています。");
    }
    internal FuchuBattleNpc CreateNpc(int slot, Color color, FuchuOnlineTerritoryState state, List<Vector3> starts)
    {
        var obj = new GameObject("Online Authority NPC " + slot);
        obj.transform.SetParent(Root, false);
        var bot = obj.AddComponent<FuchuBattleNpc>();
        bot.InitializeOnline(this, new FuchuBattleMatch.Participant { Id = slot, Name = "NPC " + slot, Color = color }, state, starts);
        return bot;
    }
}
