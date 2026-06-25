# Roadmap Progress Tracking

Tracks implementation progress against [`05_UNITY_ROADMAP.md`](05_UNITY_ROADMAP.md). Update the status and the
checklists as work lands.

| Phase | Title | Status |
|---|---|---|
| 0 | Fondations & format de données | ✅ Done |
| 1 | Partition spatiale | 🟡 In progress |
| 2 | Modifier stack non-destructif | 🟡 In progress |
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

## Phase 1 — Partition spatiale 🟡

**Objectif** : partitionner un grand mesh en sections sur une grille (bucket-sort Burst, cf. `06 §2`).

**En cours** (branche `phase-1-spatial-partition`). Grid + BuildSections livrés ; sous-découpe par
complexité différée (pass suivant). Convention d'axe **Unity-native** : plan XZ, +Y up — `Is2D` collapse
l'axe **Y** (≠ UE qui collapse Z). À re-mapper au portage du Noise modifier en Phase 2.

- [x] `GridSettings { float CellSize; bool Is2D; float3 WorldOriginOffset; }` + `FromDefinition`
      (`Runtime/Partition/GridSettings.cs`). `CellSize` cast depuis le `uint` de la Definition.
- [x] `GridDimensions.ComputeGridDimensions` — anchor-shifted floor snap (cf. `02 §4.1`), helpers
      `LinearIndex`/`LocalCoord`/`AbsoluteCoord`/`CellCenter` (`Runtime/Partition/GridDimensions.cs`).
- [x] `BuildSections` Burst (`Runtime/Partition/PartitionJobs.cs` + `MeshPartitioner.cs`) : assign
      triangles→cellule par centroïde (`AssignTrianglesToCellsJob`), bucket-sort prefix-sum + scatter
      (`BucketTrianglesJob`), comptage vertices par section (`CountSectionVerticesJob`), build mesh +
      transfert d'attributs par section (`BuildSectionMeshJob`, un `IJob` par section combinés).
      Sortie : `PartitionResult` (sections + clés de cellule absolues + side-car weight-layers).
- [ ] Sous-découpe par complexité (`MaxSectionComplexity`) — **différée**.

**Réf** : `02 §4.1–4.2`, `06 §2 & §5`, `MeshPartitionMeshBuilder.cpp`, `MeshPartitionSubsectionTransformer.h`.

Décisions / notes :
- Pas de clipping : assignation par centroïde → bords en dents de scie (masqués par les skirts en Phase 3).
- Bucket déterministe sans atomique (le centroïde tombe dans une seule cellule ; pas de CAS comme UE).
- `MeshData` reste fixe (pas d'append) : les counts par section sont calculés avant allocation. Le builder
  topologique reste différé à la Phase 2 (seul le chemin DynamicSubmesh en a besoin).
- Job System intégré au moteur en Unity 6 (`6000.3`) : aucune référence asmdef `Unity.Jobs` requise.
- Stabilité du cache : pour une ancre **fixe**, la coordonnée absolue d'un point monde ne dépend pas des
  bounds du mesh passées (≠ ancrer sur les bounds). Changer l'ancre re-numérote volontairement `OriginCoord`.
- Tests : `Tests/Runtime/PartitionTests.cs` (dims, indépendance coords/bounds, conservation des triangles,
  remap, transfert d'attributs + weight-layers, cas mono-cellule, smoke test ~500k tris).

---

## Phase 2 — Modifier stack non-destructif 🟡

**En cours** (branche `phase-2-modifier-stack`). Chemin simple (VertexPos + Weight + UV) ; pas de
`DynamicSubmesh` (topologie) ce pass — il exige le builder/append de `MeshData` toujours différé. Pipeline
= modifiers d'abord, puis partition (ordre UE) ; managé d'abord (Burst plus tard).

- [x] `MeshView` (vue bornée read/write, masque de composants) — `Runtime/Modifiers/MeshView.cs`,
      `MeshViewComponents.cs` (enum `[Flags]` + `InstanceInfo`). Collecte les vertices dans les bounds,
      cache pos/UV/poids par index de vue, writeback par vertex id. Rejette les écritures non déclarées /
      hors bounds.
- [x] `IModifierJob` / `ModifierComponent` abstract — `Runtime/Modifiers/IModifierJob.cs`,
      `ModifierComponent.cs` (Bounds, PriorityLayer, SubPriority, Complexity, IsBase, IsDisabled,
      ProduceBaseMesh / CreateJob).
- [x] Pipeline `ProcessModifierGroup` — `Runtime/Modifiers/ModifierGroup.cs` : tri `(PriorityLayer,
      SubPriority)`, le base modifier produit la géométrie (recompilation fraîche → non-destructif), chaque
      modifier suivant s'applique via un `MeshView` borné. Sortie `ModifierResult` (mesh + weight-layers)
      → prête pour `MeshPartitioner.Partition`.
- [x] Modifiers : **Noise** (`Noise/NoiseModifier.cs` + `FbmNoise.cs` — FBM Standard/Turbulent/Ridge sur
      `noise.snoise`, + sine, falloff smoothstep, écriture optionnelle de channel), **WeightUtility**
      (`WeightUtilityModifier.cs` — peinture radiale inner/outer, chemin simple sans cosine/submesh),
      **Rectangle base** (`RectangleBaseModifier.cs` — grille régulière, hook `HeightFn` pour le futur
      HeightmapImporter). HeightmapImporter lui-même différé.

Décisions / notes :
- **Espaces de coordonnées (piège n°1)** reproduits fidèlement : mesh-local → monde → patch-local,
  déplacement le long du Y patch ("up"), retour patch → monde → mesh-local. Convention Unity : plan XZ,
  +Y up — un patch Noise par défaut déplace un plan plat le long du **Y monde** (≠ UE qui déplace en Z patch).
- **Non-destructif** : chaque `Process` rebuild un `MeshData` neuf depuis le base ; désactiver un modifier
  et relancer reproduit exactement le résultat du bas de pile (testé).
- Tests : `Tests/Runtime/ModifierTests.cs` (topologie base, bornes/masques MeshView, peinture
  inner/outer, axe Y du Noise, écriture de channel, ordre de priorité, recompilation non-destructive,
  bout-en-bout stack → partition).

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
