using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace OptiWin
{
    /// <summary>Ce qu'on emporte d'un PC vers un Windows neuf. Fichier JSON lisible, sans donnée personnelle.</summary>
    public sealed class PcProfile
    {
        public int Format = 1;
        public string Created, Machine, Windows;
        public List<string> Tweaks = new List<string>();
        public List<string> StartupOff = new List<string>();
        public List<string> RemoveApps = new List<string>();
        public List<string> Software = new List<string>();
        public bool Maintenance;
        public string Summary
        {
            get
            {
                return Tweaks.Count + " réglages, " + Software.Count + " logiciels, " + StartupOff.Count + " programmes coupés au démarrage, " + RemoveApps.Count + " applications à écarter";
            }
        }
    }

    /// <summary>Une ligne du plan d'import : l'utilisateur voit et coche chaque changement.</summary>
    public sealed class PlanRow : Bindable
    {
        public string Group { get; set; }
        public string Label { get; set; }
        public string Note { get; set; }
        public bool Available { get; set; }
        bool sel;
        public bool Selected { get { return sel; } set { Set(ref sel, value); } }
        public Action<Job> Run;
    }

    public static class PcProfiles
    {
        const long MaxFileSize = 2L << 20;
        const int MaxEntries = 2000;
        // Un identifiant winget ne contient ni espace ni guillemet : un profil ne peut pas glisser d'option dans la commande.
        static readonly Regex WingetId = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]{0,127}$", RegexOptions.Compiled);
        // Composants système et bibliothèques : Windows ou les logiciels les réinstallent eux-mêmes.
        static readonly string[] SystemPrefixes = { "Microsoft.VCRedist", "Microsoft.VCLibs", "Microsoft.DotNet", "Microsoft.WindowsAppRuntime", "Microsoft.UI.Xaml", "Microsoft.Edge", "Microsoft.AppInstaller", "Microsoft.OneDrive", "Microsoft.Teams", "Microsoft.WindowsTerminal", "Microsoft.Office" };

        public static PcProfile Capture(Job job)
        {
            var p = new PcProfile { Created = DateTime.Now.ToString("o"), Machine = Environment.MachineName, Windows = Os.ProductName + " " + Os.DisplayVersion };

            job.Status("Lecture des réglages");
            job.Progress(5);
            foreach (var t in TweakCatalog.All)
            {
                if (!t.IsSupported) continue;
                t.RefreshState();
                if (t.IsApplied) p.Tweaks.Add(t.Id);
            }

            job.Status("Lecture des programmes au démarrage");
            job.Progress(20);
            p.StartupOff = StartupManager.List().Where(i => !i.IsOn).Select(i => i.Name).Distinct().ToList();

            job.Status("Lecture des applications");
            job.Progress(35);
            string err;
            var present = AppsCatalog.ListInstalled(out err);
            if (string.IsNullOrWhiteSpace(err))
                p.RemoveApps = AppsCatalog.AllPatterns().Where(x => !present.Any(a => a.Pattern == x)).ToList();
            else job.Log("Applications non lues : " + err.Trim());

            job.Status("Lecture des logiciels installés");
            job.Progress(55);
            if (AppsCatalog.WingetAvailable())
                p.Software = FilterSoftware(InstalledWinget(job));
            else job.Log("winget indisponible : la liste des logiciels ne sera pas incluse.");

            p.Maintenance = Tools.WeeklyInstalled();
            job.Progress(100);
            return p;
        }

        public static List<string> FilterSoftware(IEnumerable<string> ids)
        {
            return ids.Where(i => WingetId.IsMatch(i) && !SystemPrefixes.Any(s => i.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                      .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Identifiants winget des logiciels présents sur ce PC.</summary>
        public static List<string> InstalledWinget(Job job)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "optiwin-export-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                // Le code de retour n'est pas fiable : winget signale aussi les paquets absents de ses sources.
                Proc.Run("winget.exe", "export -o \"" + tmp + "\" --source winget --disable-interactivity", null, job.Token);
                return File.Exists(tmp) ? ParseWingetExport(File.ReadAllText(tmp, Encoding.UTF8)) : new List<string>();
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static List<string> ParseWingetExport(string json)
        {
            var res = new List<string>();
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("Sources")) return res;
            foreach (var s in (root["Sources"] as object[]) ?? new object[0])
            {
                var src = s as Dictionary<string, object>;
                if (src == null || !src.ContainsKey("Packages")) continue;
                foreach (var pk in (src["Packages"] as object[]) ?? new object[0])
                {
                    var d = pk as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("PackageIdentifier")) res.Add(Convert.ToString(d["PackageIdentifier"]));
                }
            }
            return res;
        }

        public static void Save(PcProfile p, string path)
        {
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(p), new UTF8Encoding(false));
        }

        /// <summary>Lit un profil. Il peut venir d'ailleurs : tout ce qui n'est pas reconnu est écarté.</summary>
        public static PcProfile Load(string path)
        {
            if (new FileInfo(path).Length > MaxFileSize) throw new InvalidDataException("Ce fichier est trop gros pour être un profil OptiWin.");
            PcProfile p;
            try { p = new JavaScriptSerializer().Deserialize<PcProfile>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { throw new InvalidDataException("Ce fichier n'est pas un profil OptiWin lisible.", e); }
            if (p == null || p.Format != 1) throw new InvalidDataException("Format de profil inconnu.");
            var known = new HashSet<string>(TweakCatalog.All.Select(t => t.Id));
            var patterns = new HashSet<string>(AppsCatalog.AllPatterns());
            p.Tweaks = (p.Tweaks ?? new List<string>()).Where(known.Contains).Distinct().Take(MaxEntries).ToList();
            p.RemoveApps = (p.RemoveApps ?? new List<string>()).Where(patterns.Contains).Distinct().Take(MaxEntries).ToList();
            p.Software = FilterSoftware(p.Software ?? new List<string>()).Take(MaxEntries).ToList();
            p.StartupOff = (p.StartupOff ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(MaxEntries).ToList();
            return p;
        }

        /// <summary>Compare le profil à ce PC : seul ce qui changerait vraiment est proposé.</summary>
        public static List<PlanRow> BuildPlan(PcProfile p, Job job)
        {
            var rows = new List<PlanRow>();

            job.Status("Comparaison des réglages");
            job.Progress(10);
            foreach (var id in p.Tweaks)
            {
                var t = TweakCatalog.ById(id);
                if (t == null || !t.IsSupported) continue;
                t.RefreshState();
                if (t.IsApplied) continue;
                var tweak = t;
                var advanced = t.Risk == Risk.Advanced;
                rows.Add(new PlanRow
                {
                    Group = "Réglages", Label = t.Title, Available = t.CanToggle,
                    // Ce qui réduit la sécurité ne s'applique jamais sans un geste explicite, même depuis un profil.
                    Selected = t.CanToggle && !advanced,
                    Note = advanced ? "Réduit la sécurité : à cocher vous-même." : t.Desc,
                    Run = j => TweakEngine.Apply(tweak)
                });
            }

            job.Status("Comparaison du démarrage");
            job.Progress(30);
            var off = new HashSet<string>(p.StartupOff, StringComparer.OrdinalIgnoreCase);
            foreach (var it in StartupManager.List().Where(i => i.IsOn && off.Contains(i.Name)))
            {
                var item = it;
                rows.Add(new PlanRow
                {
                    Group = "Démarrage", Label = "Couper " + it.Name, Note = it.Meta, Available = it.CanToggle, Selected = it.CanToggle,
                    Run = j => StartupManager.SetEnabled(item, false)
                });
            }

            job.Status("Comparaison des applications");
            job.Progress(50);
            string err;
            var remove = new HashSet<string>(p.RemoveApps);
            foreach (var a in AppsCatalog.ListInstalled(out err).Where(x => remove.Contains(x.Pattern)))
            {
                var app = a;
                rows.Add(new PlanRow
                {
                    Group = "Applications à retirer", Label = a.Label, Available = true, Selected = a.Recommended,
                    Note = "Absente de l'ancien PC" + (string.IsNullOrEmpty(a.Note) ? "" : " · " + a.Note),
                    Run = j => AppsCatalog.Remove(app, true, j)
                });
            }

            job.Status("Comparaison des logiciels");
            job.Progress(70);
            if (p.Software.Count > 0)
            {
                if (AppsCatalog.WingetAvailable())
                {
                    var here = new HashSet<string>(InstalledWinget(job), StringComparer.OrdinalIgnoreCase);
                    foreach (var id in p.Software.Where(i => !here.Contains(i)))
                    {
                        var soft = new SoftApp { Id = id, Label = id };
                        rows.Add(new PlanRow
                        {
                            Group = "Logiciels à installer", Label = id, Note = "Depuis la source winget", Available = true, Selected = true,
                            Run = j =>
                            {
                                var code = AppsCatalog.Install(soft, j);
                                // -1978335189 : déjà installé et à jour, ce n'est pas une erreur.
                                if (code != 0 && code != -1978335189) throw new InvalidOperationException("winget a renvoyé le code " + code);
                            }
                        });
                    }
                }
                else job.Log("winget indisponible : installez « Programme d'installation d'application » depuis le Store pour récupérer les logiciels.");
            }

            if (p.Maintenance && !Tools.WeeklyInstalled())
                rows.Add(new PlanRow
                {
                    Group = "Entretien", Label = "Activer l'entretien automatique", Note = "Nettoyage hebdomadaire des fichiers temporaires", Available = Os.IsAdmin, Selected = Os.IsAdmin,
                    Run = j => Tools.EnableWeekly(j)
                });

            job.Progress(100);
            return rows;
        }

        /// <summary>Applique les lignes cochées. Un échec n'arrête pas le reste : tout est résumé à la fin.</summary>
        public static void Apply(List<PlanRow> rows, bool restorePoint, Job job)
        {
            var chosen = rows.Where(r => r.Selected && r.Available).ToList();
            if (restorePoint && chosen.Any(r => r.Group != "Logiciels à installer"))
            {
                job.Status("Création d'un point de restauration");
                TweakEngine.CreateRestorePoint("Avant import d'un profil OptiWin", job.Log);
            }
            var failed = new List<string>();
            for (int i = 0; i < chosen.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                var r = chosen[i];
                job.Status(r.Label);
                job.Progress(100.0 * i / chosen.Count);
                try { r.Run(job); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { failed.Add(r.Label); job.Log("  Échec : " + e.Message); }
            }
            job.Progress(100);
            job.Summary = (chosen.Count - failed.Count) + " sur " + chosen.Count + " appliqués." + (failed.Count > 0 ? " Échec pour : " + string.Join(", ", failed) + "." : "");
            Store.AddHistory("profile", "Import d'un profil", job.Summary);
        }
    }
}
