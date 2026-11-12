# HDMR Home — méthode de vérification UI (100% accuracy)

_Créé 2026-09-16, sur demande explicite : "fais-toi une liste de ce que ferait un designer d'interface... à garder précieusement". Compilé à partir de checklists de Design QA publiées + des guidelines Meta Horizon OS pour le spatial/VR + principes de motion design, puis complété avec les dimensions explicitement listées par l'utilisateur. Ce document est la méthode ; `ui-qa-progress.md` (même dossier) est le carnet de suivi élément-par-élément qui l'applique._

## Sources

- [Design QA Checklist — Eleken](https://www.eleken.co/blog-posts/design-qa-checklist-to-test-ui-and-prepare-for-design-handoff) — consistance, layout, contenu, éléments interactifs
- [Design QA Checklist — 21st.dev](https://21st.dev/blog/design-qa-checklist) — ordre recommandé : référence → layout → composants/contenu → comportement → décision
- [UI Testing Checklist — QAwerk](https://qawerk.com/blog/ui-testing-checklist/) — dimensions de qualité, grille 8pt/4pt
- [Visual QA — OverlayQA](https://overlayqa.com/blog/visual-qa/) — ce que la revue de code ne capture pas
- [Comfort — Meta Horizon OS Developers](https://developers.meta.com/horizon/design/comfort/) — distances et confort en VR
- [Design immersive experiences — Meta Horizon OS Developers](https://developers.meta.com/horizon/design/) — profondeur, hiérarchie visuelle spatiale
- [Animation Duration — NN/g](https://www.nngroup.com/articles/animation-duration/) — durée/timing des transitions UI
- [20 Motion Design Principles — Mockplus](https://www.mockplus.com/blog/post/20-motion-design-principles-with-examples) — easing, spacing, mouvement naturel

## Méthode : ordre de vérification (par élément, pas par catégorie)

Pour chaque élément de l'UI (chaque tuile, le panneau preview, la charnière, la tabbar, chaque texte, chaque icône, l'aura, le sol), vérifier dans cet ordre — un élément fini avant de passer au suivant, pas une passe "toutes les couleurs" puis une passe "toutes les tailles" :

1. **Existence & activation** — l'objet existe, est actif, son composant de rendu est activé.
2. **Géométrie propre** (voir §A) — taille, forme, épaisseur.
3. **Position relative** (voir §B) — à CHAQUE autre élément pertinent, pas seulement au parent.
4. **Rotation relative** (voir §B) — idem.
5. **Couleur & matière** (voir §C).
6. **Texte & typo** (voir §D) — si applicable.
7. **Cohérence structurelle** (voir §E) — débordement, chevauchement, clipping.
8. **Animation / mouvement** (voir §F) — si l'élément bouge ou doit bouger.
9. **Lumière & profondeur** (voir §G) — si applicable.
10. **Verdict** : conforme / écart mineur / écart majeur — avec la valeur mesurée ET la valeur de référence citée (pas de "ça a l'air bon").

## §A — Géométrie

- Hauteur, largeur, épaisseur (profondeur Z) mesurées en unités réelles (mètres pour la scène 3D, pixels/anchoredPosition pour le Canvas), comparées à la valeur de référence (CSS du prototype ou schéma papier).
- Ratio largeur/hauteur (aspect ratio) — pas seulement les deux dimensions séparément.
- Rayon de coin / arrondi si applicable.
- Cohérence de la grille : les incréments d'espacement suivent-ils un pas régulier (ex. 8pt/4pt côté web ; ici, cohérence interne au prototype) plutôt que des valeurs choisies au hasard élément par élément ?

## §B — Position et rotation relatives (TOUS les axes, TOUS les autres éléments)

- Distance sur les 3 axes (X/Y/Z monde, ou X/Y anchoredPosition + profondeur de panneau) à chacun des autres éléments directement adjacents ou fonctionnellement liés — pas seulement au parent immédiat.
- Écart entre bords (gap) mesuré, pas juste la distance centre-à-centre.
- Alignement : les bords/centres qui doivent s'aligner (ex. une rangée de tuiles) le font-ils exactement, ou y a-t-il un décalage résiduel ?
- Rotation relative : deux éléments censés être coplanaires/parallèles le sont-ils réellement (angle mesuré, pas supposé) ? Un élément censé avoir un tilt spécifique (podium) a-t-il exactement cet angle, et les éléments voisins qui NE doivent PAS être tiltés sont-ils bien à 0° relatif ?
- Zone de confort VR (spécifique casque, pas web) : distance de vue dans la zone personnelle 0,5–1,2 m ; l'ensemble de l'UI tient dans ~60° du champ de vision frontal (voir Meta Comfort guidelines) — sinon fatigue de nuque.

## §C — Couleur et matière

- Valeur hexadécimale/RGBA exacte comparée au token de design source (ex. `--tile-ink #17262e`), pas "ça a l'air sombre".
- Alpha/opacité exacte, pas juste "semi-transparent".
- Dégradés : les vraies couleurs de départ/arrivée et la direction, pas une couleur plate qui approxime.
- Cohérence : le même rôle sémantique (ex. texte secondaire partout) utilise-t-il la même couleur partout, ou des variantes qui ont dérivé ?
- Matériau/shader utilisé correspond à l'usage réel (pas un matériau hérité d'un composant qu'on a réutilisé sans vérifier ce qu'il apporte).

## §D — Texte et typographie

- Police correcte (famille + fallback si un glyphe manque — vérifié en le regardant vraiment, pas supposé).
- Taille, graisse, interlignage.
- Alignement (gauche/centre/droite) correct par élément — un mauvais alignement isolé est un des écarts les plus visibles et les plus fréquents.
- **Débordement** : le texte dépasse-t-il de sa boîte (largeur insuffisante, troncature, retour à la ligne non voulu) ? Vérifier avec le texte réel le plus long possible pour ce champ (ex. "Paramètres" vs "Profils" — pas juste le plus court).
- Casse et ponctuation exactes (majuscules vs casse mixte, point médian vs tiret — déjà une source d'écart trouvée cette session).

## §E — Cohérence structurelle

- Chevauchement non voulu entre deux éléments (Z-fighting, un élément qui recouvre un texte).
- Clipping : un enfant sort-il des limites de son masque/panneau parent ?
- Marges intérieures (padding) cohérentes entre éléments de même rôle.
- Étiquetage/nommage des GameObjects cohérent avec la convention du projet (utile pour la maintenance, pas juste esthétique).

## §F — Animation et mouvement (attention particulière : lévitation)

- Amplitude réelle du mouvement mesurée (pas "ça bouge un peu").
- Période/fréquence mesurée, comparée à la valeur cible (le prototype cite ~1.4–2s pour l'idle "Lévitation").
- Courbe d'easing : accélération/décélération de type naturel (sinusoïdal pour un mouvement répétitif comme la lévitation), pas linéaire — un mouvement linéaire en boucle lit comme mécanique, pas organique (voir Mockplus/NN·g).
- Axe(s) de mouvement corrects (la lévitation du prototype est un déplacement vertical doux, pas une rotation ni un scale).
- Le mouvement ne doit pas désynchroniser l'élément de son halo/contour (piège déjà documenté dans `reference-pack.md` — la rotation seule créait un décalage avec le halo statique).
- Durée des transitions d'état (survol/sélection) dans la plage perçue comme réactive : ~200–500ms (NN/g) — trop lent lit comme mou, trop rapide lit comme un glitch.

## §G — Lumière, profondeur, effets

- Ombre portée / élévation cohérente avec l'état (sélectionné doit paraître "soulevé" par rapport à hover).
- Profondeur renforcée par des indices visuels (ombre, léger flou, taille relative) quand deux panneaux sont à des distances différentes — pas seulement une position Z sans aucun autre indice.
- Effets de halo/glow : rayon, couleur, intensité mesurés contre la référence, pas approximés à l'œil.
- Cohérence de la source de lumière across tous les éléments (pas un élément éclairé d'un côté et un autre de l'autre sans raison).

## Barème de verdict (à utiliser tel quel dans le carnet de suivi)

| Verdict | Sens |
|---|---|
| ✅ Conforme | Mesuré, correspond à la référence à la précision disponible |
| ⚠️ Écart mineur | Mesuré, différence mineure et non intentionnelle — à corriger |
| 🛠️ Corrigé | Était un écart, correctif appliqué ET revérifié après coup |
| 🚧 Placeholder assumé | Différence connue et délibérée (contenu non final) — pas un bug |
| ❓ Non mesurable ici | Nécessite un outil/contexte absent de cet environnement (ex. vrai casque porté) |
