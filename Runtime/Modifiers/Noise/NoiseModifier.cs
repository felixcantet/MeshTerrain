using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>Displacement function of the noise modifier. UE <c>ENoiseModifierType</c>.</summary>
    public enum NoiseType { SineWave, Fbm }

    /// <summary>Noise sample-space parametrization. UE <c>ENoiseParameterization</c>.</summary>
    public enum NoiseParameterization { PatchUV, World }

    /// <summary>
    /// Procedural displacement modifier (FBM / sine + edge falloff). Unity port of UE
    /// <c>UNoiseModifier</c> / <c>FNoiseBackgroundOp</c> (<c>MeshPartitionNoiseModifier.cpp</c>).
    ///
    /// <b>Coordinate spaces (the system's #1 pitfall) are reproduced faithfully:</b>
    /// mesh-local → world (via the mesh transform) → patch-local (via the inverse patch transform).
    /// The patch is a plane in its own local XZ; displacement is applied along patch-local <b>Y</b>
    /// (the patch "up"). A default (identity) patch therefore displaces a flat XZ mesh along world Y —
    /// the Unity-native axis convention (Phase 1 <c>Is2D</c> collapses Y). This differs from UE only in
    /// the relabelling of axes (UE's patch plane is XY, up = Z); the structure is identical.
    /// </summary>
    public sealed class NoiseModifier : ModifierComponent
    {
        /// <summary>
        /// Patch transform (TRS, patch-local → mesh-local): places/orients/scales the noise patch within
        /// the mesh. The patch is a plane in its own XZ; displacement is along patch-local Y.
        /// </summary>
        public float4x4 PatchTransform = float4x4.identity;

        /// <summary>Unscaled patch coverage (X,Y,Z) in patch-local units; the affected box is ±coverage/2.</summary>
        public float3 UnscaledCoverage = new float3(100f, 100f, 100f);

        public NoiseType DisplacementType = NoiseType.Fbm;
        public NoiseParameterization Parameterization = NoiseParameterization.PatchUV;
        public FbmMode FbmMode = FbmMode.Standard;

        public double Intensity = 10.0;
        /// <summary>Edge falloff fraction [0,1]; smoothsteps the displacement to 0 at the patch border.</summary>
        public double Falloff = 0.25;

        public double2 NoiseTranslate = double2.zero;
        public double2 NoiseFrequency = new double2(1.0, 1.0);
        /// <summary>Rotation of the noise sample space, in degrees.</summary>
        public double NoiseRotation = 0.0;

        public int FbmOctaves = 4;
        public double FbmLacunarity = 2.0;
        public double FbmGain = 0.5;
        public double FbmSmoothness = 1.0;
        public double FbmGamma = 1.0;

        /// <summary>When true, also writes the raw offset into <see cref="WeightChannelName"/>.</summary>
        public bool WriteToWeightChannel;
        public string WeightChannelName;

        public override Bounds ComputeBounds()
        {
            // The patch-local coverage box transformed into mesh-local space, then its AABB.
            float3 half = UnscaledCoverage * 0.5f;
            var b = new Bounds(math.transform(PatchTransform, float3.zero), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                float3 corner = new float3(
                    (i & 1) == 0 ? -half.x : half.x,
                    (i & 2) == 0 ? -half.y : half.y,
                    (i & 4) == 0 ? -half.z : half.z);
                b.Encapsulate(math.transform(PatchTransform, corner));
            }
            return b;
        }

        public override IModifierJob CreateJob() => new NoiseModifierJob(this);

        public override Hash128 ComputeParamsHash()
        {
            var h = base.ComputeParamsHash();
            h.AppendValue(PatchTransform);
            h.AppendValue(UnscaledCoverage);
            h.Append((int)DisplacementType);
            h.Append((int)Parameterization);
            h.Append((int)FbmMode);
            h.AppendValue(Intensity);
            h.AppendValue(Falloff);
            h.AppendValue(NoiseTranslate);
            h.AppendValue(NoiseFrequency);
            h.AppendValue(NoiseRotation);
            h.Append(FbmOctaves);
            h.AppendValue(FbmLacunarity);
            h.AppendValue(FbmGain);
            h.AppendValue(FbmSmoothness);
            h.AppendValue(FbmGamma);
            h.Append(WriteToWeightChannel ? 1 : 0);
            if (!string.IsNullOrEmpty(WeightChannelName)) h.Append(WeightChannelName);
            return h;
        }
    }

    /// <summary>The thread-safe op for <see cref="NoiseModifier"/>. Port of UE <c>FNoiseBackgroundOp</c>.</summary>
    public sealed class NoiseModifierJob : IModifierJob
    {
        readonly NoiseModifier _m;
        readonly float4x4 _patchToMesh;  // patch-local -> mesh-local (the modifier's PatchTransform)
        readonly float4x4 _meshToPatch;  // inverse

        public NoiseModifierJob(NoiseModifier modifier)
        {
            _m = modifier;
            _patchToMesh = modifier.PatchTransform;
            _meshToPatch = math.inverse(modifier.PatchTransform);
        }

        public void GetInstancesInBounds(Bounds queryBounds, List<InstanceInfo> outInstances)
        {
            Bounds bounds = _m.ComputeBounds();
            if (!bounds.Intersects(queryBounds)) return;

            var read = MeshViewComponents.VertexPos;
            var write = MeshViewComponents.VertexPos;
            List<string> channels = null;

            if (_m.WriteToWeightChannel && !string.IsNullOrEmpty(_m.WeightChannelName))
            {
                read |= MeshViewComponents.Weight;
                write |= MeshViewComponents.Weight;
                channels = new List<string> { _m.WeightChannelName };
            }

            outInstances.Add(new InstanceInfo
            {
                Bounds = bounds,
                InstanceID = 0,
                Read = read,
                Write = write,
                UsedChannels = channels,
            });
        }

        public void ApplyModifications(MeshView view, float4x4 meshToWorld, in InstanceInfo instance)
        {
            float3 half = _m.UnscaledCoverage * 0.5f;
            double falloffDist = 0.5 * math.clamp(_m.Falloff, 0.0, 1.0);

            double rot = math.radians(_m.NoiseRotation);
            double cos = math.cos(rot), sin = math.sin(rot);

            for (int i = 0; i < view.VertexCount; i++)
            {
                // The patch frame is defined in mesh-local space (PatchTransform: patch -> mesh-local),
                // mirroring UE's chain mesh -> ... -> patch-local. We go straight mesh-local -> patch-local;
                // the world transform is only consulted for the World parametrization's sample coordinate.
                float3 meshVertex = view.GetVertexPos(i);
                float3 patchLocal = math.transform(_meshToPatch, meshVertex);

                // Outside the patch coverage box? skip.
                if (math.any(patchLocal < -half) || math.any(patchLocal > half))
                    continue;

                // Patch UV in the patch's XZ plane (Unity flat plane); UE uses XY.
                double2 patchUV = new double2(
                    patchLocal.x / _m.UnscaledCoverage.x,
                    patchLocal.z / _m.UnscaledCoverage.z);

                double2 st;
                if (_m.Parameterization == NoiseParameterization.World)
                {
                    float3 world = math.transform(meshToWorld, meshVertex);
                    st = new double2(world.x, world.z);
                }
                else
                {
                    st = patchUV;
                }

                // translate + rotate * (st * frequency)
                double2 scaled = st * _m.NoiseFrequency;
                double2 rotated = new double2(cos * scaled.x - sin * scaled.y,
                                              sin * scaled.x + cos * scaled.y);
                st = _m.NoiseTranslate + rotated;

                double offset = _m.DisplacementType == NoiseType.SineWave
                    ? math.sin(st.x) * math.sin(st.y)
                    : FbmNoise.Evaluate(_m.FbmMode, _m.FbmOctaves, st,
                        _m.FbmLacunarity, _m.FbmGain, _m.FbmSmoothness, _m.FbmGamma);

                // Smoothstep edge falloff towards the patch border (uv re-centred to [0,1]).
                offset *= FallOff1D(math.clamp(patchUV.x + 0.5, 0.0, 1.0), falloffDist)
                        * FallOff1D(math.clamp(patchUV.y + 0.5, 0.0, 1.0), falloffDist);

                if (_m.WriteToWeightChannel && !string.IsNullOrEmpty(_m.WeightChannelName))
                    view.SetVertexAttributeWeight(_m.WeightChannelName, i, (float)offset);

                // Displace along patch-local Y (patch "up"), then map back to mesh-local.
                patchLocal.y += (float)(_m.Intensity * offset);
                float3 newMesh = math.transform(_patchToMesh, patchLocal);
                view.SetVertexPos(i, newMesh);
            }
        }

        /// <summary>Smoothstep edge falloff in [0,1]. Port of UE <c>FallOff1D</c>.</summary>
        static double FallOff1D(double value, double falloffDist)
        {
            if (falloffDist <= 0.0) return 1.0;
            if (value < falloffDist)
                return Smoothstep(0.0, falloffDist, value);
            if (value > 1.0 - falloffDist)
                return Smoothstep(0.0, falloffDist, 1.0 - value);
            return 1.0;
        }

        static double Smoothstep(double a, double b, double x)
        {
            double t = math.saturate((x - a) / (b - a));
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
