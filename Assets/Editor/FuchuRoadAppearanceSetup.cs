using System;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Creates and applies a subtle, repeating asphalt grain to generated road surfaces.</summary>
public static class FuchuRoadAppearanceSetup
{
    private const string AssetFolder = "Assets/GeneratedFuchuTerrain";
    private const string TexturePath = AssetFolder + "/FuchuAsphaltFineGrain.asset";
    private const string MaterialPath = AssetFolder + "/FuchuAsphaltDetailed.mat";

    [MenuItem("Fuchu Walk/Improve Building Detail")]
    private static void ImproveBuildingDetail()
    {
        Cesium3DTileset tileset = UnityEngine.Object.FindFirstObjectByType<Cesium3DTileset>();
        if (tileset == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "Cesium3DTilesetが見つかりません。シーンに建物データが読み込まれているか確認してください。", "OK");
            return;
        }

        Undo.RecordObject(tileset, "Improve building detail");
        tileset.maximumScreenSpaceError = 8f;
        EditorUtility.SetDirty(tileset);
        EditorSceneManager.MarkSceneDirty(tileset.gameObject.scene);
        EditorUtility.DisplayDialog(
            "Fuchu Walk",
            "建物データの詳細度を上げました。テクスチャ付きLOD2をより細かく表示します。読み込みとメモリ使用が増えることがあります。重くなった場合はCtrl+Zで元に戻してください。Ctrl+Sで保存できます。",
            "OK");
    }

    [MenuItem("Fuchu Walk/Improve Asphalt Appearance")]
    private static void ImproveCurrentRoads()
    {
        GameObject[] roadObjects =
        {
            GameObject.Find("FuchuLocalRoads_1000m"),
            GameObject.Find("FuchuLocalRoads_3000m"),
            GameObject.Find("FuchuLocalRoads_700m"),
            GameObject.Find("FuchuLocalRoads_53394308")
        };

        Mesh mesh = null;
        MeshFilter meshFilter = null;
        MeshRenderer meshRenderer = null;
        foreach (GameObject road in roadObjects)
        {
            if (road == null) continue;
            MeshRenderer candidateRenderer = road.GetComponent<MeshRenderer>();
            MeshFilter candidateFilter = road.GetComponent<MeshFilter>();
            if (candidateRenderer == null || candidateFilter == null || candidateFilter.sharedMesh == null) continue;
            if (!candidateRenderer.enabled) continue;
            mesh = candidateFilter.sharedMesh;
            meshFilter = candidateFilter;
            meshRenderer = candidateRenderer;
            break;
        }

        if (mesh == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "表示中の道路面が見つかりません。先に道路面を作成してください。", "OK");
            return;
        }

        Material material = GetOrCreateDetailedMaterial();
        if (meshFilter.sharedMesh.name.IndexOf("FineGrain", StringComparison.Ordinal) < 0)
        {
            Mesh detailedMesh = UnityEngine.Object.Instantiate(mesh);
            detailedMesh.name = mesh.name + "_FineGrain";
            Vector3[] vertices = detailedMesh.vertices;
            var uvs = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                uvs[i] = new Vector2(vertices[i].x * 0.2f, vertices[i].z * 0.2f);
            detailedMesh.uv = uvs;
            string path = AssetDatabase.GenerateUniqueAssetPath(AssetFolder + "/FuchuLocalRoadSurface_FineGrain.asset");
            AssetDatabase.CreateAsset(detailedMesh, path);
            Undo.RecordObject(meshFilter, "Add asphalt surface detail");
            meshFilter.sharedMesh = detailedMesh;
            MeshCollider collider = meshFilter.GetComponent<MeshCollider>();
            if (collider != null)
            {
                Undo.RecordObject(collider, "Update detailed road collision mesh");
                collider.sharedMesh = null;
                collider.sharedMesh = detailedMesh;
            }
        }

        Undo.RecordObject(meshRenderer, "Apply detailed asphalt material");
        meshRenderer.sharedMaterial = material;
        EditorSceneManager.MarkSceneDirty(meshFilter.gameObject.scene);
        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Fuchu Walk", "道路に控えめなアスファルトの粒状感を追加しました。道路の形状と歩行用コライダーは維持しています。Ctrl+Sでシーンを保存してください。", "OK");
    }

    public static Material GetOrCreateDetailedMaterial()
    {
        if (!AssetDatabase.IsValidFolder(AssetFolder))
            AssetDatabase.CreateFolder("Assets", "GeneratedFuchuTerrain");

        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        if (texture == null)
        {
            texture = CreateAsphaltTexture();
            AssetDatabase.CreateAsset(texture, TexturePath);
        }

        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            material = new Material(shader) { name = "Fuchu Asphalt Detailed" };
            AssetDatabase.CreateAsset(material, MaterialPath);
        }

        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.31f, 0.32f, 0.33f, 1f));
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(0.31f, 0.32f, 0.33f, 1f));
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.16f);
        if (material.HasProperty("_BaseMap")) material.SetTextureScale("_BaseMap", Vector2.one);
        if (material.HasProperty("_MainTex")) material.SetTextureScale("_MainTex", Vector2.one);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Texture2D CreateAsphaltTexture()
    {
        const int resolution = 512;
        var texture = new Texture2D(resolution, resolution, TextureFormat.RGB24, true, false)
        {
            name = "Fuchu Asphalt Fine Grain",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4
        };
        var random = new System.Random(13206);
        float[,] coarseNoise = CreateNoiseGrid(8, random);
        float[,] midNoise = CreateNoiseGrid(32, random);
        float[,] fineNoise = CreateNoiseGrid(128, random);
        var pixels = new Color[resolution * resolution];
        for (int y = 0; y < resolution; y++)
        {
            float v = (float)y / resolution;
            for (int x = 0; x < resolution; x++)
            {
                float u = (float)x / resolution;
                float coarse = SampleNoise(coarseNoise, u, v);
                float grain = SampleNoise(midNoise, u, v);
                float fine = SampleNoise(fineNoise, u, v);
                float value = Mathf.Clamp01(0.78f + coarse * 0.015f + grain * 0.025f + fine * 0.018f);
                pixels[y * resolution + x] = new Color(value, value, value, 1f);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(true, true);
        return texture;
    }

    private static float[,] CreateNoiseGrid(int size, System.Random random)
    {
        var values = new float[size, size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                values[x, y] = (float)random.NextDouble();
        return values;
    }

    private static float SampleNoise(float[,] values, float u, float v)
    {
        int size = values.GetLength(0);
        float x = u * size;
        float y = v * size;
        int x0 = Mathf.FloorToInt(x) % size;
        int y0 = Mathf.FloorToInt(y) % size;
        int x1 = (x0 + 1) % size;
        int y1 = (y0 + 1) % size;
        float tx = Mathf.SmoothStep(0f, 1f, x - Mathf.Floor(x));
        float ty = Mathf.SmoothStep(0f, 1f, y - Mathf.Floor(y));
        float bottom = Mathf.Lerp(values[x0, y0], values[x1, y0], tx);
        float top = Mathf.Lerp(values[x0, y1], values[x1, y1], tx);
        return (Mathf.Lerp(bottom, top, ty) - 0.5f) * 2f;
    }
}
