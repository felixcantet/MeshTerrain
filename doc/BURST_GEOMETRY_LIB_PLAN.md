# Burst Editable Mesh Library — Foundation Rework (Phase 7A)

A **100% Burst/Job-compatible editable half-edge mesh library** as the plugin's geometry foundation, on top
of which every complex modifier (tessellation, boolean, remesh, mesh-based) is built. It replaces the
fixed-topology working buffer (for the topology path) and the managed UnityMeshSimplifier LOD, turning the
cook into a pure Job dependency chain. This document is the **library** plan; the complex modifiers (Phases
B–D) build on it and are summarized at the end.

---

## Context & why

Today: `Runtime/Data/MeshData.cs` is a fixed-size `NativeArray` mesh (no append/remove, no adjacency, no
ref-counts — by explicit design, `MeshData.cs:11-16`). `MeshView.cs` is a managed per-vertex scatter facade;
`MeshViewComponents` omits UE's `DynamicSubmesh` bit. The cook runs on `Task.Run` because three stages are
**managed** (UnityMeshSimplifier LOD, `ChannelUVUnwrap`, `ChannelRasterizerCPU`). The spatial partition is
**already** `[BurstCompile]` (`Runtime/Partition/PartitionJobs.cs`) and is the count→prefix-sum→fill template.

UE's editable mesh (`FDynamicMesh3`, GeometryCore) gives topology ops (split/flip/collapse, append/remove,
attribute overlays, AABB tree). We port it faithfully **but in a Burst-legal storage layout**, because the
user wants a solid Burst foundation, not a managed engine on `Task.Run`.

---

## Locked decisions

1. **Target Unity.Collections 6.4 / Unity 6000.4+** — bump `package.json` (`com.unity.collections` 2.5.1→6.4,
   `com.unity.burst` 1.8.18→1.8.23; Mathematics 1.3.2 unchanged). The plugin becomes **Unity 6.4+**.
2. **Full-Burst editable mesh** — structs of `NativeList`/`NativeArray`; ops as `[BurstCompile]` jobs/methods.
3. **CSR flat adjacency** replaces UE `FSmallListSet` (linked-list one-rings = pointer-chasing, illegal in Burst).
4. **Build 4 custom native containers** (confirmed absent from 6.4): `NativeBinaryHeap`, `NativeMeshBVH`,
   `NativeCSRAdjacency`, `NativeUnionFind`.
5. **Replace UnityMeshSimplifier with a Burst Garland–Heckbert quadric decimator now** (last managed blocker).
6. **Foundation first**, then modifiers. Faithful to UE; **splines deferred**; no third-party CSG.

---

## Burst constraints that shape the design (non-negotiable)

- **No managed types** in jobs: no `class`/`List`/`Dictionary`/`string`/virtual/delegate. → channel names stay
  main-thread; jobs see channels by **int index** (existing `WeightLayerSet` side-car pattern, kept).
- **No pointer-chasing / linked lists** → UE `FSmallListSet` one-ring out; use **CSR** (`NativeCSRAdjacency`).
- **No growing a `NativeArray` mid-job**; `NativeList` grows single-writer; topology ops that add geometry run
  as **single `IJob`s** (append-safe) or **count→prefix-sum→fill** (the `PartitionJobs.BucketTrianglesJob` pattern).
- **No mid-iteration container mutation**: one-ring edits during split/flip/collapse are **recorded into a
  `NativeStream`/`NativeQueue<AdjEdit>`**, then a separate rebuild job regenerates the CSR. No erase-while-iterate.
- **Allocator discipline**: `Allocator.Temp` job-local only; per-cook scratch via **`RewindableAllocator`/
  `DoubleRewindableAllocators`** (fast bulk-free, ping-pong for in/out passes); cross-job via `TempJob`/`Persistent`.
  Leak detector ON in dev. No `Debug.Log`/`Stopwatch` in jobs (profiler markers only).
