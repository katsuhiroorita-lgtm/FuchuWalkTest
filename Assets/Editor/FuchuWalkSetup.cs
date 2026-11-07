using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FuchuWalkSetup
{
    private const string PrototypeName = "FuchuWalkPlayer";
    // DEM at the start point is approximately 57.2 m. The building tileset is
    // provisionally shifted down by about 17.6 m to test the apparent vertical
    // mismatch. With the current Cesium origin height of 80 m, ground is local Y=-22.8 m.
    private const float GroundY = -22.8f;
    private const float BuildingVerticalOffset = -17.63f;

    [MenuItem("Fuchu Walk/Create Walking Prototype")]
    private static void CreateWalkingPrototype()
    {
        GameObject georeference = GameObject.Find("CesiumGeoreference");
        if (georeference == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "CesiumGeoreferenceがシーンにありません。先にCesiumのBlank 3D Tilesetを追加してください。", "OK");
            return;
        }

        if (GameObject.Find(PrototypeName) != null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "歩行用プレイヤーはすでに作成されています。", "OK");
            return;
        }

        Camera mainCamera = Camera.main;
        if (mainCamera == null)
            mainCamera = Object.FindFirstObjectByType<Camera>();
        if (mainCamera == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "Main Cameraが見つかりません。", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Create Fuchu walking prototype");
        int undoGroup = Undo.GetCurrentGroup();

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        Undo.RegisterCreatedObjectUndo(ground, "Create temporary walking ground");
        ground.name = "TemporaryWalkGround";
        ground.transform.SetParent(georeference.transform, false);
        ground.transform.localPosition = new Vector3(0f, GroundY, 0f);
        ground.transform.localRotation = Quaternion.identity;
        ground.transform.localScale = new Vector3(50f, 1f, 50f);

        GameObject player = new GameObject(PrototypeName);
        Undo.RegisterCreatedObjectUndo(player, "Create walking player");
        player.transform.SetParent(georeference.transform, false);
        player.transform.localPosition = new Vector3(0f, GroundY + 1.1f, -20f);
        player.transform.localRotation = Quaternion.identity;

        CharacterController characterController = Undo.AddComponent<CharacterController>(player);
        characterController.height = 1.8f;
        characterController.radius = 0.35f;
        characterController.center = new Vector3(0f, 0.9f, 0f);
        characterController.stepOffset = 0.3f;
        characterController.slopeLimit = 45f;

        Undo.SetTransformParent(mainCamera.transform, player.transform, "Attach camera to walking player");
        Undo.RecordObject(mainCamera.transform, "Position player camera");
        mainCamera.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        mainCamera.transform.localRotation = Quaternion.identity;
        mainCamera.nearClipPlane = 0.1f;
        mainCamera.farClipPlane = 2000f;

        FuchuWalkController controller = Undo.AddComponent<FuchuWalkController>(player);
        SerializedObject serializedController = new SerializedObject(controller);
        serializedController.FindProperty("cameraTransform").objectReferenceValue = mainCamera.transform;
        serializedController.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(player.scene);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = player;
        EditorUtility.DisplayDialog(
            "Fuchu Walk",
            "歩行用プレイヤーと仮の地面を作成しました。WASDで移動、マウスで視点変更、Shiftでダッシュ、Spaceでジャンプできます。Escでマウス操作を解除します。",
            "OK");
    }

    [MenuItem("Fuchu Walk/Align Prototype to Start Terrain")]
    private static void AlignPrototypeToStartTerrain()
    {
        GameObject ground = GameObject.Find("TemporaryWalkGround");
        GameObject player = GameObject.Find(PrototypeName);
        GameObject tileset = GameObject.Find("Cesium3DTileset");
        if (ground == null || player == null || tileset == null)
        {
            EditorUtility.DisplayDialog("Fuchu Walk", "歩行用の地面、プレイヤー、Cesium3DTilesetのいずれかが見つかりません。", "OK");
            return;
        }

        Undo.RecordObject(ground.transform, "Align temporary ground to DEM");
        Undo.RecordObject(player.transform, "Align player to DEM");
        Undo.RecordObject(tileset.transform, "Align building tileset to DEM height");

        Vector3 tilesetPosition = tileset.transform.localPosition;
        tilesetPosition.y = BuildingVerticalOffset;
        tileset.transform.localPosition = tilesetPosition;

        Vector3 groundPosition = ground.transform.localPosition;
        groundPosition.y = GroundY;
        ground.transform.localPosition = groundPosition;

        Vector3 playerPosition = player.transform.localPosition;
        playerPosition.y = GroundY + 1.1f;
        player.transform.localPosition = playerPosition;

        EditorSceneManager.MarkSceneDirty(player.scene);
        EditorUtility.DisplayDialog(
            "Fuchu Walk",
            "確認用に建物タイルを約17.6m下げ、仮地面とプレイヤーを開始地点のDEM標高に合わせました。標高差はタイル全体の値から見積もった暫定値です。再生して、道路面と建物の位置関係を確認してください。",
            "OK");
    }
}
