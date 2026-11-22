using System;
using System.Collections.Generic;
using CesiumForUnity;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>Colors whole PLATEAU building features without changing the meshes used for picking.</summary>
internal sealed class FuchuTerritoryBuildings : IDisposable
{
    internal const string OverlayName = "FuchuTerritoryBuildingColor";

    private readonly FuchuBattleMatch match;
    private readonly FuchuOnlineTerritoryView online;
    private int OwnershipVersion => online != null ? online.Version : match.Version;
    private Material[] OwnershipMaterials => online != null ? online.BuildingMaterials : match.BuildingMaterials;
    private readonly Transform worldRoot;
    private readonly Dictionary<CesiumModelMetadata, LoadedModel> models = new Dictionary<CesiumModelMetadata, LoadedModel>();
    // Retain values after tile unloading so ownership changes never depend on visibility or LOD.
    private readonly Dictionary<string, (Vector2 Min, Vector2 Max, double Points)> knownBuildings =
        new Dictionary<string, (Vector2, Vector2, double)>(StringComparer.Ordinal);
    private readonly Dictionary<MeshRenderer, Material[]> facadeMaterials = new Dictionary<MeshRenderer, Material[]>();
    private readonly Cesium3DTileset tileset;
    private readonly FuchuBuildingEffects effects;
    private float nextScan;
    private int scannedVersion;

    public double TotalPoints { get; private set; }
    public double StaminaCapacityPoints { get; private set; }
    private double movementBuildingPoints;
    private double toughnessBuildingPoints;
    public double MovementPoints => movementBuildingPoints / 10.0;
    public double ToughnessPoints => toughnessBuildingPoints / 10.0;

    public FuchuTerritoryBuildings(Transform worldRoot, FuchuBuildingEffects effects, FuchuBattleMatch match)
    {
        this.match = match;
        this.effects = effects;
        this.worldRoot = worldRoot;
        tileset = worldRoot != null ? worldRoot.GetComponentInChildren<Cesium3DTileset>() : Object.FindFirstObjectByType<Cesium3DTileset>();
    }

    internal FuchuTerritoryBuildings(Transform root, FuchuBuildingEffects effects, FuchuOnlineTerritoryView online)
    {
        this.online = online;
        this.effects = effects;
        worldRoot = root;
        tileset = root != null ? root.GetComponentInChildren<Cesium3DTileset>() : Object.FindFirstObjectByType<Cesium3DTileset>();
    }

