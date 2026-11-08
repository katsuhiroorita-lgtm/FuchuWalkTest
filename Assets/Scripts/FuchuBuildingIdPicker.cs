using CesiumForUnity;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Lets the player inspect metadata for the building under the screen reticle.</summary>
public sealed class FuchuBuildingIdPicker : MonoBehaviour
{
    [SerializeField] private Camera targetCamera;
    [SerializeField] private float maxDistance = 2000f;
    [SerializeField] private float targetScanInterval = 0.12f;

    private float nextTargetScanTime;
    private FuchuWalkController player;
    private string currentFeatureKey = "";
    private string status = "中央の照準を建物に合わせてください";
    private string selectedInfo = "";
    private GUIStyle panelStyle;
    private GUIStyle textStyle;
    private GUIStyle reticleStyle;
    private GUIStyle reticleShadowStyle;
    private GUIStyle smallTextStyle;

    private void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera != null) player = targetCamera.GetComponentInParent<FuchuWalkController>();
    }

    private void Update()
    {
        if (Time.unscaledTime < nextTargetScanTime) return;
        nextTargetScanTime = Time.unscaledTime + Mathf.Max(0.05f, targetScanInterval);
        ScanTargetBuilding();
    }

    private bool TryRaycastPastPlayer(Ray ray, out RaycastHit hit)
    {
        if (!Physics.Raycast(ray, out hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return false;
        if (player == null || hit.transform != player.transform)
            return true;

        float nearestDistance = float.PositiveInfinity;
        foreach (RaycastHit candidate in Physics.RaycastAll(ray, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (candidate.transform == player.transform || candidate.distance >= nearestDistance)
                continue;
            hit = candidate;
            nearestDistance = candidate.distance;
        }
        return !float.IsPositiveInfinity(nearestDistance);
    }

    private void ScanTargetBuilding()
    {
        if (targetCamera == null)
        {
            ClearTarget("Main Cameraが見つかりません");
            return;
        }

        Ray ray = targetCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (!TryRaycastPastPlayer(ray, out RaycastHit hit))
        {
            ClearTarget("中央の照準を建物に合わせてください");
            return;
        }

        CesiumPrimitiveFeatures primitiveFeatures = hit.transform.GetComponent<CesiumPrimitiveFeatures>();
        MeshFilter meshFilter = hit.transform.GetComponent<MeshFilter>();
        if (primitiveFeatures == null || meshFilter == null || meshFilter.sharedMesh == null || hit.triangleIndex < 0)
        {
            ClearTarget("この場所の建物情報はありません");
            return;
        }

        CesiumFeatureIdSet[] featureIdSets = primitiveFeatures.featureIdSets;
        if (featureIdSets == null || featureIdSets.Length == 0 || featureIdSets[0].type != CesiumFeatureIdSetType.Attribute)
        {
            ClearTarget("この建物の情報を取得できません");
            return;
        }

        long featureId = primitiveFeatures.GetFeatureIdFromRaycastHit(hit);
        if (featureId < 0)
        {
            ClearTarget("この建物の情報を取得できません");
            return;
        }

        CesiumModelMetadata modelMetadata = hit.transform.GetComponentInParent<CesiumModelMetadata>();
        long tableIndex = featureIdSets[0].propertyTableIndex;
        if (modelMetadata == null || modelMetadata.propertyTables == null || tableIndex < 0 || tableIndex >= modelMetadata.propertyTables.Length)
        {
            ClearTarget("建物IDの詳細情報を読み込めません");
            return;
        }

        CesiumPropertyTable table = modelMetadata.propertyTables[tableIndex];
        if (table == null || table.status != CesiumPropertyTableStatus.Valid || featureId >= table.count)
        {
            ClearTarget("建物IDの詳細情報を読み込めません");
            return;
        }

        string featureKey = primitiveFeatures.GetInstanceID() + ":" + tableIndex + ":" + featureId;
        if (currentFeatureKey == featureKey)
        {
            status = "照準中";
            return;
        }

        Dictionary<string, CesiumMetadataValue> values = table.GetMetadataValuesForFeature(featureId);
        string buildingId = ReadString(values, "gml_id", "");
        string usage = ReadString(values, "bldg:usage", "");
        double height = ReadNumber(values, "bldg:measuredHeight", -1.0);
        int floors = ReadInteger(values, "bldg:storeysAboveGround", -1);
        double footprintArea = ReadNumberEndingWith(values, "buildingRoofEdgeArea", -1.0);

        if (string.IsNullOrWhiteSpace(buildingId)) buildingId = "（IDなし）";
        if (string.IsNullOrWhiteSpace(usage)) usage = "（用途情報なし）";
        string usageText = UsageName(usage);
        string heightText = height >= 0.0 ? height.ToString("0.##") + " m" : "（不明）";
        string floorText = floors >= 0 && floors != 9999 ? floors.ToString() + " 階" : "（不明）";

        string nextInfo = "建物ID: " + buildingId + "\n用途: " + usageText + "\n高さ: " + heightText + "\n地上階数: " + floorText;
        bool canEstimateFloorArea = footprintArea > 0.0 && floors > 0 && floors != 9999;
        double floorArea = footprintArea * floors;
        if (canEstimateFloorArea)
        {
            double displayedArea = System.Math.Round(floorArea, System.MidpointRounding.AwayFromZero);
            nextInfo += "\n建築面積: 約" + footprintArea.ToString("N1") + " m²";
            nextInfo += $"\n延床面積の目安: 約{displayedArea:N0} m²";
            nextInfo += $"\n延床面積ポイント: {displayedArea * 10.0:N0} pt";
        }
        if (IsResidentialUsage(usage))
        {
            if (canEstimateFloorArea)
            {
                double residentialFloorArea = floorArea * ResidentialShare(usage);
                double areaPerPerson = NormalizeUsageCode(usage) == "412" ? 25.0 : 30.0;
                int estimatedResidents = Mathf.Max(1, Mathf.RoundToInt((float)(residentialFloorArea / areaPerPerson)));
                nextInfo += "\n居住人数の目安: 約" + estimatedResidents + " 人*";
            }
            else
            {
                nextInfo += "\n居住人数の目安: 面積または階数情報がないため算出できません";
            }
        }

        currentFeatureKey = featureKey;
        selectedInfo = nextInfo;
        status = "照準中";
    }

    private void ClearTarget(string message)
    {
        currentFeatureKey = "";
        selectedInfo = "";
        status = message;
    }

    private void OnGUI()
    {
        EnsureStyles();
        const float width = 470f;
        float panelHeight = string.IsNullOrEmpty(selectedInfo) ? 82f : 267f;
        float left = Screen.width - width - 16f;
        GUI.Box(new Rect(left, 16f, width, panelHeight), GUIContent.none, panelStyle);
        GUI.Label(new Rect(left + 14f, 22f, width - 28f, 24f), "建物情報（中央の照準で自動表示）", textStyle);
        GUI.Label(new Rect(left + 14f, 48f, width - 28f, 28f), status, textStyle);

        if (!string.IsNullOrEmpty(selectedInfo))
        {
            GUI.Label(new Rect(left + 14f, 78f, width - 28f, panelHeight - 88f), selectedInfo, textStyle);
            GUI.Label(new Rect(left + 14f, panelHeight - 5f, width - 28f, 18f), "*居住人数は面積と階数からの概算です", smallTextStyle);
        }

        float centerX = Screen.width * 0.5f;
        float centerY = Screen.height * 0.5f;
        GUI.Label(new Rect(centerX - 14f + 1f, centerY - 20f + 1f, 28f, 40f), "+", reticleShadowStyle);
        GUI.Label(new Rect(centerX - 14f, centerY - 20f, 28f, 40f), "+", reticleStyle);
    }

    private void EnsureStyles()
    {
        if (panelStyle != null) return;
        panelStyle = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(14, 14, 10, 10),
            normal = { background = MakePanelTexture() }
        };
        textStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            wordWrap = true,
            normal = { textColor = Color.white }
        };
        reticleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 26,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        reticleShadowStyle = new GUIStyle(reticleStyle) { normal = { textColor = Color.black } };
        smallTextStyle = new GUIStyle(textStyle) { fontSize = 11, normal = { textColor = new Color(0.82f, 0.86f, 0.88f) } };
    }

    private static Texture2D MakePanelTexture()
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.08f, 0.88f));
        texture.Apply();
        return texture;
    }

    private static string ReadString(Dictionary<string, CesiumMetadataValue> values, string key, string fallback)
    {
        return values.TryGetValue(key, out CesiumMetadataValue value) && value != null ? value.GetString(fallback) : fallback;
    }

    private static double ReadNumber(Dictionary<string, CesiumMetadataValue> values, string key, double fallback)
    {
        return values.TryGetValue(key, out CesiumMetadataValue value) && value != null ? value.GetDouble(fallback) : fallback;
    }

    private static int ReadInteger(Dictionary<string, CesiumMetadataValue> values, string key, int fallback)
    {
        return values.TryGetValue(key, out CesiumMetadataValue value) && value != null ? value.GetInt32(fallback) : fallback;
    }

    private static double ReadNumberEndingWith(Dictionary<string, CesiumMetadataValue> values, string suffix, double fallback)
    {
        foreach (KeyValuePair<string, CesiumMetadataValue> pair in values)
        {
            if (pair.Key.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase) && pair.Value != null)
                return pair.Value.GetDouble(fallback);
        }
        return fallback;
    }

    private static bool IsResidentialUsage(string usage)
    {
        string code = NormalizeUsageCode(usage);
        return code == "411" || code == "412" || code == "413" || code == "415";
    }

    private static double ResidentialShare(string usage)
    {
        string code = NormalizeUsageCode(usage);
        if (code == "413") return 0.65;
        if (code == "415") return 0.50;
        return 1.0;
    }

    private static string NormalizeUsageCode(string usage)
    {
        string normalized = (usage ?? "").Trim();
        switch (normalized)
        {
            // Cesium metadata may contain the Japanese enumeration label instead of its code.
            case "住宅": return "411";
            case "共同住宅": return "412";
            case "店舗等併用住宅": return "413";
            case "作業所併用住宅": return "415";
            default: return normalized;
        }
    }

    private static string UsageName(string usage)
    {
        switch (usage)
        {
            case "401": return "401（業務施設）";
            case "402": return "402（商業施設）";
            case "403": return "403（宿泊施設）";
            case "411": return "411（住宅）";
            case "412": return "412（共同住宅）";
            case "413": return "413（店舗等併用住宅）";
            case "414": return "414（店舗等併用共同住宅）";
            case "415": return "415（作業所併用住宅）";
            case "421": return "421（官公庁施設）";
            case "422": return "422（文教厚生施設）";
            case "431": return "431（運輸倉庫施設）";
            case "441": return "441（工場）";
            case "451": return "451（農林漁業用施設）";
            case "452": return "452（供給処理施設）";
            case "453": return "453（防衛施設）";
            case "454": return "454（その他）";
            case "461": return "461（不明）";
            default: return usage;
        }
    }
}
