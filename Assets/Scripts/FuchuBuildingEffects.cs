using System.Collections.Generic;
using UnityEngine;

/// <summary>Uses a per-game seed and building ID so streamed tiles share one stable building effect.</summary>
public sealed class FuchuBuildingEffects : MonoBehaviour
{
    internal enum EffectKind { Movement, Toughness, StaminaCapacity }

    internal static double PointsFor(EffectKind kind, double floorAreaPoints) =>
        kind == EffectKind.StaminaCapacity ? floorAreaPoints : floorAreaPoints / 10.0;

    internal static string EffectName(EffectKind kind)
    {
        switch (kind)
        {
            case EffectKind.Movement: return "移動速度ポイント（緑）";
            case EffectKind.Toughness: return "頑丈さポイント（赤）";
            default: return "スタミナ上限ポイント（黄）";
        }
    }

    private static Color EffectColor(EffectKind kind)
    {
        switch (kind)
        {
            case EffectKind.Movement: return new Color(0.1f, 1f, 0.25f);
            case EffectKind.Toughness: return new Color(1f, 0.12f, 0.08f);
            default: return new Color(1f, 0.9f, 0.08f);
        }
    }

    private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>();
    private uint seed;
    private Vector2 center;
    private Transform worldRoot;
    private ParticleSystem particles;
    private Material material;
    private float nextEmission;
    private FuchuWalkController player;

    internal uint Seed => seed;
    internal void SetSessionSeed(uint value)
    {
        if (seed == value) return;
        seed = value;
        // Old-session particles must not retain a different building-effect color.
        if (particles != null) particles.Clear();
        nextEmission = 0f;
    }

    internal Vector3 PlayableCenter => new Vector3(center.x, 0f, center.y);

    private void Awake()
    {
        seed = (uint)Random.Range(1, int.MaxValue);
        player = GetComponent<FuchuWalkController>();
        worldRoot = transform.parent;
        Transform boundary = worldRoot != null ? worldRoot.Find("FuchuWalkCircularBoundary_1km") : null;
        if (boundary != null && boundary.childCount > 0)
        {
            foreach (Transform segment in boundary)
            {
                Vector3 point = worldRoot.InverseTransformPoint(segment.position);
                center += new Vector2(point.x, point.z);
            }
            center /= boundary.childCount;
        }

        Shader shader = Resources.Load<Shader>("FuchuBuildingEffectOrb");
        if (shader == null) return;
        material = new Material(shader);
        var obj = new GameObject("Building Effect Orbs");
        obj.transform.SetParent(worldRoot, false);
        particles = obj.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particles.main;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 24000;
        main.startLifetime = 5f;
        main.startSpeed = 0f;
        var emission = particles.emission;
        emission.enabled = false;
        var shape = particles.shape;
        shape.enabled = false;
        var color = particles.colorOverLifetime;
        color.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
        color.color = gradient;
        var renderer = obj.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        particles.Play();
    }

    internal EffectKind GetEffect(string id) => GetEffect(seed, id);

    internal static EffectKind GetEffect(uint sessionSeed, string id)
    {
        uint hash = sessionSeed;
        unchecked
        {
            foreach (char character in id) hash = (hash ^ character) * 16777619u;
            hash ^= hash >> 16;
            hash *= 2246822519u;
            hash ^= hash >> 13;
        }
        return (EffectKind)(hash % 3u);
    }

    internal bool OverlapsPlayableArea(Rect bounds)
    {
        Vector2 closest = new Vector2(Mathf.Clamp(center.x, bounds.xMin, bounds.xMax),
            Mathf.Clamp(center.y, bounds.yMin, bounds.yMax));
        return (closest - center).sqrMagnitude <= 1000f * 1000f;
    }

    internal void Register(string id, Vector2 min, Vector2 max, float height)
    {
        Vector2 position = (min + max) * 0.5f;
        if (string.IsNullOrEmpty(id) || (position - center).sqrMagnitude > 1000f * 1000f) return;
        if (!sources.ContainsKey(id)) sources.Add(id, new Source());
        Source source = sources[id];
        source.Height = height;
        source.Footprint = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    internal void Capture(string id)
    {
        if (sources.TryGetValue(id, out Source source)) source.Captured = true;
    }

    private void Update()
    {
        if (particles == null || player == null || !player.IsPlaying || Time.time < nextEmission) return;
        bool online = player.IsOnlineExploration;
        if (online && !player.Online.MatchPlaying) return;
        nextEmission = Time.time + 2f;
        foreach (var entry in sources)
        {
            Source source = entry.Value;
            if (online)
            {
                // Ownership and effect seed come from the host; particles are local visuals only.
                if (!player.Online.TryGetBuildingOwner(entry.Key, out int owner) || owner >= 0) continue;
            }
            else if (source.Captured) continue;
            for (int side = 0; side < 4; side++)
            {
                Rect box = source.Footprint;
                Vector2 point = side < 2
                    ? new Vector2(side == 0 ? box.xMin - 1f : box.xMax + 1f, Random.Range(box.yMin, box.yMax))
                    : new Vector2(Random.Range(box.xMin, box.xMax), side == 2 ? box.yMin - 1f : box.yMax + 1f);
                Vector3 position = new Vector3(point.x, source.Height + 0.5f, point.y);
                particles.Emit(new ParticleSystem.EmitParams
                {
                    position = worldRoot != null ? worldRoot.TransformPoint(position) : position,
                    velocity = (worldRoot != null ? worldRoot.up : Vector3.up) * Random.Range(0.7f, 1.2f),
                    startSize = Random.Range(0.5f, 0.9f),
                    startColor = EffectColor(GetEffect(entry.Key))
                }, 1);
            }
        }
    }

    private void OnDestroy()
    {
        if (particles != null) Destroy(particles.gameObject);
        if (material != null) Destroy(material);
    }

    private sealed class Source
    {
        public float Height;
        public Rect Footprint;
        public bool Captured;
    }
}
