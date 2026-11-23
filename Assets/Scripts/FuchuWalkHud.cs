using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>Control guide, accelerated day/night cycle, and simple prototype weather.</summary>
public sealed class FuchuWalkHud : MonoBehaviour
{
    private const int FullHdWidth = 1920;
    private const int FullHdHeight = 1080;
    private const float MinutesPerGameDay = 24f * 60f;
    private const float SecondsPerGameDayAtNormalSpeed = 120f;
    private FuchuWalkController player;
    private bool creditsVisible;
    private bool timeMenuVisible;
    private bool optionsVisible;
    private bool raining;
    private Light sun;
    private Material runtimeSkybox;
    private Material originalSkybox;
    private float gameMinutes;
    private float timeSpeed = 1f;
    private float weatherTimer;
    private Texture2D whiteTexture;
    private Texture2D cloudTexture;
    private Texture2D starTexture;
    private Material cloudMaterial;
    private Material starMaterial;
    private ParticleSystem cloudParticles;
    private ParticleSystem starParticles;
    private ParticleSystemRenderer cloudRenderer;
    private ParticleSystemRenderer starRenderer;
    private GUIStyle panelStyle;
    private GUIStyle headingStyle;
    private GUIStyle textStyle;
    private GUIStyle resultTitleStyle;

