# Unreal Engine "Mesh Terrain" / Mesh Partition — Dossier de référence pour portage Unity

> **But de ce dossier** : fournir à un agent IA (et à un développeur) tout le contexte nécessaire pour
> **reproduire dans Unity** le système connu sous le nom commercial *"Mesh Terrain"* d'Unreal Engine 5.8,
> dont le nom de code interne est **MegaMesh** et le plugin réel **Mesh Partition**.
>
> Ce dossier est **autonome** : il contient une copie des sources Unreal concernées (pour référence)
> et une analyse complète permettant d'écrire un équivalent Unity sans avoir accès au moteur.

---

## Comment lire ce dossier

Lis les documents dans cet ordre :

| # | Fichier | Contenu |
|---|---------|---------|
| 1 | [`01_FILE_MAP.md`](01_FILE_MAP.md) | Carte de **tous** les fichiers concernés, classés par rôle et par priorité de portage. Point d'entrée pour naviguer dans `source/`. |
| 2 | [`02_SYSTEM_ANALYSIS.md`](02_SYSTEM_ANALYSIS.md) | **Analyse complète** : concepts (MegaMesh, sections, channels, modifiers), structures de données, et l'algorithmie de chaque étape du pipeline (pseudo-code portable inclus). |
| 3 | [`03_DIAGRAMS.md`](03_DIAGRAMS.md) | Diagrammes UML (classes, séquence) et schémas de flux du pipeline, en Mermaid + ASCII. |
| 4 | [`04_NANITE_AND_PORTABILITY.md`](04_NANITE_AND_PORTABILITY.md) | Le rôle exact de Nanite et **pourquoi/comment** le système est portable vers Unity (qui n'a pas Nanite). |
| 5 | [`05_UNITY_ROADMAP.md`](05_UNITY_ROADMAP.md) | **Roadmap de portage** vers Unity en phases, avec correspondances API UE↔Unity et critères de done. |
| 6 | [`06_BURST_AND_COMPUTE.md`](06_BURST_AND_COMPUTE.md) | Comment leverager le **Burst Compiler**, le **Job System**, les **Compute Shaders** et les **NativeArray** dans ce pipeline. Guide d'implémentation Unity concret. |
| 7 | [`07_GLOSSARY.md`](07_GLOSSARY.md) | Glossaire UE → Unity (vocabulaire, types, équivalences). |

Le sous-dossier [`source/`](source/) contient les **sources Unreal copiées** (référence en lecture seule) :
- `source/MeshPartition/` — le plugin complet (cœur du système).
- `source/GeometryProcessing/` — headers des dépendances géométriques (DynamicMesh3, simplification quadric, AABB tree).
- `source/MeshTerrainMode/` — le `.uplugin` + `.Build.cs` du mode éditeur (pour comprendre les dépendances ; le reste de ce plugin est de l'UI non pertinente).

---

## TL;DR — le système en 6 lignes

1. **MegaMesh** = un immense maillage continu (ex : un terrain entier) édité de façon **non-destructive**.
2. L'édition se fait via une **pile de modifiers** (sculpt, splines, noise, mesh stamps, projection…), chacun borné à une zone.
3. Au build, le mesh est **partitionné sur une grille régulière** en *sections* (assignation par centroïde de triangle).
4. Chaque section est compilée en **StaticMesh** + texture de "channels" (poids de matériaux par-vertex rastérisés) + collision.
5. **Nanite est optionnel** : un fallback LOD (simplification quadric attribute-aware) est toujours généré → le système ne dépend pas de Nanite.
6. Le streaming des sections est géré par **World Partition** (→ remplaçable par Addressables / scènes additives en Unity).

➡️ **Tout est portable vers Unity.** Voir [`04_NANITE_AND_PORTABILITY.md`](04_NANITE_AND_PORTABILITY.md) et [`05_UNITY_ROADMAP.md`](05_UNITY_ROADMAP.md).

---

## Avertissements pour l'agent IA qui utilisera ce dossier

- Les sources dans `source/` sont du **C++ Unreal Engine** : ce sont une **référence d'algorithme**, pas du code à compiler. Ne tente pas de les builder. Lis-les pour extraire la logique, puis écris du C# Unity idiomatique.
- Le vocabulaire interne est trompeur : **"MegaMesh" == "Mesh Partition" == "Mesh Terrain"** désignent le même système. Voir le glossaire.
- Le plugin éditeur `MeshTerrainMode` n'est **que de l'UI** ; n'y cherche pas d'algorithme.
- Beaucoup de logique vit dans le module **`MeshPartitionEditor`** (suffixe *Editor*) car la compilation se fait à l'édition/cook, pas au runtime du jeu. En Unity, l'essentiel sera donc du **code éditeur** (`Editor/` + jobs Burst), pas du runtime.

*Source : Unreal Engine 5.8, `Engine/Plugins/Experimental/MeshPartition`. Documentation Epic : "Introduction to Mesh Terrain".*
