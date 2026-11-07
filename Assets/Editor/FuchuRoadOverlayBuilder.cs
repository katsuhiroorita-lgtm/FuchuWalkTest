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

/// <summary>Creates an asphalt overlay from the PLATEAU transportation data within the local 1 km area.</summary>
public static class FuchuRoadOverlayBuilder
{
    private const string RoadObjectName = "FuchuLocalRoads_1000m";
    private const string TerrainObjectName = "FuchuLocalDEMTerrain_1000m";
    private const double RadiusMeters = 1000.0;
    private const float GridCellSize = 5f;

    [MenuItem("Fuchu Walk/Build Local Road Surface from CityGML")]
    private static void BuildRoadSurface()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "先に再生を停止してから道路面を作成してください。", "OK");
            return;
        }

        CesiumGeoreference georeference = UnityEngine.Object.FindFirstObjectByType<CesiumGeoreference>();
        GameObject player = GameObject.Find("FuchuWalkPlayer");
        GameObject terrainObject = GameObject.Find(TerrainObjectName);
        if (terrainObject == null) terrainObject = GameObject.Find("FuchuLocalDEMTerrain_3000m");
        if (georeference == null || player == null || terrainObject == null || terrainObject.GetComponent<MeshFilter>()?.sharedMesh == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "CesiumGeoreference、歩行プレイヤー、またはDEM地形が見つかりません。先に地形を作成してください。", "OK");
            return;
        }

        string defaultFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "3Dモデリングゲーム用資料",
            "13206_fuchu-shi_pref_2025_citygml_1_op", "udx", "tran"));
        string folder = EditorUtility.OpenFolderPanel("交通（道路）CityGMLのフォルダーを選択", defaultFolder, "");
        if (string.IsNullOrEmpty(folder)) return;
        string[] roadFiles = Directory.GetFiles(folder, "*_tran_6697_op.gml", SearchOption.TopDirectoryOnly);
        if (roadFiles.Length == 0)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "選んだフォルダーに道路CityGMLが見つかりません。udx\\tran フォルダーを選択してください。", "OK");
            return;
        }
        if (GameObject.Find(RoadObjectName) != null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "道路面はすでに作成されています。", "OK");
            return;
        }

        Mesh terrainMesh = terrainObject.GetComponent<MeshFilter>().sharedMesh;
        Vector3 terrainOffset = terrainObject.transform.localPosition;
        var heightSampler = new TerrainHeightSampler(terrainMesh, terrainOffset);
        Vector3 playerPosition = player.transform.localPosition;
        double metersPerDegreeLat = 111132.92;
        double metersPerDegreeLon = 111319.49 * Math.Cos(georeference.latitude * Math.PI / 180.0);
        var roadVertices = new List<Vector3>();
        var roadIndices = new List<int>();
        int polygonCount = 0;
        int polygonsRead = 0;

        try
        {
            for (int roadIndex = 0; roadIndex < roadFiles.Length; roadIndex++)
            {
                string roadFile = roadFiles[roadIndex];
                using (var file = new FileStream(roadFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(file, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "lod1MultiSurface") continue;
                        using (XmlReader surfaces = reader.ReadSubtree())
                        {
                            while (surfaces.Read())
                            {
                                if (surfaces.NodeType != XmlNodeType.Element || surfaces.LocalName != "Polygon") continue;
                                polygonsRead++;
                                if (polygonsRead % 5000 == 0)
                                {
                                    float progress = (roadIndex + (float)file.Position / file.Length) / roadFiles.Length;
                                    string message = "半径1km範囲の道路を抽出中（" + Path.GetFileName(roadFile) + "）";
                                    if (EditorUtility.DisplayCancelableProgressBar("道路データを読み込み中", message, progress))
                                        throw new OperationCanceledException();
                                }
                                using (XmlReader polygon = surfaces.ReadSubtree())
                                {
                                    string posList = null;
                                    while (polygon.Read())
                                    {
                                        if (polygon.NodeType == XmlNodeType.Element && polygon.LocalName == "posList")
                                        {
                                            posList = polygon.ReadElementContentAsString();
                                            break;
                                        }
                                    }
                                    if (string.IsNullOrEmpty(posList)) continue;
                                    if (AddPolygon(posList, georeference.latitude, georeference.longitude, metersPerDegreeLat,
                                        metersPerDegreeLon, playerPosition, heightSampler, roadVertices, roadIndices))
                                        polygonCount++;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "道路の作成をキャンセルしました。シーンは変更していません。", "OK");
            return;
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "道路CityGMLを読み込めませんでした:\n" + ex.Message, "OK");
            return;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (roadIndices.Count == 0)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "開始地点の周囲に道路ポリゴンが見つかりませんでした。選択したファイルを確認してください。", "OK");
            return;
        }

        string assetFolder = "Assets/GeneratedFuchuTerrain";
        if (!AssetDatabase.IsValidFolder(assetFolder)) AssetDatabase.CreateFolder("Assets", "GeneratedFuchuTerrain");
        var mesh = new Mesh { name = "FuchuLocalRoadSurface", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(roadVertices);
        mesh.SetTriangles(roadIndices, 0);
        var roadUvs = new Vector2[roadVertices.Count];
        for (int i = 0; i < roadVertices.Count; i++)
            roadUvs[i] = new Vector2(roadVertices[i].x * 0.2f, roadVertices[i].z * 0.2f);
        mesh.uv = roadUvs;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        string meshPath = AssetDatabase.GenerateUniqueAssetPath(assetFolder + "/FuchuLocalRoadSurface.asset");
        AssetDatabase.CreateAsset(mesh, meshPath);

        Material material = FuchuRoadAppearanceSetup.GetOrCreateDetailedMaterial();
        AssetDatabase.SaveAssets();

        var roads = new GameObject(RoadObjectName);
        Undo.RegisterCreatedObjectUndo(roads, "Create Fuchu road overlay");
        roads.transform.SetParent(georeference.transform, false);
        var meshFilter = roads.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;
        roads.AddComponent<MeshRenderer>().sharedMaterial = material;
        roads.AddComponent<MeshCollider>().sharedMesh = mesh;
        foreach (string oldRoadName in new[] { "FuchuLocalRoads_3000m", "FuchuLocalRoads_700m", "FuchuLocalRoads_53394308" })
        {
            GameObject previousRoads = GameObject.Find(oldRoadName);
            if (previousRoads == null) continue;
            MeshRenderer oldRenderer = previousRoads.GetComponent<MeshRenderer>();
            MeshCollider oldCollider = previousRoads.GetComponent<MeshCollider>();
            if (oldRenderer != null)
            {
                Undo.RecordObject(oldRenderer, "Hide previous road surface");
                oldRenderer.enabled = false;
            }
            if (oldCollider != null)
            {
                Undo.RecordObject(oldCollider, "Disable previous road collision");
                oldCollider.enabled = false;
            }
        }
        EditorSceneManager.MarkSceneDirty(roads.scene);
        Selection.activeGameObject = roads;
        EditorUtility.DisplayDialog("Fuchu Walk", "開始位置を中心に半径1kmの道路面を作成しました（" + polygonCount + "区画）。以前の道路面は重なりを避けるため非表示にしました。Ctrl+Sで保存してください。", "OK");
    }

    private static bool AddPolygon(string posList, double originLat, double originLon, double metersPerDegreeLat,
        double metersPerDegreeLon, Vector3 player, TerrainHeightSampler sampler,
        List<Vector3> vertices, List<int> indices)
    {
        string[] values = posList.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        int pointCount = values.Length / 3;
        if (pointCount < 4) return false;
        var polygon = new List<Vector3>(pointCount);
        for (int i = 0; i < pointCount; i++)
        {
            double lat = double.Parse(values[i * 3], CultureInfo.InvariantCulture);
            double lon = double.Parse(values[i * 3 + 1], CultureInfo.InvariantCulture);
            float x = (float)((lon - originLon) * metersPerDegreeLon);
            float z = (float)((lat - originLat) * metersPerDegreeLat);
            polygon.Add(new Vector3(x, 0f, z));
        }

        if ((polygon[0] - polygon[polygon.Count - 1]).sqrMagnitude < 0.01f) polygon.RemoveAt(polygon.Count - 1);
        if (polygon.Count < 3) return false;
        Vector3 centroid = Vector3.zero;
        foreach (Vector3 point in polygon) centroid += point;
        centroid /= polygon.Count;
        float dx = centroid.x - player.x;
        float dz = centroid.z - player.z;
        if (dx * dx + dz * dz > RadiusMeters * RadiusMeters) return false;

        int start = vertices.Count;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector3 point = polygon[i];
            if (!sampler.TryGetHeight(point.x, point.z, out float sampledY)) return false;
            point.y = sampledY + 0.04f;
            vertices.Add(point);
        }
        for (int i = 1; i < polygon.Count - 1; i++)
        {
            // The source rings are ordered around the perimeter. Correct winding for Unity's +Y-facing surface.
            Vector3 a = vertices[start];
            Vector3 b = vertices[start + i];
            Vector3 c = vertices[start + i + 1];
            if (Vector3.Cross(b - a, c - a).y >= 0f)
            {
                indices.Add(start);
                indices.Add(start + i);
                indices.Add(start + i + 1);
            }
            else
            {
                indices.Add(start);
                indices.Add(start + i + 1);
                indices.Add(start + i);
            }
        }
        return true;
    }

    private sealed class TerrainHeightSampler
    {
        private readonly Vector3 offset;
        private readonly Vector3[] vertices;
        private readonly int[] triangles;
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

        public TerrainHeightSampler(Mesh mesh, Vector3 offset)
        {
            this.offset = offset;
            vertices = mesh.vertices;
            triangles = mesh.triangles;
            for (int triangle = 0; triangle < triangles.Length / 3; triangle++)
            {
                Vector3 a = vertices[triangles[triangle * 3]];
                Vector3 b = vertices[triangles[triangle * 3 + 1]];
                Vector3 c = vertices[triangles[triangle * 3 + 2]];
                int minX = Mathf.FloorToInt((Mathf.Min(a.x, b.x, c.x) + offset.x) / GridCellSize);
                int maxX = Mathf.FloorToInt((Mathf.Max(a.x, b.x, c.x) + offset.x) / GridCellSize);
                int minZ = Mathf.FloorToInt((Mathf.Min(a.z, b.z, c.z) + offset.z) / GridCellSize);
                int maxZ = Mathf.FloorToInt((Mathf.Max(a.z, b.z, c.z) + offset.z) / GridCellSize);
                for (int x = minX; x <= maxX; x++)
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        long key = CellKey(x, z);
                        if (!cells.TryGetValue(key, out List<int> list)) cells.Add(key, list = new List<int>());
                        list.Add(triangle);
                    }
            }
        }

        public bool TryGetHeight(float x, float z, out float height)
        {
            int cellX = Mathf.FloorToInt(x / GridCellSize);
            int cellZ = Mathf.FloorToInt(z / GridCellSize);
            if (cells.TryGetValue(CellKey(cellX, cellZ), out List<int> candidates))
            {
                foreach (int triangle in candidates)
                {
                    Vector3 a = vertices[triangles[triangle * 3]];
                    Vector3 b = vertices[triangles[triangle * 3 + 1]];
                    Vector3 c = vertices[triangles[triangle * 3 + 2]];
                    a += offset; b += offset; c += offset;
                    if (TryBarycentricHeight(x, z, a, b, c, out height)) return true;
                }
            }
            height = 0f;
            return false;
        }

        private static bool TryBarycentricHeight(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float y)
        {
            float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(denominator) < 1e-8f) { y = 0f; return false; }
            float wa = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / denominator;
            float wb = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / denominator;
            float wc = 1f - wa - wb;
            if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) { y = 0f; return false; }
            y = wa * a.y + wb * b.y + wc * c.y;
            return true;
        }

        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
