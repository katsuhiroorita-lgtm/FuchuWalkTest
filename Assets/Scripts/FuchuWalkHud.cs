using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>Control guide, accelerated day/night cycle, and simple prototype weather.</summary>
public sealed class FuchuWalkHud : MonoBehaviour
{
    private FuchuWalkController player;
    private bool visible = true;
    private bool creditsVisible;
    private bool timeMenuVisible;
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
    private string startMenuMessage = "";

    public bool TimeMenuVisible => timeMenuVisible;
    public float NightFactor
    {
        get
        {
            float hour = gameMinutes / 60f;
            return 1f - Mathf.Clamp01(Mathf.Sin((hour - 6f) * Mathf.PI / 12f));
        }
    }

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
        if (keyboard != null)
        {
            if (keyboard.f1Key.wasPressedThisFrame) visible = !visible;
            if (keyboard.f2Key.wasPressedThisFrame) creditsVisible = !creditsVisible;
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
        }

        // 1440 in-game minutes over 1800 real seconds = 0.8 game minutes per second at 1x.
        gameMinutes = (gameMinutes + Time.deltaTime * 0.8f * timeSpeed) % 1440f;
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
        if (player != null && !player.IsPlaying)
        {
            DrawMenuScreen();
            return;
        }
        if (raining) DrawRain();
        if (player != null) DrawPlayerStatus();
        if (!visible) return;

        GUI.Box(new Rect(16, 16, 366, 178), GUIContent.none, panelStyle);
        GUI.Label(new Rect(30, 24, 330, 28), "府中まち歩き Prototype", headingStyle);
        GUI.Label(new Rect(30, 56, 340, 130),
            "W / A / S / D：移動　マウス：視点変更\nShift：走る　Space：ジャンプ\nEsc：解除　F1：案内　F2：出典\nF3：雨切替　F4：時間設定　F5：開始地点へ戻る\n時刻 " + FormatTime() + "　天気 " + WeatherName() + "\n1日 " + DayLengthText(),
            textStyle);

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

    private void DrawPlayerStatus()
    {
        Rect panel = new Rect(16f, Screen.height - 153f, 288f, 137f);
        GUI.Box(panel, GUIContent.none, panelStyle);
        int seconds = (int)System.Math.Ceiling(player.RemainingRunSeconds);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 7f, 264f, 26f),
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
            $"エリア内の建物ポイント  {player.BuildingPoints:N0} pt", textStyle);
    }

    private void DrawMenuScreen()
    {
        int previousDepth = GUI.depth;
        Color previousColor = GUI.color;
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.depth = -100;
        GUI.color = new Color(0.035f, 0.055f, 0.09f, 0.94f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), whiteTexture);
        GUI.color = previousColor;

        const float width = 480f;
        float height = player.RunFinished ? 510f : 330f;
        float scale = Mathf.Min(1f, Mathf.Min((Screen.width - 20f) / width, (Screen.height - 20f) / height));
        if (scale <= 0f)
        {
            GUI.depth = previousDepth;
            return;
        }
        var position = new Vector3((Screen.width - width * scale) * 0.5f, (Screen.height - height * scale) * 0.5f, 0f);
        GUI.matrix = Matrix4x4.TRS(position, Quaternion.identity, new Vector3(scale, scale, 1f));
        GUI.Box(new Rect(0, 0, width, height), GUIContent.none, panelStyle);
        if (player.RunFinished)
            DrawResults();
        else
            DrawStartMenu();
        GUI.matrix = previousMatrix;
        GUI.depth = previousDepth;
    }

    private void DrawStartMenu()
    {
        GUI.Label(new Rect(24, 24, 432, 40), "府中まち歩き", resultTitleStyle);
        if (GUI.Button(new Rect(120, 89, 240, 44), "Game Start")) player.BeginRun();
        if (GUI.Button(new Rect(120, 145, 240, 44), "Option")) startMenuMessage = "Optionは準備中です。";
        if (GUI.Button(new Rect(120, 201, 240, 44), "Exit")) startMenuMessage = "Exitは準備中です。";
        GUI.Label(new Rect(120, 263, 240, 32), startMenuMessage, textStyle);
    }

    private void DrawResults()
    {
        GUI.Label(new Rect(24, 18, 432, 38), "タイムアップ", resultTitleStyle);
        GUI.Label(new Rect(32, 69, 416, 26), $"確定エリア面積  {player.TerritoryCoverage * 100.0:0.0000}%", headingStyle);
        GUI.Label(new Rect(32, 100, 416, 26), $"延床面積ポイント合計  {player.BuildingPoints:N0} pt", headingStyle);
        GUI.Label(new Rect(32, 134, 416, 24),
            player.RunRank > 0 ? $"今回の順位  {player.RunRank}位" : "今回の順位  トップ10圏外", textStyle);
        GUI.Label(new Rect(32, 173, 416, 26), "面積ランキング　この端末の上位10件", headingStyle);

        for (int i = 0; i < player.Ranking.Count; i++)
        {
            FuchuRunRanking.Entry entry = player.Ranking[i];
            float y = 208f + i * 22f;
            string marker = player.RunRank == i + 1 ? "▶" : "";
            GUI.Label(new Rect(32, y, 70, 22), $"{marker}{i + 1}位", textStyle);
            GUI.Label(new Rect(110, y, 150, 22), $"{entry.coverage * 100.0:0.0000}%", textStyle);
            GUI.Label(new Rect(270, y, 178, 22), $"{entry.buildingPoints:N0} pt", textStyle);
        }

        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !player.ReloadPending;
        bool retry = GUI.Button(new Rect(48, 450, 184, 40), player.ReloadPending ? "読み込み中…" : "Retry");
        bool home = GUI.Button(new Rect(248, 450, 184, 40), "Home");
        GUI.enabled = previousEnabled;
        if (retry || home) player.ReloadAfterResult(startImmediately: retry);
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
        float minutes = 30f / timeSpeed;
        return minutes < 1f ? (minutes * 60f).ToString("0") + "秒" : minutes.ToString("0.#") + "分";
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
