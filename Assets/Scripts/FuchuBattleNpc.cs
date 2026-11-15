using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

/// <summary>Walks actual road-boundary loops and records the same sampled trail as the human player.</summary>
public sealed class FuchuBattleNpc : MonoBehaviour
{
    private FuchuWalkController player;
    private FuchuBattleMatch.Participant participant;
    private FuchuResidentSpawner.TriangleNode[] graph;
    private List<int> route;
    private int target;
    private bool roaming;
    private readonly List<Vector2> loopPolygon = new List<Vector2>();
    private int evaluatedLoopVersion = -1;
    private const int MaxRoamingTrailPoints = 1024;
    private static readonly ProfilerMarker TrailMarker = new ProfilerMarker("FuchuBattleNpc.RecordTrail");
    private static readonly ProfilerMarker LoopMarker = new ProfilerMarker("FuchuBattleNpc.CompleteLoop");
    private MeshFilter visual;
    private MeshRenderer visualRenderer;
    private float phase;

    internal void Initialize(FuchuWalkController controller, FuchuBattleMatch.Participant owner,
        FuchuResidentSpawner.TriangleNode[] nodes, List<List<int>> loops, List<Vector3> starts)
    {
        player = controller;
        participant = owner;
        graph = nodes;
        // Prefer separate nearby blocks, so the first local match can be observed without crossing the city.
        float best = float.PositiveInfinity;
        Vector3 local = controller.transform.localPosition;
        foreach (List<int> loop in loops)
        {
            Vector3 start = nodes[loop[0]].Center;
            float score = Vector3.Distance(start, local) + UnityEngine.Random.Range(0f, 100f);
            foreach (Vector3 previous in starts)
                if (Vector3.Distance(previous, start) < 50f) score += 2000f;
            if (score >= best) continue;
            best = score;
            route = new List<int>(loop);
        }
        var obj = new GameObject("Player Visual", typeof(MeshFilter), typeof(MeshRenderer));
        obj.transform.SetParent(transform, false);
        visual = obj.GetComponent<MeshFilter>();
        visualRenderer = obj.GetComponent<MeshRenderer>();
        FuchuResidentSpawner.BuildPlayerVisual(visual, visualRenderer);
        FuchuBattleMatch.Tint(visualRenderer, owner.Color);
        if (route == null || route.Count < 3)
        {
            int nearest = -1;
            float nearestScore = float.PositiveInfinity;
            if (nodes != null)
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (nodes[i].Neighbors.Count == 0) continue;
                    float score = Vector3.Distance(nodes[i].Center, local);
                    foreach (Vector3 previous in starts)
                        if (Vector3.Distance(previous, nodes[i].Center) < 15f) score += 2000f;
                    if (score >= nearestScore) continue;
                    nearestScore = score;
                    nearest = i;
                }
            if (nearest < 0)
            {
                transform.localPosition = local + new Vector3(owner.Id * 2f, 0f, 2f);
                Debug.LogError($"{owner.Name}: 移動できる道路接続がありません。道路メッシュの参照を確認してください。", this);
                return;
            }
            roaming = true;
            route = new List<int> { nearest, nodes[nearest].Neighbors[0] };
        }
        if (UnityEngine.Random.value < 0.5f) route.Reverse();
        transform.localPosition = nodes[route[0]].Center + Vector3.up * 0.03f;
        target = 1;
        if (!roaming)
            foreach (int node in route)
            {
                Vector3 position = nodes[node].Center;
                loopPolygon.Add(new Vector2(position.x, position.z));
            }
    }

    private void RecordTrail()
    {
        using (TrailMarker.Auto())
        {
            // A known route is tested once at its end, never against every earlier sample.
            if (roaming && participant.Territory.OpenTrailPointCount >= MaxRoamingTrailPoints)
                participant.Territory.ClearOpenTrail();
            participant.Territory.RecordGroundStep(transform.position, evaluateClaims: roaming);
        }
    }

    private void CompleteLoop()
    {
        using (LoopMarker.Auto())
        {
            if (evaluatedLoopVersion != participant.Territory.Shape.Version)
            {
                participant.Territory.CompleteNpcLoop(loopPolygon);
                evaluatedLoopVersion = participant.Territory.Shape.Version;
            }
            else participant.Territory.ClearOpenTrail();
        }
    }

    private void Update()
    {
        if (player == null || !player.IsPlaying || route == null || Time.deltaTime <= 0f) return;
        RecordTrail();
        // Substeps preserve turns and closure even on a slow frame; no area is awarded without walking it.
        float remaining = Mathf.Min(Time.deltaTime, 0.25f) * 4.5f;
        int iterations = 0;
        while (remaining > 0.0001f && iterations++ < 64)
        {
            Vector3 destination = graph[route[target]].Center + Vector3.up * 0.03f;
            Vector3 direction = destination - transform.localPosition;
            float distance = direction.magnitude;
            float step = Mathf.Min(remaining, Mathf.Min(distance, 0.25f));
            if (distance > 0.0001f)
            {
                transform.localPosition += direction / distance * step;
                Vector3 flat = new Vector3(direction.x, 0f, direction.z);
                if (flat.sqrMagnitude > 0.0001f) transform.localRotation = Quaternion.LookRotation(flat, Vector3.up);
            }
            remaining -= step;
            RecordTrail();
            if (distance <= step + 0.0001f)
            {
                if (roaming)
                {
                    int current = route[target];
                    int previous = route[1 - target];
                    List<int> neighbors = graph[current].Neighbors;
                    int next = neighbors[UnityEngine.Random.Range(0, neighbors.Count)];
                    if (neighbors.Count > 1 && next == previous)
                        next = neighbors[(neighbors.IndexOf(next) + 1) % neighbors.Count];
                    route[1 - target] = next;
                    target = 1 - target;
                }
                else
                {
                    // Node zero is reached only after physically walking the entire closed route.
                    if (target == 0) CompleteLoop();
                    target = (target + 1) % route.Count;
                }
            }
        }
        phase += 4.5f * Time.deltaTime * 3f;
        visual.transform.localPosition = Vector3.up * Mathf.Abs(Mathf.Sin(phase)) * 0.025f;
    }

    internal static List<List<int>> FindLoops(FuchuResidentSpawner.TriangleNode[] nodes)
    {
        var result = new List<List<int>>();
        if (nodes == null) return result;
        var parent = new int[nodes.Length];
        var depth = new int[nodes.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = -2;
        // Iterative depth-first search avoids recursion on the large road mesh.
        for (int start = 0; start < nodes.Length; start++)
        {
            if (parent[start] != -2) continue;
            parent[start] = -1;
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((start, 0));
            while (stack.Count > 0)
            {
                var (current, next) = stack.Pop();
                if (next >= nodes[current].Neighbors.Count) continue;
                stack.Push((current, next + 1));
                int neighbor = nodes[current].Neighbors[next];
                if (neighbor == parent[current]) continue;
                if (parent[neighbor] == -2)
                {
                    parent[neighbor] = current;
                    depth[neighbor] = depth[current] + 1;
                    stack.Push((neighbor, 0));
                }
                else if (depth[neighbor] < depth[current] - 2)
                {
                    var loop = new List<int>();
                    int index = current;
                    while (index != neighbor && index >= 0 && loop.Count < 4096)
                    {
                        loop.Add(index);
                        index = parent[index];
                    }
                    if (index != neighbor) continue;
                    loop.Add(neighbor);
                    double twiceArea = 0;
                    float length = 0;
                    for (int i = 0; i < loop.Count; i++)
                    {
                        Vector3 a = nodes[loop[i]].Center, b = nodes[loop[(i + 1) % loop.Count]].Center;
                        twiceArea += (double)a.x * b.z - (double)b.x * a.z;
                        length += Vector3.Distance(a, b);
                    }
                    if (Math.Abs(twiceArea) >= 100 && length >= 30f && length <= 850f) result.Add(loop);
                }
            }
        }
        return result;
    }

    private void OnDestroy()
    {
        if (visual != null) Destroy(visual.sharedMesh);
        if (visualRenderer != null)
            foreach (Material material in visualRenderer.sharedMaterials) Destroy(material);
    }
}
