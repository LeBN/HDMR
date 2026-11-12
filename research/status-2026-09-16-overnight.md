# HDMR Status — pour le test de 15h (session du 2026-09-15 soir → 2026-09-16 matin)

Mis à jour ~08:40 après le déblocage d'Unity (tu as fermé la fenêtre
"Welcome to Meta XR SDK" toi-même vers 07:42). Cette version REMPLACE le
doc écrit pendant la nuit — l'essentiel de la section "0. EN PREMIER" de
cette nuit ne s'applique plus, Unity va bien.

Lis dans l'ordre. Section 1 = vérifié réellement (device ou Play Mode
réel, pas juste "ça compile"). Section 2 = encore incertain, à trancher
à 15h. Section 3 = ce qui n'a pas été touché.

## 1. Fait ET vérifié cette nuit + ce matin (preuves, pas des suppositions)

- **Bug de crash Unity réglé** : `HDMRBuildHome.cs` avait un
  `EditorApplication.Exit(2)` inconditionnel qui tuait tout l'Editor sur
  la moindre exception (c'est ce qui a fait planter Unity à 01:15 cette
  nuit). Corrigé : garde `isPlaying` sur `Build()`/`RebuildSceneOnly()`/
  `EnsureScene()`, et `Exit(2)` réservé au vrai batch-mode
  (`UnityEngine.Application.isBatchMode` — pas `EditorApplication`, une
  vraie erreur de compil de cette nuit trouvée et corrigée ce matin).
  Recompilé, 0 erreur, 0 warning.
- **Build APK propre confirmé plusieurs fois ce matin** (`result=
  Succeeded errors=0`, ~30s à 2m44s selon le cache Bee). Manifeste
  vérifié : permission USB_CAMERA, intent-filters VR, SDK horizonos
  corrects.
- **Séparateurs de tabbar ajoutés et vérifiés visuellement** (Play Mode
  Editor, screenshot réel) : Profils | Accueil | Paramètres avec 2
  lignes de séparation fines aux bonnes jointures. Couleur (`#8ba1a9`
  à 30%) est une supposition non sourcée — à valider à l'œil.
- **`HomeTileFocus` (curseur hover/select aqua/ambre) câblé** sur les 10
  tuiles. Vérifié en Play Mode réel : composant présent, `Outline` créé
  et bien désactivé au repos, aucune exception. **Le vrai comportement
  interactif (survol/sélection réels) n'a PAS été simulé** — j'ai
  délibérément évité une technique de réflexion risquée pour un gain
  marginal ; le vrai test avec ta manette/main à 15h fera foi.
- **Locomotor + TunnelingEffect reconfirmés désactivés** sur la scène
  fraîchement reconstruite ce matin (pas juste "ça tenait hier soir") —
  le fix de la vignette grise tient.
- **UI de base confirmée** (Play Mode Editor + un vrai screenshot device
  forcé) : tuiles avec icônes visibles, plaque de fond (`backplate`)
  correctement dimensionnée autour du contenu, thème sombre appliqué.
- **APK final installé sur le casque** (192.168.137.240:5555), avec les
  3 fixes du matin (crash-guard + dividers + HomeTileFocus). **C'est la
  version à tester à 15h.**

## 2. Encore incertain — à trancher en priorité à 15h

- **Croix rouge géante + motifs en X dorés vus sur un screenshot device
  forcé** (casque pas porté, `proximity_set` utilisé pour simuler
  "mounted" sans présence réelle — remis à l'état normal après le test).
  **Absents en Play Mode Editor** avec la même scène. Hypothèse la plus
  probable : notre app n'a jamais eu le vrai focus VR pendant ce test
  synthétique (logs montrent `com.oculus.vrshell`, pas notre app, comme
  client passthrough actif ; `HAND_TRACKER`/`HEAD_TRACKER` refusés à
  notre app pour "VR focus" manquant) — donc probablement un artefact du
  test forcé, pas un bug réel. **Non confirmé à 100% — c'est LA première
  chose à vérifier à 15h, avant de faire confiance à quoi que ce soit
  d'autre côté rendu visuel.**
- **Vraie interaction ray/poke** (manette ET main nue) : jamais testée
  avec un vrai contrôleur.
- **Alignement du sol réel** : le plan de sol est à Y=-1.6m (supposition
  pour une caméra EyeLevel), jamais mesuré contre ta vraie pièce.
- **HomeTileFocus en usage réel** : le survol/sélection aqua/ambre n'a
  jamais été vu avec un vrai pointeur.

## 3. Pas touché (hors scope de ce scaffold UI)

Câblage du vrai flux vidéo nuvc + assistant de calibrage dans cette
scène Home (le but final de l'app — ce scaffold n'est que la coquille du
launcher), mesure de latence glass-to-glass, Node C (compositor layer,
en pause), ré-vérification de l'audio nuvc (confirmé le 2026-09-07,
pas retesté depuis).

## 4. Priorités pour le test de 15h

1. **Remettre le casque, relancer l'app, regarder.** Confirme ou
   infirme tout d'un coup — en particulier la croix rouge (section 2).
2. Essayer de vraiment taper/ray-cliquer une tuile (manette + main) —
   vérifie à la fois le clic ET le curseur hover/select `HomeTileFocus`.
3. Vérifier que la vignette grise de locomotion a bien disparu.
4. Regarder le sol — correct dans ta vraie pièce, ou décalé ?
5. Cosmétique si le temps le permet : ajuster la couleur/hauteur des
   séparateurs de tabbar à l'œil.

Si tu ne fais qu'une seule chose, fais le #1 — ça règle la question de
la croix rouge en même temps.
