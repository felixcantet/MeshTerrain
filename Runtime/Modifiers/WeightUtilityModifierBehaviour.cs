using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Scene wrapper for <see cref="WeightUtilityModifier"/>. Mirrors UE <c>UWeightUtilityModifier</c>
    /// (<c>MeshPartitionWeightUtilityModifier.h/.cpp</c>): a radial inner→outer weight paint centered at
    /// the component location. UE's <c>ComputeBounds = FBox(PatchLocation ± (Radius+Falloff, Radius+Falloff,
    /// MaxZDistance/2))</c> and <c>Op-&gt;ComponentTransform = GetComponentTransform()</c>; here the
    /// wrapper's grid-local <b>position</b> sets the core's <see cref="WeightUtilityModifier.Center"/>.
    ///
    /// <para>Divergence (documented Phase-2 simplification): the core measures distance as axis-aligned XZ
    /// from <c>Center</c>, so rotation/scale of this object don't deform the disk (UE transforms vertices
    /// into patch space). Position is honored faithfully; rotation/scale are ignored for the paint.</para>
    /// </summary>
    [AddComponentMenu("Mesh Terrain/Modifiers/Weight Utility Modifier")]
    public sealed class WeightUtilityModifierBehaviour : ModifierBehaviour
    {
        [Header("Channel")]
        public string WeightChannelName = "";

        [Header("Profile")]
        [Tooltip("Full-strength inner radius (XZ distance). UE Radius.")]
        [Min(0f)] public float Radius = 50f;
        [Tooltip("Smoothstep falloff width beyond the radius. UE Falloff.")]
        [Min(0f)] public float Falloff = 25f;
        [Tooltip("Weight applied inside the radius. UE InnerValue.")]
        public float InnerValue = 1f;
        [Tooltip("Weight applied beyond radius + falloff. UE OuterValue.")]
        public float OuterValue = 0f;
        [Tooltip("Full height of the affected box in Y (UE MaxZDistance, here MaxYDistance). The paint is a 2D " +
                 "XZ operation, so this must span the DISPLACED vertex range — modifiers run in order, so by the " +
                 "time this paints, the base may already be pushed far in Y by noise. Too small and vertices on " +
                 "tall features fall outside the view and stay unpainted (torn paint). Default is large; reduce " +
                 "only to intentionally restrict painting to a Y band.")]
        [Min(0f)] public float MaxYDistance = 100000f;

        public override bool IsBaseModifier => false;

        public override void GetWrittenChannels(System.Collections.Generic.List<string> outNames)
        {
            if (!string.IsNullOrEmpty(WeightChannelName)) outNames.Add(WeightChannelName);
        }

        protected override ModifierComponent BuildCore(float4x4 gridToWorld)
        {
            return new WeightUtilityModifier
            {
                Center = GridLocalPosition(gridToWorld), // UE GetComponentTransform().GetTranslation()
                WeightChannelName = string.IsNullOrEmpty(WeightChannelName) ? null : WeightChannelName,
                Radius = Radius,
                Falloff = Falloff,
                InnerValue = InnerValue,
                OuterValue = OuterValue,
                MaxYDistance = MaxYDistance,
            };
        }

        // --- Gizmos: inner radius + outer falloff ring (XZ), Y extent box ---

        void OnDrawGizmos() => DrawGizmo(false);
        void OnDrawGizmosSelected() => DrawGizmo(true);

        void DrawGizmo(bool selected)
        {
            Vector3 c = transform.position;
            float a = selected ? 1f : 0.4f;
            // Inner full-strength radius.
            Gizmos.color = new Color(0.3f, 0.8f, 1f, a);
            DrawCircleXZ(c, Radius);
            // Outer falloff ring.
            Gizmos.color = new Color(0.3f, 0.8f, 1f, a * 0.5f);
            DrawCircleXZ(c, Radius + Falloff);
        }

        static void DrawCircleXZ(Vector3 center, float radius)
        {
            const int seg = 48;
            Vector3 prev = center + new Vector3(radius, 0, 0);
            for (int i = 1; i <= seg; i++)
            {
                float ang = (i / (float)seg) * Mathf.PI * 2f;
                Vector3 p = center + new Vector3(Mathf.Cos(ang) * radius, 0, Mathf.Sin(ang) * radius);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
