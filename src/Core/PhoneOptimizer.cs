using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace OptiTech
{
    public sealed class PhoneInfo
    {
        public string Brand = "", Model = "", Android = "", Serial = "", SecurityPatch = "", Soc = "", Resolution = "";
        public int Sdk, BatteryLevel = -1;
        public double BatteryTempC = -1;
        public string BatteryHealth = "", BatteryStatus = "";
        public long StorageFree = -1, StorageTotal = -1, RamTotal = -1, RamFree = -1;

        public string Name { get { return (Capital(Brand) + " " + Model).Trim(); } }

        /// <summary>Identifiant stable du téléphone, pour retrouver ce qu'OptiTech y a désactivé.</summary>
        public string Id
        {
            get
            {
                var raw = Serial.Length > 0 ? Serial : Brand + "-" + Model;
                return new string(raw.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
            }
        }

        public string BatteryText
        {
            get
            {
                if (BatteryLevel < 0) return "";
                var s = BatteryLevel + " %";
                if (BatteryTempC > 0) s += "  ·  " + Fmt.Num(BatteryTempC, "0.0") + " °C";
                if (BatteryHealth.Length > 0) s += "  ·  " + BatteryHealth;
                return s;
            }
        }

        static string Capital(string s) { return string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1); }
    }

    public sealed class PhoneDisabledEntry
    {
        public string Package;
        public bool WasUninstalled;
        public string Raw { get { return WasUninstalled ? "u:" + Package : Package; } }
    }

    /// <summary>
    /// Optimiseur pour smartphone Android (USB ou Wi-Fi / Débogage sans fil).
    /// Mêmes garanties que sur Windows et Android TV : catalogue sûr, blocage strict des composants vitaux
    /// (appels, SMS, contacts, clavier, caméra, système, lanceur actif, Play Store), suivi par appareil
    /// avant toute modification, et rétablissement réversible en un clic.
    /// </summary>
    public static class PhoneOptimizer
    {
        public const string GAds = "Publicités, pistage et installeurs fantômes",
                            GSamsung = "Samsung (Galaxy / One UI)",
                            GXiaomi = "Xiaomi / Redmi / POCO (MIUI / HyperOS)",
                            GOppo = "Oppo / Realme / OnePlus (ColorOS / OxygenOS)",
                            GOther = "Motorola / Huawei / Honor",
                            GSocial = "Réseaux sociaux et partenaires préinstallés",
                            GGoogle = "Extras Google (optionnels)";

        static TvItem I(string pkg, string label, string group, bool rec, string note)
        { return new TvItem { Package = pkg, Label = label, Group = group, Recommended = rec, Note = note }; }

        /// <summary>Catalogue sûr et vérifié de bloatwares Android (85+ paquets).</summary>
        public static List<TvItem> Catalog()
        {
            return new List<TvItem>
            {
                // Publicités, pistage et installeurs fantômes
                I("com.facebook.system", "Facebook App Installer", GAds, true, "Service système en arrière-plan (inutile même si vous utilisez Facebook)"),
                I("com.facebook.appmanager", "Facebook App Manager", GAds, true, "Mises à jour silencieuses Meta hors Play Store"),
                I("com.facebook.services", "Facebook Services", GAds, true, "Service de fond Meta permanent"),
                I("com.aura.oobe.samsung", "AppCloud / Aura (Samsung)", GAds, true, "Installe des jeux et apps sponsorisés après les mises à jour"),
                I("com.aura.oobe.samsung.gl", "AppCloud Global (Samsung)", GAds, true, "Installeur d'applications sponsorisées"),
                I("com.dti.samsung", "Digital Turbine (Samsung)", GAds, true, "Recommandations et préinstallations publicitaires"),
                I("com.dti.motorola", "Digital Turbine (Motorola)", GAds, true, "Installeur d'applications sponsorisées"),
                I("com.ironsrc.appcloud.oobe", "IronSource AppCloud", GAds, true, "Installeur d'apps sponsorisées opérateur"),
                I("com.ironsrc.appcloud.oobe.hutchison", "IronSource AppCloud (opérateur)", GAds, true, "Installeur publicitaire"),
                I("com.google.android.feedback", "Envoi d'avis à Google", GAds, true, "Rapports de commentaires"),
                I("com.google.android.partnersetup", "Google Partner Setup", GAds, true, "Préconfiguration partenaire"),
                I("com.google.android.apps.turbo", "Device Health Services (prédictions)", GAds, false, "Estimation d'autonomie restante"),
                I("com.miui.msa.global", "MSA — Publicités MIUI/HyperOS", GAds, true, "Moteur d'affichage des publicités système Xiaomi"),
                I("com.miui.analytics", "Statistiques Xiaomi (Analytics)", GAds, true, "Collecte de télémétrie en continu"),
                I("com.miui.daemon", "MIUI Daemon", GAds, true, "Collecte d'usage en arrière-plan"),
                I("com.xiaomi.joyose", "Xiaomi Joyose", GAds, false, "Télémétrie et gestion thermique/jeux (désactiver peut affecter certains jeux)"),
                I("com.samsung.android.rubin.app", "Service de personnalisation Samsung", GAds, true, "Analyse vos habitudes d'utilisation"),
                I("com.samsung.android.ipsgeofence", "Géolocalisation publicitaire Samsung", GAds, true, "Visites de lieux pour ciblage"),
                I("com.samsung.android.mdx.kit", "Samsung Diagnostics / Kit", GAds, false, "Télémétrie Samsung"),
                I("com.heytap.mcs", "Push marketing HeyTap", GAds, true, "Notifications promotionnelles Oppo/Realme"),
                I("com.nearme.statistics.rom", "Statistiques ColorOS / Realme", GAds, true, "Mesure d'usage"),

                // Samsung (Galaxy / One UI)
                I("com.samsung.android.bixby.agent", "Bixby Voice", GSamsung, false, "Assistant vocal Samsung"),
                I("com.samsung.android.bixby.wakeup", "Réveil vocal Bixby", GSamsung, true, "Écoute en arrière-plan pour Bixby"),
                I("com.samsung.android.visionintelligence", "Bixby Vision", GSamsung, false, "Analyse visuelle Bixby"),
                I("com.samsung.android.app.spage", "Samsung Free / Daily", GSamsung, true, "Flux d'actualités sur l'écran gauche"),
                I("com.samsung.sree", "Samsung Global Goals", GSamsung, true, "Application promotionnelle préinstallée"),
                I("com.samsung.android.kidsinstaller", "Samsung Kids (installeur)", GSamsung, true, "Installeur de l'espace enfants"),
                I("com.samsung.android.tvplus", "Samsung TV Plus", GSamsung, false, "Chaînes TV gratuites Samsung"),
                I("com.samsung.android.arzone", "Zone AR Samsung", GSamsung, true, "Filtres et autocollants réalité augmentée"),
                I("com.samsung.android.aremoji", "AR Emoji", GSamsung, true, "Avatars animés"),
                I("com.samsung.android.aremojieditor", "Éditeur AR Emoji", GSamsung, true, ""),
                I("com.sec.android.mimage.avatarstickers", "Autocollants AR Emoji", GSamsung, true, ""),
                I("com.samsung.android.game.gamehome", "Gaming Hub / Game Launcher", GSamsung, false, "Regroupe les jeux et ajoute des pubs de jeux"),
                I("com.samsung.android.app.tips", "Conseils Samsung (Tips)", GSamsung, true, "Guides et astuces préinstallés"),
                I("com.sec.android.app.sbrowser", "Samsung Internet", GSamsung, false, "Si vous utilisez Chrome, Firefox ou Brave"),
                I("com.samsung.android.spay", "Samsung Wallet / Pay", GSamsung, false, "Si vous n'utilisez pas Samsung Pay"),
                I("com.samsung.android.scloud", "Samsung Cloud", GSamsung, false, "Si vous utilisez Google Drive / Photos"),
                I("com.samsung.android.app.watchmanagerstub", "Installeur Galaxy Wearable", GSamsung, false, "Si vous n'avez pas de montre/écouteurs Galaxy"),
                I("com.samsung.android.app.routines", "Modes et routines", GSamsung, false, "Automatisations Samsung"),
                I("com.sec.android.app.samsungapps", "Galaxy Store", GSamsung, false, "Boutique Samsung (gardez-la si vous mettez à jour les apps Samsung)"),

                // Xiaomi / Redmi / POCO (MIUI / HyperOS)
                I("com.mi.globalbrowser", "Navigateur Mi", GXiaomi, true, "Navigateur préinstallé avec flux d'actualités"),
                I("com.miui.videoplayer", "Mi Vidéo", GXiaomi, true, "Lecteur vidéo avec publicités intégrées"),
                I("com.miui.player", "Mi Musique", GXiaomi, true, "Lecteur audio avec contenus sponsorisés"),
                I("com.xiaomi.mipicks", "GetApps (Boutique Xiaomi)", GXiaomi, true, "Boutique doublon du Play Store"),
                I("com.miui.miservice", "Services et commentaires Xiaomi", GXiaomi, true, "Envoi de rapports"),
                I("com.miui.bugreport", "Rapport de bugs MIUI", GXiaomi, true, ""),
                I("com.miui.yellowpage", "Pages Jaunes Xiaomi", GXiaomi, true, "Annuaire commercial intégré"),
                I("com.miui.android.fashiongallery", "Carrousel de fonds d'écran", GXiaomi, true, "Écran de verrouillage publicitaire"),
                I("com.miui.hybrid", "Quick Apps Xiaomi", GXiaomi, true, "Moteur d'applications instantanées"),
                I("com.miui.hybrid.accessory", "Accessoire Quick Apps", GXiaomi, true, ""),
                I("com.xiaomi.glgm", "Jeux Xiaomi (Game Center)", GXiaomi, true, "Portail de jeux préinstallé"),
                I("com.xiaomi.payment", "Mi Coin / Paiements Xiaomi", GXiaomi, true, ""),
                I("com.miui.compass", "Boussole Xiaomi", GXiaomi, false, ""),
                I("com.miui.notes", "Notes Xiaomi", GXiaomi, false, "Si vous utilisez un autre bloc-notes"),
                I("com.mi.globalminusscreen", "App Vault (écran gauche)", GXiaomi, false, "Widgets et actualités à gauche de l'accueil"),
                I("com.miui.cloudservice", "Xiaomi Cloud", GXiaomi, false, "Si vous n'utilisez pas la sauvegarde Xiaomi"),
                I("com.miui.cleanmaster", "Nettoyeur MIUI", GXiaomi, false, "Outil préinstallé contenant des publicités"),

                // Oppo / Realme / OnePlus (ColorOS / OxygenOS)
                I("com.heytap.market", "App Market (Oppo / Realme)", GOppo, true, "Boutique redondante avec le Play Store"),
                I("com.heytap.browser", "Navigateur HeyTap", GOppo, true, "Navigateur préinstallé avec flux d'actualités"),
                I("com.heytap.pictorial", "Magazine écran de verrouillage", GOppo, true, "Fonds d'écran et articles sur l'écran verrouillé"),
                I("com.heytap.music", "Musique HeyTap", GOppo, true, ""),
                I("com.heytap.cloud", "HeyTap Cloud", GOppo, false, "Si vous n'utilisez pas le cloud Oppo/Realme"),
                I("com.finshell.wallet", "FinShell Pay", GOppo, true, "Portefeuille commercial préinstallé"),
                I("com.coloros.gamespace", "Espace Jeux (Game Space)", GOppo, false, ""),
                I("com.oneplus.mall", "OnePlus Store", GOppo, true, "Boutique promotionnelle OnePlus"),
                I("net.oneplus.forums", "OnePlus Community", GOppo, true, "Application forum préinstallée"),
                I("com.oplus.logkit", "Oplus LogKit", GOppo, true, "Journaux de diagnostic en fond"),

                // Motorola / Huawei / Honor
                I("com.motorola.ccc.notification", "Notifications Motorola", GOther, true, "Messages promotionnels et astuces"),
                I("com.motorola.genie", "Aide Motorola", GOther, true, "Guide préinstallé"),
                I("com.motorola.bug2go", "Bug2Go Motorola", GOther, true, "Collecte de rapports de bogue"),
                I("com.motorola.brapps", "Applications partenaires Motorola", GOther, true, ""),
                I("com.huawei.tips", "Conseils Huawei", GOther, true, ""),
                I("com.huawei.search", "HiSearch Huawei", GOther, true, "Barre de recherche et suggestions"),
                I("com.huawei.vassistant", "Assistant vocal Huawei", GOther, false, ""),
                I("com.hihonor.tips", "Conseils Honor", GOther, true, ""),

                // Réseaux sociaux et partenaires préinstallés (décochés par défaut si l'utilisateur s'en sert)
                I("com.facebook.katana", "Facebook", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.instagram.android", "Instagram", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.zhiliaoapp.musically", "TikTok", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.ss.android.ugc.trill", "TikTok (variante globale)", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.linkedin.android", "LinkedIn", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.snapchat.android", "Snapchat", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.amazon.mShop.android.shopping", "Amazon Shopping", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.booking", "Booking.com", GSocial, true, "Application partenaire préinstallée"),
                I("com.netflix.mediaclient", "Netflix", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.spotify.music", "Spotify", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.microsoft.appmanager", "Lien avec Windows (compagnon)", GSocial, false, "Si vous ne reliez pas le téléphone à Windows"),
                I("com.microsoft.skydrive", "Microsoft OneDrive", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.microsoft.office.officehubrow", "Microsoft 365 (Office)", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.microsoft.office.outlook", "Microsoft Outlook", GSocial, false, "Si vous ne l'utilisez pas"),
                I("com.ebay.mobile", "eBay", GSocial, true, "Application partenaire préinstallée"),
                I("com.einnovation.temu", "Temu", GSocial, true, "Application partenaire préinstallée"),

                // Extras Google (non essentiels)
                I("com.google.android.videos", "Google TV / Play Films", GGoogle, false, "Si vous ne louez pas de films sur Google"),
                I("com.google.android.play.games", "Google Play Jeux", GGoogle, false, "Si vous ne jouez pas"),
                I("com.google.android.apps.youtube.music", "YouTube Music", GGoogle, false, "Si vous utilisez un autre service de musique"),
                I("com.google.android.apps.subscriptions.red", "Google One", GGoogle, false, "Si vous n'avez pas d'abonnement Google One"),
                I("com.google.android.apps.tachyon", "Google Meet", GGoogle, false, "Si vous ne l'utilisez pas"),
                I("com.google.android.apps.magazines", "Google Actualités", GGoogle, false, "Si vous ne l'utilisez pas"),
                I("com.google.android.apps.books", "Google Play Livres", GGoogle, true, "Rarement utilisé"),
                I("com.google.android.apps.podcasts", "Google Podcasts", GGoogle, true, "Service arrêté par Google"),
                I("com.google.android.projection.gearhead", "Android Auto", GGoogle, false, "Si vous n'utilisez pas Android Auto en voiture"),
                I("com.google.android.apps.wellbeing", "Bien-être numérique", GGoogle, false, "Suivi du temps d'écran en arrière-plan"),
            };
        }

        // Composants critiques d'un smartphone : appels, SMS, contacts, clavier, caméra, système, Play Store, sécurité.
        static readonly string[] ProtectedExact =
        {
            "android", "com.android.systemui", "com.android.settings", "com.android.shell",
            "com.android.phone", "com.android.telecom", "com.android.server.telecom", "com.android.incallui",
            "com.android.dialer", "com.google.android.dialer", "com.samsung.android.dialer", "com.samsung.android.incallui",
            "com.android.mms", "com.google.android.apps.messaging", "com.samsung.android.messaging",
            "com.android.contacts", "com.google.android.contacts", "com.samsung.android.app.contacts",
            "com.android.vending", "com.google.android.gms", "com.google.android.gsf", "com.google.android.webview",
            "com.android.webview", "com.android.packageinstaller", "com.google.android.packageinstaller",
            "com.android.permissioncontroller", "com.google.android.permissioncontroller",
            "com.android.bluetooth", "com.android.nfc", "com.android.keychain", "com.android.certinstaller",
            "com.android.emergency", "com.android.cellbroadcastreceiver", "com.google.android.cellbroadcastreceiver",
            "com.android.deskclock", "com.google.android.deskclock", "com.sec.android.app.clockpackage",
            "com.sec.android.app.camera", "com.android.camera", "com.android.camera2", "com.google.android.GoogleCamera",
            "com.samsung.android.honeyboard", "com.sec.android.app.launcher", "com.miui.home", "com.android.launcher3",
            "com.google.android.apps.nexuslauncher", "com.oppo.launcher", "com.oneplus.launcher",
            "com.google.android.ext.services", "com.android.externalstorage", "com.android.documentsui", "com.google.android.documentsui",
        };
        static readonly string[] ProtectedPrefix =
        {
            "com.android.providers.", "com.android.inputmethod.", "com.google.android.inputmethod.",
            "com.android.server.", "com.android.internal.", "com.qualcomm.", "com.mediatek.", "com.qti.",
        };

        public static bool IsProtected(string pkg)
        {
            if (string.IsNullOrEmpty(pkg)) return true;
            return ProtectedExact.Contains(pkg, StringComparer.OrdinalIgnoreCase)
                || ProtectedPrefix.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Extrait niveau (%), température (°C), santé et état depuis « dumpsys battery ».</summary>
        public static void ParseBattery(string text, out int level, out double tempC, out string health, out string status)
        {
            level = -1; tempC = -1; health = ""; status = "";
            if (string.IsNullOrEmpty(text)) return;
            var ml = Regex.Match(text, @"(?m)^\s*level:\s*(\d+)");
            if (ml.Success) int.TryParse(ml.Groups[1].Value, out level);
            var mt = Regex.Match(text, @"(?m)^\s*temperature:\s*(\d+)");
            int rawT = 0;
            if (mt.Success && int.TryParse(mt.Groups[1].Value, out rawT) && rawT > 0) tempC = rawT / 10.0;
            var mh = Regex.Match(text, @"(?m)^\s*health:\s*(\d+)");
            if (mh.Success)
            {
                switch (mh.Groups[1].Value)
                {
                    case "2": health = "Bonne"; break;
                    case "3": health = "Surchauffe"; break;
                    case "4": health = "Défectueuse"; break;
                    case "5": health = "Surtension"; break;
                    case "7": health = "Froide"; break;
                    default: health = ""; break;
                }
            }
            var ms = Regex.Match(text, @"(?m)^\s*status:\s*(\d+)");
            if (ms.Success)
            {
                switch (ms.Groups[1].Value)
                {
                    case "2": status = "En charge"; break;
                    case "3": status = "Sur batterie"; break;
                    case "5": status = "Pleine"; break;
                    default: status = ""; break;
                }
            }
        }

        public static string ParseResolution(string wmSize)
        {
            var m = Regex.Match(wmSize ?? "", @"(\d{3,4}x\d{3,4})");
            return m.Success ? m.Groups[1].Value : "";
        }

        public static PhoneInfo ReadInfo(string adb, string serial, CancellationToken ct)
        {
            var p = TvOptimizer.ParseGetprop(Adb.Shell(adb, serial, "getprop", ct));
            Func<string, string> g = k => { string v; return p.TryGetValue(k, out v) ? v : ""; };
            var info = new PhoneInfo
            {
                Brand = g("ro.product.brand"),
                Model = g("ro.product.model"),
                Android = g("ro.build.version.release"),
                Serial = g("ro.serialno"),
                SecurityPatch = g("ro.build.version.security_patch"),
                Soc = g("ro.soc.model").Length > 0 ? g("ro.soc.model") : g("ro.board.platform")
            };
            int sdk; int.TryParse(g("ro.build.version.sdk"), out sdk); info.Sdk = sdk;
            TvOptimizer.ParseDf(Adb.Shell(adb, serial, "df -k /data", ct), out info.StorageTotal, out info.StorageFree);
            var mem = Adb.Shell(adb, serial, "cat /proc/meminfo", ct);
            info.RamTotal = TvOptimizer.ParseMeminfo(mem, "MemTotal");
            info.RamFree = TvOptimizer.ParseMeminfo(mem, "MemAvailable");
            ParseBattery(Adb.Shell(adb, serial, "dumpsys battery", ct), out info.BatteryLevel, out info.BatteryTempC, out info.BatteryHealth, out info.BatteryStatus);
            info.Resolution = ParseResolution(Adb.Shell(adb, serial, "wm size", ct));
            return info;
        }

        public static List<TvItem> BuildPlan(IEnumerable<string> enabledPackages, string homePackage)
        {
            var catalog = Catalog();
            var res = new List<TvItem>();
            foreach (var pkg in enabledPackages.Distinct())
            {
                if (!Adb.IsValidPackage(pkg) || IsProtected(pkg)) continue;
                if (homePackage != null && string.Equals(pkg, homePackage, StringComparison.OrdinalIgnoreCase)) continue;
                var m = catalog.FirstOrDefault(c => c.Package.EndsWith(".") ? pkg.StartsWith(c.Package, StringComparison.OrdinalIgnoreCase) : string.Equals(pkg, c.Package, StringComparison.OrdinalIgnoreCase));
                if (m == null) continue;
                res.Add(new TvItem { Package = pkg, Label = m.Label, Note = m.Note, Group = m.Group, Recommended = m.Recommended, Selected = m.Recommended });
            }
            var order = new[] { GAds, GSamsung, GXiaomi, GOppo, GOther, GSocial, GGoogle };
            return res.OrderBy(i => Array.IndexOf(order, i.Group)).ThenByDescending(i => i.Recommended).ThenBy(i => i.Label).ToList();
        }

        public static List<TvItem> Analyze(string adb, string serial, CancellationToken ct)
        {
            var enabled = TvOptimizer.ParsePackages(Adb.Shell(adb, serial, "pm list packages -e", ct));
            var home = TvOptimizer.ParseHome(Adb.Shell(adb, serial, "cmd package resolve-activity --brief -c android.intent.category.HOME -a android.intent.action.MAIN", ct));
            return BuildPlan(enabled, home);
        }

        static string Key(string phoneId) { return "phone.off." + phoneId; }

        /// <summary>Liste des paquets désactivés ou retirés pour l'utilisateur 0 par OptiTech sur ce téléphone.</summary>
        public static List<PhoneDisabledEntry> DisabledByUs(string phoneId)
        {
            var res = new List<PhoneDisabledEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (Store.Setting(Key(phoneId)) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var uninst = raw.StartsWith("u:", StringComparison.Ordinal);
                var pkg = uninst ? raw.Substring(2) : raw;
                if (!Adb.IsValidPackage(pkg) || !seen.Add(pkg)) continue;
                res.Add(new PhoneDisabledEntry { Package = pkg, WasUninstalled = uninst });
            }
            return res;
        }

        static void Remember(string phoneId, IEnumerable<PhoneDisabledEntry> entries)
        {
            var l = entries.Select(e => e.Raw).Distinct().ToList();
            Store.SetSetting(Key(phoneId), l.Count == 0 ? null : string.Join(",", l));
        }

        public static string LabelOf(string pkg)
        {
            var m = Catalog().FirstOrDefault(c => c.Package.EndsWith(".") ? pkg.StartsWith(c.Package, StringComparison.OrdinalIgnoreCase) : string.Equals(c.Package, pkg, StringComparison.OrdinalIgnoreCase));
            return m == null ? pkg : m.Label;
        }

        /// <summary>
        /// Désactive proprement chaque élément sélectionné. Si le constructeur bloque « pm disable-user » sur un bloatware
        /// système, utilise « pm uninstall -k --user 0 » qui reste 100 % réversible via « cmd package install-existing ».
        /// </summary>
        public static int Disable(string adb, string serial, PhoneInfo phone, IList<TvItem> items, Job job)
        {
            int ok = 0;
            var known = DisabledByUs(phone.Id);
            for (int i = 0; i < items.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                var it = items[i];
                job.Status(it.Label);
                job.Progress(100.0 * i / items.Count);
                if (!Adb.IsValidPackage(it.Package) || IsProtected(it.Package)) { job.Log("  Ignoré par sécurité : " + it.Package); continue; }

                var o = Adb.Shell(adb, serial, "pm disable-user --user 0 " + it.Package, job.Token);
                if (o.IndexOf("new state: disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!known.Any(x => string.Equals(x.Package, it.Package, StringComparison.OrdinalIgnoreCase)))
                        known.Add(new PhoneDisabledEntry { Package = it.Package, WasUninstalled = false });
                    Remember(phone.Id, known);
                    ok++;
                    continue;
                }
                // Solution de repli officielle et réversible pour les surcouches qui verrouillent disable-user (MIUI / One UI).
                var u = Adb.Shell(adb, serial, "pm uninstall -k --user 0 " + it.Package, job.Token);
                if (u.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    known.RemoveAll(x => string.Equals(x.Package, it.Package, StringComparison.OrdinalIgnoreCase));
                    known.Add(new PhoneDisabledEntry { Package = it.Package, WasUninstalled = true });
                    Remember(phone.Id, known);
                    job.Log("  Retiré pour l'utilisateur 0 (réversible) : " + it.Package);
                    ok++;
                }
                else
                {
                    job.Log("  Refusé par le téléphone (" + it.Package + ") : " + (u.Trim().Length > 0 ? u.Trim() : o.Trim()));
                }
            }
            job.Progress(100);
            if (ok > 0) Store.AddHistory("phone:" + phone.Id, "Téléphone : " + phone.Name, Fmt.N(ok, "application allégée", "applications allégées"));
            return ok;
        }

        public static bool RestoreOne(string adb, string serial, PhoneInfo phone, PhoneDisabledEntry entry, Job job)
        {
            if (entry == null || !Adb.IsValidPackage(entry.Package)) return false;
            var ok = false;
            if (entry.WasUninstalled)
            {
                var r = Adb.Shell(adb, serial, "cmd package install-existing " + entry.Package, job.Token);
                ok = r.IndexOf("installed for user", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!ok) job.Log("  install-existing " + entry.Package + " : " + r.Trim());
            }
            var e = Adb.Shell(adb, serial, "pm enable " + entry.Package, job.Token);
            if (e.IndexOf("new state: enabled", StringComparison.OrdinalIgnoreCase) >= 0) ok = true;
            else if (!ok) job.Log("  enable " + entry.Package + " : " + e.Trim());

            if (ok)
            {
                var known = DisabledByUs(phone.Id);
                known.RemoveAll(x => string.Equals(x.Package, entry.Package, StringComparison.OrdinalIgnoreCase));
                Remember(phone.Id, known);
            }
            return ok;
        }

        public static int RestoreAll(string adb, string serial, PhoneInfo phone, Job job)
        {
            var list = DisabledByUs(phone.Id);
            int ok = 0;
            for (int i = 0; i < list.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                job.Status(LabelOf(list[i].Package));
                job.Progress(100.0 * i / list.Count);
                if (RestoreOne(adb, serial, phone, list[i], job)) ok++;
            }
            job.Progress(100);
            Store.AddHistory("phone:" + phone.Id, "Téléphone : " + phone.Name, Fmt.N(ok, "application rétablie", "applications rétablies"));
            return ok;
        }
    }
}
