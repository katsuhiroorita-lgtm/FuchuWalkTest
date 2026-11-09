using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Creates simple lightweight cars that circulate over the generated road surface.</summary>
public sealed class FuchuTrafficSpawner : MonoBehaviour
{
    [HideInInspector] public Transform worldRoot;
    [HideInInspector] public MeshFilter roadMeshFilter;
    [HideInInspector] public FuchuWalkController playerController;
    [HideInInspector] public Vector3 initialPosition;
    [Min(1)] public int carCount = 72;
    [Min(10f)] public float radiusMeters = 1000f;
    [Min(1f)] public float minimumSpeed = 5f;
    [Min(1f)] public float maximumSpeed = 9f;

    private readonly List<CarState> cars = new List<CarState>();
    private readonly List<Material> materials = new List<Material>();
    private List<List<int>> components;
    private TriangleNode[] nodes;
    private int[] searchParents;
    private int[] searchMarks;
    private int[] searchQueue;
    private int searchVersion;
    private float collisionCooldownUntil;
    private System.Random random;

    private static readonly Color[] PaintColors =
    {
        new Color(0.78f, 0.16f, 0.12f), new Color(0.12f, 0.28f, 0.62f),
        new Color(0.82f, 0.82f, 0.76f), new Color(0.18f, 0.20f, 0.21f),
        new Color(0.74f, 0.56f, 0.18f), new Color(0.20f, 0.43f, 0.30f)
    };

