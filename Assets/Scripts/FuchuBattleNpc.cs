using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using Unity.Profiling;

/// <summary>Walks actual road-boundary loops and records the same sampled trail as the human player.</summary>
public sealed class FuchuBattleNpc : MonoBehaviour
{
    private FuchuWalkController player;
    private FuchuOnlineSimulation simulation;
    private bool Finished => onlineState != null ? onlineState.MatchFinished : player == null || player.RunFinished;
    private float BaseSprintSpeed => simulation != null ? simulation.SprintSpeed : player.BaseSprintSpeed;
    private float WalkSpeed => simulation != null ? simulation.WalkingSpeed : player.WalkingSpeed;
    private double ServerTime => simulation != null ? simulation.ServerTime : player.Online.ServerTime;
    internal void InitializeOnline(FuchuOnlineSimulation context, FuchuBattleMatch.Participant owner,
        FuchuOnlineTerritoryState state, List<Vector3> starts)
    {
        simulation = context;
        Initialize(context.Player, owner, context.Graph, context.Loops, starts, state);
    }
    private FuchuOnlineTerritoryState onlineState;
    private uint correctionVersion;
    private List<int> initialOnlineRoute, initialOnlineLoop;
    private bool initialOnlineRoaming;
    private FuchuOnlineTerritoryState.Participant OnlineOwner => onlineState?.Players[participant.Id];
    private FuchuTerritoryShape Shape => onlineState != null ? OnlineOwner.Shape : participant.Territory.Shape;
    private void ClearTrail()
    {
        if (onlineState != null) onlineState.ClearTrail(participant.Id);
        else participant.Territory.ClearOpenTrail();
    }
    private FuchuBattleMatch.Participant participant;
    private FuchuResidentSpawner.TriangleNode[] graph;
    private List<int> route;
    private int target;
    private bool roaming;
    private bool planning;
    private bool transferring;
    private List<List<int>> availableLoops;
    private List<int> currentLoop;
    private List<int> pendingLoop;
    private readonly HashSet<List<int>> visitedLoops = new HashSet<List<int>>();
    private int[] searchVisited;
    private int[] searchParents;
    private float[] searchDistance;
    private int searchStamp;
    private readonly Queue<int> searchQueue = new Queue<int>();
    private static readonly ProfilerMarker SearchMarker = new ProfilerMarker("FuchuBattleNpc.SearchRoute");
    private readonly List<Vector2> loopPolygon = new List<Vector2>();
    private int evaluatedLoopVersion = -1;
    private const int MaxRoamingTrailPoints = 1024;
    private static readonly ProfilerMarker TrailMarker = new ProfilerMarker("FuchuBattleNpc.RecordTrail");
    private static readonly ProfilerMarker LoopMarker = new ProfilerMarker("FuchuBattleNpc.CompleteLoop");
    private MeshFilter visual;
    private MeshRenderer visualRenderer;
    private float phase;
    private double stamina = FuchuWalkController.BaseStamina;
    private float sprintBonus;
    private float nextItemScan;
    private int targetItem = -1;
    private Vector3 itemPosition;
    private Vector3 returnPosition;
    private bool returningToRoute;
    private Collider roadCollider;
    private Predicate<Vector3> reachableItem;
    private double MaxStamina => onlineState != null ? OnlineOwner.MaxStamina : FuchuWalkController.BaseStamina + participant.StaminaCapacityPoints;
    private int SpeedLevel => 1 + (int)Math.Min(FuchuWalkController.MaximumSprintBonus, Math.Floor((onlineState != null ? OnlineOwner.MovementPoints : participant.MovementPoints) / FuchuWalkController.PointsPerSprintBonus));

