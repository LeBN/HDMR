# HDMR Home — pack de référence 3DS (validé 2026-09-09)

## Contrainte d'analyse (honnête)

L'environnement n'a **ni yt-dlp ni ffmpeg** → je ne peux pas télécharger ni analyser image-par-image les vidéos YouTube d'ici. Conséquence sur le process :

- Le **premier jet** de l'exemple HTML vient de : structure documentée (ci-dessous) + connaissance du HOME Menu 3DS + les vidéos comme cible visuelle partagée.
- Le **calage fin** se fait avec toi comme oracle du feel via les **sliders live** de l'exemple (tu connais la 3DS, moi j'approche, tu dial, je relis les valeurs).
- Quand tu auras une capture card / des frames propres, on raffine au frame près.

## Sources validées

| Source | Usage |
|---|---|
| [The Sounds Resource — 3DS System BIOS](https://sounds.spriters-resource.com/3ds/systembios/) | Sons UI réels du HOME Menu → analyse enveloppe/pitch (jamais redistribués) |
| [Copetti — *Nintendo 3DS Architecture*](https://www.copetti.org/writings/consoles/nintendo-3ds/) | Contexte matériel + design |
| [Nintendo Life — *All 3DS HOME Menu Themes*](https://www.nintendolife.com/news/2014/10/video_take_a_look_at_all_of_the_3ds_home_menu_themes) | Mouvement + variation sonore par thème |
| [cohost/quiteajolt — *3DS HOME Menu Themes 2014-2018*](https://cohost.quiteajolt.com/2023/03/28/nintendo-3ds-home-menu-themes-2014-2018/) | Caractère audio, curation musicale |
| [Wikipedia — *Nintendo 3DS system software*](https://en.wikipedia.org/wiki/Nintendo_3DS_system_software) | Carte fonctionnelle : HOME Menu, dossiers, eShop, app Son |
| [DS-Homebrew wiki — Custom DSi/3DS SFX](https://wiki.ds-homebrew.com/twilightmenu/custom-dsi-3ds-sfx) | Slots SFX + format |
| [3dbrew — Home Menu / Themes](https://www.3dbrew.org/wiki/Home_Menu/Themes), [BCWAV](https://www.3dbrew.org/wiki/BCWAV) | Format audio thème |
| YouTube — chaîne [3DSCapture](https://www.youtube.com/user/3DSCapture) + recherches « New 3DS HOME Menu walkthrough », « 3DS eShop tour » | **Référence mouvement principale** (à cadrer ensemble quand on a de quoi extraire les frames) |

## Fait dur qui valide l'approche son

Les sons UI **par défaut** du HOME Menu 3DS sont **séquencés (CSEQ, type MIDI)** — pas des samples enregistrés. L'audio thème est en ~**16-bit / ~22 kHz** (BCWAV). → formes d'onde simples, notes musicales, enveloppes douces, **aucune brillance haute fréquence**. Reconstruire la famille sonore comme un petit synthé Web Audio n'est pas une approximation : c'est *la* méthode d'origine.

## Slots SFX documentés (structure thème)

| # | Trigger |
|---|---|
| 0 | Déplacement curseur |
| 1 | Lancement d'app |
| 2 | Décider / accepter |
| 3 | Annuler / retour |
| 4 | Curseur en butée bord gauche/droite |
| 5 | Saut d'un écran entier d'icônes |
| 6 | Bouton dossier (ouverture) |
| — | Réveil depuis la veille |

## Fiche interaction — jet initial (à caler en casque/slider)

> Valeurs = point de départ depuis connaissance du HOME Menu. `?` = à confirmer sur footage.

### Fond animé
- 2-3 couches de parallaxe, autoscroll très lent (~4-8 px/s ?), motif de thème répété
- Léger gradient vertical sur le backplate + vignette douce
- En VR : ces couches gagnent la parallaxe réelle à la tête

### Interaction 1 — curseur entre tiles
- **Déplacement** : le highlight se déplace vers la nouvelle tile, pas de glissement long — ease-out court, **~90-120 ms ?**, `cubic-bezier(0.2, 0.9, 0.25, 1)` approx
- **Son (slot 0)** : note courte pitchée, sinus + 2e harmonique faible, attaque ~4 ms, décroissance exp ~70 ms, ~**660-880 Hz ?**
- **Arpège nav** : chaque pas consécutif rapide monte d'un degré de gamme (max +5), reset après ~250 ms d'inactivité
- **Butée bord (slot 4)** : même note un ton plus bas + petit shake de 2-3 px de la grille

### Interaction 2 — sélection de tile (pop)
- **Visuel** : scale 1.0 → ~**1.06-1.10 ?** avec overshoot (spring), **~160-200 ms ?**, settle ~1 rebond ; l'ombre portée grandit (blur + offset), le tile passe légèrement devant (Z / en VR translation caméra)
- **Idle une fois sélectionné** : oscillation continue très faible — tilt ±0.5° ou scale ±0.5 %, période ~**1.4-2 s ?**, sinus
- **Son (slot 2)** : « bloup » rond, une tierce/quinte au-dessus du son de curseur, attaque douce, décroissance ~120 ms, pitch qui monte très légèrement sur l'attaque
- **Haptique (VR)** : impulsion click sur la frame exacte du transitoire audio

### Interaction 3 (plus tard) — lancement tile → carte
- Le tile se déplie vers plein cadre : scale + fade depuis sa position, **~280-400 ms ?**, ease-in-out ; la grille recule (scale 0.96) + flou léger
- **Son (slot 1)** : montée douce, deux notes, ~350 ms
- Retour = exact inverse

## Itération 2 (2026-09-14) — retour de l'échange design

- **Fond** : remplacé les ronds dispersés (aucun lien avec le premier plan) par une **grille régulière de rectangles arrondis** — même vocabulaire de forme que les tiles/le cadre curseur. Jitter léger de rotation/taille par cellule pour casser le mécanique, position gardée sur grille. Vitesse de dérive **uniforme par défaut** sur les 3 couches (`driftVariation` à 0, réglable pour tester une variation).
- **Animation idle** : la rotation seule créait un décalage avec le halo de sélection (glow statique pendant que l'objet bouge). Ajout d'un sélecteur **Aucune / Rotation / Halo pulsé / Bascule lumière / Lévitation** — tout piloté par une seule phase partagée. Défaut → **Halo pulsé** (la lumière respire, rien ne tourne, plus de décalage possible). « Bascule lumière » fait réagir le halo à l'inclinaison au lieu de l'ignorer.
- **Typo** : le mono (IBM Plex) confiné strictement aux surfaces méta (tiroir de réglage, dialogue d'export, pastille « Placeholder ») — tout ce qui est « dans l'expérience » (labels de tiles, sous-titres, onglets de dossier) passe sur la police body neutre. Ajout d'un sélecteur de police mono en direct (IBM Plex Mono / Roboto Mono / JetBrains Mono / Space Mono / Martian Mono / Fragment Mono) pour A/B tester.
- **Point de vue Nintodo — appliqué** : overshoot et échelle du pop réduits (plus décisif, moins rebond), léger transitoire de bruit ajouté à l'attaque des sons (physicalité), et l'arrière-plan **s'amortit quand une tile est sélectionnée** (« l'attention se resserre ») pour réduire le nombre de choses qui bougent en même temps.

## Cible de feel (étoile polaire)

*Sans hâte, amical, tactile, safe d'explorer, un peu « jouet » au bon sens, « c'est mon petit endroit ».*
Chaque paramètre se vérifie contre ça — pas contre la fidélité pixel.