    private void Start()
    {
        if (worldRoot == null || roadMeshFilter == null || roadMeshFilter.sharedMesh == null)
        {
            Debug.LogWarning("Fuchu traffic needs the generated road surface and georeference.", this);
            return;
        }
        random = new System.Random(Mathf.Abs(Mathf.RoundToInt(initialPosition.x * 17f + initialPosition.z * 31f)) + 531);
        nodes = BuildRoadGraph(roadMeshFilter.sharedMesh, roadMeshFilter.transform, worldRoot, initialPosition, radiusMeters);
        if (nodes.Length == 0)
        {
            Debug.LogWarning("No road sections were found inside the traffic area.", this);
            return;
        }
        BuildConnectedComponents();
        searchParents = new int[nodes.Length];
        searchMarks = new int[nodes.Length];
        searchQueue = new int[nodes.Length];
        Material glassMaterial = CreateMaterial("TrafficGlass", new Color(0.20f, 0.36f, 0.42f), 0.1f);
        Material tireMaterial = CreateMaterial("TrafficTires", new Color(0.055f, 0.06f, 0.065f), 0.05f);
        Material lampMaterial = CreateMaterial("TrafficLamps", new Color(0.95f, 0.78f, 0.48f), 0.05f);
        var paintMaterials = new Material[PaintColors.Length];
        for (int i = 0; i < PaintColors.Length; i++)
            paintMaterials[i] = CreateMaterial("CarPaint_" + i, PaintColors[i], 0.2f);

        var choices = new List<int>();
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].Neighbors.Count > 0 && nodes[i].InsideRadius && nodes[i].Component >= 0) choices.Add(i);
        Shuffle(choices);
        var selected = new List<int>();
        foreach (int candidate in choices)
        {
            Vector3 point = nodes[candidate].Center;
            bool spaced = true;
            foreach (int existing in selected)
            {
                Vector3 other = nodes[existing].Center;
                if ((point - other).sqrMagnitude < 32f * 32f) { spaced = false; break; }
            }
            if (!spaced) continue;
            selected.Add(candidate);
            if (selected.Count >= carCount) break;
        }
        if (selected.Count < carCount)
            foreach (int candidate in choices)
            {
                if (selected.Contains(candidate)) continue;
                selected.Add(candidate);
                if (selected.Count >= carCount) break;
            }

        for (int i = 0; i < selected.Count; i++)
        {
            int current = selected[i];
            int destination = PickFarDestination(current);
            List<int> route = FindRoute(current, destination);
            if (route.Count < 2) continue;
            GameObject car = new GameObject("TrafficCar_" + (i + 1).ToString("00"));
            car.transform.SetParent(transform, false);
            car.transform.localPosition = nodes[current].Center;
            Transform visual = new GameObject("Visual").transform;
            visual.SetParent(car.transform, false);
            BuildCarModel(visual, paintMaterials[i % paintMaterials.Length], glassMaterial, tireMaterial, lampMaterial);
            cars.Add(new CarState(car.transform, visual, current, destination, route,
                minimumSpeed + (float)random.NextDouble() * Mathf.Max(0f, maximumSpeed - minimumSpeed)));
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (nodes == null || dt <= 0f) return;
        foreach (CarState car in cars)
        {
            Vector3 position = car.Root.localPosition;
            Vector3 target = nodes[car.TargetNode].Center;
            Vector3 direction = target - position;
            direction.y = 0f;
            float distance = direction.magnitude;
            if (distance < 0.3f)
            {
                car.CurrentNode = car.TargetNode;
                car.RouteIndex++;
                if (car.RouteIndex >= car.Route.Count)
                {
                    car.DestinationNode = PickFarDestination(car.CurrentNode);
                    car.Route = FindRoute(car.CurrentNode, car.DestinationNode);
                    car.RouteIndex = 1;
                    if (car.Route.Count < 2) continue;
                }
                car.TargetNode = car.Route[car.RouteIndex];
                target = nodes[car.TargetNode].Center;
                direction = target - position;
                direction.y = 0f;
                distance = direction.magnitude;
            }
            if (distance < 0.001f) continue;
            direction /= distance;
            float step = Mathf.Min(distance, car.Speed * dt);
            position += direction * step;
            position.y = Mathf.Lerp(position.y, nodes[car.TargetNode].Center.y, Mathf.Clamp01(dt * 4f));
            car.Root.localPosition = position;
            car.Visual.localRotation = Quaternion.LookRotation(direction, Vector3.up);
        }
        DetectPlayerCollision();
    }

    private void DetectPlayerCollision()
    {
        if (playerController == null || Time.time < collisionCooldownUntil) return;
        CharacterController character = playerController.GetComponent<CharacterController>();
        if (character == null) return;
        Bounds playerBounds = character.bounds;
        Vector3 playerCenter = playerBounds.center;
        foreach (CarState car in cars)
        {
            Vector3 local = car.Visual.InverseTransformPoint(playerCenter);
            bool overlapsCarBody = Mathf.Abs(local.x) <= 1.32f && Mathf.Abs(local.z) <= 2.38f &&
                                   playerBounds.max.y >= car.Root.position.y + 0.12f &&
                                   playerBounds.min.y <= car.Root.position.y + 1.55f;
            if (!overlapsCarBody) continue;
            collisionCooldownUntil = Time.time + 1.5f;
            playerController.ReturnToStartingPosition();
            return;
        }
    }

    private TriangleNode[] BuildRoadGraph(Mesh mesh, Transform roadTransform, Transform root, Vector3 center, float radius)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = root.InverseTransformPoint(roadTransform.TransformPoint(vertices[i]));
        int triangleCount = triangles.Length / 3;
        var graph = new TriangleNode[triangleCount];
        var edgeOwners = new Dictionary<IndexEdge, int>(triangleCount * 2);
        var trianglesAtVertex = new Dictionary<PositionKey, List<int>>(vertices.Length);
        float radiusSquared = radius * radius;
        for (int i = 0; i < triangleCount; i++)
        {
            int a = triangles[i * 3], b = triangles[i * 3 + 1], c = triangles[i * 3 + 2];
            Vector3 point = (vertices[a] + vertices[b] + vertices[c]) / 3f;
            float dx = point.x - center.x, dz = point.z - center.z;
            graph[i] = new TriangleNode(point, dx * dx + dz * dz <= radiusSquared);
            ConnectEdge(edgeOwners, graph, i, a, b);
            ConnectEdge(edgeOwners, graph, i, b, c);
            ConnectEdge(edgeOwners, graph, i, c, a);
            AddTriangleAtPosition(trianglesAtVertex, vertices[a], i);
            AddTriangleAtPosition(trianglesAtVertex, vertices[b], i);
            AddTriangleAtPosition(trianglesAtVertex, vertices[c], i);
        }
        foreach (List<int> touching in trianglesAtVertex.Values)
            for (int i = 0; i < touching.Count; i++)
                for (int j = i + 1; j < touching.Count; j++)
                    ConnectNodes(graph, touching[i], touching[j]);
        return graph;
    }

    private static void AddTriangleAtPosition(Dictionary<PositionKey, List<int>> map, Vector3 point, int triangle)
    {
        PositionKey key = new PositionKey(point);
        if (!map.TryGetValue(key, out List<int> touching)) map.Add(key, touching = new List<int>(4));
        if (!touching.Contains(triangle)) touching.Add(triangle);
    }

    private static void ConnectEdge(Dictionary<IndexEdge, int> owners, TriangleNode[] graph, int triangle, int a, int b)
    {
        IndexEdge key = new IndexEdge(a, b);
        if (owners.TryGetValue(key, out int other))
            ConnectNodes(graph, triangle, other);
        else owners.Add(key, triangle);
    }

    private static void ConnectNodes(TriangleNode[] graph, int a, int b)
    {
        if (a == b) return;
        if (!graph[a].Neighbors.Contains(b)) graph[a].Neighbors.Add(b);
        if (!graph[b].Neighbors.Contains(a)) graph[b].Neighbors.Add(a);
    }

    private void BuildConnectedComponents()
    {
        components = new List<List<int>>();
        var queue = new Queue<int>();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!nodes[i].InsideRadius || nodes[i].Component >= 0 || nodes[i].Neighbors.Count == 0) continue;
            int componentId = components.Count;
            var component = new List<int>();
            nodes[i].Component = componentId;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                component.Add(current);
                foreach (int neighbor in nodes[current].Neighbors)
                {
                    if (!nodes[neighbor].InsideRadius || nodes[neighbor].Component >= 0) continue;
                    nodes[neighbor].Component = componentId;
                    queue.Enqueue(neighbor);
                }
            }
            components.Add(component);
        }
    }

    private int PickFarDestination(int current)
    {
        List<int> component = components[nodes[current].Component];
        Vector3 origin = nodes[current].Center;
        float minimumDistanceSquared = 350f * 350f;
        int best = current;
        float farthest = -1f;
        for (int sample = 0; sample < 80; sample++)
        {
            int candidate = component[random.Next(component.Count)];
            float distanceSquared = FlatDistanceSquared(origin, nodes[candidate].Center);
            if (distanceSquared > farthest) { farthest = distanceSquared; best = candidate; }
            if (distanceSquared >= minimumDistanceSquared) return candidate;
        }
        if (best != current) return best;
        foreach (int candidate in component)
        {
            float distanceSquared = FlatDistanceSquared(origin, nodes[candidate].Center);
            if (distanceSquared > farthest) { farthest = distanceSquared; best = candidate; }
        }
        return best;
    }

    private List<int> FindRoute(int start, int destination)
    {
        var route = new List<int>();
        if (start == destination) { route.Add(start); return route; }
        if (++searchVersion == int.MaxValue)
        {
            Array.Clear(searchMarks, 0, searchMarks.Length);
            searchVersion = 1;
        }
        int head = 0, tail = 0;
        searchQueue[tail++] = start;
        searchMarks[start] = searchVersion;
        searchParents[start] = -1;
        while (head < tail)
        {
            int current = searchQueue[head++];
            if (current == destination) break;
            foreach (int neighbor in nodes[current].Neighbors)
            {
                if (!nodes[neighbor].InsideRadius || nodes[neighbor].Component != nodes[start].Component || searchMarks[neighbor] == searchVersion) continue;
                searchMarks[neighbor] = searchVersion;
                searchParents[neighbor] = current;
                searchQueue[tail++] = neighbor;
            }
        }
        if (searchMarks[destination] != searchVersion) { route.Add(start); return route; }
        for (int node = destination; node >= 0; node = searchParents[node]) route.Add(node);
        route.Reverse();
        return route;
    }

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private void BuildCarModel(Transform parent, Material paint, Material glass, Material tires, Material lamps)
    {
        AddCube(parent, "Body", new Vector3(0f, 0.57f, 0f), new Vector3(1.85f, 0.72f, 4.05f), paint);
        AddCube(parent, "Cabin", new Vector3(0f, 1.10f, -0.20f), new Vector3(1.48f, 0.64f, 1.95f), paint);
        AddCube(parent, "Windshield", new Vector3(0f, 1.13f, 0.83f), new Vector3(1.32f, 0.47f, 0.045f), glass);
        AddCube(parent, "RearWindow", new Vector3(0f, 1.13f, -1.22f), new Vector3(1.28f, 0.44f, 0.045f), glass);
        for (int side = -1; side <= 1; side += 2)
            for (int axle = -1; axle <= 1; axle += 2)
                AddCube(parent, "Wheel", new Vector3(side * 0.91f, 0.30f, axle * 1.28f), new Vector3(0.24f, 0.40f, 0.70f), tires);
        AddCube(parent, "Headlight_L", new Vector3(-0.62f, 0.57f, 2.04f), new Vector3(0.35f, 0.18f, 0.05f), lamps);
        AddCube(parent, "Headlight_R", new Vector3(0.62f, 0.57f, 2.04f), new Vector3(0.35f, 0.18f, 0.05f), lamps);
    }

    private static void AddCube(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
    {
        GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        part.name = name;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        Collider collider = part.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        MeshRenderer renderer = part.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private Material CreateMaterial(string name, Color color, float metallic)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        Material material = new Material(shader) { name = name, color = color, enableInstancing = true };
        material.SetFloat("_Metallic", metallic);
        materials.Add(material);
        return material;
    }

    private void Shuffle(List<int> values)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private void OnDestroy()
    {
        foreach (Material material in materials)
            if (material != null) Destroy(material);
    }

    private sealed class TriangleNode
    {
        public readonly Vector3 Center;
        public readonly bool InsideRadius;
        public readonly List<int> Neighbors = new List<int>(3);
        public int Component = -1;
        public TriangleNode(Vector3 center, bool insideRadius) { Center = center; InsideRadius = insideRadius; }
    }

    private sealed class CarState
    {
        public readonly Transform Root, Visual;
        public readonly float Speed;
        public int CurrentNode, TargetNode, DestinationNode, RouteIndex;
        public List<int> Route;
        public CarState(Transform root, Transform visual, int current, int destination, List<int> route, float speed)
        {
            Root = root; Visual = visual; CurrentNode = current; DestinationNode = destination;
            Route = route; RouteIndex = 1; TargetNode = route[1]; Speed = speed;
        }
    }

    private readonly struct IndexEdge : IEquatable<IndexEdge>
    {
        private readonly int a, b;
        public IndexEdge(int x, int y) { if (x < y) { a = x; b = y; } else { a = y; b = x; } }
        public bool Equals(IndexEdge other) => a == other.a && b == other.b;
        public override bool Equals(object obj) => obj is IndexEdge other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(a, b);
    }

    private readonly struct PositionKey : IEquatable<PositionKey>
    {
        private readonly int x, z;
        public PositionKey(Vector3 point) { x = Mathf.RoundToInt(point.x * 2f); z = Mathf.RoundToInt(point.z * 2f); }
        public bool Equals(PositionKey other) => x == other.x && z == other.z;
        public override bool Equals(object obj) => obj is PositionKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, z);
    }
}
