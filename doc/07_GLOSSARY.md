# 07 — Glossaire UE → Unity

Vocabulaire, types et équivalences pour traduire le code Unreal de ce dossier en Unity C#.

## Termes du système

| Terme UE | Signification | Équivalent / note Unity |
|---|---|---|
| **MegaMesh** | Nom de code interne du système | (concept) — ta classe racine `MeshPartition` |
| **Mesh Partition** | Nom du plugin = le système | — |
| **Mesh Terrain** | Nom commercial/doc | — |
| **Section** | Morceau du mesh = 1 cellule de grille | GameObject + Mesh + collider |
| **Compiled Section** | Section cuite (résultat final) | Asset/prefab de section streamable |
| **Channel** | Couche de poids scalaire par-vertex (matériau/masque) | `NativeArray<float>` par channel + atlas |
| **Modifier** | Opération non-destructive bornée | `ModifierComponent` + `IModifierJob` |
| **Base modifier** | Modifier qui produit la géométrie initiale | `IsBase() == true` |
| **Priority Layer** | Couche d'ordre des modifiers | `int priorityLayerIndex` |
| **Transformer** | Étape post-modifiers du pipeline | étape de build (méthode/job) |
| **Build Variant** | Config de build per-platform | `ScriptableObject` de variante |
| **Definition** | Config globale partagée | `ScriptableObject` `MeshPartitionDefinition` |
| **Skirt** | Jupe anti-crack au bord de section | géométrie de bandeau vertical |
| **DDC** | Derived Data Cache (cache disque de build) | cache disque maison |
| **World Partition** | Système de streaming spatial d'UE | Addressables / scènes additives |
| **Nanite** | Virtualized geometry (rendu) | LODGroup + LODs (cf. `04`) |
| **RVT** | Runtime Virtual Texture | (optionnel, pas d'équivalent direct) |
| **HLOD** | Hierarchical LOD | impostors / far-field mesh |

## Types & API

| Type/API UE | Rôle | Unity |
|---|---|---|
| `FMeshData` | Format mesh pivot | `MeshData` custom (`NativeArray`) |
| `FDynamicMesh3` | Mesh half-edge complet | `Mesh` ou structure custom |
| `FDynamicMeshAttributeSet` | Attributs du DynamicMesh | UV/normals/weight buffers |
| `UStaticMesh` | Asset mesh | `Mesh` |
| `UStaticMeshComponent` | Composant rendu | `MeshFilter` + `MeshRenderer` |
| `AActor` | Entité de scène | `GameObject` |
| `UActorComponent` / `USceneComponent` | Composant | `MonoBehaviour` / `Component` |
| `UPrimitiveComponent` | Composant rendu/collision | `Renderer` / `Collider` |
| `UDataAsset` | Asset de données | `ScriptableObject` |
| `FTransform` | Transform | `Matrix4x4` / `Transform` |
| `FBox` | AABB | `Bounds` |
| `FOrientedBox3d` | OBB | OBB custom |
| `FVector` (double) | Vecteur 3D | `double3` / `Vector3` |
| `FVector3f` / `FVector2f` | Vecteur float | `float3` / `float2` |
| `FIndex3i` | Triplet d'indices | `int3` |
| `TArray<T>` | Liste dynamique | `List<T>` / `NativeArray<T>` |
| `TMap<K,V>` | Dictionnaire | `Dictionary` / `NativeParallelHashMap` |
| `TSet<T>` | Ensemble | `HashSet` / `NativeParallelHashSet` |
| `FName` | Nom interné | `string` (managé) / `int` index (jobs) |
| `FGuid` | Identifiant | `System.Guid` / `Hash128` |
| `ParallelFor` | Boucle parallèle | `IJobParallelFor` (Burst) |
| `UE::Tasks::FTask` | Tâche async | `JobHandle` / `Task` |
| `TMeshAABBTree3` | Arbre AABB | BVH custom / `NativeArray` |
| `FBlake3Hash` | Hash de cache | `Hash128` / hash maison |
| Compute/Pixel shader `.usf` (RDG) | Shader GPU | `.compute` / shader + `CommandBuffer` |
| `UMaterialInterface` | Matériau | `Material` |
| Custom Primitive Data | Données per-instance pour le matériau | `MaterialPropertyBlock` |
| `UPhysicalMaterial` | Matériau physique | `PhysicMaterial` |
| `FMeshDescription` | Format d'échange mesh | `Mesh.MeshData` API |

## Macros & conventions UE (à ignorer/traduire)

| Élément UE | Quoi en faire |
|---|---|
| `UCLASS()` / `USTRUCT()` / `UPROPERTY()` / `UFUNCTION()` | Réflexion UE → ignorer ; en Unity : champs sérialisés `[SerializeField]`. |
| `GENERATED_BODY()` | Boilerplate UE → ignorer. |
| `UE_API` / `MESHPARTITION_API` | Macros d'export DLL → ignorer. |
| `TObjectPtr<T>` / `TWeakObjectPtr<T>` | Pointeurs gérés UE → références C# / `WeakReference`. |
| `TSharedPtr<T>` / `MakeShared<T>` | Smart pointers → objets C# (GC). |
| `check()` / `ensure()` / `checkSlow()` | Assertions → `Debug.Assert` / `UnityEngine.Assertions`. |
| `WITH_EDITOR` / `WITH_EDITORONLY_DATA` | Code éditeur → `#if UNITY_EDITOR`. |
| `TRACE_CPUPROFILER_EVENT_SCOPE` | Profiling → `using (new ProfilerMarker(...).Auto())`. |
| `namespace UE::MeshPartition` | Namespace → `namespace YourGame.MeshPartition`. |
| `friend class` | Accès privé → `internal` / accès direct en Unity. |

## Conventions de préfixe Unreal (pour lire les sources)

- `F` = struct/classe non-UObject (`FMeshData`, `FGridSettings`).
- `U` = UObject/Component (`UModifierComponent`, `UMeshPartitionDefinition`).
- `A` = Actor (`AMeshPartition`, `ACompiledSection`).
- `I` = interface (`IModifierBackgroundOp`, `IDependencyInterface`).
- `E` = enum (`EMeshViewComponents`, `ENoiseModifierType`).
- `T` = template (`TArray`, `TMap`, `TObjectPtr`).
- `b` (préfixe de membre) = booléen (`bUseNanite`, `bIs2D`).
- Préfixe `In`/`Out` sur les paramètres = entrée/sortie.
