using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FuchuTrafficSetup
{
    private const string TrafficObjectName = "FuchuTraffic_1km";

    [MenuItem("Fuchu Walk/Create Simple Road Traffic")]
    private static void CreateTraffic()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "再生を停止してから車を設定してください。", "OK");
            return;
        }
        CesiumGeoreference georeference = Object.FindFirstObjectByType<CesiumGeoreference>();
        GameObject player = GameObject.Find("FuchuWalkPlayer");
        GameObject roads = GameObject.Find("FuchuLocalRoads_1000m");
        if (roads == null) roads = GameObject.Find("FuchuLocalRoads_3000m");
        MeshFilter roadFilter = roads != null ? roads.GetComponent<MeshFilter>() : null;
        if (georeference == null || player == null || roadFilter == null || roadFilter.sharedMesh == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "歩行プレイヤー、CesiumGeoreference、または道路面が見つかりません。先に道路面を作成してください。", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Update simple road traffic");
        int undoGroup = Undo.GetCurrentGroup();
        GameObject manager = GameObject.Find(TrafficObjectName);
        bool created = manager == null;
        if (created)
        {
            manager = new GameObject(TrafficObjectName);
            Undo.RegisterCreatedObjectUndo(manager, "Create simple road traffic");
            manager.transform.SetParent(georeference.transform, false);
        }
        else Undo.RecordObject(manager, "Update simple road traffic");
        FuchuTrafficSpawner spawner = manager.GetComponent<FuchuTrafficSpawner>();
        if (spawner == null) spawner = Undo.AddComponent<FuchuTrafficSpawner>(manager);
        else Undo.RecordObject(spawner, "Update simple road traffic settings");
        spawner.worldRoot = georeference.transform;
        spawner.roadMeshFilter = roadFilter;
        spawner.playerController = player.GetComponent<FuchuWalkController>();
        spawner.initialPosition = georeference.transform.InverseTransformPoint(player.transform.position);
        spawner.carCount = 72;
        spawner.radiusMeters = 1000f;
        spawner.minimumSpeed = 5f;
        spawner.maximumSpeed = 9f;
        EditorUtility.SetDirty(spawner);

        FuchuNightWindowLights windowLights = player.GetComponent<FuchuNightWindowLights>();
        if (windowLights == null) windowLights = Undo.AddComponent<FuchuNightWindowLights>(player);
        else Undo.RecordObject(windowLights, "Update night window lights");
        windowLights.worldRoot = georeference.transform;
        windowLights.initialPosition = spawner.initialPosition;
        windowLights.radiusMeters = 1000f;
        EditorUtility.SetDirty(windowLights);
        EditorSceneManager.MarkSceneDirty(manager.scene);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = manager;
        EditorUtility.DisplayDialog("Fuchu Walk", "簡易車両を最大72台に更新しました。道路網内の離れた目的地まで走行し、接触すると開始地点へ戻ります。夜は建物の窓に明かりが灯ります。Ctrl+Sでシーンを保存してから再生してください。", "OK");
    }

    [MenuItem("Fuchu Walk/Remove Simple Road Traffic")]
    private static void RemoveTraffic()
    {
        GameObject manager = GameObject.Find(TrafficObjectName);
        if (manager == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "道路を走る車の設定はありません。", "OK");
            return;
        }
        Undo.DestroyObjectImmediate(manager);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }
}
