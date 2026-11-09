# Sea Simulator

Simulation sous-marine en temps réel réalisée avec Unity 6 (URP).

> **🤖 Projet « AI Assisted »**
> Ce projet est développé avec l'aide d'outils d'IA, principalement **Claude** (Anthropic), connecté directement à l'éditeur Unity via MCP.
> L'objectif est d'explorer rapidement **jusqu'où il est possible d'aller** avec ce type d'outils en combinant mon expérience avec Unity et ma vision du projet.
> Une partie du code a été écrite par l'IA : je ne m'en attribue donc pas tout le mérite. Toutes les directives sur la façon de faire viennent de moi.

## Objectif

Créer une scène sous-marine vivante et crédible, étape par étape, pour un portfolio de simulation. **Projet orienté IA**

## Avancement

- [x] **Bancs de poissons** : une espèce, des milliers d'individus qui nagent en groupe
- [ ] **Fond marin** : relief naturel, comme façonné par le temps (érosion)
- [ ] **Vie sur le fond** : coraux, roches, relief
- [ ] **Ambiance sous-marine** : eau trouble, distorsion, rayons de lumière

## Bancs de poissons

- Comportement de groupe de type *boids* (séparation, alignement, cohésion)
- Chaque poisson est traité comme une capsule (et non un point) pour éviter que les corps se chevauchent
- Calcul en parallèle avec le **C# Job System** et **Burst**
- Affichage de tous les poissons en quelques appels grâce au **GPU instancing**
- Nage animée directement dans le shader (ondulation du corps et de la queue)
- Poisson généré par le code, sans modèle 3D externe

## Le projet

**Caméra :** clic droit pour regarder, ZQSD pour se déplacer, A/E pour descendre/monter, Maj pour accélérer, molette pour régler la vitesse.

Les réglages des poissons (nombre, vitesse, comportement) se trouvent sur l'objet `FishSchool` de la scène.
