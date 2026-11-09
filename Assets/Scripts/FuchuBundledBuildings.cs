using System;
using System.IO;
using CesiumForUnity;
using UnityEngine;

[RequireComponent(typeof(Cesium3DTileset))]
public sealed class FuchuBundledBuildings : MonoBehaviour
{
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private void Awake()
    {
        var tileset = GetComponent<Cesium3DTileset>();
        string tilesetPath = Path.Combine(Application.streamingAssetsPath, "FuchuBuildings", "tileset.json");
        if (!File.Exists(tilesetPath))
        {
            tileset.enabled = false;
            Debug.LogError($"同梱の建物データが見つかりません: {tilesetPath}", this);
            return;
        }

        tileset.url = new Uri(tilesetPath).AbsoluteUri;
    }
#endif
}
