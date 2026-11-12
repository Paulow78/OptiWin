using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Input;
using Microsoft.Win32;

namespace OptiWin
{
    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        readonly Action<object> run;
        public RelayCommand(Action<object> run) { this.run = run; }
        public RelayCommand(Action run) { this.run = delegate { run(); }; }
        public bool CanExecute(object p) { return true; }
        public void Execute(object p) { run(p); }
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    public static class Fmt
    {
        static readonly CultureInfo Fr = new CultureInfo("fr-FR");
        public static string Size(long b)
        {
            if (b < 0) return "—";
            if (b >= 1L << 40) return (b / (double)(1L << 40)).ToString("0.0", Fr) + " To";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0", Fr) + " Go";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0", Fr) + " Mo";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0", Fr) + " Ko";
            return b + " o";
        }
        public static string Span(TimeSpan t)
        {
            if (t.TotalDays >= 1) return (int)t.TotalDays + " j " + t.Hours + " h";
            if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + " min " + t.Seconds + " s";
            return Math.Max(0, (int)t.TotalSeconds) + " s";
        }
        public static string Num(double v, string f) { return v.ToString(f, Fr); }
        public static string Date(DateTime d) { return d.ToString("d MMM yyyy", Fr); }
        public static string DateTimeText(DateTime d) { return d.ToString("d MMM yyyy à HH:mm", Fr); }
        /// <summary>« 1 réglage », « 3 réglages » : l'accord du pluriel en un seul endroit.</summary>
        public static string N(int n, string singular, string plural = null)
        {
            return n + " " + (n > 1 ? (plural ?? singular + "s") : singular);
        }
        /// <summary>Même chose avec un adjectif accordé : « 2 réglages appliqués ».</summary>
        public static string N(int n, string singular, string adjective, string adjectivePlural)
        {
            return N(n, singular) + " " + (n > 1 ? adjectivePlural : adjective);
        }
        /// <summary>Date ISO (telle que stockée dans l'historique) rendue lisible.</summary>
        public static string When(string iso)
        {
            DateTime d;
            return DateTime.TryParse(iso, out d) ? DateTimeText(d) : (iso ?? "");
        }
    }

    public static class Paths
    {
        public static readonly string Data;
        public static readonly string Logs;
        // Constructeur explicite indispensable : sans lui, le CLR peut initialiser ces champs par anticipation,
        // avant que Main ait isolé les auto-tests avec OPTIWIN_DATA.
        static Paths()
        {
            Data = Ensure(Environment.GetEnvironmentVariable("OPTIWIN_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiWin"));
            Logs = Ensure(Path.Combine(Data, "logs"));
        }
        public static string Exe { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        static string Ensure(string p) { try { Directory.CreateDirectory(p); } catch { } return p; }
    }

    public static class Logger
    {
        static readonly object gate = new object();
        public static void Info(string m) { Write("INFO", m); }
        public static void Error(string m) { Write("ERR ", m); }
        public static void Error(string m, Exception e) { Write("ERR ", m + " : " + e); }
        static void Write(string lvl, string m)
        {
            try
            {
                lock (gate) File.AppendAllText(Path.Combine(Paths.Logs, "optiwin-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    DateTime.Now.ToString("HH:mm:ss") + " " + lvl + " " + m + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    public static class Reg
    {
        public static RegistryKey Root(RegistryHive h) { return RegistryKey.OpenBaseKey(h, RegistryView.Registry64); }
        public static object Get(RegistryHive h, string key, string name)
        {
            try
            {
                using (var r = Root(h))
                using (var k = r.OpenSubKey(key))
                    return k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch { return null; }
        }
        public static int? GetInt(RegistryHive h, string key, string name)
        {
            var v = Get(h, key, name);
            if (v is int) return (int)v;
            return null;
        }
        public static string GetStr(RegistryHive h, string key, string name)
        {
            var v = Get(h, key, name);
            return v == null ? null : Convert.ToString(v);
        }
        public static bool KeyExists(RegistryHive h, string key)
        {
            try { using (var r = Root(h)) using (var k = r.OpenSubKey(key)) return k != null; }
            catch { return false; }
        }
    }

    public static class Os
    {
        const string CV = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        public static readonly int Build;
        public static readonly int Ubr;
        public static readonly string ProductName;
        public static readonly string DisplayVersion;
        public static readonly DateTime? InstallDate;
        public static bool IsAdmin { get; private set; }
        /// <summary>Captures d'écran de présentation : l'interface telle qu'elle apparaît en administrateur, son mode normal.</summary>
        public static void ShowcaseAdmin() { IsAdmin = true; }
        public static readonly bool IsInsider;

        static Os()
        {
            int b; int.TryParse(Reg.GetStr(RegistryHive.LocalMachine, CV, "CurrentBuildNumber") ?? "0", out b);
            Build = b;
            Ubr = Reg.GetInt(RegistryHive.LocalMachine, CV, "UBR") ?? 0;
            var name = Reg.GetStr(RegistryHive.LocalMachine, CV, "ProductName") ?? "Windows";
            // Windows 11 s'annonce encore « Windows 10 » dans le registre.
            if (Build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            ProductName = name;
            DisplayVersion = Reg.GetStr(RegistryHive.LocalMachine, CV, "DisplayVersion") ?? Reg.GetStr(RegistryHive.LocalMachine, CV, "ReleaseId") ?? "";
            var inst = Reg.GetInt(RegistryHive.LocalMachine, CV, "InstallDate");
            if (inst.HasValue) InstallDate = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((uint)inst.Value).ToLocalTime();
            try { IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            IsInsider = !string.IsNullOrEmpty(Reg.GetStr(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsSelfHost\Applicability", "BranchName"));
        }
    }

    public static class Proc
    {
        [DllImport("kernel32")] static extern uint GetOEMCP();
        static readonly Encoding Oem = GetOem();
        static Encoding GetOem() { try { return Encoding.GetEncoding((int)GetOEMCP()); } catch { return Encoding.Default; } }

        /// <summary>Lance un programme, transmet chaque ligne de sortie, renvoie le code de retour.</summary>
        public static int Run(string exe, string args, Action<string> onLine, CancellationToken ct = default(CancellationToken), string cwd = null)
        {
            var psi = new ProcessStartInfo(Resolve(exe), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = cwd ?? Environment.SystemDirectory
            };
            onLine = onLine ?? delegate { };
            using (var p = Process.Start(psi))
            {
                try { p.StandardInput.Close(); } catch { }
                var a = Pump(p.StandardOutput.BaseStream, onLine);
                var b = Pump(p.StandardError.BaseStream, onLine);
                using (ct.Register(delegate { KillTree(p); }))
                {
                    p.WaitForExit();
                    a.Join(); b.Join();
                }
                ct.ThrowIfCancellationRequested();
                return p.ExitCode;
            }
        }

        /// <summary>
        /// Chemin absolu des outils système. Sinon Windows chercherait d'abord à côté d'OptiWin.exe :
        /// un faux sc.exe posé dans le même dossier serait alors exécuté avec les droits administrateur.
        /// </summary>
        static string Resolve(string exe)
        {
            if (Path.IsPathRooted(exe)) return exe;
            var sys = Path.Combine(Environment.SystemDirectory, exe);
            if (File.Exists(sys)) return sys;
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps", exe);
            return File.Exists(apps) ? apps : exe;
        }

        /// <summary>Même chose, mais lève une exception si le code de retour n'est pas 0.</summary>
        public static void RunChecked(string exe, string args, Action<string> onLine, CancellationToken ct = default(CancellationToken))
        {
            var last = new List<string>();
            var code = Run(exe, args, delegate(string l) { if (onLine != null) onLine(l); lock (last) { last.Add(l); if (last.Count > 6) last.RemoveAt(0); } }, ct);
            if (code != 0) throw new InvalidOperationException(Path.GetFileName(exe) + " a échoué (code " + code + ") : " + string.Join(" ", last));
        }

        public static string Capture(string exe, string args)
        {
            var sb = new StringBuilder();
            try { Run(exe, args, delegate(string l) { lock (sb) sb.AppendLine(l); }); } catch { }
            return sb.ToString();
        }

        static void KillTree(Process p)
        {
            try { Process.Start(new ProcessStartInfo(Resolve("taskkill.exe"), "/T /F /PID " + p.Id) { UseShellExecute = false, CreateNoWindow = true }); }
            catch { try { p.Kill(); } catch { } }
        }

        static Thread Pump(Stream s, Action<string> onLine)
        {
            var t = new Thread(delegate()
            {
                var buf = new byte[8192];
                var sb = new StringBuilder();
                Decoder dec = null;
                try
                {
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        // sfc.exe écrit en UTF-16 lorsqu'il est redirigé, les autres en page OEM.
                        if (dec == null) dec = LooksUtf16(buf, n) ? Encoding.Unicode.GetDecoder() : Oem.GetDecoder();
                        var chars = new char[dec.GetCharCount(buf, 0, n)];
                        dec.GetChars(buf, 0, n, chars, 0);
                        foreach (var c in chars)
                        {
                            if (c == '\r' || c == '\n')
                            {
                                if (sb.Length > 0) { onLine(sb.ToString()); sb.Length = 0; }
                            }
                            else if (c != '\0' && c != '﻿') sb.Append(c);
                        }
                    }
                }
                catch { }
                if (sb.Length > 0) onLine(sb.ToString());
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        static bool LooksUtf16(byte[] b, int n)
        {
            int z = 0;
            for (int i = 1; i < n; i += 2) if (b[i] == 0) z++;
            return n > 8 && z > n / 4;
        }
    }

    /// <summary>Exécute un court script PowerShell (encodé, jamais écrit sur le disque).</summary>
    public static class PS
    {
        public static string Run(string script, out string err, CancellationToken ct = default(CancellationToken))
        {
            var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand " + b64)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                try { p.StandardInput.Close(); } catch { }
                string o = null, e = null;
                var t1 = new Thread(delegate() { o = p.StandardOutput.ReadToEnd(); });
                var t2 = new Thread(delegate() { e = p.StandardError.ReadToEnd(); });
                t1.Start(); t2.Start();
                using (ct.Register(delegate { try { p.Kill(); } catch { } }))
                {
                    p.WaitForExit();
                    t1.Join(); t2.Join();
                }
                err = e ?? "";
                return (o ?? "").Trim();
            }
        }
        public static string Run(string script)
        {
            string e;
            return Run(script, out e);
        }
        /// <summary>Échappe une chaîne pour un littéral PowerShell entre apostrophes.</summary>
        public static string Q(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }
    }

    /// <summary>Tâche longue : statut, progression, journal, annulation. Indépendant de l'interface.</summary>
    public sealed class Job
    {
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        public CancellationToken Token { get { return cts.Token; } }
        public event Action<string> OnLog;
        public event Action<string> OnStatus;
        public event Action<double> OnProgress;
        public event Action<string> OnDetail;
        public string Summary;
        public void Log(string s) { Logger.Info(s); var h = OnLog; if (h != null) h(s); }
        public void Status(string s) { Log("› " + s); var h = OnStatus; if (h != null) h(s); Status2(""); }
        /// <summary>Détail discret à côté de la progression (vitesse, temps restant).</summary>
        public void Status2(string s) { var h = OnDetail; if (h != null) h(s); }
        /// <param name="pct">0 à 100, ou -1 pour une progression indéterminée.</param>
        public void Progress(double pct) { var h = OnProgress; if (h != null) h(pct); }
        public void Cancel() { cts.Cancel(); }
    }

    public sealed class HistoryEntry
    {
        public string When;
        public string Id;
        public string Title;
        public string Action;
    }

    /// <summary>État persistant : sauvegardes avant modification, historique, préférences.</summary>
    public sealed class AppState
    {
        public Dictionary<string, List<Dictionary<string, string>>> Backups = new Dictionary<string, List<Dictionary<string, string>>>();
        public List<HistoryEntry> History = new List<HistoryEntry>();
        public Dictionary<string, string> Settings = new Dictionary<string, string>();
        public List<MetricSnapshot> Metrics = new List<MetricSnapshot>();
    }

    public static class Store
    {
        static readonly object gate = new object();
        static readonly string FilePath = Path.Combine(Paths.Data, "state.json");
        static AppState s;

        static AppState St
        {
            get
            {
                if (s != null) return s;
                try
                {
                    if (File.Exists(FilePath))
                        s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<AppState>(File.ReadAllText(FilePath, Encoding.UTF8));
                }
                catch (Exception e)
                {
                    Logger.Error("state.json illisible", e);
                    // Ne jamais écraser une sauvegarde abîmée : elle reste récupérable à la main.
                    try { File.Copy(FilePath, FilePath + ".corrompu-" + DateTime.Now.Ticks, true); } catch { }
                }
                if (s == null) s = new AppState();
                if (s.Backups == null) s.Backups = new Dictionary<string, List<Dictionary<string, string>>>();
                if (s.History == null) s.History = new List<HistoryEntry>();
                if (s.Settings == null) s.Settings = new Dictionary<string, string>();
                if (s.Metrics == null) s.Metrics = new List<MetricSnapshot>();
                return s;
            }
        }

        static void Save()
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(St), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch (Exception e) { Logger.Error("Sauvegarde de state.json impossible", e); }
        }

        public static bool HasBackup(string id) { lock (gate) return St.Backups.ContainsKey(id); }
        public static List<Dictionary<string, string>> GetBackup(string id)
        {
            lock (gate)
            {
                List<Dictionary<string, string>> v;
                return St.Backups.TryGetValue(id, out v) ? v : null;
            }
        }
        public static void SetBackup(string id, List<Dictionary<string, string>> v) { lock (gate) { St.Backups[id] = v; Save(); } }
        public static void RemoveBackup(string id) { lock (gate) { if (St.Backups.Remove(id)) Save(); } }
        public static string[] BackupIds() { lock (gate) return St.Backups.Keys.ToArray(); }

        public static void AddHistory(string id, string title, string action)
        {
            lock (gate)
            {
                St.History.Add(new HistoryEntry { When = DateTime.Now.ToString("o"), Id = id, Title = title, Action = action });
                if (St.History.Count > 1000) St.History.RemoveRange(0, St.History.Count - 1000);
                Save();
            }
        }
        public static List<HistoryEntry> History() { lock (gate) return St.History.ToList(); }

        public static void AddMetric(MetricSnapshot m)
        {
            lock (gate)
            {
                St.Metrics.Add(m);
                if (St.Metrics.Count > 50) St.Metrics.RemoveRange(0, St.Metrics.Count - 50);
                Save();
            }
        }
        public static List<MetricSnapshot> Metrics() { lock (gate) return St.Metrics.ToList(); }
        public static void ClearMetrics() { lock (gate) { St.Metrics.Clear(); Save(); } }

        public static string Setting(string k, string def = null)
        {
            lock (gate)
            {
                string v;
                return St.Settings.TryGetValue(k, out v) ? v : def;
            }
        }
        public static void SetSetting(string k, string v) { lock (gate) { if (v == null) St.Settings.Remove(k); else St.Settings[k] = v; Save(); } }
    }

    public static class Native
    {
        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr res);
        /// <summary>Prévient le shell et les applications qu'un réglage a changé.</summary>
        public static void BroadcastSettingChange()
        {
            IntPtr r;
            try { SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 500, out r); } catch { }
            try { SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "Environment", 2, 500, out r); } catch { }
        }

        [DllImport("kernel32")] public static extern ulong GetTickCount64();
    }
}
