using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Paints a weight channel with a radial inner→outer blend. Unity port of UE
    /// <c>UWeightUtilityModifier</c> / its background op (<c>MeshPartitionWeightUtilityModifier.cpp</c>),
    /// <b>simple path only</b> — the cosine-weighted / submesh branch (which needs DynamicSubmesh + vertex
    /// normals) is out of scope for Phase 2. Distance is measured in the XZ plane (Unity-native flat axis).
    /// </summary>
    public sealed class WeightUtilityModifier : ModifierComponent
    {
        /// <summary>Paint center in mesh-local space.</summary>
        public float3 Center = float3.zero;

        public string WeightChannelName;

        /// <summary>Full-strength radius (XZ distance).</summary>
        public float Radius = 50f;

        /// <summary>Smoothstep falloff width beyond <see cref="Radius"/>.</summary>
        public float Falloff = 25f;

        /// <summary>Value applied inside the radius.</summary>
        public float InnerValue = 1f;

        /// <summary>Value applied beyond radius + falloff.</summary>
        public float OuterValue = 0f;

        /// <summary>Half-height of the affected box in Y (the channel paint ignores Y otherwise).</summary>
        public float MaxYDistance = 1000f;

        public override Bounds ComputeBounds()
        {
            float r = Radius + Falloff;
            return new Bounds(
                new Vector3(Center.x, Center.y, Center.z),
                new Vector3(2f * r, MaxYDistance, 2f * r));
        }

        public override IModifierJob CreateJob() => new WeightUtilityModifierJob(this);
    }

    /// <summary>Thread-safe op for <see cref="WeightUtilityModifier"/>.</summary>
    public sealed class WeightUtilityModifierJob : IModifierJob
    {
        readonly WeightUtilityModifier _m;

        public WeightUtilityModifierJob(WeightUtilityModifier modifier) => _m = modifier;

        public void GetInstancesInBounds(Bounds queryBounds, List<InstanceInfo> outInstances)
        {
            Bounds bounds = _m.ComputeBounds();
            if (!bounds.Intersects(queryBounds)) return;
            if (string.IsNullOrEmpty(_m.WeightChannelName)) return;

            outInstances.Add(new InstanceInfo
            {
                Bounds = bounds,
                InstanceID = 0,
                Read = MeshViewComponents.VertexPos | MeshViewComponents.Weight,
                Write = MeshViewComponents.Weight,
                UsedChannels = new List<string> { _m.WeightChannelName },
            });
        }

        public void ApplyModifications(MeshView view, float4x4 meshToWorld, in InstanceInfo instance)
        {
            float2 center = new float2(_m.Center.x, _m.Center.z);

            for (int i = 0; i < view.VertexCount; i++)
            {
                float3 p = view.GetVertexPos(i);
                float dist = math.distance(new float2(p.x, p.z), center);

                // smoothstep(radius, radius+falloff, dist): 0 inside, 1 fully outside.
                float falloffWeight = math.smoothstep(_m.Radius, _m.Radius + _m.Falloff, dist);
                float weight = math.lerp(_m.InnerValue, _m.OuterValue, falloffWeight);

                view.SetVertexAttributeWeight(_m.WeightChannelName, i, weight);
            }
        }
    }
}
