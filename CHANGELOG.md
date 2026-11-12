# Journal des versions

Le numéro de version suit la date : `26.11.1` est la première version publiée en novembre 2026.

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