- **Determinism**: every op a pure function of input + deterministic order (no traversal-order dependence) so
  per-cell builds reproduce the full build at borders (§6.3).

---

## Right structure for each problem (Collections 6.4 mapping)

Confirmed-available in 6.4 (verified on disk + per-type): `NativeArray/List(+AsParallelWriter/AsDeferredJobArray)`,
`NativeSlice`, `NativeReference`, `NativeHashMap/Set`, `NativeParallelHashMap/Set/MultiHashMap(+AsParallelWriter)`,
`NativeQueue(+ParallelWriter)`, `NativeRingQueue` (single-thread), `NativeStream/UnsafeStream` (uncontended
multi-buffer parallel append), `NativeBitArray`, `FixedList*`, `NativeSort` (+binary search), `UnsafeAtomicCounter`,
`xxHash3`; jobs `IJobParallelForDefer/Batch/Filter`; allocators `Rewindable/DoubleRewindable/AutoFree/Scratch`.

| Problem | Structure | Why |
|---|---|---|
| Vertex/tri/edge buffers (growable) | `NativeList<T>` | append during topology ops; `AsParallelWriter` for parallel fill |
| Ref-counts + free slots | `NativeList<ushort>` + `NativeList<int>` free-stack | port of `FRefCountVector`; flat, Burst-clean |
| One-ring adjacency | **`NativeCSRAdjacency` (custom)** | replaces `FSmallListSet`; flat offsets+list, rebuilt via count→fill |
| Edge-by-vertex-pair lookup (build) | `NativeHashMap<int2,int>` | find/create edge for a tri without one-ring |
| Topology edit deltas (parallel) | `NativeStream` (per-thread buffer) | uncontended append of `AdjEdit`/new-tris from `IJobParallelFor` |
| Variable-length next-pass over a built list | `NativeList.AsDeferredJobArray` + `IJobParallelForDefer` | process geometry whose count a prior job produced — no main-thread round-trip |
| LOD edge-collapse priority | **`NativeBinaryHeap` (custom)** | cost-ordered collapse with O(log n) `UpdateKey`; no Collections equivalent |
| Boolean/MeshProject spatial query | **`NativeMeshBVH` (custom)** | ray/nearest/winding over a mesh; no Collections tree |
| Vertex weld / UV-island labeling | **`NativeUnionFind` (custom)** | transitive merge; path-compressed parent array |
| Submesh boundary-edge set | `NativeHashSet<int2>` | source-space shared edges (weld guard) |
| Per-cook scratch | `RewindableAllocator` / `DoubleRewindableAllocators` | bulk-free per section; ping-pong tessellation in/out |
| Sorting (collapse queue seed, etc.) | `NativeSort` | Burst quicksort/radix + binary search |
| Streaming load-queue, modifier stack order | keep `List.Sort` (small n, main thread) | not worth a custom heap |

**The 4 custom containers** live in `Runtime/Geometry/Containers/`, each a `[NativeContainer]` struct over
`UnsafeList`/`NativeArray`, Burst-tested, `IDisposable`:
- `NativeBinaryHeap<TKey,TValue>` — min-heap + `heapPos[]` index map → O(log n) `Pop`/`UpdateKey` (UE `TBinaryHeap`).
- `NativeMeshBVH` — flat integer-encoded BVH (build single-job; Burst `RaycastNearestTri`/`NearestPoint`/`Winding`).
- `NativeCSRAdjacency` — `AdjStart NativeArray<int>` + `AdjList NativeList<int>` + `Rebuild` (count→prefix→fill).
- `NativeUnionFind` — `parent`/`rank` `NativeArray<int>`, path-compressed `Find`/`Union`.

---

## The Burst editable mesh (`BurstMesh`) — storage

