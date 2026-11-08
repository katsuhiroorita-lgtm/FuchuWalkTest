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

    private readonly Transform worldRoot;
    private readonly Material material;
    private readonly FuchuTerritoryShape territory;
    private readonly Dictionary<CesiumModelMetadata, LoadedModel> models = new Dictionary<CesiumModelMetadata, LoadedModel>();
    private readonly HashSet<string> capturedIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> scoredIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<MeshRenderer, Material[]> facadeMaterials = new Dictionary<MeshRenderer, Material[]>();
    private readonly Cesium3DTileset tileset;
    private float nextScan;
    private int scannedVersion;

    public double TotalPoints { get; private set; }

    public FuchuTerritoryBuildings(Transform worldRoot, Material material, FuchuTerritoryShape territory)
    {
        this.worldRoot = worldRoot;
        this.material = material;
        this.territory = territory;
        tileset = worldRoot != null ? worldRoot.GetComponentInChildren<Cesium3DTileset>() : Object.FindFirstObjectByType<Cesium3DTileset>();
    }

    public void RefreshLoadedBuildings()
    {
        if (tileset == null) return;
        bool newArea = scannedVersion != territory.Version;
        if (!newArea && Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.75f;
        scannedVersion = territory.Version;

        var unloaded = new List<CesiumModelMetadata>();
        foreach (var entry in models)
        {
            if (entry.Key != null) continue;
            entry.Value.Dispose();
            unloaded.Add(entry.Key);
        }
        foreach (CesiumModelMetadata model in unloaded) models.Remove(model);

        int previousIdCount = capturedIds.Count;
        var activeModels = tileset.GetComponentsInChildren<CesiumModelMetadata>();
        foreach (CesiumModelMetadata metadata in activeModels)
        {
            if (!models.TryGetValue(metadata, out LoadedModel model))
            {
                model = new LoadedModel();
                models.Add(metadata, model);
            }
            if (model.LastCheckedVersion == territory.Version) continue;
            model.LastCheckedVersion = territory.Version;
            if (model.Primitives.Count == 0) ReadBuildingGeometry(metadata, model);
            foreach (Building building in model.Buildings)
            {
                if (building.Captured || !territory.Contains((building.Min + building.Max) * 0.5f)) continue;
                building.Captured = true;
                model.NeedsColorUpdate = true;
                if (!string.IsNullOrEmpty(building.Id)) capturedIds.Add(building.Id);
            }
        }

        foreach (CesiumModelMetadata metadata in activeModels)
        {
            LoadedModel model = models[metadata];
            bool changed = false;
            foreach (Building building in model.Buildings)
            {
                if (!building.Captured && capturedIds.Contains(building.Id))
                {
                    building.Captured = true;
                    changed = true;
                }
                if (!building.Captured || building.Id.Length == 0 || scoredIds.Contains(building.Id)) continue;
                double points = building.GetPoints();
                if (points > 0.0 && scoredIds.Add(building.Id)) TotalPoints += points;
            }
            if (changed || model.NeedsColorUpdate)
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
        if (previousIdCount != capturedIds.Count) ColorFacadePreviews();
    }

    private void ReadBuildingGeometry(CesiumModelMetadata metadata, LoadedModel model)
    {
        if (metadata.propertyTables == null) return;
        CesiumPrimitiveFeatures[] sources = metadata.GetComponentsInChildren<CesiumPrimitiveFeatures>(true);
        bool nearArea = false;
        foreach (CesiumPrimitiveFeatures source in sources)
        {
            if (!source.TryGetComponent(out MeshRenderer renderer)) continue;
            if (!territory.Overlaps(ProjectBounds(renderer.bounds))) continue;
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
            var projected = new Vector2[vertices.Length];
            for (int i = 0; i < projected.Length; i++)
                projected[i] = Flatten(toLocal.MultiplyPoint3x4(vertices[i]));
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
        int capturedFaceCount = 0;
        foreach (Building building in primitive.Faces)
        {
            if (building != null && building.Captured) capturedFaceCount++;
        }
        if (capturedFaceCount == primitive.CapturedFaceCount) return;
        primitive.CapturedFaceCount = capturedFaceCount;
        var coloredIndices = new List<int>(capturedFaceCount * 3);
        for (int face = 0; face < primitive.Faces.Length; face++)
        {
            if (primitive.Faces[face] == null || !primitive.Faces[face].Captured) continue;
            int index = face * 3;
            coloredIndices.Add(primitive.Indices[index]);
            coloredIndices.Add(primitive.Indices[index + 1]);
            coloredIndices.Add(primitive.Indices[index + 2]);
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
            primitive.OverlayRenderer.sharedMaterial = material;
            primitive.OverlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            primitive.OverlayRenderer.enabled = primitive.Source.enabled;
        }
        primitive.OverlayMesh.SetTriangles(coloredIndices, 0);
    }

    private void ColorFacadePreviews()
    {
        if (worldRoot == null) return;
        Transform group = worldRoot.Find("FuchuFacadePreview_Radius1000m");
        if (group != null)
        {
            foreach (Transform facade in group)
            {
                const string prefix = "Facade_";
                if (facade.name.StartsWith(prefix, StringComparison.Ordinal) &&
                    capturedIds.Contains(facade.name.Substring(prefix.Length)))
                    ColorFacade(facade);
            }
        }
        foreach (Transform preview in worldRoot)
        {
            const string prefix = "FuchuFacadePreview_TargetBuilding_";
            if (!preview.name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string idPrefix = "bldg_" + preview.name.Substring(prefix.Length);
            foreach (string id in capturedIds)
            {
                if (!id.StartsWith(idPrefix, StringComparison.Ordinal)) continue;
                ColorFacade(preview);
                break;
            }
        }
    }

    private void ColorFacade(Transform facade)
    {
        foreach (MeshRenderer renderer in facade.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (facadeMaterials.ContainsKey(renderer) || renderer.name == "FuchuNightWindowQuads") continue;
            Material[] originals = renderer.sharedMaterials;
            facadeMaterials.Add(renderer, originals);
            var blue = new Material[originals.Length];
            for (int i = 0; i < blue.Length; i++) blue[i] = material;
            renderer.sharedMaterials = blue;
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
        public bool Captured;
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

        public void Expand(Vector2 point)
        {
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
        public int CapturedFaceCount;
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
        public int LastCheckedVersion;
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
