using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Scene wrapper for <see cref="NoiseModifier"/>. Mirrors UE <c>UNoiseModifier</c>
    /// (<c>MeshPartitionNoiseModifier.h/.cpp</c>): the noise patch is "centered at modifier location …
    /// transformed by the component transform" — i.e. this GameObject's transform <b>is</b> the patch.
    /// We fold the wrapper's grid-local matrix into the core's <see cref="NoiseModifier.PatchTransform"/>,
    /// reproducing UE's <c>Op-&gt;ComponentTransform = GetComponentTransform()</c> and
    /// <c>ComputeBounds = FBox(LocalCoverage, GetComponentTransform())</c>.
    ///
    /// <para>Axis note (as in the core): UE's patch plane is XY with displacement along Z; this Unity port
    /// relabels to the XZ plane with displacement along Y. Structure is identical.</para>
    /// </summary>
    [AddComponentMenu("Mesh Terrain/Modifiers/Noise Modifier")]
    public sealed class NoiseModifierBehaviour : ModifierBehaviour
    {
        [Header("Noise patch")]
        [Tooltip("Patch coverage (X,Y,Z) in patch-local units; the affected box is ±coverage/2. " +
                 "Scaled by this object's transform scale. UE UnscaledCoverage.")]
        public Vector3 UnscaledCoverage = new Vector3(2000f, 100f, 2000f);

        public NoiseParameterization Parameterization = NoiseParameterization.World;

        [Header("Noise")]
        public NoiseType DisplacementType = NoiseType.Fbm;
        public FbmMode FbmMode = FbmMode.Standard;
        public double Intensity = 10.0;
        [Range(0f, 1f)] public double Falloff = 0.25;

        [Header("Noise transform")]
        public Vector2 NoiseTranslate = Vector2.zero;
        public Vector2 NoiseFrequency = new Vector2(0.001f, 0.001f);
        [Tooltip("Rotation of the noise sample space, in degrees.")]
        public double NoiseRotation = 0.0;

        [Header("FBM")]
        [Min(1)] public int FbmOctaves = 4;
        public double FbmLacunarity = 2.0;
        public double FbmGain = 0.5;
        public double FbmSmoothness = 1.0;
        public double FbmGamma = 1.0;

        [Header("Channels")]
        public bool WriteToWeightChannel = false;
        public string WeightChannelName = "";

        public override bool IsBaseModifier => false;

        protected override ModifierComponent BuildCore(float4x4 gridToWorld)
        {
            return new NoiseModifier
            {
                // The component transform (grid-local) IS the patch transform. UE GetComponentTransform().
                PatchTransform = GridLocalMatrix(gridToWorld),
                UnscaledCoverage = (float3)UnscaledCoverage,
                Parameterization = Parameterization,
                DisplacementType = DisplacementType,
                FbmMode = FbmMode,
                Intensity = Intensity,
                Falloff = Falloff,
                NoiseTranslate = new double2(NoiseTranslate.x, NoiseTranslate.y),
                NoiseFrequency = new double2(NoiseFrequency.x, NoiseFrequency.y),
                NoiseRotation = NoiseRotation,
                FbmOctaves = FbmOctaves,
                FbmLacunarity = FbmLacunarity,
                FbmGain = FbmGain,
                FbmSmoothness = FbmSmoothness,
                FbmGamma = FbmGamma,
                WriteToWeightChannel = WriteToWeightChannel,
                WeightChannelName = string.IsNullOrEmpty(WeightChannelName) ? null : WeightChannelName,
            };
        }

        // --- Gizmos (UE UNoiseModifier::DrawVisualization: red patch rectangle + yellow oriented box) ---

        void OnDrawGizmos() => DrawGizmo(false);
        void OnDrawGizmosSelected() => DrawGizmo(true);

        void DrawGizmo(bool selected)
        {
            // Draw in world space using this transform; coverage scaled by transform scale (UE GetScale3D()).
            Matrix4x4 patchToWorld = transform.localToWorldMatrix;
            Vector3 half = UnscaledCoverage * 0.5f;

            Gizmos.matrix = patchToWorld;
            // Affected box (UE yellow wire box of the local coverage).
            Gizmos.color = selected ? new Color(1f, 0.92f, 0.2f, 1f) : new Color(1f, 0.92f, 0.2f, 0.4f);
            Gizmos.DrawWireCube(Vector3.zero, (Vector3)UnscaledCoverage);

            // Patch rectangle on the XZ plane (UE red rectangle; UE uses XY, this port uses XZ).
            Gizmos.color = selected ? Color.red : new Color(1f, 0.25f, 0.25f, 0.5f);
            Vector3 a = new Vector3(-half.x, 0f, -half.z);
            Vector3 b = new Vector3(half.x, 0f, -half.z);
            Vector3 c = new Vector3(half.x, 0f, half.z);
            Vector3 d = new Vector3(-half.x, 0f, half.z);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
