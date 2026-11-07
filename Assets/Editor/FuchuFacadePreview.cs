using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Builds a small, reversible facade comparison beside the walking start point.</summary>
public static class FuchuFacadePreview
{
    private const string RootName = "FuchuFacadePreview_ComparisonOnly";

    [MenuItem("Fuchu Walk/Create Facade Preview (3 Samples)")]
    private static void CreatePreview()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "再生を停止してから実行してください。", "OK");
            return;
        }

        GameObject existing = GameObject.Find(RootName);
        if (existing != null)
        {
            Selection.activeGameObject = existing;
            EditorUtility.DisplayDialog("Fuchu Walk", "比較用の表示はすでにあります。削除する場合は「Remove Facade Preview」を選んでください。", "OK");
            return;
        }

        GameObject player = GameObject.Find("FuchuWalkPlayer");
        GameObject georeference = GameObject.Find("CesiumGeoreference");
        if (player == null || georeference == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "歩行プレイヤーまたはCesiumGeoreferenceが見つかりません。", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Create facade comparison preview");
        int group = Undo.GetCurrentGroup();
        GameObject root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Create facade preview");
        root.transform.SetParent(georeference.transform, false);
        root.transform.localPosition = player.transform.localPosition + new Vector3(0f, -1.1f, 14f);
        root.transform.localRotation = Quaternion.identity;

        Material plaster = MakeMaterial("Facade Sample - warm residential", new Color(0.76f, 0.72f, 0.63f));
        Material shop = MakeMaterial("Facade Sample - storefront", new Color(0.74f, 0.70f, 0.61f));
        Material civic = MakeMaterial("Facade Sample - civic building", new Color(0.70f, 0.72f, 0.69f));
        Material glass = MakeMaterial("Facade Sample - muted blue glass", new Color(0.20f, 0.31f, 0.35f));
        Material trim = MakeMaterial("Facade Sample - window frames", new Color(0.88f, 0.85f, 0.77f));
        Material balcony = MakeMaterial("Facade Sample - balcony metal", new Color(0.33f, 0.35f, 0.34f));
        Material sign = MakeMaterial("Facade Sample - shop sign", new Color(0.44f, 0.27f, 0.16f));

        BuildSample(root.transform, -5.2f, "住宅", plaster, glass, trim, balcony, sign, 3, true, false);
        BuildSample(root.transform, 0f, "駅前の店舗", shop, glass, trim, balcony, sign, 2, false, true);
        BuildSample(root.transform, 5.2f, "公共施設", civic, glass, trim, balcony, sign, 3, false, false);

        EditorSceneManager.MarkSceneDirty(root.scene);
        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = root;
        EditorUtility.DisplayDialog("Fuchu Walk",
            "歩行開始地点の北側に、住宅・店舗・公共施設の外壁見本を3種類並べました。これは比較用の別表示で、PLATEAUの建物には適用していません。Ctrl+Z、または「Fuchu Walk > Remove Facade Preview」で元に戻せます。見やすい位置へ移動してシーンを保存する場合はCtrl+Sを押してください。",
            "OK");
    }

    [MenuItem("Fuchu Walk/Remove Facade Preview")]
    private static void RemovePreview()
    {
        GameObject root = GameObject.Find(RootName);
        if (root == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "比較用の表示はありません。", "OK");
            return;
        }
        Undo.DestroyObjectImmediate(root);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    private static void BuildSample(Transform root, float x, string label, Material wall, Material glass, Material frame, Material rail, Material sign, int floors, bool balconies, bool storefront)
    {
        const float width = 4.4f;
        float height = floors * 2.65f;
        float centerY = height * 0.5f;
        Cube(root, label + " 外壁", new Vector3(x, centerY, 0f), new Vector3(width, height, 0.3f), wall);
        Cube(root, label + " 屋上縁", new Vector3(x, height + 0.12f, 0f), new Vector3(width + 0.18f, 0.24f, 0.42f), frame);

        for (int floor = 0; floor < floors; floor++)
        {
            float y = 1.35f + floor * 2.65f;
            int windows = storefront && floor == 0 ? 2 : 3;
            for (int i = 0; i < windows; i++)
            {
                float wx = x + (i - (windows - 1) * 0.5f) * (windows == 2 ? 1.65f : 1.27f);
                float ww = windows == 2 ? 1.34f : 0.86f;
                float wh = storefront && floor == 0 ? 1.9f : 1.12f;
                Cube(root, label + " 窓枠", new Vector3(wx, y, -0.19f), new Vector3(ww + 0.14f, wh + 0.14f, 0.08f), frame);
                Cube(root, label + " 窓ガラス", new Vector3(wx, y, -0.245f), new Vector3(ww, wh, 0.04f), glass);
                if (windows == 3)
                    Cube(root, label + " 窓の縦桟", new Vector3(wx, y, -0.28f), new Vector3(0.045f, wh, 0.035f), frame);
            }
            if (balconies && floor > 0)
            {
                Cube(root, label + " バルコニー床", new Vector3(x, y - 0.74f, -0.43f), new Vector3(width - 0.35f, 0.10f, 0.58f), frame);
                Cube(root, label + " バルコニー手すり", new Vector3(x, y - 0.27f, -0.69f), new Vector3(width - 0.45f, 0.07f, 0.07f), rail);
                for (int post = 0; post < 8; post++)
                    Cube(root, label + " 手すり支柱", new Vector3(x - (width - 0.45f) * 0.5f + post * (width - 0.45f) / 7f, y - 0.51f, -0.69f), new Vector3(0.045f, 0.42f, 0.045f), rail);
            }
        }

        if (storefront)
        {
            Cube(root, "店舗のひさし", new Vector3(x, 2.48f, -0.46f), new Vector3(width + 0.12f, 0.22f, 0.62f), sign);
            Cube(root, "店舗の看板", new Vector3(x, 3.25f, -0.205f), new Vector3(3.1f, 0.46f, 0.09f), sign);
        }

        GameObject title = new GameObject(label + " 見本名");
        Undo.RegisterCreatedObjectUndo(title, "Create facade sample label");
        title.transform.SetParent(root, false);
        title.transform.localPosition = new Vector3(x, height - 0.48f, -0.31f);
        title.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        TextMesh text = title.AddComponent<TextMesh>();
        text.text = label;
        text.characterSize = 0.14f;
        text.fontSize = 32;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(0.18f, 0.20f, 0.19f);
    }

    private static void Cube(Transform parent, string objectName, Vector3 position, Vector3 scale, Material material)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(cube, "Create facade detail");
        cube.name = objectName;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = position;
        cube.transform.localScale = scale;
        Collider collider = cube.GetComponent<Collider>();
        if (collider != null) Object.DestroyImmediate(collider);
        cube.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    private static Material MakeMaterial(string materialName, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        const string folder = "Assets/GeneratedFacadePreview";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "GeneratedFacadePreview");
        string path = folder + "/" + materialName.Replace(" ", "") + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = materialName };
            AssetDatabase.CreateAsset(material, path);
        }
        material.color = color;
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }
}