Port of `FDynamicMesh3` storage, Burst-legal:
```
Positions/Triangles/TriEdges/Edges : NativeList<float3 / int3 / int3 / Edge(int2 V,int2 T; T.y=-1 boundary)>
Vertex/Edge/TriRefCount + Free      : NativeList<ushort> + NativeList<int>      (0 = free)
OneRing                             : NativeCSRAdjacency                         (rebuilt after topo pass)
Overlays (UV / weight / BaseID)     : flat parallel NativeLists (element ref-count, Elements<T>, ElementTris)
```
`CheckValidity()` (port of UE `FMeshData::CheckValidity`) asserts ref-count/free-list/adjacency consistency.

---

## Algorithms to implement (mechanics)

1. **Ref-counted append/remove** (`RefCountVector` port): `AppendVertex/Triangle` (reuse free slot or grow,
   auto-create edges via the `NativeHashMap<int2,int>`), `RemoveTriangle` (decrement ref-counts, free orphans).
   Mirrors `MeshPartitionMeshData.cpp:37-268`.
2. **CSR adjacency + deferred rebuild** (keystone replacing `FSmallListSet`): topology ops enqueue
   `AdjEdit{vertex,edge,Add|Remove}` into a `NativeStream`; `RebuildAdjacencyJob` does count→prefix-sum→fill
   (the `BucketTrianglesJob` pattern). Rebuild per topology *pass*, not per edge.
3. **Edge ops** (`DynamicMesh3_Edits.cpp` port, one-ring writes → the AdjEdit stream):
   `SplitEdge` (append midpoint, split 1–2 tris, 1–3 edges, overlay-interpolate), `FlipEdge` (in-place rewrite
   + 2 remove/2 add, manifold check), `CollapseEdge` (iterate vRemove's **CSR slice** — flat, Burst-legal —
   rewrite tris vRemove→vKeep, free degenerates, free vRemove), `PokeTriangle`. Each returns an info struct.
4. **Attribute overlays** (`DynamicMeshOverlay` port): flat element arrays; split/collapse **barycentric-
   interpolate** UV/weight so channel data survives topology change (prevents the weld tearing channels).
5. **`DynamicSubmesh` extract → writeback → weld** — THE critical seam (`MeshPartitionMeshView.cpp:16-654`):
   extract job builds a submesh + `VertexMap`/`TriangleMap` + `EdgesOnSubmeshBoundary` (`NativeHashSet<int2>`);
   writeback removes originals → appends submesh remapping ids + overlays → **`MergeVertexPairs` welds** boundary
   vertices onto source originals (closes cracks). Boundary edges are **constrained** (topology ops must not
   split/collapse them) — the seam-hole guard.
6. **`NativeMeshBVH`** (`MeshAABBTree3.h` port): top-down centroid-split build (single `IJob`), Burst queries
   (Möller–Trumbore raycast / nearest-point / winding-number for Boolean).
7. **`QuadricDecimator`** (Garland–Heckbert, replaces UnityMeshSimplifier): per-vertex 4×4 quadrics
   (`IJobParallelFor`), collapse cost = `vᵀ(Qa+Qb)v` at the optimal position, a **`NativeBinaryHeap`** of edges
   by cost, greedy `CollapseEdge` to the LOD's target fraction, re-cost the affected CSR one-ring. Preserve
   boundary (penalty), UV/weight seams (seam quadric term), reject normal flips. Swaps into `SectionLODBaker`,
   keeping the Phase-6 per-LOD-after-skirt ordering. UnityMeshSimplifier kept only as a parity oracle, then dropped.

---

## Cook pipeline: `Task.Run` → Job dependency chain

`EditableMeshData` wraps `BurstMesh`: `FromMeshData(in MeshData)` / `ToMeshData(Allocator)` (compact + snapshot
to the **unchanged** fixed `struct MeshData` partition/atlas/LOD already consume).
- `ModifierGroup.ApplyModifiers` (`ModifierGroup.cs:161`): any `DynamicSubmesh` modifier ⇒ wrap once in
  `EditableMeshData`; simple modifiers keep the **existing flat `MeshView` path unchanged**; topology modifiers
  use the submesh extract/writeback/weld jobs; then `ToMeshData()`. No topology ⇒ flat path verbatim.
