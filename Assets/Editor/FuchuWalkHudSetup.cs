using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FuchuWalkHudSetup
{
    [MenuItem("Fuchu Walk/Add Controls Overlay")]
    private static void AddControlsOverlay()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "先に再生を停止してから操作案内を追加してください。", "OK");
            return;
        }

        GameObject player = GameObject.Find("FuchuWalkPlayer");
        if (player == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "FuchuWalkPlayerが見つかりません。", "OK");
            return;
        }
        if (player.GetComponent<FuchuWalkHud>() != null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "操作案内はすでに追加されています。", "OK");
            return;
        }

        Undo.AddComponent<FuchuWalkHud>(player);
        EditorSceneManager.MarkSceneDirty(player.scene);
        Selection.activeGameObject = player;
        EditorUtility.DisplayDialog("Fuchu Walk", "ゲーム画面に操作案内を追加しました。Ctrl+Sで保存してください。F1キーで表示を切り替えられます。", "OK");
    }
}
