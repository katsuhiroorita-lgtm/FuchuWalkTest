using System.Collections;
using System.Collections.Generic;
using Clipper2Lib;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Records the open walking trail and keeps closed areas for the current play session.</summary>
public sealed class FuchuWalkingTerritory : MonoBehaviour
{
    private const float SampleSpacing = 0.3f;
    private const float CloseDistance = 0.35f;
    private const float FillSpacing = 2.5f;
    private const float FillHeight = 0.07f;
    private const float TrailHeight = 0.13f;
    private const double PlayableGroundArea = System.Math.PI * 1000.0 * 1000.0;
    private static readonly Quaternion TrailRotation = Quaternion.Euler(90f, 0f, 0f);

    private readonly List<Vector2> trail = new List<Vector2>();
    private readonly FuchuTerritoryShape shape = new FuchuTerritoryShape();
    private readonly List<LineRenderer> borders = new List<LineRenderer>();
    private readonly List<Collider> ground = new List<Collider>();
    private readonly Queue<(Paths64 Triangles, float BorderHeight)> pendingFills = new Queue<(Paths64, float)>();
    private readonly List<(Paths64 Triangles, float BorderHeight)> gpuFills = new List<(Paths64, float)>();
    private readonly List<Mesh> fillMeshes = new List<Mesh>();
    private Transform worldRoot;
    private LineRenderer openLine;
    private Material lineMaterial;
    private Material fillMaterial;
    private FuchuTerritoryBuildings buildings;
    private FuchuGpuGroundFill gpuGround;
    private bool fillingGround;
    private bool gpuBordersPending;
    private bool runFinished;
    private float gpuBorderHeight;
    private float groundSearchHeight;
    private float lastTrailHeight;
    private uint replicatedGeneration;
    private int replicatedCount;

    private FuchuBattleMatch match;
    internal FuchuTerritoryShape Shape => shape;
    private readonly List<GameObject> fillObjects = new List<GameObject>();

    public double Coverage => shape.ClaimedSquareMeters / PlayableGroundArea;
    public double BuildingPoints => buildings?.TotalPoints ?? 0.0;
    public double StaminaCapacityPoints => buildings?.StaminaCapacityPoints ?? 0.0;
    public double MovementPoints => buildings?.MovementPoints ?? 0.0;
    public double ToughnessPoints => buildings?.ToughnessPoints ?? 0.0;

    internal void Initialize(Vector3 playerPosition, FuchuBuildingEffects effects, Color color, FuchuBattleMatch battle, bool localPlayer)
    {
        match = battle;
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

        lineMaterial = CreateMaterial(shader, color, 3010, false);
        fillMaterial = CreateMaterial(shader, new Color(color.r, color.g, color.b, 0.68f), 3000, false);
        openLine = CreateLine("Open trail");
        if (localPlayer) buildings = new FuchuTerritoryBuildings(worldRoot, effects, battle);
        gpuGround = FuchuGpuGroundFill.TryCreate(worldRoot, ground, fillMaterial.color);
        Debug.Log(gpuGround != null ? "エリアの地面描画: GPU" : "エリアの地面描画: 差分メッシュ", this);
    }

    private void Update()
    {
        if (!runFinished && Time.timeScale > 0f)
            buildings?.RefreshLoadedBuildings();
    }

    private void LateUpdate()
    {
        if (gpuGround == null) return;
        try
        {
            if (gpuGround.TryDraw()) return;
        }
        catch (UnityException exception)
        {
            Debug.LogWarning("GPU ground overlay failed: " + exception.Message, this);
        }
        FallBackToMeshes();
    }