- `SectionCooker`/`MeshTerrainStreamer`: geometry cook becomes a **`JobHandle` chain** (modifier → partition
  (exist) → channel UV/raster → LOD quadric → skirt). Streamer `FinalizeCompleted` polls `JobHandle.IsCompleted`
  (mirror the existing `Task.IsCompleted` budget/cancel/generation logic; keep `MaxConcurrentCooks`/
  `MaxMillisPerFrame`). Burst-ify `ChannelUVUnwrap` (`Dictionary`→`NativeHashMap`) + `ChannelRasterizerCPU`
  (`float[][]`→flat `NativeArray`+offset) so the chain has **no managed gap**. Blob serialize stays main-thread.
- **Partition consistency**: full build trivial; per-cell ships only the **identity** topology modifier in this
  phase (adds nothing) so §6.3 holds by construction; operands spanning >1 cell ⇒ **windowed multi-cell build**
  (handled when B/C land).

---

## New files

`Runtime/Geometry/Containers/`: `NativeBinaryHeap.cs`, `NativeMeshBVH.cs`, `NativeCSRAdjacency.cs`,
`NativeUnionFind.cs`.
`Runtime/Geometry/`: `BurstMesh.cs`, `MeshRefCounts.cs`, `MeshAttributeOverlays.cs`, `QuadricDecimator.cs`,
`DynamicSubmesh.cs`.
`Runtime/Data/EditableMeshData.cs`.
`Runtime/Modifiers/`: `MeshView.Topology.cs` (additive `DynamicSubmesh` branch), `MeshViewComponents.cs`
(un-defer `DynamicSubmesh = 1<<3`).
Modified: `ModifierGroup.cs` (cook seam), `SectionCooker.cs` + `MeshTerrainStreamer.cs` (Job chain),
`SectionCompiler.cs`/`SectionLODBaker` (quadric swap), `package.json` (deps bump).
Tests (`Tests/Runtime/Geometry/`): `Native{BinaryHeap,MeshBVH,CSRAdjacency,UnionFind}Tests`, `BurstMeshTests`,
`EditableMeshDataTests`, `SubmeshWeldTests` (keystone), `QuadricDecimatorTests` (parity), `IdentitySubmeshModifier`,
a Job-scheduled cook smoke test; extend `ProcessCellTests`.

---

## Risks & mitigations
1. **CSR deferred-rebuild correctness** → `CheckValidity` after every op; golden adjacency vs brute force.
2. **The weld** (boundary-edge identity in source space) → identity round-trip + "poke-then-weld" tests.
3. **Quadric LOD parity/quality** → golden vs UnityMeshSimplifier within epsilon (boundary/UV-seam preserved)
   before dropping it.
4. **Custom-container Burst legality** → each `[GenerateTestsForBurstCompatibility]` + a job-scheduled unit test.
5. **Job-chain integration** (streamer budget/cancel on `JobHandle`) → reuse the exact `Task.IsCompleted`
   finalize structure; load/unload sweep asserts no stall + no leak.
6. **6.4 upgrade fallout** (Unity 6.4 requirement, API drift) → bump in one commit, compile-gate, verify the
   existing partition jobs still build before any new code.

---

## Phasing (de-risk order; each independently verifiable)
- **A0** — bump deps to Collections 6.4 / Burst 1.8.23 / Unity 6.4; confirm existing jobs compile. *No new code.*
- **A1** — 4 custom containers (`NativeBinaryHeap`, `NativeMeshBVH`, `NativeCSRAdjacency`, `NativeUnionFind`),
  unit-tested in jobs. *Reusable foundation.*
- **A2** — `BurstMesh` + ref-counts + append/remove + `CheckValidity` + `EditableMeshData` round-trip.
- **A3** — CSR rebuild + `SplitEdge`/`FlipEdge`/`CollapseEdge` (deferred AdjEdit stream).
- **A4** — overlays (UV/weight/BaseID) + barycentric interpolation through topology ops.
- **A5 (keystone)** — `DynamicSubmesh` extract → writeback → **weld** + boundary constraint; identity-modifier
  build == no-modifier build (no cracks); §6.3 holds.
