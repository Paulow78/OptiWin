using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace OptiWin
{
    /// <summary>
    /// Catalogue des réglages. Volontairement absents, car inefficaces ou dangereux :
    /// « nettoyeur de registre », « booster de RAM », désactivation de Defender ou de Windows Update,
    /// vidage de Prefetch, désactivation des atténuations Spectre/Meltdown.
    /// </summary>
    public static class TweakCatalog
    {
        const string ADV = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string POL = @"SOFTWARE\Policies\Microsoft\Windows";
        const string DESK = @"Control Panel\Desktop";
        const string THEME = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string SEARCH = @"Software\Microsoft\Windows\CurrentVersion\Search";

        public const string CatPerf = "Performance", CatPriv = "Confidentialité", CatUi = "Interface",
                            CatNet = "Réseau et mises à jour", CatGame = "Jeux", CatSvc = "Services";
        public static readonly string[] Categories = { CatPerf, CatGame, CatPriv, CatUi, CatNet, CatSvc };

        static RegOp U(string key, string name, int v, int? d)
        { return new RegOp(RegistryHive.CurrentUser, key, name, RegistryValueKind.DWord, v, d.HasValue ? (object)d.Value : null); }
        static RegOp U(string key, string name, string v, string d)
        { return new RegOp(RegistryHive.CurrentUser, key, name, RegistryValueKind.String, v, d); }
        static RegOp M(string key, string name, int v, int? d)
        { return new RegOp(RegistryHive.LocalMachine, key, name, RegistryValueKind.DWord, v, d.HasValue ? (object)d.Value : null); }
        static RegOp UB(string key, string name, byte[] v, byte[] d)
        { return new RegOp(RegistryHive.CurrentUser, key, name, RegistryValueKind.Binary, v, d); }

        static Tweak T(string id, string cat, string title, string desc, Risk r, Prof p, Effect e, params TweakOp[] ops)
        { return new Tweak { Id = id, Cat = cat, Title = title, Desc = desc, Risk = r, Profiles = p, Effect = e, Ops = ops.ToList() }; }

        static bool OnHdd { get { return SysInfo.Current.SystemDiskIsHdd; } }
        static bool NotOnHdd { get { return !SysInfo.Current.SystemDiskIsHdd; } }
        static bool Desktop { get { return !SysInfo.Current.IsLaptop; } }

        // Un seul jeu d'objets, quel que soit le thread qui l'ouvre en premier : l'interface y est liée.
        static readonly Lazy<List<Tweak>> all = new Lazy<List<Tweak>>(Build);
        public static List<Tweak> All { get { return all.Value; } }
        public static Tweak ById(string id) { return All.FirstOrDefault(t => t.Id == id); }

        public static IEnumerable<Tweak> ForProfile(Prof p)
        {
            return All.Where(t => (t.Profiles & p) != 0 && t.IsSupported && t.FitsThisPc && !t.Blocked);
        }

        static List<Tweak> Build()
        {
            const Prof All5 = Prof.OldPc | Prof.Fresh | Prof.Gaming | Prof.Laptop | Prof.Privacy;
            var list = new List<Tweak>
            {
                // ───────────── PERFORMANCE ─────────────
                T("perf.visual", CatPerf, "Effets visuels allégés",
                  "Supprime animations et ombres, garde le lissage des polices. Gain net sur les vieilles cartes graphiques.",
                  Risk.Safe, Prof.OldPc, Effect.Logoff,
                  U(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3, 0),
                  UB(DESK, "UserPreferencesMask", new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0, 0, 0 }, new byte[] { 0x9E, 0x1E, 0x07, 0x80, 0x12, 0, 0, 0 }),
                  U(DESK + @"\WindowMetrics", "MinAnimate", "0", "1"),
                  U(ADV, "TaskbarAnimations", 0, 1),
                  U(ADV, "ListviewAlphaSelect", 0, 1),
                  U(ADV, "ListviewShadow", 0, 1),
                  U(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0, 1)),

                T("perf.transparency", CatPerf, "Désactiver la transparence",
                  "Retire l'effet translucide de la barre des tâches et des menus. Soulage le processeur graphique.",
                  Risk.Safe, Prof.OldPc | Prof.Laptop, Effect.Immediate, U(THEME, "EnableTransparency", 0, 1)),

                T("perf.menudelay", CatPerf, "Menus plus réactifs",
                  "Réduit le délai d'ouverture des sous-menus de 400 à 50 ms.",
                  Risk.Safe, Prof.OldPc | Prof.Fresh | Prof.Gaming, Effect.Logoff, U(DESK, "MenuShowDelay", "50", "400")),

                T("perf.startdelay", CatPerf, "Sans délai au démarrage des applications",
                  "Windows attend normalement quelques secondes avant de lancer les programmes de démarrage. Utile surtout sur SSD.",
                  Risk.Safe, Prof.Fresh | Prof.Gaming, Effect.Immediate,
                  U(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null),
                  U(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "WaitForIdleState", 0, null))
                  .If(() => NotOnHdd),

                T("perf.bgapps", CatPerf, "Bloquer les applications en arrière-plan",
                  "Les applications du Store ne tournent plus en tâche de fond. Certaines notifications peuvent arriver plus tard.",
                  Risk.Moderate, Prof.OldPc | Prof.Laptop, Effect.Immediate,
                  U(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0),
                  U(SEARCH, "BackgroundAppGlobalToggle", 0, null)),

                T("perf.widgets", CatPerf, "Désactiver les widgets et les actualités",
                  "Supprime le panneau d'actualités de la barre des tâches, qui garde un navigateur web en mémoire.",
                  Risk.Safe, All5, Effect.Logoff,
                  M(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null),
                  M(POL + @"\Windows Feeds", "EnableFeeds", 0, null)).MinB(19041).Manual("ms-settings:taskbar"),

                T("perf.edge", CatPerf, "Empêcher Edge de rester en mémoire",
                  "Coupe le préchargement d'Edge et son activité une fois fermé. Edge affichera « géré par votre organisation ».",
                  Risk.Safe, Prof.OldPc | Prof.Laptop | Prof.Gaming, Effect.Immediate,
                  M(@"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, null),
                  M(@"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, null)),

                T("perf.search", CatPerf, "Arrêter l'indexation de la recherche",
                  "Évite le disque à 100 % sur un disque dur. La recherche reste possible, mais les résultats seront plus lents.",
                  Risk.Moderate, Prof.OldPc, Effect.Immediate, new ServiceOp("WSearch", 4, 2)).If(() => OnHdd),

                T("perf.sysmain", CatPerf, "Désactiver SysMain (Superfetch)",
                  "À essayer si le disque reste saturé. Peu utile sur SSD, et peut ralentir l'ouverture des applications sur HDD avec assez de RAM.",
                  Risk.Moderate, Prof.None, Effect.Immediate, new ServiceOp("SysMain", 4, 2)),

                T("perf.lastaccess", CatPerf, "Ne plus enregistrer la date de dernier accès",
                  "Évite une écriture sur le disque à chaque lecture de fichier. Gain réel sur disque dur.",
                  Risk.Safe, Prof.OldPc, Effect.Reboot,
                  M(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", unchecked((int)0x80000001), unchecked((int)0x80000002))).If(() => OnHdd),

                T("perf.hibernate", CatPerf, "Désactiver la veille prolongée",
                  "Libère plusieurs Go (hiberfil.sys) sur un petit disque. Désactive aussi le démarrage rapide.",
                  Risk.Moderate, Prof.None, Effect.Immediate,
                  new CmdOp("powercfg.exe", "/h off", "/h on",
                      () => Reg.GetInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") == 0,
                      "powercfg /h off")),

                T("perf.faststart", CatPerf, "Désactiver le démarrage rapide",
                  "Un arrêt devient un vrai arrêt. Le démarrage à froid sera un peu plus long, mais règle bien des pilotes capricieux.",
                  Risk.Moderate, Prof.None, Effect.Reboot,
                  M(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)),

                T("perf.storagesense", CatPerf, "Activer l'assistant de stockage",
                  "Windows supprime automatiquement les fichiers temporaires inutiles quand l'espace vient à manquer.",
                  Risk.Safe, Prof.Fresh | Prof.OldPc, Effect.Immediate,
                  U(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, 0)).MinB(17763),

                // ───────────── JEUX ─────────────
                T("game.mode", CatGame, "Activer le mode Jeu",
                  "Windows donne la priorité au jeu et suspend les mises à jour de pilotes pendant la partie.",
                  Risk.Safe, Prof.Gaming, Effect.Immediate,
                  U(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null),
                  U(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, null)),

                T("game.dvr", CatGame, "Couper l'enregistrement en arrière-plan",
                  "Désactive Game DVR, qui encode en permanence les dernières minutes de jeu.",
                  Risk.Safe, Prof.Gaming | Prof.OldPc, Effect.Logoff,
                  U(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                  U(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, null),
                  M(POL + @"\GameDVR", "AllowGameDVR", 0, null)),

                T("game.hags", CatGame, "Planification GPU accélérée (HAGS)",
                  "Réduit la latence lorsque la carte graphique et son pilote le gèrent. Sans effet sinon.",
                  Risk.Moderate, Prof.Gaming, Effect.Reboot,
                  M(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1)).MinB(19041),

                T("game.mouse", CatGame, "Souris sans accélération",
                  "Le déplacement du curseur devient proportionnel au geste. Précieux pour viser.",
                  Risk.Safe, Prof.Gaming, Effect.Logoff,
                  U(@"Control Panel\Mouse", "MouseSpeed", "0", "1"),
                  U(@"Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                  U(@"Control Panel\Mouse", "MouseThreshold2", "0", "10")),

                T("game.highperf", CatGame, "Plan d'alimentation Performances élevées",
                  "Le processeur reste à sa fréquence haute. À réserver à un PC fixe : consommation et chaleur augmentent.",
                  Risk.Moderate, Prof.Gaming, Effect.Immediate, new PowerPlanOp()).If(() => Desktop),

                T("game.hvci", CatGame, "Désactiver l'intégrité de la mémoire",
                  "Gain possible en jeu, documenté par Microsoft, mais baisse réelle de la protection contre les pilotes malveillants.",
                  Risk.Advanced, Prof.None, Effect.Reboot,
                  M(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, 1))
                  .MinB(19041).Warn("Ce réglage réduit la sécurité de Windows. Ne l'activez que si vous en avez un besoin précis."),

                // ───────────── CONFIDENTIALITÉ ─────────────
                T("priv.telemetry", CatPriv, "Réduire la télémétrie au minimum",
                  "Limite les données de diagnostic envoyées à Microsoft et arrête le service DiagTrack. À éviter sur un PC Windows Insider.",
                  Risk.Safe, Prof.OldPc | Prof.Fresh | Prof.Privacy | Prof.Gaming, Effect.Reboot,
                  M(POL + @"\DataCollection", "AllowTelemetry", 0, null),
                  M(POL + @"\DataCollection", "LimitDiagnosticLogCollection", 1, null),
                  new ServiceOp("DiagTrack", 4, 2)).If(() => !Os.IsInsider),

                T("priv.ads", CatPriv, "Couper l'identifiant publicitaire",
                  "Les applications ne peuvent plus vous suivre pour personnaliser leurs publicités.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy, Effect.Immediate,
                  U(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1),
                  U(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1),
                  U(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, null)),

                T("priv.suggest", CatPriv, "Stopper suggestions et applications installées d'office",
                  "Plus de publicités dans le menu Démarrer ni de Candy Crush qui réapparaît après une mise à jour.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy | Prof.OldPc, Effect.Immediate,
                  U(CDM, "SilentInstalledAppsEnabled", 0, 1),
                  U(CDM, "SystemPaneSuggestionsEnabled", 0, 1),
                  U(CDM, "SoftLandingEnabled", 0, 1),
                  U(CDM, "OemPreInstalledAppsEnabled", 0, 1),
                  U(CDM, "PreInstalledAppsEnabled", 0, 1),
                  U(CDM, "RotatingLockScreenOverlayEnabled", 0, 1),
                  U(CDM, "SubscribedContent-338387Enabled", 0, 1),
                  U(CDM, "SubscribedContent-338388Enabled", 0, 1),
                  U(CDM, "SubscribedContent-338389Enabled", 0, 1),
                  U(CDM, "SubscribedContent-338393Enabled", 0, 1),
                  U(CDM, "SubscribedContent-353694Enabled", 0, 1),
                  U(CDM, "SubscribedContent-353696Enabled", 0, 1)),

                T("priv.startrecs", CatPriv, "Retirer les recommandations du menu Démarrer",
                  "Masque les conseils et les promotions d'applications dans le menu Démarrer de Windows 11.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy, Effect.Explorer, U(ADV, "Start_IrisRecommendations", 0, 1)).MinB(22621),

                T("priv.bing", CatPriv, "Retirer Bing de la recherche Windows",
                  "Le menu Démarrer ne cherche plus que sur le PC. Plus rapide, et rien n'est envoyé sur le web.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy | Prof.OldPc, Effect.Explorer,
                  U(SEARCH, "BingSearchEnabled", 0, null),
                  U(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)).MinB(19041),

                T("priv.activity", CatPriv, "Désactiver l'historique d'activité",
                  "Windows ne conserve plus la liste des applications et documents utilisés.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy, Effect.Immediate,
                  M(POL + @"\System", "EnableActivityFeed", 0, null),
                  M(POL + @"\System", "PublishUserActivities", 0, null),
                  M(POL + @"\System", "UploadUserActivities", 0, null)),

                T("priv.copilot", CatPriv, "Masquer Copilot",
                  "Retire le bouton et désactive l'intégration de Copilot dans Windows.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy | Prof.OldPc, Effect.Explorer,
                  U(ADV, "ShowCopilotButton", 0, null),
                  U(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                  M(POL + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1, null)).MinB(22621),

                T("priv.recall", CatPriv, "Désactiver Recall",
                  "Empêche la capture régulière de l'écran sur les PC compatibles Copilot+.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy, Effect.Reboot,
                  M(POL + @"\WindowsAI", "DisableAIDataAnalysis", 1, null),
                  M(POL + @"\WindowsAI", "AllowRecallEnablement", 0, null)).MinB(26100),

                T("priv.cortana", CatPriv, "Désactiver Cortana", "Retire Cortana de la recherche de Windows 10.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy | Prof.OldPc, Effect.Logoff,
                  M(POL + @"\Windows Search", "AllowCortana", 0, null)).MaxB(21999),

                T("priv.feedback", CatPriv, "Stopper les demandes d'avis",
                  "Windows ne vous interrompt plus pour vous demander votre opinion.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy, Effect.Immediate,
                  U(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null),
                  M(POL + @"\DataCollection", "DoNotShowFeedbackNotifications", 1, null)),

                T("priv.inking", CatPriv, "Ne plus analyser la saisie",
                  "Windows cesse de collecter vos frappes et votre écriture pour personnaliser le clavier.",
                  Risk.Safe, Prof.Privacy, Effect.Immediate,
                  U(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, null),
                  U(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, null),
                  U(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, null),
                  U(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, null)),

                T("priv.ceip", CatPriv, "Couper les tâches de collecte de données",
                  "Désactive les tâches planifiées du programme d'amélioration de l'expérience utilisateur.",
                  Risk.Safe, Prof.OldPc | Prof.Privacy, Effect.Immediate,
                  new TaskOp(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
                  new TaskOp(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
                  new TaskOp(@"\Microsoft\Windows\Autochk\Proxy"),
                  new TaskOp(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"),
                  new TaskOp(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"),
                  new TaskOp(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector")),

                T("priv.location", CatPriv, "Refuser la localisation aux applications",
                  "Coupe l'accès à la position pour toutes les applications. La météo locale et « Localiser mon appareil » cesseront de fonctionner.",
                  Risk.Moderate, Prof.None, Effect.Immediate,
                  new RegOp(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", RegistryValueKind.String, "Deny", "Allow")),

                // ───────────── INTERFACE ─────────────
                T("ui.ext", CatUi, "Afficher les extensions de fichiers",
                  "Indispensable pour repérer un faux document qui serait en réalité un programme.",
                  Risk.Safe, Prof.Fresh, Effect.Explorer, U(ADV, "HideFileExt", 0, 1)),
                T("ui.hidden", CatUi, "Afficher les fichiers cachés", "Rend visibles les dossiers comme AppData.",
                  Risk.Safe, Prof.None, Effect.Explorer, U(ADV, "Hidden", 1, 2)),
                T("ui.thispc", CatUi, "L'Explorateur s'ouvre sur « Ce PC »", "Plutôt que sur l'accueil et les fichiers récents.",
                  Risk.Safe, Prof.Fresh, Effect.Explorer, U(ADV, "LaunchTo", 1, 2)),
                T("ui.classic", CatUi, "Menu contextuel complet (clic droit)",
                  "Retrouve directement toutes les options, sans passer par « Afficher d'autres options ».",
                  Risk.Safe, Prof.Fresh, Effect.Explorer,
                  new RegOp(RegistryHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", RegistryValueKind.String, "", null)
                      .PruneUpTo(@"Software\Classes\CLSID")).MinB(22000),
                T("ui.left", CatUi, "Barre des tâches alignée à gauche", "Replace le menu Démarrer dans le coin, comme sur Windows 10.",
                  Risk.Safe, Prof.None, Effect.Immediate, U(ADV, "TaskbarAl", 0, 1)).MinB(22000),
                T("ui.taskview", CatUi, "Masquer le bouton Vue des tâches", "Libère de la place dans la barre des tâches.",
                  Risk.Safe, Prof.None, Effect.Immediate, U(ADV, "ShowTaskViewButton", 0, 1)),
                T("ui.searchicon", CatUi, "Recherche réduite à une icône", "Remplace le large champ de recherche de la barre des tâches.",
                  Risk.Safe, Prof.Fresh, Effect.Immediate, U(SEARCH, "SearchboxTaskbarMode", 1, 2)),
                T("ui.endtask", CatUi, "« Fin de tâche » dans la barre des tâches",
                  "Clic droit sur une application bloquée pour la fermer, sans ouvrir le Gestionnaire des tâches.",
                  Risk.Safe, Prof.Fresh | Prof.Gaming, Effect.Immediate,
                  U(ADV + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null)).MinB(22631),
                T("ui.dark", CatUi, "Thème sombre", "Pour Windows et les applications.",
                  Risk.Safe, Prof.None, Effect.Immediate, U(THEME, "AppsUseLightTheme", 0, 1), U(THEME, "SystemUsesLightTheme", 0, 1)),
                T("ui.sticky", CatUi, "Couper l'alerte des touches rémanentes", "Plus de fenêtre après cinq appuis sur Maj, notamment en jeu.",
                  Risk.Safe, Prof.Gaming, Effect.Logoff, U(@"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510")),
                T("ui.nag", CatUi, "Supprimer « Finalisons la configuration »",
                  "Cet écran plein écran qui réapparaît après les mises à jour pour vendre des services Microsoft.",
                  Risk.Safe, Prof.Fresh | Prof.Privacy, Effect.Immediate,
                  U(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null)),
                T("ui.longpaths", CatUi, "Autoriser les chemins de plus de 260 caractères", "Évite des erreurs de copie sur les arborescences profondes.",
                  Risk.Safe, Prof.Fresh, Effect.Immediate, M(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)),

                // ───────────── RÉSEAU ET MISES À JOUR ─────────────
                T("net.p2p", CatNet, "Ne plus partager les mises à jour en P2P",
                  "Votre connexion cesse de servir à envoyer des mises à jour à d'autres PC.",
                  Risk.Safe, All5, Effect.Immediate, M(POL + @"\DeliveryOptimization", "DODownloadMode", 0, null)),
                T("net.noreboot", CatNet, "Pas de redémarrage automatique en session",
                  "Windows ne redémarre plus seul pour une mise à jour tant que vous êtes connecté. Les mises à jour restent actives.",
                  Risk.Safe, Prof.Fresh | Prof.Gaming, Effect.Immediate,
                  M(POL + @"\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, null)),
                T("net.nodrivers", CatNet, "Exclure les pilotes de Windows Update",
                  "Évite qu'une mise à jour remplace un pilote qui fonctionne. Vous gérez alors vos pilotes vous-même.",
                  Risk.Moderate, Prof.None, Effect.Immediate, M(POL + @"\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, null)),

                // ───────────── SERVICES ─────────────
                T("svc.rare", CatSvc, "Passer les services rarement utiles en manuel",
                  "Cartes hors ligne, suivi des liens distribués, assistant de compatibilité. Ils se lancent encore si un programme en a besoin.",
                  Risk.Safe, Prof.OldPc, Effect.Reboot,
                  new ServiceOp("MapsBroker", 3, 2),
                  new ServiceOp("TrkWks", 3, 2),
                  new ServiceOp("PcaSvc", 3, 2)),
                T("svc.legacy", CatSvc, "Désactiver Fax et le mode démo",
                  "Services sans utilité sur un PC domestique.",
                  Risk.Safe, Prof.OldPc | Prof.Fresh, Effect.Immediate,
                  new ServiceOp("Fax", 4, 3),
                  new ServiceOp("RetailDemo", 4, 3)),
                T("svc.spooler", CatSvc, "Désactiver le service d'impression",
                  "Uniquement si vous n'avez aucune imprimante. Libère un peu de mémoire et ferme une surface d'attaque.",
                  Risk.Moderate, Prof.None, Effect.Immediate, new ServiceOp("Spooler", 4, 2)),
            };
            return list;
        }
    }
}