    public void RefreshLoadedBuildings()
    {
        if (tileset == null)
        {
            RecalculateOwnedPoints();
            return;
        }
        bool newArea = scannedVersion != OwnershipVersion;
        if (!newArea && Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.75f;
        scannedVersion = OwnershipVersion;

        var unloaded = new List<CesiumModelMetadata>();
        foreach (var entry in models)
        {
            if (entry.Key != null) continue;
            entry.Value.Dispose();
            unloaded.Add(entry.Key);
        }
        foreach (CesiumModelMetadata model in unloaded) models.Remove(model);

        var activeModels = tileset.GetComponentsInChildren<CesiumModelMetadata>();
        foreach (CesiumModelMetadata metadata in activeModels)
        {
            if (!models.TryGetValue(metadata, out LoadedModel model))
            {
                model = new LoadedModel();
                models.Add(metadata, model);
            }
            if (model.Primitives.Count == 0)
            {
                ReadBuildingGeometry(metadata, model);
                model.LastCheckedVersion = -1;
            }
            foreach (Building building in model.Buildings)
            {
                effects.Register(building.Id, building.Min, building.Max, building.MinHeight);
                if (online != null) continue;
                if (building.Id.Length > 0 && !knownBuildings.ContainsKey(building.Id))
                    knownBuildings.Add(building.Id, (building.Min, building.Max, building.GetPoints()));
            }
            if (model.LastCheckedVersion == OwnershipVersion) continue;
            model.LastCheckedVersion = OwnershipVersion;
            foreach (Building building in model.Buildings)
            {
                int owner = online != null ? online.OwnerOf(building.Id) : match.BuildingOwner(building.Id, building.Min, building.Max);
                if (building.Owner != owner) model.NeedsColorUpdate = true;
                building.Owner = owner;
                if (online == null && owner >= 0) effects.Capture(building.Id);
            }
        }

        foreach (CesiumModelMetadata metadata in activeModels)
        {
            LoadedModel model = models[metadata];
            if (model.NeedsColorUpdate)
            {
                model.NeedsColorUpdate = false;
                foreach (Primitive primitive in model.Primitives) ApplyColor(primitive);
            }
            foreach (Primitive primitive in model.Primitives)
            {
                if (primitive.OverlayRenderer != null && primitive.Source != null)
                    primitive.OverlayRenderer.enabled = primitive.Source.enabled;
            }
        }
        RecalculateOwnedPoints();
        if (newArea) ColorFacadePreviews();
    }

    private void RecalculateOwnedPoints()
    {
        if (online != null) return;
        TotalPoints = 0.0;
        StaminaCapacityPoints = 0.0;
        movementBuildingPoints = 0.0;
        toughnessBuildingPoints = 0.0;
        foreach (FuchuBattleMatch.Participant participant in match.Players)
        {
            participant.MovementPoints = 0.0;
            participant.StaminaCapacityPoints = 0.0;
        }
        foreach (var entry in knownBuildings)
        {
            var building = entry.Value;
            int owner = match.BuildingOwner(entry.Key, building.Min, building.Max);
            if (owner < 0) continue;
            FuchuBuildingEffects.EffectKind effect = effects.GetEffect(entry.Key);
            if (effect == FuchuBuildingEffects.EffectKind.Movement)
                match.Players[owner].MovementPoints += building.Points / 10.0;
            else if (effect == FuchuBuildingEffects.EffectKind.StaminaCapacity)
                match.Players[owner].StaminaCapacityPoints += building.Points;
            if (owner != 0) continue;
            TotalPoints += building.Points;
            switch (effects.GetEffect(entry.Key))
            {
                case FuchuBuildingEffects.EffectKind.Movement:
                    movementBuildingPoints += building.Points;
                    break;
                case FuchuBuildingEffects.EffectKind.Toughness:
                    toughnessBuildingPoints += building.Points;
                    break;
                case FuchuBuildingEffects.EffectKind.StaminaCapacity:
                    StaminaCapacityPoints += building.Points;
                    break;
            }
        }
    }

    private void ReadBuildingGeometry(CesiumModelMetadata metadata, LoadedModel model)
    {
        if (metadata.propertyTables == null) return;
        CesiumPrimitiveFeatures[] sources = metadata.GetComponentsInChildren<CesiumPrimitiveFeatures>(true);
        bool nearArea = false;
        foreach (CesiumPrimitiveFeatures source in sources)
        {
            if (!source.TryGetComponent(out MeshRenderer renderer)) continue;
            if (!effects.OverlapsPlayableArea(ProjectBounds(renderer.bounds))) continue;
            nearArea = true;
            break;
        }
        if (!nearArea) return;

        var buildingsByFeature = new Dictionary<(long Table, long Feature), Building>();
        var buildingsById = new Dictionary<string, Building>(StringComparer.Ordinal);
        foreach (CesiumPrimitiveFeatures source in sources)
        {
            CesiumFeatureIdSet[] sets = source.featureIdSets;
            if (sets == null || sets.Length == 0 || sets[0].type != CesiumFeatureIdSetType.Attribute ||
                !source.TryGetComponent(out MeshFilter filter) || !source.TryGetComponent(out MeshRenderer renderer)) continue;
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || !mesh.isReadable) continue;
            CesiumFeatureIdSet featureSet = sets[0];
            long tableIndex = featureSet.propertyTableIndex;
            if (tableIndex < 0 || tableIndex >= metadata.propertyTables.Length) continue;
            CesiumPropertyTable table = metadata.propertyTables[tableIndex];
            if (table == null || table.status != CesiumPropertyTableStatus.Valid) continue;
            table.properties.TryGetValue("gml_id", out CesiumPropertyTableProperty idProperty);

            int[] indices = mesh.triangles;
            Vector3[] vertices = mesh.vertices;
            var faces = new Building[indices.Length / 3];
            Matrix4x4 toLocal = filter.transform.localToWorldMatrix;
            if (worldRoot != null) toLocal = worldRoot.worldToLocalMatrix * toLocal;
            var projected = new Vector3[vertices.Length];
            for (int i = 0; i < projected.Length; i++)
                projected[i] = toLocal.MultiplyPoint3x4(vertices[i]);
            var featureIds = new Dictionary<int, long>();

            for (int i = 0; i < indices.Length; i += 3)
            {
                int vertex = indices[i];
                if (!featureIds.TryGetValue(vertex, out long id))
                {
                    id = featureSet.GetFeatureIdForVertex(vertex);
                    featureIds.Add(vertex, id);
                }
                if (id < 0 || id == featureSet.nullFeatureId || id >= table.count) continue;
                if (!buildingsByFeature.TryGetValue((tableIndex, id), out Building building))
                {
                    string buildingId = idProperty?.GetValue(id)?.GetString("") ?? "";
                    if (!buildingsById.TryGetValue(buildingId, out building))
                    {
                        building = new Building(buildingId, table, id);
                        if (buildingId.Length > 0) buildingsById.Add(buildingId, building);
                        model.Buildings.Add(building);
                    }
                    buildingsByFeature.Add((tableIndex, id), building);
                }
                building.Expand(projected[vertex]);
                building.Expand(projected[indices[i + 1]]);
                building.Expand(projected[indices[i + 2]]);
                faces[i / 3] = building;
            }
            model.Primitives.Add(new Primitive(renderer, mesh, indices, faces));
        }
    }

