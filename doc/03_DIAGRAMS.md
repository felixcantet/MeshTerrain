# 03 — Diagrammes

Diagrammes en **Mermaid** (rendus par la plupart des viewers Markdown / GitHub / IDE) avec un repli ASCII pour
les schémas de flux. Pour un agent IA, le Mermaid est directement parsable.

---

## 3.1 Diagramme de classes — Données & runtime

```mermaid
classDiagram
    class AMeshPartition {
        +UMeshPartitionComponent Component
        +UMeshPartitionDefinition Definition
        +WorldToLocal(Box) Box
        +LocalToWorld(Box) Box
    }
    class UMeshPartitionDefinition {
        +UMaterialInterface Material
        +FChannelMap ChannelMap
        +FCompiledSectionBuildVariant[] BuildVariants
        +FName[] ModifierTypePriorities
        +float ChannelTexelSize
    }
    class FCompiledSectionBuildVariant {
        +double MaxSectionComplexity
        +UTransformerPipeline TransformerPipeline
        +FName Name
    }
    class ACompiledSection {
        +UStaticMeshComponent[] MeshComponents
        +UCollisionComponent[] CollisionComponents
        +UTexture ChannelTexture
        +uint8[] ChannelTable
        +FCompiledSectionBuildInfo BuildInfo
        +FIntVector GridCellCoord
    }
    class FMeshData {
        +Vector3d[] Vertices
        +Index3i[] Triangles
        +Vector3f[] Normals
        +Map~Name,float[]~ WeightLayers
        +int[] BaseIDLayer
        +Vector2f[] ChannelUVs
        +ConvertToDynamicMesh()
        +ConvertToTriMeshCollisionData()
    }
    class FChannelMap {
        +FChannelDesc[] ChannelDescs
        +FindChannel(Name) int
    }
    class FGridSettings {
        +uint32 CellSize
        +bool bIs2D
        +Vector3 WorldOriginOffset
    }

    AMeshPartition "1" --> "1" UMeshPartitionDefinition
    AMeshPartition "1" --> "N" ACompiledSection : produces
    UMeshPartitionDefinition "1" --> "N" FCompiledSectionBuildVariant
    UMeshPartitionDefinition "1" --> "1" FChannelMap
    FCompiledSectionBuildVariant --> UTransformerPipeline
    ACompiledSection ..> FMeshData : built from
    ACompiledSection ..> FGridSettings : positioned by
```

---

## 3.2 Diagramme de classes — Modifier stack (non-destructif)

```mermaid
classDiagram
    class UModifierComponent {
        <<abstract>>
        +ComputeBounds() Box[]
        +GetType() Name
        +GetPriority() double
        +GetComplexity() double
        +IsBase() bool
        +CreateBackgroundOp(buildType) IModifierBackgroundOp
        +GatherDependencies(deps)
    }
    class IModifierBackgroundOp {
        <<interface>>
        +GetInstancesInBounds(box, out instances)
        +ApplyModifications(FMeshView, transform, instance)
        +DisableDDCWrite() bool
    }
    class FInstanceInfo {
        +Box Bounds
        +int InstanceID
        +EMeshViewComponents ReadViewComponents
        +EMeshViewComponents WriteViewComponents
        +Name[] UsedChannels
    }
    class FMeshView {
        +VertexCount() int
        +GetVertexPos(i) Vector3d
        +SetVertexPos(i, p)
        +SetVertexAttributeWeight(channel, i, v)
        -bounds : Box
        -readMask, writeMask : EMeshViewComponents
    }
    class UEditableModifierBase {
        +ApplyEditWithMesh(DynamicMesh)
        +PrepareForEdit(DynamicMesh)
    }
    class UNoiseModifier
    class URemeshModifier
    class USplineModifier
    class UMeshProjectModifier
    class UWeightUtilityModifier

    UModifierComponent <|-- UEditableModifierBase
    UModifierComponent <|-- UNoiseModifier
    UModifierComponent <|-- UWeightUtilityModifier
    UEditableModifierBase <|-- URemeshModifier
    UEditableModifierBase <|-- USplineModifier
    UEditableModifierBase <|-- UMeshProjectModifier
    UModifierComponent ..> IModifierBackgroundOp : creates
    IModifierBackgroundOp ..> FInstanceInfo : produces
    IModifierBackgroundOp ..> FMeshView : mutates (bounded)
    FInstanceInfo --> FMeshView : configures masks
```

