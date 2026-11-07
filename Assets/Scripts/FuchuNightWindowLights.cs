using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Adds lightweight warm window lights to large vertical city surfaces at night.</summary>
public sealed class FuchuNightWindowLights : MonoBehaviour
{
    [HideInInspector] public Transform worldRoot;
    [HideInInspector] public Vector3 initialPosition;
    [Min(10f)] public float radiusMeters = 1000f;

    private readonly HashSet<int> processedFilters = new HashSet<int>();
    private readonly HashSet<WindowCell> occupiedCells = new HashSet<WindowCell>();
    private readonly List<MeshRenderer> windowRenderers = new List<MeshRenderer>();
    private Material windowMaterial;
    private FuchuWalkHud walkHud;
    private float nextScanTime;
    private bool lightsEnabled;
    private int totalWindows;

    private const int MaxWindowsPerMesh = 320;
    private const int MaxWindowsTotal = 12000;
    private const float WindowDensityArea = 13f;
    private const float ScanInterval = 3f;

    private void Awake()
    {
        walkHud = GetComponent<FuchuWalkHud>();
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        windowMaterial = new Material(shader) { name = "FuchuNightWindowGlow", enableInstancing = true };
        windowMaterial.color = new Color(0.95f, 0.72f, 0.38f);
        windowMaterial.EnableKeyword("_EMISSION");
        windowMaterial.SetColor("_EmissionColor", new Color(1f, 0.55f, 0.19f) * 3f);
    }

    private void Update()
    {
        float nightFactor = walkHud != null ? walkHud.NightFactor : 0f;
        bool shouldEnable = nightFactor > 0.28f;
        if (shouldEnable != lightsEnabled)
        {
            lightsEnabled = shouldEnable;
            SetWindowRenderersEnabled(lightsEnabled);
        }
        windowMaterial.SetColor("_EmissionColor", new Color(1f, 0.55f, 0.19f) * Mathf.Lerp(0.2f, 3.5f, nightFactor));

        if (totalWindows >= MaxWindowsTotal || Time.unscaledTime < nextScanTime) return;
        nextScanTime = Time.unscaledTime + ScanInterval;
        ScanForBuildingMeshes();
    }

    private void ScanForBuildingMeshes()
    {
        MeshFilter[] filters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        foreach (MeshFilter filter in filters)
        {
            if (filter == null || !filter.gameObject.activeInHierarchy || filter.sharedMesh == null ||
                filter.gameObject.name == "FuchuNightWindowQuads" ||
                filter.gameObject.name == FuchuTerritoryBuildings.OverlayName || processedFilters.Contains(filter.GetInstanceID())) continue;
            MeshRenderer sourceRenderer = filter.GetComponent<MeshRenderer>();
            if (sourceRenderer == null || !sourceRenderer.enabled) continue;
            Bounds bounds = sourceRenderer.bounds;
            if (bounds.size.y < 7f || Mathf.Max(bounds.size.x, bounds.size.z) < 3f) continue;
            Vector3 localCenter = worldRoot != null ? worldRoot.InverseTransformPoint(bounds.center) : bounds.center;
            float dx = localCenter.x - initialPosition.x;
            float dz = localCenter.z - initialPosition.z;
            if (dx * dx + dz * dz > radiusMeters * radiusMeters * 1.44f) continue;

            processedFilters.Add(filter.GetInstanceID());
            try
            {
                totalWindows += CreateWindows(filter);
            }
            catch (UnityException exception)
            {
                Debug.LogWarning("夜間の窓明かりをこの建物モデルには作成できませんでした: " + exception.Message, filter);
            }
            if (totalWindows >= MaxWindowsTotal) break;
        }
    }