    private void ApplyColor(Primitive primitive)
    {
        if (primitive.Source == null) return;
        var coloredIndices = new List<int>[FuchuBattleMatch.PlayerCount];
        for (int i = 0; i < coloredIndices.Length; i++) coloredIndices[i] = new List<int>();
        for (int face = 0; face < primitive.Faces.Length; face++)
        {
            Building building = primitive.Faces[face];
            if (building == null || building.Owner < 0) continue;
            int index = face * 3;
            coloredIndices[building.Owner].Add(primitive.Indices[index]);
            coloredIndices[building.Owner].Add(primitive.Indices[index + 1]);
            coloredIndices[building.Owner].Add(primitive.Indices[index + 2]);
        }

        if (primitive.OverlayMesh == null)
        {
            var obj = new GameObject(OverlayName) { layer = primitive.Source.gameObject.layer };
            obj.transform.SetParent(primitive.Source.transform, false);
            var mesh = new Mesh { name = OverlayName, indexFormat = primitive.Mesh.indexFormat };
            mesh.vertices = primitive.Mesh.vertices;
            mesh.normals = primitive.Mesh.normals;
            primitive.OverlayMesh = mesh;
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            primitive.OverlayRenderer = obj.AddComponent<MeshRenderer>();
            primitive.OverlayRenderer.sharedMaterials = OwnershipMaterials;
            primitive.OverlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            primitive.OverlayRenderer.enabled = primitive.Source.enabled;
        }
        primitive.OverlayMesh.subMeshCount = coloredIndices.Length;
        for (int i = 0; i < coloredIndices.Length; i++) primitive.OverlayMesh.SetTriangles(coloredIndices[i], i);
    }

