using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Creates a reversible one-wall facade sample for the building selected by its PLATEAU ID.</summary>
public static class FuchuSecondBuildingFacadePreview
{
    private const string BuildingId = "bldg_a5d88250-a755-436e-b54b-7529cab6f547";
    private const string PreviewName = "FuchuFacadePreview_TargetBuilding_a5d88250";
    private const string GeneratedFolder = "Assets/GeneratedFuchuFacadePreview";
    private const string MaterialPrefix = GeneratedFolder + "/SecondBuildingFacade_";
    private static readonly XNamespace Gml = "http://www.opengis.net/gml";
    private static readonly XNamespace Building = "http://www.opengis.net/citygml/building/2.0";

    [MenuItem("Fuchu Walk/Create Second Building Facade Preview")]
    private static void CreatePreview()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "再生を停止してから実行してください。", "OK");
            return;
        }

        if (GameObject.Find(PreviewName) != null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "この建物の外壁見本はすでにあります。取り消す場合は「Remove Second Building Facade Preview」を選んでください。", "OK");
            return;
        }

        CesiumGeoreference georeference = UnityEngine.Object.FindFirstObjectByType<CesiumGeoreference>();
        Cesium3DTileset tileset = UnityEngine.Object.FindFirstObjectByType<Cesium3DTileset>();
        GameObject terrainObject = GameObject.Find("FuchuLocalDEMTerrain_1000m");
        if (terrainObject == null) terrainObject = GameObject.Find("FuchuLocalDEMTerrain_3000m");
        MeshFilter terrainFilter = terrainObject != null ? terrainObject.GetComponent<MeshFilter>() : null;
        if (georeference == null || tileset == null || terrainFilter == null || terrainFilter.sharedMesh == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "CesiumGeoreference、建物データ、またはDEM地形が見つかりません。位置を安全に合わせるためシーンは変更していません。", "OK");
            return;
        }
        TerrainHeightSampler heightSampler = new TerrainHeightSampler(terrainFilter.sharedMesh, terrainObject.transform.localPosition);

        XElement building = FindTargetBuilding(out string loadError);
        if (building == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", loadError, "OK");
            return;
        }

        XElement largestWall = FindLargestWall(building, out float width, out float height);
        if (largestWall == null || width < 2f || height < 1.5f)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "この建物の平らな壁面を特定できませんでした。シーンは変更していません。", "OK");
            return;
        }

        XElement posList = largestWall.Descendants(Gml + "posList").FirstOrDefault();
        if (posList == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "壁面の座標が見つかりません。シーンは変更していません。", "OK");
            return;
        }

        Vector3[] corners;
        try
        {
            corners = ParseWallCorners(georeference, heightSampler, posList.Value);
        }
        catch (Exception exception)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "壁面の位置をUnity座標へ変換できませんでした。\n" + exception.Message, "OK");
            return;
        }

        width = Vector3.Distance(corners[0], corners[1]);
        height = Mathf.Max(Vector3.Distance(corners[0], corners[3]), Vector3.Distance(corners[1], corners[2]));
        // CityGML ring winding for this building points inward; reverse it so the sample facade faces the street side.
        Vector3 outward = -Vector3.Cross((corners[1] - corners[0]).normalized, Vector3.up).normalized;
        if (outward.sqrMagnitude < 0.9f)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "この壁面の向きを特定できません。シーンは変更していません。", "OK");
            return;
        }

        Material wallMaterial = GetMaterial("Wall", new Color(0.72f, 0.68f, 0.59f));
        Material frameMaterial = GetMaterial("Frame", new Color(0.84f, 0.81f, 0.73f));
        Material glassMaterial = GetMaterial("Glass", new Color(0.18f, 0.29f, 0.33f));
        Material trimMaterial = GetMaterial("Trim", new Color(0.34f, 0.32f, 0.28f));
        Material signMaterial = GetMaterial("Fascia", new Color(0.43f, 0.29f, 0.19f));

        Undo.SetCurrentGroupName("Create target building facade preview");
        int undoGroup = Undo.GetCurrentGroup();

        GameObject preview = new GameObject(PreviewName);
        Undo.RegisterCreatedObjectUndo(preview, "Create target building facade preview");
        preview.transform.SetParent(georeference.transform, false);

        Quaternion facadeRotation = Quaternion.LookRotation(outward, Vector3.up);
        Vector3 bottomCenter = (corners[0] + corners[1]) * 0.5f;
        Vector3 center = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;

        CreateCube(preview.transform, "選択した外壁の試作面", center + outward * 0.055f, facadeRotation,
            new Vector3(width + 0.04f, height + 0.04f, 0.07f), wallMaterial);

        GameObject details = new GameObject("窓と入口");
        Undo.RegisterCreatedObjectUndo(details, "Create facade details");
        details.transform.SetParent(preview.transform, false);
        details.transform.localPosition = center + outward * 0.12f;
        details.transform.localRotation = facadeRotation;

        // この建物は壁面が横長で複数階にまたがるため、入口は追加せず窓の反復配置で外観を確認します。
        float marginX = Mathf.Min(2.0f, width * 0.04f);
        float marginY = Mathf.Min(1.25f, height * 0.12f);
        float availableWidth = Mathf.Max(0f, width - marginX * 2f);
        float availableHeight = Mathf.Max(0f, height - marginY * 2f);
        int columns = Mathf.Clamp(Mathf.FloorToInt(availableWidth / 4.2f), 1, 32);
        int rows = Mathf.Clamp(Mathf.FloorToInt(availableHeight / 3.1f), 1, 8);
        float cellWidth = availableWidth / columns;
        float cellHeight = availableHeight / rows;
        float windowWidth = Mathf.Min(1.85f, cellWidth * 0.58f);
        float windowHeight = Mathf.Min(2.15f, cellHeight * 0.64f);
        for (int row = 0; row < rows; row++)
        {
            float y = -height * 0.5f + marginY + cellHeight * (row + 0.5f);
            for (int column = 0; column < columns; column++)
            {
                float x = -width * 0.5f + marginX + cellWidth * (column + 0.5f);
                CreateWindow(details.transform, "窓 " + (row + 1) + "F-" + (column + 1), x, y, windowWidth, windowHeight, frameMaterial, glassMaterial, trimMaterial);
            }
        }        EditorSceneManager.MarkSceneDirty(preview.scene);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = preview;

        string usage = (string)building.Element(Building + "usage") ?? "不明";
        string measuredHeight = (string)building.Element(Building + "measuredHeight") ?? "不明";
        EditorUtility.DisplayDialog(
            "Fuchu Walk",
            "建物ID " + BuildingId + " の最大壁面（約" + width.ToString("0.0") + "m × " + height.ToString("0.0") + "m）に、高層建物向けの窓配置を試作しました。CityGML用途コード " + usage + "、高さ " + measuredHeight + "m の建物です。\n\n選択した一面だけに重ねています。確認後にCtrl+Sで保存してください。Ctrl+Z、または「Remove Second Building Facade Preview」で試作を取り消せます。",
            "OK");
    }

    [MenuItem("Fuchu Walk/Remove Second Building Facade Preview")]
    private static void RemovePreview()
    {
        GameObject preview = GameObject.Find(PreviewName);
        if (preview == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "対象建物の外壁試作はありません。", "OK");
            return;
        }

        Undo.DestroyObjectImmediate(preview);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    private static XElement FindTargetBuilding(out string error)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string workspaceRoot = Directory.GetParent(projectRoot).FullName;
        string gmlPath = Path.Combine(workspaceRoot, "3Dモデリングゲーム用資料", "13206_fuchu-shi_pref_2025_citygml_1_op", "udx", "bldg", "53394308_bldg_6697_op.gml");
        if (!File.Exists(gmlPath))
        {
            error = "府中市のCityGMLファイルが見つかりません。";
            return null;
        }

        XDocument document = XDocument.Load(gmlPath, LoadOptions.None);
        XElement target = document.Descendants(Building + "Building")
            .FirstOrDefault(element => (string)element.Attribute(Gml + "id") == BuildingId);
        error = target == null ? "指定された建物IDがCityGML内に見つかりませんでした。" : "";
        return target;
    }

    private static XElement FindLargestWall(XElement building, out float width, out float height)
    {
        XElement best = null;
        double bestArea = 0.0;
        width = 0f;
        height = 0f;

        foreach (XElement wall in building.Descendants(Building + "WallSurface"))
        {
            foreach (XElement polygon in wall.Descendants(Gml + "Polygon"))
            {
                XElement list = polygon.Descendants(Gml + "posList").FirstOrDefault();
                if (list == null) continue;
                double[] coordinates;
                try { coordinates = list.Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Select(text => Double.Parse(text, CultureInfo.InvariantCulture)).ToArray(); }
                catch { continue; }
                if (coordinates.Length < 15 || coordinates.Length % 3 != 0) continue;

                double baseHeight = coordinates[2];
                double topHeight = baseHeight;
                for (int i = 2; i < coordinates.Length; i += 3)
                    topHeight = Math.Max(topHeight, coordinates[i]);
                double edgeWidth = GroundDistanceMeters(coordinates[0], coordinates[1], coordinates[3], coordinates[4]);
                double edgeHeight = topHeight - baseHeight;
                double area = edgeWidth * edgeHeight;
                if (area <= bestArea || edgeHeight < 1.5) continue;

                bestArea = area;
                best = polygon;
                width = (float)edgeWidth;
                height = (float)edgeHeight;
            }
        }
        return best;
    }

    private static double GroundDistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadius = 6378137.0;
        double meanLatitude = (latitudeA + latitudeB) * 0.5 * Math.PI / 180.0;
        double east = (longitudeB - longitudeA) * Math.PI / 180.0 * earthRadius * Math.Cos(meanLatitude);
        double north = (latitudeB - latitudeA) * Math.PI / 180.0 * earthRadius;
        return Math.Sqrt(east * east + north * north);
    }

    private static Vector3[] ParseWallCorners(CesiumGeoreference georeference, TerrainHeightSampler heightSampler, string value)
    {
        double[] coordinates = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .Select(text => Double.Parse(text, CultureInfo.InvariantCulture)).ToArray();
        if (coordinates.Length < 12 || coordinates.Length % 3 != 0)
            throw new FormatException("CityGMLの座標数が想定と異なります。");

        int vertexCount = coordinates.Length / 3;
        int first = 0;
        int second = 1;
        int third = -1;
        int fourth = -1;
        double baseHeight = coordinates[2];
        double topHeight = baseHeight;
        for (int i = 0; i < vertexCount; i++)
            topHeight = Math.Max(topHeight, coordinates[i * 3 + 2]);

        for (int i = 1; i < vertexCount; i++)
        {
            if (Math.Abs(coordinates[i * 3 + 2] - baseHeight) > 0.05) continue;
            second = i;
            break;
        }
        for (int i = 1; i < vertexCount; i++)
        {
            if (i == second || Math.Abs(coordinates[i * 3 + 2] - topHeight) > 0.05) continue;
            double distanceToSecond = Math.Abs(coordinates[i * 3] - coordinates[second * 3]) + Math.Abs(coordinates[i * 3 + 1] - coordinates[second * 3 + 1]);
            if (distanceToSecond < 0.0000001) { third = i; break; }
        }
        for (int i = 1; i < vertexCount; i++)
        {
            if (i == third || Math.Abs(coordinates[i * 3 + 2] - topHeight) > 0.05) continue;
            double distanceToFirst = Math.Abs(coordinates[i * 3] - coordinates[first * 3]) + Math.Abs(coordinates[i * 3 + 1] - coordinates[first * 3 + 1]);
            if (distanceToFirst < 0.0000001) { fourth = i; break; }
        }
        if (third < 0 || fourth < 0)
            throw new FormatException("壁面の四隅を読み取れませんでした。");

        return new[]
        {
            ConvertCoordinate(georeference, heightSampler, coordinates, first, baseHeight),
            ConvertCoordinate(georeference, heightSampler, coordinates, second, baseHeight),
            ConvertCoordinate(georeference, heightSampler, coordinates, third, baseHeight),
            ConvertCoordinate(georeference, heightSampler, coordinates, fourth, baseHeight)
        };
    }

    private static Vector3 ConvertCoordinate(CesiumGeoreference georeference, TerrainHeightSampler heightSampler, double[] coordinates, int index, double baseHeight)
    {
        double latitude = coordinates[index * 3];
        double longitude = coordinates[index * 3 + 1];
        double heightAboveWallBase = coordinates[index * 3 + 2] - baseHeight;
        double metersPerDegreeLat = 111132.92;
        double metersPerDegreeLon = 111319.49 * Math.Cos(georeference.latitude * Math.PI / 180.0);
        float x = (float)((longitude - georeference.longitude) * metersPerDegreeLon);
        float z = (float)((latitude - georeference.latitude) * metersPerDegreeLat);
        if (!heightSampler.TryGetHeight(x, z, out float terrainHeight))
            throw new InvalidOperationException("建物の位置でDEMの高さを取得できませんでした。");
        return new Vector3(x, terrainHeight + (float)heightAboveWallBase + 0.06f, z);
    }

    private static Material GetMaterial(string suffix, Color color)
    {
        if (!AssetDatabase.IsValidFolder(GeneratedFolder))
            AssetDatabase.CreateFolder("Assets", "GeneratedFuchuFacadePreview");

        string path = MaterialPrefix + suffix + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            material = new Material(shader) { name = "Second Building Facade " + suffix };
            AssetDatabase.CreateAsset(material, path);
        }
        material.color = color;
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }

    private static void CreateWindow(Transform parent, string label, float x, float y, float width, float height, Material frame, Material glass, Material trim)
    {
        CreateCube(parent, label + " 窓枠", new Vector3(x, y, 0f), Quaternion.identity, new Vector3(width + 0.16f, height + 0.16f, 0.09f), frame);
        CreateCube(parent, label + " ガラス", new Vector3(x, y, 0.055f), Quaternion.identity, new Vector3(width, height, 0.035f), glass);
        CreateCube(parent, label + " 縦桟", new Vector3(x, y, 0.08f), Quaternion.identity, new Vector3(0.045f, height, 0.025f), trim);
        CreateCube(parent, label + " 横桟", new Vector3(x, y, 0.08f), Quaternion.identity, new Vector3(width, 0.04f, 0.025f), trim);
    }

    private static void CreateDoor(Transform parent, float x, float y, float width, float height, Material frame, Material glass, Material trim)
    {
        CreateCube(parent, "入口の枠", new Vector3(x, y, 0.005f), Quaternion.identity, new Vector3(width + 0.12f, height + 0.08f, 0.11f), frame);
        CreateCube(parent, "入口のガラス", new Vector3(x, y, 0.065f), Quaternion.identity, new Vector3(width, height, 0.04f), glass);
        CreateCube(parent, "入口の縦桟", new Vector3(x + width * 0.35f, y, 0.09f), Quaternion.identity, new Vector3(0.035f, height, 0.02f), trim);
    }

    private static GameObject CreateCube(Transform parent, string objectName, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(cube, "Create target facade detail");
        cube.name = objectName;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = position;
        cube.transform.localRotation = rotation;
        cube.transform.localScale = scale;
        Collider collider = cube.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        cube.GetComponent<MeshRenderer>().sharedMaterial = material;
        return cube;
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
            this.offset = offset;
            vertices = mesh.vertices;
            triangles = mesh.triangles;
            for (int triangle = 0; triangle < triangles.Length / 3; triangle++)
            {
                Vector3 a = vertices[triangles[triangle * 3]];
                Vector3 b = vertices[triangles[triangle * 3 + 1]];
                Vector3 c = vertices[triangles[triangle * 3 + 2]];
                int minX = Mathf.FloorToInt((Mathf.Min(a.x, b.x, c.x) + offset.x) / CellSize);
                int maxX = Mathf.FloorToInt((Mathf.Max(a.x, b.x, c.x) + offset.x) / CellSize);
                int minZ = Mathf.FloorToInt((Mathf.Min(a.z, b.z, c.z) + offset.z) / CellSize);
                int maxZ = Mathf.FloorToInt((Mathf.Max(a.z, b.z, c.z) + offset.z) / CellSize);
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
            int cellX = Mathf.FloorToInt(x / CellSize);
            int cellZ = Mathf.FloorToInt(z / CellSize);
            if (cells.TryGetValue(CellKey(cellX, cellZ), out List<int> candidates))
            {
                foreach (int triangle in candidates)
                {
                    Vector3 a = vertices[triangles[triangle * 3]] + offset;
                    Vector3 b = vertices[triangles[triangle * 3 + 1]] + offset;
                    Vector3 c = vertices[triangles[triangle * 3 + 2]] + offset;
                    float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(denominator) < 1e-8f) continue;
                    float wa = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / denominator;
                    float wb = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / denominator;
                    float wc = 1f - wa - wb;
                    if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) continue;
                    height = wa * a.y + wb * b.y + wc * c.y;
                    return true;
                }
            }

            height = 0f;
            return false;
        }

        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}



