using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Scene wrapper for <see cref="RectangleBaseModifier"/> — the <b>base</b> modifier producing the grid
    /// the rest of the stack deforms. UE base modifiers are <c>IsBase()</c> mesh providers placed by their
    /// component transform; here the wrapper's grid-local position sets the rect
    /// <see cref="RectangleBaseModifier.Center"/> (XZ + base-plane Y), and <see cref="Size"/>/
    /// <see cref="Resolution"/> are authored directly.
    ///
    /// <para>The base usually spans the whole streamed world, so a single instance sits at/near the grid
    /// origin. <see cref="RectangleBaseModifier.HeightFn"/> (the heightmap-import seam) is not exposed here;
    /// a heightmap importer wrapper plugs in later.</para>
    /// </summary>
    [AddComponentMenu("Mesh Terrain/Modifiers/Rectangle Base Modifier")]
    public sealed class RectangleBaseModifierBehaviour : ModifierBehaviour
    {
        [Header("Base grid")]
        [Tooltip("Quad resolution along X and Z. Produces (x+1)*(z+1) verts, x*z*2 tris.")]
        public Vector2Int Resolution = new Vector2Int(8, 8);

        [Tooltip("World-space size of the rectangle on the XZ plane.")]
        public Vector2 Size = new Vector2(100f, 100f);

        public override bool IsBaseModifier => true;

        protected override ModifierComponent BuildCore(float4x4 gridToWorld)
        {
            return new RectangleBaseModifier
            {
                Resolution = new int2(Mathf.Max(1, Resolution.x), Mathf.Max(1, Resolution.y)),
                Size = new float2(Size.x, Size.y),
                Center = GridLocalPosition(gridToWorld), // base-plane center (XZ + Y height)
            };
        }

        // --- Gizmo: footprint outline (UE skips base bounds; keep it dim) ---

        void OnDrawGizmosSelected()
        {
            Vector3 c = transform.position;
            Vector3 h = new Vector3(Size.x * 0.5f, 0f, Size.y * 0.5f);
            Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.5f);
            Vector3 a = c + new Vector3(-h.x, 0, -h.z);
            Vector3 b = c + new Vector3(h.x, 0, -h.z);
            Vector3 d = c + new Vector3(h.x, 0, h.z);
            Vector3 e = c + new Vector3(-h.x, 0, h.z);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, d); Gizmos.DrawLine(d, e); Gizmos.DrawLine(e, a);
        }
    }
}
