using System;
using System.Collections;
using UnityEngine;

/// <summary>Projection of authoritative state only; never decides territory or building ownership.</summary>
public sealed class FuchuOnlineTerritoryView : MonoBehaviour
{
    private FuchuOnlineTerritoryState state;
    private readonly FuchuWalkingTerritory[] ground = new FuchuWalkingTerritory[5];
    private FuchuTerritoryBuildings buildings;
    private FuchuOnlineRecoveryView recovery;
    private int displayedRevision = -1;
    private Coroutine refreshing;
    internal int Version => state?.Revision ?? -1;
    internal Material[] BuildingMaterials { get; private set; }
    internal int OwnerOf(string id) => state?.OwnerOf(id) ?? -1;
    internal int FacadeOwner(string name)
    {
        if (state == null) return -1;
        if (name.StartsWith("Facade_", StringComparison.Ordinal)) return OwnerOf(name.Substring(7));
        const string prefix = "FuchuFacadePreview_TargetBuilding_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return -1;
        string fragment = "bldg_" + name.Substring(prefix.Length);
        foreach (var entry in state.Catalog.Entries)
            if (entry.Id.StartsWith(fragment, StringComparison.Ordinal)) return OwnerOf(entry.Id);
        return -1;
    }
    internal void Initialize(FuchuWalkController player, FuchuOnlineTerritoryState initial, Color[] colors)
    {
        state = initial;
        var items = new GameObject("Online Recovery Items");
        items.transform.SetParent(player.transform.parent, false);
        recovery = items.AddComponent<FuchuOnlineRecoveryView>();
        recovery.Bind(initial);
        BuildingMaterials = new Material[5];
        Shader shader = Resources.Load<Shader>("FuchuTerritoryColor");
        for (int i = 0; i < ground.Length; i++)
        {
            BuildingMaterials[i] = new Material(shader) { color = colors[i], renderQueue = 2010 };
            BuildingMaterials[i].SetFloat("_ZWrite", 1f);
            BuildingMaterials[i].SetFloat("_Shade", 1f);
            var obj = new GameObject("Online Territory " + i);
            obj.transform.SetParent(player.transform.parent, false);
            ground[i] = obj.AddComponent<FuchuWalkingTerritory>();
            ground[i].Initialize(player.transform.position, player.BuildingEffects, colors[i], null, false);
        }
        buildings = new FuchuTerritoryBuildings(player.transform.parent, player.BuildingEffects, this);
    }
    internal void BindState(FuchuOnlineTerritoryState next) { state = next; recovery.Bind(next); }
    private void Update()
    {
        if (state == null) return;
        for (int i = 0; i < ground.Length; i++)
            ground[i].DrawReplicatedTrail(state.Players[i].Trail, state.Players[i].TrailGeneration);
        if (displayedRevision != state.Revision && refreshing == null)
            refreshing = StartCoroutine(RefreshGround());
        buildings?.RefreshLoadedBuildings();
    }
    private IEnumerator RefreshGround()
    {
        int revision = state.Revision;
        for (int i = 0; i < ground.Length; i++)
        {
            ground[i].ReplaceReplicatedShape(state.Players[i].Shape.CopyPaths());
            yield return null;
        }
        displayedRevision = revision;
        refreshing = null;
    }
    private void OnDestroy()
    {
        buildings?.Dispose();
        if (recovery != null) Destroy(recovery.gameObject);
        foreach (FuchuWalkingTerritory territory in ground)
            if (territory != null) Destroy(territory.gameObject);
        if (BuildingMaterials != null) foreach (Material material in BuildingMaterials) Destroy(material);
    }
}
