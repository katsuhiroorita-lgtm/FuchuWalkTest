using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Adds the runtime resident manager to the current scene.</summary>
public static class FuchuResidentSetup
{
    private const string ResidentsName = "FuchuWalkResidents_1km";

    [MenuItem("Fuchu Walk/Create Walking Residents Within 1km")]
    private static void CreateResidents()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "再生を停止してから住人を設定してください。", "OK");
            return;
        }
        if (GameObject.Find(ResidentsName) != null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "住人の設定はすでにあります。作り直す場合は「Remove Walking Residents」を先に実行してください。", "OK");
            return;
        }

        CesiumForUnity.CesiumGeoreference georeference = Object.FindFirstObjectByType<CesiumForUnity.CesiumGeoreference>();
        GameObject player = GameObject.Find("FuchuWalkPlayer");
        GameObject roads = GameObject.Find("FuchuLocalRoads_1000m");
        if (roads == null) roads = GameObject.Find("FuchuLocalRoads_3000m");
        MeshFilter roadFilter = roads != null ? roads.GetComponent<MeshFilter>() : null;
        if (georeference == null || player == null || roadFilter == null || roadFilter.sharedMesh == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "歩行プレイヤー、CesiumGeoreference、または道路面が見つかりません。先に1km地形と道路面を作成してください。", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Create walking residents");
        int undoGroup = Undo.GetCurrentGroup();
        GameObject managerObject = new GameObject(ResidentsName);
        Undo.RegisterCreatedObjectUndo(managerObject, "Create walking residents");
        managerObject.transform.SetParent(georeference.transform, false);
        FuchuResidentSpawner spawner = Undo.AddComponent<FuchuResidentSpawner>(managerObject);
        spawner.worldRoot = georeference.transform;
        spawner.roadMeshFilter = roadFilter;
        spawner.initialPosition = georeference.transform.InverseTransformPoint(player.transform.position);
        spawner.residentCount = 192;
        spawner.radiusMeters = 1000f;
        EditorUtility.SetDirty(spawner);
        EditorSceneManager.MarkSceneDirty(managerObject.scene);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = managerObject;
        EditorUtility.DisplayDialog("Fuchu Walk", "半径1km内の道路両脇に、10種類の住人を配置する設定を追加しました。住人は再生中に最大192人表示され、道路の縁に沿って歩きます。Ctrl+Sで保存してから再生してください。", "OK");
    }

    [MenuItem("Fuchu Walk/Remove Walking Residents")]
    private static void RemoveResidents()
    {
        GameObject managerObject = GameObject.Find(ResidentsName);
        if (managerObject == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "住人の設定はありません。", "OK");
            return;
        }
        Undo.DestroyObjectImmediate(managerObject);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }
}
