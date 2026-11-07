using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Records the open walking trail and keeps closed areas for the current play session.</summary>
public sealed class FuchuWalkingTerritory : MonoBehaviour
{
    private const float SampleSpacing = 0.3f;
    private const float CloseDistance = 0.35f;
    private const float MinimumArea = 2f;
    private const float FillSpacing = 2.5f;
    private const float FillHeight = 0.07f;
    private const float TrailHeight = 0.13f;
    private static readonly Quaternion TrailRotation = Quaternion.Euler(90f, 0f, 0f);

    private readonly List<Vector2> trail = new List<Vector2>();
    private readonly List<Region> regions = new List<Region>();
    private readonly List<Mesh> fillMeshes = new List<Mesh>();
    private readonly List<Collider> ground = new List<Collider>();
    private Transform worldRoot;
    private LineRenderer openLine;
    private Material lineMaterial;
    private Material fillMaterial;
    private Material buildingMaterial;
    private FuchuTerritoryBuildings buildings;
    private float groundSearchHeight;

    internal void Initialize(Vector3 playerPosition)
    {
        worldRoot = transform.parent;
        groundSearchHeight = ToLocal(playerPosition).y + 200f;
        foreach (string surface in new[] { "FuchuLocalDEMTerrain_1000m", "FuchuLocalRoads_1000m", "TemporaryWalkGround" })
        {
            GameObject obj = GameObject.Find(surface);
            if (obj != null && obj.TryGetComponent(out Collider collider) && collider.enabled)
                ground.Add(collider);
        }

        Shader shader = Resources.Load<Shader>("FuchuTerritoryColor");
        if (shader == null)
        {
            Debug.LogWarning("The walking territory shader could not be loaded.", this);
            return;
        }

        lineMaterial = CreateMaterial(shader, new Color(0.06f, 0.48f, 1f), 3010, false);
        fillMaterial = CreateMaterial(shader, new Color(0.04f, 0.27f, 0.9f, 0.68f), 3000, false);
        buildingMaterial = CreateMaterial(shader, new Color(0.07f, 0.38f, 0.96f), 2010, true);
        openLine = CreateLine("Open trail");
        buildings = new FuchuTerritoryBuildings(worldRoot, buildingMaterial, regions);
    }

    private void Update()
    {
        if (regions.Count > 0)
            buildings.RefreshLoadedBuildings();
    }

    public void RecordGroundStep(Vector3 position)
    {
        if (openLine == null) return;
        Vector3 local = ToLocal(position);
        var point = new Vector2(local.x, local.z);
        if (trail.Count > 0)
        {
            float distance = Vector2.Distance(trail[trail.Count - 1], point);
            if (distance < SampleSpacing) return;
            if (distance > 3f) ClearOpenTrail();
        }
        if (!TryGroundPoint(point, TrailHeight, out Vector3 groundPoint))
        {
            ClearOpenTrail();
            return;
        }

        if (trail.Count >= 5 && TryCloseTrail(point, out Region region, out List<int> triangles))
        {
            regions.Add(region);
            LineRenderer border = CreateLine("Confirmed border");
            border.loop = true;
            border.positionCount = region.Points.Count;
            for (int i = 0; i < region.Points.Count; i++)
            {
                Vector2 edgePoint = region.Points[i];
                if (!TryGroundPoint(edgePoint, TrailHeight, out Vector3 borderPoint))
                    borderPoint = new Vector3(edgePoint.x, groundPoint.y, edgePoint.y);
                border.SetPosition(i, Quaternion.Inverse(TrailRotation) * borderPoint);
            }
            StartCoroutine(FillRegion(region, triangles));
            buildings.RefreshLoadedBuildings();
            ClearOpenTrail();
        }

        trail.Add(point);
        openLine.positionCount = trail.Count;
        openLine.SetPosition(trail.Count - 1, Quaternion.Inverse(TrailRotation) * groundPoint);
    }

    public void ClearOpenTrail()
    {
        trail.Clear();
        if (openLine != null) openLine.positionCount = 0;
    }

