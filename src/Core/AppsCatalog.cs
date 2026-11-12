using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OptiWin
{
    public sealed class BloatApp : Bindable
    {
        public string Pattern;
        public string Label { get; set; }
        public string Note { get; set; }
        public string Package;
        public string PackageName;
        public bool Recommended { get; set; }
        bool sel;
        public bool Selected { get { return sel; } set { Set(ref sel, value); } }
    }

    public sealed class SoftApp : Bindable
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Note { get; set; }
        public string Group { get; set; }
        bool sel;
        public bool Selected { get { return sel; } set { Set(ref sel, value); } }
    }

    /// <summary>
    /// Liste fermée : seules les applications reconnues ici sont proposées au retrait.
    /// Le Store, l'installateur d'applications (winget), Sécurité Windows et les bibliothèques
    /// système n'y figureront jamais.
    /// </summary>
    public static class AppsCatalog
    {
        static BloatApp B(string pat, string label, bool rec, string note)
        { return new BloatApp { Pattern = pat, Label = label, Recommended = rec, Note = note, Selected = rec }; }

        static List<BloatApp> Templates()
        {
            return new List<BloatApp>
            {
                B("Microsoft.BingNews", "Actualités", true, "Flux d'actualités MSN"),
                B("Microsoft.BingWeather", "Météo", false, "Pratique pour certains"),
                B("Microsoft.BingSearch", "Recherche Bing", true, "Intégration Bing"),
                B("Microsoft.GetHelp", "Obtenir de l'aide", true, "Rarement utilisé"),
                B("Microsoft.Getstarted", "Astuces", true, "Promotion de fonctionnalités"),
                B("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (raccourci)", true, "Publicité pour l'abonnement"),
                B("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", true, "Contient de la publicité"),
                B("Microsoft.People", "Contacts", true, "Application obsolète"),
                B("Microsoft.PowerAutomateDesktop", "Power Automate", true, "Automatisation pour professionnels"),
                B("Microsoft.Todos", "To Do", false, "Listes de tâches"),
                B("Microsoft.WindowsFeedbackHub", "Hub de commentaires", true, "Envoi d'avis à Microsoft"),
                B("Microsoft.WindowsMaps", "Cartes", true, "Application obsolète"),
                B("Microsoft.WindowsSoundRecorder", "Enregistreur vocal", false, ""),
                B("Microsoft.YourPhone", "Mobile connecté", false, "Liaison avec un téléphone"),
                B("Microsoft.ZuneMusic", "Lecteur multimédia", false, "Lecture audio de Windows"),
                B("Microsoft.ZuneVideo", "Films et TV", true, "Remplacé par le Lecteur"),
                B("Microsoft.XboxApp", "Xbox (ancienne)", false, "Utile pour Game Pass"),
                B("Microsoft.GamingApp", "Xbox", false, "Utile pour Game Pass"),
                B("Microsoft.XboxGamingOverlay", "Xbox Game Bar", false, "Capture et surcouche en jeu"),
                B("Microsoft.XboxGameOverlay", "Xbox Game Overlay", false, ""),
                B("Microsoft.XboxSpeechToTextOverlay", "Xbox Speech to Text", true, ""),
                B("Microsoft.Xbox.TCUI", "Xbox TCUI", false, "Nécessaire à certains jeux Xbox"),
                B("Microsoft.XboxIdentityProvider", "Xbox Identity", false, "Connexion aux jeux Xbox"),
                B("Microsoft.MixedReality.Portal", "Portail de réalité mixte", true, ""),
                B("Microsoft.Microsoft3DViewer", "Visionneuse 3D", true, ""),
                B("Microsoft.MSPaint", "Paint 3D", true, "Paint classique non concerné"),
                B("Microsoft.SkypeApp", "Skype", true, "Service arrêté par Microsoft"),
                B("Microsoft.Office.OneNote", "OneNote (Store)", false, ""),
                B("Microsoft.Wallet", "Portefeuille", true, ""),
                B("Microsoft.549981C3F5F10", "Cortana", true, "Service arrêté"),
                B("Microsoft.Copilot", "Copilot", false, "Assistant IA de Microsoft"),
                B("Microsoft.Windows.DevHome", "Dev Home", true, "Pour développeurs, abandonné"),
                B("Microsoft.OutlookForWindows", "Outlook (nouveau)", false, ""),
                B("MicrosoftCorporationII.MicrosoftFamily", "Family Safety", true, "Contrôle parental"),
                B("MicrosoftCorporationII.QuickAssist", "Assistance rapide", false, "Aide à distance"),
                B("MicrosoftTeams", "Teams (ancien)", true, ""),
                B("MSTeams", "Teams", false, "Si vous n'utilisez pas Teams"),
                B("Clipchamp.Clipchamp", "Clipchamp", true, "Montage vidéo en ligne"),
                B("SpotifyAB.SpotifyMusic", "Spotify", true, "Installé d'office"),
                B("Disney.", "Disney+", true, "Installé d'office"),
                B("king.com.", "Jeux King (Candy Crush...)", true, "Installés d'office"),
                B("Facebook.", "Facebook / Instagram", true, "Installés d'office"),
                B("BytedancePte.", "TikTok", true, "Installé d'office"),
                B("AmazonVideo.PrimeVideo", "Prime Video", true, "Installé d'office"),
                B("Netflix", "Netflix", false, ""),
                B("22364Disney.", "Disney+", true, "Installé d'office"),
                B("Microsoft.Advertising.Xaml", "Bibliothèque publicitaire", true, "Ancienne, inutile"),
                B("Microsoft.WindowsAlarms", "Horloge", false, "Alarmes, minuteurs, horloges mondiales"),
                B("Microsoft.MicrosoftStickyNotes", "Pense-bêtes", false, "Notes sur le bureau"),
                B("Microsoft.WindowsCamera", "Caméra", false, "Inutile sans webcam"),
                B("Microsoft.Whiteboard", "Whiteboard", false, "Tableau blanc collaboratif"),
            };
        }

        /// <summary>Applications présentes sur ce PC et reconnues par la liste.</summary>
        public static List<BloatApp> ListInstalled(out string error)
        {
            var o = PS.Run("Get-AppxPackage | ForEach-Object { $_.Name + '|' + $_.PackageFullName }", out error);
            var t = Templates();
            var res = new List<BloatApp>();
            foreach (var line in o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('|');
                if (p.Length != 2) continue;
                var m = t.FirstOrDefault(x => x.Pattern.EndsWith(".") ? p[0].StartsWith(x.Pattern, StringComparison.OrdinalIgnoreCase) : string.Equals(p[0], x.Pattern, StringComparison.OrdinalIgnoreCase));
                if (m == null) continue;
                res.Add(new BloatApp { Pattern = m.Pattern, Label = m.Label, Note = m.Note, Recommended = m.Recommended, Selected = m.Recommended, Package = p[1], PackageName = p[0] });
            }
            return res.OrderByDescending(x => x.Recommended).ThenBy(x => x.Label).ToList();
        }

        public static void Remove(BloatApp a, bool provisioned, Job job)
        {
            string err;
            job.Log("Retrait : " + a.Label);
            PS.Run("Remove-AppxPackage -Package " + PS.Q(a.Package) + " -ErrorAction Stop", out err, job.Token);
            if (!string.IsNullOrWhiteSpace(err)) job.Log("  " + err.Trim());
            if (provisioned && Os.IsAdmin)
            {
                // Évite que l'application revienne pour les nouveaux comptes et après les grosses mises à jour.
                PS.Run("Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq " + PS.Q(a.PackageName) + " | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null", out err, job.Token);
            }
            Store.AddHistory("app:" + a.PackageName, "Application : " + a.Label, "Retirée (réinstallable depuis le Store)");
        }

        public static readonly string[] BloatPrefixesForImage = Templates().Where(t => t.Recommended).Select(t => t.Pattern).ToArray();
        public static List<string> AllPatterns() { return Templates().Select(t => t.Pattern).Distinct().ToList(); }

        static SoftApp S(string id, string label, string group, string note) { return new SoftApp { Id = id, Label = label, Group = group, Note = note }; }

        public static List<SoftApp> Software()
        {
            return new List<SoftApp>
            {
                S("Mozilla.Firefox", "Firefox", "Navigateurs", "Navigateur indépendant"),
                S("Google.Chrome", "Chrome", "Navigateurs", ""),
                S("Brave.Brave", "Brave", "Navigateurs", "Bloqueur de pubs intégré"),

                S("7zip.7zip", "7-Zip", "Utilitaires", "Archives"),
                S("voidtools.Everything", "Everything", "Utilitaires", "Recherche instantanée de fichiers"),
                S("Microsoft.PowerToys", "PowerToys", "Utilitaires", "Boîte à outils Microsoft"),
                S("Notepad++.Notepad++", "Notepad++", "Utilitaires", "Éditeur de texte"),
                S("ShareX.ShareX", "ShareX", "Utilitaires", "Captures d'écran"),
                S("SumatraPDF.SumatraPDF", "Sumatra PDF", "Utilitaires", "Lecteur PDF très léger"),
                S("qBittorrent.qBittorrent", "qBittorrent", "Utilitaires", "Client BitTorrent libre, sans publicité"),

                S("VideoLAN.VLC", "VLC", "Multimédia", "Lit tout"),
                S("OBSProject.OBSStudio", "OBS Studio", "Multimédia", "Enregistrement et diffusion"),
                S("HandBrake.HandBrake", "HandBrake", "Multimédia", "Conversion vidéo"),
                S("Audacity.Audacity", "Audacity", "Multimédia", "Édition audio"),
                S("KDE.Krita", "Krita", "Multimédia", "Dessin"),
                S("GIMP.GIMP", "GIMP", "Multimédia", "Retouche d'images"),
                S("Inkscape.Inkscape", "Inkscape", "Multimédia", "Dessin vectoriel"),
                S("Spotify.Spotify", "Spotify", "Multimédia", "Musique en streaming"),

                S("TheDocumentFoundation.LibreOffice", "LibreOffice", "Bureautique", "Suite bureautique gratuite"),
                S("Mozilla.Thunderbird", "Thunderbird", "Bureautique", "Messagerie"),
                S("Adobe.Acrobat.Reader.64-bit", "Acrobat Reader", "Bureautique", "Lecteur PDF d'Adobe"),
                S("Obsidian.Obsidian", "Obsidian", "Bureautique", "Notes et base de connaissances"),

                S("Discord.Discord", "Discord", "Communication", ""),
                S("OpenWhisperSystems.Signal", "Signal", "Communication", "Messagerie chiffrée"),
                S("Zoom.Zoom", "Zoom", "Communication", ""),
                S("Telegram.TelegramDesktop", "Telegram", "Communication", ""),

                S("Valve.Steam", "Steam", "Jeux", ""),
                S("EpicGames.EpicGamesLauncher", "Epic Games", "Jeux", ""),
                S("GOG.Galaxy", "GOG Galaxy", "Jeux", "Jeux sans DRM"),

                S("Bitwarden.Bitwarden", "Bitwarden", "Sécurité", "Gestionnaire de mots de passe"),
                S("KeePassXCTeam.KeePassXC", "KeePassXC", "Sécurité", "Mots de passe, hors ligne"),
                S("Malwarebytes.Malwarebytes", "Malwarebytes", "Sécurité", "Analyse complémentaire"),

                S("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "Diagnostic", "Santé des disques"),
                S("REALiX.HWiNFO", "HWiNFO", "Diagnostic", "Matériel et températures"),
                S("AntibodySoftware.WizTree", "WizTree", "Diagnostic", "Espace disque"),
                S("Klocman.BulkCrapUninstaller", "BCUninstaller", "Diagnostic", "Désinstallation en masse"),
                S("Microsoft.Sysinternals.Autoruns", "Autoruns", "Diagnostic", "Démarrage en détail"),

                S("Ventoy.Ventoy", "Ventoy", "Clés USB", "Clé multiboot"),
                S("Rufus.Rufus", "Rufus", "Clés USB", "Référence des clés bootables"),

                S("Microsoft.VisualStudioCode", "VS Code", "Développement", ""),
                S("Git.Git", "Git", "Développement", ""),
                S("Python.Python.3.13", "Python 3.13", "Développement", ""),
                S("Microsoft.WindowsTerminal", "Windows Terminal", "Développement", "Terminal moderne à onglets"),
                S("Oracle.VirtualBox", "VirtualBox", "Développement", "Machines virtuelles"),

                S("Microsoft.VCRedist.2015+.x64", "Visual C++ 2015+", "Composants", "Requis par beaucoup de jeux"),
                S("Microsoft.DotNet.DesktopRuntime.8", ".NET Desktop 8", "Composants", "Requis par certaines applications"),
            };
        }

        public static bool WingetAvailable()
        {
            try { return Proc.Run("winget.exe", "--version", null) == 0; } catch { return false; }
        }

        public static int Install(SoftApp a, Job job)
        {
            job.Status("Installation de " + a.Label);
            return Proc.Run("winget.exe", "install --id \"" + a.Id + "\" --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                l => { if (l.Length > 3 && !l.Contains("█") && !l.Contains("▒")) job.Log(l.Trim()); }, job.Token);
        }
    }
}