    public void RecordGroundStep(Vector3 position, bool evaluateClaims = true)
    {
        if (runFinished || openLine == null) return;
        Vector3 local = ToLocal(position);
        var point = new Vector2(local.x, local.z);
        if (trail.Count > 0 && Vector2.Distance(trail[trail.Count - 1], point) < SampleSpacing)
            return;

        // Project movement onto the ground during jumps as well as when walking.
        if (!TryGroundPoint(point, TrailHeight, out Vector3 groundPoint))
            return;

        if (evaluateClaims && (shape.TryClaimAgainstBoundary(trail, point, CloseDistance, out Paths64 triangles) ||
            (trail.Count >= 5 && TryCloseTrail(point, out triangles))))
        {
            match.Claim(this, shape.LastClaim);
            DrawClaimedGround(triangles, groundPoint.y);
            buildings?.RefreshLoadedBuildings();
            ClearOpenTrail();
            // A new trail may start on the border that was just confirmed.
            shape.TryClaimAgainstBoundary(trail, point, CloseDistance, out _);
        }

        lastTrailHeight = groundPoint.y;
        trail.Add(point);
        openLine.positionCount = trail.Count;
        openLine.SetPosition(trail.Count - 1, Quaternion.Inverse(TrailRotation) * groundPoint);
    }

    internal void CompleteNpcLoop(List<Vector2> polygon)
    {
        if (!runFinished && openLine != null && shape.TryClaimLoop(polygon, out Paths64 triangles))
        {
            match.Claim(this, shape.LastClaim);
            DrawClaimedGround(triangles, lastTrailHeight);
        }
        // A completed walk must end even when the polygon adds no new area.
        ClearOpenTrail();
    }

    internal int OpenTrailPointCount => trail.Count;

    internal void RefreshBuildingOwnership() => buildings?.RefreshLoadedBuildings();

    internal void RemoveClaim(Paths64 claimed)
    {
        if (!shape.Remove(claimed)) return;
        // SetPaths invalidates old boundary contacts, but the visible open trail remains playable.
        RebuildOwnedGround();
    }

    internal void ReplaceReplicatedShape(Paths64 paths)
    {
        if (shape.PathsMatch(paths)) return;
        shape.ReplaceFromAuthority(paths);
        RebuildOwnedGround();
    }

    internal void DrawReplicatedTrail(IReadOnlyList<Vector2> points, uint generation)
    {
        if (openLine == null) return;
        if (generation != replicatedGeneration || points.Count < replicatedCount)
        {
            openLine.positionCount = 0;
            replicatedCount = 0;
            replicatedGeneration = generation;
        }
        if (points.Count == replicatedCount) return;
        openLine.positionCount = points.Count;
        Quaternion inverse = Quaternion.Inverse(TrailRotation);
        for (int i = replicatedCount; i < points.Count; i++)
        {
            if (!TryGroundPoint(points[i], TrailHeight, out Vector3 point))
                point = new Vector3(points[i].x, lastTrailHeight, points[i].y);
            lastTrailHeight = point.y;
            openLine.SetPosition(i, inverse * point);
        }
        replicatedCount = points.Count;
    }

    private void RebuildOwnedGround()
    {
        StopAllCoroutines();
        fillingGround = false;
        gpuBordersPending = false;
        pendingFills.Clear();
        gpuFills.Clear();
        foreach (GameObject obj in fillObjects) Destroy(obj);
        fillObjects.Clear();
        foreach (Mesh mesh in fillMeshes) Destroy(mesh);
        fillMeshes.Clear();
        Paths64 triangles = shape.Triangles();
        if (triangles.Count > 0) gpuFills.Add((triangles, gpuBorderHeight));
        if (gpuGround != null && !gpuGround.TryReplace(triangles)) FallBackToMeshes();
        else if (gpuGround == null && triangles.Count > 0)
        {
            pendingFills.Enqueue((triangles, gpuBorderHeight));
            StartPendingMeshFills();
        }
        RedrawBorders(gpuBorderHeight);
    }

    private void DrawClaimedGround(Paths64 triangles, float borderHeight)
    {
        gpuBorderHeight = borderHeight;
        if (gpuGround == null)
        {
            pendingFills.Enqueue((triangles, borderHeight));
            StartPendingMeshFills();
            return;
        }

        gpuFills.Add((triangles, borderHeight));
        if (!gpuGround.TryStamp(triangles))
        {
            FallBackToMeshes();
            return;
        }

        gpuBorderHeight = borderHeight;
        if (gpuBordersPending) return;
        gpuBordersPending = true;
        StartCoroutine(RedrawGpuBorders());
    }

