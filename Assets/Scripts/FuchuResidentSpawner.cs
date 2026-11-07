using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Builds and moves a small set of low-poly residents along the generated road surface.</summary>
public sealed class FuchuResidentSpawner : MonoBehaviour
{
    [HideInInspector] public Transform worldRoot;
    [HideInInspector] public MeshFilter roadMeshFilter;
    [HideInInspector] public Vector3 initialPosition;
    [Min(1)] public int residentCount = 192;
    [Min(10f)] public float radiusMeters = 1000f;

    private TriangleNode[] nodes;
    private readonly List<ResidentState> residents = new List<ResidentState>();
    private readonly List<Mesh> ownedMeshes = new List<Mesh>();
    private readonly List<Material> ownedMaterials = new List<Material>();
    private System.Random random;

    private static readonly VariantDescription[] Variants =
    {
        new VariantDescription("AdultMan_Navy", false, false, 1.00f, new Color(0.19f, 0.28f, 0.37f), new Color(0.10f, 0.09f, 0.08f)),
        new VariantDescription("AdultMan_Olive", false, false, 1.03f, new Color(0.34f, 0.38f, 0.27f), new Color(0.18f, 0.13f, 0.10f)),
        new VariantDescription("AdultMan_Brown", false, false, 0.97f, new Color(0.43f, 0.30f, 0.22f), new Color(0.13f, 0.11f, 0.10f)),
        new VariantDescription("AdultWoman_Red", false, true, 0.98f, new Color(0.48f, 0.23f, 0.25f), new Color(0.12f, 0.08f, 0.07f)),
        new VariantDescription("AdultWoman_Teal", false, true, 0.95f, new Color(0.23f, 0.39f, 0.39f), new Color(0.23f, 0.14f, 0.09f)),
        new VariantDescription("AdultWoman_Mustard", false, true, 1.01f, new Color(0.64f, 0.49f, 0.23f), new Color(0.16f, 0.11f, 0.09f)),
        new VariantDescription("ChildBoy_Blue", true, false, 0.68f, new Color(0.20f, 0.39f, 0.65f), new Color(0.11f, 0.09f, 0.08f)),
        new VariantDescription("ChildBoy_Green", true, false, 0.65f, new Color(0.35f, 0.49f, 0.28f), new Color(0.20f, 0.13f, 0.09f)),
        new VariantDescription("ChildGirl_Pink", true, true, 0.66f, new Color(0.66f, 0.34f, 0.42f), new Color(0.18f, 0.11f, 0.08f)),
        new VariantDescription("ChildGirl_Lilac", true, true, 0.70f, new Color(0.42f, 0.34f, 0.58f), new Color(0.32f, 0.22f, 0.12f))
    };

    private void Start()
    {
        if (worldRoot == null || roadMeshFilter == null || roadMeshFilter.sharedMesh == null)
        {
            Debug.LogWarning("Fuchu residents need a road surface and georeference.", this);
            return;
        }

        random = new System.Random(Mathf.Abs(Mathf.RoundToInt(initialPosition.x * 17f + initialPosition.z * 31f)) + 2026);
        nodes = BuildSidewalkGraph(roadMeshFilter.sharedMesh, roadMeshFilter.transform, worldRoot, initialPosition, radiusMeters);
        if (nodes == null || nodes.Length == 0 || Array.TrueForAll(nodes, node => !node.InsideRadius || node.Neighbors.Count == 0))
        {
            Debug.LogWarning("No walkable road sections were found inside the resident area.", this);
            return;
        }

        Material skin = CreateMaterial("ResidentSkin", new Color(0.73f, 0.53f, 0.40f));
        var modelMeshes = new Mesh[Variants.Length];
        var modelMaterials = new Material[Variants.Length][];
        for (int i = 0; i < Variants.Length; i++)
        {
            VariantDescription variant = Variants[i];
            Material clothes = CreateMaterial("ResidentClothes_" + variant.Name, variant.ClothesColor);
            Material hair = CreateMaterial("ResidentHair_" + variant.Name, variant.HairColor);
            modelMeshes[i] = BuildModel(variant);
            ownedMeshes.Add(modelMeshes[i]);
            modelMaterials[i] = new[] { skin, clothes, hair };
        }

        SpawnResidents(modelMeshes, modelMaterials);
    }