    private int CreateWindows(MeshFilter source)
    {
        Mesh sourceMesh = source.sharedMesh;
        Vector3[] vertices = sourceMesh.vertices;
        int[] triangles = sourceMesh.triangles;
        var windowVertices = new List<Vector3>();
        var windowNormals = new List<Vector3>();
        var windowTriangles = new List<int>();
        System.Random random = new System.Random(source.GetInstanceID());

        for (int i = 0; i + 2 < triangles.Length && windowVertices.Count / 4 < MaxWindowsPerMesh && totalWindows + windowVertices.Count / 4 < MaxWindowsTotal; i += 3)
        {
            Vector3 a = vertices[triangles[i]];
            Vector3 b = vertices[triangles[i + 1]];
            Vector3 c = vertices[triangles[i + 2]];
            Vector3 worldA = source.transform.TransformPoint(a);
            Vector3 worldB = source.transform.TransformPoint(b);
            Vector3 worldC = source.transform.TransformPoint(c);
            Vector3 cross = Vector3.Cross(worldB - worldA, worldC - worldA);
            float area = cross.magnitude * 0.5f;
            if (area < 0.18f) continue;
            Vector3 worldNormal = cross.normalized;
            if (Mathf.Abs(worldNormal.y) > 0.28f) continue;

            int count = Mathf.FloorToInt(area / WindowDensityArea);
            if (random.NextDouble() < (area / WindowDensityArea) - count) count++;
            count = Mathf.Min(count, 6);
            if (count == 0) continue;

            Vector3 localNormal = source.transform.InverseTransformDirection(worldNormal).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, worldNormal).normalized;
            if (right.sqrMagnitude < 0.01f) continue;
            Vector3 localRight = source.transform.InverseTransformDirection(right).normalized;
            Vector3 localUp = source.transform.InverseTransformDirection(Vector3.up).normalized;

            for (int window = 0; window < count && windowVertices.Count / 4 < MaxWindowsPerMesh; window++)
            {
                float r1 = Mathf.Sqrt((float)random.NextDouble());
                float r2 = (float)random.NextDouble();
                Vector3 localPoint = (1f - r1) * a + (r1 * (1f - r2)) * b + (r1 * r2) * c;
                Vector3 worldPoint = source.transform.TransformPoint(localPoint);
                if (!occupiedCells.Add(new WindowCell(worldPoint))) continue;

                float halfWidth = 0.15f + (float)random.NextDouble() * 0.07f;
                float halfHeight = 0.20f + (float)random.NextDouble() * 0.08f;
                Vector3 center = localPoint + localNormal * 0.018f;
                int first = windowVertices.Count;
                windowVertices.Add(center - localRight * halfWidth - localUp * halfHeight);
                windowVertices.Add(center + localRight * halfWidth - localUp * halfHeight);
                windowVertices.Add(center + localRight * halfWidth + localUp * halfHeight);
                windowVertices.Add(center - localRight * halfWidth + localUp * halfHeight);
                for (int vertex = 0; vertex < 4; vertex++) windowNormals.Add(localNormal);
                windowTriangles.Add(first); windowTriangles.Add(first + 1); windowTriangles.Add(first + 2);
                windowTriangles.Add(first); windowTriangles.Add(first + 2); windowTriangles.Add(first + 3);
            }
        }

        if (windowTriangles.Count == 0) return 0;
        var windowMesh = new Mesh { name = "FuchuNightWindows_" + source.GetInstanceID() };
        windowMesh.indexFormat = windowVertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        windowMesh.SetVertices(windowVertices);
        windowMesh.SetNormals(windowNormals);
        windowMesh.SetTriangles(windowTriangles, 0);
        windowMesh.RecalculateBounds();
        GameObject windowObject = new GameObject("FuchuNightWindowQuads");
        windowObject.transform.SetParent(source.transform, false);
        MeshFilter windowFilter = windowObject.AddComponent<MeshFilter>();
        windowFilter.sharedMesh = windowMesh;
        MeshRenderer renderer = windowObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = windowMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.enabled = lightsEnabled;
        windowRenderers.Add(renderer);
        return windowVertices.Count / 4;
    }

    private void SetWindowRenderersEnabled(bool enabled)
    {
        for (int i = windowRenderers.Count - 1; i >= 0; i--)
        {
            MeshRenderer renderer = windowRenderers[i];
            if (renderer == null) windowRenderers.RemoveAt(i);
            else renderer.enabled = enabled;
        }
    }

    private void OnDestroy()
    {
        if (windowMaterial != null) Destroy(windowMaterial);
    }

    private readonly struct WindowCell : System.IEquatable<WindowCell>
    {
        private readonly int x, y, z;
        public WindowCell(Vector3 point)
        {
            x = Mathf.RoundToInt(point.x / 0.9f);
            y = Mathf.RoundToInt(point.y / 0.9f);
            z = Mathf.RoundToInt(point.z / 0.9f);
        }
        public bool Equals(WindowCell other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is WindowCell other && Equals(other);
        public override int GetHashCode() => System.HashCode.Combine(x, y, z);
    }
}
