using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Small, reversible tool for visually aligning the local building tiles with the DEM ground.</summary>
public sealed class FuchuHeightAlignmentWindow : EditorWindow
{
    private const string TilesetName = "Cesium3DTileset";
    private const string TerrainName = "FuchuLocalDEMTerrain_1000m";
    private const string RoadsName = "FuchuLocalRoads_1000m";

    [MenuItem("Fuchu Walk/Building and Ground Height Alignment")]
    private static void Open()
    {
        GetWindow<FuchuHeightAlignmentWindow>("Height Alignment");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("建物と地面の高さ合わせ", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "建物の底と地面の隙間を見ながら、建物データだけを少しずつ上下します。地形や道路、歩行位置は動かしません。\n\n" +
            "先に再生を停止してください。SceneビューまたはGameビューで同じ場所を見て、0.25mずつ調整します。いつでもCtrl+Zで戻せます。",
            MessageType.Info);

        if (EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("調整する前に、上部の再生ボタンを押して再生を停止してください。", MessageType.Warning);
            return;
        }

        GameObject tileset = GameObject.Find(TilesetName);
        GameObject terrain = GameObject.Find(TerrainName);
        if (terrain == null) terrain = GameObject.Find("FuchuLocalDEMTerrain_3000m");
        GameObject roads = GameObject.Find(RoadsName);
        if (roads == null) roads = GameObject.Find("FuchuLocalRoads_700m");
        if (roads == null) roads = GameObject.Find("FuchuLocalRoads_3000m");
        if (tileset == null || terrain == null)
        {
            EditorGUILayout.HelpBox("建物タイルまたは1km地形が見つかりません。両方がシーンに読み込まれているか確認してください。", MessageType.Error);
            return;
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("現在の高さ（地形と道路は変更しません）", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("建物データ", tileset.transform.localPosition.y.ToString("+0.00;-0.00;0.00") + " m");
        EditorGUILayout.LabelField("地形データ", terrain.transform.localPosition.y.ToString("+0.00;-0.00;0.00") + " m");
        if (roads != null)
            EditorGUILayout.LabelField("道路データ", roads.transform.localPosition.y.ToString("+0.00;-0.00;0.00") + " m");

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("建物を上げる", EditorStyles.boldLabel);
        DrawAdjustmentButtons(tileset, 1f);
        DrawAdjustmentButtons(tileset, 0.25f);
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("建物を下げる", EditorStyles.boldLabel);
        DrawAdjustmentButtons(tileset, -0.25f);
        DrawAdjustmentButtons(tileset, -1f);
        EditorGUILayout.Space(8);
        if (GUILayout.Button("今の高さでシーンを保存（Ctrl+Sでも保存できます）", GUILayout.Height(30)))
        {
            EditorSceneManager.SaveScene(tileset.scene);
        }
    }

    private void DrawAdjustmentButtons(GameObject tileset, float amount)
    {
        string label = Mathf.Abs(amount) < 0.5f ? "0.25 m" : "1 m";
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(55));
        if (GUILayout.Button("一度だけ動かす", GUILayout.Height(26)))
        {
            Undo.RecordObject(tileset.transform, "Adjust building height");
            Vector3 position = tileset.transform.localPosition;
            position.y += amount;
            tileset.transform.localPosition = position;
            EditorSceneManager.MarkSceneDirty(tileset.scene);
            SceneView.RepaintAll();
            Repaint();
        }
        EditorGUILayout.EndHorizontal();
    }
}
