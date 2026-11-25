using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OptiTech
{
    public sealed class TvDevice
    {
        public string Serial, State;
        public bool Ready { get { return State == "device"; } }
    }

    public sealed class TvInfo
    {
        public string Brand = "", Model = "", Android = "", Serial = "";
        public int Sdk;
        public long StorageFree = -1, StorageTotal = -1, RamTotal = -1, RamFree = -1;
        public string Name { get { return (Capital(Brand) + " " + Model).Trim(); } }
        /// <summary>Identifiant stable du téléviseur, pour retrouver ce qu'on y a désactivé.</summary>
        public string Id
        {
            get
            {
                var raw = Serial.Length > 0 ? Serial : Brand + "-" + Model;
                return new string(raw.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
            }
        }
        static string Capital(string s) { return string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1); }
    }

    public sealed class TvItem : Bindable
    {
        public string Package { get; set; }
        public string Label { get; set; }
        public string Note { get; set; }
        public string Group { get; set; }
        public bool Recommended { get; set; }
        public string Detail { get { return string.IsNullOrEmpty(Note) ? Package : Note + "  ·  " + Package; } }
        bool sel;
        public bool Selected { get { return sel; } set { Set(ref sel, value); } }
    }

    /// <summary>Pilote adb, l'outil officiel de Google pour dialoguer avec un appareil Android.</summary>
    public static class Adb
    {
        const string PackageId = "Google.PlatformTools";
        static readonly Regex Host = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}(:\d{1,5})?$", RegexOptions.Compiled);
        static readonly Regex DnsHost = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)+$", RegexOptions.Compiled);
        static readonly Regex Pkg = new Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);
        static readonly Regex PairCode = new Regex(@"^\d{6}$", RegexOptions.Compiled);
        static readonly string[] AnimKeys = { "window_animation_scale", "transition_animation_scale", "animator_duration_scale" };
        static readonly string[] AllowedScales = { "0", "0.25", "0.5", "1" };
        static readonly int[] AllowedKeys = { 3, 4, 19, 20, 21, 22, 23, 24, 25, 26, 85, 164 };
        static bool serverStarted;

        /// <summary>Une adresse ne contient que lettres, chiffres, points et tirets : rien ne peut se glisser dans la commande.</summary>
        public static bool IsValidHost(string h) { return h != null && Host.IsMatch(h); }
        public static bool IsValidPackage(string p) { return p != null && p.Length <= 200 && Pkg.IsMatch(p); }
        public static bool IsValidPairCode(string c) { return c != null && PairCode.IsMatch(c); }
        public static bool IsValidDnsHost(string h) { return h != null && h.Length <= 253 && DnsHost.IsMatch(h); }
        public static bool IsValidAnimScale(string s) { return s != null && AllowedScales.Contains(s); }
        public static bool IsValidRemoteKey(int key) { return AllowedKeys.Contains(key); }
        public static string WithPort(string h) { return h.Contains(":") ? h : h + ":5555"; }

        public static string Find()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(local, @"Android\Sdk\platform-tools\adb.exe"),
                Path.Combine(Path.GetDirectoryName(Paths.Exe) ?? "", @"platform-tools\adb.exe"),
            };
            var home = Environment.GetEnvironmentVariable("ANDROID_HOME");
            if (!string.IsNullOrEmpty(home)) candidates.Add(Path.Combine(home, @"platform-tools\adb.exe"));
            foreach (var root in new[] { Path.Combine(local, @"Microsoft\WinGet\Packages"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WinGet\Packages") })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var dir in Directory.GetDirectories(root, PackageId + "*"))
                        candidates.AddRange(Directory.GetFiles(dir, "adb.exe", SearchOption.AllDirectories));
                }
                catch (Exception e) { Logger.Info("Recherche d'adb : " + e.Message); }
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        public static string Ensure(Job job)
        {
            var exe = Find();
            if (exe == null)
            {
                if (!AppsCatalog.WingetAvailable()) throw new InvalidOperationException("winget est indisponible : impossible de récupérer les outils Android de Google (adb).");
                job.Status("Récupération des outils Android de Google via winget");
                var code = AppsCatalog.Install(new SoftApp { Id = PackageId, Label = "Android SDK Platform-Tools" }, job);
                exe = Find();
                if (exe == null) throw new InvalidOperationException("Les outils Android n'ont pas pu être installés (winget, code " + code + ").");
                Store.AddHistory("soft:" + PackageId, "Logiciel : Android Platform-Tools", "Installé");
            }
            StartServer(exe);
            return exe;
        }

        /// <summary>
        /// Lance le service d'adb sans rediriger sa sortie : sinon ce service, qui survit à la commande,
        /// garderait nos tubes ouverts et la première commande ne se terminerait jamais.
        /// </summary>
        public static void StartServer(string exe)
        {
            if (serverStarted) return;
            using (var p = Process.Start(new ProcessStartInfo(exe, "start-server") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) }))
                p.WaitForExit(20000);
            serverStarted = true;
        }

        /// <summary>Arrête le service à la fermeture d'OptiTech, pour ne rien laisser tourner derrière soi.</summary>
        public static void StopServer()
        {
            if (!serverStarted) return;
            try
            {
                var exe = Find();
                if (exe != null) Process.Start(new ProcessStartInfo(exe, "kill-server") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
            serverStarted = false;
        }

        public static string Out(string exe, string args, CancellationToken ct)
        {
            var sb = new StringBuilder();
            Proc.Run(exe, args, delegate(string l) { lock (sb) sb.AppendLine(l); }, ct, Path.GetDirectoryName(exe));
            return sb.ToString();
        }

        public static string Shell(string exe, string serial, string command, CancellationToken ct)
        {
            return Out(exe, "-s " + serial + " shell " + command, ct);
        }

        /// <summary>Appairage sans fil Android 11+ (Options développeurs › Débogage sans fil › Associer avec un code).</summary>
        public static string Pair(string exe, string hostPort, string code, Job job)
        {
            var h = (hostPort ?? "").Trim();
            var c = (code ?? "").Trim();
            if (!IsValidHost(h) || !h.Contains(":")) throw new ArgumentException("Adresse d'appairage invalide : indiquez IP:port (ex. 192.168.1.25:37421).");
            if (!IsValidPairCode(c)) throw new ArgumentException("Code d'appairage invalide : 6 chiffres attendus.");
            job.Status("Appairage avec " + h);
            var o = Out(exe, "pair " + h + " " + c, job.Token).Trim();
            job.Log(o);
            if (o.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Appairage refusé : " + (o.Length > 0 ? o : "vérifiez l'adresse IP, le port et le code à 6 chiffres."));
            return o;
        }

        /// <summary>Extrait l'adresse IPv4 Wi-Fi depuis la sortie de « ip route » ou « ip -f inet addr show wlan0 ».</summary>
        public static string ParseWifiIp(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"\bsrc\s+(\d{1,3}(?:\.\d{1,3}){3})\b");
            if (!m.Success) m = Regex.Match(text, @"\binet\s+(\d{1,3}(?:\.\d{1,3}){3})/");
            if (!m.Success) return null;
            IPAddress ip;
            if (!IPAddress.TryParse(m.Groups[1].Value, out ip) || IPAddress.IsLoopback(ip)) return null;
            return ip.ToString();
        }

        /// <summary>Bascule un appareil branché en USB vers le débogage Wi-Fi (port 5555) et renvoie IP:5555.</summary>
        public static string EnableTcpIp(string exe, string serial, Job job)
        {
            job.Status("Lecture de l'adresse Wi-Fi de l'appareil");
            var ip = ParseWifiIp(Shell(exe, serial, "ip route", job.Token))
                  ?? ParseWifiIp(Shell(exe, serial, "ip -f inet addr show wlan0", job.Token));
            if (string.IsNullOrEmpty(ip))
                throw new InvalidOperationException("Impossible de lire l'adresse Wi-Fi de l'appareil. Vérifiez qu'il est connecté au même réseau Wi-Fi que ce PC.");
            job.Status("Activation du débogage réseau (port 5555)");
            var res = Out(exe, "-s " + serial + " tcpip 5555", job.Token).Trim();
            job.Log(res);
            job.Token.WaitHandle.WaitOne(1500);
            var target = ip + ":5555";
            job.Status("Connexion Wi-Fi à " + target);
            var conn = Out(exe, "connect " + target, job.Token).Trim();
            job.Log(conn);
            if (conn.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Mode Wi-Fi activé sur " + ip + ", mais la connexion directe a échoué : " + conn);
            return target;
        }

        /// <summary>Installe un fichier .apk sur l'appareil (remplace une version existante sans perdre ses données).</summary>
        public static void InstallApk(string exe, string serial, string apkPath, Job job)
        {
            if (string.IsNullOrWhiteSpace(apkPath) || !File.Exists(apkPath) || !apkPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Sélectionnez un fichier .apk valide.");
            job.Status("Installation de " + Path.GetFileName(apkPath));
            var sb = new StringBuilder();
            var code = Proc.Run(exe, "-s " + serial + " install -r \"" + apkPath + "\"", delegate(string l) { lock (sb) sb.AppendLine(l); job.Log(l); }, job.Token, Path.GetDirectoryName(exe));
            var text = sb.ToString();
            if (code != 0 || text.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("L'appareil a refusé l'installation : " + text.Trim());
        }

        /// <summary>Capture l'écran de l'appareil en PNG directement vers le PC (aucun fichier laissé sur l'appareil).</summary>
        public static void CaptureScreenshot(string exe, string serial, string outPng, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, "-s " + serial + " exec-out screencap -p")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            using (var p = Process.Start(psi))
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
            using (var fs = File.Create(outPng))
            {
                p.StandardOutput.BaseStream.CopyTo(fs);
                p.WaitForExit(15000);
            }
            var fi = new FileInfo(outPng);
            if (!fi.Exists || fi.Length < 128)
            {
                try { File.Delete(outPng); } catch { }
                throw new InvalidOperationException("Capture impossible : l'écran est peut-être protégé (contenu DRM) ou en veille.");
            }
        }

        /// <summary>Envoie une touche de télécommande sûre (Accueil, Retour, Volume, Lecture/Pause, Veille).</summary>
        public static void SendKey(string exe, string serial, int keyCode, CancellationToken ct)
        {
            if (!IsValidRemoteKey(keyCode)) throw new ArgumentException("Touche non autorisée.");
            Shell(exe, serial, "input keyevent " + keyCode, ct);
        }

        public static string GetGlobalSetting(string exe, string serial, string key, CancellationToken ct)
        {
            return Shell(exe, serial, "settings get global " + key, ct).Trim();
        }

        /// <summary>Lit l'échelle d'animation courante (« 1 », « 0.5 », « 0.25 », « 0 » ou valeur brute).</summary>
        public static string GetAnimScale(string exe, string serial, CancellationToken ct)
        {
            var v = GetGlobalSetting(exe, serial, "window_animation_scale", ct);
            if (string.IsNullOrEmpty(v) || v == "null") return "1";
            double d;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return "1";
            if (d <= 0.01) return "0";
            if (d <= 0.35) return "0.25";
            if (d <= 0.75) return "0.5";
            return "1";
        }

        /// <summary>Règle la vitesse des animations (« 1 », « 0.5 », « 0.25 », « 0 ») ou restaure les valeurs d'origine (« restore »).</summary>
        public static void SetAnimScale(string exe, string serial, string storeKey, string deviceLabel, string scale, Job job)
        {
            if (scale == "restore" || scale == "1")
            {
                var saved = (Store.Setting(storeKey) ?? "").Split(',');
                for (int i = 0; i < AnimKeys.Length; i++)
                {
                    var v = i < saved.Length ? saved[i] : "1";
                    if (Regex.IsMatch(v, @"^\d+(\.\d+)?$")) Shell(exe, serial, "settings put global " + AnimKeys[i] + " " + v, job.Token);
                    else Shell(exe, serial, "settings delete global " + AnimKeys[i], job.Token);
                }
                Store.SetSetting(storeKey, null);
                Store.AddHistory(storeKey, deviceLabel, "Animations d'origine rétablies");
                return;
            }
            if (!IsValidAnimScale(scale)) throw new ArgumentException("Échelle d'animation invalide.");
            if (Store.Setting(storeKey) == null)
                Store.SetSetting(storeKey, string.Join(",", AnimKeys.Select(k => { var v = GetGlobalSetting(exe, serial, k, job.Token); return Regex.IsMatch(v, @"^\d+(\.\d+)?$") ? v : "null"; })));
            foreach (var k in AnimKeys) Shell(exe, serial, "settings put global " + k + " " + scale, job.Token);
            Store.AddHistory(storeKey, deviceLabel, "Animations réglées à " + scale + "x");
        }

        /// <summary>Lit le DNS privé (Android 9+) : renvoie « opportunistic », « off » ou le nom d'hôte configuré.</summary>
        public static string GetPrivateDns(string exe, string serial, CancellationToken ct)
        {
            var mode = GetGlobalSetting(exe, serial, "private_dns_mode", ct);
            if (string.Equals(mode, "hostname", StringComparison.OrdinalIgnoreCase))
            {
                var host = GetGlobalSetting(exe, serial, "private_dns_specifier", ct);
                return !string.IsNullOrEmpty(host) && host != "null" ? host : "opportunistic";
            }
            if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)) return "off";
            return "opportunistic";
        }

        /// <summary>Configure le DNS privé chiffré (DNS-over-TLS) sur Android 9+ ; réversible avec mode « opportunistic ».</summary>
        public static void SetPrivateDns(string exe, string serial, string storeKey, string deviceLabel, string mode, string host, Job job)
        {
            if (Store.Setting(storeKey) == null)
            {
                var curMode = GetGlobalSetting(exe, serial, "private_dns_mode", job.Token);
                var curHost = GetGlobalSetting(exe, serial, "private_dns_specifier", job.Token);
                Store.SetSetting(storeKey, (curMode == "null" ? "" : curMode) + "|" + (curHost == "null" ? "" : curHost));
            }
            if (mode == "hostname")
            {
                if (!IsValidDnsHost(host)) throw new ArgumentException("Nom d'hôte DNS privé invalide.");
                Shell(exe, serial, "settings put global private_dns_mode hostname", job.Token);
                Shell(exe, serial, "settings put global private_dns_specifier " + host, job.Token);
                Store.AddHistory(storeKey, deviceLabel, "DNS privé : " + host);
            }
            else if (mode == "off")
            {
                Shell(exe, serial, "settings put global private_dns_mode off", job.Token);
                Store.AddHistory(storeKey, deviceLabel, "DNS privé désactivé");
            }
            else
            {
                Shell(exe, serial, "settings put global private_dns_mode opportunistic", job.Token);
                Shell(exe, serial, "settings delete global private_dns_specifier", job.Token);
                Store.AddHistory(storeKey, deviceLabel, "DNS privé : automatique");
            }
        }

        /// <summary>Lance la compilation officielle ART/DEX d'Android (accélère l'ouverture des applications et réduit la chauffe).</summary>
        public static string OptimizeApps(string exe, string serial, Job job)
        {
            job.Status("Compilation optimisée des applications (ART / DEX)…");
            job.Log("Exécution de la tâche officielle Android « cmd package bg-dexopt-job » (peut prendre 1 à 5 minutes)...");
            var o = Shell(exe, serial, "cmd package bg-dexopt-job", job.Token).Trim();
            if (o.Length > 0) job.Log(o);
            return o;
        }

        /// <summary>Applications tierces installées par l'utilisateur (sans les paquets système).</summary>
        public static List<string> ListThirdPartyApps(string exe, string serial, CancellationToken ct)
        {
            return TvOptimizer.ParsePackages(Shell(exe, serial, "pm list packages -3 -e", ct)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void ForceStop(string exe, string serial, string pkg, CancellationToken ct)
        {
            if (!IsValidPackage(pkg)) throw new ArgumentException("Paquet invalide.");
            Shell(exe, serial, "am force-stop " + pkg, ct);
        }

        /// <summary>Lit la sortie de « adb devices ».</summary>
        public static List<TvDevice> ParseDevices(string text)
        {
            var res = new List<TvDevice>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var p = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || p[0] == "List" || p[0].StartsWith("*")) continue;
                if (p[1] != "device" && p[1] != "unauthorized" && p[1] != "offline" && p[1] != "authorizing") continue;
                res.Add(new TvDevice { Serial = p[0], State = p[1] });
            }
            return res;
        }

        public static List<TvDevice> Devices(string exe, CancellationToken ct) { return ParseDevices(Out(exe, "devices", ct)); }

        /// <summary>Adresses à sonder : le sous-réseau /24 de chaque carte réseau active.</summary>
        public static List<IPAddress> ScanTargets()
        {
            var res = new List<IPAddress>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType != NetworkInterfaceType.Ethernet && n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                    var b = a.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    for (int i = 1; i < 255; i++)
                    {
                        if (i == b[3]) continue;
                        var ip = new IPAddress(new[] { b[0], b[1], b[2], (byte)i });
                        if (!res.Contains(ip)) res.Add(ip);
                    }
                }
            }
            return res;
        }

        static bool PortOpen(IPAddress ip, int port, int ms)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ms)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Cherche sur le réseau local les appareils dont le débogage réseau est ouvert (port 5555).</summary>
        public static List<string> Scan(Job job)
        {
            var queue = new ConcurrentQueue<IPAddress>(ScanTargets());
            var total = Math.Max(1, queue.Count);
            var found = new ConcurrentBag<string>();
            int done = 0;
            var threads = new List<Thread>();
            for (int t = 0; t < 48; t++)
            {
                var th = new Thread(delegate()
                {
                    IPAddress ip;
                    while (!job.Token.IsCancellationRequested && queue.TryDequeue(out ip))
                    {
                        if (PortOpen(ip, 5555, 350)) found.Add(ip.ToString());
                        job.Progress(100.0 * Interlocked.Increment(ref done) / total);
                    }
                }) { IsBackground = true };
                th.Start();
                threads.Add(th);
            }
            foreach (var th in threads) th.Join();
            job.Token.ThrowIfCancellationRequested();
            return found.OrderBy(x => int.Parse(x.Substring(x.LastIndexOf('.') + 1))).ToList();
        }
    }

    /// <summary>
    /// Allège un téléviseur Android TV ou Google TV depuis le PC. Mêmes principes que pour Windows :
    /// rien n'est désinstallé (les applications sont seulement désactivées), la liste de ce qui a été
    /// coupé est mémorisée avant d'agir, et tout se rétablit d'un clic. Le lanceur en service, la diffusion
    /// (Chromecast), les mises à jour, les entrées HDMI et les services Google ne sont jamais proposés.
    /// </summary>
    public static class TvOptimizer
    {
        public const string GAds = "Publicités et recommandations", GTrack = "Pistage et télémétrie",
                            GBrand = "Extras du fabricant", GVoice = "Assistant vocal", GApps = "Applications préinstallées";

        static TvItem I(string pkg, string label, string group, bool rec, string note)
        { return new TvItem { Package = pkg, Label = label, Group = group, Recommended = rec, Note = note }; }

        /// <summary>Liste fermée : seules les applications reconnues ici peuvent être proposées.</summary>
        public static List<TvItem> Catalog()
        {
            return new List<TvItem>
            {
                // Publicités et rangées sponsorisées
                I("com.google.android.tvrecommendations", "Recommandations Google", GAds, true, "Rangées sponsorisées de l'accueil"),
                I("com.google.android.leanbacklauncher.recommendations", "Recommandations de l'ancien accueil", GAds, true, "Android TV 8 et antérieur"),
                I("com.google.android.partnersetup", "Partenaires Google TV", GAds, true, "Suggestions et préchargements partenaires"),
                I("com.xm.webcontent", "Contenus web Xiaomi", GAds, true, "Rangées promotionnelles PatchWall"),
                I("com.xiaomi.floatingframe", "Bandeaux flottants Xiaomi", GAds, true, "Fenêtres promotionnelles"),
                I("com.xiaomi.mitv.shop", "Boutique Xiaomi", GAds, true, "Vitrine commerciale intégrée"),
                I("com.sony.dtv.sonyselect", "Sony Select", GAds, true, "Vitrine d'applications sponsorisées"),
                I("com.sony.dtv.promos", "Promotions Sony", GAds, true, "Bannières promotionnelles Bravia"),
                I("org.droidtv.nettvrecommender", "Recommandations Philips", GAds, true, "Suggestions NetTV"),
                I("org.droidtv.nettvadvert", "Publicités Philips", GAds, true, "Régie NetTV"),
                I("com.tcl.messagebox", "Boîte de messages TCL", GAds, true, "Messages promotionnels"),
                I("com.tcl.appmarket2", "Boutique d'apps TCL", GAds, true, "Boutique redondante avec le Play Store"),
                I("com.hisense.store.tv", "Boutique Hisense", GAds, true, "Vitrine promotionnelle"),

                // Pistage et télémétrie
                I("com.google.android.feedback", "Envoi d'avis à Google", GTrack, true, "Rapports et commentaires"),
                I("com.google.android.tv.bugreportsender", "Envoi de rapports de bogue", GTrack, true, "Collecte de diagnostics"),
                I("com.google.android.gms.location.history", "Historique de localisation Google", GTrack, true, ""),
                I("com.miui.tv.analytics", "Statistiques Xiaomi", GTrack, true, "Mesure d'usage"),
                I("com.xiaomi.statistic", "Télémétrie Xiaomi", GTrack, true, ""),
                I("com.tcl.bi", "Statistiques TCL", GTrack, true, "Mesure d'usage"),
                I("com.tcl.usercenter", "Compte et suivi TCL", GTrack, true, ""),
                I("com.sony.dtv.networkrecommendation", "Recommandations réseau Sony", GTrack, true, "Suggestions fondées sur l'usage"),
                I("com.nvidia.stats", "Statistiques NVIDIA Shield", GTrack, true, "Envoi de télémétrie Shield"),
                I("com.nvidia.feedback", "Avis et diagnostics NVIDIA", GTrack, true, ""),
                I("com.nvidia.diagtools", "Diagnostics NVIDIA", GTrack, true, "Télémétrie de fond"),
                I("tv.samba.ssm", "Samba TV", GTrack, true, "Reconnaissance automatique de ce que vous regardez (ACR)"),
                I("com.nielsen.", "Mesure d'audience Nielsen", GTrack, true, "Suivi d'audience"),
                I("com.hisense.tv.analytics", "Télémétrie Hisense", GTrack, true, ""),

                // Extras du fabricant
                I("com.xiaomi.mitv.tvmanager", "Gestionnaire TV Xiaomi", GBrand, true, "Service d'arrière-plan"),
                I("com.xiaomi.mitv.handbook", "Manuel Xiaomi", GBrand, true, ""),
                I("com.xiaomi.mimusic2", "Mi Musique", GBrand, true, ""),
                I("com.xiaomi.mitv.payment", "Paiements Xiaomi", GBrand, true, "Inutile hors écosystème Xiaomi"),
                I("com.xiaomi.mitv.mediaexplorer", "Explorateur Xiaomi", GBrand, false, "Doublon si vous utilisez VLC/Kodi"),
                I("com.mitv.gallery", "Galerie Xiaomi", GBrand, false, "Diaporama de photos"),
                I("com.sony.dtv.demomode", "Mode démonstration Sony", GBrand, true, "Réservé aux magasins"),
                I("com.sony.dtv.demosupport", "Assistance démonstration Sony", GBrand, true, "Réservé aux magasins"),
                I("com.sony.dtv.livingdecor", "Living Decor Sony", GBrand, false, "Écran de veille décoratif"),
                I("com.sony.dtv.osat.album", "Album photo Sony", GBrand, false, ""),
                I("org.droidtv.demome", "Démonstration Philips", GBrand, true, "Réservé aux magasins"),
                I("com.tcl.partnercustomizer", "Personnalisation partenaires TCL", GBrand, false, "Applications suggérées à l'installation"),
                I("com.tcl.browser", "Navigateur TCL", GBrand, false, ""),
                I("com.tcl.tcast", "MagiConnect / T-Cast TCL", GBrand, false, "Doublon de Chromecast"),
                I("com.nvidia.tegrazone3", "NVIDIA Games", GBrand, false, "Si vous n'utilisez pas GeForce NOW"),

                // Assistant vocal : utile à certains, donc jamais coché d'office
                I("com.google.android.katniss", "Assistant Google", GVoice, false, "Le bouton micro de la télécommande ne répondra plus"),
                I("com.google.android.speech.pumpkin", "Reconnaissance vocale hors ligne", GVoice, false, ""),

                // Applications imposées : à vous de dire lesquelles vous utilisez
                I("com.google.android.play.games", "Google Play Jeux", GApps, true, ""),
                I("com.google.android.videos", "Google Play Films", GApps, true, "Remplacé par Google TV"),
                I("com.google.android.music", "Google Play Musique", GApps, true, "Service arrêté"),
                I("com.google.android.youtube.tvmusic", "YouTube Music", GApps, false, ""),
                I("com.google.android.youtube.tvkids", "YouTube Kids", GApps, false, ""),
                I("com.netflix.ninja", "Netflix", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.amazon.amazonvideo.livingroom", "Prime Video", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.disney.disneyplus", "Disney+", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.spotify.tv.android", "Spotify", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.apple.atve.androidtv.appletv", "Apple TV", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.wbd.stream", "Max (HBO)", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.cbs.ott", "Paramount+", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.canal.android.canal", "CANAL+", GApps, false, "Si vous ne l'utilisez pas"),
                I("tv.molotov.app", "Molotov TV", GApps, false, "Si vous ne l'utilisez pas"),
                I("tv.twitch.android.app", "Twitch", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.zhiliaoapp.musically.tv", "TikTok TV", GApps, true, "Réseau social préinstallé"),
                I("com.facebook.katana", "Facebook", GApps, true, ""),
                I("tv.pluto.android", "Pluto TV", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.rakuten.tv.android", "Rakuten TV", GApps, false, "Si vous ne l'utilisez pas"),
                I("com.redbull.rbtv", "Red Bull TV", GApps, false, "Si vous ne l'utilisez pas"),
            };
        }

        // Ce dont dépendent l'affichage, la télécommande, la diffusion, les mises à jour et les entrées HDMI.
        static readonly string[] ProtectedExact =
        {
            "android", "com.android.systemui", "com.android.settings", "com.android.tv.settings", "com.android.tv", "com.android.shell",
            "com.android.vending", "com.android.bluetooth", "com.android.packageinstaller", "com.google.android.packageinstaller",
            "com.google.android.gms", "com.google.android.gsf", "com.google.android.webview", "com.google.android.ext.services",
            "com.google.android.apps.mediashell", "com.google.android.tvlauncher", "com.google.android.apps.tv.launcherx",
            "com.google.android.leanbacklauncher", "com.google.android.tv.remote.service", "com.xiaomi.mitv.updateservice",
            "com.google.android.youtube.tv", "com.google.android.tts", "com.google.android.backdrop", "com.nvidia.ota",
        };
        static readonly string[] ProtectedPrefix =
        {
            "com.android.providers.", "com.android.inputmethod.", "com.google.android.inputmethod.", "com.droidlogic.", "com.mediatek.",
            "com.android.server.", "com.google.android.permissioncontroller", "com.android.permissioncontroller", "com.realtek.", "com.amlogic.",
        };

        public static bool IsProtected(string pkg)
        {
            if (string.IsNullOrEmpty(pkg)) return true;
            return ProtectedExact.Contains(pkg, StringComparer.OrdinalIgnoreCase)
                || ProtectedPrefix.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Lit « pm list packages » : une ligne « package:nom » par application.</summary>
        public static List<string> ParsePackages(string text)
        {
            return (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:", StringComparison.Ordinal))
                .Select(l => l.Substring(8).Trim()).Where(Adb.IsValidPackage).Distinct().ToList();
        }

        /// <summary>Lit la sortie de « getprop » : « [clé]: [valeur] ».</summary>
        public static Dictionary<string, string> ParseGetprop(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(text ?? "", @"(?m)^\[([^\]]+)\]:\s*\[([^\]]*)\]")) d[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            return d;
        }

        /// <summary>Total et espace libre d'après « df -k » ; -1 si la sortie n'est pas reconnue.</summary>
        public static void ParseDf(string text, out long total, out long free)
        {
            total = -1; free = -1;
            foreach (var raw in (text ?? "").Split('\n').Reverse())
            {
                var p = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                long t, u, a;
                if (p.Length >= 4 && long.TryParse(p[1], out t) && long.TryParse(p[2], out u) && long.TryParse(p[3], out a) && t > 0)
                {
                    total = t * 1024; free = a * 1024;
                    return;
                }
            }
        }

        public static long ParseMeminfo(string text, string key)
        {
            var m = Regex.Match(text ?? "", @"(?m)^" + Regex.Escape(key) + @":\s*(\d+)\s*kB");
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1024 : -1;
        }

        /// <summary>Paquet du lanceur en service, d'après « cmd package resolve-activity --brief ».</summary>
        public static string ParseHome(string text)
        {
            foreach (var raw in (text ?? "").Split('\n').Reverse())
            {
                var l = raw.Trim();
                var i = l.IndexOf('/');
                if (i > 0 && Adb.IsValidPackage(l.Substring(0, i))) return l.Substring(0, i);
            }
            return null;
        }

        public static TvInfo ReadInfo(string adb, string serial, CancellationToken ct)
        {
            var p = ParseGetprop(Adb.Shell(adb, serial, "getprop", ct));
            Func<string, string> g = k => { string v; return p.TryGetValue(k, out v) ? v : ""; };
            var info = new TvInfo { Brand = g("ro.product.brand"), Model = g("ro.product.model"), Android = g("ro.build.version.release"), Serial = g("ro.serialno") };
            int sdk; int.TryParse(g("ro.build.version.sdk"), out sdk); info.Sdk = sdk;
            ParseDf(Adb.Shell(adb, serial, "df -k /data", ct), out info.StorageTotal, out info.StorageFree);
            var mem = Adb.Shell(adb, serial, "cat /proc/meminfo", ct);
            info.RamTotal = ParseMeminfo(mem, "MemTotal");
            info.RamFree = ParseMeminfo(mem, "MemAvailable");
            return info;
        }

        public static long FreeStorage(string adb, string serial, CancellationToken ct)
        {
            long total, free;
            ParseDf(Adb.Shell(adb, serial, "df -k /data", ct), out total, out free);
            return free;
        }

        /// <summary>
        /// Ce qui peut être coupé sur ce téléviseur : le catalogue, réduit aux applications présentes et actives,
        /// sans jamais toucher au lanceur en service ni aux composants protégés.
        /// </summary>
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
            var order = new[] { GAds, GTrack, GBrand, GApps, GVoice };
            return res.OrderBy(i => Array.IndexOf(order, i.Group)).ThenByDescending(i => i.Recommended).ThenBy(i => i.Label).ToList();
        }

        public static List<TvItem> Analyze(string adb, string serial, CancellationToken ct)
        {
            var enabled = ParsePackages(Adb.Shell(adb, serial, "pm list packages -e", ct));
            var home = ParseHome(Adb.Shell(adb, serial, "cmd package resolve-activity --brief -c android.intent.category.HOME -a android.intent.action.MAIN", ct));
            return BuildPlan(enabled, home);
        }

        /// <summary>
        /// Établit la liaison et attend que le téléviseur autorise ce PC. Sans adresse, prend l'appareil branché en USB.
        /// Renvoie l'identifiant de l'appareil pour adb.
        /// </summary>
        public static string Connect(string adb, string host, Job job)
        {
            string target = null;
            if (!string.IsNullOrEmpty(host))
            {
                if (!Adb.IsValidHost(host)) throw new ArgumentException("Adresse invalide.");
                target = Adb.WithPort(host);
                job.Status("Connexion à " + target);
                var o = Adb.Out(adb, "connect " + target, job.Token).Trim();
                job.Log(o);
                if (o.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) < 0 && o.IndexOf("authenticate", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("L'appareil ne répond pas à " + target + ". Vérifiez que le débogage réseau y est activé, et que le PC et l'appareil sont sur le même réseau.");
            }
            var limit = DateTime.UtcNow.AddSeconds(90);
            var told = false;
            while (true)
            {
                job.Token.ThrowIfCancellationRequested();
                var devs = Adb.Devices(adb, job.Token);
                var d = target != null ? devs.FirstOrDefault(x => x.Serial == target) : (devs.FirstOrDefault(x => x.Ready) ?? devs.FirstOrDefault());
                if (d != null && d.Ready) return d.Serial;
                if (d == null && target == null)
                    throw new InvalidOperationException("Aucun appareil détecté. Indiquez une adresse IP, ou branchez l'appareil en USB avec le débogage activé.");
                if (!told)
                {
                    told = true;
                    job.Status("Regardez l'écran de l'appareil : cochez « Toujours autoriser », puis validez");
                    job.Progress(-1);
                }
                if (DateTime.UtcNow > limit)
                    throw new TimeoutException("L'appareil n'a pas autorisé ce PC. Relancez la connexion et validez la demande affichée à l'écran.");
                job.Token.WaitHandle.WaitOne(1500);
            }
        }

        static string Key(string tvId) { return "tv.off." + tvId; }

        /// <summary>Ce qu'OptiTech a désactivé sur ce téléviseur, et qu'il sait donc rétablir.</summary>
        public static List<string> DisabledByUs(string tvId)
        {
            return (Store.Setting(Key(tvId)) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(Adb.IsValidPackage).Distinct().ToList();
        }

        static void Remember(string tvId, IEnumerable<string> pkgs)
        {
            var l = pkgs.Distinct().ToList();
            Store.SetSetting(Key(tvId), l.Count == 0 ? null : string.Join(",", l));
        }

        public static string LabelOf(string pkg)
        {
            var m = Catalog().FirstOrDefault(c => c.Package.EndsWith(".") ? pkg.StartsWith(c.Package, StringComparison.OrdinalIgnoreCase) : c.Package == pkg);
            return m == null ? pkg : m.Label;
        }

        /// <summary>Désactive les éléments choisis. La liste est mémorisée avant chaque commande : un arrêt en cours de route reste réversible.</summary>
        public static int Disable(string adb, string serial, TvInfo tv, IList<TvItem> items, Job job)
        {
            int ok = 0;
            var known = DisabledByUs(tv.Id);
            for (int i = 0; i < items.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                var it = items[i];
                job.Status(it.Label);
                job.Progress(100.0 * i / items.Count);
                if (!Adb.IsValidPackage(it.Package) || IsProtected(it.Package)) { job.Log("  Ignoré par sécurité : " + it.Package); continue; }
                if (!known.Contains(it.Package)) { known.Add(it.Package); Remember(tv.Id, known); }
                var o = Adb.Shell(adb, serial, "pm disable-user --user 0 " + it.Package, job.Token);
                if (o.IndexOf("new state: disabled", StringComparison.OrdinalIgnoreCase) >= 0) ok++;
                else
                {
                    known.Remove(it.Package); Remember(tv.Id, known);
                    job.Log("  Refusé par le téléviseur : " + o.Trim());
                }
            }
            job.Progress(100);
            if (ok > 0) Store.AddHistory("tv:" + tv.Id, "Téléviseur : " + tv.Name, Fmt.N(ok, "application désactivée", "applications désactivées"));
            return ok;
        }

        public static bool Enable(string adb, string serial, TvInfo tv, string pkg, Job job)
        {
            if (!Adb.IsValidPackage(pkg)) return false;
            var o = Adb.Shell(adb, serial, "pm enable " + pkg, job.Token);
            var ok = o.IndexOf("new state: enabled", StringComparison.OrdinalIgnoreCase) >= 0;
            if (ok)
            {
                var known = DisabledByUs(tv.Id); known.Remove(pkg); Remember(tv.Id, known);
            }
            else job.Log("  " + pkg + " : " + o.Trim());
            return ok;
        }

        /// <summary>Réactive tout ce qu'OptiTech a coupé sur ce téléviseur.</summary>
        public static int RestoreAll(string adb, string serial, TvInfo tv, Job job)
        {
            var list = DisabledByUs(tv.Id);
            int ok = 0;
            for (int i = 0; i < list.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                job.Status(LabelOf(list[i]));
                job.Progress(100.0 * i / list.Count);
                if (Enable(adb, serial, tv, list[i], job)) ok++;
            }
            job.Progress(100);
            Store.AddHistory("tv:" + tv.Id, "Téléviseur : " + tv.Name, Fmt.N(ok, "application réactivée", "applications réactivées"));
            return ok;
        }

        public static bool AnimationsFast(string adb, string serial, CancellationToken ct)
        {
            var s = Adb.GetAnimScale(adb, serial, ct);
            return s == "0.5" || s == "0.25" || s == "0";
        }

        /// <summary>Animations deux fois plus courtes : l'interface paraît nettement plus vive sur un téléviseur lent.</summary>
        public static void SetAnimations(string adb, string serial, TvInfo tv, bool fast, Job job)
        {
            Adb.SetAnimScale(adb, serial, "tv.anim." + tv.Id, "Téléviseur : " + tv.Name, fast ? "0.5" : "restore", job);
        }

        public static void SetAnimationPreset(string adb, string serial, TvInfo tv, string scale, Job job)
        {
            Adb.SetAnimScale(adb, serial, "tv.anim." + tv.Id, "Téléviseur : " + tv.Name, scale, job);
        }

        /// <summary>Demande à Android de vider les caches de toutes les applications. Renvoie l'espace gagné.</summary>
        public static long TrimCaches(string adb, string serial, Job job)
        {
            var before = FreeStorage(adb, serial, job.Token);
            Adb.Shell(adb, serial, "pm trim-caches 999999M", job.Token);
            var after = FreeStorage(adb, serial, job.Token);
            return before < 0 || after < 0 ? -1 : Math.Max(0, after - before);
        }
    }
}
