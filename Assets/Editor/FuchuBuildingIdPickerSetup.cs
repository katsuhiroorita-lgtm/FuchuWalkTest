using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

#pragma warning disable 618

public static class FuchuBuildingIdPickerSetup
{
    [MenuItem("Fuchu Walk/Enable Building ID Picker")]
    private static void EnableBuildingIdPicker()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "再生を停止してから実行してください。", "OK");
            return;
        }

        GameObject player = GameObject.Find("FuchuWalkPlayer");
        Cesium3DTileset tileset = Object.FindFirstObjectByType<Cesium3DTileset>();
        if (player == null || tileset == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "歩行プレイヤーまたはCesium3DTilesetが見つかりません。", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Enable Fuchu building ID picker");
        int undoGroup = Undo.GetCurrentGroup();

        CesiumMetadata legacyMetadata = tileset.GetComponent<CesiumMetadata>();
        if (legacyMetadata != null)
            Undo.DestroyObjectImmediate(legacyMetadata);

        FuchuBuildingIdPicker picker = player.GetComponent<FuchuBuildingIdPicker>();
        if (picker == null)
            picker = Undo.AddComponent<FuchuBuildingIdPicker>(player);

        SerializedObject serializedPicker = new SerializedObject(picker);
        Camera camera = player.GetComponentInChildren<Camera>();
        serializedPicker.FindProperty("targetCamera").objectReferenceValue = camera;
        serializedPicker.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(player.scene);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = player;
        EditorUtility.DisplayDialog(
            "Fuchu Walk",
            "建物の自動情報表示を設定しました。以前の旧式情報読み取り部品は取り外しました。Ctrl+Sで保存してください。再生中は画面中央の照準を建物に合わせると、ID・用途・高さ・階数などが自動表示されます。",
            "OK");
    }
}
#pragma warning restore 618