- **A6** — `QuadricDecimator` (parity vs UnityMeshSimplifier) → swap into `SectionLODBaker`.
- **A7** — cook `Task.Run` → `JobHandle` chain; Burst-ify channel UV unwrap + raster. *Zero visual change, full green.*

**Single most important milestone:** A5 (weld round-trips identity, passes §6.3) + A6 (Burst-LOD parity). Those
prove the Burst foundation; every later phase is additive.

---

## Then: complex modifiers on the foundation (Phases B–D, summarized)
- **B — Tessellation**: `HalfEdgeMesh.cs` (port of `HalfEdgeMesh.h`, cleanest 1:1, already Burst-shaped) +
  `AdaptiveDisplacement.cs` + `MeshPostProcessing.cs` (uses A3 ops) + `Ops/TessellateOp.cs` → adaptive
  Patch/TexturePatch. Boundary edges constrained (no split) so the weld matches.
- **C — Boolean + Remesh**: `MeshBoolean.cs` (winding via A1 `NativeMeshBVH`), `Remesher.cs`/`QueueRemesher.cs`
  (A3 collapse + `NativeBinaryHeap`), `MeshConstraints.cs`, `MeshProjectionTarget.cs`, `Ops/RemeshOp.cs`,
  `BooleanModifier`/`RemeshModifier` (+`*Behaviour`). Margin = max(cellMargin, operand reach); operand >1 cell
  ⇒ windowed multi-cell build.
- **D — Modifiers**: SimpleWrite (first), Lattice, Patch/TexturePatch (flat then adaptive), MeshProvider
  (`UnityEngine.Mesh`→`BurstMesh`, main-thread cached), MeshProject (A1 BVH raycast + bary sampling). Each:
  `<Name>Modifier.cs` + `<Name>ModifierBehaviour.cs`; `ComputeParamsHash` wired into `SectionKeyBuilder`.

## Critical reference files
- UE: `Engine/.../GeometryCore/Public/DynamicMesh/DynamicMesh3.h` (+ `DynamicMesh3_Edits.cpp`),
  `Util/RefCountVector.h`, `Util/SmallListSet.h` (what CSR replaces), `DynamicMeshOverlay.h`,
  `Spatial/MeshAABBTree3.h`; `doc/source/.../MeshPartitionMeshData.h` (append/remove contract),
  `MeshPartitionMeshView.cpp:16-654` (weld), `.../Tessellation/HalfEdgeMesh.h`.
- Ours: `Runtime/Partition/PartitionJobs.cs` (count→fill pattern the CSR rebuild mirrors),
  `Runtime/Data/MeshData.cs`, `Runtime/Modifiers/MeshView.cs`/`ModifierGroup.cs`,
  `Runtime/Sections/SectionCompiler.cs`, `Runtime/Streaming/SectionCooker.cs`/`MeshTerrainStreamer.cs`,
  `Tests/Runtime/ProcessCellTests.cs` (§6.3 harness). Collections 6.4 source:
  `c:/Repos/Prototypes/MeshTerrainDev/Library/PackageCache/com.unity.collections@5b6ebd78ccc0/`.

## Verification (user runs Unity tests)
- **A0–A1**: existing + custom-container tests green in scheduled jobs; leak detector clean.
- **A2–A4**: `BurstMesh`/overlay tests; `CheckValidity` after every op; `ToMeshData`/`FromMeshData` round-trip
  == input.
- **A5**: identity-submesh build == no-modifier build (no cracks), reusing `ProcessCellTests` equality; §6.3.
- **A6**: `QuadricDecimator` parity vs UnityMeshSimplifier within epsilon; boundary/UV-seam preserved.
- **A7**: terrain renders identically (zero regression) with the cook as a Job chain; load/unload sweep — no
  stall, no leak.
