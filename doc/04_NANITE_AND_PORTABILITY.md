# 04 — Nanite & portabilité vers Unity

## La question

> *"D'après la doc, le plugin est lié à Nanite. Est-il possible de le porter vers Unity qui ne dispose pas d'un
> système similaire ?"*

## Réponse courte

**OUI, totalement. Nanite n'est pas un obstacle.** Nanite est un détail de **rendu runtime**, optionnel dans ce
système, et il existe déjà un chemin **sans Nanite** (fallback LOD) intégré au pipeline. L'algorithmie d'authoring
et de partition est **100 % indépendante de Nanite**.

---

## Preuve dans le code

📄 `source/MeshPartition/Source/MeshPartitionEditor/Public/MeshPartitionStaticMeshTransformer.h`

```cpp
UPROPERTY(EditAnywhere, Category = "Nanite")
bool bUseNanite = true;                              // ← peut être false

UPROPERTY(..., EditCondition = "bUseNanite", DisplayName = "Generate Fallback Mesh")
ENaniteGenerateFallback NaniteFallbackMode = ENaniteGenerateFallback::Enabled;  // ← fallback EXISTE

UPROPERTY(..., EditCondition = "bUseNanite && NaniteFallbackTarget == PercentTriangles", ...)
float NaniteFallbackPercentTriangles = 0.2f;
```

Et la struct `FMeshPartitionTransformerSimplificationSettings` + `FLODSettings` définissent une **chaîne de LODs
classiques** générée par simplification quadric. Cette chaîne est :
- le **fallback** quand Nanite est activé (pour les plateformes sans support Nanite, le ray-tracing, etc.) ;
- les **LODs runtime** quand `bUseNanite = false`.

**Conclusion : le système génère toujours une géométrie LOD classique. Nanite ne fait que la remplacer au runtime
par son propre streaming de clusters.** L'algorithme qui produit les triangles est identique dans les deux cas.

---

## Quel problème Nanite résout-il ici, et comment Unity le résout autrement

| Rôle de Nanite dans Mesh Partition | Pourquoi c'est nécessaire | Équivalent Unity |
|---|---|---|
| Afficher des sections à très haute densité de triangles sans coût de LOD manuel | Un terrain MegaMesh peut faire des dizaines de millions de triangles | **LODGroup** par section + les LODs quadric déjà générés |
| Sélection de détail continue (cluster-level) | Évite le "LOD popping" | Cross-fade LODGroup (discret mais acceptable) ; les **skirts** masquent les transitions |
| Streaming fin de la géométrie | Charger seulement le détail visible | Streaming **par section** : Addressables / scènes additives. C'est déjà ce que fait World Partition. |

**Le vrai mécanisme de scalabilité du système n'est PAS Nanite — c'est le partitionnement spatial + le streaming
par section.** Or, ça, Unity le fait nativement. Nanite n'est qu'un bonus de densité intra-section.

---

## Stratégie de rendu en Unity (3 niveaux, du plus simple au plus avancé)

### Niveau 1 — Sections + LODGroup (recommandé pour démarrer) ✅
- Chaque `ACompiledSection` → un GameObject avec `MeshRenderer` + `MeshFilter` + `LODGroup`.
- Les LODs quadric (déjà dans le pipeline) alimentent le `LODGroup`.
- Streaming par section via **Addressables** ou `SceneManager.LoadSceneAsync(Additive)`.
- **Couvre 90 % du besoin.** Pas de Nanite, pas de compute exotique.

### Niveau 2 — Streaming de mesh par tuiles + impostors lointains
- Ajouter un `FarFieldMesh` (le pipeline UE en a un : `FarFieldTransformer`) : un mesh très simplifié pour les
  sections lointaines, ou des impostors/billboards.
- Pooling/unload agressif des sections hors-vue.

### Niveau 3 — Virtual geometry maison (si vraiment nécessaire) ⚠️
- Unity n'a pas d'équivalent Nanite natif. Des solutions tierces existent (meshlet/cluster culling via compute,
  ex. approches type "Unity Mesh Shaders" sur GPU récents, ou assets du store).
- **Déconseillé sauf besoin extrême.** Le coût d'ingénierie est énorme pour un gain marginal vs Niveau 1-2 sur la
  plupart des projets. Le partitionnement rend ce niveau rarement nécessaire.

---

## Ce qui est portable vs ce qui change

| Composant | Portable ? | Note |
|---|---|---|
| `FMeshData` (format mesh) | ✅ Direct | → buffers `NativeArray` Unity |
| Partition par grille (centroïde) | ✅ Direct | Pur calcul, idéal Burst |
| Modifier stack + FMeshView | ✅ Direct | Le cœur ; pattern reproductible tel quel |
| Simplification quadric (LODs) | ✅ Avec lib | UnityMeshSimplifier, ou port de l'algo quadric attribute-aware |
| Channels → atlas (GPU) | ✅ Avec compute | Compute Shader + Blit (cf. `06`) |
| Skirts anti-crack | ✅ Direct | Géométrie simple |
| Collision tri-mesh | ✅ Direct | → `MeshCollider` Unity |
| Build incrémental (hash/DDC) | ✅ À réécrire | Cache disque Unity + hashs |
| **Nanite** | 🔄 Remplacé | → LODGroup + streaming (Niveau 1) |
| World Partition | 🔄 Remplacé | → Addressables / scènes additives |
| Runtime Virtual Texture | ⚠️ Optionnel | Pas d'équivalent direct ; souvent inutile |
| PCG interop / HLOD / DataLayers | ⬜ Ignorer | Spécifique UE |

---

## Verdict

Le portage vers Unity est **réaliste et bien cadré**. Concentre l'effort sur :
1. Le **modifier stack non-destructif** (la vraie valeur, et la partie la plus subtile).
2. La **partition par grille** (simple, parallélisable).
3. Le **streaming par section** via Addressables.

Nanite se remplace par un `LODGroup` alimenté par les LODs que le pipeline génère **de toute façon**. Aucun
blocage technique. Voir la roadmap détaillée dans `05_UNITY_ROADMAP.md`.
