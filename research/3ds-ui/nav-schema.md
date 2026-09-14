# HDMR Home — schéma de navigation (transcrit du dessin, 2026-09-15)

_Source : deux photos de schéma papier. Retranscription structurée pour servir de référence stable au prototype et au build Unity._

## Flux de démarrage

```
Démarrage de l'app
  → Scène de chargement avec logo (sans aide Unity — écran de démarrage custom)
  → Menu Principal (avec preview HDMI live)
```

## Graphe de navigation (depuis le Menu Principal)

Le Menu Principal est le hub central, connecté en aller-retour à :

- **Process de Calibrage** (bref — un mini-flux guidé, pas un écran isolé)
- **Menu de Réglages lumière**
- **Menu de paramètres** (général)
- **Menu de définition et attribution de profils**
- Idée notée en marge : **bouton de redémarrage** dans les paramètres

Et séparément, en aller-retour, vers **3 modes de présentation** — qui sont aussi interconnectés **entre eux** (on bascule d'un mode à l'autre directement, pas besoin de repasser par le menu à chaque fois) :

- **Mode 3DS / MR**
- **Mode Full VR** (existant)
- **Mode « Cinéma »**

## Les 3 modes de présentation

| Mode | Description |
|---|---|
| **3DS / MR** | Écran flottant **fixé dans la réalité** (world-locked), passthrough actif, stéréo. Placeable à volonté sur un meuble ou un mur. Option d'affichage de la distance à l'écran. |
| **Full VR** | Le mode actuel (`NuvcXrRig`), inchangé. Nouveauté : option d'activer le passthrough à la place du fond noir (dans les paramètres généraux). |
| **« Cinéma »** | Comme YouTube VR — reflets et decorum, écran plaçable n'importe où, option passthrough. |

**Note transversale** : un **mode « écran méta 2D »** sera une option dans les paramètres — toute l'app repasse en rendu 2D plat, avec un avertissement de perte de performance (compromis qualité visuelle 3D/MR vs perf).

## Écran du Menu Principal (maquette détaillée)

- **Zone preview** (haut) : flux HDMI live, dropdown résolution en haut-gauche (liste des résolutions **par jeu**, mise à jour en direct) + indicateur FPS en haut-droite. Rendu **légèrement plus gros** que dans le prototype actuel.
- **Séparateur** entre preview et deck : une forme **fine, en pilule** — clin d'œil explicite **au gond de la 3DS** (la charnière physique entre les deux écrans). À reprendre visuellement.
- **Deck** (bas) :
  - 2 tiles principales, en avant : **« Lancer le mode Stéréo VR »**, **« Calibrage Stéréo »**
  - Une rangée de tiles secondaires, **plus petites, alignées à gauche** (pas une grille régulière étalée) : Profil 1, Profil 2, mode MR, mode Cinéma, et une tile de retouche d'image (nom à trancher — voir plus bas)
  - Barre du bas : **Profils | Accueil | Paramètres**
- **Comportement de « Paramètres »** : ce n'est **pas** un écran séparé qui remplace tout — la page principale **s'agrandit et la preview rapetisse** pour faire de la place à un système de sous-menu **long et scrollable**. Une transformation de layout in-place, pas un hard cut.

## Process de Calibrage (le mini-flux guidé)

```
Demande de passer la console/l'appareil en VR
  → Sélectionner la résolution
  → Vérifier que le preset est bon (Switch 2) — sinon afficher les réglages complets
  → Choix de la distance (ou adaptation auto de l'écran selon la distance)
  → Test dans le mode de son choix
```
**Les étapes sont navigables** (aller-retour libre, pas un assistant strictement linéaire).

## Statut

L'écran **Menu Principal** (preview + gond + 2 tiles primaires + rangée secondaire alignée à gauche + barre Profils/Accueil/Paramètres + reflow in-place sur Paramètres) est implémenté dans le prototype HTML — mêmes lien/mécaniques de feel que le reste (curseur, pop, lévitation, son).

Le **process de calibrage** (5 étapes, navigables librement via les puces ou ←/→) est maintenant implémenté aussi : sélection de la tile « Calibrage stéréo » ouvre le mini-flux (VR → Résolution → Preset → Distance → Test), avec les réglages complets (gain/gamma/lift) repliables à l'étape Preset et les 3 modes de test à l'étape finale. Les 3 modes de présentation eux-mêmes (3DS/MR, Full VR, Cinéma) restent à construire comme véritables écrans — pour l'instant le wizard ne fait que les référencer en option de test.

## Précisions confirmées (2026-09-15)

- **« 0DF-Stéréo »** sous Mode Full VR = **0 Degree of Freedom** — l'écran est « collé » à la tête (head-locked), en stéréo. Confirme le comportement actuel de `NuvcXrRig` (le quad suit la caméra à chaque frame). Ça positionne bien les 3 modes sur un axe de liberté : **Full VR = 0DoF** (collé tête) → **3DS/MR = 6DoF** (world-locked, placé dans la pièce) → **Cinéma** probablement world-locked aussi (environnement).
- Nom de la tile retouche d'image → **« Étalonnage »**, confirmé.
