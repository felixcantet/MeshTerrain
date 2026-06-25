# 06 — Burst, Job System & Compute Shaders dans ce pipeline

Ce document montre **comment leverager les outils de performance Unity** (Burst Compiler, Job System,
NativeCollections, Compute Shaders) aux endroits où le pipeline UE utilise `ParallelFor`, `UE::Tasks`, et les
shaders RDG. Il contient du **code Unity C# / HLSL concret** comme point de départ.

> Principe directeur : Unreal parallélise massivement (`ParallelFor` partout) et utilise le GPU pour les channels.
> En Unity, l'équivalent moderne est **Burst + Jobs** pour le CPU et **Compute Shaders** pour le GPU. Le système
> est *embarrassingly parallel* par section → il se prête très bien à cette stack.

---

## 1. Où la parallélisation se trouve dans le pipeline UE

| Étape UE (fichier) | Mécanisme UE | Équivalent Unity recommandé |
|---|---|---|
| Marquage triangles → cellule (`BuildSections`) | `ParallelFor` + atomic CAS | `IJobParallelFor` Burst + bucket-sort |
| Construction mesh par section | `ParallelFor` | `IJobParallelFor` (une section = un index) |
| Transfert d'attributs (UV/normals/weights) | `ParallelFor` | `IJobParallelFor` Burst |
| `ApplyModifications` (par vertex) | boucle (souvent parallélisable) | `IJobParallelFor` Burst par vertex |
| Filtrage triangles hors-bounds | `ParallelForWithTaskContext` | `IJobParallelFor` + `NativeList.ParallelWriter` |
| Simplification quadric (LODs) | tâches | lib (UnityMeshSimplifier) ou job custom |
| Channels → atlas | Compute/Pixel shaders (RDG) | Compute Shader `.compute` + Blit |
| Pull-push gutter fill | Compute shaders | Compute Shader (pyramide de mips) |

---

## 2. Partition par grille en Burst (Phase 1)

L'approche UE (atomic compare-and-swap pour le tie-break) peut être **simplifiée** en Unity par un **bucket-sort**,
plus naturel et plus rapide en Burst.

### 2.1 Job : assigner chaque triangle à sa cellule

```csharp
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
struct AssignTrianglesToCellsJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> Vertices;
    [ReadOnly] public NativeArray<int3>   Triangles;
    public float3 Anchor;
    public float  CellSize;
    public int3   CellNumber;   // dimensions de grille
    public bool   Is2D;

    [WriteOnly] public NativeArray<int> TriangleCell;   // index linéaire de cellule par triangle

    public void Execute(int triIndex)
    {
        int3 t = Triangles[triIndex];
        float3 centroid = (Vertices[t.x] + Vertices[t.y] + Vertices[t.z]) / 3f;

        int3 coord = (int3)math.floor((centroid - Anchor) / CellSize);
        coord = math.clamp(coord, int3.zero, CellNumber - 1);
        if (Is2D) coord.z = 0;

        TriangleCell[triIndex] = coord.x
                               + coord.y * CellNumber.x
                               + coord.z * CellNumber.x * CellNumber.y;
    }
}
```

### 2.2 Bucket-sort des triangles par cellule

Deux options :
- **Simple** : `NativeParallelMultiHashMap<int, int>` (cellIndex → triangleIndex), rempli en parallèle via
  `.AsParallelWriter()`. Facile, légèrement moins cache-friendly.
- **Rapide** : count (prefix-sum) puis scatter dans un `NativeArray<int>` plat avec offsets par cellule.
  Recommandé pour les gros volumes.

```csharp
// Option simple : remplissage parallèle d'une multi-hashmap
[BurstCompile]
struct BucketJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<int> TriangleCell;
    [WriteOnly] public NativeParallelMultiHashMap<int, int>.ParallelWriter Buckets;
    public void Execute(int triIndex) => Buckets.Add(TriangleCell[triIndex], triIndex);
}
```

### 2.3 Construction du mesh de chaque section

Une section = un index de job. À l'intérieur : remap des vertices (hashmap local source→nouveau), append des
triangles, transfert des attributs. Comme les sections sont indépendantes, c'est un `IJobParallelFor` parfait.

> ⚠️ **Pièges Burst** : pas de `managed types` (string, classes) dans les jobs → les noms de channels deviennent
> des `int` (index) ou `FixedString`. Les `Dictionary` deviennent `NativeParallelHashMap`. Alloue avec le bon
> `Allocator` (`TempJob` pour la durée du job, `Persistent` pour le cache).

---

## 3. Channels → atlas en Compute Shader (Phase 4)

Reproduit `MeshPartitionMakeSectionChannels.usf` (rastérisation dans le domaine UV) + le pull-push.

### 3.1 Rastérisation des poids dans l'atlas

Deux voies en Unity :

**(a) Voie "graphics" (fidèle à UE)** — dessiner les triangles dans une `RenderTexture` en utilisant les **UV
comme position clip**. Un `Material` avec un vertex shader qui fait `clipPos = float4(uv*2-1, 0, 1)` et un
fragment qui écrit le poids. Rendu via `CommandBuffer.DrawMesh` sur la RT. C'est l'équivalent exact du `.usf`.

```hlsl
// Vertex : position écran = UV (domaine UV), comme DrawUVDomainVS
v2f vert (appdata v) {
    v2f o;
    float2 uv = v.uv;            // ChannelUVs
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;          // flip Y (selon convention RT)
    o.weight = v.channelWeight;  // poids du channel pour ce vertex
    return o;
}
fragment: return o.weight;       // + un masque dans un 2e RT (MRT) ou canal alpha
```