    private void ColorFacadePreviews()
    {
        foreach (var entry in facadeMaterials)
            if (entry.Key != null) entry.Key.sharedMaterials = entry.Value;
        if (worldRoot == null) return;
        Transform group = worldRoot.Find("FuchuFacadePreview_Radius1000m");
        if (group != null)
            foreach (Transform facade in group)
                if (facade.name.StartsWith("Facade_", StringComparison.Ordinal))
                    ColorFacade(facade);
        foreach (Transform preview in worldRoot)
            if (preview.name.StartsWith("FuchuFacadePreview_TargetBuilding_", StringComparison.Ordinal))
                ColorFacade(preview);
    }

    private void ColorFacade(Transform facade)
    {
        foreach (MeshRenderer renderer in facade.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer.name == "FuchuNightWindowQuads") continue;
            Rect bounds = ProjectBounds(renderer.bounds);
            int owner = online != null ? online.FacadeOwner(facade.name) : match.OwnerAt(bounds.center);
            if (owner < 0) continue;
            if (!facadeMaterials.ContainsKey(renderer)) facadeMaterials.Add(renderer, renderer.sharedMaterials);
            var colors = new Material[facadeMaterials[renderer].Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = OwnershipMaterials[owner];
            renderer.sharedMaterials = colors;
        }
    }

    private Rect ProjectBounds(Bounds bounds)
    {
        Vector3 center = bounds.center;
        Vector3 extent = bounds.extents;
        Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = center + new Vector3((i & 1) == 0 ? -extent.x : extent.x,
                (i & 2) == 0 ? -extent.y : extent.y, (i & 4) == 0 ? -extent.z : extent.z);
            Vector2 point = Flatten(worldRoot != null ? worldRoot.InverseTransformPoint(corner) : corner);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static Vector2 Flatten(Vector3 point) => new Vector2(point.x, point.z);

    public void Dispose()
    {
        foreach (LoadedModel model in models.Values) model.Dispose();
        foreach (var entry in facadeMaterials)
        {
            if (entry.Key != null) entry.Key.sharedMaterials = entry.Value;
        }
    }

    private sealed class Building
    {
        public readonly string Id;
        public Vector2 Min = Vector2.positiveInfinity;
        public Vector2 Max = Vector2.negativeInfinity;
        public float MinHeight = float.PositiveInfinity;
        public int Owner = -1;
        private readonly CesiumPropertyTable table;
        private readonly long featureId;
        private double? points;

        public Building(string id, CesiumPropertyTable table, long featureId)
        {
            Id = id;
            this.table = table;
            this.featureId = featureId;
        }

        public double GetPoints()
        {
            if (!points.HasValue)
            {
                double area = FuchuBuildingIdPicker.EstimateFloorArea(table.GetMetadataValuesForFeature(featureId), out _, out _);
                points = FuchuBuildingIdPicker.FloorAreaPoints(area);
            }
            return points.Value;
        }

        public void Expand(Vector3 position)
        {
            Vector2 point = Flatten(position);
            MinHeight = Mathf.Min(MinHeight, position.y);
            Min = Vector2.Min(Min, point);
            Max = Vector2.Max(Max, point);
        }
    }

    private sealed class Primitive
    {
        public readonly MeshRenderer Source;
        public readonly Mesh Mesh;
        public readonly int[] Indices;
        public readonly Building[] Faces;
        public Mesh OverlayMesh;
        public MeshRenderer OverlayRenderer;

        public Primitive(MeshRenderer source, Mesh mesh, int[] indices, Building[] faces)
        {
            Source = source;
            Mesh = mesh;
            Indices = indices;
            Faces = faces;
        }
    }

    private sealed class LoadedModel : IDisposable
    {
        public int LastCheckedVersion = -1;
        public bool NeedsColorUpdate;
        public readonly List<Building> Buildings = new List<Building>();
        public readonly List<Primitive> Primitives = new List<Primitive>();

        public void Dispose()
        {
            foreach (Primitive primitive in Primitives)
            {
                if (primitive.OverlayRenderer != null) Object.Destroy(primitive.OverlayRenderer.gameObject);
                if (primitive.OverlayMesh != null) Object.Destroy(primitive.OverlayMesh);
            }
        }
    }
}
