using System.Collections.Generic;
using UnityEngine;

/// <summary>Maintains 180 small, 90 medium, and 30 large recovery cubes on the local roads.</summary>
[DefaultExecutionOrder(100)]
public sealed class FuchuStaminaItems : MonoBehaviour
{
    private readonly List<Vector3> locations = new List<Vector3>();
    private readonly List<Item> items = new List<Item>();
    private FuchuWalkController player;
    private CharacterController character;
    private Material material;
    private Vector3 previousCenter;
    private bool wasPlaying;

    internal void Initialize(FuchuWalkController controller, CharacterController playerCharacter)
    {
        player = controller;
        character = playerCharacter;
        Transform road = transform.parent.Find("FuchuLocalRoads_1000m");
        if (road == null || !road.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
        {
            Debug.LogError("スタミナ回復アイテムを配置する道路が見つかりません。", this);
            return;
        }

        // Boundary segments are arranged evenly around the playable circle.
        Transform boundary = transform.parent.Find("FuchuWalkCircularBoundary_1km");
        if (boundary == null || boundary.childCount == 0)
        {
            Debug.LogError("スタミナ回復アイテムの配置範囲が見つかりません。", this);
            return;
        }
        Vector3 center = Vector3.zero;
        foreach (Transform segment in boundary) center += segment.position;
        center /= boundary.childCount;

        Mesh mesh = filter.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        var surfaces = new List<(Vector3 A, Vector3 B, Vector3 C, float TotalArea)>();
        float totalArea = 0f;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = road.TransformPoint(vertices[triangles[i]]);
            Vector3 b = road.TransformPoint(vertices[triangles[i + 1]]);
            Vector3 c = road.TransformPoint(vertices[triangles[i + 2]]);
            Vector3 cross = Vector3.Cross(b - a, c - a);
            if (cross.magnitude < 0.01f || Vector3.Angle(cross.normalized, transform.up) > character.slopeLimit) continue;
            totalArea += cross.magnitude * 0.5f;
            surfaces.Add((a, b, c, totalArea));
        }
        if (surfaces.Count == 0) return;

        // Sample proportional to road area, then reject obstructed points and the circular boundary.
        for (int attempt = 0; attempt < 30000 && locations.Count < 2000; attempt++)
        {
            float targetArea = Random.value * totalArea;
            int low = 0;
            int high = surfaces.Count - 1;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (surfaces[middle].TotalArea < targetArea) low = middle + 1;
                else high = middle;
            }
            var surface = surfaces[low];
            float u = Random.value;
            float v = Random.value;
            if (u + v > 1f) { u = 1f - u; v = 1f - v; }
            Vector3 point = surface.A + (surface.B - surface.A) * u + (surface.C - surface.A) * v;
            Vector3 offset = Vector3.ProjectOnPlane(point - center, transform.up);
            if (offset.sqrMagnitude > 990f * 990f || !IsClear(point)) continue;
            locations.Add(point);
        }

        material = new Material(Resources.Load<Shader>("FuchuTerritoryColor"));
        material.color = new Color(0.12f, 0.95f, 0.24f);
        material.renderQueue = 2000;
        material.SetFloat("_ZWrite", 1f);
        material.SetFloat("_Shade", 1f);
        for (int i = 0; i < 300; i++)
        {
            double fraction = i < 180 ? 0.1 : i < 270 ? 0.5 : 1.0;
            float size = i < 180 ? 0.45f : i < 270 ? 0.8f : 1.2f;
            var item = new Item { Fraction = fraction, Size = size };
            if (!TryFindLocation(item, out Vector3 point))
            {
                Debug.LogError($"スタミナ回復アイテムの配置場所が不足しています（{items.Count}/300）。", this);
                break;
            }
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"Stamina {fraction:P0}";
            cube.transform.SetParent(transform, false);
            cube.transform.localScale = Vector3.one * size;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            item.Collider = cube.GetComponent<BoxCollider>();
            item.Collider.isTrigger = true;
            cube.transform.position = point + transform.up * (size * 0.5f + 0.15f);
            items.Add(item);
        }
    }

    private bool IsClear(Vector3 point)
    {
        Vector3 halfExtents = new Vector3(0.8f, character.height * 0.5f, 0.8f);
        return !Physics.CheckBox(point + transform.up * (halfExtents.y + 0.15f), halfExtents,
            transform.rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }

    private bool TryFindLocation(Item item, out Vector3 point)
    {
        int start = Random.Range(0, locations.Count);
        for (int i = 0; i < locations.Count; i++)
        {
            point = locations[(start + i) % locations.Count];
            if ((point - player.transform.position).sqrMagnitude < 10f * 10f) continue;
            if (item.Collider != null && (point - item.Collider.transform.position).sqrMagnitude < 10f * 10f) continue;
            bool occupied = false;
            foreach (Item other in items)
            {
                if (other == item || other.Collider == null) continue;
                if ((point - other.Collider.transform.position).sqrMagnitude < 4f * 4f)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied && IsClear(point)) return true;
        }
        point = default;
        return false;
    }

    private void Update()
    {
        if (player == null || !player.IsPlaying)
        {
            wasPlaying = false;
            return;
        }
        Bounds playerBounds = character.bounds;
        Vector3 center = playerBounds.center;
        // Ignore teleports (F5/car collisions), while checking the whole movement segment during sprinting.
        if (!wasPlaying || (center - previousCenter).sqrMagnitude > 100f) previousCenter = center;
        Vector3 movement = center - previousCenter;
        foreach (Item item in items)
        {
            if (!item.Collider.enabled)
            {
                if (TryFindLocation(item, out Vector3 point))
                {
                    item.Collider.transform.position = point + transform.up * (item.Size * 0.5f + 0.15f);
                    item.Collider.enabled = true;
                    item.Collider.GetComponent<Renderer>().enabled = true;
                }
                continue;
            }
            Bounds contact = new Bounds(item.Collider.transform.position, Vector3.one * item.Size);
            contact.Expand(playerBounds.size);
            bool touched = contact.Contains(center) || contact.Contains(previousCenter);
            if (!touched && movement.sqrMagnitude > 0f)
                touched = contact.IntersectRay(new Ray(previousCenter, movement.normalized), out float distance) && distance <= movement.magnitude;
            if (!touched) continue;
            player.RecoverStamina(item.Fraction);
            item.Collider.enabled = false;
            item.Collider.GetComponent<Renderer>().enabled = false;
        }
        previousCenter = center;
        wasPlaying = true;
    }

    private void OnDestroy()
    {
        if (material != null) Destroy(material);
    }

    private sealed class Item
    {
        public double Fraction;
        public float Size;
        public BoxCollider Collider;
    }
}
