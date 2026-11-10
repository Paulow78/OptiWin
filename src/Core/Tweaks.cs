using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OptiWin
{
    public enum Risk { Safe, Moderate, Advanced }
    public enum Effect { Immediate, Explorer, Logoff, Reboot }
    [Flags] public enum Prof { None = 0, OldPc = 1, Fresh = 2, Gaming = 4, Laptop = 8, Privacy = 16 }

    /// <summary>
    /// Opération élémentaire et réversible. Avant d'appliquer, l'état réel est capturé :
    /// l'annulation restaure donc ce qui existait vraiment, pas une valeur supposée.
    /// </summary>
    public abstract class TweakOp
    {
        public virtual bool Exists { get { return true; } }
        public virtual bool NeedsAdmin { get { return true; } }
        /// <summary>Un échec ne bloque pas le réglage (ex. tâche planifiée protégée).</summary>
        public virtual bool Optional { get { return false; } }
        public abstract bool IsApplied();
        public abstract void Capture(Dictionary<string, string> d);
        public abstract void Apply();
        /// <param name="d">État capturé, ou null pour revenir au défaut de Windows.</param>
        public abstract void Restore(Dictionary<string, string> d);
        public abstract string Describe();
    }

    public sealed class RegOp : TweakOp
    {
        readonly RegistryHive hive; readonly string key, name; readonly RegistryValueKind kind; readonly object val, def;
        string pruneUpTo;

        public RegOp(RegistryHive hive, string key, string name, RegistryValueKind kind, object val, object def)
        { this.hive = hive; this.key = key; this.name = name; this.kind = kind; this.val = val; this.def = def; }

        /// <summary>Au retour au défaut, supprime aussi les clés devenues vides jusqu'à cet ancêtre.</summary>
        public RegOp PruneUpTo(string ancestor) { pruneUpTo = ancestor; return this; }

        public override bool NeedsAdmin { get { return hive != RegistryHive.CurrentUser; } }

        public override bool IsApplied()
        {
            var cur = Reg.Get(hive, key, name);
            if (val == null) return cur == null;
            return Same(cur, val);
        }

        static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == b;
            var ba = a as byte[]; var bb = b as byte[];
            if (ba != null && bb != null) return ba.SequenceEqual(bb);
            if (a is int && b is int) return (int)a == (int)b;
            return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        public override void Capture(Dictionary<string, string> d)
        {
            using (var r = Reg.Root(hive))
            {
                using (var k = r.OpenSubKey(key))
                {
                    var cur = k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    d["existed"] = cur == null ? "0" : "1";
                    if (cur != null)
                    {
                        var kd = k.GetValueKind(name);
                        d["kind"] = kd.ToString();
                        d["data"] = Encode(cur, kd);
                    }
                }
                // Ancêtre existant le plus proche : pour ne supprimer que ce que l'on a créé.
                var a = key;
                while (!string.IsNullOrEmpty(a))
                {
                    using (var k = r.OpenSubKey(a)) if (k != null) break;
                    a = Parent(a);
                }
                d["anc"] = a ?? "";
            }
        }

        public override void Apply()
        {
            using (var r = Reg.Root(hive))
            using (var k = r.CreateSubKey(key))
            {
                if (val == null) k.DeleteValue(name, false);
                else k.SetValue(name, val, kind);
            }
        }

        public override void Restore(Dictionary<string, string> d)
        {
            using (var r = Reg.Root(hive))
            {
                if (d == null)
                {
                    if (def == null)
                    {
                        using (var k = r.OpenSubKey(key, true)) if (k != null) k.DeleteValue(name, false);
                        if (pruneUpTo != null) Prune(r, key, pruneUpTo);
                    }
                    else using (var k = r.CreateSubKey(key)) k.SetValue(name, def, kind);
                    return;
                }
                if (d.ContainsKey("existed") && d["existed"] == "1")
                {
                    var kd = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), d["kind"]);
                    using (var k = r.CreateSubKey(key)) k.SetValue(name, Decode(d["data"], kd), kd);
                }
                else
                {
                    using (var k = r.OpenSubKey(key, true)) if (k != null) k.DeleteValue(name, false);
                    string anc;
                    if (d.TryGetValue("anc", out anc)) Prune(r, key, anc);
                }
            }
        }

        static void Prune(RegistryKey root, string from, string upTo)
        {
            var cur = from;
            while (!string.IsNullOrEmpty(cur) && !string.Equals(cur, upTo, StringComparison.OrdinalIgnoreCase))
            {
                using (var k = root.OpenSubKey(cur))
                    if (k != null && (k.SubKeyCount > 0 || k.ValueCount > 0)) return;
                try { root.DeleteSubKey(cur, false); } catch { return; }
                cur = Parent(cur);
            }
        }

        static string Parent(string p)
        {
            var i = p.LastIndexOf('\\');
            return i < 0 ? "" : p.Substring(0, i);
        }

        static string Encode(object v, RegistryValueKind k)
        {
            switch (k)
            {
                case RegistryValueKind.DWord: return ((int)v).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.QWord: return ((long)v).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.Binary: return BitConverter.ToString((byte[])v);
                case RegistryValueKind.MultiString: return string.Join("\0", (string[])v);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        static object Decode(string s, RegistryValueKind k)
        {
            switch (k)
            {
                case RegistryValueKind.DWord: return int.Parse(s, CultureInfo.InvariantCulture);
                case RegistryValueKind.QWord: return long.Parse(s, CultureInfo.InvariantCulture);
                case RegistryValueKind.Binary:
                    return s.Length == 0 ? new byte[0] : s.Split('-').Select(x => Convert.ToByte(x, 16)).ToArray();
                case RegistryValueKind.MultiString: return s.Split('\0');
                default: return s;
            }
        }

        public override string Describe()
        {
            var h = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            var v = val == null ? "(supprimé)" : (val is byte[] ? BitConverter.ToString((byte[])val) : Convert.ToString(val, CultureInfo.InvariantCulture));
            return h + "\\" + key + " › " + (name.Length == 0 ? "(défaut)" : name) + " = " + v;
        }
    }

    public sealed class ServiceOp : TweakOp
    {
        readonly string svc; readonly int target, def;
        /// <param name="target">2 = auto, 3 = manuel, 4 = désactivé.</param>
        public ServiceOp(string svc, int target, int def) { this.svc = svc; this.target = target; this.def = def; }
        string KeyPath { get { return @"SYSTEM\CurrentControlSet\Services\" + svc; } }
        public override bool Exists { get { return Reg.KeyExists(RegistryHive.LocalMachine, KeyPath); } }
        int Current { get { return Reg.GetInt(RegistryHive.LocalMachine, KeyPath, "Start") ?? -1; } }
        public override bool IsApplied() { return !Exists || Current == target; }
        public override void Capture(Dictionary<string, string> d) { d["start"] = Current.ToString(CultureInfo.InvariantCulture); }
        static string Word(int s) { return s == 2 ? "auto" : s == 4 ? "disabled" : "demand"; }

        public override void Apply()
        {
            Proc.RunChecked("sc.exe", "config \"" + svc + "\" start= " + Word(target), null);
            if (target == 4) Proc.Run("sc.exe", "stop \"" + svc + "\"", null);
        }

        public override void Restore(Dictionary<string, string> d)
        {
            int start = def;
            string s;
            if (d != null && d.TryGetValue("start", out s)) { int v; if (int.TryParse(s, out v) && v >= 2 && v <= 4) start = v; }
            Proc.RunChecked("sc.exe", "config \"" + svc + "\" start= " + Word(start), null);
            if (start == 2) Proc.Run("sc.exe", "start \"" + svc + "\"", null);
        }
        public override string Describe() { return "Service " + svc + " → " + (target == 4 ? "désactivé" : target == 3 ? "manuel" : "automatique"); }
    }

    public sealed class TaskOp : TweakOp
    {
        readonly string path;
        public TaskOp(string path) { this.path = path; }
        public override bool Optional { get { return true; } }

        dynamic Open()
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            svc.Connect();
            var i = path.LastIndexOf('\\');
            dynamic folder = svc.GetFolder(path.Substring(0, i));
            return folder.GetTask(path.Substring(i + 1));
        }
        public override bool Exists { get { try { return Open() != null; } catch { return false; } } }
        public override bool IsApplied() { try { return !(bool)Open().Enabled; } catch { return true; } }
        public override void Capture(Dictionary<string, string> d) { d["enabled"] = IsApplied() ? "0" : "1"; }
        public override void Apply() { dynamic t = Open(); t.Enabled = false; }
        public override void Restore(Dictionary<string, string> d)
        {
            string e;
            var enable = d == null || !d.TryGetValue("enabled", out e) || e == "1";
            dynamic t = Open();
            t.Enabled = enable;
        }
        public override string Describe() { return "Tâche planifiée " + path + " → désactivée"; }
    }

    /// <summary>Opération par commande, pour ce que le registre seul ne peut pas faire.</summary>
    public sealed class CmdOp : TweakOp
    {
        readonly string exe, onArgs, offArgs, text; readonly Func<bool> check;
        public CmdOp(string exe, string onArgs, string offArgs, Func<bool> check, string text)
        { this.exe = exe; this.onArgs = onArgs; this.offArgs = offArgs; this.check = check; this.text = text; }
        public override bool IsApplied() { return check(); }
        public override void Capture(Dictionary<string, string> d) { }
        public override void Apply() { Proc.RunChecked(exe, onArgs, null); }
        public override void Restore(Dictionary<string, string> d) { Proc.RunChecked(exe, offArgs, null); }
        public override string Describe() { return text; }
    }

    public sealed class PowerPlanOp : TweakOp
    {
        const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        static string Active
        {
            get { return (Reg.GetStr(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme") ?? "").ToLowerInvariant(); }
        }
        public override bool IsApplied() { return Active == High; }
        public override void Capture(Dictionary<string, string> d) { d["guid"] = Active; }
        public override void Apply()
        {
            if (Proc.Run("powercfg.exe", "/setactive " + High, null) != 0)
            {
                // Absent sur les PC à veille moderne : on le recrée depuis le modèle de Windows.
                Proc.Run("powercfg.exe", "/duplicatescheme " + High + " " + High, null);
                Proc.RunChecked("powercfg.exe", "/setactive " + High, null);
            }
        }
        public override void Restore(Dictionary<string, string> d)
        {
            string g;
            var target = d != null && d.TryGetValue("guid", out g) && !string.IsNullOrEmpty(g) ? g : Balanced;
            Proc.RunChecked("powercfg.exe", "/setactive " + target, null);
        }
        public override string Describe() { return "Plan d'alimentation → Performances élevées"; }
    }

    public sealed class Tweak : Bindable
    {
        public string Id, Cat, Title, Desc, Warning;
        public Risk Risk;
        public Effect Effect;
        public Prof Profiles;
        public int MinBuild;
        public int MaxBuild = int.MaxValue;
        public Func<bool> When;
        public List<TweakOp> Ops = new List<TweakOp>();

        /// <summary>Page des Paramètres où faire le réglage à la main, si Windows interdit aux programmes de le modifier.</summary>
        public string SettingsUri;
        public Tweak Manual(string uri) { SettingsUri = uri; return this; }
        /// <summary>Windows a déjà refusé ce réglage sur ce PC : il n'est plus proposé par les profils.</summary>
        public bool Blocked { get { return Store.Setting("blocked." + Id) != null; } }

        public Tweak MinB(int b) { MinBuild = b; return this; }
        public Tweak MaxB(int b) { MaxBuild = b; return this; }
        public Tweak If(Func<bool> f) { When = f; return this; }
        public Tweak Warn(string w) { Warning = w; return this; }

        // Propriétés lues par l'interface
        public string TitleText { get { return Title; } }
        public string DescText { get { return Desc; } }
        public string RiskKey { get { return Risk.ToString(); } }
        public string RiskLabel { get { return Risk == Risk.Safe ? "Sûr" : Risk == Risk.Moderate ? "Modéré" : "Avancé"; } }
        public string EffectLabel
        {
            get { return Effect == Effect.Reboot ? "Redémarrage" : Effect == Effect.Logoff ? "Reconnexion" : Effect == Effect.Explorer ? "Explorateur" : ""; }
        }
        public bool HasEffect { get { return Effect != Effect.Immediate; } }
        public string Details { get { return string.Join("\n", Ops.Select(o => o.Describe())); } }
        public bool NeedsAdmin { get { return Ops.Any(o => o.NeedsAdmin); } }
        bool? supported;
        public bool IsSupported
        {
            get
            {
                if (!supported.HasValue) supported = Os.Build >= MinBuild && Os.Build <= MaxBuild && Ops.Any(o => o.Exists);
                return supported.Value;
            }
        }
        public bool CanToggle { get { return !Busy && (!NeedsAdmin || Os.IsAdmin); } }
        public string ToolTipText { get { return Details + (NeedsAdmin && !Os.IsAdmin ? "\n\nDroits administrateur requis." : ""); } }
        /// <summary>Le réglage convient-il à ce PC lorsqu'il est proposé par un profil ?</summary>
        public bool FitsThisPc { get { return When == null || When(); } }

        bool applied, busy;
        public bool Busy { get { return busy; } set { if (Set(ref busy, value)) Raise("CanToggle"); } }
        public bool IsApplied { get { return applied; } }

        /// <summary>Lié à l'interrupteur : le basculer lance l'application ou l'annulation.</summary>
        public bool IsOn
        {
            get { return applied; }
            set { if (value != applied && ToggleRequested != null) ToggleRequested(this, value); }
        }
        public static Action<Tweak, bool> ToggleRequested;

        public void RefreshState()
        {
            bool v;
            try
            {
                var live = Ops.Where(o => o.Exists).ToList();
                var core = live.Where(o => !o.Optional).ToList();
                if (core.Count == 0) core = live;
                v = core.Count > 0 && core.All(o => o.IsApplied());
            }
            catch (Exception e) { Logger.Error("État de " + Id, e); v = false; }
            applied = v;
            Raise("IsOn");
            Raise("IsApplied");
        }
    }

    public sealed class SettingProtectedException : InvalidOperationException
    {
        public readonly Tweak Tweak;
        public SettingProtectedException(Tweak t, Exception inner)
            : base("Windows protège ce réglage : seul Windows peut le modifier." + (t.SettingsUri != null ? " Faites-le dans les Paramètres." : ""), inner)
        { Tweak = t; }
    }

    public static class TweakEngine
    {
        public static void Apply(Tweak t)
        {
            if (t.NeedsAdmin && !Os.IsAdmin) throw new InvalidOperationException("Droits administrateur requis.");
            var snaps = new List<Dictionary<string, string>>();
            var done = new List<KeyValuePair<TweakOp, Dictionary<string, string>>>();
            var warnings = new List<string>();
            try
            {
                foreach (var op in t.Ops)
                {
                    var d = new Dictionary<string, string>();
                    snaps.Add(d);
                    if (!op.Exists) { d["skip"] = "1"; continue; }
                    op.Capture(d);
                    try
                    {
                        op.Apply();
                        done.Add(new KeyValuePair<TweakOp, Dictionary<string, string>>(op, d));
                    }
                    catch (Exception e)
                    {
                        if (!op.Optional)
                        {
                            // Une écriture refusée peut laisser une clé vide, créée juste avant : on la retire.
                            if (op is RegOp)
                                try { op.Restore(d); } catch (Exception r) { Logger.Error("Nettoyage après échec " + t.Id, r); }
                            if (e is UnauthorizedAccessException && op is RegOp)
                            {
                                // Refus malgré les droits administrateur : Windows protège ce réglage (pilote UCPD).
                                // On le note pour ne plus le proposer, sans chercher à contourner la protection.
                                Store.SetSetting("blocked." + t.Id, DateTime.Now.ToString("o"));
                                throw new SettingProtectedException(t, e);
                            }
                            throw;
                        }
                        d["skip"] = "1";
                        warnings.Add(op.Describe() + " : " + e.Message);
                    }
                }
            }
            catch
            {
                // Réglage tout-ou-rien : on défait ce qui vient d'être fait.
                foreach (var x in Enumerable.Reverse(done))
                    try { x.Key.Restore(x.Value); } catch (Exception e) { Logger.Error("Retour arrière " + t.Id, e); }
                t.RefreshState();
                throw;
            }
            // Ne jamais écraser la sauvegarde d'origine lors d'une seconde application.
            if (!Store.HasBackup(t.Id)) Store.SetBackup(t.Id, snaps);
            if (t.Blocked) Store.SetSetting("blocked." + t.Id, null);
            Store.AddHistory(t.Id, t.Title, "Appliqué");
            foreach (var w in warnings) Logger.Error("Partiel " + t.Id + " : " + w);
            Native.BroadcastSettingChange();
            t.RefreshState();
        }

        public static void Revert(Tweak t)
        {
            if (t.NeedsAdmin && !Os.IsAdmin) throw new InvalidOperationException("Droits administrateur requis.");
            var snaps = Store.GetBackup(t.Id);
            for (int i = t.Ops.Count - 1; i >= 0; i--)
            {
                var op = t.Ops[i];
                if (!op.Exists) continue;
                var d = snaps != null && i < snaps.Count ? snaps[i] : null;
                if (d != null && d.ContainsKey("skip")) continue;
                op.Restore(d);
            }
            Store.RemoveBackup(t.Id);
            Store.AddHistory(t.Id, t.Title, "Annulé");
            Native.BroadcastSettingChange();
            t.RefreshState();
        }

        public static void RestartExplorer()
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
                try { p.Kill(); } catch { }
            // Windows relance normalement l'Explorateur seul ; sinon on le fait après un court délai.
            Thread.Sleep(2500);
            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                try { System.Diagnostics.Process.Start("explorer.exe"); } catch { }
        }

        /// <summary>Crée un point de restauration système. Renvoie vrai si Windows l'a accepté.</summary>
        public static bool CreateRestorePoint(string description, Action<string> log)
        {
            if (!Os.IsAdmin) { if (log != null) log("Point de restauration : droits administrateur requis."); return false; }
            const string K = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
            var prev = Reg.GetInt(RegistryHive.LocalMachine, K, "SystemRestorePointCreationFrequency");
            try
            {
                // Windows limite à un point par 24 h : on lève la limite le temps de l'appel, puis on la remet.
                using (var r = Reg.Root(RegistryHive.LocalMachine))
                using (var k = r.CreateSubKey(K)) k.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                string err;
                var script = "Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue;" +
                             "Checkpoint-Computer -Description " + PS.Q(description) + " -RestorePointType MODIFY_SETTINGS -ErrorAction Stop;'OK'";
                var o = PS.Run(script, out err);
                var ok = o.Contains("OK");
                if (log != null) log(ok ? "Point de restauration créé." : "Point de restauration impossible : " + err.Trim());
                return ok;
            }
            catch (Exception e)
            {
                if (log != null) log("Point de restauration impossible : " + e.Message);
                return false;
            }
            finally
            {
                try
                {
                    using (var r = Reg.Root(RegistryHive.LocalMachine))
                    using (var k = r.CreateSubKey(K))
                    {
                        if (prev.HasValue) k.SetValue("SystemRestorePointCreationFrequency", prev.Value, RegistryValueKind.DWord);
                        else k.DeleteValue("SystemRestorePointCreationFrequency", false);
                    }
                }
                catch { }
            }
        }
    }
}