**(b) Voie "compute"** — un Compute Shader qui, pour chaque triangle, calcule sa bbox UV en texels et fait du
scan-line barycentrique pour écrire les poids interpolés. Plus de contrôle, pas de pipeline graphique, mais plus
de code. Préférable si tu veux tout en compute.

### 3.2 Pull-Push gutter fill

Pyramide de mips, deux compute kernels (cf. `FillPullCS` / `FillPushCS` dans
`MeshPartitionChannelRasterizationShaders.h`) :

```hlsl
// PULL : downsample en ne moyennant que les texels couverts (masque > 0)
[numthreads(8,8,1)]
void Pull (uint3 id : SV_DispatchThreadID) {
    float4 sum = 0; float wsum = 0;
    [unroll] for (int dy=0; dy<2; ++dy)
    [unroll] for (int dx=0; dx<2; ++dx) {
        uint2 src = id.xy*2 + uint2(dx,dy);
        float m = MaskIn[src];
        sum += SectionIn[src] * m; wsum += m;
    }
    MaskOut[id.xy]    = wsum > 0 ? 1 : 0;
    SectionOut[id.xy] = wsum > 0 ? sum / wsum : 0;
}

// PUSH : upsample, comble les texels NON couverts depuis le niveau grossier
[numthreads(8,8,1)]
void Push (uint3 id : SV_DispatchThreadID) {
    if (MaskFine[id.xy] > 0) return;                 // déjà couvert, garder
    SectionFine[id.xy] = SectionCoarse[id.xy/2];     // (bilinéaire en pratique)
}
```

Dispatch : Pull du mip 0 → N, puis Push de N → 0. Résultat : gouttières remplies, pas de bleeding.

> **MVP** : tu peux sauter le pull-push au début (Phase 4) et juste ajouter un `Border fill` simple (1-2 texels),
> voire rastériser sur CPU/Burst pour des atlas de 256². L'optimiser plus tard.

---

## 4. NativeArray & gestion mémoire — recommandations

- **Le `MeshData` Unity = un sac de `NativeArray`** (pas de classes managées) → directement utilisable dans les
  jobs Burst, sérialisable, et convertible en `Mesh` via `Mesh.SetVertexBufferData` / `MeshData` API
  (`Mesh.AllocateWritableMeshData` + `Mesh.ApplyAndDisposeWritableMeshData` — **zéro copie**, idéal pour générer
  des meshes depuis des jobs).
- Utilise l'API **`Mesh.MeshData` / `Mesh.MeshDataArray`** (Unity 2020.1+) pour construire les meshes de sections
  **dans des jobs** sans repasser par le main thread. C'est l'équivalent le plus proche du pipeline async UE.
- Allocators : `Allocator.TempJob` (≤ 4 frames), `Allocator.Persistent` (cache de sections), `Allocator.Temp`
  (intra-job). Toujours `Dispose()` (ou `[DeallocateOnJobCompletion]`).
- Pour les weight-layers (N channels nommés) : un `NativeArray<float>` par channel + un mapping
  `name → channelIndex` côté managé (hors job). Dans les jobs, ne manipule que des `int` channel index.

---

## 5. Job System — orchestration du build

Reproduit `FMeshBuilder` + `UE::Tasks` :

```
JobHandle ScheduleSectionBuild(MeshData source):
    h1 = AssignTrianglesToCellsJob.Schedule(numTris, batch)
    h2 = BucketJob.Schedule(numTris, batch, h1)
    h3 = BuildSectionMeshesJob.Schedule(numSections, 1, h2)   // un mesh par section
    h4 = TransferAttributesJob.Schedule(..., h3)
    return h4   // Complete() ou JobHandle.CombineDependencies pour le reste du pipeline
```

- **Pipeline de modifiers** : chaque modifier = un (ou plusieurs) job dépendant du précédent (la pile est
  séquentielle par priorité, mais les vertices d'un même modifier sont parallèles).
- **Async éditeur** : en éditeur, lance les jobs sur plusieurs frames (`EditorApplication.update`) pour ne pas
  geler l'UI — équivalent du build async d'UE.
- **Determinisme** : le bucket-sort par centroïde est déterministe sans atomique (contrairement au CAS d'UE),
  donc tu n'as même pas le souci de tie-break non-déterministe.

---

## 6. Quand NE PAS sur-optimiser

- La **simplification quadric** : utilise une lib éprouvée (UnityMeshSimplifier) avant d'écrire la tienne.
- Le **pull-push** : un border-fill simple suffit longtemps.
- Le **virtual geometry** (remplacer Nanite) : presque jamais nécessaire grâce au partitionnement (cf. `04`).
- Commence **CPU/Burst** partout ; passe au **Compute** seulement quand le profiler le justifie (typiquement
  l'atlas de channels à haute résolution et le build temps-réel pendant le sculpt).

---

## Récapitulatif des packages Unity à installer

| Besoin | Package |
|---|---|
| Compilation native rapide | `com.unity.burst` |
| Jobs parallèles | inclus (Job System) + `com.unity.jobs` |
| NativeArray/List/HashMap | `com.unity.collections` |
| Math (float3, int3, math.*) | `com.unity.mathematics` |
| Streaming par section | `com.unity.addressables` |
| Génération mesh zéro-copie | inclus (`Mesh.MeshData` API) |
| Simplification (LOD) | `UnityMeshSimplifier` (Whinarn, GitHub/OpenUPM) |
