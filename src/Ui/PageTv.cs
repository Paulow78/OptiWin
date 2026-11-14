using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace OptiTech
{
    public sealed class TvDisabledRow
    {
        public string Package { get; set; }
        public string Label { get; set; }
        public ICommand Enable { get; set; }
    }

    // ─────────────────────────── Téléviseur ───────────────────────────
    /// <summary>Connecter un téléviseur Android TV ou Google TV, voir ce qui peut être coupé, nettoyer, rétablir.</summary>
    public sealed class TvPage : Page
    {
        readonly TextBox host;
        readonly ComboBox found;
        readonly TextBlock status, tvName, planTitle, planSub, disabledEmpty;
        readonly ItemsControl facts, planList, disabledList;
        readonly Button btnClean, btnRestore;
        readonly ToggleButton anim;
        readonly FrameworkElement deviceCard, planCard, disabledCard;
        string adb, serial;
        TvInfo tv;
        List<TvItem> plan = new List<TvItem>();

        public TvPage()
        {
            View = (FrameworkElement)Ui.Load("PageTv.xaml");
            host = F<TextBox>("TvHost"); found = F<ComboBox>("TvFound");
            status = F<TextBlock>("TvStatus"); tvName = F<TextBlock>("TvName"); planTitle = F<TextBlock>("TvPlanTitle"); planSub = F<TextBlock>("TvPlanSub");
            disabledEmpty = F<TextBlock>("TvDisabledEmpty");
            facts = F<ItemsControl>("TvFacts"); planList = F<ItemsControl>("TvPlan"); disabledList = F<ItemsControl>("TvDisabled");
            btnClean = F<Button>("BtnTvClean"); btnRestore = F<Button>("BtnTvRestore");
            anim = F<ToggleButton>("TvAnim");
            deviceCard = F<FrameworkElement>("TvDeviceCard"); planCard = F<FrameworkElement>("TvPlanCard"); disabledCard = F<FrameworkElement>("TvDisabledCard");

            F<Button>("BtnTvConnect").Click += delegate { Connect(); };
            F<Button>("BtnTvScan").Click += delegate { Scan(); };
            F<Button>("BtnTvAnalyze").Click += delegate { Reanalyze("Analyse du téléviseur", null); };
            F<Button>("BtnTvCaches").Click += delegate { Caches(); };
            F<Button>("BtnTvKill").Click += delegate { KillBackground(); };
            F<Button>("BtnTvReboot").Click += delegate { Reboot(); };
            F<Button>("BtnTvAll").Click += delegate { foreach (var i in plan) i.Selected = true; };
            F<Button>("BtnTvNone").Click += delegate { foreach (var i in plan) i.Selected = false; };
            btnClean.Click += delegate { Clean(); };
            btnRestore.Click += delegate { RestoreAll(); };
            anim.Click += delegate { ToggleAnimations(); };
            found.SelectionChanged += delegate { var s = found.SelectedItem as string; if (s != null) host.Text = s; };
            host.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Connect(); } };
        }

        public override bool OnFind() { host.Focus(); host.SelectAll(); return true; }
        public override void OnRefresh() { if (Ready(false)) Reanalyze("Analyse du téléviseur", null); }

        bool Ready(bool tell = true)
        {
            if (adb != null && serial != null && tv != null) return true;
            if (tell) Shell.I.Toast("Connectez d'abord un téléviseur.", "warn");
            return false;
        }

        async void Scan()
        {
            List<string> list = null;
            var ok = await Shell.I.RunJob("Recherche des téléviseurs sur le réseau", true, job =>
            {
                job.Status("Recherche des appareils dont le débogage réseau est ouvert");
                list = Adb.Scan(job);
                job.Summary = list.Count == 0 ? "Aucun appareil trouvé. Activez le débogage réseau sur le téléviseur, puis relancez la recherche."
                    : Fmt.N(list.Count, "appareil trouvé", "appareils trouvés") + " : " + string.Join(", ", list) + ".";
            });
            if (!ok || list == null) return;
            found.ItemsSource = list;
            found.Visibility = list.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count >= 1) { host.Text = list[0]; if (list.Count > 1) found.SelectedIndex = 0; }
            status.Text = list.Count == 0 ? "Aucun appareil trouvé sur le réseau." : list.Count == 1 ? "Un appareil trouvé : cliquez sur Connecter." : "Plusieurs appareils trouvés : choisissez le vôtre, puis connectez.";
        }

        async void Connect()
        {
            var h = (host.Text ?? "").Trim();
            if (h.Length > 0 && !Adb.IsValidHost(h)) { Shell.I.Toast("Adresse invalide : attendu une adresse IP, par exemple 192.168.1.42.", "warn"); return; }
            if (Adb.Find() == null)
            {
                var agreed = await Shell.I.Confirm("Récupérer les outils Android ?",
                    "Pour dialoguer avec le téléviseur, OptiTech utilise adb, l'outil officiel de Google (Android SDK Platform-Tools). Il sera récupéré via winget, depuis sa source officielle. Le récupérer vaut acceptation de sa licence.",
                    "Récupérer", false);
                if (!agreed) return;
            }
            string exe = null, id = null; TvInfo info = null; List<TvItem> items = null; bool fast = false;
            var ok = await Shell.I.RunJob("Connexion au téléviseur", true, job =>
            {
                exe = Adb.Ensure(job);
                id = TvOptimizer.Connect(exe, h, job);
                job.Status("Lecture du téléviseur");
                info = TvOptimizer.ReadInfo(exe, id, job.Token);
                items = TvOptimizer.Analyze(exe, id, job.Token);
                fast = TvOptimizer.AnimationsFast(exe, id, job.Token);
                job.Progress(100);
                job.Summary = "Connecté à " + info.Name + ". " + (items.Count == 0 ? "Rien à couper d'après la liste d'OptiTech." : Fmt.N(items.Count, "élément") + " à examiner : rien n'a encore été modifié.");
            });
            if (!ok || info == null) { status.Text = "Connexion non établie. Le journal de l'opération en donne la raison."; return; }
            adb = exe; serial = id; tv = info;
            status.Text = "Connecté à " + tv.Name + ".";
            ShowDevice(fast);
            ShowPlan(items);
        }

        void ShowDevice(bool fast)
        {
            tvName.Text = tv.Name.Length > 0 ? tv.Name : "Appareil Android";
            var f = new List<KeyValuePair<string, string>>();
            f.Add(new KeyValuePair<string, string>("Système", "Android " + tv.Android + (tv.Sdk > 0 ? " (API " + tv.Sdk + ")" : "")));
            if (tv.StorageTotal > 0) f.Add(new KeyValuePair<string, string>("Stockage", Fmt.Size(tv.StorageFree) + " libres sur " + Fmt.Size(tv.StorageTotal)));
            if (tv.RamTotal > 0) f.Add(new KeyValuePair<string, string>("Mémoire", (tv.RamFree >= 0 ? Fmt.Size(tv.RamFree) + " disponibles sur " : "") + Fmt.Size(tv.RamTotal)));
            f.Add(new KeyValuePair<string, string>("Liaison", serial));
            facts.ItemsSource = f;
            anim.IsChecked = fast;
            deviceCard.Visibility = Visibility.Visible;
        }

        void ShowPlan(List<TvItem> items)
        {
            plan = items ?? new List<TvItem>();
            var cv = new ListCollectionView(plan);
            cv.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            planList.ItemsSource = cv;
            foreach (var i in plan) i.PropertyChanged += delegate { UpdatePlan(); };
            planCard.Visibility = Visibility.Visible;
            UpdatePlan();
            ShowDisabled();
        }

        void UpdatePlan()
        {
            var n = plan.Count(i => i.Selected);
            planTitle.Text = plan.Count == 0 ? "Rien à couper sur ce téléviseur" : Fmt.N(plan.Count, "élément") + " à examiner";
            planSub.Text = plan.Count == 0 ? "Aucune application de la liste d'OptiTech n'y est active."
                : Fmt.N(n, "élément", "sélectionné", "sélectionnés") + ". Le lanceur, la diffusion, les mises à jour et les entrées HDMI ne sont jamais proposés.";
            btnClean.IsEnabled = n > 0;
        }

        void ShowDisabled()
        {
            var list = tv == null ? new List<string>() : TvOptimizer.DisabledByUs(tv.Id);
            disabledList.ItemsSource = list.Select(p =>
            {
                var pkg = p;
                return new TvDisabledRow { Package = pkg, Label = TvOptimizer.LabelOf(pkg), Enable = new RelayCommand(() => EnableOne(pkg)) };
            }).ToList();
            disabledCard.Visibility = tv == null ? Visibility.Collapsed : Visibility.Visible;
            disabledEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            btnRestore.IsEnabled = list.Count > 0;
        }

        /// <summary>Relit le téléviseur après une action, dans la même tâche que cette action.</summary>
        async void Reanalyze(string title, Action<Job> before)
        {
            if (!Ready()) return;
            TvInfo info = null; List<TvItem> items = null; bool fast = false;
            var ok = await Shell.I.RunJob(title, true, job =>
            {
                if (before != null) before(job);
                var summary = job.Summary;
                job.Status("Lecture du téléviseur");
                info = TvOptimizer.ReadInfo(adb, serial, job.Token);
                items = TvOptimizer.Analyze(adb, serial, job.Token);
                fast = TvOptimizer.AnimationsFast(adb, serial, job.Token);
                job.Progress(100);
                job.Summary = summary ?? (items.Count == 0 ? "Rien à couper d'après la liste d'OptiTech." : Fmt.N(items.Count, "élément") + " à examiner.");
            });
            if (!ok || info == null) return;
            // Le numéro de série peut manquer d'une lecture à l'autre : on garde l'identité établie à la connexion.
            info.Serial = tv.Serial; info.Brand = tv.Brand; info.Model = tv.Model;
            tv = info;
            ShowDevice(fast);
            ShowPlan(items);
        }

        async void Clean()
        {
            if (!Ready()) return;
            var sel = plan.Where(i => i.Selected).ToList();
            if (sel.Count == 0) return;
            var ok = await Shell.I.Confirm("Désactiver " + Fmt.N(sel.Count, "élément") + " sur " + tv.Name + " ?",
                string.Join(", ", sel.Select(i => i.Label)) + ".\n\nRien n'est désinstallé : chaque élément se réactive depuis cette page, un par un ou d'un coup.", "Désactiver", false);
            if (!ok) return;
            var ramBefore = tv.RamFree;
            Reanalyze("Nettoyage du téléviseur", job =>
            {
                var n = TvOptimizer.Disable(adb, serial, tv, sel, job);
                var ramAfter = TvOptimizer.ParseMeminfo(Adb.Shell(adb, serial, "cat /proc/meminfo", job.Token), "MemAvailable");
                job.Summary = Fmt.N(n, "élément désactivé", "éléments désactivés") + " sur " + sel.Count + "."
                    + (ramBefore > 0 && ramAfter > 0 ? " Mémoire disponible : " + Fmt.Size(ramBefore) + " avant, " + Fmt.Size(ramAfter) + " après." : "")
                    + " Redémarrez le téléviseur pour finir.";
            });
        }

        void EnableOne(string pkg)
        {
            if (!Ready()) return;
            Reanalyze("Réactivation", job =>
            {
                var ok = TvOptimizer.Enable(adb, serial, tv, pkg, job);
                job.Summary = ok ? TvOptimizer.LabelOf(pkg) + " est réactivé." : "Le téléviseur a refusé : voir le journal.";
            });
        }

        async void RestoreAll()
        {
            if (!Ready()) return;
            var ok = await Shell.I.Confirm("Tout rétablir sur " + tv.Name + " ?", "Toutes les applications désactivées par OptiTech sur ce téléviseur seront réactivées.", "Tout rétablir", false);
            if (!ok) return;
            Reanalyze("Rétablissement du téléviseur", job =>
            {
                var n = TvOptimizer.RestoreAll(adb, serial, tv, job);
                job.Summary = Fmt.N(n, "application réactivée", "applications réactivées") + ".";
            });
        }

        void Caches()
        {
            if (!Ready()) return;
            Reanalyze("Vidage des caches du téléviseur", job =>
            {
                job.Status("Vidage des caches des applications");
                var freed = TvOptimizer.TrimCaches(adb, serial, job);
                job.Summary = freed < 0 ? "Caches vidés." : freed == 0 ? "Caches vidés : il n'y avait presque rien à récupérer." : Fmt.Size(freed) + " libérés sur le téléviseur.";
            });
        }

        void KillBackground()
        {
            if (!Ready()) return;
            Reanalyze("Fermeture des applications en arrière-plan", job =>
            {
                Adb.Shell(adb, serial, "am kill-all", job.Token);
                job.Summary = "Applications en arrière-plan fermées. Android relancera celles dont il a besoin.";
            });
        }

        async void ToggleAnimations()
        {
            var want = anim.IsChecked == true;
            if (!Ready()) { anim.IsChecked = false; return; }
            var ok = await Shell.I.RunJob(want ? "Animations accélérées" : "Animations d'origine", false, job =>
            {
                TvOptimizer.SetAnimations(adb, serial, tv, want, job);
                job.Summary = want ? "Animations deux fois plus courtes : l'interface répond plus vite." : "Durées d'animation d'origine rétablies.";
            });
            if (!ok) anim.IsChecked = !want;
        }

        async void Reboot()
        {
            if (!Ready()) return;
            if (!await Shell.I.Confirm("Redémarrer " + tv.Name + " ?", "Le téléviseur redémarre tout de suite. Il faudra le reconnecter ensuite.", "Redémarrer", false)) return;
            var exe = adb; var id = serial;
            await Shell.I.RunJob("Redémarrage du téléviseur", false, job =>
            {
                Adb.Out(exe, "-s " + id + " reboot", job.Token);
                job.Summary = "Redémarrage demandé. Reconnectez le téléviseur quand il est revenu.";
            });
            adb = null; serial = null; tv = null;
            deviceCard.Visibility = planCard.Visibility = disabledCard.Visibility = Visibility.Collapsed;
            status.Text = "Le téléviseur redémarre. Reconnectez-le quand il est revenu.";
        }
    }
}
