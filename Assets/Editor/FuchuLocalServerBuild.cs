using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Explicit editor commands only; never generates scenes or builds during import.</summary>
public static class FuchuLocalServerBuild
{
    private const string SourceScene = "Assets/Scenes/SampleScene.unity";
    private const string ServerScene = "Assets/Scenes/FuchuLocalServer.unity";

    [MenuItem("Fuchu Walk/Local Server/Generate Server Scene")]
    public static void GenerateScene()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("再生を停止してから実行してください。");
        Scene previous = SceneManager.GetActiveScene();
        Scene source = SceneManager.GetSceneByPath(SourceScene);
        bool openedSource = !source.isLoaded;
        Scene generated = default;
        try
        {
            if (openedSource) source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
            MeshFilter road = null;
            Transform boundary = null;
            FuchuWalkController player = null;
            FuchuTrafficSpawner traffic = null;
            FuchuResidentSpawner residents = null;
            foreach (GameObject root in source.GetRootGameObjects())
            {
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.name == "FuchuLocalRoads_1000m") road = filter;
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    if (child.name == "FuchuWalkCircularBoundary_1km") boundary = child;
                if (player == null) player = root.GetComponentInChildren<FuchuWalkController>(true);
                if (traffic == null) traffic = root.GetComponentInChildren<FuchuTrafficSpawner>(true);
                if (residents == null) residents = root.GetComponentInChildren<FuchuResidentSpawner>(true);
            }
            if (road == null || road.sharedMesh == null || !road.sharedMesh.isReadable || boundary == null || boundary.childCount == 0 || player == null)
                throw new InvalidOperationException("SampleSceneに読み取り可能な1km道路・円形境界・歩行プレイヤーが必要です。");
            if (SceneManager.GetSceneByPath(ServerScene).isLoaded)
                throw new InvalidOperationException("生成済みサーバーシーンを閉じてから作り直してください。");
            Transform sourceRoot = road.transform.parent;
            Vector2 center = Vector2.zero;
            foreach (Transform segment in boundary)
            {
                Vector3 point = sourceRoot.InverseTransformPoint(segment.position);
                center += new Vector2(point.x, point.z);
            }
            center /= boundary.childCount;
            generated = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var world = new GameObject("Fuchu Server World");
            SceneManager.MoveGameObjectToScene(world, generated);
            var roadObject = new GameObject("FuchuLocalRoads_1000m");
            roadObject.transform.SetParent(world.transform, false);
            roadObject.transform.SetLocalPositionAndRotation(road.transform.localPosition, road.transform.localRotation);
            roadObject.transform.localScale = road.transform.localScale;
            var serverRoad = roadObject.AddComponent<MeshFilter>();
            serverRoad.sharedMesh = road.sharedMesh;
            roadObject.AddComponent<MeshCollider>().sharedMesh = road.sharedMesh;
            if (traffic != null)
            {
                var obj = new GameObject("Fuchu Server Traffic"); obj.transform.SetParent(world.transform, false);
                obj.transform.SetLocalPositionAndRotation(traffic.transform.localPosition, traffic.transform.localRotation);
                obj.transform.localScale = traffic.transform.localScale;
                var copy = obj.AddComponent<FuchuTrafficSpawner>(); EditorUtility.CopySerialized(traffic, copy);
                copy.worldRoot = world.transform; copy.roadMeshFilter = serverRoad; copy.playerController = null; copy.enabled = true;
            }
            if (residents != null)
            {
                var obj = new GameObject("Fuchu Server Residents"); obj.transform.SetParent(world.transform, false);
                obj.transform.SetLocalPositionAndRotation(residents.transform.localPosition, residents.transform.localRotation);
                obj.transform.localScale = residents.transform.localScale;
                var copy = obj.AddComponent<FuchuResidentSpawner>(); EditorUtility.CopySerialized(residents, copy);
                copy.worldRoot = world.transform; copy.roadMeshFilter = serverRoad; copy.enabled = true;
            }
            var entry = world.AddComponent<FuchuLocalServer>();
            entry.road = serverRoad; entry.playableCenter = new Vector3(center.x, 0, center.y);
            var settings = new SerializedObject(player);
            entry.walkingSpeed = settings.FindProperty("walkSpeed").floatValue;
            entry.sprintSpeed = settings.FindProperty("sprintSpeed").floatValue;
            if (!EditorSceneManager.SaveScene(generated, ServerScene)) throw new IOException("サーバーシーンを保存できません。");
            Debug.Log("サーバー専用シーンを生成しました: " + ServerScene + "（描画・ローカルプレイヤー・Cesiumなし）");
        }
        finally
        {
            if (generated.IsValid() && generated.isLoaded) EditorSceneManager.CloseScene(generated, true);
            if (openedSource && source.IsValid() && source.isLoaded) EditorSceneManager.CloseScene(source, true);
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
        }
    }

    [Serializable]
    private sealed class ServerManifest
    {
        public int formatVersion = 1;
        public string target;
        public int protocolVersion = 5;
        public string unityVersion = Application.unityVersion;
        public string applicationVersion = Application.version;
        public string builtUtc = DateTime.UtcNow.ToString("O");
    }

    [MenuItem("Fuchu Walk/Local Server/Build Linux Server (AWS)")]
    public static void BuildLinuxServer()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("再生を停止してから実行してください。");
        RequireLinuxBuildSupport();
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string folder = EditorUtility.OpenFolderPanel("Linux専用サーバーの出力先（Windows用とは別）", "", "");
        if (string.IsNullOrEmpty(folder)) return;
        ValidateOutputFolder(folder);
        GenerateScene();
        BuildLinuxPlayer(folder);
    }

    public static void BuildLinuxServerBatch()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("このコマンドはUnityのバッチモードで実行してください。");
        RequireLinuxBuildSupport();
        string folder = Environment.GetEnvironmentVariable("FUCHU_LINUX_BUILD_DIRECTORY");
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
            throw new InvalidOperationException("FUCHU_LINUX_BUILD_DIRECTORYに出力先の絶対パスを指定してください。");
        ValidateOutputFolder(folder);
        if (!File.Exists(ServerScene))
            throw new FileNotFoundException("先にサーバー専用シーンを生成してください。", ServerScene);
        if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length != 0)
            throw new IOException("空の出力フォルダーを指定してください。");
        Directory.CreateDirectory(folder);
        BuildLinuxPlayer(folder);
    }

    private static void RequireLinuxBuildSupport()
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
            throw new InvalidOperationException("Unity Hubで、このEditorのLinux Dedicated Server Build Supportを追加してください。");
    }

    private static void BuildLinuxPlayer(string folder)
    {
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { ServerScene }, locationPathName = Path.Combine(folder, "FuchuServer.x86_64"),
            target = BuildTarget.StandaloneLinux64, options = BuildOptions.None,
            subtarget = (int)StandaloneBuildSubtarget.Server
        });
        if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Linuxサーバービルドに失敗しました。Consoleを確認してください。");
        File.WriteAllText(Path.Combine(folder, "server-manifest.json"), JsonUtility.ToJson(new ServerManifest { target = "linux-x86_64" }, true));
        File.WriteAllText(Path.Combine(folder, "StartLocalServer.sh"),
            "#!/usr/bin/env bash\nset -euo pipefail\ncd -- \"$(dirname -- \"$0\")\"\n" +
            "exec ./FuchuServer.x86_64 -batchmode -nographics -fuchu-bind \"${FUCHU_BIND:-127.0.0.1}\" -fuchu-port \"${FUCHU_PORT:-7777}\" -logFile \"${FUCHU_LOG:-./LocalServer.log}\"\n");
        Debug.Log("Linux専用サーバーをビルドしました。AWSへのアップロード・起動は行っていません。");
    }

    private static void ValidateOutputFolder(string folder)
    {
        string fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string assetsFolder = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullFolder.Equals(assetsFolder, StringComparison.OrdinalIgnoreCase) ||
            fullFolder.StartsWith(assetsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ビルド先はAssetsの外にしてください。プラットフォームごとに別の出力フォルダーを使用してください。");
    }

    [MenuItem("Fuchu Walk/Local Server/Build Windows Server")]
    public static void BuildWindowsServer()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("再生を停止してから実行してください。");
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string folder = EditorUtility.OpenFolderPanel("ローカル専用サーバーの出力先", "", "");
        if (string.IsNullOrEmpty(folder)) return;
        ValidateOutputFolder(folder);
        GenerateScene();
        // A regular Windows player running without graphics avoids an extra Server Build Support module.
        // Only the baked server scene is included; the client SampleScene and its services are excluded.
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { ServerScene }, locationPathName = Path.Combine(folder, "FuchuLocalServer.exe"),
            target = BuildTarget.StandaloneWindows64, options = BuildOptions.None,
            subtarget = (int)StandaloneBuildSubtarget.Player
        });
        if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("サーバービルドが完了しませんでした。Consoleを確認してください。");
        File.WriteAllText(Path.Combine(folder, "server-manifest.json"), JsonUtility.ToJson(new ServerManifest { target = "windows-x86_64" }, true));
        File.WriteAllText(Path.Combine(folder, "StartLocalServer.cmd"),
            "@echo off\r\ncd /d \"%~dp0\"\r\n\"%~dp0FuchuLocalServer.exe\" -batchmode -nographics -fuchu-bind 127.0.0.1 -fuchu-port 7777 -logFile \"%~dp0LocalServer.log\"\r\n");
        Debug.Log("ローカル専用サーバーをビルドしました。起動は出力先のStartLocalServer.cmdを使用してください。");
    }
}
