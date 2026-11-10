using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace OptiWin
{
    public sealed class MetricSnapshot
    {
        public string When, Label;
        public double Cpu;
        public long Ram, Free;
        public int Processes, Services, Startup;
        public long BootMs = -1;
        public double UptimeMin;
        public override string ToString()
        {
            DateTime d;
            return Label + " — " + (DateTime.TryParse(When, out d) ? Fmt.DateTimeText(d) : When);
        }
    }

    public sealed class MetricRow
    {
        public string Name { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public string Delta { get; set; }
        public string Level { get; set; } // ok = mieux, warn = moins bien, info = stable
    }

    /// <summary>
    /// Mesures factuelles, pour juger d'un gain sans se fier à une impression.
    /// Un écart de moins de 3 % est présenté comme stable : c'est du bruit, pas un gain.
    /// </summary>
    public static class Metrics
    {
        const double Noise = 0.03;

        public static MetricSnapshot Capture(string label, CancellationToken ct)
        {
            var s = new MetricSnapshot { When = DateTime.Now.ToString("o"), Label = label, UptimeMin = SysInfo.Uptime.TotalMinutes };
            s.Cpu = IdleCpu(3000, ct);
            s.Ram = SysInfo.RamUsed();
            s.Processes = Process.GetProcesses().Length;
            s.Services = SysInfo.Wmi(@"root\cimv2", "SELECT Name FROM Win32_Service WHERE State='Running'").Count;
            try { s.Startup = StartupManager.List().Count(i => i.IsOn); } catch { }
            try { s.Free = SysInfo.SystemDrive.AvailableFreeSpace; } catch { }
            s.BootMs = LastBootMs();
            return s;
        }

        static double IdleCpu(int ms, CancellationToken ct)
        {
            long i1, t1, i2, t2;
            if (!SysInfo.RawTimes(out i1, out t1)) return 0;
            ct.WaitHandle.WaitOne(ms);
            ct.ThrowIfCancellationRequested();
            if (!SysInfo.RawTimes(out i2, out t2) || t2 <= t1) return 0;
            return Math.Max(0, Math.Min(100, 100.0 * ((t2 - t1) - (i2 - i1)) / (t2 - t1)));
        }

        /// <summary>Durée du dernier démarrage selon Windows, ou -1 (ce journal est réservé aux administrateurs).</summary>
        public static long LastBootMs()
        {
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true };
                using (var r = new EventLogReader(q))
                using (var e = r.ReadEvent())
                    return e == null ? -1 : ParseBootEvent(e.ToXml());
            }
            catch (Exception e) { Logger.Info("Temps de démarrage : " + e.Message); return -1; }
        }

        public static long ParseBootEvent(string xml)
        {
            var d = new XmlDocument { XmlResolver = null };
            d.LoadXml(xml);
            var ns = new XmlNamespaceManager(d.NameTable);
            ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
            var n = d.SelectSingleNode("//e:EventData/e:Data[@Name='BootTime']", ns);
            long v;
            return n != null && long.TryParse(n.InnerText, out v) ? v : -1;
        }

        static string Sec(long ms) { return ms < 0 ? "Admin requis" : Fmt.Num(ms / 1000.0, "0.0") + " s"; }

        public static List<MetricRow> Compare(MetricSnapshot a, MetricSnapshot b)
        {
            var r = new List<MetricRow>();
            Action<string, double, double, Func<double, string>, bool> add = (name, x, y, f, lowerBetter) =>
            {
                var level = "info"; var delta = "Stable";
                if (x < 0 || y < 0) delta = "—";
                else
                {
                    var diff = y - x;
                    var bas = Math.Max(Math.Abs(x), 1e-9);
                    if (Math.Abs(diff) / bas >= Noise)
                    {
                        delta = (diff > 0 ? "+" : "−") + f(Math.Abs(diff));
                        level = (diff < 0) == lowerBetter ? "ok" : "warn";
                    }
                }
                r.Add(new MetricRow { Name = name, Before = x < 0 ? "Admin requis" : f(x), After = y < 0 ? "Admin requis" : f(y), Delta = delta, Level = level });
            };
            add("Dernier démarrage de Windows", a.BootMs, b.BootMs, v => Sec((long)v), true);
            add("Mémoire utilisée", a.Ram, b.Ram, v => Fmt.Size((long)v), true);
            add("Processus", a.Processes, b.Processes, v => Fmt.Num(v, "0"), true);
            add("Services en cours", a.Services, b.Services, v => Fmt.Num(v, "0"), true);
            add("Programmes au démarrage", a.Startup, b.Startup, v => Fmt.Num(v, "0"), true);
            add("Activité du processeur au repos", a.Cpu, b.Cpu, v => Fmt.Num(v, "0.0") + " %", true);
            add("Espace libre", a.Free, b.Free, v => Fmt.Size((long)v), false);
            return r;
        }

        /// <summary>Signale ce qui fausserait la comparaison, plutôt que d'afficher un gain trompeur.</summary>
        public static string FairnessNote(MetricSnapshot a, MetricSnapshot b)
        {
            var n = new List<string>();
            if (a.UptimeMin < 5 || b.UptimeMin < 5) n.Add("Une mesure a été prise moins de 5 minutes après le démarrage : Windows travaille encore, les chiffres sont gonflés.");
            if (b.UptimeMin >= a.UptimeMin && b.BootMs == a.BootMs && a.BootMs >= 0) n.Add("Pas de redémarrage entre ces deux mesures : le temps de démarrage ne peut pas avoir changé, et beaucoup de réglages ne sont pas encore actifs.");
            else if (b.UptimeMin >= a.UptimeMin) n.Add("Le PC n'a pas redémarré entre ces deux mesures : beaucoup de réglages ne sont pas encore actifs.");
            if (n.Count == 0) n.Add("Mesures comparables. Pour rester juste, fermez les mêmes applications avant chaque mesure.");
            return string.Join(" ", n);
        }
    }

    public sealed class DiskFact
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    public sealed class DiskHealth
    {
        public string Name { get; set; }
        public string Sub { get; set; }
        public string Level { get; set; }
        public string Verdict { get; set; }
        public List<DiskFact> Facts { get; set; }
    }

    /// <summary>État réel des disques, à partir des compteurs que chaque disque rapporte à Windows.</summary>
    public static class DiskHealthReader
    {
        const string Script =
            "$l = @(Get-PhysicalDisk | ForEach-Object { $d=$_; $r=$null; try { $r = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch {};" +
            "[pscustomobject]@{Name=$d.FriendlyName;Media=[string]$d.MediaType;Bus=[string]$d.BusType;Size=$d.Size;Health=[string]$d.HealthStatus;HasCounters=($r -ne $null);" +
            "Temp=$r.Temperature;TempMax=$r.TemperatureMax;Wear=$r.Wear;Hours=$r.PowerOnHours;Starts=$r.StartStopCycleCount;ReadErr=$r.ReadErrorsUncorrected;WriteErr=$r.WriteErrorsUncorrected} });" +
            "ConvertTo-Json -Compress -InputObject $l";

        public static List<DiskHealth> Read(out string err)
        {
            var o = PS.Run(Script, out err);
            return Parse(o);
        }

        static long? N(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return null;
            try { return Convert.ToInt64(v); } catch { return null; }
        }
        static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : "";
        }

        public static List<DiskHealth> Parse(string json)
        {
            var res = new List<DiskHealth>();
            if (string.IsNullOrWhiteSpace(json)) return res;
            var root = new JavaScriptSerializer().DeserializeObject(json);
            var arr = root as object[] ?? new[] { root };
            foreach (var x in arr)
            {
                var d = x as Dictionary<string, object>;
                if (d != null) res.Add(Build(d));
            }
            return res;
        }

        static DiskHealth Build(Dictionary<string, object> d)
        {
            var media = S(d, "Media"); var bus = S(d, "Bus"); var healthText = S(d, "Health");
            var isHdd = media == "HDD";
            var type = isHdd ? "Disque dur" : bus == "NVMe" ? "SSD NVMe" : media == "SSD" ? "SSD" : "Type inconnu";
            var size = N(d, "Size");
            var facts = new List<DiskFact>();
            var issues = new List<string>();
            var level = "ok";
            Action<string, string> raise = (lvl, msg) => { issues.Add(msg); if (lvl == "bad" || level == "ok") level = lvl; };

            var healthy = healthText == "Healthy" || healthText == "0";
            facts.Add(new DiskFact { Key = "État selon Windows", Value = healthy ? "Sain" : healthText == "Warning" || healthText == "1" ? "Avertissement" : healthText == "Unhealthy" || healthText == "2" ? "Défaillant" : "Inconnu" });
            if (healthText == "Warning" || healthText == "1") raise("warn", "Windows signale un avertissement sur ce disque.");
            if (healthText == "Unhealthy" || healthText == "2") raise("bad", "Windows signale ce disque comme défaillant.");

            var hasCounters = d.ContainsKey("HasCounters") && d["HasCounters"] is bool && (bool)d["HasCounters"];
            var temp = N(d, "Temp"); var tempMax = N(d, "TempMax"); var wear = N(d, "Wear"); var hours = N(d, "Hours");
            var rerr = N(d, "ReadErr"); var werr = N(d, "WriteErr");

            if (temp.HasValue && temp.Value > 0)
            {
                facts.Add(new DiskFact { Key = "Température", Value = temp.Value + " °C" + (tempMax.HasValue && tempMax.Value > 0 ? " (limite " + tempMax.Value + " °C)" : "") });
                if (temp.Value >= (isHdd ? 55 : 70)) raise("warn", "Température élevée : vérifiez la ventilation.");
            }
            if (wear.HasValue && !isHdd)
            {
                facts.Add(new DiskFact { Key = "Usure", Value = wear.Value + " %" });
                if (wear.Value >= 90) raise("bad", "SSD en fin de vie : remplacez-le et sauvegardez vos fichiers.");
                else if (wear.Value >= 70) raise("warn", "SSD bien usé : prévoyez son remplacement.");
            }
            if (hours.HasValue && hours.Value > 0)
            {
                var years = hours.Value / 8760.0;
                facts.Add(new DiskFact { Key = "Temps de fonctionnement", Value = Fmt.Num(hours.Value, "N0") + " h" + (years >= 1 ? " (" + Fmt.Num(years, "0.0") + " ans)" : "") });
            }
            if ((rerr ?? 0) + (werr ?? 0) > 0)
            {
                facts.Add(new DiskFact { Key = "Erreurs non corrigées", Value = ((rerr ?? 0) + (werr ?? 0)).ToString() });
                raise("warn", "Le disque a rencontré des erreurs : sauvegardez vos fichiers importants.");
            }

            string verdict;
            if (issues.Count > 0) verdict = string.Join(" ", issues);
            else if (!hasCounters) verdict = Os.IsAdmin ? "Aucun problème signalé. Ce disque ne fournit pas de détails d'usure." : "Aucun problème signalé. Température et usure : mode administrateur requis.";
            else verdict = "Aucun problème détecté.";

            return new DiskHealth
            {
                Name = S(d, "Name"), Sub = type + (size.HasValue ? " · " + Fmt.Size(size.Value) : ""),
                Level = level, Verdict = verdict, Facts = facts
            };
        }
    }
}