    private bool TryCloseTrail(Vector2 point, out Region region, out List<int> triangles)
    {
        Vector2 last = trail[trail.Count - 1];
        Vector2 step = point - last;
        var crossings = new List<(float Order, int Segment, Vector2 Point)>();
        for (int i = 0; i < trail.Count - 4; i++)
        {
            Vector2 edge = trail[i + 1] - trail[i];
            Vector2 separation = trail[i] - last;
            float determinant = Cross(step, edge);
            if (Mathf.Abs(determinant) > 0.00001f)
            {
                float alongStep = Cross(separation, edge) / determinant;
                float alongEdge = Cross(separation, step) / determinant;
                if (alongStep > 0.0001f && alongStep <= 1f && alongEdge >= 0f && alongEdge <= 1f)
                {
                    crossings.Add((alongStep, i, last + step * alongStep));
                    continue;
                }
            }
            // Allow reconnecting to the visible stroke without hitting its exact centerline.
            Vector2 closest = trail[i] + edge * Mathf.Clamp01(Vector2.Dot(point - trail[i], edge) / edge.sqrMagnitude);
            float distance = Vector2.Distance(closest, point);
            if (distance <= CloseDistance && Vector2.Distance(closest, last) > distance)
                crossings.Add((1f + distance, i, closest));
        }

        crossings.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var crossing in crossings)
        {
            var polygon = new List<Vector2> { crossing.Point };
            for (int i = crossing.Segment + 1; i < trail.Count; i++)
            {
                if ((trail[i] - polygon[polygon.Count - 1]).sqrMagnitude > 0.000001f)
                    polygon.Add(trail[i]);
            }
            if ((polygon[0] - polygon[polygon.Count - 1]).sqrMagnitude < 0.000001f)
                polygon.RemoveAt(polygon.Count - 1);
            if (!TryTriangulate(polygon, out triangles)) continue;
            region = new Region(polygon);
            return true;
        }
        region = null;
        triangles = null;
        return false;
    }

    private static bool TryTriangulate(List<Vector2> polygon, out List<int> triangles)
    {
        triangles = new List<int>();
        float twiceArea = 0f;
        Vector2 origin = polygon[0];
        for (int i = 1; i + 1 < polygon.Count; i++)
            twiceArea += Cross(polygon[i] - origin, polygon[i + 1] - origin);
        if (Mathf.Abs(twiceArea) < 2f * MinimumArea) return false;

        var remaining = new List<int>(polygon.Count);
        for (int i = 0; i < polygon.Count; i++)
            remaining.Add(twiceArea > 0f ? i : polygon.Count - 1 - i);

        while (remaining.Count > 2)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int previous = remaining[(i + remaining.Count - 1) % remaining.Count];
                int current = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];
                Vector2 a = polygon[previous], b = polygon[current], c = polygon[next];
                float turn = Cross(b - a, c - b);
                if (Mathf.Abs(turn) <= 0.02f * (c - a).magnitude && Vector2.Dot(b - a, c - b) >= 0f)
                {
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (turn <= 0f) continue;

                bool containsVertex = false;
                foreach (int other in remaining)
                {
                    if (other == previous || other == current || other == next) continue;
                    Vector2 p = polygon[other];
                    if (Cross(b - a, p - a) >= 0f && Cross(c - b, p - b) >= 0f && Cross(a - c, p - c) >= 0f)
                    {
                        containsVertex = true;
                        break;
                    }
                }
                if (containsVertex) continue;
                triangles.Add(previous);
                triangles.Add(current);
                triangles.Add(next);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) return false;
        }
        return triangles.Count > 0;
    }

    private IEnumerator FillRegion(Region region, List<int> triangles)
    {
        var pending = new Stack<(Vector2 A, Vector2 B, Vector2 C)>();
        for (int i = 0; i < triangles.Count; i += 3)
            pending.Push((region.Points[triangles[i]], region.Points[triangles[i + 1]], region.Points[triangles[i + 2]]));
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var sampled = new Dictionary<Vector2, int>();
        int work = 0;
        while (pending.Count > 0)
        {
            if (++work % 512 == 0) yield return null;
            var (a, b, c) = pending.Pop();
            float ab = (a - b).sqrMagnitude, bc = (b - c).sqrMagnitude, ca = (c - a).sqrMagnitude;
            float longest = Mathf.Max(ab, Mathf.Max(bc, ca));
            if (longest > FillSpacing * FillSpacing)
            {
                // Rotate the triangle so its longest edge is the first one.
                if (longest == bc) (a, b, c) = (b, c, a);
                else if (longest == ca) (a, b, c) = (c, a, b);
                Vector2 middle = (a + b) * 0.5f;
                pending.Push((a, middle, c));
                pending.Push((middle, b, c));
                continue;
            }
            int ai = Sample(a), bi = Sample(b), ci = Sample(c);
            if (ai < 0 || bi < 0 || ci < 0) continue;
            indices.Add(ai);
            indices.Add(ci);
            indices.Add(bi);
        }

        if (indices.Count == 0) yield break;
        var mesh = new Mesh { name = "Confirmed blue area", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(indices, 0);
        fillMeshes.Add(mesh);
        var obj = new GameObject("Confirmed blue area");
        obj.transform.SetParent(transform, false);
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = fillMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;

        int Sample(Vector2 point)
        {
            if (sampled.TryGetValue(point, out int index)) return index;
            index = -1;
            if (TryGroundPoint(point, FillHeight, out Vector3 groundPoint))
            {
                index = vertices.Count;
                vertices.Add(groundPoint);
            }
            sampled.Add(point, index);
            return index;
        }
    }

    private bool TryGroundPoint(Vector2 point, float lift, out Vector3 result)
    {
        Vector3 localStart = new Vector3(point.x, groundSearchHeight, point.y);
        Vector3 up = worldRoot != null ? worldRoot.up : Vector3.up;
        Vector3 start = worldRoot != null ? worldRoot.TransformPoint(localStart) : localStart;
        var ray = new Ray(start, -up);
        float nearest = 400f;
        result = default;
        bool found = false;
        foreach (Collider surface in ground)
        {
            if (surface == null || !surface.enabled || !surface.gameObject.activeInHierarchy ||
                !surface.Raycast(ray, out RaycastHit hit, nearest)) continue;
            nearest = hit.distance;
            result = ToLocal(hit.point) + Vector3.up * lift;
            found = true;
        }
        return found;
    }

    private Vector3 ToLocal(Vector3 point) => worldRoot != null ? worldRoot.InverseTransformPoint(point) : point;

    private LineRenderer CreateLine(string label)
    {
        var obj = new GameObject(label);
        obj.transform.SetParent(transform, false);
        obj.transform.localRotation = TrailRotation;
        LineRenderer line = obj.AddComponent<LineRenderer>();
        line.sharedMaterial = lineMaterial;
        line.useWorldSpace = false;
        line.alignment = LineAlignment.TransformZ;
        line.widthMultiplier = 0.32f;
        line.numCornerVertices = 3;
        line.positionCount = 0;
        line.shadowCastingMode = ShadowCastingMode.Off;
        return line;
    }

    private static Material CreateMaterial(Shader shader, Color color, int queue, bool solid)
    {
        var material = new Material(shader) { color = color, renderQueue = queue };
        material.SetFloat("_ZWrite", solid ? 1f : 0f);
        material.SetFloat("_Shade", solid ? 1f : 0f);
        return material;
    }

    private void OnDestroy()
    {
        buildings?.Dispose();
        foreach (Mesh mesh in fillMeshes) Destroy(mesh);
        Destroy(lineMaterial);
        Destroy(fillMaterial);
        Destroy(buildingMaterial);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    internal sealed class Region
    {
        public readonly List<Vector2> Points;
        public readonly Rect Bounds;

        public Region(List<Vector2> points)
        {
            Points = points;
            Vector2 min = points[0], max = points[0];
            foreach (Vector2 point in points)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            Bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        public bool Contains(Vector2 point)
        {
            if (!Bounds.Contains(point)) return false;
            bool inside = false;
            for (int i = 0, previous = Points.Count - 1; i < Points.Count; previous = i++)
            {
                Vector2 a = Points[previous], b = Points[i];
                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < a.x + (b.x - a.x) * (point.y - a.y) / (b.y - a.y))
                    inside = !inside;
            }
            return inside;
        }
    }
}
