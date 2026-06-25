# 01 — Carte des fichiers (File Map)

Cette carte recense **tous les fichiers concernés** par le système Mesh Partition / MegaMesh, copiés dans
[`source/`](source/). Chaque fichier est annoté avec son **rôle** et sa **priorité de portage Unity** :

- 🟥 **P0 — Cœur indispensable** : à porter en premier, le système n'existe pas sans ça.
- 🟧 **P1 — Important** : qualité / fonctionnalités majeures.
- 🟨 **P2 — Secondaire** : optimisation, cas avancés.
- ⬜ **P3 — Ignorable pour Unity** : UI éditeur UE, intégrations spécifiques UE (World Partition, RVT, PCG, HLOD…).

> Convention de nommage Unreal : préfixes `F` = struct, `U` = UObject/Component, `A` = Actor, `I` = interface,
> `E` = enum. Suffixe de module `*Editor` = code éditeur (build/cook), sans suffixe = runtime du jeu.

---

## Vue d'ensemble des modules

```
source/
├── MeshPartition/                      ← LE PLUGIN (cœur du système)
│   ├── MeshPartition.uplugin           ← déclare les 5 modules + dépendances
│   ├── Shaders/                        ← compute/pixel shaders (channels → texture)
│   └── Source/
│       ├── MeshPartition/              🟥 Runtime : données + actors + rendu
│       ├── MeshPartitionEditor/        🟥 Editor  : TOUTE l'algorithmie de build
│       ├── MeshPartitionCompute/       🟧 Runtime : pilotage GPU des channels
│       ├── MeshPartitionModelingToolset/ 🟧 Editor : outils interactifs (sculpt…)
│       └── MeshPartitionEditorUI/      ⬜ Editor  : panneaux Slate (UI pure)
├── GeometryProcessing/                 🟥 Dépendances géométriques (headers)
└── MeshTerrainMode/                    ⬜ Mode éditeur UI (référence dépendances)
```

---

## 🟥 P0 — Le cœur (à porter en premier)

### Structures de données pivots — `MeshPartition/Source/MeshPartition/Public/`

| Fichier | Type | Rôle |
|---|---|---|
| `MeshPartitionMeshData.h` | `FMeshData` | **Le format mesh pivot** de tout le pipeline. Vertices/triangles + free-lists + refcounts, weight-layers (par vertex), base-IDs (par triangle), channel-UVs. Conversion ↔ `FDynamicMesh3`. **Commence par celui-ci.** |
| `MeshPartitionChannel.h` | `FChannelMap`, `FChannelDesc`, `FChannelPacking` | Les **couches de signal** (poids de matériaux/masques par vertex) et leur packing en table 24-channels pour le matériau. |
| `MeshPartitionDefinition.h` | `UMeshPartitionDefinition` | **Asset de config global** : matériau, channels, build variants per-platform, priorités de modifiers, texel sizes, UV layout. |
| `MeshPartitionGridSettings.h` | `FGridSettings` | Config de la grille de partition : cell size, 2D/3D, origine monde. |
| `MeshPartitionCompiledSection.h` | `ACompiledSection`, `FCompiledSectionBuildInfo` | **Résultat final** : un actor par cellule (StaticMesh + ChannelTexture + ChannelTable + collision) + les hashs d'invalidation. |
| `MeshPartitionTransformer.h` | `FTransformer`, `FTransformerContext`, `FTransformerUnit` | Interface des étapes de pipeline (post-modifiers). |
| `MeshPartitionTransformerPipeline.h` | `UTransformerPipeline` | Liste ordonnée de transformers (Subsection→StaticMesh→Skirt→…). |
| `MeshPartitionStaticMeshDescriptor.h` | `FStaticMeshDescriptor` | Métadonnées d'une section (collision profile, UV region). |

### Pipeline de build / partition — `MeshPartition/Source/MeshPartitionEditor/`

