using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode tests for the Phase 6 scene-object modifier wrappers (<see cref="ModifierBehaviour"/> and
    /// the concrete wrappers). Verifies that a wrapper at a given transform builds a plain core whose
    /// <c>ComputeBounds()</c> / <c>ComputeParamsHash()</c> match a hand-built core (transform→placement
    /// correctness, plan risk #1) and that moving a wrapper changes both — the basis of incremental rebuild.
    /// </summary>
    public class ModifierBehaviourTests
    {
        readonly List<Object> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        T NewWrapper<T>() where T : ModifierBehaviour
        {
            var go = new GameObject(typeof(T).Name);
            _spawned.Add(go);
            return go.AddComponent<T>();
        }

        // Identity grid: the wrapper's world transform maps 1:1 to the modifier's mesh-local frame.
        static readonly float4x4 GridIdentity = float4x4.identity;

        static void AssertBoundsApproxEqual(Bounds a, Bounds b, float eps = 1e-3f)
        {
            Assert.That((a.center - b.center).magnitude, Is.LessThan(eps), "bounds center");
            Assert.That((a.size - b.size).magnitude, Is.LessThan(eps), "bounds size");
        }

        // ---- Noise wrapper: transform → PatchTransform (UE GetComponentTransform) ----

        [Test]
        public void NoiseWrapper_BuildCore_MatchesManualCore()
        {
            var w = NewWrapper<NoiseModifierBehaviour>();
            w.transform.position = new Vector3(120f, 5f, -40f);
            w.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
            w.UnscaledCoverage = new Vector3(200f, 80f, 300f);
            w.Intensity = 12.0;
            w.NoiseFrequency = new Vector2(0.002f, 0.003f);

            var core = (NoiseModifier)w.GetCore(GridIdentity);

            // Hand-built core with the SAME placement matrix the wrapper derives (identical float path, so the
            // PatchTransform-folded hash matches bit-for-bit) and the same params.
            var manual = new NoiseModifier
            {
                PatchTransform = (float4x4)w.transform.localToWorldMatrix,
                UnscaledCoverage = new float3(200f, 80f, 300f),
                Intensity = 12.0,
                NoiseFrequency = new double2(0.002f, 0.003f),
            };

            AssertBoundsApproxEqual(core.ComputeBounds(), manual.ComputeBounds());
            Assert.AreEqual(manual.ComputeParamsHash(), core.ComputeParamsHash());
        }

        [Test]
        public void MovingNoiseWrapper_ChangesCoreBoundsAndHash()
        {
            var w = NewWrapper<NoiseModifierBehaviour>();
            w.transform.position = new Vector3(0f, 0f, 0f);
            var hashA = w.GetCore(GridIdentity).ComputeParamsHash();
            var boundsA = w.GetCore(GridIdentity).ComputeBounds();

            w.transform.position = new Vector3(500f, 0f, 0f);
            w.MarkDirty();
            var coreB = w.GetCore(GridIdentity);

            Assert.AreNotEqual(hashA, coreB.ComputeParamsHash(), "moving the patch must change the params hash");
            Assert.That((coreB.ComputeBounds().center - boundsA.center).magnitude, Is.GreaterThan(100f));
        }

        // ---- WeightUtility wrapper: transform position → Center (UE PatchLocation) ----

        [Test]
        public void WeightWrapper_BuildCore_MatchesManualCore()
        {
            var w = NewWrapper<WeightUtilityModifierBehaviour>();
            w.transform.position = new Vector3(60f, 0f, 75f);
            w.WeightChannelName = "Rock";
            w.Radius = 40f; w.Falloff = 15f; w.InnerValue = 1f; w.OuterValue = 0f; w.MaxYDistance = 800f;

            var core = (WeightUtilityModifier)w.GetCore(GridIdentity);
            var manual = new WeightUtilityModifier
            {
                Center = new float3(60f, 0f, 75f),
                WeightChannelName = "Rock",
                Radius = 40f, Falloff = 15f, InnerValue = 1f, OuterValue = 0f, MaxYDistance = 800f,
            };

            AssertBoundsApproxEqual(core.ComputeBounds(), manual.ComputeBounds());
            Assert.AreEqual(manual.ComputeParamsHash(), core.ComputeParamsHash());
        }

        // ---- Rectangle base wrapper ----

        [Test]
        public void RectangleWrapper_IsBase_AndMatchesManualCore()
        {
            var w = NewWrapper<RectangleBaseModifierBehaviour>();
            w.transform.position = new Vector3(10f, 2f, 10f);
            w.Resolution = new Vector2Int(16, 16);
            w.Size = new Vector2(640f, 640f);

            Assert.IsTrue(w.IsBaseModifier);
            var core = (RectangleBaseModifier)w.GetCore(GridIdentity);
            var manual = new RectangleBaseModifier
            {
                Resolution = new int2(16, 16),
                Size = new float2(640f, 640f),
                Center = new float3(10f, 2f, 10f),
            };

            Assert.IsTrue(core.IsBase);
            AssertBoundsApproxEqual(core.ComputeBounds(), manual.ComputeBounds());
            Assert.AreEqual(manual.ComputeParamsHash(), core.ComputeParamsHash());
        }

        // ---- Grid-origin offset: a wrapper's placement is RELATIVE to the streamer grid origin ----

        [Test]
        public void WrapperPlacement_IsRelativeToGridOrigin()
        {
            var w = NewWrapper<WeightUtilityModifierBehaviour>();
            w.transform.position = new Vector3(1000f, 0f, 0f);

            // Grid origin at (900,0,0): the modifier's mesh-local center should be (100,0,0).
            var gridToWorld = float4x4.Translate(new float3(900f, 0f, 0f));
            var core = (WeightUtilityModifier)w.GetCore(gridToWorld);

            Assert.That(math.distance(core.Center, new float3(100f, 0f, 0f)), Is.LessThan(1e-3f));
        }
    }
}
