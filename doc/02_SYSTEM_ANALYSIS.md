# 02 — Analyse complète du système

## Sommaire
1. [Concepts & vocabulaire](#1-concepts--vocabulaire)
2. [Structures de données](#2-structures-de-données)
3. [Le pipeline de bout en bout](#3-le-pipeline-de-bout-en-bout)
4. [Algorithme : partition spatiale](#4-algorithme--partition-spatiale)
5. [Algorithme : modifier stack non-destructif](#5-algorithme--modifier-stack-non-destructif)
6. [Algorithme : génération des channels (GPU)](#6-algorithme--génération-des-channels-gpu)
7. [Algorithme : LODs & simplification](#7-algorithme--lods--simplification)
8. [Algorithme : skirts anti-cracks](#8-algorithme--skirts-anti-cracks)
9. [Build incrémental & cache](#9-build-incrémental--cache)

---

## 1. Concepts & vocabulaire

| Terme | Définition |
|---|---|
| **MegaMesh** | Nom de code interne. Un immense maillage logique unique (ex : un terrain de plusieurs km²). Représenté par l'actor `AMeshPartition`. |
| **Mesh Partition** | Nom du plugin. Désigne le système entier. Synonyme de MegaMesh. |
| **Mesh Terrain** | Nom commercial/doc. Même chose. |
| **Section** | Un morceau du MegaMesh correspondant à une cellule de la grille de partition. Au final = un `ACompiledSection` (actor streamable). |
| **Channel** | Une couche de signal scalaire sur la surface : poids de matériau, masque, hauteur de blend… Stocké **par-vertex** (`WeightLayers`) puis rastérisé en texture. |
| **Modifier** | Une opération non-destructive bornée appliquée au mesh (`UModifierComponent`). Sculpt, noise, spline, mesh stamp, boolean… |
| **Base modifier** | Modifier de priorité 0 qui **produit** de la géométrie (heightmap, mesh provider, rectangle). Les autres la transforment. |
| **Priority Layer** | Couche nommée ordonnant l'application des modifiers (ex : "Terrain" < "Roads" < "Details"). |
| **Transformer** | Étape post-modifiers du pipeline (Subsection split, StaticMesh, Skirt, Channels…). Distinct des modifiers. |
| **Build Variant** | Configuration de build per-platform (ex : "High" avec Nanite, "Mobile" avec LODs). Porte `MaxSectionComplexity`. |
| **Definition** | Asset `UMeshPartitionDefinition` : config partagée par tout le MegaMesh (matériau, channels, variants…). |
| **Compiled Section** | Le résultat final cuit : StaticMesh + ChannelTexture + ChannelTable + collision. |
| **DDC** | Derived Data Cache d'UE. Cache disque des résultats de build, clé par hash. |

> ⚠️ **Piège** : la "hauteur" du terrain n'est **pas** stockée comme une heightmap. C'est de la **géométrie 3D pure**
> (vertices XYZ). Une heightmap n'est qu'un *importateur* qui génère cette géométrie. Le système est un éditeur de
> mesh, pas un éditeur de heightfield. C'est ce qui le distingue du `Landscape` classique d'UE et le rapproche
> d'un workflow type ZBrush/Houdini scalable.

---

## 2. Structures de données

### 2.1 `FMeshData` — le format pivot

📄 `source/MeshPartition/Source/MeshPartition/Public/MeshPartitionMeshData.h`

C'est un format mesh **volontairement minimal** (plus léger que `FDynamicMesh3` qui a tout le half-edge).
Conçu pour des millions de vertices avec attributs.

```
FMeshData {
    // Géométrie
    Vertices       : Vector3d[]          // positions
    Triangles      : Index3i[]           // triplets d'indices de vertex
    VertexRefCount : int16[]             // nb de triangles référençant ce vertex (-1 = libre)
    TriangleRefCount: int16[]            // 1 = valide, -1 = libre
    FreeVertices   : int[]               // free-list (heap) pour réutiliser les slots
    FreeTriangles  : int[]

    // Attributs par-vertex
    Normals        : Vector3f[]
    SourceUVChannels: Vector2f[][]       // jusqu'à 7 UV sets importés
    ChannelUVs     : Vector2f[]          // UV auto-générées pour l'atlas de channels
    WeightLayers   : Map<Name, float[]>  // ← LES CHANNELS (poids par vertex, par nom)

    // Attributs par-triangle
    BaseIDLayer    : int[]               // de quel base-modifier provient ce triangle

    UVRegion       : Box2f               // bbox des UV (pour l'atlas)
}
```

**Points importants pour le portage :**
- **Free-list + refcount** : permet l'ajout/suppression de triangles sans réallouer (les modifiers topologiques en abusent). En Unity, `NativeList` + index de réutilisation, ou un rebuild complet par section si tu veux simplifier au départ.
- `WeightLayers` est un `Map<Name, float[]>` : N couches scalaires nommées, une valeur par vertex. **C'est la donnée d'authoring des matériaux** (équivalent splat weights, mais sur le mesh).
- `BaseIDLayer` per-triangle : sert au re-weld aux jointures entre sources (`MergeVertexPairs`, `WeldCoincidentVertices`).
- Conversions clés : `ConvertToDynamicMesh` (vers le mesh half-edge complet pour les modifiers topologiques), `ConvertToMeshDescription` (vers le format StaticMesh), `ConvertToTriMeshCollisionData`.
- `TMeshAABBTree3<FMeshData>` (alias `FMeshABBTree3`) : l'arbre AABB est construit **directement** sur `FMeshData` via un adaptateur. Utilisé par la partition.

### 2.2 `FChannelMap` / `FChannelPacking`

📄 `MeshPartitionChannel.h`

- `FChannelDesc { Name }` : déclare une couche. `FChannelMap` = la liste, dans la Definition.
- `FChannelPacking` : packe jusqu'à **24 channels** dans une table de 4 mots de 32 bits (5 bits/slot). La table est stockée en **Custom Primitive Data** du composant → le matériau lit la table pour mapper slot→texture-channel.
- Au runtime, le matériau échantillonne **une** texture de channels par section (l'atlas) + lit la table de packing.

### 2.3 `UMeshPartitionDefinition` — config globale

📄 `MeshPartitionDefinition.h`

Asset partagé. Champs notables :
- `Material`, `ChannelMap`, `ModifierTypePriorities` (ordre des priority layers).
- `CompiledSectionBuildVariants[]` : variantes per-platform. Chacune porte `MaxSectionComplexity` (≈ `256*256*4` triangles par défaut) et un `TransformerPipeline`.
- `ChannelTexelSize` (résolution de l'atlas channels, défaut 100 uu = 1 texel/m), `MaterialCacheTexelSize`.
- `ChannelUVLayoutMethod` : comment déplier les UV de l'atlas (`ReferenceBoxProject`, `PlaneProject`, `VolumeEncoded`…).
- `MegaMeshClassVersion` (métadonnée) : bumpée quand le code de build change → invalide toutes les sections.

### 2.4 `FGridSettings`

📄 `MeshPartitionGridSettings.h`

```
FGridSettings {
    CellSize         : uint32   // taille de cellule en unités. 0 = pas de split (mono-section)
    bIs2D            : bool     // true = colonne unique en Z (terrain). false = vraie grille 3D
    WorldOriginOffset: Vector3  // ancre monde de la grille (origine World Partition)
}
```

### 2.5 `ACompiledSection` — résultat final

📄 `MeshPartitionCompiledSection.h`

Un actor par cellule. Contient :
- `MeshComponents[]` (un `UMeshPartitionStaticMeshComponent` portant le `UStaticMesh` cuit, Nanite ou LOD).
- `CollisionComponents[]`, `FarFieldMeshComponent`.
- `ChannelTexture` + `ChannelTable` (uint8[]) + `ChannelTexcoordDesc`.
- `BuildInfo` : tous les **hashs d'invalidation** (modifiers, packages, classes, variant) + `GridCellCoord` (coord absolue).

---

## 3. Le pipeline de bout en bout

```
ENTRÉE (base modifiers)
  └─ heightmap import / mesh stamp / spline / rectangle  →  FMeshData "de base"
        │
        ▼
ÉTAPE A — MODIFIER STACK (FMeshBuilder::ProcessModifierGroup)
  └─ pour chaque modifier (trié par PriorityLayer, SubPriority) :
        crée un IModifierBackgroundOp
        GetInstancesInBounds(zone) → instances à traiter
        ApplyModifications(FMeshView borné, transform, instance)
  →  un grand FMeshData continu, modifié
        │
        ▼
ÉTAPE B — PARTITION (GridHelpers::BuildGridCellMeshes)
  └─ ComputeGridDimensions : snap bounds sur grille alignée
     BuildSections : assigne chaque triangle à la cellule de son CENTROÏDE (pas de clipping !)
     → N FMeshData (un par cellule non-vide), clé = coord grille absolue
     SubsectionTransformer : re-split si section > MaxSectionComplexity
        │
        ├──────────────────┬──────────────────┐
        ▼                  ▼                  ▼
ÉTAPE C1 — CHANNELS    C2 — STATIC MESH    C3 — COLLISION
  rasterise weights      skirt anti-crack    tri-mesh +
  par-vertex → atlas     → UStaticMesh       physical material
  (GPU: UV domain +      → Nanite OU         par channel dominant
   pull-push gutter)        LOD chain (quadric)
        └──────────────────┴──────────────────┘
                           ▼
                  ACompiledSection (par cellule)
                  → streamé par World Partition (Unity : Addressables/scènes)
```

---

## 4. Algorithme : partition spatiale

📄 `MeshPartitionMeshBuilder.cpp` (lignes 164-732). **C'est l'algo le plus important à porter correctement.**

### 4.1 Calcul des dimensions de grille (`ComputeGridDimensions`)

**Anchor-shifted floor snap** : les cellules sont alignées sur des multiples de `CellSize` à partir d'une ancre
(l'origine World Partition exprimée en espace local), **pas** à partir de zéro. Cela garantit que les coordonnées
de cellule sont **stables** entre deux éditions (le cache reste valide).

```python
def compute_grid_dimensions(bounds, grid_settings, local_to_world):
    cell = grid_settings.CellSize
    anchor = grid_settings.WorldOriginOffset - local_to_world.translation   # ComputeLocalAnchor

    # snap du min vers le bas, ancré
    snapped_min = floor((bounds.min - anchor) / cell) * cell + anchor
    origin_coord = floor((bounds.min - anchor) / cell)                      # coord ENTIÈRE absolue

    extents = (bounds.max - snapped_min)
    num_x = max(1, ceil(extents.x / cell))
    num_y = max(1, ceil(extents.y / cell))
    num_z = max(1, ceil(extents.z / cell))

    cell_extent = Vector3(cell, cell, cell)
    if grid_settings.bIs2D:                  # mode terrain : une seule cellule en Z
        num_z = 1
        origin_coord.z = 0
        cell_extent.z = bounds.max.z - snapped_min.z   # la cellule couvre tout le Z

    return GridDimensions(snapped_min, origin_coord, (num_x, num_y, num_z), cell_extent)
```

### 4.2 Assignation des triangles (`BuildSections`) — POINT CLÉ

> **Le système ne découpe PAS les triangles aux frontières de cellule.** Chaque triangle est assigné
> **entièrement** à la cellule qui contient son **centroïde**. Les overlaps sont résolus de façon
> déterministe (le plus petit index de section gagne, via compare-and-swap atomique).

Conséquence : les bords de sections sont **irréguliers** (en dents de scie). C'est le **skirt** (§8) qui masque
les cracks visuels. **Cette simplification est une excellente nouvelle pour le portage** : pas de polygon clipping,
parfaitement parallélisable en Burst.

```python
def build_sections(mesh, grid):
    spatial = AABBTree(mesh)                       # accélère le marquage
    triangle_owner = atomic_int[mesh.num_triangles]  # 0 = non assigné

    # PASSE 1 — marquage parallèle par cellule
    parallel_for(section_index in range(grid.total_cells)):
        section_box = box of cell(section_index)
        for triangle in spatial.query(section_box):       # broad-phase AABB
            centroid = mean(triangle.vertices)
            if section_box.contains(centroid):
                # CAS : garde le plus PETIT index de section (déterministe)
                cas_keep_min(triangle_owner[triangle], section_index + 1)

    # PASSE 2 — construction du mesh de chaque section (parallèle)
    parallel_for(section_index in range(grid.total_cells)):
        result = FMeshData()
        src_to_new_vertex = {}
        for tri in triangles where triangle_owner[tri] == section_index + 1:
            new_tri = []
            for v in tri.vertices:
                if v not in src_to_new_vertex:
                    src_to_new_vertex[v] = result.append_vertex(mesh.position[v])
                new_tri.append(src_to_new_vertex[v])
            result.append_triangle(new_tri)
        transfer_vertex_attributes(result, mesh)   # normals, UV, weight-layers (parallèle)
        transfer_triangle_attributes(result, mesh) # base-IDs
        cells[grid.origin_coord + local_coord(section_index)] = result

    return cells   # map coord_absolue -> FMeshData
```

**Notes de portage Unity/Burst :**
- L'`AABBTree` accélère seulement le broad-phase ; tu peux commencer par un test brute-force par centroïde
  (parfaitement Burst-able) puis optimiser.
- Le `compare_exchange` atomique → en Burst, un `NativeArray<int>` + `Interlocked`-like, ou un tri par cellule.
  Alternative plus simple : calcule `cell_index_of_centroid(tri)` pour chaque triangle (parallèle, sans atomique),
  puis bucket-sort des triangles par cellule. **Recommandé pour Unity** (voir `06_BURST_AND_COMPUTE.md`).

### 4.3 Sous-découpe par complexité (`SubsectionTransformer`)

📄 `MeshPartitionSubsectionTransformer.h`. Si une section dépasse `MaxSectionComplexity` (≈ nb de triangles),
elle est re-splittée récursivement sur une sous-grille (`SubSectionSize` par défaut 12800 uu). Même algo qu'au §4.2.

---

## 5. Algorithme : modifier stack non-destructif

📄 `MeshPartitionModifierComponent.h`, `MeshPartitionMeshView.h`, exemple complet : `MeshPartitionNoiseModifier.cpp`.

### 5.1 Le pattern

Chaque modifier est un `UModifierComponent` (a des **bounds**, un **priority layer**, une **sub-priority**,
une **complexity**). Au build, il produit un `IModifierBackgroundOp` (thread-safe) avec **deux méthodes** :

```cpp
// 1. Quelles instances de ce modifier touchent cette zone ? + quels attributs lire/écrire ?
void GetInstancesInBounds(Box queryBounds, out List<FInstanceInfo> instances);

// 2. Applique la modif sur une VUE BORNÉE du mesh
void ApplyModifications(FMeshView view, Transform xf, FInstanceInfo instance);
```

`FInstanceInfo` déclare un **masque read/write** d'attributs (`EMeshViewComponents`) :
`VertexPos`, `DynamicSubmesh` (topologie), `VertexAttributeWeight` (channels), `VertexUVs`.

> **`FMeshView` est la pièce maîtresse du scaling non-destructif.** Elle donne au modifier une vue *restreinte
> à ses bounds* du mega-mesh, et *restreinte aux attributs déclarés*. Un modifier ne peut physiquement pas
> toucher hors de sa zone → le système sait exactement quelles sections invalider quand un modifier change.

### 5.2 Exemple concret : le Noise modifier

Extrait de `ApplyModifications` (simplifié) — montre le pattern type **"déplacement par vertex avec falloff"** :

```python
def apply_noise(view, world_xf, op):
    for i in range(view.vertex_count):
        p_local = view.get_vertex_pos(i)
        p_world = world_xf * p_local
        p_patch = op.component_xf.inverse() * p_world      # espace local du patch

        if not op.local_bounds.contains(p_patch):
            continue                                        # hors zone

        uv = (p_patch.x / coverage.x, p_patch.y / coverage.y)

        # paramétrisation : World ou UV du patch
        st = world_pos.xy if op.param == WORLD else uv
        st = op.translate + rotate(op.rotation) * st * op.frequency

        offset = (sin(st.x)*sin(st.y)        if op.type == SINE
                  else fbm_noise(op.fbm_mode, op.octaves, st, op.lacunarity, op.gain, ...))

        # falloff vers les bords du rectangle (smoothstep)
        offset *= falloff_1d(uv.x+0.5, d) * falloff_1d(uv.y+0.5, d)

        if op.write_to_weight_channel:
            view.set_vertex_attribute_weight(op.channel_name, i, offset)   # écrit un CHANNEL

        p_patch.z += op.intensity * offset
        view.set_vertex_pos(i, world_xf.inverse() * (op.component_xf * p_patch))
```

**Observations clés :**
- Tout se fait dans des espaces explicites (mesh-local ↔ world ↔ patch-local). À reproduire fidèlement.
- Un modifier peut écrire **et** la géométrie **et** un channel (ici : déplacement + masque).
- Le falloff `smoothstep` aux bords évite les discontinuités → essentiel pour le non-destructif (les zones se blendent).
- `fbm_noise` (Fractal Brownian Motion) vient de `GeometryCore` (`FractalBrownianMotionNoise`) — à porter en C# (lib de bruit standard).

### 5.3 Ordre & groupes

`FMeshBuilder::ProcessModifierGroup` trie les modifiers par `(PriorityLayer index, SubPriority)`. Les **base
modifiers** (`IsBase()`) produisent la géométrie initiale ; les suivants la transforment. Les modifiers sont
groupés par zone pour ne rebuild que les régions touchées.

### 5.4 Catalogue des modifiers (à porter selon besoin)

| Modifier | Effet | Complexité de port |
|---|---|---|
| Noise | Déplacement procédural FBM/sine + falloff | ⭐ Facile (exemple ci-dessus) |
| SimpleWrite | Écriture directe de test | ⭐ Trivial |
| WeightUtility | Peinture de channels (matériaux) | ⭐ Facile |
| MeshProvider / Heightmap / Rectangle | **Base** : produit la géométrie | ⭐⭐ Moyen |
| Spline / SplineRemesh | Routes/rivières le long de splines | ⭐⭐⭐ Moyen-difficile |
| MeshProject | Stamp d'un mesh sur la surface | ⭐⭐⭐ (raycast) |
| Patch / TexturePatch | Patch local géométrie/texture | ⭐⭐⭐ |
| Remesh | Re-maillage isotrope d'une zone | ⭐⭐⭐⭐ (Remesher) |
| Lattice | Déformation FFD | ⭐⭐⭐ |
| Boolean | CSG local | ⭐⭐⭐⭐⭐ (mesh boolean) |

---

## 6. Algorithme : génération des channels (GPU)

📄 `Shaders/MeshPartitionMakeSectionChannels.usf`, `MeshPartitionChannelRasterizationShaders.h`.

Objectif : transformer les **poids par-vertex** (`WeightLayers`) en une **texture atlas** échantillonnable par le
matériau. Pipeline GPU en 3 passes :

### Passe 1 — Rastérisation du domaine UV (`DrawUVDomainVS/PS`)
Le mesh de la section a des **UV d'atlas** (`ChannelUVs`). On dessine les triangles **dans l'espace UV**
(la position NDC du vertex = ses UV remappées `[0,1] → [-1,1]`), et on écrit le poids du channel comme couleur.

```glsl
// VS : position écran = UV du vertex (pas la position 3D !)
out_position.xy = uv * 2.0 - 1.0;   // (avec flip Y)
out_weight      = channel_weight[vertex_id];
// PS : écrit le poids + un masque
SV_Target0 = weight;   // valeur du channel
SV_Target1 = 1.0;      // masque "ce texel est couvert"
```

### Passe 2 — Border fill (`BorderFill.usf`)
Étend les bords couverts d'un texel pour amorcer le remplissage des gouttières.

### Passe 3 — Pull-Push gutter fill (`FillPullCS` / `FillPushCS`)
Technique classique d'**inpainting d'atlas** par pyramide de mips :
- **Pull** : downsample successif en ne moyennant que les texels couverts (le masque se propage).
- **Push** : upsample en comblant les texels non couverts depuis le niveau plus grossier.

Résultat : les gouttières (espace entre îlots UV) sont remplies par extrapolation → pas de bleeding noir au
filtrage bilinéaire/mip. **Purement qualité de rendu** ; optionnel pour un premier portage.

➡️ **En Unity** : tout ceci se reproduit en **Compute Shaders + Blit**. Voir `06_BURST_AND_COMPUTE.md §3`.
Au début, tu peux même rastériser les channels sur CPU (Burst) pour de petites résolutions.

---

## 7. Algorithme : LODs & simplification

📄 `MeshPartitionStaticMeshTransformer.h`, `GeometryProcessing/MeshSimplification*.h`.

Chaque section est simplifiée en une **chaîne de LODs** par **quadric error metric attribute-aware** :
- La métrique de quadric préserve non seulement la géométrie mais aussi **normals, UV, tangents, vertex colors,
  et weight-layers**, chacun avec un poids configurable (`NormalAttributeWeight`, `WeightLayerWeight`…).
- `ScaleCorrection` rééquilibre l'influence géométrie vs attributs (important car le terrain a une échelle de
  feature différente des assets standard).
- Trois modes de pilotage du LOD :
  - **ErrorTolerance** → déviation géométrique max, ScreenSize dérivé.
  - **ScreenSize** → seuil d'activation directe, ErrorTolerance dérivé via `PixelError` (budget en pixels à 1080p/90°FOV).
  - **TriangleCountFromScreenSize** → fraction de triangles cible.

➡️ Voir `04_NANITE_AND_PORTABILITY.md` : ces LODs **sont** le fallback de Nanite. Sans Nanite, ils deviennent
les LODs runtime. **C'est ce qui rend le système portable.**

---

## 8. Algorithme : skirts anti-cracks

📄 `MeshPartitionMeshSkirt.h`, `MeshPartitionSkirtTransformer.h`.

Comme les sections ne partagent pas de vertices (assignation par centroïde + LODs indépendants), les bords ne
coïncident pas exactement → **cracks** (fissures) visibles entre sections. Le **skirt** ajoute une "jupe"
verticale (un bandeau de triangles tombant vers le bas) le long du périmètre de chaque section. Visuellement,
la jupe bouche le trou. Technique standard en chunked terrain (clipmaps, etc.).

➡️ Simple à porter. Important pour la qualité visuelle dès qu'il y a des LODs.

---

## 9. Build incrémental & cache

📄 `MeshPartitionWorldUpdater.h/.cpp`, `MeshPartitionMeshBuilder.cpp` (DDC).

Le `FMeshPartitionWorldUpdater` détermine **quelles sections rebuilder** en comparant des hashs stockés dans
`FCompiledSectionBuildInfo` :
- `ModifiersHash` (réglages des modifiers), `ModifierSetHash` (ajout/retrait), `PackageHash` (assets sur disque
  changés sans charger), `ClassHash` (implémentation des classes de modifier changée), `BuildVariantHash`
  (réglages de la Definition/variant).
- Statuts par section : `Reuse` / `PackageHashFails` / `ClassHashFails` / `Missing` / `NonTargetVariant` /
  `Duplicate`… (voir `FCompiledSectionStatus`).
- Résultats de build mis en **DDC** (cache disque) sous le bucket `"MegaMesh"`, clé Blake3.

➡️ **À porter en dernier.** C'est une optimisation d'itération éditeur, invisible fonctionnellement. En Unity :
un cache sur disque (sérialisation `FMeshData` Unity) clé par hash des paramètres, + comparaison de hash pour
décider du rebuild par section.