| Fichier | Rôle |
|---|---|
| `Public/MeshPartitionMeshBuilder.h` + `Private/MeshPartitionMeshBuilder.cpp` | **L'algo de partition.** `GridHelpers::ComputeGridDimensions` / `BuildGridCellMeshes` / `BuildHelpers::BuildSections` (assignation par centroïde) / `SimplifyMesh` / `ProcessModifierGroup`. **Fichier le plus important du dossier.** Voir `02_SYSTEM_ANALYSIS.md §4`. |
| `Public/MeshPartitionModifierComponent.h` + `.cpp` | **Classe de base de tous les modifiers** (`UModifierComponent`) + `IModifierBackgroundOp` (apply async borné). Le cœur du non-destructif. |
| `Public/MeshPartitionMeshView.h` | `FMeshView` / `EMeshViewComponents` : **la vue bornée** donnée à chaque modifier (masque read/write d'attributs + submesh). Pièce conceptuelle clé du scaling. |
| `Public/MeshPartitionSubsectionTransformer.h` | Sous-découpe d'une section trop complexe (`MaxSectionComplexity`). |
| `Public/MeshPartitionStaticMeshTransformer.h` | Section → `UStaticMesh` : LODs (quadric), **flag Nanite + fallback**, skirt. |
| `Public/MeshPartitionWorldUpdater.h` + `.cpp` | **Orchestrateur d'invalidation incrémentale** (hash-based : quels sections rebuilder). |

---

## 🟧 P1 — Important (qualité & fonctionnalités)

### Modifiers concrets — `MeshPartitionEditor/Public/Modifiers/` (+ `.cpp` dans `Private/Modifiers/`)

| Fichier (header + cpp) | Rôle | Algorithme |
|---|---|---|
| `MeshPartitionEditableModifierBase.h` | Base des modifiers éditables par outil interactif. | — |
| `MeshPartitionMeshBasedModifierBase.*` | Base des modifiers qui apportent/projettent un mesh. | — |
| `MeshPartitionNoiseModifier.cpp` | Déplacement procédural par bruit. | **Le plus simple — bon exemple de pattern `ApplyModifications`.** |
| `MeshPartitionRemeshModifier.*` + `Ops/MeshPartitionRemeshOp.*` | Re-maillage isotrope d'une zone. | Remesher (split/collapse/flip/smooth) |
| `MeshPartitionSplineModifier.*` + `MeshPartitionSplineRemeshModifier.*` | Routes/rivières le long de splines. | Projection + remesh contraint |
| `MeshPartitionMeshProjectModifier.*` | Projette un mesh sur la surface (stamp). | Raycast + déplacement |
| `MeshPartitionPatchModifier.*` / `MeshPartitionTexturePatchModifier.*` + `Ops/MeshPartitionTexturePatchOp.h` | Patch local de géométrie / de texture. | Blend par masque |
| `MeshPartitionBooleanModifier.cpp` | Opérations CSG locales. | Mesh boolean (GeometryAlgorithms) |
| `MeshPartitionLatticeModifier.cpp` | Déformation par lattice (FFD). | Free-form deformation |
| `MeshPartitionWeightUtilityModifier.cpp` | Édition des weight-layers (peinture de matériaux). | Écriture de channels |
| `MeshPartitionSimpleWriteModifier.cpp` | Modifier de test/écriture minimal. | **2e meilleur exemple pédagogique.** |
| `MeshPartitionProjectSculptLayersModifier.*` | Importe des sculpt layers projetées. | Projection |

### Tessellation / displacement — `MeshPartitionEditor/Private/Modifiers/Tessellation/`

| Fichier | Rôle |
|---|---|
| `HalfEdgeMesh.h` + `.cpp` | Mesh half-edge avec coords **barycentriques** par rapport au triangle de base (pour displacement). |
| `AdaptiveDisplacement.h` + `.cpp` | **Tessellation adaptative Red-Green** + displacement. Le morceau le plus mathématique. |
| `MeshPostProcessing.h` + `.cpp` | Nettoyage post-tessellation. |
| `Ops/MeshPartitionTessellateOp.*` | Uniform/Adaptive rings (GLSL-style). |

### Génération channels (GPU) — `MeshPartitionCompute/` + `Shaders/`

| Fichier | Rôle |
|---|---|
| `Shaders/MeshPartitionMakeSectionChannels.usf` | VS/PS : rastérise les poids par-vertex dans le **domaine UV** → texture atlas. |
| `Shaders/MeshPartitionBorderFill.usf` | Remplissage de bord (gutter). |
| `MeshPartitionCompute/Public/MeshPartitionChannelRasterizationShaders.h` | Déclare `DrawUVDomain`, `BorderFill`, `FillPull`, `FillPush` (**pull-push inpainting** de l'atlas). |

### Anti-cracks & sections — `MeshPartitionEditor/Public/`

| Fichier | Rôle |
|---|---|
| `MeshPartitionMeshSkirt.h` (+ `Private/.cpp`) | **Skirt** : jupe verticale au bord des sections pour masquer les cracks entre LODs. |
| `MeshPartitionSkirtTransformer.h` | Applique le skirt dans le pipeline. |
| `MeshPartitionHeightmapImporter.h` (+ `.cpp`) | Import heightmap → MegaMesh partitionné (point d'entrée terrain typique). |
| `MeshPartitionRectangleGenerator.h` | Génère un quad/plan de base. |
| `MeshPartitionCollisionTransformer.h` | Génère la collision (tri-mesh + physical material par channel dominant). |

### Dépendances géométriques — `source/GeometryProcessing/`

| Fichier | Rôle |
|---|---|
| `DynamicMesh3.h` | `FDynamicMesh3` : le mesh half-edge **standard** d'UE (les modifiers travaillent dessus via submesh). |
| `DynamicMeshAttributeSet.h` | Attributs (normals, UV, weight maps) attachés au DynamicMesh. |
| `MeshSimplification.h` + `MeshSimplificationQuadrics.h` | **Simplification quadric attribute-aware** (génère les LODs / fallback Nanite). |
| `Remesher.h` / `QueueRemesher.h` / `NormalFlowRemesher.h` | Re-maillage isotrope (utilisé par RemeshModifier). |
| `MeshAABBTree3.h` | Arbre AABB pour les requêtes spatiales (partition, projection). |

---

## 🟨 P2 — Secondaire (cas avancés / intégrations)

| Fichier | Rôle | Pourquoi P2 |
|---|---|---|
| `MeshPartitionEditorSubsystem.h` / `MeshPartitionEditorWorldSubsystem.h` | Subsystems éditeur (orchestration globale). | Glue UE ; à réimplémenter différemment en Unity. |
| `MeshPartitionEditorComponent.h` | Composant éditeur attaché à l'AMeshPartition. | Glue UE. |
| `MeshPartitionDescriptorCache.h` / `MeshPartitionModifierGraphCache.h` / `MeshPartitionGroupRegistry.h` | Caches d'invalidation. | Optimisation (faire en dernier). |
| `MeshPartitionModifierDescriptors.h` / `MeshPartitionModifierComponentDesc.h` / `MeshPartitionDependencyContext.h` / `MeshPartitionDependencyInterface.h` | Système de hash/dépendances pour le build incrémental. | Optimisation. |
| `MeshPartitionPreviewSection.h` / `MeshPartitionPreviewComponents.h` / `MeshPartitionInteractiveSection.h` | Preview live pendant l'édition. | Confort éditeur. |
| `MeshPartitionPlatformCellTransformer.h` / `MeshPartitionRuntimeCellTransformer.h` / `MeshPartitionFarFieldTransformer.h` | Transformers per-platform / far-field LOD. | Variantes de build. |
| `MeshPartitionCollisionComponent.h` / `MeshPartitionStaticMeshComponent.h` | Composants runtime spécialisés. | Mappables sur MeshCollider/MeshRenderer Unity. |
| `MeshPartitionModelingToolset/` (tout le module) | Outils interactifs sculpt/paint/remesh basés sur l'Interactive Tools Framework d'UE. | UI/outillage ; l'algo sous-jacent est dans les Ops déjà listés. |
| `MeshPartitionModule.h` / `MeshPartitionSettings.h` / `MeshPartitionUVLayoutMethod.h` | Module init / settings / méthodes UV. | Config. |

---

## ⬜ P3 — Ignorable pour Unity (spécifique UE)

| Fichier / Dossier | Pourquoi ignorer |
|---|---|
| `MeshTerrainMode/` (tout sauf `.uplugin`/`.Build.cs`) | UI du mode éditeur (Slate). Aucun algorithme. |
| `MeshPartitionEditorUI/` (tout le module) | Panneaux Slate, colonnes d'outliner. UI pure. |
| `*ActorDesc*` / `WorldPartition*` / `*DataLayer*` | World Partition d'UE (streaming). → Remplacé par Addressables/scènes additives Unity. |
| `MeshPartitionRVTTransformer.h` / `MeshPartitionMaterialCacheCommon.h` | Runtime Virtual Texture d'UE. → Optionnel ; pas d'équivalent direct Unity. |
| `PCGMeshPartitionInterop` (non copié) | Intégration au Procedural Content Generation framework d'UE. |
| `*HLOD*` | Hierarchical LOD d'UE. |
| `MeshPartitionStylusInput*` (dans MeshTerrainMode) | Support stylet pour le sculpt. |
| `Test/` (fichiers de tests) | Tests unitaires UE — utiles comme **spécification de comportement** mais pas à porter tels quels. |

---

## Fichiers à lire en priorité absolue (top 8)

Si tu n'as le temps de lire que 8 fichiers source pour comprendre le système :

1. `MeshPartition/Source/MeshPartition/Public/MeshPartitionMeshData.h` — le format de données.
2. `MeshPartition/Source/MeshPartitionEditor/Private/MeshPartitionMeshBuilder.cpp` (lignes 164-732) — partition par grille.
3. `MeshPartition/Source/MeshPartitionEditor/Public/MeshPartitionModifierComponent.h` — le modifier stack.
4. `MeshPartition/Source/MeshPartitionEditor/Public/MeshPartitionMeshView.h` — la vue bornée.
5. `MeshPartition/Source/MeshPartitionEditor/Private/Modifiers/MeshPartitionNoiseModifier.cpp` — un modifier complet simple.
6. `MeshPartition/Source/MeshPartitionEditor/Public/MeshPartitionStaticMeshTransformer.h` — LODs + Nanite.
7. `MeshPartition/Source/MeshPartition/Public/MeshPartitionDefinition.h` — la config globale.
8. `MeshPartition/Shaders/MeshPartitionMakeSectionChannels.usf` — la rastérisation des channels.
