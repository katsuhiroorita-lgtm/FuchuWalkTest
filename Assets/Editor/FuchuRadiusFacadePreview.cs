using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Creates reversible facade samples on buildings within 1km of the scene start position.</summary>
public static class FuchuRadiusFacadePreview
{
    private const string GroupName = "FuchuFacadePreview_Radius1000m";
    private const string GeneratedFolder = "Assets/GeneratedFuchuFacadePreview";
    private const float RadiusMeters = 1000f;
    private static readonly XNamespace Gml = "http://www.opengis.net/gml";
    private static readonly XNamespace BuildingNs = "http://www.opengis.net/citygml/building/2.0";

    [MenuItem("Fuchu Walk/Create Facade Previews Within 1km")]
    private static void CreatePreviews()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "再生を停止してから実行してください。", "OK");
            return;
        }
        if (GameObject.Find(GroupName) != null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "1km範囲の外壁試作はすでにあります。作り直す場合は「Remove Facade Previews Within 1km」を先に実行してください。", "OK");
            return;
        }

        CesiumGeoreference georeference = UnityEngine.Object.FindFirstObjectByType<CesiumGeoreference>();
        GameObject player = GameObject.Find("FuchuWalkPlayer");
        GameObject terrainObject = GameObject.Find("FuchuLocalDEMTerrain_1000m");
        if (terrainObject == null) terrainObject = GameObject.Find("FuchuLocalDEMTerrain_3000m");
        MeshFilter terrainFilter = terrainObject != null ? terrainObject.GetComponent<MeshFilter>() : null;
        if (georeference == null || player == null || terrainFilter == null || terrainFilter.sharedMesh == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "CesiumGeoreference、FuchuWalkPlayer、またはDEM地形が見つかりません。シーンは変更していません。", "OK");
            return;
        }

        string gmlDirectory = FindBuildingDirectory();
        if (string.IsNullOrEmpty(gmlDirectory))
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "府中市の建物CityGMLフォルダーが見つかりません。", "OK");
            return;
        }

        Vector3 startLocal = georeference.transform.InverseTransformPoint(player.transform.position);
        double metersPerDegreeLat = 111132.92;
        double metersPerDegreeLon = 111319.49 * Math.Cos(georeference.latitude * Math.PI / 180.0);
        double startLat = georeference.latitude + startLocal.z / metersPerDegreeLat;
        double startLon = georeference.longitude + startLocal.x / metersPerDegreeLon;
        string[] meshCodes = GetMeshCodesAround(startLat, startLon, RadiusMeters).ToArray();
        List<string> files = meshCodes.Select(code => Path.Combine(gmlDirectory, code + "_bldg_6697_op.gml"))
            .Where(File.Exists).Distinct().ToList();
        if (files.Count == 0)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "開始位置の周辺に該当する建物CityGMLが見つかりません。", "OK");
            return;
        }

        var heightSampler = new TerrainHeightSampler(terrainFilter.sharedMesh, terrainObject.transform.localPosition);
        Material wallMaterial = GetMaterial("RadiusWall", new Color(0.67f, 0.66f, 0.62f));
        Material frameMaterial = GetMaterial("RadiusFrame", new Color(0.77f, 0.75f, 0.69f));
        Material glassMaterial = GetMaterial("RadiusGlass", new Color(0.16f, 0.25f, 0.29f));
        GameObject previous100mPreview = GameObject.Find("FuchuFacadePreview_Radius100m");
        if (previous100mPreview != null) Undo.DestroyObjectImmediate(previous100mPreview);
        Undo.SetCurrentGroupName("Create facade previews within 1km");
        int undoGroup = Undo.GetCurrentGroup();
        GameObject group = new GameObject(GroupName);
        Undo.RegisterCreatedObjectUndo(group, "Create facade previews within 1km");
        group.transform.SetParent(georeference.transform, false);

        int created = 0;
        int skipped = 0;
        try
        {
            for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                string filePath = files[fileIndex];
                EditorUtility.DisplayProgressBar("Fuchu Walk", "周辺の建物を確認しています…", (float)fileIndex / files.Count);
                using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(file, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Building" || reader.NamespaceURI != BuildingNs.NamespaceName)
                            continue;
                        XElement building = XNode.ReadFrom(reader) as XElement;
                        if (building != null)
                            ProcessBuilding(building, georeference, heightSampler, startLocal, group.transform,
                                wallMaterial, frameMaterial, glassMaterial, ref created, ref skipped);
                    }
                }
                GC.Collect();
            }
        }
        catch (Exception exception)
        {
            Undo.DestroyObjectImmediate(group);
            EditorUtility.DisplayDialog("Fuchu Walk", "CityGMLを読み込めませんでした。シーンへの配置を取り消しました。\n" + exception.Message, "OK");
            return;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (created == 0)
        {
            Undo.DestroyObjectImmediate(group);
            EditorUtility.DisplayDialog("Fuchu Walk", "1km以内に、外壁と位置を特定できる建物がありませんでした。", "OK");
            return;
        }
        EditorSceneManager.MarkSceneDirty(group.scene);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = group;
        EditorUtility.DisplayDialog("Fuchu Walk", "開始位置から1km以内の建物 " + created + " 棟に、外壁一面ずつ窓の試作を配置しました。\n形状や向きを安全に特定できなかった建物はスキップしています（" + skipped + "件）。\n\n確認後にCtrl+Sで保存してください。まとめて取り消す場合は「Remove Facade Previews Within 1km」を選んでください。", "OK");
    }

    [MenuItem("Fuchu Walk/Remove Facade Previews Within 1km")]
    private static void RemovePreviews()
    {
        GameObject group = GameObject.Find(GroupName);
        if (group == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "1km範囲の外壁試作はありません。", "OK");
            return;
        }
        Undo.DestroyObjectImmediate(group);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    private static string FindBuildingDirectory()
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string workspaceRoot = Directory.GetParent(projectRoot).FullName;
        string path = Path.Combine(workspaceRoot, "3Dモデリングゲーム用資料", "13206_fuchu-shi_pref_2025_citygml_1_op", "udx", "bldg");
        return Directory.Exists(path) ? path : null;
    }

    private static void ProcessBuilding(XElement building, CesiumGeoreference georeference, TerrainHeightSampler heightSampler,
        Vector3 startLocal, Transform parent, Material wallMaterial, Material frameMaterial, Material glassMaterial,
        ref int created, ref int skipped)
    {
        if (!TryGetFootprint(building, georeference, out List<Vector2> footprint)) return;
        if (DistanceToFootprint(new Vector2(startLocal.x, startLocal.z), footprint) > RadiusMeters) return;

        XElement wall = FindLargestWall(building, georeference, startLocal, out WallCorners rawCorners);
        if (wall == null || rawCorners == null) { skipped++; return; }
        try
        {
            Vector3[] corners = ConvertCorners(georeference, heightSampler, rawCorners);
            Vector3 edge = corners[1] - corners[0];
            float width = edge.magnitude;
            float height = (Vector3.Distance(corners[0], corners[3]) + Vector3.Distance(corners[1], corners[2])) * 0.5f;
            if (width < 2f || height < 2f) { skipped++; return; }

            Vector3 normal = Vector3.Cross(edge.normalized, Vector3.up).normalized;
            Vector3 center = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            Vector2 footprintCenter = Average(footprint);
            Vector3 awayFromBuilding = new Vector3(center.x - footprintCenter.x, 0f, center.z - footprintCenter.y);
            if (awayFromBuilding.sqrMagnitude < 0.01f) { skipped++; return; }
            if (Vector3.Dot(normal, awayFromBuilding) < 0f) normal = -normal;

            GameObject facade = new GameObject("Facade_" + GetBuildingId(building));
            Undo.RegisterCreatedObjectUndo(facade, "Create generated building facade");
            facade.transform.SetParent(parent, false);
            facade.transform.localPosition = center + normal * 0.08f;
            facade.transform.localRotation = Quaternion.LookRotation(normal, Vector3.up);
            AddFacadeMeshes(facade, width, height, wallMaterial, frameMaterial, glassMaterial);
            created++;
        }
        catch
        {
            skipped++;
        }
    }

    private static IEnumerable<string> GetMeshCodesAround(double latitude, double longitude, float radius)
    {
        double latDelta = radius / 111132.92;
        double lonDelta = radius / (111319.49 * Math.Cos(latitude * Math.PI / 180.0));
        var codes = new HashSet<string>();
        const int samplesPerAxis = 80;
        for (int latIndex = 0; latIndex <= samplesPerAxis; latIndex++)
        {
            double lat = latitude - latDelta + 2.0 * latDelta * latIndex / samplesPerAxis;
            for (int lonIndex = 0; lonIndex <= samplesPerAxis; lonIndex++)
            {
                double lon = longitude - lonDelta + 2.0 * lonDelta * lonIndex / samplesPerAxis;
                codes.Add(ToMeshCode(lat, lon));
            }
        }
        return codes;
    }

    private static string ToMeshCode(double latitude, double longitude)
    {
        int firstLat = (int)Math.Floor(latitude * 1.5);
        int firstLon = (int)Math.Floor(longitude) - 100;
        double latRemainder = latitude - firstLat / 1.5;
        double lonRemainder = longitude - Math.Floor(longitude);
        int secondLat = (int)Math.Floor(latRemainder * 12.0);
        int secondLon = (int)Math.Floor(lonRemainder * 8.0);
        int thirdLat = (int)Math.Floor((latRemainder - secondLat / 12.0) * 120.0 + 1e-8);
        int thirdLon = (int)Math.Floor((lonRemainder - secondLon / 8.0) * 80.0 + 1e-8);
        thirdLat = Mathf.Clamp(thirdLat, 0, 9);
        thirdLon = Mathf.Clamp(thirdLon, 0, 9);
        return firstLat.ToString("00") + firstLon.ToString("00") + secondLat + secondLon + thirdLat + thirdLon;
    }

    private static string GetBuildingId(XElement building) => (string)building.Attribute(Gml + "id") ?? "Building";

    private static bool TryGetFootprint(XElement building, CesiumGeoreference georeference, out List<Vector2> points)
    {
        points = new List<Vector2>();
        foreach (XElement surface in building.Descendants(BuildingNs + "GroundSurface"))
        {
            foreach (XElement posList in surface.Descendants(Gml + "posList"))
            {
                double[] values = ParseCoordinates(posList.Value);
                for (int i = 0; i + 2 < values.Length; i += 3)
                {
                    Vector3 local = ToLocal(georeference, values[i], values[i + 1]);
                    points.Add(new Vector2(local.x, local.z));
                }
                if (points.Count > 2) return true;
            }
        }
        return false;
    }

    private static float DistanceToFootprint(Vector2 point, List<Vector2> polygon)
    {
        bool inside = false;
        float minimum = float.MaxValue;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector2 a = polygon[j];
            Vector2 b = polygon[i];
            if (((a.y > point.y) != (b.y > point.y)) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
            Vector2 segment = b - a;
            float lengthSquared = segment.sqrMagnitude;
            float t = lengthSquared > 0f ? Mathf.Clamp01(Vector2.Dot(point - a, segment) / lengthSquared) : 0f;
            minimum = Mathf.Min(minimum, Vector2.Distance(point, a + segment * t));
        }
        return inside ? 0f : minimum;
    }

    private static Vector2 Average(List<Vector2> points)
    {
        Vector2 sum = Vector2.zero;
        foreach (Vector2 point in points) sum += point;
        return sum / Mathf.Max(1, points.Count);
    }

    private static XElement FindLargestWall(XElement building, CesiumGeoreference georeference, Vector3 startLocal, out WallCorners bestCorners)
    {
        XElement best = null;
        bestCorners = null;
        double bestArea = 0.0;
        foreach (XElement polygon in building.Descendants(BuildingNs + "WallSurface").Descendants(Gml + "Polygon"))
        {
            XElement posList = polygon.Descendants(Gml + "posList").FirstOrDefault();
            if (posList == null) continue;
            if (!TryGetWallCorners(posList.Value, out WallCorners corners)) continue;
            Vector3 wallCenter = Vector3.zero;
            int[] cornerIndices = { corners.First, corners.Second, corners.TopSecond, corners.TopFirst };
            foreach (int cornerIndex in cornerIndices)
            {
                Vector3 local = ToLocal(georeference, corners.Values[cornerIndex * 3], corners.Values[cornerIndex * 3 + 1]);
                wallCenter += local * 0.25f;
            }
            float dx = wallCenter.x - startLocal.x;
            float dz = wallCenter.z - startLocal.z;
            if (dx * dx + dz * dz > (RadiusMeters + 5f) * (RadiusMeters + 5f)) continue;
            double area = corners.Width * corners.Height;
            if (area > bestArea && corners.Width >= 2.0 && corners.Height >= 2.0)
            {
                bestArea = area;
                best = polygon;
                bestCorners = corners;
            }
        }
        return best;
    }

    private static bool TryGetWallCorners(string text, out WallCorners corners)
    {
        corners = null;
        double[] values;
        try { values = ParseCoordinates(text); }
        catch { return false; }
        int count = values.Length / 3;
        if (count < 4) return false;
        double minHeight = double.MaxValue;
        double maxHeight = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            minHeight = Math.Min(minHeight, values[i * 3 + 2]);
            maxHeight = Math.Max(maxHeight, values[i * 3 + 2]);
        }
        if (maxHeight - minHeight < 2.0) return false;
        var bottom = new List<int>();
        for (int i = 0; i < count; i++)
            if (Math.Abs(values[i * 3 + 2] - minHeight) < 0.05) bottom.Add(i);
        if (bottom.Count < 2) return false;
        int first = bottom[0], second = bottom[1];
        double farthest = 0.0;
        foreach (int a in bottom)
            foreach (int b in bottom)
            {
                double distance = GroundDistance(values[a * 3], values[a * 3 + 1], values[b * 3], values[b * 3 + 1]);
                if (distance > farthest) { farthest = distance; first = a; second = b; }
            }
        int topSecond = FindMatchingVertex(values, count, second, maxHeight);
        int topFirst = FindMatchingVertex(values, count, first, maxHeight);
        if (topSecond < 0 || topFirst < 0 || farthest < 2.0) return false;
        corners = new WallCorners
        {
            Values = values,
            First = first,
            Second = second,
            TopSecond = topSecond,
            TopFirst = topFirst,
            Width = farthest,
            Height = maxHeight - minHeight,
            BaseHeight = minHeight
        };
        return true;
    }

    private static int FindMatchingVertex(double[] values, int count, int source, double height)
    {
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(values[i * 3 + 2] - height) >= 0.05) continue;
            if (Math.Abs(values[i * 3] - values[source * 3]) < 1e-8 && Math.Abs(values[i * 3 + 1] - values[source * 3 + 1]) < 1e-8)
                return i;
        }
        return -1;
    }

    private static Vector3[] ConvertCorners(CesiumGeoreference georeference, TerrainHeightSampler sampler, WallCorners wall)
    {
        int[] indices = { wall.First, wall.Second, wall.TopSecond, wall.TopFirst };
        Vector3[] result = new Vector3[4];
        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            double latitude = wall.Values[index * 3];
            double longitude = wall.Values[index * 3 + 1];
            Vector3 local = ToLocal(georeference, latitude, longitude);
            if (!sampler.TryGetHeight(local.x, local.z, out float terrainHeight))
                throw new InvalidOperationException("DEM地形の範囲外です。");
            float aboveBase = (float)(wall.Values[index * 3 + 2] - wall.BaseHeight);
            result[i] = new Vector3(local.x, terrainHeight + aboveBase + 0.06f, local.z);
        }
        return result;
    }

    private static Vector3 ToLocal(CesiumGeoreference georeference, double latitude, double longitude)
    {
        double metersPerDegreeLat = 111132.92;
        double metersPerDegreeLon = 111319.49 * Math.Cos(georeference.latitude * Math.PI / 180.0);
        return new Vector3((float)((longitude - georeference.longitude) * metersPerDegreeLon), 0f,
            (float)((latitude - georeference.latitude) * metersPerDegreeLat));
    }

    private static double[] ParseCoordinates(string text) => text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
        .Select(value => Double.Parse(value, CultureInfo.InvariantCulture)).ToArray();

    private static double GroundDistance(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        double meanLatitude = (latitudeA + latitudeB) * 0.5 * Math.PI / 180.0;
        double east = (longitudeB - longitudeA) * Math.PI / 180.0 * 6378137.0 * Math.Cos(meanLatitude);
        double north = (latitudeB - latitudeA) * Math.PI / 180.0 * 6378137.0;
        return Math.Sqrt(east * east + north * north);
    }

    private static void AddFacadeMeshes(GameObject facade, float width, float height, Material wall, Material frame, Material glass)
    {
        float marginX = Mathf.Min(1.2f, width * 0.06f);
        float marginY = Mathf.Min(0.9f, height * 0.12f);
        float usableWidth = Mathf.Max(0.5f, width - marginX * 2f);
        float usableHeight = Mathf.Max(0.5f, height - marginY * 2f);
        int columns = Mathf.Clamp(Mathf.FloorToInt(usableWidth / 4.0f), 1, 24);
        int rows = Mathf.Clamp(Mathf.FloorToInt(usableHeight / 3.0f), 1, 8);
        float cellWidth = usableWidth / columns;
        float cellHeight = usableHeight / rows;
        float windowWidth = Mathf.Min(1.7f, cellWidth * 0.62f);
        float windowHeight = Mathf.Min(2.0f, cellHeight * 0.62f);
        float frameSize = Mathf.Min(0.09f, windowWidth * 0.06f, windowHeight * 0.06f);
        float depth = 0.035f;

        var wallMesh = new QuadMeshBuilder();
        wallMesh.Add(-width * 0.5f, -height * 0.5f, width * 0.5f, height * 0.5f, 0f);
        var frameMesh = new QuadMeshBuilder();
        var glassMesh = new QuadMeshBuilder();
        for (int row = 0; row < rows; row++)
        {
            float y = -height * 0.5f + marginY + cellHeight * (row + 0.5f);
            for (int column = 0; column < columns; column++)
            {
                float x = -width * 0.5f + marginX + cellWidth * (column + 0.5f);
                float left = x - windowWidth * 0.5f;
                float right = x + windowWidth * 0.5f;
                float bottom = y - windowHeight * 0.5f;
                float top = y + windowHeight * 0.5f;
                float z = depth;
                frameMesh.Add(left - frameSize, bottom - frameSize, left, top + frameSize, z);
                frameMesh.Add(right, bottom - frameSize, right + frameSize, top + frameSize, z);
                frameMesh.Add(left, bottom - frameSize, right, bottom, z);
                frameMesh.Add(left, top, right, top + frameSize, z);
                glassMesh.Add(left, bottom, right, top, z + 0.006f);
            }
        }
        AddMesh(facade, "外壁", wallMesh.Build("FacadeWall"), wall);
        AddMesh(facade, "窓枠", frameMesh.Build("FacadeFrames"), frame);
        AddMesh(facade, "ガラス", glassMesh.Build("FacadeGlass"), glass);
    }

    private static void AddMesh(GameObject parent, string name, Mesh mesh, Material material)
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent.transform, false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        child.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private static Material GetMaterial(string suffix, Color color)
    {
        if (!AssetDatabase.IsValidFolder(GeneratedFolder)) AssetDatabase.CreateFolder("Assets", "GeneratedFuchuFacadePreview");
        string path = GeneratedFolder + "/" + suffix + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            material = new Material(shader) { name = suffix };
            AssetDatabase.CreateAsset(material, path);
        }
        material.color = color;
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }

    private sealed class WallCorners
    {
        public double[] Values;
        public int First, Second, TopSecond, TopFirst;
        public double Width, Height, BaseHeight;
    }

    private sealed class QuadMeshBuilder
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<int> triangles = new List<int>();
        public void Add(float left, float bottom, float right, float top, float front)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(left, bottom, front));
            vertices.Add(new Vector3(right, bottom, front));
            vertices.Add(new Vector3(right, top, front));
            vertices.Add(new Vector3(left, top, front));
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }
        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    private sealed class TerrainHeightSampler
    {
        private const float CellSize = 5f;
        private readonly Vector3 offset;
        private readonly Vector3[] vertices;
        private readonly int[] triangles;
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        public TerrainHeightSampler(Mesh mesh, Vector3 offset)
        {
            this.offset = offset; vertices = mesh.vertices; triangles = mesh.triangles;
            for (int triangle = 0; triangle < triangles.Length / 3; triangle++)
            {
                Vector3 a = vertices[triangles[triangle * 3]], b = vertices[triangles[triangle * 3 + 1]], c = vertices[triangles[triangle * 3 + 2]];
                int minX = Mathf.FloorToInt((Mathf.Min(a.x, b.x, c.x) + offset.x) / CellSize), maxX = Mathf.FloorToInt((Mathf.Max(a.x, b.x, c.x) + offset.x) / CellSize);
                int minZ = Mathf.FloorToInt((Mathf.Min(a.z, b.z, c.z) + offset.z) / CellSize), maxZ = Mathf.FloorToInt((Mathf.Max(a.z, b.z, c.z) + offset.z) / CellSize);
                for (int x = minX; x <= maxX; x++) for (int z = minZ; z <= maxZ; z++)
                {
                    long key = CellKey(x, z);
                    if (!cells.TryGetValue(key, out List<int> list)) cells.Add(key, list = new List<int>());
                    list.Add(triangle);
                }
            }
        }
        public bool TryGetHeight(float x, float z, out float height)
        {
            int cellX = Mathf.FloorToInt(x / CellSize), cellZ = Mathf.FloorToInt(z / CellSize);
            if (cells.TryGetValue(CellKey(cellX, cellZ), out List<int> candidates))
                foreach (int triangle in candidates)
                {
                    Vector3 a = vertices[triangles[triangle * 3]] + offset, b = vertices[triangles[triangle * 3 + 1]] + offset, c = vertices[triangles[triangle * 3 + 2]] + offset;
                    float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(denominator) < 1e-8f) continue;
                    float wa = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / denominator;
                    float wb = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / denominator;
                    float wc = 1f - wa - wb;
                    if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) continue;
                    height = wa * a.y + wb * b.y + wc * c.y;
                    return true;
                }
            height = 0f; return false;
        }
        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}






