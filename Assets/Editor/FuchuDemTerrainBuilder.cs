using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Builds a collidable terrain patch from the local PLATEAU DEM CityGML.</summary>
public static class FuchuDemTerrainBuilder
{
    private const double RadiusMeters = 1000.0;
    private const string TerrainName = "FuchuLocalDEMTerrain_1000m";
    // Observed runtime player ground (-22.71) vs. Cesium tileset minimum local height
    // (74.83 m - georeference height 80 m = -5.17): approximately 17.54 m.
    private const float CesiumHeightAlignmentOffset = 17.54f;

    private struct VertexKey : IEquatable<VertexKey>
    {
        public long x, y, z;
        public VertexKey(Vector3 p)
        {
            x = (long)Math.Round(p.x * 100.0);
            y = (long)Math.Round(p.y * 100.0);
            z = (long)Math.Round(p.z * 100.0);
        }
        public bool Equals(VertexKey other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is VertexKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
    }

    [MenuItem("Fuchu Walk/Build 1km Radius Field from DEM Data")]
    private static void BuildTerrain()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "先に再生を停止してから1km範囲を作成してください。", "OK");
            return;
        }

        CesiumGeoreference georeference = UnityEngine.Object.FindFirstObjectByType<CesiumGeoreference>();
        GameObject player = GameObject.Find("FuchuWalkPlayer");
        if (georeference == null || player == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "CesiumGeoreferenceまたは歩行プレイヤーが見つかりません。", "OK");
            return;
        }
        if (GameObject.Find(TerrainName) != null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "半径1kmの地形はすでに作成されています。", "OK");
            return;
        }

        string defaultDemFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "3Dモデリングゲーム用資料",
            "13206_fuchu-shi_pref_2025_citygml_1_op", "udx", "dem"));
        string folder = EditorUtility.OpenFolderPanel("demフォルダーを選択", defaultDemFolder, "");
        if (string.IsNullOrEmpty(folder)) return;
        string[] demFiles = Directory.GetFiles(folder, "*_dem_6697_op.gml", SearchOption.TopDirectoryOnly);
        if (demFiles.Length == 0)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "選択したフォルダーにDEMのGMLファイルがありません。udx\\dem フォルダーを選択してください。", "OK");
            return;
        }

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var vertexMap = new Dictionary<VertexKey, int>();
        Vector3 center = player.transform.localPosition;
        GameObject existingTerrain = GameObject.Find("FuchuLocalDEMTerrain_1000m");
        if (existingTerrain == null) existingTerrain = GameObject.Find("FuchuLocalDEMTerrain_3000m");
        if (existingTerrain == null) existingTerrain = GameObject.Find("FuchuLocalDEMTerrain_700m");
        if (existingTerrain == null) existingTerrain = GameObject.Find("FuchuLocalDEMTerrain_240m");
        float existingVerticalOffset = existingTerrain != null ? existingTerrain.transform.localPosition.y : 0f;
        double originLatitude = georeference.latitude;
        double originLongitude = georeference.longitude;
        double originHeight = georeference.height;
        double metersPerDegreeLatitude = 111132.92;
        double metersPerDegreeLongitude = 111319.49 * Math.Cos(originLatitude * Math.PI / 180.0);
        demFiles = Array.FindAll(demFiles, file => DemFileIntersectsArea(file, originLatitude, originLongitude,
            metersPerDegreeLatitude, metersPerDegreeLongitude, center));
        if (demFiles.Length == 0)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "選択したDEMファイルは、開始地点から半径1kmの範囲をカバーしていません。府中市のudx\\demフォルダーを選択してください。", "OK");
            return;
        }
        int trianglesRead = 0;

        try
        {
            for (int fileIndex = 0; fileIndex < demFiles.Length; fileIndex++)
            {
                using (var file = new FileStream(demFiles[fileIndex], FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(file, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Triangle") continue;
                        using (XmlReader triangleReader = reader.ReadSubtree())
                        {
                            string posList = null;
                            while (triangleReader.Read())
                                if (triangleReader.NodeType == XmlNodeType.Element && triangleReader.LocalName == "posList")
                                {
                                    posList = triangleReader.ReadElementContentAsString();
                                    break;
                                }

                            if (!string.IsNullOrEmpty(posList))
                                AddTriangle(posList, originLatitude, originLongitude, originHeight, metersPerDegreeLatitude,
                                    metersPerDegreeLongitude, center, vertices, indices, vertexMap);
                        }

                        trianglesRead++;
                        if (trianglesRead % 10000 == 0)
                        {
                            float progress = (fileIndex + (float)file.Position / file.Length) / demFiles.Length;
                            string message = "半径1km範囲を抽出中（" + Path.GetFileName(demFiles[fileIndex]) + "）";
                            if (EditorUtility.DisplayCancelableProgressBar("地形データを読み込み中", message, progress))
                                throw new OperationCanceledException();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "地形の作成をキャンセルしました。シーンは変更していません。", "OK");
            return;
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "DEMファイルを読み込めませんでした:\n" + ex.Message, "OK");
            return;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (indices.Count == 0)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "このDEMファイルには開始地点の周囲の三角形が見つかりません。533943_dem_6697_op.gml を選択してください。", "OK");
            return;
        }

        string assetFolder = "Assets/GeneratedFuchuTerrain";
        if (!AssetDatabase.IsValidFolder(assetFolder)) AssetDatabase.CreateFolder("Assets", "GeneratedFuchuTerrain");
        string meshPath = AssetDatabase.GenerateUniqueAssetPath(assetFolder + "/FuchuLocalTerrain.asset");
        var mesh = new Mesh { name = TerrainName, indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, meshPath);

        string materialPath = AssetDatabase.GenerateUniqueAssetPath(assetFolder + "/FuchuTerrainMaterial.mat");
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var material = new Material(shader) { name = "Fuchu DEM Terrain" };
        material.color = new Color(0.48f, 0.52f, 0.42f);
        AssetDatabase.CreateAsset(material, materialPath);
        AssetDatabase.SaveAssets();

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Build Fuchu DEM terrain patch");
        GameObject terrain = new GameObject(TerrainName);
        Undo.RegisterCreatedObjectUndo(terrain, "Create DEM terrain patch");
        terrain.transform.SetParent(georeference.transform, false);
        terrain.transform.localPosition = new Vector3(0f, existingVerticalOffset, 0f);
        var filter = terrain.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        var renderer = terrain.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        var collider = terrain.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;

        foreach (string oldTerrainName in new[] { "FuchuLocalDEMTerrain_3000m", "FuchuLocalDEMTerrain_700m", "FuchuLocalDEMTerrain_240m" })
        {
            GameObject oldTerrainObject = GameObject.Find(oldTerrainName);
            if (oldTerrainObject == null) continue;
            MeshRenderer oldRenderer = oldTerrainObject.GetComponent<MeshRenderer>();
            MeshCollider oldCollider = oldTerrainObject.GetComponent<MeshCollider>();
            if (oldRenderer != null)
            {
                Undo.RecordObject(oldRenderer, "Hide previous DEM terrain patch");
                oldRenderer.enabled = false;
            }
            if (oldCollider != null)
            {
                Undo.RecordObject(oldCollider, "Disable previous DEM terrain collision");
                oldCollider.enabled = false;
            }
        }

        CreateBoundaryWalls(georeference.transform, center);

        GameObject temporaryGround = GameObject.Find("TemporaryWalkGround");
        if (temporaryGround != null)
        {
            Undo.RecordObject(temporaryGround.GetComponent<MeshRenderer>(), "Hide temporary flat ground");
            Undo.RecordObject(temporaryGround.GetComponent<MeshCollider>(), "Disable temporary flat ground collision");
            temporaryGround.GetComponent<MeshRenderer>().enabled = false;
            temporaryGround.GetComponent<MeshCollider>().enabled = false;
        }

        EditorSceneManager.MarkSceneDirty(terrain.scene);
        Selection.activeGameObject = terrain;
            EditorUtility.DisplayDialog("Fuchu Walk", "プレイヤー初期位置を中心に、半径1kmの円形DEM地形と歩行用境界を作成しました。以前の地形は重なりを避けるため非表示にしました。Ctrl+Sで保存してください。", "OK");
    }

    [MenuItem("Fuchu Walk/Align DEM Terrain with 3D Tiles (Temporary Test)")]
    private static void AlignExistingTerrainWithTiles()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "先に再生を停止してから、この調整を実行してください。", "OK");
            return;
        }

        GameObject terrain = GameObject.Find(TerrainName);
        GameObject player = GameObject.Find("FuchuWalkPlayer");
        if (terrain == null || player == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "地形または歩行プレイヤーが見つかりません。", "OK");
            return;
        }

        if (Mathf.Abs(terrain.transform.localPosition.y - CesiumHeightAlignmentOffset) < 0.05f)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "地形はすでに調整済みです。", "OK");
            return;
        }

        Undo.RecordObject(terrain.transform, "Align DEM terrain to Cesium tile height");
        Undo.RecordObject(player.transform, "Align player to adjusted DEM terrain");
        Vector3 terrainPosition = terrain.transform.localPosition;
        terrainPosition.y += CesiumHeightAlignmentOffset;
        terrain.transform.localPosition = terrainPosition;

        Vector3 playerPosition = player.transform.localPosition;
        playerPosition.y += CesiumHeightAlignmentOffset;
        player.transform.localPosition = playerPosition;

        EditorSceneManager.MarkSceneDirty(player.scene);
        EditorUtility.DisplayDialog(
            "Fuchu Walk",
            "地形とプレイヤーを約17.5m上げました。標高差を合わせるための暫定テストです。Ctrl+Zで取り消せます。再生して道路面との位置を確認し、問題なければCtrl+Sで保存してください。",
            "OK");
    }

    private static void AddTriangle(string posList, double originLat, double originLon, double originHeight,
        double metersPerDegreeLat, double metersPerDegreeLon, Vector3 center, List<Vector3> vertices,
        List<int> indices, Dictionary<VertexKey, int> vertexMap)
    {
        string[] parts = posList.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 9) return;
        Vector3[] points = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            double lat = double.Parse(parts[i * 3], CultureInfo.InvariantCulture);
            double lon = double.Parse(parts[i * 3 + 1], CultureInfo.InvariantCulture);
            double height = double.Parse(parts[i * 3 + 2], CultureInfo.InvariantCulture);
            float x = (float)((lon - originLon) * metersPerDegreeLon);
            float z = (float)((lat - originLat) * metersPerDegreeLat);
            float y = (float)(height - originHeight);
            points[i] = new Vector3(x, y, z);
        }

        Vector3 centroid = (points[0] + points[1] + points[2]) / 3f;
        float dx = centroid.x - center.x;
        float dz = centroid.z - center.z;
        double terrainRadius = RadiusMeters + 5.0;
        if (dx * dx + dz * dz > terrainRadius * terrainRadius) return;
        if (Vector3.Cross(points[1] - points[0], points[2] - points[0]).y < 0f)
        {
            Vector3 swap = points[1];
            points[1] = points[2];
            points[2] = swap;
        }

        for (int i = 0; i < 3; i++)
        {
            var key = new VertexKey(points[i]);
            if (!vertexMap.TryGetValue(key, out int index))
            {
                index = vertices.Count;
                vertices.Add(points[i]);
                vertexMap.Add(key, index);
            }
            indices.Add(index);
        }
    }

    private static void CreateBoundaryWalls(Transform parent, Vector3 center)
    {
        foreach (string oldName in new[] { "FuchuWalkBoundaryNorth", "FuchuWalkBoundarySouth", "FuchuWalkBoundaryEast", "FuchuWalkBoundaryWest" })
        {
            GameObject oldWall = GameObject.Find(oldName);
            if (oldWall != null) Undo.DestroyObjectImmediate(oldWall);
        }
        GameObject oldRing = GameObject.Find("FuchuWalkCircularBoundary_1km");
        if (oldRing != null) Undo.DestroyObjectImmediate(oldRing);

        const int segments = 64;
        const float wallHeight = 1000f;
        const float wallThickness = 4f;
        float wallRadius = (float)RadiusMeters + wallThickness * 0.5f;
        float segmentLength = 2f * wallRadius * Mathf.Tan(Mathf.PI / segments);
        GameObject ring = new GameObject("FuchuWalkCircularBoundary_1km");
        Undo.RegisterCreatedObjectUndo(ring, "Create 1km circular walking boundary");
        ring.transform.SetParent(parent, false);
        ring.transform.localPosition = Vector3.zero;

        for (int i = 0; i < segments; i++)
        {
            float angle = i * 360f / segments;
            float radians = angle * Mathf.Deg2Rad;
            GameObject wall = new GameObject("BoundarySegment_" + (i + 1).ToString("00"));
            Undo.RegisterCreatedObjectUndo(wall, "Create circular boundary segment");
            wall.transform.SetParent(ring.transform, false);
            wall.transform.localPosition = new Vector3(center.x + Mathf.Sin(radians) * wallRadius, 0f,
                center.z + Mathf.Cos(radians) * wallRadius);
            wall.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
            BoxCollider box = Undo.AddComponent<BoxCollider>(wall);
            box.size = new Vector3(segmentLength + 0.5f, wallHeight, wallThickness);
        }
    }
    private static bool DemFileIntersectsArea(string path, double originLat, double originLon,
        double metersPerDegreeLat, double metersPerDegreeLon, Vector3 center)
    {
        string lower = null;
        string upper = null;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = XmlReader.Create(file, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore }))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "lowerCorner") lower = reader.ReadElementContentAsString();
                else if (reader.LocalName == "upperCorner") upper = reader.ReadElementContentAsString();
                if (lower != null && upper != null) break;
            }
        }
        if (lower == null || upper == null) return true; // Keep unrecognized tiles rather than risk leaving a hole.

        string[] low = lower.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        string[] high = upper.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (low.Length < 2 || high.Length < 2) return true;
        double minZ = (double.Parse(low[0], CultureInfo.InvariantCulture) - originLat) * metersPerDegreeLat;
        double maxZ = (double.Parse(high[0], CultureInfo.InvariantCulture) - originLat) * metersPerDegreeLat;
        double minX = (double.Parse(low[1], CultureInfo.InvariantCulture) - originLon) * metersPerDegreeLon;
        double maxX = (double.Parse(high[1], CultureInfo.InvariantCulture) - originLon) * metersPerDegreeLon;
        return maxX >= center.x - RadiusMeters && minX <= center.x + RadiusMeters &&
               maxZ >= center.z - RadiusMeters && minZ <= center.z + RadiusMeters;
    }
}


