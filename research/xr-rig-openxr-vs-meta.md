# XR rig : Unity OpenXR pur vs Meta XR rig — pour HDMR + l'UI 3DS

_Analyse 2026-09-09. Sources : docs Meta (developers.meta.com), vérifiées via metavr._
_Contexte : HDMR passe à l'UI/UX (démo autonome style 3DS, grille spatialisée, écran du haut = flux nuvc live), et la question est de migrer **tout** le projet sur le « Meta rig »._

---

## Ce qui est vérifié (docs Meta, à jour 2026)

1. **Le runtime Horizon OS est 100 % conforme OpenXR.** Une app faite avec le Unity OpenXR Plugin tourne sans modif. (doc « Unity and OpenXR Compatibility », maj 2026-05-18)
2. **Le Meta XR SDK fonctionne PAR-DESSUS le Unity OpenXR Plugin.** Le préfixe `OVR` (OVRManager, OVRCameraRig…) est un héritage de nommage de l'ancien Oculus XR plugin — « All of these are compatible with Unity's OpenXR plugin ». Ce n'est **pas** un choix exclusif « OpenXR **ou** Meta ».
3. Le **Oculus XR Plugin est déprécié**. Le provider, dans les deux scénarios, c'est le **Unity OpenXR Plugin** (ce que HDMR utilise déjà : `com.unity.xr.openxr` 1.17.1 + feature `metaquest`).
4. **Le Meta XR SDK est Quest-only.** OVRManager a des dépendances dures sur des extensions OpenXR Meta → ne tourne pas sur un casque OpenXR non-Meta.
5. `com.unity.xr.meta-openxr` (package Unity) donne les features Meta (passthrough, depth/occlusion) sur le chemin OpenXR pur, **sans** le SDK Meta — mais pas les compositor layers ni l'Interaction SDK.
6. **Compositor layers = `OVROverlay` / `OVROverlayCanvas`**, qui **dépendent d'`OVRManager`** (donc du Meta XR Core SDK). Bénéfices : texte net, vidéo haute qualité, cadence de rafraîchissement indépendante de l'eye buffer, et surtout **`Is External Surface`** → la vidéo passe directement au compositeur (moins de latence, pas de resample eye-buffer).
7. **Meta XR Interaction SDK** fournit le **UI Set** : boutons/tiles/shelf/sliders/toggles/dropdowns/dialogs bâtis sur Unity Canvas, avec `PokeInteractable` + `RayInteractable` + `PointableCanvas` déjà câblés, un **Theme Manager** (couleurs + police), des **Animators** de feedback (scale au press/hover), canvas courbe ou plat, et des **scènes d'exemple** (UISet, UISetPatterns, PokeExamples, RayExamples).

---

## Les deux options réelles

### A — Rester Unity OpenXR pur + Unity XR Interaction Toolkit (+ XR Composition Layers)

