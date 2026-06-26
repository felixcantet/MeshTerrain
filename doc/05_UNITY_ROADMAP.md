# 05 — Roadmap de portage vers Unity

Roadmap en **6 phases incrémentales**. Chaque phase livre quelque chose de testable. L'ordre privilégie
l'apprentissage du système (data + partition d'abord) avant la complexité (modifiers, GPU, cache).

> **Stack Unity cible** : Unity 2022 LTS+ (ou 6), packages **Burst**, **Collections** (`NativeArray`/`NativeList`),
> **Jobs** (`IJobParallelFor`), **Mathematics**, optionnellement **Entities Graphics**/**Addressables** pour le
> streaming. URP ou HDRP au choix (le pipeline channel/atlas est agnostique).

> **Convention de portage depuis `02_SYSTEM_ANALYSIS.md`** : le code UE analysé est en mode terrain XY/Z-up
> (`bIs2D` collapse Z, Noise déplace en Z patch). Le port Unity choisit une convention native XZ/+Y :
> `Is2D` collapse Y, les UV/paramètres terrain utilisent XZ, et les modifiers déplacent le long du Y patch.
> Toutes les décisions ci-dessous doivent préserver les invariants de `02` (géométrie 3D pure, assignation par
> centroïde, coordonnées de cellules stables, stack non-destructif, channels par-vertex puis atlas), en remappant
> seulement les axes.

---

## Phase 0 — Fondations & format de données

**Objectif** : avoir le format mesh pivot et les conversions de base.

- [ ] `MeshData` (équivalent `FMeshData`) : `NativeArray<float3> Vertices`, `NativeArray<int3> Triangles`,
      `NativeArray<float3> Normals`, `NativeArray<float2> ChannelUVs`, `Dictionary<string, NativeArray<float>> WeightLayers`,
      `NativeArray<int> BaseIDLayer`.
- [ ] Conversion `MeshData → UnityEngine.Mesh` (set vertices/triangles/normals/uv + un UV channel pour l'atlas).
- [ ] Conversion `MeshData → Mesh` pour collision (`MeshCollider.sharedMesh`).
- [ ] (Option) une struct `ScriptableObject` `MeshPartitionDefinition` : material, liste de channels (noms),
      `CellSize` (`0 = no split` / mono-section), `Is2D` (Unity : collapse Y), `MaxSectionComplexity`,
      `ChannelTexelSize`.

**Done quand** : tu peux créer un `MeshData` en code, le convertir en `Mesh` Unity et l'afficher.
**Réf source** : `MeshPartitionMeshData.h`, `MeshPartitionDefinition.h`, `MeshPartitionChannel.h`.

---

## Phase 1 — Partition spatiale (cœur, sans modifiers)

**Objectif** : partitionner un grand mesh en sections sur une grille. **C'est la brique la plus fondamentale.**

- [ ] `GridSettings { float CellSize; bool Is2D; float3 WorldOriginOffset; }`
      (`CellSize <= 0` = mono-section, équivalent UE `CellSize = 0`).
- [ ] `ComputeGridDimensions(bounds, grid, meshToWorld)` : anchor-shifted floor snap (cf. `02 §4.1`).
      En Unity, `Is2D` collapse Y (et non Z) ; l'ancre doit rester en espace monde/local cohérent avec le
      transform du mesh pour que les coordonnées de cellule restent stables.
- [ ] `BuildSections(meshData, gridDims)` — **version Burst recommandée** :
      1. `IJobParallelFor` : pour chaque triangle, calcule `cellIndex = floor((centroid - anchor)/cell)` →
         `NativeArray<int> triangleCell`.
      2. Bucket : compte les triangles par cellule, alloue, remplit (`NativeMultiHashMap` ou prefix-sum + scatter).
      3. Par cellule (parallèle) : remap des vertices, construit le `MeshData` de section, transfère attributs.
      - *(Cette approche bucket-sort évite l'atomique CAS de la version UE et est plus simple/rapide en Burst.
         Le tie-break "plus petit index" est automatique si le centroïde tombe dans une seule cellule.)*
- [ ] Sous-découpe par complexité (`MaxSectionComplexity`) : re-split récursif d'une section trop dense.

**Done quand** : un plan de 1M triangles se découpe en NxN sections cohérentes, sans triangles perdus ni dupliqués.
**Réf source** : `MeshPartitionMeshBuilder.cpp` (164-732), `MeshPartitionSubsectionTransformer.h`.
**Voir aussi** : `06_BURST_AND_COMPUTE.md §2` (implémentation Burst détaillée).

---

## Phase 2 — Modifier stack non-destructif (la vraie valeur)

**Objectif** : reproduire le système de modifiers bornés rejouables.

- [ ] `MeshView` : vue bornée sur le `MeshData` avec masque read/write (`enum MeshViewComponents { VertexPos,
      DynamicSubmesh, Weight, UV }`). Au minimum : itération sur les vertices d'une zone + get/set position +
      get/set weight ; `DynamicSubmesh` peut rester différé tant que les modifiers topologiques le sont.
- [ ] `IModifierJob` / `ModifierComponent` (abstract) : `Bounds`, `PriorityLayer`, `SubPriority`, `Complexity`,
      `GetInstancesInBounds()`, `ApplyModifications(MeshView, transform, instance)`.
- [ ] Pipeline `ProcessModifierGroup` : trie par `(priorityLayer, subPriority)`, applique en séquence ; base
      modifier produit la géométrie.
- [ ] **Premier modifier concret : Noise** (cf. `02 §5.2`, port direct de `MeshPartitionNoiseModifier.cpp`,
      avec remap Unity XZ/+Y et tests non-identity `meshToWorld`/patch transform avant intégration scène).
- [ ] **Deuxième : WeightUtility** (peinture de channel) et **Rectangle/HeightmapImporter** (base modifier).

**Done quand** : un base modifier (heightmap) + un Noise + une peinture de channel produisent un `MeshData`
modifié, et désactiver un modifier ré-applique proprement la pile (non-destructif).
**Réf source** : `MeshPartitionModifierComponent.h`, `MeshPartitionMeshView.h`, `MeshPartitionNoiseModifier.cpp`,
`MeshPartitionHeightmapImporter.h`.

---

## Phase 3 — Compilation des sections (Mesh + LOD + collision)

**Objectif** : transformer chaque section en asset rendu/collisionnable.

- [ ] `Section → UnityEngine.Mesh` + GameObject (`MeshRenderer`/`MeshFilter`).
- [ ] **LODs par simplification quadric** : intégrer `UnityMeshSimplifier` (Whinarn) pour un premier chemin ou
      porter l'algo quadric attribute-aware. Alimenter un `LODGroup`. (Remplace Nanite — cf. `04`.)
      Attention : la parité avec `02 §7` exige de préserver normals/UV/weight-layers ; si le simplifier n'est
      pas attribute-aware, les weights doivent au minimum être packés/testés à travers les UVs de LOD.
- [ ] **Skirt** anti-crack : bandeau vertical au périmètre de section.
- [ ] Collision : `MeshCollider` par section (avec, à terme, le physical material du channel dominant).

**Done quand** : les sections s'affichent avec LODs fonctionnels et sans cracks visibles aux jointures.
**Réf source** : `MeshPartitionStaticMeshTransformer.h`, `MeshPartitionMeshSkirt.h`, `GeometryProcessing/MeshSimplification*.h`.

---

## Phase 4 — Channels → texture atlas (rendu des matériaux)

**Objectif** : rastériser les weight-layers en texture pour le shading.

- [x] Générer les `ChannelUVs` (dépliage atlas par section — box/triangle-normal project).
- [x] **Compute Shader** : rastérise les poids par-vertex dans une `RenderTexture` via le domaine UV
      (cf. `06 §3`) **et** une version CPU/Burst. Backend sélectionnable ; le GPU garde un
      `RenderTexture` array vivant (pas de readback) et retombe sur le CPU si compute non supporté.
- [x] Pull-push gutter fill (CPU + Compute) pour éviter le bleeding.
- [x] Shader de terrain (prototype URP) qui échantillonne l'atlas + la table de packing des channels
      (`FChannelPacking` : 24 channels, transport slot→texture-slice via `MaterialPropertyBlock`).
      Les weights packés en UV2-UV7 pour les LODs restent un transport intermédiaire, pas le rendu final.

**Done quand** : la peinture de channels (Phase 2) se voit comme un blend de matériaux à l'écran.
**Réf source** : `Shaders/MeshPartitionMakeSectionChannels.usf`, `Shaders/MeshPartitionBorderFill.usf`,
`MeshPartitionChannelRasterizationShaders.h`, `MeshPartitionChannel.h` (`FChannelPacking`).

---

## Phase 5 — Streaming & build incrémental

**Objectif** : passer à l'échelle (mondes larges) et accélérer l'itération.

- [ ] **Streaming par section** : Addressables ou scènes additives, charge/décharge par distance caméra.
      (Remplace World Partition.)
- [ ] `FarFieldMesh` / impostors pour les sections lointaines.
- [ ] **Build incrémental** : hash des paramètres de chaque modifier + de la Definition ; ne rebuild que les
      sections dont le hash a changé (équivalent `WorldUpdater`). Cache disque des `MeshData` cuits.

**Done quand** : édition d'un modifier ne rebuild que les sections touchées ; un monde large streame sans hitch.
**Réf source** : `MeshPartitionWorldUpdater.h`, `MeshPartitionDescriptorCache.h`, `MeshPartitionFarFieldTransformer.h`.

---

## Phase 6 (optionnelle) — Modifiers avancés & outils interactifs

À piocher selon les besoins du projet :
- [ ] Spline modifier (routes/rivières), MeshProject (stamps), Patch, Lattice, Boolean, Remesh.
- [ ] Tessellation adaptative + displacement (Red-Green — `AdaptiveDisplacement.cpp`, `HalfEdgeMesh.cpp`).
- [ ] Outils d'édition interactifs (sculpt brush, paint brush) — l'équivalent du `MeshPartitionModelingToolset`.

---

## Correspondances API UE → Unity (mémo rapide)

| Unreal | Unity |
|---|---|
| `FDynamicMesh3` | `UnityEngine.Mesh` ou structure custom + `NativeArray` |
| `FMeshData` | `MeshData` custom (`NativeArray` buffers) |
| `UStaticMesh` / `UStaticMeshComponent` | `Mesh` / `MeshFilter`+`MeshRenderer` |
| `ParallelFor` | `IJobParallelFor.Schedule()` (Burst) |
| `UE::Tasks::FTask` | `JobHandle` / `Task` C# |
| Nanite | `LODGroup` + LODs quadric (+ streaming) |
| World Partition | Addressables / `SceneManager` additif |
| `UMaterialInterface` + Custom Primitive Data | `Material` + `MaterialPropertyBlock` |
| Compute shader `.usf` (RDG) | Compute shader `.compute` + `CommandBuffer`/`Graphics.Blit` |
| DDC (Derived Data Cache) | Cache disque maison + `AssetDatabase` (éditeur) |
| `UPhysicalMaterial` | `PhysicMaterial` |
| `FBox` / `FOrientedBox3d` | `Bounds` / OBB custom |
| `FTransform` | `Matrix4x4` / `Transform` |

---

## Risques & pièges identifiés

1. **L'assignation par centroïde** (pas de clipping) donne des bords irréguliers : **ne pas oublier les skirts**
   sinon cracks. (Phase 3.)
2. **Transport des weight-layers à travers la simplification** : le quadric doit pondérer les weights
   (`WeightLayerWeight`) sinon les matériaux "bavent" sur les LODs. (Phase 3.)
3. **Stabilité des coordonnées de cellule** (anchor-shifted snap) : indispensable pour le cache incrémental ;
   ne pas ancrer sur les bounds du mesh. (Phase 1 & 5.)
4. **Espaces de coordonnées** dans les modifiers (mesh-local ↔ world ↔ patch-local) : source d'erreurs n°1.
   Reproduire fidèlement les transformations du Noise modifier, avec le remap Unity XZ/+Y documenté plus haut. (Phase 2.)
5. **Non-destructif = recompilation** : ne jamais muter la géométrie source en place de façon irréversible ;
   toujours repartir des base modifiers + rejouer la pile. (Phase 2.)