    private IEnumerator RedrawGpuBorders()
    {
        // Let the new fill reach the screen before resampling the full outer border.
        yield return null;
        gpuBordersPending = false;
        if (gpuGround != null) RedrawBorders(gpuBorderHeight);
    }

    private void FallBackToMeshes()
    {
        gpuGround.Dispose();
        gpuGround = null;
        foreach (var fill in gpuFills) pendingFills.Enqueue(fill);
        gpuFills.Clear();
        Debug.LogWarning("GPUで地面を描画できなくなったため、差分メッシュに切り替えます。", this);
        StartPendingMeshFills();
    }

    private void StartPendingMeshFills()
    {
        if (fillingGround || pendingFills.Count == 0) return;
        fillingGround = true;
        StartCoroutine(DrawPendingGround());
    }

    public void ClearOpenTrail()
    {
        trail.Clear();
        shape.ClearOpenTrail();
        if (openLine != null) openLine.positionCount = 0;
    }

    internal void FinishRun()
    {
        runFinished = true;
        ClearOpenTrail();
    }

    private bool TryCloseTrail(Vector2 point, out Paths64 triangles)
    {
        Vector2 last = trail[trail.Count - 1];
        Vector2 step = point - last;
        var crossings = new List<(float Order, int Segment, Vector2 Point)>();
        for (int i = 0; i < trail.Count - 4; i++)
        {
            Vector2 edge = trail[i + 1] - trail[i];
            if (FuchuTerritoryShape.TryIntersect(last, step, trail[i], edge, out float alongStep))
            {
                crossings.Add((alongStep, i, last + step * alongStep));
                continue;
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
            if (shape.TryClaimLoop(polygon, out triangles)) return true;
        }
        triangles = null;
        return false;
    }

    private void RedrawBorders(float fallbackHeight)
    {
        foreach (LineRenderer border in borders) Destroy(border.gameObject);
        borders.Clear();
        Quaternion toLineSpace = Quaternion.Inverse(TrailRotation);
        foreach (List<Vector2> contour in shape.Contours)
        {
            var points = new List<Vector3>();
            for (int i = 0; i < contour.Count; i++)
            {
                Vector2 a = contour[i], b = contour[(i + 1) % contour.Count];
                int segments = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / SampleSpacing));
                for (int j = 0; j < segments; j++)
                {
                    Vector2 edge = Vector2.Lerp(a, b, (float)j / segments);
                    if (!TryGroundPoint(edge, TrailHeight, out Vector3 groundPoint))
                        groundPoint = new Vector3(edge.x, fallbackHeight, edge.y);
                    points.Add(toLineSpace * groundPoint);
                }
            }
            LineRenderer border = CreateLine("Confirmed border");
            border.loop = true;
            border.positionCount = points.Count;
            border.SetPositions(points.ToArray());
            borders.Add(border);
        }
    }

    private IEnumerator DrawPendingGround()
    {
        while (pendingFills.Count > 0)
        {
            var fill = pendingFills.Dequeue();
            yield return FillNewGround(fill.Triangles);
            if (pendingFills.Count == 0) RedrawBorders(fill.BorderHeight);
        }
        fillingGround = false;
    }

    private IEnumerator FillNewGround(Paths64 triangles)
    {
        var pending = new Stack<(Vector2 A, Vector2 B, Vector2 C)>();
        foreach (Path64 triangle in triangles)
            pending.Push((FuchuTerritoryShape.ToVector(triangle[0]),
                FuchuTerritoryShape.ToVector(triangle[1]), FuchuTerritoryShape.ToVector(triangle[2])));
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
        var mesh = new Mesh { name = "Confirmed area", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(indices, 0);
        fillMeshes.Add(mesh);
        var obj = new GameObject("Confirmed area");
        obj.transform.SetParent(transform, false);
        fillObjects.Add(obj);
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
        gpuGround?.Dispose();
        buildings?.Dispose();
        foreach (Mesh mesh in fillMeshes) Destroy(mesh);
        Destroy(lineMaterial);
        Destroy(fillMaterial);
    }

}
