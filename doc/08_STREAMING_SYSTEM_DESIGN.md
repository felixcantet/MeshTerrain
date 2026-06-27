# 08 — Streaming System Design (Phase 5)

> **Status**: design — drives Phase 5 development. No code yet. This document is the source of truth
> for the custom streaming + incremental-build solution. It deliberately diverges from the Unreal
> original on one axis (see §1.2) and stays faithful on the rest.

## Sommaire
1. [Objectifs & contexte](#1-objectifs--contexte)
2. [Principe directeur : cook vs instantiate](#2-principe-directeur--cook-vs-instantiate)
3. [Vue d'ensemble de l'architecture](#3-vue-densemble-de-larchitecture)
4. [Le presenter seam (agnosticisme backend)](#4-le-presenter-seam-agnosticisme-backend)
5. [Coordonnées, résidence & politique de streaming](#5-coordonnées-résidence--politique-de-streaming)
6. [Build borné par cellule (`ProcessCell`)](#6-build-borné-par-cellule-processcell)
7. [Le cache : invalidation par hash & format disque](#7-le-cache--invalidation-par-hash--format-disque)
8. [File de travail budgétée & async](#8-file-de-travail-budgétée--async)
9. [Cycle de vie & garanties mémoire](#9-cycle-de-vie--garanties-mémoire)
10. [Far-field / anti-pop](#10-far-field--anti-pop)
11. [Scénarios supportés](#11-scénarios-supportés)
12. [API publique proposée](#12-api-publique-proposée)
13. [Découpage en jalons](#13-découpage-en-jalons)
14. [Tests & vérification](#14-tests--vérification)
15. [Risques & questions ouvertes](#15-risques--questions-ouvertes)

---

## 1. Objectifs & contexte

### 1.1 Objectif

Faire passer Mesh Terrain à l'échelle de mondes larges via un **streaming par section piloté par la
distance**, tout en supportant la **génération procédurale à l'exécution** (player build) — capacité
qu'aucune solution Unity prête à l'emploi (ECS subscenes, Addressables) ne couvre, car toutes deux
exigent un *bake hors-ligne*. Voir `doc/research` (conversation Phase 5) pour la justification.

**Acceptance (repris de `05_UNITY_ROADMAP.md` Phase 5)** : éditer un modifier ne rebuild que les
sections touchées ; un monde large streame sans hitch.

### 1.2 Où l'on diverge d'Unreal — et pourquoi

L'original UE est **build-time** : tout est cuit hors-ligne (module `MeshPartitionEditor`,
`ProcessModifierGroup`/`BuildGridCellMeshes`, DDC), puis le runtime ne fait que **streamer des
`ACompiledSection` préfabriqués** via World Partition (`ACompiledSection::IsRuntimeOnly() == true`,
aucune logique de build dans le module runtime). Le seul build « runtime » est le **placeholder PIE**
(`URuntimeCellTransformer`, `#if WITH_EDITOR`), absent du player.

Notre port **ajoute** la génération à l'exécution : on cuit **paresseusement au premier accès** (cache
miss), puis on streame du préfabriqué pour toujours. En régime permanent, le coût runtime est **identique**
à UE (stream de préfabriqué) ; la différence est seulement *quand* le cook a lieu.

**Invariant clé** : le cook n'arrive **qu'une fois** par couple `(cellule, paramètres)`. Le streaming
piloté caméra ne fait, en régime permanent, **que de l'instantiate** (lire un blob + uploader un `Mesh`).
Si le profiler montre une recompilation de géométrie sur simple déplacement caméra, c'est un **bug de
cache**, pas le design.

### 1.3 Contraintes de couplage

Le système doit rester **fortement couplé à Mesh Terrain** (réutilise `GridDimensions`,
`MeshPartitioner`, `ModifierGroup`, `SectionCompiler`, `MeshData`, `WeightLayerSet`) mais **agnostique
du backend de présentation** (GameObject aujourd'hui ; ECS/entities en option future) via un seam
`ISectionPresenter` (§4). Aucune dépendance sur le package Entities dans le cœur du streaming.

---

## 2. Principe directeur : cook vs instantiate

Deux opérations radicalement différentes en coût. Toute la performance du système tient à ne jamais
confondre les deux.

| Opération | Contenu | Coût | Fréquence visée |
|---|---|---|---|
| **Cook** | `ModifierGroup` (FBM par vertex), partition, LOD quadric, rastérisation channels | lourd (10⁻¹–10⁰ s/section) | **une fois** par `(cellule, hash params)` |
| **Instantiate** | `MeshData → UnityEngine.Mesh` (zéro-copie), `AddComponent`, bind atlas | léger (10⁻⁴–10⁻³ s) | chaque load |

Cycle de vie d'une section :

```
Premier besoin d'une cellule (cache MISS) :
    cook (lourd) → écrit MeshData+weights+atlas cuits sur disque → instantiate (léger)

Tout load ultérieur (cache HIT — le cas normal) :
    lit le MeshData cuit (RAM LRU, sinon disque) → instantiate (léger)
    ⟵ AUCUN modifier eval, AUCUNE partition, AUCUN LOD simplify, AUCUNE raster channel

Unload :
    Dispose GameObject/Mesh/collider/atlas. Les octets cuits restent sur disque.
```

Un rebuild n'arrive **que** quand le hash d'invalidation change (§7) : édition d'un modifier (miss
scopé sur les sections dans ses bounds), changement de Definition/variant. Le déplacement caméra
n'invalide **jamais** rien.

---

## 3. Vue d'ensemble de l'architecture

```
                         focus (caméra) en espace monde
                                     │
                   ┌─────────────────▼──────────────────┐
                   │     StreamingController (MonoBeh.)  │  par-frame : calcule l'ensemble désiré,
                   │     - Definition + stack modifiers  │  diffe vs résident, enfile load/unload
                   │     - resident set : Map<int3,…>    │
                   └──────┬──────────────────────┬───────┘
                   load   │                      │  unload
                          ▼                      ▼
              ┌────────────────────┐    presenter.Release(handle)
              │  SectionStreamQueue │    + cache LRU eviction (§9)
              │  budget N/frame ou  │
              │  budget temps/frame │
              └─────────┬───────────┘
                        │ résoudre la section
            ┌───────────▼─────────────┐  hit RAM   ┌──────────────────────────┐
            │     SectionCache         │───────────▶│  LRU mémoire (cuit + Mesh)│
            │  - clé = SectionKey hash │            └──────────────────────────┘
            │  - 2 tiers : RAM + disque│  hit disque
            └───────────┬─────────────┘───────────▶ lit blob → CookedSection
                        │ miss
            ┌───────────▼─────────────┐
            │  SectionCooker           │  ModifierGroup.ProcessCell(coord) →
            │  (build borné §6)        │  Partition(1 cellule) → CookedSection
            └───────────┬─────────────┘  → écrit cache (RAM + disque)
                        │ CookedSection (MeshData+weights+atlas cuits)
            ┌───────────▼─────────────┐
            │  ISectionPresenter       │  GameObjectSectionPresenter (défaut) :
            │  (seam backend §4)       │  SectionCompiler → CompiledSection
            └──────────────────────────┘  → SectionHandle (pour Release)
```

Responsabilités, une par composant :

- **StreamingController** — *politique* : qui doit être résident, vu le focus. Pur calcul + diff.
- **SectionStreamQueue** — *ordonnancement* : applique un budget par frame pour ne jamais bloquer.
- **SectionCache** — *persistance* : RAM LRU + disque, clé par hash d'invalidation.
- **SectionCooker** — *production* : build borné d'**une** cellule (la partie qu'UE n'a pas au runtime).
- **ISectionPresenter** — *présentation* : transforme le cuit en quelque chose de visible/collidable.

---

## 4. Le presenter seam (agnosticisme backend)

Le cœur du streaming ne connaît **que** `CookedSection` (données cuites, backend-agnostiques) et une
interface de présentation. C'est ce qui permet d'ajouter ECS plus tard **sans toucher au cœur**.

```csharp
namespace Fca.MeshTerrain.Streaming
{
    /// Données cuites d'une section, prêtes à présenter. Backend-agnostique.
    /// Possède des NativeArray (MeshData) + le blob atlas ; le owner doit Dispose.
    public sealed class CookedSection : IDisposable
    {
        public int3 Coord;
        public SectionKey Key;                 // hash d'invalidation (§7)
        public MeshData Mesh;                  // cuit (post-modifiers, post-partition pour cette cellule)
        public WeightLayerSet Weights;
        public byte[] ChannelAtlasBlob;        // atlas cuit sérialisé (ou null si non généré)
        public float2 ChannelTexcoordMetrics;
        public ChannelTable ChannelTable;
        public GridDimensions Dims;            // pour positionner le root
        public void Dispose() { Mesh.Dispose(); Weights?.Dispose(); }
    }

    /// Handle opaque retourné par un presenter ; sert à libérer.
    public interface ISectionHandle { int3 Coord { get; } }

    /// Transforme une CookedSection en présentation (GO, entités, …) et la libère.
    public interface ISectionPresenter
    {
        /// Présente la section. Synchrone et léger (instantiate, pas cook).
        ISectionHandle Present(CookedSection cooked, Transform root);
        /// Libère TOUTES les ressources de la section (voir §9 garanties mémoire).
        void Release(ISectionHandle handle);
    }
}
```

**Presenter par défaut (Phase 5)** : `GameObjectSectionPresenter` — délègue à `SectionCompiler.Compile`
(réutilise tel quel LOD/skirt/collision/atlas, cf. `Runtime/Sections/SectionCompiler.cs`), retourne un
handle enveloppant le `CompiledSection`. `Release` appelle `CompiledSection.Dispose()` (qui détruit déjà
GO + meshes + texture, cf. Phase 4).

**Presenter futur (opt-in, hors Phase 5)** : `EcsSubscenePresenter` — pour le scénario *frozen/baked*
haut volume, lit le **même** `CookedSection`/blob disque et émet des entités (export subscene hors-ligne).
N'est jamais une dépendance du cœur. **Le blob disque cuit est la monnaie universelle** : choisir le
custom maintenant ne gâche aucun travail si ECS arrive plus tard, car la partie chère (le cook) est
partagée.

> **Décision d'agnosticisme** : le cœur (`Controller`/`Queue`/`Cache`/`Cooker`) ne référence **jamais**
> `UnityEngine.GameObject`/`MeshRenderer` ni `Unity.Entities`. Seuls les presenters concrets le font.

---

## 5. Coordonnées, résidence & politique de streaming

### 5.1 La clé de streaming

La clé est la **coordonnée absolue `int3`** de la cellule — déjà fournie, stable et ancrée monde, par
`GridDimensions` (`Runtime/Partition/GridDimensions.cs`) : `OriginCoord + localCoord`, snap ancré
(`ComputeGridDimensions`). C'est le rôle que joue la cell coord de World Partition chez UE. **Aucune
nouvelle math de grille** — on réutilise `AbsoluteCoord`, `CellMin`, `CellCenter`.

> En mode `Is2D` (terrain), `coord.y == 0` toujours ; l'anneau de streaming est 2D (X/Z).

### 5.2 Calcul de l'ensemble désiré

Chaque frame (ou quand le focus a bougé de plus de Δ) :

```
focusCellCoord = floor((focusWorld - anchor) / cellSize)   // même snap que ComputeGridDimensions
desired = { coord : ringDistance(coord, focusCellCoord) <= loadRadiusCells }
```

`ringDistance` = distance de Chebyshev (carré) ou euclidienne (disque) sur (x,z). `loadRadiusCells`
dérivé d'une distance monde / `CellSize`.

### 5.3 Hystérésis (anti-thrash)

Deux rayons : `loadRadius < unloadRadius`. On **charge** dès qu'une cellule entre dans `loadRadius`, on
**décharge** seulement quand elle sort de `unloadRadius`. La bande entre les deux empêche le
load/unload oscillant à la frontière quand le focus tremble.

### 5.4 État de résidence

```csharp
enum SectionState { Queued, Cooking, Presenting, Ready }
sealed class ResidentSection
{
    public int3 Coord;
    public SectionState State;
    public ISectionHandle Handle;     // non-null quand Ready
    public JobHandle? PendingJobs;    // travail Burst/async en vol
    public int Generation;            // pour annuler un load obsolète si évincé pendant le cook
}
Dictionary<int3, ResidentSection> _resident;
```

Diff par-frame : `desired \ resident` → enfile **load** ; `resident \ desiredWithHysteresis` → enfile
**unload**. Un load encore `Queued`/`Cooking` au moment où il sort de portée est **annulé** (compare
`Generation`) pour ne pas cuire dans le vide.

---

## 6. Build borné par cellule (`ProcessCell`)

C'est **la** pièce nouvelle algorithmiquement (UE ne build pas une cellule isolée au runtime). Objectif :
produire le `MeshData` d'**une** cellule sans reconstruire le monde.

### 6.1 Problème

`ModifierGroup.Process` actuel (`Runtime/Modifiers/ModifierGroup.cs`) produit le mesh **continu entier**,
puis `MeshPartitioner.Partition` le découpe en N sections. Pour le streaming on veut produire **une**
section pour un `coord` donné, à la demande.

### 6.2 Approche

Ajouter un point d'entrée borné qui réutilise l'évaluation de modifiers existante :

```csharp
// ModifierGroup (nouveau)
public static ModifierResult ProcessCell(
    IEnumerable<ModifierComponent> modifiers,
    in GridSettings grid,
    in GridDimensions dims,
    int3 absoluteCoord,
    float cellMargin,            // marge pour jitter centroïde + skirt overlap
    float4x4 meshToWorld,
    Allocator allocator);
```

Étapes :

1. **Bounds de la cellule + marge** : `cellBounds = [dims.CellMin(local), CellMin(local)+CellExtent]`
   élargi de `cellMargin` sur X/Z (et plein Y en 2D). La marge couvre (a) les triangles dont le
   centroïde tombe dans la cellule mais dont des vertices débordent, (b) le ruban de skirt.
2. **Base modifier borné** : le base produit la géométrie **seulement** dans `cellBounds+marge`.
   - `RectangleBaseModifier`/heightmap : trivial — ne générer que la sous-grille de la cellule.
   - Base mesh arbitraire : interroger l'AABB du source par `cellBounds` (broad-phase, comme le
     `BuildSections` d'UE, mais paresseux par cellule). *(Différé si le seul base au départ est le
     rectangle/heightmap ; flag explicite.)*
3. **Stack** : appliquer les modifiers non-base via `MeshView` bornés à `cellBounds` (l'API
   `GetInstancesInBounds(cellBounds, …)` existe déjà sur `IModifierJob`).
4. **Partition d'une cellule** : appeler `MeshPartitioner.Partition` sur ce petit mesh avec la **même**
   `GridSettings`/ancre, puis prendre la section dont `SectionCoords[i] == absoluteCoord`. Comme
   l'ancre est stable, l'assignation par centroïde produit **exactement** les triangles que produirait
   un build complet pour cette cellule (déterminisme du centroïde, cf. `02 §4.2`).

### 6.3 Invariant de cohérence inter-cellules (le point délicat)

> **Build borné de la cellule C == cette même cellule extraite d'un build complet du monde.**

Garanti par : (a) ancre de grille **fixe** (pas ancrée sur les bounds, cf. `GridSettings.WorldOriginOffset`)
→ coordonnées stables ; (b) assignation **par centroïde déterministe** (le plus petit index gagne,
`02 §4.2`) → pas de triangle dupliqué/perdu entre voisins ; (c) **marge** suffisante pour que tout
triangle assigné à C soit présent dans le build borné. La couture **channel** reste la limitation connue
documentée (`ROADMAP_TRACKING.md` Phase 4) — atlas par-section indépendant.

**Test de non-régression obligatoire (golden)** : `ProcessCell(C)` doit être bit-équivalent (ou
ε-équivalent sur positions) à la section `C` d'un `Process` + `Partition` complet sur la même entrée.
Voir §14.

---

## 7. Le cache : invalidation par hash & format disque

Porte le modèle `FCompiledSectionBuildInfo` d'UE (`02 §9`, `MeshPartitionCompiledSection.h`) presque
verbatim. C'est aussi le livrable « build incrémental » de la Phase 5.

### 7.1 La clé (`SectionKey`)

```csharp
readonly struct SectionKey : IEquatable<SectionKey>
{
    public int3 Coord;            // GridCellCoord absolu (stable)
    public Hash128 ModifiersHash; // hash des params sérialisés de TOUS les modifiers couvrant la cellule
    public Hash128 ModifierSetHash; // hash de l'ensemble (ajout/retrait d'un modifier)
    public Hash128 VariantHash;   // Definition + LOD/skirt/channel settings + CellSize/Is2D
    public Hash128 ClassHash;     // version d'implémentation des classes de modifier (bump manuel)
    // Hash128 combiné -> nom de fichier disque
}
```

Correspondance UE → ici :

| UE `FCompiledSectionBuildInfo` | Ici |
|---|---|
| `GridCellCoord` | `Coord` |
| `ModifiersHash` | `ModifiersHash` |
| `ModifierSetHash` | `ModifierSetHash` |
| `PackageHash` (assets disque) | *(différé : pas d'assets référencés au départ)* |
| `ClassHash` (`MegaMeshClassVersion`) | `ClassHash` (const par classe de modifier) |
| `BuildVariantHash` | `VariantHash` |

**Scoping de l'invalidation** : seuls les modifiers dont les `ComputeBounds()` **intersectent** la
cellule contribuent à son `ModifiersHash`. Donc éditer un modifier ne change le hash que des cellules
qu'il couvre → **miss scopé**, le reste reste hit. C'est ce qui rend « éditer = rebuild seulement les
sections touchées » vrai.

### 7.2 Deux tiers

| Tier | Contenu | Au stream-in | Éviction |
|---|---|---|---|
| **RAM LRU** | `CookedSection` + `Mesh`/atlas présentés récents | réutilise (GO poolé ré-activé) | LRU par budget mémoire |
| **Disque** | blob sérialisé `MeshData`+weights+atlas | lit octets → upload `Mesh` | jamais (sauf purge explicite) |
| **(miss)** | rien | cook complet (§6), peuple RAM+disque | — |

### 7.3 Format disque (blittable)

`MeshData` est adossé à des `NativeArray` → directement blittable. Pas de sérialisation Unity.

```
[SectionBlob v1]
  magic "MTSC", version u32
  SectionKey (coord int3, 4× Hash128)
  GridDimensions (snappedMin, originCoord, cellNumber, cellExtent)
  flags u32 (hasNormals, hasChannelUVs, hasSourceUV0, hasBaseIDs, hasWeights, hasAtlas)
  vertexCount u32, triangleCount u32
  float3[] Vertices         (raw)
  int3[]   Triangles        (raw)
  float3[] Normals?         (raw)
  float2[] ChannelUVs?      (raw)
  float2[] SourceUV0?       (raw)
  int[]    BaseIDLayer?     (raw)
  weightLayerCount u32, [ name(len-prefixed utf8), float[vertexCount] ]…
  atlas?  : width u32, height u32, slices u32, format u8, R8 bytes (post-cook)
          + ChannelTable (uint4), TexcoordMetrics (float2)
```

- Écriture : `FileStream` + `NativeArray.AsReadOnlySpan` → `Write`. Lecture : `Read` → `NativeArray`
  via `Allocator.Persistent`.
- Localisation : `Application.persistentDataPath/MeshTerrain/<megaMeshGuid>/<keyHash>.mtsc` au runtime
  (marche en player — ce qu'ECS/Addressables ne peuvent pas pour du runtime-gen) ; cache éditeur sous
  `Library/MeshTerrain/…` pour l'itération.
- **Versioning** : `version` du blob + `ClassHash` ⇒ un bump invalide proprement (relit comme miss).

### 7.4 Ship du cache (collapse vers le modèle UE)

Si le monde est *authored & frozen* : cuire une fois dans l'éditeur, **shipper le dossier cache** comme
data. Le player a alors **zéro cook**, identique au modèle préfabriqué d'UE. C'est le même système, on
choisit par projet si l'on pré-peuple le cache.

---

## 8. File de travail budgétée & async

Le cook est lourd ; il ne doit jamais bloquer le main thread.

- **Budget par frame** : `maxCooksInFlight` et/ou un budget temps (`maxMillisPerFrame`). Au-delà, les
  loads restent `Queued`.
- **Async** : les jobs Burst de partition (`PartitionJobs`) et la raster GPU channel
  (`ChannelRasterizerGPU`) tournent hors main thread / sur le GPU ; le presenter finalise le GO quand
  le `JobHandle`/le travail GPU est complété (poll non-bloquant via `JobHandle.IsCompleted`).
- **Priorité** : trier la file par distance au focus (les plus proches d'abord). Optionnel : prédiction
  le long du vecteur vitesse caméra pour **pré-warm** (cuire en avance, priorité basse).
- **Phasage de l'instantiate** : même un hit doit étaler `MeshCollider.sharedMesh` (cook PhysX, ms-scale)
  — au plus K colliders assignés par frame.

> Parallèle UE : « charger de grandes scènes prend plusieurs frames ; tout le chargement est async ».

---

## 9. Cycle de vie & garanties mémoire

Trois systèmes mémoire doivent **tous** se libérer à l'unload. C'est une exigence de correction, pas un
détail — voir les fuites déjà rencontrées dans `MeshTerrainDemo`.

| Système | Détenu par | Libéré par | Piège |
|---|---|---|---|
| **NativeArray** (`MeshData`/`WeightLayerSet`) | `CookedSection` / cache RAM | `Dispose()` explicite | non-GC : fuite mémoire native si oublié |
| **UnityEngine.Object** (`Mesh`, `RenderTexture`/`Texture2DArray`, `Material`) | `CompiledSection` (via presenter) | `Destroy`/`DestroyImmediate` (+ `RenderTexture.Release()`) | détruire le GO ne libère **pas** le Mesh/Texture |
| **Références managées** | `Dictionary<int3, ResidentSection>`, MPB poolés | retirer l'entrée + drop refs | une seule ref pendante épingle tout le chunk |

**Contrat d'éviction** :

```csharp
void Evict(int3 coord)
{
    var r = _resident[coord];
    _resident.Remove(coord);              // (c) drop la ref managée
    if (r.Handle != null)
        _presenter.Release(r.Handle);     // (b) Destroy Mesh+RT+collider+GO  (CompiledSection.Dispose)
    _cache.OnEvicted(coord);              // (a) Dispose les NativeArray RAM si retenus ; blob disque conservé
}
```

**Garantie « totalement hors RAM »** — vérifiable, pas supposée :
- Le `CompiledSection.Dispose()` existant détruit déjà GO + meshes possédés + `ChannelTexture` (Phase 4).
- `ChannelRasterResult`/`RenderTexture` : `.Release()` puis `Destroy` (déjà fait Phase 4).
- **Diagnostic intégré** : compteur de `Mesh`/`Texture`/`NativeArray` vivants ; après load→unload+1 frame,
  retour à la baseline. Test PlayMode dédié (§14) + snapshot Memory Profiler manuel.
- Détecteur de fuite Collections (`Allocator.Persistent`) actif en dev : aboie au quit si un
  `NativeArray` n'a pas été disposé.

> **Honnêteté vs ECS** : ici « totalement libéré » est **notre** responsabilité (classe de bug à garder
> active à chaque évolution du chemin d'éviction). `SceneSystem.UnloadScene` d'ECS libère les chunks
> d'entités tout seul — fuite-by-construction plus difficile. C'est un avantage réel d'ECS, payé par la
> perte du runtime-gen.

**Pooling** : un pool de `GameObject`/`MeshFilter`/`MeshRenderer`/`MeshCollider` pour que
load→unload→load d'une frontière mobile ne génère pas de churn GC. Le pool **réinitialise** (clear MPB,
sharedMesh = null) à la remise pour ne pas épingler un Mesh/Texture évincé.

---

## 10. Far-field / anti-pop

Le streaming par distance laisse un trou : une cellule juste hors `loadRadius` disparaît → pop. UE le
résout via `FarFieldMesh`/impostors (`MeshPartitionFarFieldTransformer.h`). Version minimale Phase 5 (ou
différée) :

- Conserver un **mesh far-field grossier** (un LOD très bas par section, ou une tuile basse résolution
  mergée par super-cellule) résident sur un rayon plus large, échangé contre la section streamée
  complète quand elle entre dans `loadRadius`.
- Le far-field est lui-même un `CookedSection` (LOD le plus bas) → réutilise tout le pipeline ; juste un
  presenter/rayon distinct. **Différable** : pas requis pour la v1 fonctionnelle.

---

## 11. Scénarios supportés

| Scénario | Couvert ? | Comment |
|---|---|---|
| **Génération procédurale au runtime** (mondes infinis/seedés/player-built) | ✅ **unique** | cook-on-miss → cache `persistentDataPath`. Impossible avec ECS/Addressables. |
| **Éditeur authored, frozen, ship préfabriqué** | ✅ | cuire dans l'éditeur, **shipper le cache** → zéro cook au player (= modèle UE). Optionnel : presenter ECS opt-in. |
| **Itération éditeur sur monde large** | ✅ | cache rend les ré-éditions incrémentales (miss scopé sur modifier touché). |
| **Intégration rendu** | ✅ | presenter GO par défaut → `MeshRenderer`/URP, marche dans tout projet. ECS = opt-in. |

**Décision sur l'« everything everywhere » (crainte de complexité)** : **un seul backend (custom
GameObject)** est livré en Phase 5. Le seam `ISectionPresenter` rend ECS **ajoutable plus tard sans
toucher au cœur**, pour le seul scénario frozen haut-volume, comme module opt-in. On ne paie la
complexité ECS (double rendu, dépendance Entities, double surface de test) **que si** un projet réel
l'exige. Le cache cuit est partagé entre backends → aucun travail gâché.

---

## 12. API publique proposée

```csharp
namespace Fca.MeshTerrain.Streaming
{
    // MonoBehaviour orchestrateur (le seul point d'entrée scène).
    public sealed class MeshTerrainStreamer : MonoBehaviour
    {
        public MeshPartitionDefinition Definition;
        public Transform Focus;               // caméra/joueur ; défaut = Camera.main
        public float LoadDistance = 600f;     // monde ; -> loadRadiusCells
        public float UnloadDistance = 800f;   // hystérésis
        public int   MaxCooksInFlight = 2;
        public float MaxMillisPerFrame = 4f;
        public float CellMargin = 0f;          // 0 => défaut dérivé du skirt
        public bool  UseComputeChannels = true;

        // Stack modifiers fourni en code (Phase 5) ; éditeur/MonoBehaviour modifiers = Phase 6.
        public void SetModifierStack(IReadOnlyList<ModifierComponent> stack);

        // Pilotage manuel optionnel.
        public void ForceLoad(int3 coord);
        public void ForceUnloadAll();
        public void InvalidateModifier(ModifierComponent m); // -> miss scopé sur ses cellules
    }

    public static class SectionCooker
    {
        public static CookedSection Cook(
            IReadOnlyList<ModifierComponent> stack, in GridSettings grid,
            in GridDimensions dims, int3 coord, float cellMargin,
            ChannelCookOptions channels, Allocator allocator);
    }

    public interface ISectionCache
    {
        bool TryGet(in SectionKey key, out CookedSection cooked);  // RAM puis disque
        void Put(in SectionKey key, CookedSection cooked);          // RAM + disque
        void OnEvicted(int3 coord);                                 // libère RAM, garde disque
        void Purge();                                               // efface le disque (rebuild)
    }

    public interface ISectionPresenter { /* §4 */ }
    public sealed class GameObjectSectionPresenter : ISectionPresenter { /* SectionCompiler */ }
}
```

Réutilisé tel quel (couplage Mesh Terrain) :
`GridDimensions`, `GridSettings`, `MeshData`, `WeightLayerSet`, `MeshPartitioner.Partition`,
`ModifierGroup` (+ nouveau `ProcessCell`), `SectionCompiler`/`CompiledSection`, `ChannelRasterizer*`,
`ChannelPacking`.

---

## 13. Découpage en jalons

Chaque jalon livre quelque chose de testable, suit le workflow projet (branche → dev → demo → tests →
validation humaine → commit/PR/merge). Sous-PR pour limiter la taille.

| Jalon | Contenu | Done quand |
|---|---|---|
| **5.0 ProcessCell** | Build borné d'une cellule (`ModifierGroup.ProcessCell`) + golden test vs build complet | Une cellule isolée == sa cellule d'un build complet (ε). |
| **5.1 CookedSection + Cache disque** | `CookedSection`, sérialisation blob, `SectionCache` (RAM LRU + disque), `SectionKey`/hash | Cuire→écrire→relire reproduit la section ; clé stable. |
| **5.2 Presenter seam** | `ISectionPresenter` + `GameObjectSectionPresenter` (extrait de l'usage SectionCompiler) | Présenter un `CookedSection` == sortie `SectionCompiler` actuelle. |
| **5.3 Streamer** | `MeshTerrainStreamer` : résidence, hystérésis, file budgétée, éviction | Déplacer le focus load/unload sans hitch ni fuite. |
| **5.4 Invalidation incrémentale** | Hash scopé par bounds modifier ; `InvalidateModifier` → miss ciblé | Éditer un modifier ne rebuild que ses cellules. |
| **5.5 Far-field (opt.)** | LOD grossier résident large rayon, swap à l'entrée | Pas de pop visible à la frontière de chargement. |

Demo : mode `Phase5_Streaming` dans `MeshTerrainDemo` — focus déplaçable, gizmos de l'anneau
load/unload, compteur de sections résidentes + hits/miss cache + sections vivantes (diagnostic mémoire).

---

## 14. Tests & vérification

**EditMode (déterministe, sans GPU)** :
- `ProcessCell_MatchesFullBuild` — golden : cellule bornée == cellule du build complet (positions ε,
  même set de triangles). **Le test garde-fou central.**
- `SectionBlob_RoundTrips` — cuire → sérialiser → désérialiser == original (geo + weights + atlas table).
- `SectionKey_StableForUnchangedInputs` / `ChangesWhenModifierEdited` (scopé bounds).
- `Cache_HitAvoidsCook` — un 2ᵉ accès ne rappelle pas le cooker (compteur de cooks).
- `DesiredSet_RingAndHysteresis` — math d'anneau + bande d'hystérésis correcte.

**PlayMode (GPU/scene, skip si non supporté)** :
- `Streamer_LoadUnload_NoLeak` — après N cycles load/unload : compteurs `Mesh`/`Texture`/`NativeArray`
  reviennent à baseline (+1 frame). **Le test garde-fou mémoire.**
- `Streamer_BudgetNeverStalls` — temps main thread/frame sous seuil pendant un balayage du focus.
- `Cache_ShippedWorld_ZeroCooks` — cache pré-peuplé → aucun cook au runtime.

**Manuel** : snapshot Memory Profiler avant/après un cycle ; détecteur de fuite Collections au quit.

---

## 15. Risques & questions ouvertes

1. **Cohérence inter-cellules du build borné** (§6.3) — *risque n°1*. Marge + déterminisme centroïde +
   ancre fixe doivent garantir l'équivalence avec le build complet. Gardé par le golden test 5.0. La
   couture **channel** reste la limitation connue (atlas par-section), à traiter au shader de prod.
2. **Discipline mémoire** (§9) — fuites possibles à chaque évolution du chemin d'éviction. Gardé par le
   test de non-fuite PlayMode + diagnostic intégré. *Désavantage assumé vs ECS.*
3. **Coût d'instantiate GameObject** — création GO/component + cook PhysX du `MeshCollider` plus lourd
   qu'un chargement de chunk ECS. Mitigé par pooling + phasage des colliders. Reste plus lourd qu'ECS au
   pur streaming → ECS resterait préférable pour le *scénario frozen haut-volume* (d'où le seam).
4. **Hash scopé par bounds** — un modifier global (bounds infinis) invaliderait tout. Acceptable
   (édition d'un base global = rebuild global) ; documenter.
5. **Périmètre du base borné** (§6.2) — facile pour rectangle/heightmap ; non-trivial pour un base mesh
   arbitraire (requête AABB par cellule). Différer le base mesh arbitraire ; flag explicite.
6. **`ProcessCell` partitionne une cellule via le partitionneur global** — vérifier que partitionner un
   petit mesh borné avec la même ancre ne réintroduit pas de coût O(monde). Sinon, chemin direct
   « assign + extract single cell » sans le bucket complet.

### Questions à trancher avant 5.0
- **Stack de modifiers** : fourni en code (Phase 5) ou déjà via MonoBehaviour/éditeur ? (Impacte d'où
  vient `ModifiersHash`.) — *proposé : code en 5.x, éditeur en Phase 6.*
- **`CellMargin` par défaut** : dériver du `MeshSkirtSettings.Width`/`PushDown` + une marge de jitter
  centroïde (≈ une arête max de triangle). À mesurer.
- **Far-field** : in-scope 5.5 ou Phase 5.bis ? — *proposé : optionnel, après le streamer fonctionnel.*
```
