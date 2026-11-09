# Sea Simulator

Simulation sous-marine en temps réel réalisée avec Unity 6 (URP).

> **🤖 Projet « AI Assisted »**
> Ce projet est développé avec l'aide d'outils d'IA, principalement **Claude** (Anthropic), connecté directement à l'éditeur Unity via MCP.
> L'objectif est d'explorer rapidement **jusqu'où il est possible d'aller** avec ce type d'outils en combinant mon expérience avec Unity et ma vision du projet.
> Une partie du code a été écrite par l'IA : je ne m'en attribue donc pas tout le mérite. Toutes les directives sur la façon de faire viennent de moi.

## Objectif

Créer une scène sous-marine vivante et crédible, étape par étape, pour un portfolio de simulation. **Projet orienté IA**

## Avancement

- [x] **Bancs de poissons** : des milliers de sardines et de maquereaux qui nagent en bancs séparés et fuient un prédateur
- [ ] **Fond marin** : relief naturel, comme façonné par le temps (érosion)
- [ ] **Vie sur le fond** : coraux, roches, relief
- [ ] **Ambiance sous-marine** : eau trouble, distorsion, rayons de lumière

## Bancs de poissons

- Comportement de groupe de type *boids* (séparation, alignement, cohésion)
- **Voisins topologiques** : chaque poisson suit ses ~7 voisins visibles les plus proches, comme observé dans les vrais bancs et nuées
- Chaque poisson est traité comme une capsule (et non un point) pour éviter que les corps se chevauchent
- **Limites physiques** : rayon de virage minimal lié à la taille du corps, vitesse de croisière propre à chaque poisson
- **Nage « battement puis glisse »** : la queue bat plus vite quand le poisson accélère et se calme quand il glisse
- **Prédateur et fuite** : un prédateur chasse ; les poissons proches fuient sur les côtés, l'alerte se propage de voisin en voisin et le banc s'ouvre autour de lui
- **Plusieurs espèces** (sardines et maquereaux pour l'instant) :
  - aucun poisson n'en traverse un autre, quelle que soit son espèce ;
  - un banc ne contient qu'une seule espèce : deux bancs de la même espèce peuvent fusionner, deux bancs d'espèces différentes s'évitent ou se croisent ;
  - l'alerte face au prédateur passe d'une espèce à l'autre ;
  - ajouter une espèce ne demande pas de code : un asset `FishSpecies` (forme, taille, couleurs, nage, comportement) et un `FishSchool` dans la scène
- Calcul en parallèle avec le **C# Job System** et **Burst**, avec une grille spatiale triée pour trouver les voisins rapidement
- Affichage de tous les poissons en quelques appels grâce au **GPU instancing**
- Nage animée directement dans le shader, pilotée par la simulation
- Poissons générés par le code, sans modèle 3D externe

## Enclave

- Une enclave de falaises de calcaire érodé entoure la zone de nage, avec une marge : la roche n'y entre jamais
- Falaises générées par le code : grandes masses arrondies, poches creusées par l'eau, alcôves, sommet arrondi en plateau
- Contour irrégulier (baies et avancées) pour ne pas trahir la forme rectangulaire de la zone de nage
- Sol de sable en dunes douces, avec des rochers posés dessus
- Textures réelles projetées sur les trois axes (triplanaire) avec l'éclairage PBR d'URP ; une seconde lecture de la texture à une autre échelle casse la répétition du motif
- Tous les réglages (taille, relief, contour, plateau) se font dans l'Inspector de l'objet `Enclave`

## Crédits

- Textures de roche (*Rock Pitted Mossy*) et de sable (*Sand 01*) : [Poly Haven](https://polyhaven.com), licence CC0

## Le projet

**Caméra :** clic droit pour regarder, ZQSD pour se déplacer, A/E pour descendre/monter, Maj pour accélérer, molette pour régler la vitesse.

Les bancs sont regroupés sous l'objet `FishSchools` de la scène : chaque `FishSchool` définit une espèce, un nombre de poissons et une zone de nage. L'apparence et le comportement de chaque espèce se règlent dans son asset (`Assets/Simulation/Fish/Species/`), et le prédateur sur l'objet `Predator`.
