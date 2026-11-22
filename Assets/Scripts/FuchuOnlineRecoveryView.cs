using UnityEngine;

/// <summary>Visual-only recovery cubes. All contact decisions and respawns are host-owned.</summary>
public sealed class FuchuOnlineRecoveryView : MonoBehaviour
{
    private FuchuOnlineTerritoryState state;
    private readonly Transform[] cubes = new Transform[300];
    private readonly uint[] versions = new uint[300];
    private Material material;
    internal void Bind(FuchuOnlineTerritoryState current) => state = current;
    private void Update()
    {
        if (state == null || state.Items.Count != 300) return;
        if (material == null)
        {
            material = new Material(Resources.Load<Shader>("FuchuTerritoryColor")) { color = new Color(0.12f, 0.95f, 0.24f), renderQueue = 2000 };
            material.SetFloat("_ZWrite", 1f); material.SetFloat("_Shade", 1f);
        }
        int created = 0;
        for (int i = 0; i < cubes.Length; i++)
        {
            var item = state.Items[i];
            if (cubes[i] == null)
            {
                if (created >= 25) continue;
                created++;
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "Online Stamina " + i;
                obj.transform.SetParent(transform, false);
                obj.transform.localScale = Vector3.one * item.Size;
                obj.GetComponent<Renderer>().sharedMaterial = material;
                var collider = obj.GetComponent<Collider>(); collider.enabled = false; Destroy(collider);
                cubes[i] = obj.transform;
                // Apply initial version zero as well (a temporarily unplaced item).
                versions[i] = uint.MaxValue;
            }
            if (versions[i] == item.Version) continue;
            cubes[i].localPosition = item.Position + Vector3.up * (item.Size * 0.5f + 0.15f);
            cubes[i].gameObject.SetActive(item.Active);
            versions[i] = item.Version;
        }
    }
    private void OnDestroy() { if (material != null) Destroy(material); }
}