| + | − |
|---|---|
| Vendor-neutral (tournerait sur d'autres casques OpenXR) | HDMR est **intrinsèquement Quest-only** : `horizonos.permission.USB_CAMERA`, carte MACROSILICON, un seul device cible. La portabilité ne sert à rien ici. |
| Moins de packages, moins de churn | Il faut **construire soi-même** toute la grammaire 3DS (tiles, shelves, feedback élastique, thème) sur des primitives XRI |
| Déjà le stack actuel — zéro migration du rig | Compositor layers via `com.unity.xr.compositionlayers` : fonctionnel mais moins éprouvé sur Quest que `OVROverlay`, pas de `Is External Surface` équivalent simple pour la vidéo |
| Support Unity direct | Pas de scènes d'exemple prêtes pour ce type d'UI |

### B — Migrer sur le Meta XR rig : Unity OpenXR (provider) + Meta XR Core SDK + Interaction SDK

| + | − |
|---|---|
| **UI Set ≈ le pattern 3DS clé en main** : tiles, shelf, poke+ray, Theme Manager, Animators springy. Énorme avance pour « analyser et retranscrire ce type d'interface » | **Churn** : Meta publie ~tous les mois. Il faut **pinner les versions** et upgrader délibérément |
| **`OVROverlay` améliore le cœur du produit** : le panneau de capture devient un compositor layer (plus net, moins de latence, `Is External Surface` pour le flux) — c'est exactement ce que la note mémoire « Node C compositor-layer PARKED » visait | **Migration** : remplacer le rig code-built par `OVRCameraRig` ; `NuvcXrRig` doit pointer sur `OVRCameraRig.centerEyeAnchor` au lieu de `Camera.main` |
| Texte d'UI net (compositor) — important pour une UI textuelle | **Manifeste** : réconcilier `UsbManifestPostProcessor` avec `OVRManifestPreprocessor` (les deux injectent dans `AndroidManifest.xml`). À chaque changement d'`OVRProjectConfig` → `GenerateOrUpdateAndroidManifest()` obligatoire |
| Building Blocks + Project Setup Tool → moins de code de rig/config custom | Re-tester tout le chemin capture XR (Gamma colorspace, single-pass instanced, stéréo) sous `OVRManager` |
| Passthrough trivial si on veut l'UI 3DS flottant dans la pièce | Config `OVRManager` à committer dans la scène plutôt que dans le build script |
| Scènes d'exemple Meta à décortiquer pour les courbes/timings | Lock-in Meta (acceptable ici : app mono-device) |

---

## Recommandation : **B**, mais progressive

HDMR est verrouillé Quest par nature — le seul vrai argument pour A (portabilité) ne vaut rien ici. En face, l'Interaction SDK donne 80 % de la grammaire 3DS gratuitement, et `OVROverlay` fait gagner en netteté/latence **sur le flux de capture lui-même**, pas juste sur l'UI.

**Plan :**

1. **Nouveau projet Unity dédié** pour la démo UX (choix « démo autonome »). Stack : Unity 6000.5.10f1, Unity OpenXR Plugin + feature Meta Quest, **Meta XR Core SDK** + **Meta XR Interaction SDK** (Essentials suit). Versions **pinnées**.
2. Monter le **spike 3DS** dessus : `OVRCameraRig` (ou `OVRCameraRigInteraction`), grille de tiles UI Set sur canvas courbe, ray + poke, feedback élastique, transition tile→carte, panneau du haut en `OVROverlay` (placeholder puis flux nuvc réel via l'AAR `nuvc` + `Is External Surface`).
3. Une fois le rig + Interaction SDK + panneau compositor **prouvés en casque**, **porter le setup validé dans HDMR** : swap du rig, réconciliation manifeste, re-test capture. Le `nuvc` (plugin natif, `NuvcCapture`, shaders) est indépendant du rig — il se transpose tel quel.
4. `com.unity.xr.meta-openxr` : **optionnel**, seulement si on veut passthrough/depth dans la démo.

**Points de vigilance migration HDMR :**
- `NuvcXrRig.FindHead()` → `OVRCameraRig.centerEyeAnchor`
- `HDMRBuildNuvcXR` : la config XR (loader, feature Meta Quest) reste, mais `OVRManager`/`OVRProjectConfig` s'ajoutent et pilotent une partie du manifeste
- `UsbManifestPostProcessor` : garder l'injection `USB_DEVICE_ATTACHED` + `device_filter_uvc`, vérifier qu'elle survit au passage d'`OVRManifestPreprocessor` (ordre des `IPostGenerateGradleAndroidProject`)
- Colorspace Gamma + single-pass instanced : re-valider, l'`OVRManager` a ses propres réglages stéréo
- Le flip / offsets / distorsion de `NuvcStereoXR` : inchangés (c'est du shader sur un quad, que le quad soit sous un rig OpenXR ou un `OVRCameraRig`)

---

## Option B — spécification exacte (lève l'ambiguïté)

> ⚠️ Le doc de setup Meta lui-même appelle « Option B » l'**Oculus XR Plugin déprécié**. Rien à voir. Ici « Option B » = **provider Unity OpenXR + Meta XR SDK par-dessus**.

| Élément | Choix | Notes |
|---|---|---|
| Provider XR | **Unity OpenXR Plugin** `com.unity.xr.openxr` ≥ 1.17 | HDMR est déjà en 1.17.1. OpenXR sur l'onglet Android. |
| Feature group | **Meta XR** (Meta XR Feature + Foveation + Subsampled Layout) | Dans XR Plug-in Management → OpenXR → Android |
| SDK 1 | **Meta XR Core SDK** `com.meta.xr.sdk.core` **v76+** | v76 = première version Core qui supporte le provider Unity OpenXR (avant : Oculus plugin obligatoire). Pinner une v7x récente. Fournit OVRManager, OVRCameraRig, **OVROverlay/OVROverlayCanvas**, Project Setup Tool, Building Blocks. |
| SDK 2 | **Meta XR Interaction SDK** `com.meta.xr.sdk.interaction` (+ Essentials auto) | ray/poke/`PointableCanvas`, **UI Set**, Theme Manager, scènes d'exemple |
| Rig | prefab **`OVRCameraRigInteraction`** | OVRCameraRig + interaction manettes/mains pré-câblée |
| **Pas** | Meta XR All-in-One SDK, Platform SDK, Movement SDK, MRUK, `com.unity.xr.meta-openxr` | Rien de tout ça n'est nécessaire pour le shell. MRUK seulement si un jour on veut poser l'UI sur un mur réel. |
| Render pipeline | **Built-in RP** (comme HDMR — vérifié : `m_CustomRenderPipeline: 0`, shaders `CGPROGRAM`) | Meta docs recommandent URP par défaut mais Built-in est supporté. On garde Built-in pour que l'intégration du flux `nuvc` (shaders `Hdmr/NV12`, `NuvcStereoXR`) soit du copier-coller. |
| Colorspace | **Gamma** | Déjà validé pour le chemin de capture |
| Controllers | Touch **v3** (Quest 2) + Touch **Plus** (Quest 3) | Le config XR actuel de HDMR n'a que `metaquestplus` → ajouter le profil Touch de base pour Quest 2 |

### Cible : Quest 2 + Quest 3 uniquement

Quest 2 = **plancher perf**. Contraintes design qui en découlent :
- Compositor layers : 2-3 max (panneau du haut + éventuellement un layer UI)
- Fixed Foveated Rendering activé, MSAA 2-4×, 72 Hz baseline
- Batching de canvas agressif, géométrie de grille légère, surveiller l'overdraw des couches de parallaxe
- Pas de features Vulkan-only si elles cassent GLES3

---

## Toolchain d'analyse & de vérification (reconstruction du caractère 3DS)

### Analyse de la référence
- `ffmpeg` → extraction des frames à la cadence source (60 fps idéalement — émulateur Citra ou capture directe)
- Lecture frame par frame, suivi de la position/bbox d'un élément animé sur N frames (tracking par color-key / template match simple, script Python/JS)
- Courbe position(t) → vitesse/accélération → **fit** sur familles d'easing standard (cubic, back/overshoot, spring) : durée, % d'overshoot, temps de stabilisation, stagger inter-éléments
- Mesure des métriques statiques (gutter, corner radius, marges, ratio tile) sur screenshots
- Sortie : une **fiche de référence** — chaque interaction → {trigger, durée, courbe, overshoot, propriétés affectées, son}

### Vérification de la reconstruction
- Le build Unity logue transform/scale de l'élément animé **par frame** → même extraction de courbe sur ma sortie → **diff numérique** vs la référence
- Visuel : capture `metavr` (screenshot + gif) du build qui tourne, comparaison côte-à-côte / overlay avec les frames de référence
- Scène « motion harness » en casque : mon animation et la vidéo de référence lues côte à côte
- Panneau debug live-tunable pour les params d'easing (comme l'EDIT bar de `nuvc`) → itération rapide en casque
- `ui_dump` / `ui_actions` (metavr) pour piloter l'UI en test

### Niveau de précision réaliste
- **Mesurable au ~frame près** (±16 ms à 60 fps) : durées, et fit de courbe avec overshoot % / settle time
- **Ratios de layout** : ±1-2 % depuis les screenshots
- **Inhérent­ement approximé** : les constantes d'easing propriétaires exactes de Nintendo (non publiées) → je fitte la courbe standard la plus proche ; le « feel » subjectif demande 2-3 passes de tuning en casque
- **Réinterprété par nature** (c'est une adaptation VR, pas un port d'écran tactile) : parallaxe simulée → parallaxe réelle à la tête ; stylet/tactile → ray + poke ; profondeur 3D-slider → profondeur spatiale continue. Chaque réinterprétation sera explicitée.
- Résultat visé : **immédiatement reconnaissable comme lignée 3DS**, courbes de mouvement à une ou deux frames de la référence, affiné en comparaison directe — pas un clone frame-identique.

---

## Si finalement on veut rester en A

Viable, mais budgéter le temps de reconstruire : système de tiles/shelf, thème, courbes de feedback, et l'intégration compositor-layer pour la vidéo. XRI gère ray/poke/UI-canvas ; le reste est à faire main.
