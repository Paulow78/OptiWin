# Journal des versions

Le numéro de version suit la date : `26.11.4` est la quatrième version publiée en novembre 2026. Jusqu'à la 26.11.1, l'application s'appelait OptiWin.

## 26.11.4

### Visuel « Liquid Glass » & Fond nuit profonde

- **Fond nuit profonde avec nébuleuse colorée** : nouvelle toile de fond très sombre (`#04060C`) éclairée par 5 halos colorés subtils (menthe, saphir, violet électrique, touche magenta, cyan) mis en cache GPU (`BitmapCache`) en une seule passe.
- **Glassmorphisme façon Apple (Liquid Glass)** : cartes, panneaux, dialogues, pilules et boutons vitrés à plusieurs couches, arêtes spéculaires lumineuses (`GlassBorder` / `GlassBorderSoft`) et reflets supérieurs discrets (`CardSheen`) sans flou coûteux par carte. Le **mode léger** reste disponible en un clic.

### Téléphone Android (nouvelle page — USB & Wi-Fi)

- **Connexion USB & Wi-Fi / Réseau** : connexion directe par câble USB, bascule en un clic « Passer en Wi-Fi » (`adb tcpip 5555` + lecture auto de l'IP), scan réseau local (port 5555) et **appairage sans fil Android 11+** par code à 6 chiffres (`adb pair`).
- **Télémétrie en direct** : marque, modèle, Android + patch de sécurité, batterie (% + température + santé), stockage libre/total, RAM disponible/totale, SoC et définition d'écran.
- **Débloatage sûr et réversible (85+ paquets)** : publicités et installeurs fantômes (MSA, AppCloud/Aura, Digital Turbine, Meta Services), Samsung Galaxy/One UI, Xiaomi/Redmi/POCO (MIUI/HyperOS), Oppo/Realme/OnePlus, Motorola/Huawei/Honor, réseaux sociaux/partenaires et extras Google. Blocage strict des composants vitaux (appels, SMS, contacts, clavier, caméra, système, lanceur actif, Play Store). Repli réversible `pm uninstall -k --user 0` restauré automatiquement par `cmd package install-existing`.
- **Optimisation 1 clic & outils** : vidage des caches (`pm trim-caches`), vitesse d'animations (`1× / 0,5× / 0,25× / 0×`), DNS privé chiffré anti-pub/rapide (AdGuard, Cloudflare, Quad9, Automatique), compilation officielle **ART / DEX** (`bg-dexopt-job`), installation d'APK depuis le PC, capture d'écran PNG et liste d'applications tierces (forcer l'arrêt).

### Téléviseur Android TV / Google TV enrichi

- **Catalogue étendu (60+ paquets)** : ajout de NVIDIA Shield (diagnostics/promos), Hisense, Sony Bravia, TCL, Xiaomi PatchWall et applications préinstallées.
- **Nouvelles capacités** : appairage sans fil Android 11+, bouton **Optimiser en 1 clic**, sélecteur de vitesse d'animations (`1× / 0,5× / 0,25× / 0×`), **DNS privé anti-pub/rapide**, optimisation **ART / DEX**, installation d'applications `.apk` depuis le PC, capture d'écran PNG, **télécommande rapide** (Accueil, Retour, OK, Lecture/Pause, Volume, Muet, Veille) et inspecteur d'applications tierces.

### Automatisation du nettoyage PC & nouveaux outils

- **Nettoyer › Automatisation** : choisissez librement le rythme (**quotidien**, **hebdomadaire** avec jour au choix, **toutes les 2 semaines**, **mensuel** avec jour du mois, ou **à l'ouverture de session**), l'heure, la gestion batterie / rattrapage si PC éteint, et exactement quelles catégories sûres nettoyer automatiquement. Bouton « Tester maintenant » intégré.
- **Nettoyer › Fichiers en double** : détecteur rapide en 3 passes (taille → empreinte 64 Ko → **SHA-256** complet) dans le dossier de votre choix, en lecture seule (aucune suppression automatique) avec ouverture directe dans l'Explorateur.
- **Outils › Mode d'alimentation Windows** : bascule en un clic entre vos plans d'alimentation (Économie, Équilibré, Performances élevées…) et bouton pour débloquer le mode **Performances optimales**.
- Palette de commandes (`Ctrl+K`) enrichie avec toutes les nouvelles pages et onglets ; 7 nouveaux auto-tests (144 au total).

## 26.11.3

### Fonctionnalités

- **Palette de commandes (`Ctrl+K`)** : recherche instantanée dans toutes les pages, réglages, profils, outils et actions rapides, pilotable au clavier (`↑↓`, `Entrée`, `Échap`) ou depuis la barre latérale.
- **Applications › Mettre à jour** : nouvel onglet qui liste vos logiciels installés disposant d'une mise à jour via `winget`, avec version actuelle → disponible et mise à jour groupée en un clic. Catalogue d'installation enrichi de 6 logiciels (52 au total : LocalSend, LibreWolf, Paint.NET, Blender, RustDesk, WinSCP).
- **Optimiser** : 8 réglages réversibles de plus (66 au total) : allégement d'Edge (barre latérale, shopping, télémétrie), synchro cloud du presse-papiers, offres OneDrive dans l'Explorateur, Galerie Windows 11, Aero Shake, messages détaillés au démarrage/arrêt, limitation énergétique des processus et VRR pour jeux fenêtrés. Bouton « Réinitialiser les filtres » quand aucun réglage ne correspond.
- **Nettoyer** : nouvelle catégorie pour les caches d'applications courantes (Discord, Spotify, Steam, VS Code). Dans l'analyseur d'espace, bouton « Plus gros fichiers » (> 50 Mo) et bouton pour ouvrir n'importe quel dossier ou fichier dans l'Explorateur.
- **Diagnostic** : nouvelles cartes « Réseau actif » (vitesse de liaison, IP, passerelle et batterie sur portable) et « Stabilité (30 derniers jours) » (arrêts imprévus Kernel-Power 41 et écrans bleus BugCheck 1001 lus dans le journal système), plus ouverture directe de l'emplacement des processus gourmands.
- **Outils** : 3 nouveaux outils : déblocage de la file d'attente d'impression, diagnostic rapide de la connexion réseau et vérification de l'état d'activation de Windows.
- **Accueil** : barre d'actions rapides en 4 raccourcis directs et état de la batterie sur PC portable.

### Visuel et fluidité

- **Mode léger adaptatif** : coupe le verre dépoli, le flou et les animations (activé automatiquement si Windows désactive les animations ou sans accélération matérielle, et basculable dans Outils ou `Ctrl+K`).
- **Compromis verre dépoli / légèreté** : mise en cache GPU (`BitmapCache`) des halos ambiants et du logo, pinceaux figés et mis en cache dans la jauge circulaire et les courbes (zéro allocation par image), courbes lissées avec point lumineux en direct, transitions de page en douceur et survols fluides.
- **Listes virtualisées** : défilement fluide au pixel avec recyclage des conteneurs sur Optimiser, Démarrage, Nettoyer et Applications.

### Optimisations et correctifs

- Démarrage plus rapide : les requêtes WMI de `SysInfo` tournent en parallèle ; le benchmark DNS interroge les 5 serveurs en parallèle (~5× plus rapide) ; l'analyse de nettoyage parallélise sur SSD et reste séquentielle sur disque dur mécanique (HDD).
- L'échantillonnage toutes les 5 s sur l'Accueil (processus et espace disque) ne bloque plus le thread d'interface ; la mémoire est lue en un seul appel noyau.
- Regroupement des écritures `state.json` (`BeginBatch`/`EndBatch`) lors de l'application d'un profil ou d'une annulation complète.
- Correction de fuites de handles `Process` dans les mesures, le rapport système, la détection des navigateurs et le redémarrage de l'Explorateur ; identifiants de requêtes DNS sûrs en concurrence.
- 11 auto-tests de plus (137 au total).

## 26.11.2

**OptiWin devient OptiTech** : l'application ne s'occupe plus seulement de Windows. Réglages, sauvegardes et historique de l'ancienne version sont repris automatiquement au premier lancement.

### Fonctionnalités

- **Téléviseur** (nouvelle page) : allège un téléviseur ou un boîtier Android TV ou Google TV depuis le PC. Recherche des appareils sur le réseau local, connexion guidée, puis un plan classé par groupes (publicités, pistage, extras du fabricant, applications imposées, assistant vocal). Les applications sont désactivées, jamais désinstallées, et la liste est mémorisée avant chaque changement : tout se réactive un par un ou d'un coup.
- **Téléviseur** : vidage des caches avec l'espace gagné, fermeture des applications en arrière-plan, animations accélérées (réversible), redémarrage, mémoire disponible avant et après. Le lanceur en service, la diffusion, les mises à jour, les entrées HDMI et les services Google ne sont jamais proposés.
- **Mises à jour depuis GitHub** : une nouvelle version est annoncée dans la barre latérale, téléchargée en HTTPS, contrôlée par son empreinte SHA-256, puis installée avec redémarrage de l'application. Recherche automatique une fois par jour au plus, désactivable, et bouton de recherche dans la page Outils.
- L'ancienne tâche d'entretien hebdomadaire créée par OptiWin est reconnue et remplacée proprement.

### Visuel

- **Verre dépoli partout** : cartes et panneaux translucides à liseré lumineux, halos de couleur en arrière-plan, fond acrylique de Windows 11 quand il est disponible.
- Le contenu se floute derrière les dialogues.
- Nouveau logo : pastille de verre en dégradé turquoise, bleu et violet. Boutons, interrupteurs, jauge et barres de progression reprennent ce dégradé.
- Coins plus ronds, page active soulignée d'un liseré de verre.

### Technique

- 13 auto-tests de plus (126 au total) : lecture des releases GitHub, empreintes, échange des exécutables, migration des données, catalogue et plan du téléviseur, validation de tout ce qui entre dans une commande adb.
- L'icône est produite par `tools/make-icon.py`.

## 26.11.1

### Fonctionnalités

- **Optimiser** : 8 réglages de plus (58 au total), tous réversibles : cache NTFS élargi, optimisations plein écran, contenus promotionnels de Windows, rapports d'erreurs, secondes dans l'horloge, Explorateur compact, fichiers récents, bouton Chat. Les réglages contestés restent hors de tout profil.
- **Optimiser** : filtre par état (appliqués / non appliqués), recherche qui couvre aussi la catégorie, compteur « N affichés · N appliqués », message quand rien ne correspond, et le profil conseillé pour ce PC est signalé sur sa carte.
- **Démarrage** : filtre par nom ou éditeur, bouton « Couper les superflus » qui désactive d'un coup tout ce qui est marqué comme tel, et un bouton pour ouvrir l'emplacement de chaque programme.
- **Nettoyer** : « Tout cocher » et « Tout décocher » (les choix définitifs ne se cochent jamais d'un bloc), progression catégorie par catégorie pendant l'analyse, date du dernier nettoyage.
- **Applications** : recherche dans les logiciels à installer, 10 logiciels de plus (46 au total : Spotify, Telegram, GIMP, Inkscape, Obsidian, Acrobat Reader, GOG Galaxy, qBittorrent, Windows Terminal, VirtualBox), 4 applications Windows de plus proposées au retrait, jamais cochées d'office.
- **Diagnostic** : nouvelle carte « Ce qui occupe la mémoire en ce moment », instances regroupées par programme.
- **Outils** : test de la mémoire vive (Diagnostic de mémoire Windows, programmé pour le prochain redémarrage), rapport d'énergie, réparation du Microsoft Store. La carte de l'entretien automatique dit s'il est actif.
- **Accueil** : signale un redémarrage en attente après une mise à jour, et rappelle qu'un SSD reste la vraie amélioration d'un PC sur disque dur. La mémoire s'affiche aussi en pourcentage.
- **Historique** : filtre du journal, export dans un fichier texte, accès au dossier des journaux détaillés.
- **Raccourcis clavier** : Ctrl+1 à Ctrl+0 pour changer de page, F5 pour actualiser, Ctrl+F pour chercher, Ctrl+T pour le thème, Échap pour fermer un dialogue. La liste est rappelée en survolant le logo.
- La fenêtre retrouve sa taille, sa position et son état d'une session à l'autre, et une seule fenêtre d'OptiWin s'ouvre à la fois.

### Visuel

- Navigation regroupée en trois sections (Entretenir, Installer, Comprendre), icône de la page active en couleur d'accent.
- Halo de couleur derrière le score de santé, icônes sur les mesures en direct, pastilles d'icônes sur les cartes d'outils.
- Dialogues qui s'ouvrent en douceur, messages éphémères qui glissent depuis le bas et restent plus longtemps quand ils sont longs, lignes qui réagissent au survol, interrupteurs qui répondent sous la souris.
- États vides explicites pour chaque liste, texte d'aide sur les boutons.

### Optimisations et correctifs

- La recherche de la page Optimiser ne reconstruit la liste qu'une fois la frappe terminée.
- Les objets processus de l'accueil sont libérés après comptage, et le compteur de réglages se met à jour sans reconstruire la liste.
- Téléchargement de Windows : un emplacement d'ISO choisi à la main n'est plus écrasé quand on change de langue ou d'architecture.
- Le survol du bouton Agrandir ne peut plus déborder sur un message de fenêtre 64 bits.
- 9 auto-tests de plus (113 au total), dont la vérification du numéro de version compilé.

## 26.3

Première version publique.
