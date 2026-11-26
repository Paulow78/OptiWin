using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    public sealed class AndroidThirdAppRow
    {
        public string Package { get; set; }
        public ICommand Stop { get; set; }
    }

    // ─────────────────────────── Téléviseur ───────────────────────────
    /// <summary>Connecter un téléviseur Android TV ou Google TV, alléger, accélérer, régler le DNS, installer un APK, piloter.</summary>
    public sealed class TvPage : Page
    {
        readonly TextBox host, pairHost, pairCode, planSearch, appsSearch;
        readonly ComboBox found;
        readonly TextBlock status, tvName, planTitle, planSub, disabledEmpty, animInfo, dnsInfo, appsSub, appsEmpty;
        readonly ItemsControl facts, planList, disabledList, thirdAppsList;
        readonly Button btnClean, btnRestore, btnAnim1, btnAnim05, btnAnim025, btnAnim0;
        readonly FrameworkElement deviceCard, workspace, pairBox, panelDebloat, panelTune, panelRemote;
        string adb, serial, currentAnim = "1", currentDns = "opportunistic";
        TvInfo tv;
        List<TvItem> plan = new List<TvItem>();
        List<string> thirdApps = new List<string>();

        public TvPage()
        {
            View = (FrameworkElement)Ui.Load("PageTv.xaml");
            host = F<TextBox>("TvHost"); pairHost = F<TextBox>("TvPairHost"); pairCode = F<TextBox>("TvPairCode");
            planSearch = F<TextBox>("TvPlanSearch"); appsSearch = F<TextBox>("TvAppsSearch");
            found = F<ComboBox>("TvFound");
            status = F<TextBlock>("TvStatus"); tvName = F<TextBlock>("TvName"); planTitle = F<TextBlock>("TvPlanTitle"); planSub = F<TextBlock>("TvPlanSub");
            disabledEmpty = F<TextBlock>("TvDisabledEmpty"); animInfo = F<TextBlock>("TvAnimInfo"); dnsInfo = F<TextBlock>("TvDnsInfo");
            appsSub = F<TextBlock>("TvAppsSub"); appsEmpty = F<TextBlock>("TvAppsEmpty");
            facts = F<ItemsControl>("TvFacts"); planList = F<ItemsControl>("TvPlan"); disabledList = F<ItemsControl>("TvDisabled"); thirdAppsList = F<ItemsControl>("TvThirdApps");
            btnClean = F<Button>("BtnTvClean"); btnRestore = F<Button>("BtnTvRestore");
            btnAnim1 = F<Button>("BtnTvAnim1"); btnAnim05 = F<Button>("BtnTvAnim05"); btnAnim025 = F<Button>("BtnTvAnim025"); btnAnim0 = F<Button>("BtnTvAnim0");
            deviceCard = F<FrameworkElement>("TvDeviceCard"); workspace = F<FrameworkElement>("TvWorkspace"); pairBox = F<FrameworkElement>("TvPairBox");
            panelDebloat = F<FrameworkElement>("PanelTvDebloat"); panelTune = F<FrameworkElement>("PanelTvTune"); panelRemote = F<FrameworkElement>("PanelTvRemote");

            F<Button>("BtnTvConnect").Click += delegate { Connect(); };
            F<Button>("BtnTvScan").Click += delegate { Scan(); };
            F<Button>("BtnTvPairToggle").Click += delegate { pairBox.Visibility = pairBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
            F<Button>("BtnTvPair").Click += delegate { Pair(); };
            F<Button>("BtnTvQuickOpt").Click += delegate { QuickOptimize(); };
            F<Button>("BtnTvAnalyze").Click += delegate { Reanalyze("Analyse du téléviseur", null); };
            F<Button>("BtnTvCaches").Click += delegate { Caches(); };
            F<Button>("BtnTvKill").Click += delegate { KillBackground(); };
            F<Button>("BtnTvShot").Click += delegate { CaptureScreen(); };
            F<Button>("BtnTvReboot").Click += delegate { Reboot(); };
            F<Button>("BtnTvRec").Click += delegate { foreach (var i in plan) i.Selected = i.Recommended; };
            F<Button>("BtnTvAll").Click += delegate { foreach (var i in plan) i.Selected = true; };
            F<Button>("BtnTvNone").Click += delegate { foreach (var i in plan) i.Selected = false; };
            btnClean.Click += delegate { Clean(); };
            btnRestore.Click += delegate { RestoreAll(); };

            btnAnim1.Click += delegate { SetAnim("1"); };
            btnAnim05.Click += delegate { SetAnim("0.5"); };
            btnAnim025.Click += delegate { SetAnim("0.25"); };
            btnAnim0.Click += delegate { SetAnim("0"); };

            F<Button>("BtnTvDnsAdguard").Click += delegate { SetDns("hostname", "dns.adguard-dns.com", "AdGuard (anti-pub)"); };
            F<Button>("BtnTvDnsCf").Click += delegate { SetDns("hostname", "one.one.one.one", "Cloudflare"); };
            F<Button>("BtnTvDnsQuad9").Click += delegate { SetDns("hostname", "dns.quad9.net", "Quad9"); };
            F<Button>("BtnTvDnsAuto").Click += delegate { SetDns("opportunistic", null, "Automatique"); };
            F<Button>("BtnTvDexOpt").Click += delegate { OptimizeApps(); };
            F<Button>("BtnTvApk").Click += delegate { InstallApk(); };

            F<Button>("BtnRemHome").Click += delegate { SendKey(3, "Accueil"); };
            F<Button>("BtnRemBack").Click += delegate { SendKey(4, "Retour"); };
            F<Button>("BtnRemOk").Click += delegate { SendKey(23, "OK"); };
            F<Button>("BtnRemPlay").Click += delegate { SendKey(85, "Lecture / Pause"); };
            F<Button>("BtnRemVolDown").Click += delegate { SendKey(25, "Volume −"); };
            F<Button>("BtnRemVolUp").Click += delegate { SendKey(24, "Volume +"); };
            F<Button>("BtnRemMute").Click += delegate { SendKey(164, "Muet"); };
            F<Button>("BtnRemSleep").Click += delegate { SendKey(26, "Veille"); };
            F<Button>("BtnTvAppsRefresh").Click += delegate { LoadThirdPartyApps(); };

            F<RadioButton>("TabTvDebloat").Checked += delegate { ShowTab(0); };
            F<RadioButton>("TabTvTune").Checked += delegate { ShowTab(1); };
            F<RadioButton>("TabTvRemote").Checked += delegate { ShowTab(2); if (Ready(false) && thirdApps.Count == 0) LoadThirdPartyApps(); };

            Ui.Debounce(planSearch, 120, FilterPlan);
            Ui.Debounce(appsSearch, 120, FilterThirdApps);
            found.SelectionChanged += delegate { var s = found.SelectedItem as string; if (s != null) host.Text = s; };
            host.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Connect(); } };
        }

        void ShowTab(int index)
        {
            panelDebloat.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            panelTune.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            panelRemote.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        public override bool OnFind()
        {
            if (workspace.Visibility == Visibility.Visible && panelDebloat.Visibility == Visibility.Visible) { planSearch.Focus(); planSearch.SelectAll(); return true; }
            host.Focus(); host.SelectAll(); return true;
        }
        public override void OnRefresh() { if (Ready(false)) Reanalyze("Analyse du téléviseur", null); }

        bool Ready(bool tell = true)
        {
            if (adb != null && serial != null && tv != null) return true;
            if (tell) Shell.I.Toast("Connectez d'abord un téléviseur.", "warn");
            return false;
        }

        async Task<bool> ConfirmAdbIfMissing()
        {
            if (Adb.Find() != null) return true;
            return await Shell.I.Confirm("Télécharger les outils Android de Google ?",
                "Pour dialoguer avec le téléviseur, OptiTech utilise adb, l'outil officiel de Google (Android SDK Platform-Tools, ~7 Mo). Il sera téléchargé en HTTPS directement depuis Google (dl.google.com) dans le dossier d'OptiTech.",
                "Télécharger", false);
        }

        async void Pair()
        {
            var h = (pairHost.Text ?? "").Trim();
            var c = (pairCode.Text ?? "").Trim();
            if (!Adb.IsValidHost(h) || !h.Contains(":")) { Shell.I.Toast("Indiquez l'IP et le port d'appairage (ex. 192.168.1.42:37421).", "warn"); return; }
            if (!Adb.IsValidPairCode(c)) { Shell.I.Toast("Saisissez le code à 6 chiffres affiché sur le téléviseur.", "warn"); return; }
            if (!await ConfirmAdbIfMissing()) return;
            var ok = await Shell.I.RunJob("Appairage sans fil", true, job =>
            {
                var exe = Adb.Ensure(job);
                Adb.Pair(exe, h, c, job);
                job.Summary = "Appairage réussi. Saisissez maintenant l'adresse IP:port de connexion (affichée dans Débogage sans fil), puis cliquez sur Connecter.";
            });
            if (ok) { pairCode.Text = ""; status.Text = "Appairage réussi : cliquez maintenant sur Connecter avec l'IP:port de connexion."; }
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
            if (!await ConfirmAdbIfMissing()) return;
            string exe = null, id = null, anim = "1", dns = "opportunistic"; TvInfo info = null; List<TvItem> items = null;
            var ok = await Shell.I.RunJob("Connexion au téléviseur", true, job =>
            {
                exe = Adb.Ensure(job);
                id = TvOptimizer.Connect(exe, h, job);
                job.Status("Lecture du téléviseur");
                info = TvOptimizer.ReadInfo(exe, id, job.Token);
                items = TvOptimizer.Analyze(exe, id, job.Token);
                anim = Adb.GetAnimScale(exe, id, job.Token);
                dns = Adb.GetPrivateDns(exe, id, job.Token);
                job.Progress(100);
                job.Summary = "Connecté à " + info.Name + ". " + (items.Count == 0 ? "Rien à couper d'après la liste d'OptiTech." : Fmt.N(items.Count, "élément") + " à examiner : rien n'a encore été modifié.");
            });
            if (!ok || info == null) { status.Text = "Connexion non établie. Le journal de l'opération en donne la raison."; return; }
            adb = exe; serial = id; tv = info; currentAnim = anim; currentDns = dns;
            thirdApps = new List<string>();
            status.Text = "Connecté à " + tv.Name + ".";
            ShowDevice();
            ShowPlan(items);
        }

        void ShowDevice()
        {
            tvName.Text = tv.Name.Length > 0 ? tv.Name : "Appareil Android";
            var f = new List<KeyValuePair<string, string>>();
            f.Add(new KeyValuePair<string, string>("Système", "Android " + tv.Android + (tv.Sdk > 0 ? " (API " + tv.Sdk + ")" : "")));
            if (tv.StorageTotal > 0) f.Add(new KeyValuePair<string, string>("Stockage", Fmt.Size(tv.StorageFree) + " libres sur " + Fmt.Size(tv.StorageTotal)));
            if (tv.RamTotal > 0) f.Add(new KeyValuePair<string, string>("Mémoire", (tv.RamFree >= 0 ? Fmt.Size(tv.RamFree) + " disponibles sur " : "") + Fmt.Size(tv.RamTotal)));
            f.Add(new KeyValuePair<string, string>("Liaison", serial));
            facts.ItemsSource = f;
            UpdateTuningStatus();
            deviceCard.Visibility = Visibility.Visible;
            workspace.Visibility = Visibility.Visible;
        }

        void UpdateTuningStatus()
        {
            var pri = (Style)Application.Current.Resources["BtnPrimary"];
            var bas = (Style)Application.Current.Resources["BtnBase"];
            btnAnim1.Style = currentAnim == "1" ? pri : bas;
            btnAnim05.Style = currentAnim == "0.5" ? pri : bas;
            btnAnim025.Style = currentAnim == "0.25" ? pri : bas;
            btnAnim0.Style = currentAnim == "0" ? pri : bas;
            animInfo.Text = "Actuel : " + (currentAnim == "1" ? "1× (origine)" : currentAnim == "0" ? "0× (instantané)" : currentAnim + "× (accéléré)") + ". Réversible.";
            dnsInfo.Text = "Actuel : " + (currentDns == "opportunistic" ? "Automatique" : currentDns == "off" ? "Désactivé" : currentDns)
                + (tv != null && tv.Sdk > 0 && tv.Sdk < 28 ? " (requiert Android 9+)" : "");
        }

        void ShowPlan(List<TvItem> items)
        {
            plan = items ?? new List<TvItem>();
            foreach (var i in plan) i.PropertyChanged += delegate { UpdatePlan(); };
            FilterPlan();
            UpdatePlan();
            ShowDisabled();
        }

        void FilterPlan()
        {
            var q = (planSearch.Text ?? "").Trim();
            var cmp = StringComparison.CurrentCultureIgnoreCase;
            var filtered = q.Length == 0 ? plan : plan.Where(i => i.Label.IndexOf(q, cmp) >= 0 || i.Package.IndexOf(q, cmp) >= 0 || (i.Note ?? "").IndexOf(q, cmp) >= 0 || i.Group.IndexOf(q, cmp) >= 0).ToList();
            var cv = new ListCollectionView(filtered);
            cv.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            planList.ItemsSource = cv;
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
            disabledEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            btnRestore.IsEnabled = list.Count > 0;
        }

        async void Reanalyze(string title, Action<Job> before)
        {
            if (!Ready()) return;
            TvInfo info = null; List<TvItem> items = null; string anim = currentAnim, dns = currentDns;
            var ok = await Shell.I.RunJob(title, true, job =>
            {
                if (before != null) before(job);
                var summary = job.Summary;
                job.Status("Lecture du téléviseur");
                info = TvOptimizer.ReadInfo(adb, serial, job.Token);
                items = TvOptimizer.Analyze(adb, serial, job.Token);
                anim = Adb.GetAnimScale(adb, serial, job.Token);
                dns = Adb.GetPrivateDns(adb, serial, job.Token);
                job.Progress(100);
                job.Summary = summary ?? (items.Count == 0 ? "Rien à couper d'après la liste d'OptiTech." : Fmt.N(items.Count, "élément") + " à examiner.");
            });
            if (!ok || info == null) return;
            info.Serial = tv.Serial; info.Brand = tv.Brand; info.Model = tv.Model;
            tv = info; currentAnim = anim; currentDns = dns;
            ShowDevice();
            ShowPlan(items);
        }

        async void QuickOptimize()
        {
            if (!Ready()) return;
            var rec = plan.Where(i => i.Recommended).ToList();
            var ok = await Shell.I.Confirm("Optimiser " + tv.Name + " en 1 clic ?",
                "Cette opération va :\n• Désactiver " + Fmt.N(rec.Count, "élément recommandé", "éléments recommandés") + " (publicités, télémétrie, démos)\n• Accélérer les animations à 0,5×\n• Vider tous les caches des applications\n• Fermer les processus en arrière-plan\n\nTout reste 100 % réversible depuis cette page.",
                "Optimiser maintenant", false);
            if (!ok) return;
            Reanalyze("Optimisation rapide du téléviseur", job =>
            {
                int disabled = 0;
                if (rec.Count > 0) disabled = TvOptimizer.Disable(adb, serial, tv, rec, job);
                job.Status("Accélération des animations (0,5×)");
                TvOptimizer.SetAnimationPreset(adb, serial, tv, "0.5", job);
                job.Status("Vidage des caches");
                var freed = TvOptimizer.TrimCaches(adb, serial, job);
                job.Status("Fermeture de l'arrière-plan");
                Adb.Shell(adb, serial, "am kill-all", job.Token);
                job.Summary = Fmt.N(disabled, "élément désactivé", "éléments désactivés") + ", animations à 0,5×" + (freed > 0 ? ", " + Fmt.Size(freed) + " libérés" : "") + ".";
            });
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

        async void SetAnim(string scale)
        {
            if (!Ready()) return;
            var ok = await Shell.I.RunJob("Vitesse des animations", false, job =>
            {
                TvOptimizer.SetAnimationPreset(adb, serial, tv, scale, job);
                job.Summary = scale == "1" ? "Animations d'origine rétablies." : "Animations réglées à " + scale + "×.";
            });
            if (ok) { currentAnim = scale; UpdateTuningStatus(); }
        }

        async void SetDns(string mode, string hostName, string label)
        {
            if (!Ready()) return;
            if (tv.Sdk > 0 && tv.Sdk < 28) { Shell.I.Toast("Le DNS privé chiffré requiert Android 9 (API 28) ou supérieur.", "warn"); return; }
            var ok = await Shell.I.RunJob("DNS privé : " + label, false, job =>
            {
                Adb.SetPrivateDns(adb, serial, "tv.dns." + tv.Id, "Téléviseur : " + tv.Name, mode, hostName, job);
                job.Summary = "DNS privé configuré sur " + label + ".";
            });
            if (ok) { currentDns = mode == "hostname" ? hostName : mode; UpdateTuningStatus(); }
        }

        void OptimizeApps()
        {
            if (!Ready()) return;
            Reanalyze("Optimisation ART / DEX du téléviseur", job =>
            {
                Adb.OptimizeApps(adb, serial, job);
                job.Summary = "Optimisation ART / DEX terminée.";
            });
        }

        async void InstallApk()
        {
            if (!Ready()) return;
            var path = Dialogs.PickOpen("Choisir une application Android (.apk)", "Application Android (*.apk)|*.apk");
            if (path == null) return;
            await Shell.I.RunJob("Installation de " + Path.GetFileName(path), true, job =>
            {
                Adb.InstallApk(adb, serial, path, job);
                Store.AddHistory("tv:" + tv.Id, "Téléviseur : " + tv.Name, "APK installé : " + Path.GetFileName(path));
                job.Summary = Path.GetFileName(path) + " installé sur " + tv.Name + ".";
            });
            if (panelRemote.Visibility == Visibility.Visible) LoadThirdPartyApps();
        }

        async void CaptureScreen()
        {
            if (!Ready()) return;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OptiTech-TV-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png");
            var ok = await Shell.I.RunJob("Capture d'écran du téléviseur", true, job =>
            {
                job.Status("Capture de l'écran en cours");
                Adb.CaptureScreenshot(adb, serial, path, job.Token);
                job.Summary = "Capture enregistrée : " + path;
            });
            if (ok && File.Exists(path)) try { Process.Start(path); } catch { }
        }

        void SendKey(int code, string label)
        {
            if (!Ready()) return;
            var exe = adb; var id = serial;
            Task.Factory.StartNew(() => Adb.SendKey(exe, id, code, CancellationToken.None))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) Shell.I.Toast("Échec de la commande « " + label + " ».", "warn");
                    else Shell.I.Toast("Touche envoyée : " + label, "info");
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void LoadThirdPartyApps()
        {
            if (!Ready(false)) return;
            var exe = adb; var id = serial;
            appsSub.Text = "Chargement de la liste des applications tierces…";
            Task.Factory.StartNew(() => Adb.ListThirdPartyApps(exe, id, CancellationToken.None))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) { appsSub.Text = "Lecture impossible."; return; }
                    thirdApps = t.Result;
                    appsSub.Text = Fmt.N(thirdApps.Count, "application tierce active", "applications tierces actives") + ".";
                    FilterThirdApps();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void FilterThirdApps()
        {
            var q = (appsSearch.Text ?? "").Trim();
            var cmp = StringComparison.CurrentCultureIgnoreCase;
            var shown = thirdApps.Where(p => q.Length == 0 || p.IndexOf(q, cmp) >= 0).Select(p =>
            {
                var pkg = p;
                return new AndroidThirdAppRow { Package = pkg, Stop = new RelayCommand(() => ForceStopApp(pkg)) };
            }).ToList();
            thirdAppsList.ItemsSource = shown;
            appsEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void ForceStopApp(string pkg)
        {
            if (!Ready()) return;
            var exe = adb; var id = serial;
            Task.Factory.StartNew(() => Adb.ForceStop(exe, id, pkg, CancellationToken.None))
                .ContinueWith(t => Shell.I.Toast(t.IsFaulted ? "Impossible d'arrêter " + pkg : "Arrêt forcé : " + pkg, t.IsFaulted ? "warn" : "ok"),
                    TaskScheduler.FromCurrentSynchronizationContext());
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
            deviceCard.Visibility = workspace.Visibility = Visibility.Collapsed;
            status.Text = "Le téléviseur redémarre. Reconnectez-le quand il est revenu.";
        }
    }
}