    private void Update()
    {
        if (nodes == null) return;
        float deltaTime = Time.deltaTime;
        for (int i = 0; i < residents.Count; i++)
        {
            ResidentState resident = residents[i];
            Vector3 target = nodes[resident.TargetNode].Center;
            Vector3 position = resident.Root.localPosition;
            Vector3 flatDirection = new Vector3(target.x - position.x, 0f, target.z - position.z);
            float distance = flatDirection.magnitude;
            if (distance < 0.25f)
            {
                resident.PreviousNode = resident.CurrentNode;
                resident.CurrentNode = resident.TargetNode;
                resident.TargetNode = PickNextNode(resident.CurrentNode, resident.PreviousNode);
                target = nodes[resident.TargetNode].Center;
                flatDirection = new Vector3(target.x - position.x, 0f, target.z - position.z);
                distance = flatDirection.magnitude;
            }

            if (distance > 0.001f)
            {
                Vector3 direction = flatDirection / distance;
                position += direction * Mathf.Min(distance, resident.Speed * deltaTime);
                float targetY = nodes[resident.TargetNode].Center.y;
                position.y = Mathf.Lerp(position.y, targetY, Mathf.Clamp01(deltaTime * 3f));
                resident.Root.localRotation = Quaternion.LookRotation(direction, Vector3.up);
                resident.Visual.localPosition = new Vector3(0f, 0.012f + Mathf.Abs(Mathf.Sin(Time.time * resident.StepRate + resident.Phase)) * 0.025f, 0f);
                resident.Visual.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * resident.StepRate + resident.Phase) * 2f);
                resident.Root.localPosition = position;
            }
        }
    }

    private void OnDestroy()
    {
        foreach (Mesh mesh in ownedMeshes)
            if (mesh != null) Destroy(mesh);
        foreach (Material material in ownedMaterials)
            if (material != null) Destroy(material);
    }

    private TriangleNode[] BuildSidewalkGraph(Mesh mesh, Transform roadTransform, Transform root, Vector3 center, float radius)
    {
        Vector3[] sourceVertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        var localVertices = new Vector3[sourceVertices.Length];
        for (int i = 0; i < sourceVertices.Length; i++)
            localVertices[i] = root.InverseTransformPoint(roadTransform.TransformPoint(sourceVertices[i]));

        int triangleCount = triangles.Length / 3;
        var edgeTable = new Dictionary<EdgeKey, BoundaryEdge>(triangleCount * 2);
        float radiusSquared = radius * radius;
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            int ia = triangles[triangle * 3];
            int ib = triangles[triangle * 3 + 1];
            int ic = triangles[triangle * 3 + 2];
            Vector3 a = localVertices[ia];
            Vector3 b = localVertices[ib];
            Vector3 c = localVertices[ic];
            Vector3 centroid = (a + b + c) / 3f;
            AddBoundaryCandidate(edgeTable, ia, ib, a, b, centroid);
            AddBoundaryCandidate(edgeTable, ib, ic, b, c, centroid);
            AddBoundaryCandidate(edgeTable, ic, ia, c, a, centroid);
        }

        var candidates = new List<BoundaryEdge>();
        foreach (BoundaryEdge edge in edgeTable.Values)
        {
            if (edge.Count != 1) continue;
            Vector3 midpoint = (edge.A + edge.B) * 0.5f;
            Vector3 inward = edge.Interior - midpoint;
            inward.y = 0f;
            if (inward.sqrMagnitude > 0.0001f)
                midpoint += inward.normalized * Mathf.Min(1.5f, inward.magnitude * 0.55f);
            float dx = midpoint.x - center.x;
            float dz = midpoint.z - center.z;
            if (dx * dx + dz * dz <= radiusSquared)
                candidates.Add(new BoundaryEdge(edge.VertexA, edge.VertexB, midpoint));
        }

        var graph = new TriangleNode[candidates.Count];
        var edgesAtVertex = new Dictionary<long, List<int>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            BoundaryEdge edge = candidates[i];
            graph[i] = new TriangleNode(edge.Center, true);
            AddBoundaryAdjacency(edgesAtVertex, edge.VertexA, i);
            AddBoundaryAdjacency(edgesAtVertex, edge.VertexB, i);
        }
        foreach (List<int> connectedEdges in edgesAtVertex.Values)
        {
            for (int i = 0; i < connectedEdges.Count; i++)
                for (int j = i + 1; j < connectedEdges.Count; j++)
                {
                    int a = connectedEdges[i], b = connectedEdges[j];
                    if (!graph[a].Neighbors.Contains(b)) graph[a].Neighbors.Add(b);
                    if (!graph[b].Neighbors.Contains(a)) graph[b].Neighbors.Add(a);
                }
        }
        return graph;
    }

    private static void AddBoundaryCandidate(Dictionary<EdgeKey, BoundaryEdge> edgeTable,
        int vertexA, int vertexB, Vector3 a, Vector3 b, Vector3 triangleCenter)
    {
        EdgeKey key = new EdgeKey(a, b);
        if (edgeTable.TryGetValue(key, out BoundaryEdge existing))
        {
            existing.Count++;
            edgeTable[key] = existing;
        }
        else
        {
            edgeTable.Add(key, new BoundaryEdge(vertexA, vertexB, a, b, triangleCenter));
        }
    }

    private static void AddBoundaryAdjacency(Dictionary<long, List<int>> map, int vertexIndex, int edgeIndex)
    {
        long key = vertexIndex;
        if (!map.TryGetValue(key, out List<int> edges)) map.Add(key, edges = new List<int>(2));
        edges.Add(edgeIndex);
    }

    private void SpawnResidents(Mesh[] modelMeshes, Material[][] modelMaterials)
    {
        var nearNodes = new List<int>();
        var farNodes = new List<int>();
        Vector2 center = new Vector2(initialPosition.x, initialPosition.z);
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!nodes[i].InsideRadius || nodes[i].Neighbors.Count == 0) continue;
            float distance = Vector2.Distance(center, new Vector2(nodes[i].Center.x, nodes[i].Center.z));
            if (distance <= Mathf.Min(300f, radiusMeters * 0.3f)) nearNodes.Add(i);
            else farNodes.Add(i);
        }
        Shuffle(nearNodes);
        Shuffle(farNodes);

        int nearCount = Mathf.Min(residentCount / 2, nearNodes.Count);
        var chosen = new List<int>(residentCount);
        AddSpacedNodes(nearNodes, nearCount, 16f, chosen);
        AddSpacedNodes(farNodes, residentCount - chosen.Count, 22f, chosen);
        if (chosen.Count < residentCount)
        {
            var remaining = new List<int>(nearNodes.Count + farNodes.Count);
            remaining.AddRange(nearNodes);
            remaining.AddRange(farNodes);
            Shuffle(remaining);
            foreach (int node in remaining)
            {
                if (!chosen.Contains(node)) chosen.Add(node);
                if (chosen.Count >= residentCount) break;
            }
        }

        for (int i = 0; i < chosen.Count; i++)
        {
            int current = chosen[i];
            int next = PickNextNode(current, -1);
            int variant = i % Variants.Length;
            GameObject resident = new GameObject("Resident_" + Variants[variant].Name + "_" + (i + 1).ToString("00"));
            resident.transform.SetParent(transform, false);
            resident.transform.localPosition = nodes[current].Center;
            var visual = new GameObject("Visual");
            visual.transform.SetParent(resident.transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            MeshFilter filter = visual.AddComponent<MeshFilter>();
            filter.sharedMesh = modelMeshes[variant];
            MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = modelMaterials[variant];
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            VariantDescription description = Variants[variant];
            float speed = description.IsChild ? 0.65f + (float)random.NextDouble() * 0.15f : 0.85f + (float)random.NextDouble() * 0.35f;
            residents.Add(new ResidentState(resident.transform, visual.transform, current, current, next,
                speed, description.IsChild ? 9f : 7f, (float)random.NextDouble() * 6.28f));
        }
    }

    private void AddSpacedNodes(List<int> source, int count, float spacing, List<int> output)
    {
        if (count <= 0) return;
        float spacingSquared = spacing * spacing;
        int added = 0;
        foreach (int candidate in source)
        {
            Vector3 position = nodes[candidate].Center;
            bool farEnough = true;
            foreach (int selected in output)
            {
                Vector3 other = nodes[selected].Center;
                float dx = position.x - other.x;
                float dz = position.z - other.z;
                if (dx * dx + dz * dz < spacingSquared) { farEnough = false; break; }
            }
            if (!farEnough) continue;
            output.Add(candidate);
            added++;
            if (added >= count) return;
        }
    }

    private int PickNextNode(int current, int previous)
    {
        List<int> neighbors = nodes[current].Neighbors;
        int available = 0;
        foreach (int neighbor in neighbors)
            if (nodes[neighbor].InsideRadius && neighbor != previous) available++;
        if (available == 0)
        {
            available = 0;
            foreach (int neighbor in neighbors)
                if (nodes[neighbor].InsideRadius) available++;
        }
        if (available == 0) return current;
        int choice = random.Next(available);
        foreach (int neighbor in neighbors)
        {
            if (!nodes[neighbor].InsideRadius || (available > 1 && neighbor == previous)) continue;
            if (choice-- == 0) return neighbor;
        }
        return current;
    }

    private void Shuffle(List<int> values)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            int temp = values[i];
            values[i] = values[j];
            values[j] = temp;
        }
    }

    internal static void BuildPlayerVisual(MeshFilter filter, MeshRenderer renderer)
    {
        var variant = new VariantDescription("Player", false, false, 1f,
            new Color(0.24f, 0.42f, 0.58f), new Color(0.10f, 0.09f, 0.08f));
        filter.sharedMesh = BuildModel(variant);
        renderer.sharedMaterials = new[]
        {
            CreateColoredMaterial("PlayerSkin", new Color(0.73f, 0.53f, 0.40f)),
            CreateColoredMaterial("PlayerClothes", variant.ClothesColor),
            CreateColoredMaterial("PlayerHair", variant.HairColor)
        };
    }

    private static Mesh BuildModel(VariantDescription variant)
    {
        GameObject temporaryRoot = new GameObject("ResidentModelBuilder");
        temporaryRoot.hideFlags = HideFlags.HideAndDontSave;
        temporaryRoot.SetActive(false);
        temporaryRoot.transform.localScale = Vector3.one * variant.HeightScale;
        var parts = new List<CombineInstance>[3] { new List<CombineInstance>(), new List<CombineInstance>(), new List<CombineInstance>() };

        AddPart(temporaryRoot.transform, PrimitiveType.Capsule, 1, new Vector3(0f, 1.10f, 0f), new Vector3(0.23f, 0.27f, 0.14f));
        AddPart(temporaryRoot.transform, PrimitiveType.Sphere, 0, new Vector3(0f, 1.51f, 0f), new Vector3(0.27f, 0.30f, 0.25f));
        AddPart(temporaryRoot.transform, PrimitiveType.Capsule, 1, new Vector3(-0.29f, 1.08f, 0f), new Vector3(0.08f, 0.23f, 0.08f), Quaternion.Euler(0f, 0f, -10f));
        AddPart(temporaryRoot.transform, PrimitiveType.Capsule, 1, new Vector3(0.29f, 1.08f, 0f), new Vector3(0.08f, 0.23f, 0.08f), Quaternion.Euler(0f, 0f, 10f));
        AddPart(temporaryRoot.transform, PrimitiveType.Sphere, 0, new Vector3(-0.31f, 0.84f, 0f), new Vector3(0.075f, 0.08f, 0.075f));
        AddPart(temporaryRoot.transform, PrimitiveType.Sphere, 0, new Vector3(0.31f, 0.84f, 0f), new Vector3(0.075f, 0.08f, 0.075f));

        bool skirt = variant.IsFemale;
        if (skirt)
            AddPart(temporaryRoot.transform, PrimitiveType.Cylinder, 1, new Vector3(0f, 0.78f, 0f), new Vector3(0.23f, 0.18f, 0.15f));
        else if (variant.IsChild)
            AddPart(temporaryRoot.transform, PrimitiveType.Cylinder, 1, new Vector3(0f, 0.73f, 0f), new Vector3(0.22f, 0.09f, 0.14f));

        AddPart(temporaryRoot.transform, PrimitiveType.Capsule, 1, new Vector3(-0.105f, 0.42f, 0f), new Vector3(0.09f, 0.34f, 0.095f));
        AddPart(temporaryRoot.transform, PrimitiveType.Capsule, 1, new Vector3(0.105f, 0.42f, 0f), new Vector3(0.09f, 0.34f, 0.095f));
        AddPart(temporaryRoot.transform, PrimitiveType.Cube, 1, new Vector3(-0.105f, 0.07f, 0.035f), new Vector3(0.13f, 0.10f, 0.22f));
        AddPart(temporaryRoot.transform, PrimitiveType.Cube, 1, new Vector3(0.105f, 0.07f, 0.035f), new Vector3(0.13f, 0.10f, 0.22f));

        AddPart(temporaryRoot.transform, PrimitiveType.Sphere, 2, new Vector3(0f, 1.64f, -0.015f), new Vector3(0.28f, 0.12f, 0.26f));
        if (variant.IsFemale)
        {
            AddPart(temporaryRoot.transform, PrimitiveType.Capsule, 2, new Vector3(0f, 1.39f, -0.105f), new Vector3(0.11f, 0.20f, 0.10f));
            if (variant.IsChild)
                AddPart(temporaryRoot.transform, PrimitiveType.Sphere, 2, new Vector3(0.13f, 1.48f, -0.04f), new Vector3(0.10f, 0.11f, 0.10f));
        }
        if (variant.IsChild)
            AddPart(temporaryRoot.transform, PrimitiveType.Cube, 1, new Vector3(0f, 0.96f, -0.16f), new Vector3(0.28f, 0.30f, 0.12f));

        var submeshes = new CombineInstance[parts.Length];
        for (int category = 0; category < parts.Length; category++)
        {
            Mesh categoryMesh = new Mesh { name = variant.Name + "_Part" + category };
            categoryMesh.CombineMeshes(parts[category].ToArray(), true, true);
            submeshes[category] = new CombineInstance { mesh = categoryMesh, transform = Matrix4x4.identity };
        }
        var finalMesh = new Mesh { name = variant.Name, indexFormat = IndexFormat.UInt32 };
        finalMesh.CombineMeshes(submeshes, false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();
        foreach (CombineInstance submesh in submeshes)
            Destroy(submesh.mesh);
        Destroy(temporaryRoot);
        return finalMesh;

        void AddPart(Transform parent, PrimitiveType type, int category, Vector3 position, Vector3 scale, Quaternion? rotation = null)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation ?? Quaternion.identity;
            part.transform.localScale = scale;
            MeshFilter filter = part.GetComponent<MeshFilter>();
            parts[category].Add(new CombineInstance
            {
                mesh = filter.sharedMesh,
                transform = part.transform.localToWorldMatrix
            });
        }
    }

    private Material CreateMaterial(string materialName, Color color)
    {
        Material material = CreateColoredMaterial(materialName, color);
        ownedMaterials.Add(material);
        return material;
    }

    private static Material CreateColoredMaterial(string materialName, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        return new Material(shader) { name = materialName, color = color, enableInstancing = true };
    }

    private sealed class TriangleNode
    {
        public readonly Vector3 Center;
        public readonly bool InsideRadius;
        public readonly List<int> Neighbors = new List<int>(3);
        public TriangleNode(Vector3 center, bool insideRadius) { Center = center; InsideRadius = insideRadius; }
    }

    private struct BoundaryEdge
    {
        public readonly int VertexA, VertexB;
        public readonly Vector3 A, B, Interior;
        public readonly Vector3 Center;
        public int Count;
        public BoundaryEdge(int vertexA, int vertexB, Vector3 a, Vector3 b, Vector3 interior)
        {
            VertexA = vertexA; VertexB = vertexB; A = a; B = b; Interior = interior; Center = (a + b) * 0.5f; Count = 1;
        }
        public BoundaryEdge(int vertexA, int vertexB, Vector3 center)
        {
            VertexA = vertexA; VertexB = vertexB; Center = center; A = B = Interior = center; Count = 1;
        }
    }

    private sealed class ResidentState
    {
        public readonly Transform Root;
        public readonly Transform Visual;
        public int CurrentNode, PreviousNode, TargetNode;
        public readonly float Speed, StepRate, Phase;
        public ResidentState(Transform root, Transform visual, int current, int previous, int target, float speed, float stepRate, float phase)
        {
            Root = root; Visual = visual; CurrentNode = current; PreviousNode = previous; TargetNode = target;
            Speed = speed; StepRate = stepRate; Phase = phase;
        }
    }

    private sealed class VariantDescription
    {
        public readonly string Name;
        public readonly bool IsChild, IsFemale;
        public readonly float HeightScale;
        public readonly Color ClothesColor;
        public readonly Color HairColor;
        public VariantDescription(string name, bool isChild, bool isFemale, float heightScale, Color clothesColor, Color hairColor)
        {
            Name = name; IsChild = isChild; IsFemale = isFemale; HeightScale = heightScale; ClothesColor = clothesColor; HairColor = hairColor;
        }
    }

    private struct EdgeKey : IEquatable<EdgeKey>
    {
        private readonly long ax, az, bx, bz;
        public EdgeKey(Vector3 a, Vector3 b)
        {
            long x1 = (long)Math.Round(a.x * 10.0), z1 = (long)Math.Round(a.z * 10.0);
            long x2 = (long)Math.Round(b.x * 10.0), z2 = (long)Math.Round(b.z * 10.0);
            if (x1 < x2 || (x1 == x2 && z1 <= z2)) { ax = x1; az = z1; bx = x2; bz = z2; }
            else { ax = x2; az = z2; bx = x1; bz = z1; }
        }
        public bool Equals(EdgeKey other) => ax == other.ax && az == other.az && bx == other.bx && bz == other.bz;
        public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ax, az, bx, bz);
    }
}