    internal void Initialize(FuchuWalkController controller, FuchuBattleMatch.Participant owner,
        FuchuResidentSpawner.TriangleNode[] nodes, List<List<int>> loops, List<Vector3> starts, FuchuOnlineTerritoryState authority = null)
    {
        player = controller;
        onlineState = authority;
        participant = owner;
        graph = nodes;
        availableLoops = loops;
        if (nodes != null)
        {
            searchVisited = new int[nodes.Length];
            searchParents = new int[nodes.Length];
            searchDistance = new float[nodes.Length];
        }
        reachableItem = CanReachItem;
        Transform road = transform.parent != null ? transform.parent.Find("FuchuLocalRoads_1000m") : null;
        if (road != null) roadCollider = road.GetComponent<Collider>();
        // Prefer separate nearby blocks, so the first local match can be observed without crossing the city.
        float best = float.PositiveInfinity;
        Vector3 local = simulation != null ? simulation.Spawn : controller.transform.localPosition;
        foreach (List<int> loop in loops)
        {
            Vector3 start = nodes[loop[0]].Center;
            float score = Vector3.Distance(start, local) + UnityEngine.Random.Range(0f, 100f);
            foreach (Vector3 previous in starts)
                if (Vector3.Distance(previous, start) < 50f) score += 2000f;
            if (score >= best) continue;
            best = score;
            route = new List<int>(loop);
            currentLoop = loop;
        }
        if (simulation == null || !simulation.Dedicated)
        {
            var obj = new GameObject("Player Visual", typeof(MeshFilter), typeof(MeshRenderer));
            obj.transform.SetParent(transform, false);
            visual = obj.GetComponent<MeshFilter>();
            visualRenderer = obj.GetComponent<MeshRenderer>();
            FuchuResidentSpawner.BuildPlayerVisual(visual, visualRenderer);
            FuchuBattleMatch.Tint(visualRenderer, owner.Color);
        }
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
        if (onlineState != null)
        {
            initialOnlineRoute = new List<int>(route); initialOnlineLoop = currentLoop; initialOnlineRoaming = roaming;
        }
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
            if (onlineState != null)
            {
                onlineState.Record(participant.Id, new Vector2(transform.localPosition.x, transform.localPosition.z), evaluateClaims: roaming);
                return;
            }
            if (roaming && participant.Territory.OpenTrailPointCount >= MaxRoamingTrailPoints)
                ClearTrail();
            participant.Territory.RecordGroundStep(transform.position, evaluateClaims: roaming);
        }
    }

    private void CompleteLoop()
    {
        using (LoopMarker.Auto())
        {
            if (evaluatedLoopVersion != Shape.Version)
            {
                if (onlineState != null) onlineState.ClaimNpcLoop(participant.Id, loopPolygon);
                else participant.Territory.CompleteNpcLoop(loopPolygon);
                evaluatedLoopVersion = Shape.Version;
            }
            else ClearTrail();
        }
        if (!planning && availableLoops.Count > 1)
        {
            visitedLoops.Add(currentLoop);
            planning = true;
            StartCoroutine(FindExpansionRoute(route[0]));
        }
    }

    private IEnumerator FindExpansionRoute(int start)
    {
        // One bounded breadth-first traversal; reused arrays avoid a new allocation per road node.
        searchStamp++;
        searchQueue.Clear();
        searchVisited[start] = searchStamp;
        searchParents[start] = -1;
        searchDistance[start] = 0f;
        searchQueue.Enqueue(start);
        while (searchQueue.Count > 0 && !Finished)
        {
            using (SearchMarker.Auto())
            {
                for (int work = 0; work < 256 && searchQueue.Count > 0; work++)
                {
                    int node = searchQueue.Dequeue();
                    foreach (int next in graph[node].Neighbors)
                    {
                        if (searchVisited[next] == searchStamp) continue;
                        float distance = searchDistance[node] + Vector3.Distance(graph[node].Center, graph[next].Center);
                        if (distance > 600f) continue;
                        searchVisited[next] = searchStamp;
                        searchParents[next] = node;
                        searchDistance[next] = distance;
                        searchQueue.Enqueue(next);
                    }
                }
            }
            yield return null;
        }
        if (Finished) { planning = false; yield break; }

        // Shortlist nearby loops, preferring blocks not visited by this NPC yet.
        var candidates = new List<(List<int> Loop, int Entry, float Cost)>();
        int inspected = 0;
        foreach (List<int> loop in availableLoops)
        {
            if (loop == currentLoop) continue;
            int entry = -1;
            float cost = float.PositiveInfinity;
            for (int i = 0; i < loop.Count; i++)
            {
                int node = loop[i];
                if (searchVisited[node] != searchStamp || searchDistance[node] >= cost) continue;
                entry = i;
                cost = searchDistance[node];
            }
            if (entry >= 0) candidates.Add((loop, entry, cost + (visitedLoops.Contains(loop) ? 200f : 0f)));
            if (++inspected % 16 == 0) yield return null;
        }
        candidates.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        List<int> selected = null;
        int selectedEntry = -1;
        float bestScore = 0f;
        var polygon = new List<Vector2>();
        // Polygon difference is expensive: evaluate at most eight candidates, one per frame.
        for (int i = 0; i < Mathf.Min(8, candidates.Count); i++)
        {
            if (Finished) { planning = false; yield break; }
            using (SearchMarker.Auto())
            {
                var candidate = candidates[i];
                polygon.Clear();
                foreach (int node in candidate.Loop)
                {
                    Vector3 point = graph[node].Center;
                    polygon.Add(new Vector2(point.x, point.z));
                }
                double added = Shape.AdditionalSquareMeters(polygon);
                float score = (float)added / (1f + candidate.Cost * 0.05f);
                if (added >= 2.0 && score > bestScore)
                {
                    selected = candidate.Loop;
                    selectedEntry = candidate.Entry;
                    bestScore = score;
                }
            }
            yield return null;
        }
        if (Finished || selected == null) { planning = false; yield break; }
        var connection = new List<int>();
        int destination = selected[selectedEntry];
        for (int node = destination; node >= 0; node = searchParents[node]) connection.Add(node);
        connection.Reverse();
        pendingLoop = new List<int>(selected.Count);
        for (int i = 0; i < selected.Count; i++) pendingLoop.Add(selected[(selectedEntry + i) % selected.Count]);
        currentLoop = selected;
        ClearTrail();
        if (connection.Count > 1)
        {
            route = connection;
            target = 1;
            transferring = true;
        }
        else BeginNextLoop();
        planning = false;
    }

    private void BeginNextLoop()
    {
        route = pendingLoop;
        pendingLoop = null;
        transferring = false;
        target = 1;
        evaluatedLoopVersion = -1;
        loopPolygon.Clear();
        foreach (int node in route)
        {
            Vector3 point = graph[node].Center;
            loopPolygon.Add(new Vector2(point.x, point.z));
        }
        ClearTrail();
    }

    private bool CanReachItem(Vector3 destination)
    {
        if (roadCollider == null) return false;
        Vector3 start = transform.position;
        Vector3 up = transform.up;
        int samples = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(start, destination) / 1.5f));
        Vector3 previous = start;
        for (int i = 1; i <= samples; i++)
        {
            Vector3 sample = Vector3.Lerp(start, destination, (float)i / samples);
            if (!roadCollider.Raycast(new Ray(sample + up * 20f, -up), out RaycastHit hit, 40f) ||
                Vector3.Angle(hit.normal, up) > 45f) return false;
            Vector3 feet = hit.point + up * 0.03f;
            Vector3 direction = feet - previous;
            float distance = direction.magnitude;
            if (Physics.CheckCapsule(feet + up * 0.4f, feet + up * 1.5f, 0.28f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            if (distance > 0.001f && Physics.CapsuleCast(previous + up * 0.4f, previous + up * 1.5f,
                0.28f, direction / distance, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            previous = feet;
        }
        return true;
    }

    private void UpdateItemTarget()
    {
        if (onlineState != null)
        {
            if (targetItem >= 0)
            {
                var existing = onlineState.Items[targetItem];
                if (!existing.Active || (transform.parent.TransformPoint(existing.Position) - itemPosition).sqrMagnitude > 0.25f)
                { targetItem = -1; returningToRoute = true; }
                return;
            }
            if (returningToRoute || Time.time < nextItemScan) return;
            nextItemScan = Time.time + 0.5f;
            float best = 144f;
            for (int i = 0; i < onlineState.Items.Count; i++)
            {
                var item = onlineState.Items[i];
                if (!item.Active) continue;
                Vector3 world = transform.parent.TransformPoint(item.Position);
                float distance = (world - transform.position).sqrMagnitude;
                if (distance >= best || !CanReachItem(world)) continue;
                best = distance; targetItem = i; itemPosition = world;
            }
            if (targetItem >= 0) returnPosition = transform.localPosition;
            return;
        }
        FuchuStaminaItems items = player.RecoveryItems;
        if (targetItem >= 0)
        {
            if (items == null || !items.TryGetItemPosition(targetItem, out Vector3 current) ||
                (current - itemPosition).sqrMagnitude > 0.25f)
            {
                targetItem = -1;
                returningToRoute = true;
            }
            return;
        }
        if (returningToRoute || items == null || Time.time < nextItemScan) return;
        nextItemScan = Time.time + 0.5f;
        if (items.TryFindNearbyItem(transform.position, 12f, reachableItem, out targetItem, out itemPosition))
            returnPosition = transform.localPosition;
    }

    private void AdvanceRoute()
    {
        if (transferring)
        {
            if (target == route.Count - 1) BeginNextLoop();
            else target++;
        }
        else if (roaming)
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
            if (target == 0) CompleteLoop();
            target = (target + 1) % route.Count;
        }
    }

    private void Update()
    {
        if ((onlineState == null ? player == null || !player.IsPlaying : !onlineState.Playing) || route == null || Time.deltaTime <= 0f) return;
        if (onlineState != null)
        {
            if (!onlineState.Playing || ServerTime >= onlineState.MatchDeadline || !OnlineOwner.IsNpc) return;
            stamina = OnlineOwner.Stamina;
            if (correctionVersion != OnlineOwner.CorrectionVersion)
            {
                correctionVersion = OnlineOwner.CorrectionVersion;
                StopAllCoroutines(); planning = false; targetItem = -1; returningToRoute = false;
                route = new List<int>(initialOnlineRoute); currentLoop = initialOnlineLoop; roaming = initialOnlineRoaming;
                transferring = false; pendingLoop = null; evaluatedLoopVersion = -1;
                loopPolygon.Clear();
                if (!roaming) foreach (int node in route)
                    loopPolygon.Add(new Vector2(graph[node].Center.x, graph[node].Center.z));
                target = route.Count > 1 ? 1 : 0;
                transform.localPosition = OnlineOwner.SpawnPosition;
                OnlineOwner.Position = transform.localPosition;
                OnlineOwner.HasPosition = true;
                ClearTrail();
            }
        }
        if (planning) return;
        stamina = Math.Min(stamina, MaxStamina);
        sprintBonus = Mathf.Min(sprintBonus, SpeedLevel - 1);
        UpdateItemTarget();
        RecordTrail();
        float remainingTime = Mathf.Min(Time.deltaTime, 0.25f);
        if (stamina > 0.00001)
            sprintBonus = Mathf.MoveTowards(sprintBonus, SpeedLevel - 1, remainingTime * FuchuWalkController.SprintBonusPerSecond);
        else sprintBonus = 0f;
        float moved = 0f;
        int iterations = 0;
        while (remainingTime > 0.00001f && iterations++ < 128)
        {
            bool detour = targetItem >= 0 || returningToRoute;
            Vector3 destination = returningToRoute ? returnPosition : targetItem >= 0
                ? transform.parent.InverseTransformPoint(itemPosition) + Vector3.up * 0.03f
                : graph[route[target]].Center + Vector3.up * 0.03f;
            Vector3 direction = destination - transform.localPosition;
            float distance = direction.magnitude;
            bool sprinting = stamina > 0.00001;
            float slice = sprinting ? Mathf.Min(remainingTime, (float)(stamina / FuchuWalkController.SprintStaminaPerSecond)) : remainingTime;
            if (!sprinting) sprintBonus = 0f;
            float speed = sprinting ? BaseSprintSpeed + sprintBonus : WalkSpeed;
            if (onlineState != null && ServerTime < OnlineOwner.SlowUntil) speed *= 0.5f;
            float step = Mathf.Min(distance, Mathf.Min(0.25f, speed * slice));
            if (distance > 0.0001f)
            {
                transform.localPosition += direction / distance * step;
                Vector3 flat = new Vector3(direction.x, 0f, direction.z);
                if (flat.sqrMagnitude > 0.0001f) transform.localRotation = Quaternion.LookRotation(flat, Vector3.up);
            }
            float consumedTime = step / speed;
            remainingTime -= consumedTime;
            moved += step;
            if (sprinting) stamina = Math.Max(0.0, stamina - consumedTime * FuchuWalkController.SprintStaminaPerSecond);
            RecordTrail();
            if (distance <= step + 0.0001f)
            {
                if (returningToRoute)
                {
                    returningToRoute = false;
                    nextItemScan = Time.time + 0.5f;
                }
                else if (detour)
                {
                    if (onlineState == null && player.RecoveryItems.TryCollectNpcItem(targetItem, transform.position, out double fraction))
                        stamina = Math.Min(MaxStamina, stamina + MaxStamina * fraction);
                    targetItem = -1;
                    returningToRoute = true;
                }
                else AdvanceRoute();
                if (planning) break;
            }
        }
        if (onlineState != null)
            onlineState.ProcessMovement(participant.Id, transform.localPosition, moved > 0 && OnlineOwner.Stamina > 0,
                Time.deltaTime, false, 0, BaseSprintSpeed, ServerTime < OnlineOwner.SlowUntil ? 0.5f : 1f);
        phase += moved * 3f;
        if (visual != null) visual.transform.localPosition = Vector3.up * Mathf.Abs(Mathf.Sin(phase)) * 0.025f;
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
