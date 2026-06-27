using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Fca.MeshTerrain.Streaming;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode integration tests for the streamer (Phase 5.3). Drives <see cref="MeshTerrainStreamer"/>
    /// deterministically through <c>ForceLoad</c>/<c>ForceUnloadAll</c> (no Update loop needed) and verifies
    /// the end-to-end cook→present→evict path plus the leak-free guarantee
    /// (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §9, §14</c>).
    /// </summary>
    public class StreamerIntegrationTests
    {
        static List<ModifierComponent> Stack() => new List<ModifierComponent>
        {
            new RectangleBaseModifier { Resolution = new int2(40, 40), Size = new float2(400, 400) },
            new WeightUtilityModifier { WeightChannelName = "Grass", Radius = 120, Falloff = 60, InnerValue = 1, OuterValue = 0 },
        };

        static (MeshTerrainStreamer streamer, GameObject go, string dir) MakeStreamer(bool channels)
        {
            string dir = Path.Combine(Path.GetTempPath(), "MeshTerrainStreamerTest_" + System.Guid.NewGuid().ToString("N"));

            // Build the GO inactive so we can set fields before OnEnable runs.
            var go = new GameObject("StreamerTest");
            go.SetActive(false);
            var s = go.AddComponent<MeshTerrainStreamer>();
            s.WorldOriginOffset = Vector3.zero;
            s.WorldHeight = 4000f;
            s.LoadDistance = 250f;
            s.UnloadDistance = 350f;
            s.MaxConcurrentCooks = 4;
            s.MaxMillisPerFrame = 1000f; // tests finalize via ForceLoad; keep finalize unbounded if used
            s.GenerateChannels = channels;
            s.RamCapacity = 64;
            s.CacheDirOverride = dir;

            var def = ScriptableObject.CreateInstance<MeshPartitionDefinition>();
            def.name = "StreamerTestDef";
            def.CellSize = 100;
            def.Is2D = true;
            s.Definition = def;

            go.SetActive(true); // OnEnable -> EnsureInitialized
            s.SetModifierStack(Stack());
            return (s, go, dir);
        }

        static void Teardown(MeshTerrainStreamer s, GameObject go, string dir)
        {
            var def = s != null ? s.Definition : null;
            if (go != null) Object.DestroyImmediate(go);       // OnDisable -> ForceUnloadAll + cache dispose
            if (def != null) Object.DestroyImmediate(def);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }

        [Test]
        public void Streamer_ForceLoad_PresentsSection_AndForceUnload_FreesIt()
        {
            StreamingDiagnostics.Reset();
            var (s, go, dir) = MakeStreamer(channels: false);
            try
            {
                int3 coord = new int3(0, 0, 0); // center cell of the 400u rectangle around the origin
                s.ForceLoad(coord);

                Assert.AreEqual(1, s.residentCount, "ForceLoad must make the cell resident.");
                Assert.AreEqual(1, StreamingDiagnostics.LiveSections, "One live section after load.");
                Assert.GreaterOrEqual(StreamingDiagnostics.Cooks, 1, "First load must cook.");

                // The section GO must exist under the streamer.
                Assert.Greater(go.transform.childCount, 0, "A section GameObject must be parented under the streamer.");

                s.ForceUnloadAll();
                Assert.AreEqual(0, s.residentCount, "Unload must clear residency.");
                Assert.AreEqual(0, StreamingDiagnostics.LiveSections, "No live sections after unload (no leak).");
            }
            finally { Teardown(s, go, dir); }
        }

        [Test]
        public void Streamer_RepeatedLoadUnload_ReturnsToBaseline_NoLeak()
        {
            StreamingDiagnostics.Reset();
            var (s, go, dir) = MakeStreamer(channels: true);
            try
            {
                int3[] coords =
                {
                    new int3(0, 0, 0), new int3(1, 0, 0), new int3(0, 0, 1), new int3(-1, 0, 0),
                };

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    foreach (var c in coords) s.ForceLoad(c);
                    Assert.AreEqual(coords.Length, StreamingDiagnostics.LiveSections, $"Cycle {cycle}: all sections live.");

                    s.ForceUnloadAll();
                    Assert.AreEqual(0, StreamingDiagnostics.LiveSections, $"Cycle {cycle}: back to zero live sections.");
                    Assert.AreEqual(0, s.residentCount, $"Cycle {cycle}: residency cleared.");

                    // No leftover section GameObjects under the streamer after unload.
                    Assert.AreEqual(0, go.transform.childCount, $"Cycle {cycle}: no orphan section GameObjects.");
                }

                // After the first cycle, subsequent loads must hit the cache (no extra cooks).
                int cooksAfterFirstCycle = StreamingDiagnostics.Cooks;
                foreach (var c in coords) s.ForceLoad(c);
                Assert.AreEqual(cooksAfterFirstCycle, StreamingDiagnostics.Cooks,
                    "Re-loading the same cells must hit the cache, not re-cook.");
                s.ForceUnloadAll();
            }
            finally { Teardown(s, go, dir); }
        }

        [Test]
        public void Streamer_AsyncTick_LoadsAroundFocus_AndUnloadsWhenFocusLeaves_NoLeak()
        {
            StreamingDiagnostics.Reset();
            var (s, go, dir) = MakeStreamer(channels: false);
            try
            {
                // Focus at the origin: pump the async path until the ring is resident.
                s.PumpUntilIdleForTest(new float3(0, 0, 0));
                Assert.Greater(s.residentCount, 1, "Focus at origin must load a ring of sections.");
                Assert.AreEqual(0, s.pendingCount, "No cooks should remain in flight after pumping.");
                int liveAtOrigin = StreamingDiagnostics.LiveSections;
                Assert.Greater(liveAtOrigin, 0, "Sections must be live.");

                // Move the focus far away: the origin ring leaves the unload radius and must be freed.
                s.PumpUntilIdleForTest(new float3(100000, 0, 100000));
                // Out at the far focus the rectangle has no geometry, so nothing should remain live there.
                Assert.AreEqual(0, StreamingDiagnostics.LiveSections, "Leaving the area must unload all live sections (no leak).");
                Assert.AreEqual(0, go.GetComponentsInChildren<MeshRenderer>().Length, "No section renderers should remain after unload.");
            }
            finally { Teardown(s, go, dir); }
        }

        [Test]
        public void Streamer_BakedTile_ReusedFromDisk_WithoutRerunningPipeline()
        {
            StreamingDiagnostics.Reset();
            var (s, go, dir) = MakeStreamer(channels: true);
            try
            {
                int3 coord = new int3(0, 0, 0);

                // First load: a cache miss -> the full cook pipeline runs once and writes the disk blob.
                s.ForceLoad(coord);
                Assert.AreEqual(1, StreamingDiagnostics.Cooks, "First visit cooks exactly once.");
                Assert.AreEqual(1, StreamingDiagnostics.CacheMisses);

                // Unload, then drop the RAM tier so the next load can only come from the baked disk blob.
                s.ForceUnloadAll();
                s.Cache.OnEvicted(coord);

                int cooksBefore = StreamingDiagnostics.Cooks;
                s.ForceLoad(coord);

                // Reuse: NO extra cook (the whole modifier/partition/LOD/raster pipeline is skipped); served
                // from the baked tile on disk.
                Assert.AreEqual(cooksBefore, StreamingDiagnostics.Cooks, "Re-loading a baked tile must NOT re-run the pipeline.");
                Assert.GreaterOrEqual(StreamingDiagnostics.CacheHits, 1, "The reload must register as a cache hit.");

                // The baked tile must still present with its channel atlas (proves the atlas survived the
                // disk round-trip and was not re-rasterized).
                var sectionGo = go.transform.GetChild(0).gameObject;
                var mpb = new MaterialPropertyBlock();
                bool atlasBound = false;
                foreach (var r in sectionGo.GetComponentsInChildren<MeshRenderer>())
                {
                    r.GetPropertyBlock(mpb);
                    if (mpb.GetTexture(Fca.MeshTerrain.ChannelPacking.ChannelTexId) != null) atlasBound = true;
                }
                Assert.IsTrue(atlasBound, "Reused baked tile must still bind its channel atlas.");
            }
            finally { Teardown(s, go, dir); }
        }

        [Test]
        public void Streamer_InvalidateModifier_RecooksCoveredCells()
        {
            StreamingDiagnostics.Reset();

            string dir = Path.Combine(Path.GetTempPath(), "MeshTerrainStreamerTest_" + System.Guid.NewGuid().ToString("N"));
            var go = new GameObject("StreamerTest");
            go.SetActive(false);
            var s = go.AddComponent<MeshTerrainStreamer>();
            s.WorldOriginOffset = Vector3.zero; s.WorldHeight = 4000f;
            s.LoadDistance = 250f; s.UnloadDistance = 350f; s.MaxConcurrentCooks = 4; s.MaxMillisPerFrame = 1000f;
            s.GenerateChannels = false; s.RamCapacity = 64; s.CacheDirOverride = dir;
            var def = ScriptableObject.CreateInstance<MeshPartitionDefinition>();
            def.name = "StreamerTestDef"; def.CellSize = 100; def.Is2D = true;
            s.Definition = def;
            go.SetActive(true);

            var rect = new RectangleBaseModifier { Resolution = new int2(40, 40), Size = new float2(400, 400) };
            var paint = new WeightUtilityModifier { WeightChannelName = "Grass", Radius = 1000, Falloff = 1, InnerValue = 1, OuterValue = 1 };
            s.SetModifierStack(new List<ModifierComponent> { rect, paint });

            try
            {
                int3 coord = new int3(0, 0, 0);
                s.ForceLoad(coord);
                int cooksAfterFirst = StreamingDiagnostics.Cooks;
                Assert.GreaterOrEqual(cooksAfterFirst, 1);

                paint.InnerValue = 0.25f;         // edit a covering modifier
                s.InvalidateModifier(paint);      // evicts the covered, resident cell
                s.ForceLoad(coord);               // re-load -> new key -> miss -> re-cook

                Assert.AreEqual(cooksAfterFirst + 1, StreamingDiagnostics.Cooks,
                    "Editing + invalidating a covering modifier must re-cook the covered cell.");
                s.ForceUnloadAll();
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(def);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        // ---- Phase 6: scene-object modifier assembly ----

        // Builds a streamer that assembles its stack from child ModifierBehaviour wrappers (no SetModifierStack,
        // so UseSceneModifiers drives collection). Returns the streamer + the spawned wrappers + cleanup data.
        static (MeshTerrainStreamer s, GameObject go, MeshPartitionDefinition def, string dir) MakeSceneStreamer()
        {
            string dir = Path.Combine(Path.GetTempPath(), "MeshTerrainSceneTest_" + System.Guid.NewGuid().ToString("N"));
            var go = new GameObject("SceneStreamerTest");
            go.SetActive(false);
            var s = go.AddComponent<MeshTerrainStreamer>();
            s.WorldOriginOffset = Vector3.zero; s.WorldHeight = 4000f;
            s.LoadDistance = 250f; s.UnloadDistance = 350f; s.MaxConcurrentCooks = 4; s.MaxMillisPerFrame = 1000f;
            s.GenerateChannels = false; s.RamCapacity = 64; s.CacheDirOverride = dir;
            s.UseSceneModifiers = true;
            var def = ScriptableObject.CreateInstance<MeshPartitionDefinition>();
            def.name = "SceneStreamerTestDef"; def.CellSize = 100; def.Is2D = true;
            s.Definition = def;
            return (s, go, def, dir);
        }

        static T AddChildWrapper<T>(GameObject parent) where T : ModifierBehaviour
        {
            var child = new GameObject(typeof(T).Name);
            child.transform.SetParent(parent.transform, false);
            return child.AddComponent<T>();
        }

        [Test]
        public void SceneStack_AssemblesBaseFirstThenByPriority()
        {
            var (s, go, def, dir) = MakeSceneStreamer();
            try
            {
                // Add non-base wrappers BEFORE the base, and out of priority order, to prove the sort.
                var paintHi = AddChildWrapper<WeightUtilityModifierBehaviour>(go);
                paintHi.WeightChannelName = "B"; paintHi.SubPriority = 10;
                var paintLo = AddChildWrapper<WeightUtilityModifierBehaviour>(go);
                paintLo.WeightChannelName = "A"; paintLo.SubPriority = 1;
                var rect = AddChildWrapper<RectangleBaseModifierBehaviour>(go);
                rect.Size = new Vector2(400, 400); rect.Resolution = new Vector2Int(40, 40);

                go.SetActive(true); // OnEnable -> EnsureInitialized -> CollectSceneModifiers

                var stack = s.ModifierStack;
                Assert.AreEqual(3, stack.Count);
                Assert.IsTrue(stack[0] is RectangleBaseModifier, "base must sort first");
                Assert.IsTrue(stack[0].IsBase);
                // Then by ascending SubPriority: paintLo (1) before paintHi (10).
                Assert.AreEqual("A", ((WeightUtilityModifier)stack[1]).WeightChannelName);
                Assert.AreEqual("B", ((WeightUtilityModifier)stack[2]).WeightChannelName);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(def);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void EditingSceneWrapper_ReCooksCoveredCells()
        {
            StreamingDiagnostics.Reset();
            var (s, go, def, dir) = MakeSceneStreamer();
            try
            {
                var rect = AddChildWrapper<RectangleBaseModifierBehaviour>(go);
                rect.Size = new Vector2(400, 400); rect.Resolution = new Vector2Int(40, 40);
                var paint = AddChildWrapper<WeightUtilityModifierBehaviour>(go);
                paint.WeightChannelName = "Grass"; paint.Radius = 1000; paint.Falloff = 1;
                paint.InnerValue = 1; paint.OuterValue = 1;

                go.SetActive(true);

                int3 coord = new int3(0, 0, 0);
                s.ForceLoad(coord);
                int cooksAfterFirst = StreamingDiagnostics.Cooks;
                Assert.GreaterOrEqual(cooksAfterFirst, 1);

                // Edit a covering wrapper field and notify (mirrors the inspector auto-rebuild path).
                paint.InnerValue = 0.25f;
                paint.MarkDirty();
                s.NotifyModifierEdited(paint);
                s.ForceLoad(coord);

                Assert.AreEqual(cooksAfterFirst + 1, StreamingDiagnostics.Cooks,
                    "Editing a covering scene wrapper must re-cook the covered cell.");
                s.ForceUnloadAll();
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(def);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
