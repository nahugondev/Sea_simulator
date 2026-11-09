# Sea Simulator

Simulation sous-marine en temps réel réalisée avec Unity 6 (URP).

> **🤖 Projet « AI Assisted »**
> Ce projet est développé avec l'aide d'outils d'IA, principalement **Claude** (Anthropic), connecté directement à l'éditeur Unity via MCP.
> L'objectif est d'explorer rapidement **jusqu'où il est possible d'aller** avec ce type d'outils en combinant mon expérience avec Unity et ma vision du projet.
> Une partie du code a été écrite par l'IA : je ne m'en attribue donc pas tout le mérite. Toutes les directives sur la façon de faire viennent de moi.

## Objectif

Créer une scène sous-marine vivante et crédible, étape par étape, pour un portfolio de simulation. **Projet orienté IA**

## Avancement

- [x] **Bancs de poissons** : des milliers d'individus qui nagent en groupe et fuient un prédateur (architecture prête pour plusieurs espèces)
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
- **Plusieurs espèces possibles** : chaque espèce est un asset `FishSpecies` (forme, taille, nage, comportement)
- Calcul en parallèle avec le **C# Job System** et **Burst**
- Affichage de tous les poissons en quelques appels grâce au **GPU instancing**
- Nage animée directement dans le shader, pilotée par la simulation
- Poissons générés par le code, sans modèle 3D externe

## Le projet

**Caméra :** clic droit pour regarder, ZQSD pour se déplacer, A/E pour descendre/monter, Maj pour accélérer, molette pour régler la vitesse.

Le nombre de poissons se règle sur l'objet `FishSchool` de la scène. Leur apparence et leur comportement se règlent dans l'asset d'espèce (`Assets/Simulation/Fish/Species/Sardine.asset`), et le prédateur sur l'objet `Predator`.