---

## 3.3 Diagramme de séquence — Build d'une zone

```mermaid
sequenceDiagram
    participant WU as WorldUpdater
    participant MB as FMeshBuilder
    participant MG as ModifierGroup
    participant GR as GridHelpers
    participant TR as Transformers
    participant CS as ACompiledSection

    WU->>WU: compare hashes (modifiers/package/class/variant)
    WU->>MB: LaunchBuilds(sections out-of-date)
    MB->>MG: ProcessModifierGroup (trié par priority)
    loop pour chaque modifier
        MG->>MG: CreateBackgroundOp()
        MG->>MG: GetInstancesInBounds(zone)
        MG->>MG: ApplyModifications(FMeshView borné)
    end
    MG-->>MB: FMeshData continu
    MB->>GR: BuildGridCellMeshes (assignation par centroïde)
    GR-->>MB: N FMeshData (par cellule)
    MB->>TR: pipeline (Subsection → StaticMesh → Skirt → Channels → Collision)
    TR->>TR: SubsectionTransformer (split si > MaxComplexity)
    TR->>TR: StaticMeshTransformer (LOD quadric / Nanite)
    TR->>TR: SkirtTransformer (anti-crack)
    TR->>TR: Channels (GPU rasterize + pull-push)
    TR->>CS: crée/met à jour ACompiledSection
    CS-->>WU: section à jour (hash stocké)
```

---

## 3.4 Flux de données du pipeline (ASCII)

```
  [Heightmap.png]   [Mesh stamp]   [Spline]            ← ENTRÉES (base modifiers)
        \               |            /
         \              |           /
          v             v          v
       ┌────────────────────────────────┐
       │   BASE FMeshData (continu)      │
       └───────────────┬────────────────┘
                       │  MODIFIER STACK (priority layers)
        ┌──────────────┼──────────────┐
        v              v              v
   [Noise]        [Spline road]   [Weight paint]      ← chaque modifier borné (FMeshView)
        └──────────────┼──────────────┘
                       v
       ┌────────────────────────────────┐
       │   FMeshData modifié (continu)   │
       └───────────────┬────────────────┘
                       │  PARTITION (centroïde → cellule)
        ┌──────────┬───┴────┬──────────┐
        v          v        v          v
     cell(0,0)  cell(1,0) cell(0,1)  cell(1,1)         ← N FMeshData
        │          │        │          │
        │  (chaque cellule, en parallèle :)
        v          v        v          v
   ┌─────────────────────────────────────────┐
   │ C1 Channels → atlas texture (GPU)        │
   │ C2 LOD quadric + Skirt → UStaticMesh     │
   │ C3 Tri-mesh collision + physical mat     │
   └────────────────────┬────────────────────┘
                        v
            ACompiledSection × N  (streamés)
```

---

## 3.5 Mapping conceptuel UE → Unity (vue d'ensemble)

```mermaid
flowchart LR
    subgraph UE[Unreal Engine]
        A1[AMeshPartition] 
        A2[UModifierComponent]
        A3[FMeshData]
        A4[ACompiledSection]
        A5[World Partition streaming]
        A6[Nanite + fallback LOD]
        A7[Channel atlas - Compute Shader]
    end
    subgraph UN[Unity]
        B1[MeshPartition MonoBehaviour/ScriptableObject]
        B2[Modifier MonoBehaviour + IModifierJob]
        B3[NativeArray mesh buffers + Burst]
        B4[Section asset - Mesh + Texture + collider]
        B5[Addressables / scènes additives]
        B6[LODGroup - mesh LODs]
        B7[Channel atlas - Compute Shader + Blit]
    end
    A1 --> B1
    A2 --> B2
    A3 --> B3
    A4 --> B4
    A5 --> B5
    A6 --> B6
    A7 --> B7
```