    internal static bool ShowBuildingIds { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void LoadOptions() => ShowBuildingIds = PlayerPrefs.GetInt("Fuchu.ShowBuildingIds", 0) != 0;

    public bool TimeMenuVisible => timeMenuVisible;
    public float NightFactor
    {
        get
        {
            float hour = gameMinutes / 60f;
            return 1f - Mathf.Clamp01(Mathf.Sin((hour - 6f) * Mathf.PI / 12f));
        }
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ApplyStartupDisplayMode()
    {
        Screen.SetResolution(FullHdWidth, FullHdHeight, FullScreenMode.Windowed);
    }
#endif

    private void Start()
    {
        player = GetComponent<FuchuWalkController>();
        gameMinutes = 12f * 60f;
        weatherTimer = NextWeatherTimer();
        originalSkybox = RenderSettings.skybox;
        if (originalSkybox != null)
        {
            runtimeSkybox = new Material(originalSkybox);
            RenderSettings.skybox = runtimeSkybox;
        }
        CreateSkyObjects();
    }

    private void Update()
    {
        if (player != null && !player.IsPlaying) return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && (player == null || !player.MenuOpen))
        {
            if (keyboard.f2Key.wasPressedThisFrame) creditsVisible = !creditsVisible;
#if UNITY_EDITOR
            if (keyboard.f3Key.wasPressedThisFrame)
            {
                raining = !raining;
                weatherTimer = NextWeatherTimer();
            }
            if (keyboard.f4Key.wasPressedThisFrame)
            {
                timeMenuVisible = !timeMenuVisible;
                if (timeMenuVisible)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
#endif
        }

        float minutesPerSecond = MinutesPerGameDay / SecondsPerGameDayAtNormalSpeed;
        gameMinutes = (gameMinutes + Time.deltaTime * minutesPerSecond * timeSpeed) % MinutesPerGameDay;
        weatherTimer -= Time.deltaTime;
        if (weatherTimer <= 0f)
        {
            raining = !raining;
            weatherTimer = NextWeatherTimer();
        }
        ApplyWorldLighting();
    }

    private void OnGUI()
    {
        EnsureStyles();
        if (player != null && (!player.IsPlaying || player.MenuOpen))
        {
            DrawMenuScreen();
            return;
        }
        if (raining) DrawRain();
        if (player != null)
        {
            DrawPlayerStatus();
            DrawBattleStandings(new Rect(Screen.width - 346f, Screen.height - 231f, 330f, 215f));
            if (player.IsOnlineExploration) DrawOnlineCountdown();
        }
#if UNITY_EDITOR
        if (timeMenuVisible)
        {
            GUI.Box(new Rect(16, 204, 366, 122), GUIContent.none, panelStyle);
            GUI.Label(new Rect(30, 212, 340, 24), "時間の速さを選択（F4で閉じる）", headingStyle);
            DrawSpeedButton(new Rect(30, 248, 56, 34), 0.25f, "0.25x");
            DrawSpeedButton(new Rect(92, 248, 56, 34), 0.5f, "0.5x");
            DrawSpeedButton(new Rect(154, 248, 56, 34), 1f, "1x");
            DrawSpeedButton(new Rect(216, 248, 56, 34), 2f, "2x");
            DrawSpeedButton(new Rect(278, 248, 56, 34), 4f, "4x");
        }

#endif
        if (creditsVisible)
        {
            float y = timeMenuVisible ? 336f : 204f;
            GUI.Box(new Rect(16, y, 520, 144), GUIContent.none, panelStyle);
            GUI.Label(new Rect(30, y + 8, 490, 28), "使用データの出典", headingStyle);
            GUI.Label(new Rect(30, y + 40, 490, 96),
                "出典：3D都市モデル（Project PLATEAU）東京都府中市（2025年度）\n建物・地形・道路データを加工して作成\n配布元：G空間情報センター／利用条件は配布ページを参照\n施設名の一部は府中市オープンデータ（公共施設・観光施設一覧）をもとに照合",
                textStyle);
        }
    }

    private void DrawOnlineCountdown()
    {
        int countdown = player.Online.Countdown;
        if (countdown == 0 && !player.Online.ShowingGo) return;
        var frame = new Rect((Screen.width - 160f) * 0.5f, (Screen.height - 94f) * 0.45f, 160f, 94f);
        GUI.Box(frame, GUIContent.none, panelStyle);
        GUI.Label(frame, countdown > 0 ? countdown.ToString() : "スタート", resultTitleStyle);
    }

    private void DrawPlayerStatus()
    {
        Rect panel = new Rect(16f, Screen.height - 257f, 330f, 241f);
        GUI.Box(panel, GUIContent.none, panelStyle);
        int seconds = (int)System.Math.Ceiling(player.RemainingRunSeconds);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 7f, 306f, 26f),
            player.IsOnlineExploration && player.Online.Countdown > 0 ? "まもなく試合開始" :
            player.IsOnlineExploration && !player.Online.MatchStarted ? "試合開始を待っています" :
            $"残り時間  {seconds / 60:00}:{seconds % 60:00}", headingStyle);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 36f, 264f, 22f),
            $"スタミナ  {System.Math.Ceiling(player.Stamina):N0} / {player.MaxStamina:N0}", textStyle);

        Rect bar = new Rect(panel.x + 12f, panel.y + 63f, 264f, 12f);
        Color previous = GUI.color;
        GUI.color = new Color(0.12f, 0.16f, 0.19f);
        GUI.DrawTexture(bar, whiteTexture);
        bar.width *= (float)(player.Stamina / player.MaxStamina);
        GUI.color = new Color(0.33f, 0.84f, 0.63f);
        GUI.DrawTexture(bar, whiteTexture);
        GUI.color = previous;

        GUI.Label(new Rect(panel.x + 12f, panel.y + 81f, 196f, 22f),
            $"確定エリア  {player.TerritoryCoverage * 100.0:0.0000}%", textStyle);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 105f, 264f, 22f),
            $"所有中の建物ポイント  {player.BuildingPoints:N0} pt", textStyle);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 131f, 264f, 22f),
            $"速度  Lv{player.SpeedLevel}　頑丈さ  Lv{player.ToughnessLevel}", textStyle);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 157f, 306f, 22f),
            $"移動速度ポイント（緑）  {player.MovementPoints:N1} pt", textStyle);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 183f, 306f, 22f),
            $"頑丈さポイント（赤）  {player.ToughnessPoints:N1} pt", textStyle);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 209f, 306f, 22f),
            $"スタミナ上限ポイント（黄）  {player.StaminaCapacityPoints:N0} pt", textStyle);
    }

    private void DrawMenuScreen()
    {
        int previousDepth = GUI.depth;
        Color previousColor = GUI.color;
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.depth = -100;
        GUI.color = new Color(0.035f, 0.055f, 0.09f, player.MenuOpen ? 0.55f : 0.94f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), whiteTexture);
        GUI.color = previousColor;

        bool online = player.IsOnlineExploration;
        bool finished = player.RunFinished || online && player.Online.MatchFinished;
        bool onlineMenu = player.Online != null && player.Online.MenuVisible &&
            (!online || !player.Online.MatchStarted) && !optionsVisible;
        float width = onlineMenu ? 640f : 480f;
        float height = onlineMenu ? 540f : finished ? (online ? 482f : 710f) : optionsVisible ? 380f : player.MenuOpen ? 590f : 330f;
        float scale = Mathf.Min(1f, Mathf.Min((Screen.width - 20f) / width, (Screen.height - 20f) / height));
        if (scale <= 0f)
        {
            GUI.depth = previousDepth;
            return;
        }
        var position = new Vector3((Screen.width - width * scale) * 0.5f, (Screen.height - height * scale) * 0.5f, 0f);
        GUI.matrix = Matrix4x4.TRS(position, Quaternion.identity, new Vector3(scale, scale, 1f));
        GUI.Box(new Rect(0, 0, width, height), GUIContent.none, panelStyle);
        if (onlineMenu)
            player.Online.DrawMenu(headingStyle, textStyle);
        else if (finished)
            DrawResults();
        else if (optionsVisible)
            DrawOptions();
        else if (player.MenuOpen)
            DrawGameplayMenu();
        else
            DrawStartMenu();
        GUI.matrix = previousMatrix;
        GUI.depth = previousDepth;
    }

    private void DrawStartMenu()
    {
        GUI.Label(new Rect(24, 24, 432, 40), "府中まち歩き", resultTitleStyle);
        if (GUI.Button(new Rect(120, 79, 240, 44), "Game Start（NPC対戦）")) player.BeginRun();
        if (GUI.Button(new Rect(120, 133, 240, 44), "マルチプレイ")) player.Online.OpenMenu();
        if (GUI.Button(new Rect(120, 187, 240, 44), "Option")) optionsVisible = true;
        if (GUI.Button(new Rect(120, 241, 240, 44), "Exit")) Application.Quit();
    }

    internal void CloseGameplayMenu() => optionsVisible = false;

    private void DrawGameplayMenu()
    {
        GUI.Label(new Rect(24, 24, 432, 40), "ゲームメニュー", resultTitleStyle);
        int seconds = (int)System.Math.Ceiling(player.RemainingRunSeconds);
        GUI.Label(new Rect(120, 85, 240, 26), $"残り時間  {seconds / 60:00}:{seconds % 60:00}", headingStyle);
        bool online = player.IsOnlineExploration;
        if (online) GUI.Label(new Rect(120, 108, 300, 20), (player.Online.DirectConnection ? "接続先：" : "参加コード：") + player.Online.RoomCode, textStyle);

        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !player.ReloadPending && (!online || !player.Online.IsBusy);
        if (GUI.Button(new Rect(120, 128, 240, 44), "Continue")) player.ContinueRun();
        if (GUI.Button(new Rect(120, 184, 240, 44), online ? (player.Online.DirectConnection ? "接続先をコピー" : "参加コードをコピー") : "Retry"))
        {
            if (online) player.Online.CopyRoomCode();
            else player.ReloadRun(startImmediately: true);
        }
        if (GUI.Button(new Rect(120, 240, 240, 44), online ? "退出してタイトルへ" : "Home"))
        {
            if (online) _ = player.Online.LeaveAsync();
            else player.ReloadRun(startImmediately: false);
        }
        if (GUI.Button(new Rect(120, 296, 240, 44), "Option")) optionsVisible = true;
        GUI.enabled = previousEnabled;
        GUI.Label(new Rect(24, 359, 432, 26), "操作説明", headingStyle);
        string guide = "W / A / S / D：移動　マウス：視点変更\nShift：ダッシュ　Space：ジャンプ\nEsc：メニュー開閉　F2：出典表示\nF5：今回の開始地点へ戻る";
#if UNITY_EDITOR
        guide += "\n開発用：F3 雨切替／F4 時間設定";
#endif
        GUI.Label(new Rect(24, 393, 432, 130), guide, textStyle);
        GUI.Label(new Rect(24, 520, 432, 22), "メニュー中もゲームは進行します", textStyle);
        GUI.Label(new Rect(24, 548, 432, 22), "時刻 " + FormatTime() + "　天気 " + WeatherName(), textStyle);
    }

    private void DrawOptions()
    {
        GUI.Label(new Rect(24, 18, 432, 40), "オプション", resultTitleStyle);
        string displayMode = Screen.fullScreen ? "フルスクリーン" : "ウィンドウ";
        GUI.Label(new Rect(120, 64, 340, 24), $"現在：{Screen.width} × {Screen.height}（{displayMode}）", textStyle);

        if (GUI.Button(new Rect(100, 100, 280, 40), "フルHD  1920 × 1080（ウィンドウ）"))
            Screen.SetResolution(FullHdWidth, FullHdHeight, FullScreenMode.Windowed);
        if (GUI.Button(new Rect(100, 150, 280, 40), "4K  3840 × 2160（ウィンドウ）"))
            Screen.SetResolution(3840, 2160, FullScreenMode.Windowed);
        if (GUI.Button(new Rect(100, 200, 280, 40), "フルスクリーン"))
        {
            Resolution desktop = Screen.currentResolution;
            Screen.SetResolution(desktop.width, desktop.height, FullScreenMode.FullScreenWindow);
        }
        bool showIds = GUI.Toggle(new Rect(100, 254, 280, 30), ShowBuildingIds, "建物IDを表示する");
        if (showIds != ShowBuildingIds)
        {
            ShowBuildingIds = showIds;
            PlayerPrefs.SetInt("Fuchu.ShowBuildingIds", showIds ? 1 : 0);
            PlayerPrefs.Save();
        }
        if (GUI.Button(new Rect(120, 312, 240, 40), "戻る")) optionsVisible = false;
    }

    private void DrawResults()
    {
        GUI.Label(new Rect(24, 18, 432, 38), "タイムアップ", resultTitleStyle);
        GUI.Label(new Rect(32, 69, 416, 26), $"確定エリア面積  {player.TerritoryCoverage * 100.0:0.0000}%", headingStyle);
        GUI.Label(new Rect(32, 100, 416, 26), $"延床面積ポイント合計  {player.BuildingPoints:N0} pt", headingStyle);
        DrawBattleStandings(new Rect(32, 136, 416, 190));
        if (player.IsOnlineExploration)
        {
            var online = player.Online;
            GUI.Label(new Rect(32, 337, 416, 24), online.CanStartMatch
                ? "再戦の準備をすると、全員が待機画面に戻ります。"
                : "開始権限を持つ参加者が再戦を準備できます。", textStyle);
            bool available = GUI.enabled;
            GUI.enabled = available && online.CanScheduleMatch;
            if (GUI.Button(new Rect(48, 367, 384, 40), "再戦の準備")) online.RequestRematch();
            GUI.enabled = available && !online.IsBusy;
            if (GUI.Button(new Rect(48, 421, 184, 40), "対戦画面を見る")) player.ContinueRun();
            if (GUI.Button(new Rect(248, 421, 184, 40), "退出してタイトルへ")) _ = online.LeaveAsync();
            GUI.enabled = available;
            return;
        }
        GUI.Label(new Rect(32, 337, 416, 24),
            player.RunRank > 0 ? $"端末記録での順位  {player.RunRank}位" : "端末記録での順位  トップ10圏外", textStyle);
        GUI.Label(new Rect(32, 368, 416, 26), "面積ランキング　この端末の上位10件", headingStyle);

        for (int i = 0; i < player.Ranking.Count; i++)
        {
            FuchuRunRanking.Entry entry = player.Ranking[i];
            float y = 403f + i * 22f;
            string marker = player.RunRank == i + 1 ? "▶" : "";
            GUI.Label(new Rect(32, y, 70, 22), $"{marker}{i + 1}位", textStyle);
            GUI.Label(new Rect(110, y, 150, 22), $"{entry.coverage * 100.0:0.0000}%", textStyle);
            GUI.Label(new Rect(270, y, 178, 22), $"{entry.buildingPoints:N0} pt", textStyle);
        }

        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !player.ReloadPending;
        bool retry = GUI.Button(new Rect(48, 650, 184, 40), player.ReloadPending ? "読み込み中…" : "Retry");
        bool home = GUI.Button(new Rect(248, 650, 184, 40), "Home");
        GUI.enabled = previousEnabled;
        if (retry || home) player.ReloadRun(startImmediately: retry);
    }

    private struct Standing
    {
        internal int Id;
        internal string Name, ColorName;
        internal Color Color;
        internal double Coverage;
    }
    private readonly List<Standing> standings = new List<Standing>(5);

    private void ReadStandings()
    {
        standings.Clear();
        if (player.IsOnlineExploration)
        {
            var state = player.Online.DisplayState;
            if (state == null || !player.Online.TerritoryReady) return;
            int localSlot = player.Online.LocalSlot;
            for (int slot = 0; slot < state.Players.Length; slot++)
            {
                var entry = state.Players[slot];
                if (!entry.Active) continue;
                standings.Add(new Standing { Id = slot, Name = slot == localSlot ? "あなた" : entry.IsNpc ? "NPC " + (slot + 1) : "プレイヤー " + (slot + 1),
                    ColorName = FuchuOnlineConnection.DisplayColorName(slot), Color = FuchuOnlineConnection.DisplayColor(slot),
                    Coverage = entry.Shape.ClaimedSquareMeters / (System.Math.PI * 1000.0 * 1000.0) });
            }
        }
        else if (player.Battle != null)
            foreach (var entry in player.Battle.Players)
                standings.Add(new Standing { Id = entry.Id, Name = entry.Name, ColorName = entry.ColorName,
                    Color = entry.Color, Coverage = entry.Territory.Coverage });
        standings.Sort((a, b) => {
            int comparison = b.Coverage.CompareTo(a.Coverage);
            return comparison != 0 ? comparison : a.Id.CompareTo(b.Id);
        });
    }

    private void DrawBattleStandings(Rect panel)
    {
        ReadStandings();
        GUI.Box(panel, GUIContent.none, panelStyle);
        bool finished = player.RunFinished || player.IsOnlineExploration && player.Online.MatchFinished;
        GUI.Label(new Rect(panel.x + 12, panel.y + 8, panel.width - 24, 26),
            finished ? "対戦結果（確保面積順）" : "5人対戦（確保面積順）", headingStyle);
        if (standings.Count == 0)
        {
            GUI.Label(new Rect(panel.x + 12, panel.y + 42, panel.width - 24, 24), "対戦情報を同期しています…", textStyle);
            return;
        }
        double previousCoverage = -1;
        int rank = 0;
        for (int row = 0; row < standings.Count; row++)
        {
            Standing entry = standings[row];
            if (entry.Coverage != previousCoverage) rank = row + 1;
            previousCoverage = entry.Coverage;
            float y = panel.y + 42 + row * 27;
            Color previous = GUI.color;
            GUI.color = entry.Color;
            GUI.DrawTexture(new Rect(panel.x + 12, y + 4, 14, 14), whiteTexture);
            GUI.color = previous;
            GUI.Label(new Rect(panel.x + 34, y, panel.width - 46, 24),
                $"{rank}位 {entry.Name}（{entry.ColorName}）  {entry.Coverage * 100.0:0.0000}%", textStyle);
        }
    }

    private void EnsureStyles()
    {
        if (panelStyle != null) return;
        panelStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(12, 12, 10, 10) };
        headingStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        resultTitleStyle = new GUIStyle(headingStyle) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
        textStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, normal = { textColor = Color.white } };
        whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        whiteTexture.SetPixel(0, 0, Color.white);
        whiteTexture.Apply();
    }

    private void ApplyWorldLighting()
    {
        if (sun == null)
        {
            GameObject lightObject = GameObject.Find("Directional Light");
            if (lightObject != null) sun = lightObject.GetComponent<Light>();
        }

        float hour = gameMinutes / 60f;
        float daylight = Mathf.Clamp01(Mathf.Sin((hour - 6f) * Mathf.PI / 12f));
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler((hour / 24f) * 360f - 90f, 145f, 0f);
            sun.intensity = Mathf.Lerp(0.015f, 1.1f, daylight) * (raining ? 0.72f : 1f);
            sun.color = Color.Lerp(new Color(0.34f, 0.42f, 0.68f), new Color(1f, 0.91f, 0.76f), daylight);
        }

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.Lerp(new Color(0.035f, 0.045f, 0.09f), new Color(0.55f, 0.57f, 0.58f), daylight);
        RenderSettings.fog = false;
        if (runtimeSkybox != null)
        {
            Color skyTint = Color.Lerp(new Color(0.018f, 0.028f, 0.075f), Color.white, daylight);
            if (runtimeSkybox.HasProperty("_Tint")) runtimeSkybox.SetColor("_Tint", skyTint);
            if (runtimeSkybox.HasProperty("_SkyTint")) runtimeSkybox.SetColor("_SkyTint", skyTint);
            if (runtimeSkybox.HasProperty("_GroundColor")) runtimeSkybox.SetColor("_GroundColor", Color.Lerp(new Color(0.008f, 0.012f, 0.028f), new Color(0.38f, 0.36f, 0.34f), daylight));
            if (runtimeSkybox.HasProperty("_SunSize")) runtimeSkybox.SetFloat("_SunSize", Mathf.Lerp(0f, 0.04f, daylight));
            if (runtimeSkybox.HasProperty("_Exposure")) runtimeSkybox.SetFloat("_Exposure", Mathf.Lerp(0.08f, 1f, daylight));
        }
        float nightFactor = 1f - daylight;
        if (starRenderer != null) starRenderer.enabled = nightFactor > 0.42f;
        if (starMaterial != null)
        {
            float starBrightness = Mathf.SmoothStep(0.42f, 0.9f, nightFactor) * 2.2f;
            Color starColor = new Color(0.78f * starBrightness, 0.86f * starBrightness, starBrightness, 1f);
            starMaterial.SetColor("_BaseColor", starColor);
            starMaterial.SetColor("_Color", starColor);
        }
        if (cloudMaterial != null)
        {
            Color cloudColor = Color.Lerp(new Color(0.38f, 0.46f, 0.64f, 0.46f), new Color(0.92f, 0.95f, 1f, 0.62f), daylight);
            cloudMaterial.SetColor("_BaseColor", cloudColor);
            cloudMaterial.SetColor("_Color", cloudColor);
        }
    }

    private void CreateSkyObjects()
    {
        Shader particleShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (particleShader == null) particleShader = Shader.Find("Particles/Standard Unlit");
        if (particleShader == null) return;

        cloudTexture = CreateCloudTexture();
        starTexture = CreateStarTexture();
        cloudMaterial = CreateParticleMaterial("FuchuCloudMaterial", cloudTexture);
        starMaterial = CreateParticleMaterial("FuchuStarMaterial", starTexture);
        cloudParticles = CreateParticleSystem("FuchuSkyClouds", 64, out cloudRenderer);
        starParticles = CreateParticleSystem("FuchuStarField", 900, out starRenderer);
        cloudRenderer.sharedMaterial = cloudMaterial;
        starRenderer.sharedMaterial = starMaterial;
        starRenderer.enabled = false;

        System.Random random = new System.Random(2701);
        Vector3 center = transform.position;
        for (int i = 0; i < 38; i++)
        {
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            float radius = 650f + (float)random.NextDouble() * 800f;
            float altitude = 190f + (float)random.NextDouble() * 190f;
            Vector3 position = center + new Vector3(Mathf.Cos(angle) * radius, altitude, Mathf.Sin(angle) * radius);
            Vector3 wind = new Vector3(0.12f + (float)random.NextDouble() * 0.22f, 0f, -0.08f - (float)random.NextDouble() * 0.15f);
            var cloud = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = wind,
                startLifetime = 36000f,
                startSize = 250f + (float)random.NextDouble() * 260f,
                startColor = Color.white
            };
            cloudParticles.Emit(cloud, 1);
        }

        for (int i = 0; i < 620; i++)
        {
            float y = (float)random.NextDouble() * 0.96f + 0.04f;
            float horizontal = Mathf.Sqrt(1f - y * y);
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            float radius = 1550f + (float)random.NextDouble() * 350f;
            Vector3 direction = new Vector3(Mathf.Cos(angle) * horizontal, y, Mathf.Sin(angle) * horizontal);
            var star = new ParticleSystem.EmitParams
            {
                position = center + direction * radius,
                startLifetime = 36000f,
                startSize = 1.4f + (float)random.NextDouble() * 2.6f,
                startColor = Color.white
            };
            starParticles.Emit(star, 1);
        }
    }

    private ParticleSystem CreateParticleSystem(string objectName, int maximum, out ParticleSystemRenderer renderer)
    {
        var child = new GameObject(objectName);
        child.transform.SetParent(transform, false);
        ParticleSystem particleSystem = child.AddComponent<ParticleSystem>();
        particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particleSystem.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = maximum;
        main.startLifetime = 36000f;
        main.startSpeed = 0f;
        main.startSize = 1f;
        var emission = particleSystem.emission;
        emission.enabled = false;
        var shape = particleSystem.shape;
        shape.enabled = false;
        renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sortingFudge = -10f;
        particleSystem.Play();
        return particleSystem;
    }

    private static Material CreateParticleMaterial(string materialName, Texture2D texture)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        var material = new Material(shader) { name = materialName, enableInstancing = true };
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_SrcBlendAlpha")) material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        if (material.HasProperty("_DstBlendAlpha")) material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Transparent;
        return material;
    }

    private static Texture2D CreateCloudTexture()
    {
        const int width = 192, height = 96;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "FuchuProceduralCloudTexture",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)width, v = y / (float)height;
                float broad = Mathf.PerlinNoise(u * 5.2f + 3.7f, v * 3.2f + 8.1f);
                float detail = Mathf.PerlinNoise(u * 12.5f + 1.8f, v * 8.7f + 2.4f);
                float noise = broad * 0.72f + detail * 0.28f;
                float edge = Mathf.SmoothStep(0f, 0.22f, Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v)));
                float alpha = Mathf.SmoothStep(0.47f, 0.69f, noise) * edge * 0.82f;
                pixels[y * width + x] = new Color(1f, 1f, 1f, alpha);
            }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return texture;
    }

    private static Texture2D CreateStarTexture()
    {
        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "FuchuProceduralStarTexture",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - (size - 1) * 0.5f) / (size * 0.5f);
                float dy = (y - (size - 1) * 0.5f) / (size * 0.5f);
                float radius = Mathf.Sqrt(dx * dx + dy * dy);
                float core = Mathf.Exp(-radius * radius * 15f);
                float spike = Mathf.Max(Mathf.Exp(-Mathf.Abs(dx) * 28f - Mathf.Abs(dy) * 3f), Mathf.Exp(-Mathf.Abs(dy) * 28f - Mathf.Abs(dx) * 3f));
                pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(core * 0.8f + spike * 0.34f));
            }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return texture;
    }

    private void DrawRain()
    {
        Color previous = GUI.color;
        GUI.color = new Color(0.12f, 0.17f, 0.24f, 0.12f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), whiteTexture);
        GUI.color = new Color(0.72f, 0.82f, 0.94f, 0.32f);
        int count = Mathf.Clamp(Screen.width / 18, 45, 110);
        float drift = Time.time * 260f;
        for (int i = 0; i < count; i++)
        {
            float x = (i * 97.31f + drift) % Screen.width;
            float y = (i * 173.17f + drift * 1.8f) % Screen.height;
            GUI.DrawTexture(new Rect(x, y, 1.3f, 13f), whiteTexture);
        }
        GUI.color = previous;
    }

    private float NextWeatherTimer()
    {
        // Rain lasts 30–75 seconds; dry intervals last 90–210 seconds.
        return raining ? Random.Range(30f, 75f) : Random.Range(90f, 210f);
    }

    private string FormatTime()
    {
        int minutes = Mathf.FloorToInt(gameMinutes);
        return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
    }

    private string WeatherName() => raining ? "雨" : "晴れ";

    private string DayLengthText()
    {
        float seconds = SecondsPerGameDayAtNormalSpeed / timeSpeed;
        return seconds < 60f ? seconds.ToString("0") + "秒" : (seconds / 60f).ToString("0.#") + "分";
    }

    private void DrawSpeedButton(Rect rect, float value, string label)
    {
        string buttonLabel = Mathf.Approximately(timeSpeed, value) ? "▶ " + label : label;
        if (GUI.Button(rect, buttonLabel)) timeSpeed = value;
    }

    private void OnDisable()
    {
        if (cloudParticles != null) cloudParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        if (starParticles != null) starParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        if (runtimeSkybox != null)
        {
            if (RenderSettings.skybox == runtimeSkybox) RenderSettings.skybox = originalSkybox;
            Destroy(runtimeSkybox);
            runtimeSkybox = null;
        }
    }

    private void OnDestroy()
    {
        if (cloudMaterial != null) Destroy(cloudMaterial);
        if (starMaterial != null) Destroy(starMaterial);
        if (cloudTexture != null) Destroy(cloudTexture);
        if (starTexture != null) Destroy(starTexture);
        if (whiteTexture != null) Destroy(whiteTexture);
    }
}
