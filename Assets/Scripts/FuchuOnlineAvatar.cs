using System;
using UnityEngine;

/// <summary>A visual-only peer or NPC. The host decides territory, growth and road actor contacts.</summary>
internal sealed class FuchuOnlineAvatar : IDisposable
{
    private readonly GameObject root;
    private readonly Mesh mesh;
    private readonly Material[] materials;
    private Vector3 destination;
    private Quaternion rotation;
    private float phase;
    internal int SeenInSnapshot;

    internal FuchuOnlineAvatar(Transform worldRoot, ulong id, Color color, Vector3 position, float yaw)
    {
        root = new GameObject("Online Player " + id);
        root.transform.SetParent(worldRoot, false);
        var visual = new GameObject("Visual", typeof(MeshFilter), typeof(MeshRenderer));
        visual.transform.SetParent(root.transform, false);
        var filter = visual.GetComponent<MeshFilter>();
        var renderer = visual.GetComponent<MeshRenderer>();
        FuchuResidentSpawner.BuildPlayerVisual(filter, renderer);
        FuchuBattleMatch.Tint(renderer, color);
        mesh = filter.sharedMesh;
        materials = renderer.sharedMaterials;
        SetTarget(position, yaw, true);
    }

    internal void SetTarget(Vector3 position, float yaw, bool teleport = false)
    {
        destination = position;
        rotation = Quaternion.Euler(0f, yaw, 0f);
        if (teleport || (root.transform.localPosition - position).sqrMagnitude > 900f)
            root.transform.SetLocalPositionAndRotation(destination, rotation);
    }

    internal void Tick(float deltaTime)
    {
        Vector3 previous = root.transform.localPosition;
        float blend = 1f - Mathf.Exp(-18f * deltaTime);
        root.transform.localPosition = Vector3.Lerp(previous, destination, blend);
        root.transform.localRotation = Quaternion.Slerp(root.transform.localRotation, rotation, blend);
        phase += Vector3.Distance(previous, root.transform.localPosition) * 3f;
        root.transform.GetChild(0).localPosition = Vector3.up * Mathf.Abs(Mathf.Sin(phase)) * 0.025f;
    }

    public void Dispose()
    {
        UnityEngine.Object.Destroy(root);
        UnityEngine.Object.Destroy(mesh);
        foreach (Material material in materials) UnityEngine.Object.Destroy(material);
    }
}
