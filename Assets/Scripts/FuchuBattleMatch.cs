using System;
using System.Collections.Generic;
using Clipper2Lib;
using UnityEngine;

/// <summary>Local match authority. All claims pass through here before other owners are updated.</summary>
public sealed class FuchuBattleMatch : MonoBehaviour
{
    internal const int PlayerCount = 5;
    internal sealed class Participant
    {
        internal int Id;
        internal string Name;
        internal string ColorName;
        internal Color Color;
        internal Transform Character;
        internal FuchuWalkingTerritory Territory;
        internal double MovementPoints;
        internal double StaminaCapacityPoints;
    }

    private readonly List<Participant> players = new List<Participant>(PlayerCount);
    private readonly List<FuchuBattleNpc> bots = new List<FuchuBattleNpc>();
    private readonly Dictionary<string, Vector2> buildingCenters = new Dictionary<string, Vector2>(StringComparer.Ordinal);
    private FuchuWalkController localPlayer;
    internal IReadOnlyList<Participant> Players => players;
    internal Material[] BuildingMaterials { get; private set; }
    internal int Version { get; private set; }

    internal FuchuWalkingTerritory Initialize(FuchuWalkController controller, MeshRenderer localVisual)
    {
        localPlayer = controller;
        var colors = new[] { new Color(0.95f, 0.12f, 0.12f), new Color(0.08f, 0.42f, 1f),
            new Color(1f, 0.85f, 0.08f), new Color(0.1f, 0.8f, 0.25f), new Color(0.65f, 0.18f, 0.95f) };
        var names = new[] { "赤", "青", "黄", "緑", "紫" };
        for (int i = colors.Length - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (colors[i], colors[j]) = (colors[j], colors[i]);
            (names[i], names[j]) = (names[j], names[i]);
        }
        BuildingMaterials = new Material[PlayerCount];
        Shader shader = Resources.Load<Shader>("FuchuTerritoryColor");
        for (int i = 0; i < PlayerCount; i++)
        {
            BuildingMaterials[i] = new Material(shader) { color = colors[i], renderQueue = 2010 };
            BuildingMaterials[i].SetFloat("_ZWrite", 1f);
            BuildingMaterials[i].SetFloat("_Shade", 1f);
            var territoryObject = new GameObject("Territory Player " + (i + 1));
            territoryObject.transform.SetParent(transform.parent, false);
            var participant = new Participant { Id = i, Name = i == 0 ? "あなた" : "NPC " + i,
                ColorName = names[i], Color = colors[i], Territory = territoryObject.AddComponent<FuchuWalkingTerritory>() };
            participant.Territory.Initialize(controller.transform.position, controller.BuildingEffects, colors[i], this, i == 0);
            players.Add(participant);
        }
        players[0].Character = controller.transform;
        Tint(localVisual, colors[0]);
        CreateBots();
        return players[0].Territory;
    }

    private void CreateBots()
    {
        Transform root = transform.parent;
        Transform road = root != null ? root.Find("FuchuLocalRoads_1000m") : null;
        FuchuResidentSpawner.TriangleNode[] graph = null;
        var travelNodes = new List<FuchuResidentSpawner.TriangleNode>();
        if (road != null && road.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
            graph = FuchuResidentSpawner.BuildSidewalkGraph(filter.sharedMesh, road, root,
                localPlayer.BuildingEffects.PlayableCenter, 995f, weldConnections: true, travelNodes: travelNodes);
        var routes = FuchuBattleNpc.FindLoops(graph);
        if (travelNodes.Count > 0) graph = travelNodes.ToArray();
        var starts = new List<Vector3> { transform.localPosition };
        for (int i = 1; i < PlayerCount; i++)
        {
            var obj = new GameObject("Battle NPC " + i);
            obj.transform.SetParent(root, false);
            var bot = obj.AddComponent<FuchuBattleNpc>();
            bot.Initialize(localPlayer, players[i], graph, routes, starts);
            players[i].Character = obj.transform;
            starts.Add(obj.transform.localPosition);
            bots.Add(bot);
        }
        Debug.Log($"対戦NPC経路: 道路ノード {graph?.Length ?? 0}、周回経路 {routes.Count}", this);
        if (routes.Count == 0) Debug.LogWarning("対戦NPCの周回経路が見つからないため、接続された道路の巡回に切り替えます。", this);
    }

    internal static void Tint(MeshRenderer renderer, Color color)
    {
        Material[] materials = renderer.sharedMaterials;
        if (materials.Length > 1) materials[1].color = color;
    }

    internal void Claim(FuchuWalkingTerritory owner, Paths64 claimed)
    {
        // The owner's shape is already updated. Subtract the entire new enclosure, including holes.
        foreach (Participant participant in players)
            if (participant.Territory != owner) participant.Territory.RemoveClaim(claimed);
        Version++;
        // Apply losses and gains synchronously, including claims made by NPCs later in the frame.
        players[0].Territory.RefreshBuildingOwnership();
        localPlayer.SyncOwnedGrowth();
    }

    internal int BuildingOwner(string id, Vector2 min, Vector2 max)
    {
        Vector2 center = (min + max) * 0.5f;
        // Keep one representative position per ID across streamed tiles and LOD replacement.
        if (!string.IsNullOrEmpty(id))
        {
            if (!buildingCenters.TryGetValue(id, out center))
            {
                center = (min + max) * 0.5f;
                buildingCenters.Add(id, center);
            }
        }
        return OwnerAt(center);
    }

    internal int OwnerAt(Vector2 point)
    {
        foreach (Participant participant in players)
            if (participant.Territory.Shape.Contains(point)) return participant.Id;
        return -1;
    }

    internal List<Participant> Standings()
    {
        var sorted = new List<Participant>(players);
        sorted.Sort((a, b) => {
            int comparison = b.Territory.Shape.ClaimedSquareMeters.CompareTo(a.Territory.Shape.ClaimedSquareMeters);
            return comparison != 0 ? comparison : a.Id.CompareTo(b.Id);
        });
        return sorted;
    }

    internal int Rank(Participant participant)
    {
        int rank = 1;
        foreach (Participant other in players)
            if (other.Territory.Shape.ClaimedSquareMeters > participant.Territory.Shape.ClaimedSquareMeters) rank++;
        return rank;
    }

    internal void Finish()
    {
        foreach (Participant participant in players) participant.Territory.FinishRun();
    }

    private void OnDestroy()
    {
        foreach (Participant participant in players)
            if (participant.Territory != null) Destroy(participant.Territory.gameObject);
        foreach (FuchuBattleNpc bot in bots)
            if (bot != null) Destroy(bot.gameObject);
        if (BuildingMaterials != null)
            foreach (Material material in BuildingMaterials) Destroy(material);
    }
}
