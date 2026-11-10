using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OptiWin
{
    public sealed class SysSnapshot
    {
        public string Machine, OsName, OsVersion, Cpu = "Inconnu", Gpu = "Inconnue", DiskModel = "Inconnu", DiskType = "Inconnu", DiskHealth = "Inconnue";
        public int Build, Ubr, Cores, Threads, CpuMhz;
        public long Ram;
        public bool IsLaptop, SystemDiskIsHdd, IsUefi, SecureBoot, DiskUnhealthy;
        public int? TpmMajor;
        public DateTime? InstalledOn;
        public bool IsFreshInstall { get { return InstalledOn.HasValue && (DateTime.Now - InstalledOn.Value).TotalDays < 45; } }
    }

    public static class SysInfo
    {
        static readonly object gate = new object();
        static SysSnapshot cur;
        public static SysSnapshot Current { get { lock (gate) return cur ?? (cur = Load()); } }

        [StructLayout(LayoutKind.Sequential)]
        struct MemStatus
        {
            public uint Length, Load;
            public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt;
        }
        [DllImport("kernel32", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemStatus m);
        [StructLayout(LayoutKind.Sequential)] struct PowerStatus { public byte AC, Flag, Percent, Saver; public int Life, Full; }
        [DllImport("kernel32")] static extern bool GetSystemPowerStatus(out PowerStatus s);
        [DllImport("kernel32")] static extern bool GetFirmwareType(out int t);
        [DllImport("kernel32")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public static List<ManagementBaseObject> Wmi(string scope, string wql)
        {
            var r = new List<ManagementBaseObject>();
            try
            {
                using (var s = new ManagementObjectSearcher(scope, wql))
                    foreach (var o in s.Get()) r.Add(o);
            }
            catch (Exception e) { Logger.Info("WMI " + wql + " : " + e.Message); }
            return r;
        }

        static string S(ManagementBaseObject o, string p)
        {
            try { var v = o[p]; return v == null ? null : Convert.ToString(v).Trim(); } catch { return null; }
        }
        static long N(ManagementBaseObject o, string p)
        {
            try { var v = o[p]; return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; }
        }

        static SysSnapshot Load()
        {
            var s = new SysSnapshot
            {
                Machine = Environment.MachineName, OsName = Os.ProductName, OsVersion = Os.DisplayVersion,
                Build = Os.Build, Ubr = Os.Ubr, InstalledOn = Os.InstallDate
            };
            s.Ram = (long)RamTotal();

            var cpu = Wmi(@"root\cimv2", "SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor").FirstOrDefault();
            if (cpu != null)
            {
                s.Cpu = Regex.Replace(S(cpu, "Name") ?? "Inconnu", @"\s+", " ").Replace("(R)", "").Replace("(TM)", "");
                s.Cores = (int)N(cpu, "NumberOfCores");
                s.Threads = (int)N(cpu, "NumberOfLogicalProcessors");
                s.CpuMhz = (int)N(cpu, "MaxClockSpeed");
            }

            var gpus = Wmi(@"root\cimv2", "SELECT Name FROM Win32_VideoController")
                .Select(g => S(g, "Name")).Where(n => n != null && !n.Contains("Basic") && !n.Contains("Remote") && !n.Contains("Virtual")).ToList();
            if (gpus.Count > 0) s.Gpu = string.Join(" + ", gpus);

            PowerStatus ps;
            s.IsLaptop = GetSystemPowerStatus(out ps) && ps.Flag != 128 && ps.Flag != 255;

            try { int t; if (GetFirmwareType(out t)) s.IsUefi = t == 2; } catch (EntryPointNotFoundException) { }
            s.SecureBoot = Reg.GetInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") == 1;

            ReadSystemDisk(s);

            var tpm = Wmi(@"root\cimv2\security\microsofttpm", "SELECT SpecVersion FROM Win32_Tpm").FirstOrDefault();
            if (tpm != null)
            {
                var spec = S(tpm, "SpecVersion") ?? "";
                int major;
                if (int.TryParse(spec.Split(new[] { '.', ',' })[0].Trim(), out major)) s.TpmMajor = major;
            }
            return s;
        }

        static void ReadSystemDisk(SysSnapshot s)
        {
            var letter = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0];
            var num = Disks.DiskNumberOf(letter);
            if (num < 0) return;
            var pd = Wmi(@"root\Microsoft\Windows\Storage", "SELECT DeviceId,FriendlyName,MediaType,BusType,HealthStatus FROM MSFT_PhysicalDisk")
                .FirstOrDefault(d => S(d, "DeviceId") == num.ToString());
            if (pd != null)
            {
                s.DiskModel = S(pd, "FriendlyName") ?? s.DiskModel;
                var media = N(pd, "MediaType");
                var bus = N(pd, "BusType");
                s.DiskType = media == 3 ? "HDD" : bus == 17 ? "SSD NVMe" : media == 4 ? "SSD" : "Inconnu";
                s.SystemDiskIsHdd = media == 3;
                var h = N(pd, "HealthStatus");
                s.DiskHealth = h == 0 ? "Bonne" : h == 1 ? "Avertissement" : h == 2 ? "Défaillant" : "Inconnue";
                s.DiskUnhealthy = h == 1 || h == 2;
            }
            else
            {
                var d = Wmi(@"root\cimv2", "SELECT Model,Index FROM Win32_DiskDrive WHERE Index=" + num).FirstOrDefault();
                if (d != null) s.DiskModel = S(d, "Model") ?? s.DiskModel;
            }
        }

        public static ulong RamTotal()
        {
            var m = new MemStatus { Length = (uint)Marshal.SizeOf(typeof(MemStatus)) };
            return GlobalMemoryStatusEx(ref m) ? m.TotalPhys : 0;
        }
        public static int RamLoad()
        {
            var m = new MemStatus { Length = (uint)Marshal.SizeOf(typeof(MemStatus)) };
            return GlobalMemoryStatusEx(ref m) ? (int)m.Load : 0;
        }
        public static long RamUsed()
        {
            var m = new MemStatus { Length = (uint)Marshal.SizeOf(typeof(MemStatus)) };
            return GlobalMemoryStatusEx(ref m) ? (long)(m.TotalPhys - m.AvailPhys) : 0;
        }

        /// <summary>Compteurs bruts du processeur, pour mesurer sur une durée sans gêner la courbe de l'accueil.</summary>
        public static bool RawTimes(out long idle, out long total)
        {
            long kernel, user;
            var ok = GetSystemTimes(out idle, out kernel, out user);
            total = kernel + user;
            return ok;
        }

        static long lastIdle, lastTotal;
        /// <summary>Charge processeur depuis l'appel précédent, sans compteur de performance coûteux.</summary>
        public static double CpuLoad()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return 0;
            var total = kernel + user;
            var dt = total - lastTotal; var di = idle - lastIdle;
            lastTotal = total; lastIdle = idle;
            return dt <= 0 ? 0 : Math.Max(0, Math.Min(100, 100.0 * (dt - di) / dt));
        }

        public static TimeSpan Uptime { get { return TimeSpan.FromMilliseconds(Native.GetTickCount64()); } }

        public static DriveInfo SystemDrive
        {
            get { return new DriveInfo((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\"); }
        }

        /// <summary>Profil le plus utile pour cette machine, d'après des faits mesurés.</summary>
        public static Prof RecommendedProfile(SysSnapshot s)
        {
            if (s.Ram > 0 && s.Ram < 8L * (1L << 30) || s.SystemDiskIsHdd || (s.Threads > 0 && s.Threads <= 2)) return Prof.OldPc;
            if (s.IsFreshInstall) return Prof.Fresh;
            if (s.IsLaptop) return Prof.Laptop;
            // Un profil « Jeux » n'a de sens qu'avec une vraie carte graphique.
            return Regex.IsMatch(s.Gpu ?? "", "GeForce|RTX|GTX|Radeon RX|Radeon HD|Radeon R[579]|Intel.*Arc") ? Prof.Gaming : Prof.Fresh;
        }
        public static string ProfileName(Prof p)
        {
            switch (p)
            {
                case Prof.OldPc: return "Vieux PC";
                case Prof.Fresh: return "Fraîchement installé";
                case Prof.Gaming: return "Jeux";
                case Prof.Laptop: return "Portable";
                case Prof.Privacy: return "Confidentialité";
                default: return "Personnalisé";
            }
        }
        public static string ProfileReason(SysSnapshot s, Prof p)
        {
            var ram = Fmt.Size(s.Ram) + " de RAM";
            switch (p)
            {
                case Prof.OldPc: return ram + (s.SystemDiskIsHdd ? ", disque dur" : "") + " : priorité à la légèreté";
                case Prof.Fresh: return s.IsFreshInstall && s.InstalledOn.HasValue ? "Windows installé le " + Fmt.Date(s.InstalledOn.Value) + " : on retire le superflu" : "Retirer le superflu et les nuisances de Windows";
                case Prof.Laptop: return "Portable : moins d'activité en arrière-plan, plus d'autonomie";
                default: return ram + ", " + s.DiskType + " : machine récente";
            }
        }
    }

    public sealed class Insight
    {
        public string Level { get; set; }   // ok, info, warn, bad
        public string Title { get; set; }
        public string Detail { get; set; }
        public string ActionLabel { get; set; }
        public System.Windows.Input.ICommand Cmd { get; set; }
        public bool HasAction { get { return Cmd != null; } }
    }

    public sealed class HealthResult
    {
        public int Score = 100;
        public List<Insight> Insights = new List<Insight>();
        public string Verdict { get { return Score >= 85 ? "Très bon état" : Score >= 65 ? "Bon état, améliorable" : Score >= 45 ? "À entretenir" : "Mérite une vraie remise en forme"; } }
    }

    /// <summary>
    /// Score transparent, calculé uniquement à partir de faits mesurés sur ce PC.
    /// Chaque point perdu correspond à une recommandation affichée.
    /// </summary>
    public static class Health
    {
        public static HealthResult Compute(SysSnapshot s, long cleanable, int enabledStartup, int coverageMissing, Func<string, System.Windows.Input.ICommand> go)
        {
            var r = new HealthResult();
            Action<int, string, string, string, string, string> add = delegate(int cost, string lvl, string title, string detail, string label, string page)
            {
                r.Score -= cost;
                r.Insights.Add(new Insight { Level = lvl, Title = title, Detail = detail, ActionLabel = label, Cmd = page == null || go == null ? null : go(page) });
            };

            if (s.DiskUnhealthy)
                add(30, "bad", "Le disque système signale un problème", "État : " + s.DiskHealth + ". Sauvegardez vos fichiers dès maintenant.", "Voir", "diagnostic");

            try
            {
                var d = SysInfo.SystemDrive;
                var pct = 100.0 * d.AvailableFreeSpace / d.TotalSize;
                if (pct < 10) add(25, "bad", "Disque presque plein", "Il reste " + Fmt.Size(d.AvailableFreeSpace) + " sur " + d.Name + " : Windows ralentit nettement sous 10 %.", "Nettoyer", "clean");
                else if (pct < 20) add(10, "warn", "Espace disque limité", "Il reste " + Fmt.Size(d.AvailableFreeSpace) + " sur " + d.Name + ".", "Nettoyer", "clean");
            }
            catch { }

            if (cleanable > 5L << 30) add(10, "warn", Fmt.Size(cleanable) + " récupérables", "Caches et fichiers temporaires que Windows ne supprime pas seul.", "Nettoyer", "clean");
            else if (cleanable > 1L << 30) add(5, "info", Fmt.Size(cleanable) + " récupérables", "Caches et fichiers temporaires.", "Nettoyer", "clean");

            if (enabledStartup > 10) add(10, "warn", enabledStartup + " programmes au démarrage", "Chacun allonge l'ouverture de session et occupe de la mémoire.", "Trier", "startup");
            else if (enabledStartup > 5) add(5, "info", enabledStartup + " programmes au démarrage", "Quelques-uns sont sans doute superflus.", "Trier", "startup");

            if (coverageMissing > 0)
            {
                var cost = Math.Min(20, coverageMissing * 2);
                add(cost, coverageMissing > 5 ? "warn" : "info", coverageMissing + (coverageMissing > 1 ? " réglages conseillés" : " réglage conseillé") + " à appliquer", "Pour le profil adapté à ce PC. Tous sont réversibles.", "Voir", "optimize");
            }

            if (SysInfo.Uptime.TotalDays > 14) add(5, "info", "Allumé depuis " + Fmt.Span(SysInfo.Uptime), "Un redémarrage libère la mémoire et termine les mises à jour.", null, null);
            if (SysInfo.RamLoad() > 85) add(10, "warn", "Mémoire saturée", SysInfo.RamLoad() + " % de la RAM est utilisée en ce moment.", "Démarrage", "startup");

            if (r.Insights.Count == 0)
                r.Insights.Add(new Insight { Level = "ok", Title = "Rien à signaler", Detail = "Aucun point mesuré ne demande d'action." });

            r.Score = Math.Max(0, Math.Min(100, r.Score));
            r.Insights = r.Insights.OrderBy(i => i.Level == "bad" ? 0 : i.Level == "warn" ? 1 : i.Level == "info" ? 2 : 3).ToList();
            return r;
        }
    }
}
