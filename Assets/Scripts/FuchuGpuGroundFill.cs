using System;
using System.Collections.Generic;
using Clipper2Lib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>Stamps claimed ground into a single-channel map and projects it onto the existing surfaces.</summary>
internal sealed class FuchuGpuGroundFill : IDisposable
{
    private const int MaskSize = 4096;
    private const GraphicsFormat MaskFormat = GraphicsFormat.R8_UNorm;
    private static readonly int MaskId = Shader.PropertyToID("_GroundMask");
    private static readonly int MaskRectId = Shader.PropertyToID("_MaskRect");
    private static readonly int WorldToGroundId = Shader.PropertyToID("_WorldToGround");
    private static readonly int FillColorId = Shader.PropertyToID("_FillColor");

    private readonly List<(MeshFilter Filter, MeshRenderer Renderer)> surfaces =
        new List<(MeshFilter, MeshRenderer)>();
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<int> indices = new List<int>();
    private RenderTexture mask;
    private Mesh stamp;
    private CommandBuffer commands;
    private Material material;
    private Transform worldRoot;
    private int stampPass;
    private bool hasFill;

    public static FuchuGpuGroundFill TryCreate(Transform worldRoot, List<Collider> ground, Color color)
    {
        if (ground.Count == 0 || SystemInfo.maxTextureSize < MaskSize ||
            !SystemInfo.IsFormatSupported(MaskFormat, GraphicsFormatUsage.Render | GraphicsFormatUsage.Sample))
            return null;

        Shader shader = Resources.Load<Shader>("FuchuGpuGroundFill");
        if (shader == null || !shader.isSupported) return null;

        var fill = new FuchuGpuGroundFill();
        try
        {
            if (fill.Initialize(worldRoot, ground, shader, color)) return fill;
        }
        catch (UnityException exception)
        {
            Debug.LogWarning("GPU ground fill initialization failed: " + exception.Message);
        }
        fill.Dispose();
        return null;
    }

    private bool Initialize(Transform root, List<Collider> ground, Shader shader, Color color)
    {
        worldRoot = root;
        Matrix4x4 worldToGround = WorldToGround();
        Vector2 min = Vector2.positiveInfinity;
        Vector2 max = Vector2.negativeInfinity;
        foreach (Collider surface in ground)
        {
            if (!surface.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null ||
                !surface.TryGetComponent(out MeshRenderer renderer)) return false;

            surfaces.Add((filter, renderer));
            Bounds bounds = filter.sharedMesh.bounds;
            Matrix4x4 toGround = worldToGround * filter.transform.localToWorldMatrix;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                Vector3 position = toGround.MultiplyPoint3x4(local);
                Vector2 point = new Vector2(position.x, position.z);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
        }

        min -= Vector2.one;
        max += Vector2.one;
        Vector2 size = max - min;
        var descriptor = new RenderTextureDescriptor(MaskSize, MaskSize, MaskFormat, 0)
        {
            useMipMap = false,
            autoGenerateMips = false,
            msaaSamples = 1
        };
        mask = new RenderTexture(descriptor)
        {
            name = "Fuchu claimed ground mask",
            filterMode = SystemInfo.IsFormatSupported(MaskFormat, GraphicsFormatUsage.Linear)
                ? FilterMode.Bilinear : FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        if (!mask.Create() || mask.graphicsFormat != MaskFormat) return false;

        material = new Material(shader);
        material.SetTexture(MaskId, mask);
        material.SetVector(MaskRectId, new Vector4(min.x, min.y, 1f / size.x, 1f / size.y));
        material.SetColor(FillColorId, color);
        stampPass = material.FindPass("Stamp");
        if (stampPass < 0 || material.FindPass("Overlay") < 0) return false;

        stamp = new Mesh { name = "Fuchu ground mask stamp", indexFormat = IndexFormat.UInt32 };
        stamp.MarkDynamic();
        commands = new CommandBuffer { name = "Fuchu ground mask" };
        commands.SetRenderTarget(mask, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        commands.ClearRenderTarget(false, true, Color.clear);
        Graphics.ExecuteCommandBuffer(commands);
        return true;
    }

    public bool TryStamp(Paths64 triangles)
    {
        if (!mask.IsCreated()) return false;
        vertices.Clear();
        indices.Clear();
        foreach (Path64 triangle in triangles)
        {
            foreach (Point64 vertex in triangle)
            {
                Vector2 point = FuchuTerritoryShape.ToVector(vertex);
                indices.Add(vertices.Count);
                vertices.Add(new Vector3(point.x, 0f, point.y));
            }
        }

        try
        {
            stamp.Clear();
            stamp.SetVertices(vertices);
            stamp.SetTriangles(indices, 0);
            commands.Clear();
            commands.SetRenderTarget(mask, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            commands.SetViewport(new Rect(0f, 0f, mask.width, mask.height));
            commands.DrawMesh(stamp, Matrix4x4.identity, material, 0, stampPass);
            Graphics.ExecuteCommandBuffer(commands);
            hasFill = true;
            return true;
        }
        catch (UnityException exception)
        {
            Debug.LogWarning("GPU ground fill stamp failed: " + exception.Message);
            return false;
        }
    }

    public bool TryDraw()
    {
        if (!mask.IsCreated()) return false;
        if (!hasFill) return true;

        material.SetMatrix(WorldToGroundId, WorldToGround());
        foreach (var (filter, renderer) in surfaces)
        {
            if (filter == null || renderer == null || !renderer.isActiveAndEnabled || filter.sharedMesh == null) continue;
            var parameters = new RenderParams(material)
            {
                layer = renderer.gameObject.layer,
                worldBounds = renderer.bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };
            Mesh mesh = filter.sharedMesh;
            Matrix4x4 matrix = filter.transform.localToWorldMatrix;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                Graphics.RenderMesh(parameters, mesh, submesh, matrix);
        }
        return true;
    }

    private Matrix4x4 WorldToGround() => worldRoot != null ? worldRoot.worldToLocalMatrix : Matrix4x4.identity;

    public void Dispose()
    {
        commands?.Release();
        if (mask != null) mask.Release();
        Object.Destroy(mask);
        Object.Destroy(material);
        Object.Destroy(stamp);
    }
}
