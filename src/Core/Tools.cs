using System;
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
using System.Windows.Input;
using Microsoft.Win32;

namespace OptiWin
{
    public sealed class ToolAction
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Desc { get; set; }
        public string Icon { get; set; }
        public object IconGeo { get { return System.Windows.Application.Current.Resources[Icon]; } }
        public string Duration { get; set; }
        public bool NeedsAdmin { get; set; }
        public bool CanRun { get { return !NeedsAdmin || Os.IsAdmin; } }
        public string Confirm;
        public bool PickFolder;
        public Action<Job, string> Run;
        public ICommand Cmd { get; set; }
    }

    public static class Tools
    {
        static readonly Regex Pct = new Regex(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

        /// <summary>Lance un outil système en suivant son pourcentage, sans noyer le journal.</summary>
        public static int Tracked(Job job, string exe, string args)
        {
            return Proc.Run(exe, args, delegate(string l)
            {
                var m = Pct.Match(l);
                if (m.Success)
                {
                    double v;
                    if (double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v <= 100) job.Progress(v);
                    if (l.Contains("[==") || l.Contains("[ ") || l.Length < 60) return;
                }
                job.Log(l.Trim());
            }, job.Token);
        }

        public static List<ToolAction> Catalog()
        {
            var sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            return new List<ToolAction>
            {
                new ToolAction { Id = "repair", Icon = "IcoShield", Title = "Réparer les fichiers système", Desc = "DISM puis SFC : compare Windows à une copie saine et remplace ce qui est abîmé.", Duration = "10 à 30 min", NeedsAdmin = true,
                    Run = (j, _) =>
                    {
                        j.Status("Réparation de l'image Windows (DISM)");
                        j.Progress(0);
                        var a = Tracked(j, "DISM.exe", "/Online /Cleanup-Image /RestoreHealth");
                        j.Status("Vérification des fichiers système (SFC)");
                        j.Progress(0);
                        var b = Tracked(j, "sfc.exe", "/scannow");
                        j.Summary = a == 0 && b == 0 ? "Réparation terminée." : "Terminé avec des avertissements : voir le journal (DISM " + a + ", SFC " + b + ").";
                    } },
                new ToolAction { Id = "defrag", Icon = "IcoDrive", Title = "Optimiser les disques", Desc = "TRIM sur les SSD, défragmentation sur les disques durs : Windows choisit selon le type.", Duration = "1 à 60 min", NeedsAdmin = true,
                    Run = (j, _) => { j.Status("Optimisation de tous les volumes"); Tracked(j, "defrag.exe", "/C /O /U /V"); j.Summary = "Disques optimisés."; } },
                new ToolAction { Id = "chkdsk", Icon = "IcoSearch", Title = "Analyser le disque système", Desc = "Recherche les erreurs du système de fichiers, sans rien bloquer ni modifier.", Duration = "2 à 10 min", NeedsAdmin = true,
                    Run = (j, _) => { j.Status("Analyse de " + sys); var c = Tracked(j, "chkdsk.exe", sys + " /scan"); j.Summary = c == 0 ? "Aucune erreur détectée." : "Des problèmes ont été signalés : voir le journal."; } },
                new ToolAction { Id = "netreset", Icon = "IcoGlobe", Title = "Réparer la connexion réseau", Desc = "Vide le cache DNS, réinitialise Winsock et la pile IP. Un redémarrage sera nécessaire.", Duration = "Quelques secondes", NeedsAdmin = true,
                    Confirm = "Les réglages IP manuels (adresse fixe) peuvent être réinitialisés. Continuer ?",
                    Run = (j, _) =>
                    {
                        Proc.Run("ipconfig.exe", "/flushdns", j.Log, j.Token);
                        Proc.Run("netsh.exe", "winsock reset", j.Log, j.Token);
                        Proc.Run("netsh.exe", "int ip reset", j.Log, j.Token);
                        j.Summary = "Réseau réinitialisé. Redémarrez pour terminer.";
                    } },
                new ToolAction { Id = "dnsflush", Icon = "IcoGlobe", Title = "Vider le cache DNS", Desc = "Utile quand un site reste inaccessible alors que le réseau fonctionne.", Duration = "Immédiat",
                    Run = (j, _) => { Proc.Run("ipconfig.exe", "/flushdns", j.Log, j.Token); j.Summary = "Cache DNS vidé."; } },
                new ToolAction { Id = "wureset", Icon = "IcoRefresh", Title = "Réparer Windows Update", Desc = "Arrête les services, met de côté les caches de mises à jour puis relance le tout.", Duration = "1 min", NeedsAdmin = true,
                    Confirm = "L'historique des mises à jour affiché dans Windows sera réinitialisé. Continuer ?",
                    Run = (j, _) =>
                    {
                        foreach (var s in new[] { "wuauserv", "bits", "cryptsvc", "msiserver" }) Proc.Run("net.exe", "stop " + s + " /y", j.Log, j.Token);
                        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        var stamp = DateTime.Now.ToString("yyyyMMddHHmm");
                        foreach (var d in new[] { Path.Combine(win, "SoftwareDistribution"), Path.Combine(win, @"System32\catroot2") })
                            try { if (Directory.Exists(d)) { Directory.Move(d, d + ".old" + stamp); j.Log("Mis de côté : " + d); } }
                            catch (Exception e) { j.Log("Impossible de déplacer " + d + " : " + e.Message); }
                        foreach (var s in new[] { "cryptsvc", "bits", "wuauserv" }) Proc.Run("net.exe", "start " + s, j.Log, j.Token);
                        j.Summary = "Windows Update réinitialisé. Les anciens dossiers portent le suffixe .old" + stamp + ".";
                    } },
                new ToolAction { Id = "icons", Icon = "IcoMonitor", Title = "Reconstruire le cache d'icônes", Desc = "Corrige les icônes blanches ou incorrectes. L'Explorateur redémarre.", Duration = "Quelques secondes",
                    Run = (j, _) =>
                    {
                        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
                        foreach (var p in Process.GetProcessesByName("explorer")) try { p.Kill(); } catch { }
                        Thread.Sleep(800);
                        try { foreach (var f in Directory.GetFiles(dir, "iconcache_*.db")) try { File.Delete(f); } catch { } } catch { }
                        Thread.Sleep(1500);
                        if (Process.GetProcessesByName("explorer").Length == 0) Process.Start("explorer.exe");
                        j.Summary = "Cache d'icônes reconstruit.";
                    } },
                new ToolAction { Id = "drivers", Icon = "IcoDownload", Title = "Sauvegarder les pilotes", Desc = "Copie tous les pilotes installés dans un dossier, à réinjecter après une réinstallation.", Duration = "1 à 5 min", NeedsAdmin = true, PickFolder = true,
                    Run = (j, folder) => { j.Status("Export des pilotes"); Proc.RunChecked("DISM.exe", "/Online /Export-Driver /Destination:\"" + folder + "\"", j.Log, j.Token); j.Summary = "Pilotes sauvegardés dans " + folder + "."; } },
                new ToolAction { Id = "rescue", Icon = "IcoUsb", Title = "Ajouter le kit de secours à une clé", Desc = "Copier ses fichiers et réparer le démarrage d'un PC en panne. Choisissez la racine d'une clé d'installation de Windows.", Duration = "Immédiat", PickFolder = true,
                    Run = (j, folder) =>
                    {
                        var dest = Path.Combine(folder, @"OptiWin\Secours");
                        RescueKit.WriteTo(dest);
                        j.Summary = "Kit de secours copié dans " + dest + ". Le mode d'emploi est dans LISEZMOI.txt.";
                    } },
                new ToolAction { Id = "battery", Icon = "IcoActivity", Title = "Rapport de batterie", Desc = "Usure réelle comparée à la capacité d'origine, historique d'autonomie.", Duration = "Immédiat",
                    Run = (j, _) =>
                    {
                        var path = Path.Combine(Path.GetTempPath(), "OptiWin-batterie.html");
                        Proc.RunChecked("powercfg.exe", "/batteryreport /output \"" + path + "\"", j.Log, j.Token);
                        Process.Start(path);
                        j.Summary = "Rapport ouvert dans le navigateur.";
                    } },
                new ToolAction { Id = "report", Icon = "IcoCpu", Title = "Exporter un rapport système", Desc = "Fiche texte de la machine, à joindre à une demande d'aide.", Duration = "Immédiat",
                    Run = (j, _) =>
                    {
                        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OptiWin-Rapport-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".txt");
                        File.WriteAllText(path, BuildReport(), Encoding.UTF8);
                        Process.Start(path);
                        j.Summary = "Rapport enregistré : " + path;
                    } },
                new ToolAction { Id = "weekly", Icon = "IcoClock", Title = "Entretien automatique chaque semaine", Desc = "Nettoie les fichiers temporaires sans intervention. Relancez pour le retirer.", Duration = "Immédiat", NeedsAdmin = true,
                    Run = (j, _) => ToggleWeekly(j) },
            };
        }

        const string TaskName = @"OptiWin\Entretien";

        public static bool WeeklyInstalled()
        {
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                dynamic t = svc.GetFolder(@"\OptiWin").GetTask("Entretien");
                return t != null;
            }
            catch { return false; }
        }

        public static string WeeklyXml(string exe)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "<RegistrationInfo><Description>Entretien hebdomadaire OptiWin : fichiers temporaires uniquement.</Description></RegistrationInfo>\r\n" +
                "<Triggers><CalendarTrigger><StartBoundary>2026-01-04T12:00:00</StartBoundary><ScheduleByWeek><DaysOfWeek><Sunday/></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger></Triggers>\r\n" +
                "<Principals><Principal id=\"Author\"><RunLevel>HighestAvailable</RunLevel><LogonType>InteractiveToken</LogonType></Principal></Principals>\r\n" +
                "<Settings><StartWhenAvailable>true</StartWhenAvailable><DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>true</StopIfGoingOnBatteries><ExecutionTimeLimit>PT1H</ExecutionTimeLimit><Priority>7</Priority></Settings>\r\n" +
                "<Actions><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--maintenance</Arguments></Exec></Actions>\r\n" +
                "</Task>";
        }

        static void ToggleWeekly(Job j)
        {
            if (WeeklyInstalled())
            {
                Proc.RunChecked("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", j.Log, j.Token);
                Store.AddHistory("weekly", "Entretien automatique", "Retiré");
                j.Summary = "Entretien automatique retiré.";
                return;
            }
            EnableWeekly(j);
            j.Summary = "Entretien programmé chaque dimanche à 12 h, ou dès que le PC est allumé. Ne déplacez pas OptiWin.exe.";
        }

        public static void EnableWeekly(Job j)
        {
            var xml = Path.Combine(Path.GetTempPath(), "optiwin-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(xml, WeeklyXml(Paths.Exe), Encoding.Unicode);
            try { Proc.RunChecked("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + xml + "\" /F", j.Log, j.Token); }
            finally { try { File.Delete(xml); } catch { } }
            Store.AddHistory("weekly", "Entretien automatique", "Activé");
        }

        public static string BuildReport()
        {
            var s = SysInfo.Current;
            var b = new StringBuilder();
            b.AppendLine("Rapport OptiWin — " + DateTime.Now);
            b.AppendLine("Machine      : " + s.Machine);
            b.AppendLine("Système      : " + s.OsName + " " + s.OsVersion + " (build " + s.Build + "." + s.Ubr + ")");
            b.AppendLine("Installé le  : " + (s.InstalledOn.HasValue ? s.InstalledOn.Value.ToString("d") : "?"));
            b.AppendLine("Processeur   : " + s.Cpu + " — " + s.Cores + " cœurs / " + s.Threads + " threads");
            b.AppendLine("Mémoire      : " + Fmt.Size(s.Ram) + " (" + SysInfo.RamLoad() + " % utilisés)");
            b.AppendLine("Carte vidéo  : " + s.Gpu);
            b.AppendLine("Disque       : " + s.DiskModel + " (" + s.DiskType + "), santé : " + s.DiskHealth);
            b.AppendLine("Micrologiciel: " + (s.IsUefi ? "UEFI" : "BIOS") + ", Secure Boot " + (s.SecureBoot ? "actif" : "inactif"));
            b.AppendLine("Allumé depuis: " + Fmt.Span(SysInfo.Uptime));
            b.AppendLine();
            b.AppendLine("Lecteurs :");
            foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
                b.AppendLine("  " + d.Name + " " + d.DriveFormat + "  " + Fmt.Size(d.AvailableFreeSpace) + " libres sur " + Fmt.Size(d.TotalSize));
            b.AppendLine();
            b.AppendLine("Programmes au démarrage :");
            foreach (var i in StartupManager.List())
                b.AppendLine("  [" + (i.IsOn ? "actif" : "inactif") + "] " + i.Name + " — " + i.Command);
            b.AppendLine();
            b.AppendLine("Processus les plus gourmands :");
            foreach (var p in Process.GetProcesses().OrderByDescending(x => { try { return x.WorkingSet64; } catch { return 0; } }).Take(15))
                try { b.AppendLine("  " + Fmt.Size(p.WorkingSet64).PadLeft(8) + "  " + p.ProcessName); } catch { }
            b.AppendLine();
            b.AppendLine("Réglages OptiWin appliqués :");
            foreach (var t in TweakCatalog.All.Where(x => { x.RefreshState(); return x.IsApplied; }))
                b.AppendLine("  " + t.Title);
            return b.ToString();
        }
    }

    public sealed class CheckItem
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Level { get; set; } // ok, warn, bad, info
    }

    /// <summary>Compare ce PC aux conditions d'installation de Windows 11.</summary>
    public static class Win11Check
    {
        public static List<CheckItem> Run(SysSnapshot s)
        {
            var r = new List<CheckItem>();
            Action<string, string, string> add = (t, d, l) => r.Add(new CheckItem { Title = t, Detail = d, Level = l });

            add("Processeur 64 bits, 2 cœurs, 1 GHz", s.Cpu + " — " + s.Cores + " cœurs, " + Fmt.Num(s.CpuMhz / 1000.0, "0.0") + " GHz",
                Environment.Is64BitOperatingSystem && s.Cores >= 2 && s.CpuMhz >= 1000 ? "ok" : "bad");

            var gen = CpuGeneration(s.Cpu);
            add("Génération du processeur", gen == null ? "Non identifiée : consultez la liste officielle de Microsoft." : gen.Value ? "Génération prise en charge d'après son nom." : "Antérieure aux processeurs pris en charge. Installation possible avec les options de la clé USB, sans garantie de mises à jour.",
                gen == null ? "info" : gen.Value ? "ok" : "warn");

            add("Mémoire : 4 Go minimum", Fmt.Size(s.Ram), s.Ram >= 3900L << 20 ? "ok" : "bad");
            try
            {
                var d = SysInfo.SystemDrive;
                add("Disque : 64 Go minimum", Fmt.Size(d.TotalSize), d.TotalSize >= 60L << 30 ? "ok" : "bad");
            }
            catch { }
            add("Démarrage UEFI", s.IsUefi ? "UEFI" : "BIOS hérité", s.IsUefi ? "ok" : "bad");
            add("Secure Boot", s.SecureBoot ? "Actif" : "Inactif, souvent activable dans l'UEFI", s.SecureBoot ? "ok" : "warn");
            if (s.TpmMajor.HasValue)
                add("TPM 2.0", "Version " + s.TpmMajor.Value + " détectée", s.TpmMajor.Value >= 2 ? "ok" : "bad");
            else
                add("TPM 2.0", Os.IsAdmin ? "Non détecté : vérifiez l'option fTPM ou PTT dans l'UEFI." : "Vérification réservée au mode administrateur.", Os.IsAdmin ? "warn" : "info");
            return r;
        }

        /// <summary>Estimation à partir du nom : faux négatifs possibles, jamais présentée comme une certitude.</summary>
        static bool? CpuGeneration(string name)
        {
            var m = Regex.Match(name, @"i[3579]-(\d{4,5})");
            if (m.Success)
            {
                var num = m.Groups[1].Value;
                var g = num.Length == 5 ? int.Parse(num.Substring(0, 2)) : int.Parse(num.Substring(0, 1));
                return g >= 8;
            }
            if (Regex.IsMatch(name, @"Core\(?.*Ultra")) return true;
            m = Regex.Match(name, @"Ryzen\s+(?:Threadripper\s+)?\d\s+(\d)\d{3}");
            if (m.Success) return int.Parse(m.Groups[1].Value) >= 2;
            return null;
        }
    }

    public sealed class DnsResult : Bindable
    {
        public string Name { get; set; }
        public string Primary { get; set; }
        public string Secondary { get; set; }
        public string Note { get; set; }
        double ms = -1;
        public double Ms { get { return ms; } set { if (Set(ref ms, value)) Raise("MsText"); } }
        public string MsText { get { return ms < 0 ? "—" : double.IsInfinity(ms) ? "Sans réponse" : Fmt.Num(ms, "0") + " ms"; } }
        bool best;
        public bool IsBest { get { return best; } set { Set(ref best, value); } }
        public ICommand Use { get; set; }
    }

    public static class Dns
    {
        public static List<DnsResult> Candidates()
        {
            return new List<DnsResult>
            {
                new DnsResult { Name = "Cloudflare", Primary = "1.1.1.1", Secondary = "1.0.0.1", Note = "Très rapide" },
                new DnsResult { Name = "Google", Primary = "8.8.8.8", Secondary = "8.8.4.4", Note = "Très répandu" },
                new DnsResult { Name = "Quad9", Primary = "9.9.9.9", Secondary = "149.112.112.112", Note = "Bloque les domaines malveillants" },
                new DnsResult { Name = "AdGuard", Primary = "94.140.14.14", Secondary = "94.140.15.15", Note = "Bloque les publicités" },
                new DnsResult { Name = "OpenDNS", Primary = "208.67.222.222", Secondary = "208.67.220.220", Note = "Filtrage possible" },
            };
        }

        static readonly string[] Domains = { "www.microsoft.com", "www.wikipedia.org", "www.cloudflare.com", "www.google.com", "www.amazon.fr", "www.lemonde.fr" };

        /// <summary>Temps médian de réponse mesuré par de vraies requêtes DNS. Infini si le serveur ne répond pas.</summary>
        public static double Measure(string server, CancellationToken ct)
        {
            var times = new List<double>();
            foreach (var d in Domains)
            {
                ct.ThrowIfCancellationRequested();
                var t = Query(server, d);
                if (t >= 0) times.Add(t);
            }
            if (times.Count == 0) return double.PositiveInfinity;
            times.Sort();
            return times[times.Count / 2];
        }

        static double Query(string server, string domain)
        {
            try
            {
                var q = BuildQuery(domain);
                using (var u = new UdpClient())
                {
                    u.Client.ReceiveTimeout = 1500;
                    u.Connect(IPAddress.Parse(server), 53);
                    var sw = Stopwatch.StartNew();
                    u.Send(q, q.Length);
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var a = u.Receive(ref ep);
                    sw.Stop();
                    if (a.Length < 12 || a[0] != q[0] || a[1] != q[1] || (a[3] & 0x0F) != 0) return -1;
                    return sw.Elapsed.TotalMilliseconds;
                }
            }
            catch { return -1; }
        }

        public static byte[] BuildQuery(string domain)
        {
            var m = new MemoryStream();
            var id = new byte[2];
            new Random().NextBytes(id);
            m.Write(id, 0, 2);
            m.Write(new byte[] { 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
            foreach (var part in domain.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(part);
                m.WriteByte((byte)b.Length);
                m.Write(b, 0, b.Length);
            }
            m.Write(new byte[] { 0, 0, 1, 0, 1 }, 0, 5);
            return m.ToArray();
        }

        static IEnumerable<NetworkInterface> ActiveAdapters()
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                n.GetIPProperties().GatewayAddresses.Any());
        }

        const string TcpIf = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";

        public static string CurrentSummary()
        {
            var parts = new List<string>();
            foreach (var n in ActiveAdapters())
            {
                var dns = n.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                var manual = !string.IsNullOrEmpty(Reg.GetStr(RegistryHive.LocalMachine, TcpIf + n.Id, "NameServer"));
                parts.Add(n.Name + " : " + (dns.Count == 0 ? "aucun" : string.Join(", ", dns)) + (manual ? " (manuel)" : " (automatique)"));
            }
            return parts.Count == 0 ? "Aucune carte réseau active." : string.Join("   ·   ", parts);
        }

        public static void Apply(string primary, string secondary, Job j)
        {
            foreach (var n in ActiveAdapters())
            {
                var key = "dns.backup." + n.Id;
                // Conserve le réglage d'origine une seule fois, pour pouvoir y revenir.
                if (Store.Setting(key) == null) Store.SetSetting(key, Reg.GetStr(RegistryHive.LocalMachine, TcpIf + n.Id, "NameServer") ?? "");
                Proc.RunChecked("netsh.exe", "interface ipv4 set dnsservers name=\"" + n.Name + "\" static " + primary + " primary validate=no", j.Log, j.Token);
                Proc.Run("netsh.exe", "interface ipv4 add dnsservers name=\"" + n.Name + "\" " + secondary + " index=2 validate=no", j.Log, j.Token);
                j.Log(n.Name + " : DNS réglé sur " + primary + " et " + secondary);
            }
            Proc.Run("ipconfig.exe", "/flushdns", null, j.Token);
            Store.AddHistory("dns", "DNS : " + primary, "Appliqué");
        }

        public static void RestoreOriginal(Job j)
        {
            foreach (var n in ActiveAdapters())
            {
                var saved = Store.Setting("dns.backup." + n.Id) ?? "";
                var list = saved.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (list.Length == 0)
                    Proc.RunChecked("netsh.exe", "interface ipv4 set dnsservers name=\"" + n.Name + "\" dhcp", j.Log, j.Token);
                else
                {
                    Proc.RunChecked("netsh.exe", "interface ipv4 set dnsservers name=\"" + n.Name + "\" static " + list[0] + " primary validate=no", j.Log, j.Token);
                    for (int i = 1; i < list.Length; i++)
                        Proc.Run("netsh.exe", "interface ipv4 add dnsservers name=\"" + n.Name + "\" " + list[i] + " index=" + (i + 1) + " validate=no", j.Log, j.Token);
                }
                Store.SetSetting("dns.backup." + n.Id, null);
                j.Log(n.Name + " : réglage DNS d'origine restauré.");
            }
            Proc.Run("ipconfig.exe", "/flushdns", null, j.Token);
            Store.AddHistory("dns", "DNS", "Réglage d'origine restauré");
        }
    }
}
