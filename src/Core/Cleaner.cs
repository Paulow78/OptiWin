using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace OptiWin
{
    public sealed class FsSpec
    {
        public string Root, Pattern = "*";
        public int MinAgeHours;
        public bool Recurse = true;
        public FsSpec(string root, int minAgeHours = 0, string pattern = "*", bool recurse = true)
        { Root = root; MinAgeHours = minAgeHours; Pattern = pattern; Recurse = recurse; }
    }

    /// <summary>
    /// Mesure et suppression de fichiers. Garde-fous : jamais de racine ni de dossier sensible,
    /// jamais de suivi de jonction ou de lien symbolique, fichiers récents et verrouillés ignorés.
    /// </summary>
    public static class Fs
    {
        static readonly string[] Forbidden = BuildForbidden();
        static string[] BuildForbidden()
        {
            var l = new List<string>();
            foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.Desktop })
            {
                var p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) l.Add(Norm(p));
            }
            return l.ToArray();
        }
        static string Norm(string p) { return Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant(); }

        static bool IsSafeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            string n;
            try { n = Norm(root); } catch { return false; }
            if (n.Length <= 3) return false;
            return !Forbidden.Contains(n);
        }

        public static long Measure(IEnumerable<FsSpec> specs, CancellationToken ct)
        {
            long t = 0;
            foreach (var s in specs) t += Walk(s.Root, s, false, ct);
            return t;
        }

        public static long Delete(IEnumerable<FsSpec> specs, CancellationToken ct)
        {
            long t = 0;
            foreach (var s in specs) t += Walk(s.Root, s, true, ct);
            return t;
        }

        static long Walk(string dir, FsSpec s, bool delete, CancellationToken ct)
        {
            // Un dossier protégé n'est acceptable que pour un motif de fichier précis, sans descendre dedans.
            var exactFile = !s.Recurse && s.Pattern != "*";
            if ((!IsSafeRoot(s.Root) && !exactFile) || !Directory.Exists(dir)) return 0;
            long total = 0;
            var cutoff = DateTime.UtcNow.AddHours(-s.MinAgeHours);
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles(s.Pattern))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (s.MinAgeHours > 0 && (f.LastWriteTimeUtc > cutoff || f.CreationTimeUtc > cutoff)) continue;
                        var len = f.Length;
                        if (delete)
                        {
                            if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
                            f.Delete();
                        }
                        total += len;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (s.Recurse)
                {
                    foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            total += Walk(d.FullName, s, delete, ct);
                            // Ne supprime qu'un dossier devenu vide, et jamais la racine visée.
                            if (delete && s.Pattern == "*" && (s.MinAgeHours == 0 || d.CreationTimeUtc < cutoff) && !d.EnumerateFileSystemInfos().Any()) d.Delete(false);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return total;
        }
    }

    public sealed class CleanCat : Bindable
    {
        public string Id;
        public string Title { get; set; }
        public string Desc { get; set; }
        public string Group { get; set; }
        public bool NeedsAdmin { get; set; }
        public bool Permanent;
        public bool SizeUnknown;
        public Func<CancellationToken, long> Scanner;
        public Func<Job, long> Cleaner;

        bool selected; long size = -1; string status = "";
        public bool Selected { get { return selected; } set { Set(ref selected, value); } }
        public long Size { get { return size; } set { if (Set(ref size, value)) Raise("SizeText"); } }
        public string SizeText { get { return SizeUnknown ? "Variable" : size < 0 ? "—" : size == 0 ? "Vide" : Fmt.Size(size); } }
        public string Status { get { return status; } set { Set(ref status, value); } }
        public bool Available { get { return !NeedsAdmin || Os.IsAdmin; } }
    }

    public static class Cleaner
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct RbInfo { public int Size; public long Bytes; public long Items; }
        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref RbInfo info);
        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        static string L(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel); }
        static string W(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), rel); }
        static string PD(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), rel); }
        static string SysRoot { get { return Environment.GetEnvironmentVariable("SystemDrive") + "\\"; } }

        static CleanCat Simple(string id, string group, string title, string desc, bool on, bool admin, Func<IEnumerable<FsSpec>> specs)
        {
            return new CleanCat
            {
                Id = id, Group = group, Title = title, Desc = desc, Selected = on && (!admin || Os.IsAdmin), NeedsAdmin = admin,
                Scanner = ct => Fs.Measure(specs(), ct),
                Cleaner = j => Fs.Delete(specs(), j.Token)
            };
        }

        static IEnumerable<FsSpec> BrowserSpecs()
        {
            var roots = new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data", @"Vivaldi\User Data", @"Opera Software\Opera Stable" };
            foreach (var r in roots)
            {
                var root = L(r);
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> profiles;
                try { profiles = Directory.GetDirectories(root).Where(d => { var n = Path.GetFileName(d); return n == "Default" || n.StartsWith("Profile "); }).ToList(); }
                catch { continue; }
                foreach (var p in profiles)
                    foreach (var sub in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" })
                        yield return new FsSpec(Path.Combine(p, sub));
            }
            var ff = L(@"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ff))
            {
                string[] dirs = new string[0];
                try { dirs = Directory.GetDirectories(ff); } catch { }
                foreach (var d in dirs)
                {
                    yield return new FsSpec(Path.Combine(d, "cache2"));
                    yield return new FsSpec(Path.Combine(d, "startupCache"));
                }
            }
        }

        public static string[] RunningBrowsers()
        {
            return new[] { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi" }.Where(n => Process.GetProcessesByName(n).Length > 0).ToArray();
        }

        static bool UpdatePending()
        {
            return Reg.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                || File.Exists(W(@"WinSxS\pending.xml"));
        }

        public static List<CleanCat> BuildCatalog()
        {
            var g1 = "Fichiers temporaires"; var g2 = "Windows"; var g3 = "Options à décider";
            var list = new List<CleanCat>();

            list.Add(Simple("temp.user", g1, "Fichiers temporaires de l'utilisateur", "Fichiers de plus de 24 h, pour ne rien retirer à un programme en cours.", true, false,
                () => new[] { new FsSpec(Path.GetTempPath(), 24) }));
            list.Add(Simple("temp.win", g1, "Fichiers temporaires de Windows", "Dossier Windows\\Temp, fichiers de plus de 24 h.", true, true,
                () => new[] { new FsSpec(W("Temp"), 24) }));

            var browsers = Simple("browsers", g1, "Caches des navigateurs", "Chrome, Edge, Brave, Firefox, Opera, Vivaldi. Mots de passe, historique et cookies ne sont pas touchés.", true, false, BrowserSpecs);
            browsers.Cleaner = j =>
            {
                var run = RunningBrowsers();
                if (run.Length > 0) j.Log("Navigateur ouvert (" + string.Join(", ", run) + ") : les fichiers en cours d'utilisation sont ignorés.");
                return Fs.Delete(BrowserSpecs(), j.Token);
            };
            list.Add(browsers);

            list.Add(Simple("thumbs", g1, "Miniatures de l'Explorateur", "Recréées automatiquement à l'affichage des dossiers.", true, false,
                () => new[] { new FsSpec(L(@"Microsoft\Windows\Explorer"), 0, "thumbcache_*.db", false) }));
            list.Add(Simple("shader", g1, "Caches de shaders", "DirectX, NVIDIA et AMD. Recréés au prochain lancement des jeux.", true, false,
                () => new[] { new FsSpec(L("D3DSCache")), new FsSpec(L(@"NVIDIA\DXCache")), new FsSpec(L(@"NVIDIA\GLCache")), new FsSpec(L(@"AMD\DxCache")), new FsSpec(L(@"AMD\GLCache")) }));

            var wu = Simple("wu", g2, "Cache de Windows Update", "Mises à jour déjà installées. Ignoré si une installation attend un redémarrage.", true, true,
                () => new[] { new FsSpec(W(@"SoftwareDistribution\Download")) });
            wu.Cleaner = j =>
            {
                if (UpdatePending()) { j.Log("Une mise à jour attend un redémarrage : cache conservé par précaution."); return 0; }
                Proc.Run("net.exe", "stop wuauserv /y", null);
                Proc.Run("net.exe", "stop bits /y", null);
                try { return Fs.Delete(new[] { new FsSpec(W(@"SoftwareDistribution\Download")) }, j.Token); }
                finally { Proc.Run("net.exe", "start bits", null); Proc.Run("net.exe", "start wuauserv", null); }
            };
            list.Add(wu);

            var dopt = Simple("dopt", g2, "Optimisation de la distribution", "Copies de mises à jour conservées pour le partage réseau.", true, true,
                () => new[] { new FsSpec(W(@"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")) });
            dopt.Cleaner = j =>
            {
                var path = new[] { new FsSpec(W(@"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")) };
                var before = Fs.Measure(path, j.Token);
                PS.Run("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue");
                return Math.Max(0, before - Fs.Measure(path, j.Token));
            };
            list.Add(dopt);

            list.Add(Simple("wer", g2, "Rapports d'erreurs", "Rapports déjà envoyés ou en attente.", true, true,
                () => new[] { new FsSpec(PD(@"Microsoft\Windows\WER\ReportArchive")), new FsSpec(PD(@"Microsoft\Windows\WER\ReportQueue")), new FsSpec(L(@"Microsoft\Windows\WER\ReportArchive")), new FsSpec(L(@"Microsoft\Windows\WER\ReportQueue")) }));
            list.Add(Simple("dumps", g2, "Fichiers de vidage après plantage", "Inutiles si vous n'analysez pas un écran bleu en ce moment.", true, true,
                () => new[] { new FsSpec(L("CrashDumps")), new FsSpec(W("Minidump")), new FsSpec(W("LiveKernelReports")), new FsSpec(W("."), 0, "MEMORY.DMP", false) }));
            list.Add(Simple("logs", g2, "Anciens journaux système", "Journaux d'installation de plus de 7 jours.", true, true,
                () => new[] { new FsSpec(W(@"Logs\CBS"), 168), new FsSpec(W(@"Logs\DISM"), 168) }));

            var comp = new CleanCat
            {
                Id = "winsxs", Group = g3, Title = "Composants Windows remplacés", Desc = "Nettoyage officiel par DISM. Peut durer plusieurs minutes.", NeedsAdmin = true, SizeUnknown = true,
                Scanner = ct => 0,
                Cleaner = j =>
                {
                    var before = SysInfo.SystemDrive.AvailableFreeSpace;
                    Proc.Run("DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup", l => { if (!l.Contains("%")) j.Log(l); }, j.Token);
                    return Math.Max(0, SysInfo.SystemDrive.AvailableFreeSpace - before);
                }
            };
            list.Add(comp);

            var setup = Simple("setup", g3, "Restes d'installation de Windows", "Dossiers $WINDOWS.~BT, $WINDOWS.~WS et Windows10Upgrade. À éviter pendant une mise à niveau.", false, true,
                () => new[] { new FsSpec(SysRoot + "$WINDOWS.~BT"), new FsSpec(SysRoot + "$WINDOWS.~WS"), new FsSpec(SysRoot + "Windows10Upgrade") });
            list.Add(setup);

            var old = new CleanCat
            {
                Id = "winold", Group = g3, Title = "Ancienne installation (Windows.old)", Desc = "Plusieurs Go, mais vous ne pourrez plus revenir à la version précédente de Windows.", NeedsAdmin = true, Permanent = true,
                Scanner = ct => Fs.Measure(new[] { new FsSpec(SysRoot + "Windows.old") }, ct),
                Cleaner = j =>
                {
                    var before = SysInfo.SystemDrive.AvailableFreeSpace;
                    using (var r = Reg.Root(RegistryHive.LocalMachine))
                    using (var k = r.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Previous Installations", true))
                    {
                        if (k == null) { j.Log("Nettoyage de disque Windows indisponible."); return 0; }
                        k.SetValue("StateFlags4242", 2, RegistryValueKind.DWord);
                    }
                    Proc.Run("cleanmgr.exe", "/sagerun:4242", j.Log, j.Token);
                    return Math.Max(0, SysInfo.SystemDrive.AvailableFreeSpace - before);
                }
            };
            list.Add(old);

            var rb = new CleanCat
            {
                Id = "recycle", Group = g3, Title = "Corbeille", Desc = "Les fichiers seront supprimés définitivement.", Permanent = true,
                Scanner = ct =>
                {
                    var i = new RbInfo { Size = Marshal.SizeOf(typeof(RbInfo)) };
                    return SHQueryRecycleBin(null, ref i) == 0 ? i.Bytes : 0;
                },
                Cleaner = j =>
                {
                    var i = new RbInfo { Size = Marshal.SizeOf(typeof(RbInfo)) };
                    var before = SHQueryRecycleBin(null, ref i) == 0 ? i.Bytes : 0;
                    SHEmptyRecycleBin(IntPtr.Zero, null, 1 | 2 | 4);
                    return before;
                }
            };
            list.Add(rb);
            return list;
        }

        /// <summary>Estimation rapide pour le tableau de bord (sans les opérations longues).</summary>
        public static long QuickEstimate()
        {
            long t = 0;
            foreach (var c in BuildCatalog().Where(x => x.Selected && !x.SizeUnknown))
                try { t += c.Scanner(CancellationToken.None); } catch { }
            return t;
        }

        /// <summary>Entretien silencieux lancé par la tâche planifiée : uniquement ce qui est sans risque.</summary>
        public static long Maintenance(Job j)
        {
            long t = 0;
            foreach (var c in BuildCatalog().Where(x => new[] { "temp.user", "temp.win", "thumbs", "shader", "wer", "logs" }.Contains(x.Id) && x.Available))
                try { var n = c.Cleaner(j); t += n; j.Log(c.Title + " : " + Fmt.Size(n)); }
                catch (Exception e) { j.Log(c.Title + " : " + e.Message); }
            return t;
        }
    }

    public sealed class DirNode
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsFile { get; set; }
        public long Size { get; set; }
        public double Share { get; set; }
        public string SizeText { get { return Fmt.Size(Size); } }
        public List<DirNode> Kids = new List<DirNode>();
        public DirNode Parent;
    }

    /// <summary>Calcule la place occupée par chaque dossier d'un lecteur.</summary>
    public static class DiskAnalyzer
    {
        const long KeepFileAbove = 50L << 20;

        public static DirNode Scan(string root, CancellationToken ct, Action<string> onDir)
        {
            var n = new DirNode { Name = root, FullPath = root };
            Walk(n, ct, onDir);
            return n;
        }

        static void Walk(DirNode node, CancellationToken ct, Action<string> onDir)
        {
            ct.ThrowIfCancellationRequested();
            if (onDir != null) onDir(node.FullPath);
            long loose = 0; int looseCount = 0;
            try
            {
                foreach (var e in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos())
                {
                    try
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var f = e as FileInfo;
                        if (f != null)
                        {
                            if (f.Length >= KeepFileAbove) { node.Kids.Add(new DirNode { Name = f.Name, FullPath = f.FullName, IsFile = true, Size = f.Length, Parent = node }); node.Size += f.Length; }
                            else { loose += f.Length; looseCount++; node.Size += f.Length; }
                        }
                        else
                        {
                            var kid = new DirNode { Name = e.Name, FullPath = e.FullName, Parent = node };
                            Walk(kid, ct, onDir);
                            node.Kids.Add(kid);
                            node.Size += kid.Size;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (looseCount > 0) node.Kids.Add(new DirNode { Name = looseCount + " petits fichiers", FullPath = node.FullPath, IsFile = true, Size = loose, Parent = node });
            node.Kids.Sort((a, b) => b.Size.CompareTo(a.Size));
            if (node.Size > 0) foreach (var k in node.Kids) k.Share = k.Size * 100.0 / node.Size;
        }
    }
}
