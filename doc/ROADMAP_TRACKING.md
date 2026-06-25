# Roadmap Progress Tracking

Tracks implementation progress against [`05_UNITY_ROADMAP.md`](05_UNITY_ROADMAP.md). Update the status and the
checklists as work lands.

| Phase | Title | Status |
|---|---|---|
| 0 | Fondations & format de données | ✅ Done |
| 1 | Partition spatiale | ⬜ Not started |
| 2 | Modifier stack non-destructif | ⬜ Not started |
| 3 | Compilation des sections (Mesh + LOD + collision) | ⬜ Not started |
| 4 | Channels → texture atlas | ⬜ Not started |
| 5 | Streaming & build incrémental | ⬜ Not started |
| 6 | Modifiers avancés & outils interactifs (optionnel) | ⬜ Not started |

Legend: ✅ Done · 🟡 In progress · ⬜ Not started

---

## Phase 0 — Fondations & format de données ✅

**Done** (2026-06-25). Acceptance met: a `MeshData` can be built in code, converted to a `UnityEngine.Mesh`, and
displayed. All EditMode tests pass.

Deliverables:
- [x] Package dependencies added (`com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`) and
      Runtime/Tests asmdefs updated (`allowUnsafeCode`, `rootNamespace` → `Fca.MeshTerrain`; removed the stale
      `Terraformer` test reference).
- [x] `MeshData` — NativeArray-backed pivot format (positions, triangles, normals, channel UVs, one source UV,
      per-triangle base IDs). UE free-list/ref-count dropped in favour of the rebuild-per-section model.
- [x] `WeightLayerSet` — managed name → `NativeArray<float>` side-car (keeps weight layers out of Burst jobs).
- [x] `MeshData → UnityEngine.Mesh` (render): zero-copy `Mesh.MeshData` API; auto 32-bit index buffer > 65535
      verts; atlas UV in UV0, source UV in UV1.
- [x] `MeshData → UnityEngine.Mesh` (collision): positions + indices only, assignable to `MeshCollider.sharedMesh`.
- [x] `MeshData ← UnityEngine.Mesh` (`FromUnityMesh`) for building test inputs and round-trip tests.
- [x] `MeshPartitionDefinition` ScriptableObject (Material, channel names, `CellSize`, `Is2D`,
      `MaxSectionComplexity`, `ChannelTexelSize`).
- [x] EditMode tests: allocation/dispose, `WeightLayerSet`, render/collision conversion, geometry round-trip,
      large-mesh 32-bit index path, Definition defaults.

Key files:
- `Runtime/Data/MeshData.cs`, `Runtime/Data/WeightLayerSet.cs`, `Runtime/Data/MeshDataConversions.cs`,
  `Runtime/Data/MeshPartitionDefinition.cs`
- `Tests/Runtime/*` (`TestMeshFactory`, `MeshDataTests`, `MeshDataConversionTests`, `DefinitionTests`)

Decisions / notes for later phases:
- Runtime namespace is `Fca.MeshTerrain`.
- Render layout always carries a Normal channel (zero-filled + `RecalculateNormals` when the source has none).
- `MeshData` is currently fixed-size (no append/builder path). A builder may be needed when the Phase 2 modifier
  stack performs in-place topology edits.
- Single source UV channel for now; UE supports up to 7 (`SourceUVChannels`) — grow if a phase needs it.

---

## Phase 1 — Partition spatiale ⬜

**Objectif** : partitionner un grand mesh en sections sur une grille (bucket-sort Burst, cf. `06 §2`).

- [ ] `GridSettings { uint CellSize; bool Is2D; float3 WorldOriginOffset; }`.
- [ ] `ComputeGridDimensions` — anchor-shifted floor snap (cf. `02 §4.1`).
- [ ] `BuildSections` Burst : assign triangles→cellule par centroïde, bucket-sort, build mesh par section.
- [ ] Sous-découpe par complexité (`MaxSectionComplexity`).

**Réf** : `02 §4.1–4.2`, `06 §2`, `MeshPartitionMeshBuilder.cpp`, `MeshPartitionSubsectionTransformer.h`.

---

## Phase 2 — Modifier stack non-destructif ⬜

- [ ] `MeshView` (vue bornée read/write).
- [ ] `IModifierJob` / `ModifierComponent` abstract.
- [ ] Pipeline `ProcessModifierGroup` (tri par priorité).
- [ ] Modifiers : Noise, WeightUtility, Rectangle/HeightmapImporter.

---

## Phase 3 — Compilation des sections ⬜

- [ ] Section → `Mesh` + GameObject.
- [ ] LODs (simplification quadric) + `LODGroup`.
- [ ] Skirt anti-crack.
- [ ] Collision par section.

---

## Phase 4 — Channels → texture atlas ⬜

- [ ] Génération des `ChannelUVs`.
- [ ] Rastérisation des poids (Compute ou CPU/Burst).
- [ ] Pull-push gutter fill (optionnel).
- [ ] Shader terrain échantillonnant l'atlas.

---

## Phase 5 — Streaming & build incrémental ⬜

- [ ] Streaming par section (Addressables / scènes additives).
- [ ] FarFieldMesh / impostors.
- [ ] Build incrémental (hash par modifier + cache disque).

---

## Phase 6 — Modifiers avancés & outils interactifs (optionnel) ⬜

- [ ] Spline, MeshProject, Patch, Lattice, Boolean, Remesh.
- [ ] Tessellation adaptative + displacement.
- [ ] Outils d'édition interactifs (sculpt/paint brush).
