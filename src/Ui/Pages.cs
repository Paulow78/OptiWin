using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace OptiWin
{
    // ─────────────────────────── Accueil ───────────────────────────
    public sealed class HomePage : Page
    {
        readonly RingGauge ring; readonly Sparkline cpu, ram;
        readonly TextBlock scoreText, verdict, verdictSub, cpuVal, ramVal, diskVal, diskSub, diskLabel, uptimeVal, procSub, startupSub, profName, profReason;
        readonly ProgressBar diskBar;
        readonly ItemsControl insights, facts;
        readonly DispatcherTimer timer;
        Prof recommended = Prof.Fresh;
        int tick;

        public HomePage()
        {
            View = (FrameworkElement)Ui.Load("PageHome.xaml");
            ring = F<RingGauge>("Ring"); cpu = F<Sparkline>("CpuSpark"); ram = F<Sparkline>("RamSpark");
            scoreText = F<TextBlock>("ScoreText"); verdict = F<TextBlock>("Verdict"); verdictSub = F<TextBlock>("VerdictSub");
            cpuVal = F<TextBlock>("CpuVal"); ramVal = F<TextBlock>("RamVal"); diskVal = F<TextBlock>("DiskVal"); diskSub = F<TextBlock>("DiskSub"); diskLabel = F<TextBlock>("DiskLabel");
            uptimeVal = F<TextBlock>("UptimeVal"); procSub = F<TextBlock>("ProcSub"); startupSub = F<TextBlock>("StartupSub");
            profName = F<TextBlock>("ProfileName"); profReason = F<TextBlock>("ProfileReason");
            diskBar = F<ProgressBar>("DiskBar"); insights = F<ItemsControl>("Insights"); facts = F<ItemsControl>("Facts");
            F<Button>("BtnOptimize").Click += delegate { Shell.I.ShowProfile(recommended); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { Sample(); };
            SysInfo.CpuLoad();
            RefreshAnalysis();
        }

        public override void OnShow() { timer.Start(); Sample(); }
        public override void OnHide() { timer.Stop(); }

        void Sample()
        {
            var c = SysInfo.CpuLoad();
            var r = SysInfo.RamLoad();
            cpu.Push(c); ram.Push(r);
            cpuVal.Text = Fmt.Num(c, "0") + " %";
            ramVal.Text = Fmt.Size(SysInfo.RamUsed());
            if (tick++ % 5 == 0)
            {
                uptimeVal.Text = Fmt.Span(SysInfo.Uptime);
                procSub.Text = Process.GetProcesses().Length + " processus";
                try
                {
                    var d = SysInfo.SystemDrive;
                    diskLabel.Text = "Disque système (" + d.Name.TrimEnd('\\') + ")";
                    diskVal.Text = Fmt.Size(d.AvailableFreeSpace) + " libres";
                    diskBar.Value = 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize;
                    diskSub.Text = "sur " + Fmt.Size(d.TotalSize);
                }
                catch { }
            }
        }

        /// <summary>Recalcule le score en arrière-plan : l'interface reste réactive même sur un vieux disque.</summary>
        public void RefreshAnalysis()
        {
            verdictSub.Text = "Mesure de l'espace récupérable et des réglages…";
            Func<string, ICommand> go = page => new RelayCommand(() => Shell.I.Go(page));
            Task.Factory.StartNew(() =>
            {
                var s = SysInfo.Current;
                var prof = SysInfo.RecommendedProfile(s);
                var clean = Cleaner.QuickEstimate();
                var startup = StartupManager.List().Count(i => i.IsOn);
                var plan = TweakCatalog.ForProfile(prof).ToList();
                foreach (var t in plan) t.RefreshState();
                var missing = plan.Count(t => !t.IsApplied);
                var health = Health.Compute(s, clean, startup, missing, go);
                return new { s, prof, health, startup };
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) { Logger.Error("Analyse", t.Exception); verdict.Text = "Analyse incomplète"; verdictSub.Text = t.Exception.GetBaseException().Message; return; }
                var r = t.Result;
                recommended = r.prof;
                scoreText.Text = r.health.Score.ToString();
                ring.AnimateTo(r.health.Score);
                ring.SetResourceReference(RingGauge.StrokeProperty, r.health.Score >= 65 ? "Accent" : r.health.Score >= 45 ? "Warn" : "Danger");
                verdict.Text = r.health.Verdict;
                var n = r.health.Insights.Count(i => i.Level != "ok");
                verdictSub.Text = n == 0 ? "Aucune action nécessaire pour le moment." : n + " point" + (n > 1 ? "s" : "") + " à améliorer, détaillé" + (n > 1 ? "s" : "") + " ci-dessous.";
                insights.ItemsSource = r.health.Insights;
                profName.Text = "Profil conseillé : " + SysInfo.ProfileName(r.prof);
                profReason.Text = SysInfo.ProfileReason(r.s, r.prof);
                startupSub.Text = r.startup + (r.startup > 1 ? " programmes" : " programme") + " au démarrage";
                var f = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string,string>("Système", r.s.OsName + " " + r.s.OsVersion + " (build " + r.s.Build + ")"),
                    new KeyValuePair<string,string>("Processeur", r.s.Cpu + " — " + r.s.Cores + " cœurs, " + r.s.Threads + " threads"),
                    new KeyValuePair<string,string>("Mémoire", Fmt.Size(r.s.Ram)),
                    new KeyValuePair<string,string>("Carte graphique", r.s.Gpu),
                    new KeyValuePair<string,string>("Disque système", r.s.DiskModel + " (" + r.s.DiskType + "), santé : " + r.s.DiskHealth),
                    new KeyValuePair<string,string>("Démarrage", (r.s.IsUefi ? "UEFI" : "BIOS") + ", Secure Boot " + (r.s.SecureBoot ? "actif" : "inactif")),
                };
                if (r.s.InstalledOn.HasValue) f.Add(new KeyValuePair<string, string>("Windows installé le", Fmt.Date(r.s.InstalledOn.Value)));
                facts.ItemsSource = f;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    // ─────────────────────────── Optimiser ───────────────────────────
    public sealed class OptimizePage : Page
    {
        readonly ItemsControl list; readonly TextBox search; readonly StackPanel cats;
        readonly Border pendingBar; readonly TextBlock pendingText; readonly Button btnExplorer;
        string cat = "Tous";
        bool needExplorer, needLogoff, needReboot;

        public OptimizePage()
        {
            View = (FrameworkElement)Ui.Load("PageOptimize.xaml");
            list = F<ItemsControl>("List"); search = F<TextBox>("Search"); cats = F<StackPanel>("Cats");
            pendingBar = F<Border>("PendingBar"); pendingText = F<TextBlock>("PendingText"); btnExplorer = F<Button>("BtnRestartExplorer");

            F<Button>("ProfOld").Click += delegate { Shell.I.ShowProfile(Prof.OldPc); };
            F<Button>("ProfFresh").Click += delegate { Shell.I.ShowProfile(Prof.Fresh); };
            F<Button>("ProfGame").Click += delegate { Shell.I.ShowProfile(Prof.Gaming); };
            F<Button>("ProfLaptop").Click += delegate { Shell.I.ShowProfile(Prof.Laptop); };
            F<Button>("ProfPrivacy").Click += delegate { Shell.I.ShowProfile(Prof.Privacy); };

            var style = (Style)Application.Current.Resources["Pill"];
            foreach (var c in new[] { "Tous" }.Concat(TweakCatalog.Categories))
            {
                var label = c;
                var r = new RadioButton { Content = label, Style = style, GroupName = "TweakCats", IsChecked = label == "Tous" };
                r.Checked += delegate { cat = label; Refresh(); };
                cats.Children.Add(r);
            }
            search.TextChanged += delegate { Refresh(); };
            btnExplorer.Click += async delegate
            {
                btnExplorer.IsEnabled = false;
                await Task.Factory.StartNew(TweakEngine.RestartExplorer);
                btnExplorer.IsEnabled = true;
                needExplorer = false;
                UpdatePending();
                Shell.I.Toast("Explorateur redémarré.");
            };
        }

        public override void OnShow()
        {
            Task.Factory.StartNew(() => { foreach (var t in TweakCatalog.All) if (t.IsSupported) t.RefreshState(); })
                .ContinueWith(_ => Refresh(), TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Refresh()
        {
            var q = (search.Text ?? "").Trim();
            list.ItemsSource = TweakCatalog.All.Where(t => t.IsSupported
                && (cat == "Tous" || t.Cat == cat)
                && (q.Length == 0 || t.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || t.Desc.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
        }

        public void NotePending(Effect e)
        {
            if (e == Effect.Explorer) needExplorer = true;
            if (e == Effect.Logoff) needLogoff = true;
            if (e == Effect.Reboot) needReboot = true;
            UpdatePending();
        }

        void UpdatePending()
        {
            var any = needExplorer || needLogoff || needReboot;
            pendingBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            btnExplorer.Visibility = needExplorer ? Visibility.Visible : Visibility.Collapsed;
            pendingText.Text = needReboot ? "Certains réglages prendront effet au prochain redémarrage du PC."
                : needLogoff ? "Certains réglages prendront effet à votre prochaine connexion."
                : "Redémarrez l'Explorateur pour voir ces changements.";
        }
    }

    // ─────────────────────────── Nettoyer ───────────────────────────
    public sealed class CleanPage : Page
    {
        readonly List<CleanCat> catsAll = Cleaner.BuildCatalog();
        readonly TextBlock total, sub, analyzePath;
        readonly Button btnScan, btnClean, btnAnalyze, btnUp;
        readonly ItemsControl analyzeList;
        readonly ComboBox driveCombo;
        readonly ProgressBar analyzeBar;
        bool scanned, scanning;
        DirNode node;
        CancellationTokenSource analyzeCts;

        public ICommand Open { get; private set; }

        public CleanPage()
        {
            View = (FrameworkElement)Ui.Load("PageClean.xaml");
            total = F<TextBlock>("CleanTotal"); sub = F<TextBlock>("CleanSub");
            btnScan = F<Button>("BtnScan"); btnClean = F<Button>("BtnClean");
            var cv = new ListCollectionView(catsAll);
            cv.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            F<ItemsControl>("CleanList").ItemsSource = cv;
            foreach (var c in catsAll) c.PropertyChanged += OnCatChanged;
            btnScan.Click += delegate { Scan(); };
            btnClean.Click += delegate { Clean(); };

            var panelClean = F<Grid>("PanelClean"); var panelAnalyze = F<Grid>("PanelAnalyze");
            F<RadioButton>("TabClean").Checked += delegate { panelClean.Visibility = Visibility.Visible; panelAnalyze.Visibility = Visibility.Collapsed; };
            F<RadioButton>("TabAnalyze").Checked += delegate { panelClean.Visibility = Visibility.Collapsed; panelAnalyze.Visibility = Visibility.Visible; };

            analyzeList = F<ItemsControl>("AnalyzeList"); driveCombo = F<ComboBox>("DriveCombo"); analyzePath = F<TextBlock>("AnalyzePath");
            btnAnalyze = F<Button>("BtnAnalyze"); btnUp = F<Button>("BtnUp"); analyzeBar = F<ProgressBar>("AnalyzeBar");
            Open = new RelayCommand(o => OpenNode(o as DirNode));
            analyzeList.DataContext = this;
            driveCombo.ItemsSource = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name).ToList();
            driveCombo.SelectedIndex = 0;
            btnAnalyze.Click += delegate { Analyze(); };
            btnUp.Click += delegate { if (node != null && node.Parent != null) ShowNode(node.Parent); };
        }

        public override void OnShow() { if (!scanned && !scanning) Scan(); }

        // L'analyse tourne en arrière-plan : la mise à jour de l'écran doit revenir sur le thread de l'interface.
        void OnCatChanged(object s, PropertyChangedEventArgs e) { if (e.PropertyName == "Selected" || e.PropertyName == "Size") View.Dispatcher.BeginInvoke(new Action(UpdateTotal)); }

        void UpdateTotal()
        {
            var sel = catsAll.Where(c => c.Selected).ToList();
            var bytes = sel.Where(c => c.Size > 0).Sum(c => c.Size);
            total.Text = scanned || scanning ? Fmt.Size(bytes) + " récupérables" : "—";
            sub.Text = scanning ? "Analyse en cours…" : sel.Count + " catégorie" + (sel.Count > 1 ? "s" : "") + " sélectionnée" + (sel.Count > 1 ? "s" : "") + ".";
            btnClean.IsEnabled = scanned && !scanning && sel.Count > 0;
        }

        void Scan()
        {
            scanning = true;
            btnScan.IsEnabled = false;
            foreach (var c in catsAll) { c.Status = ""; c.Size = -1; }
            UpdateTotal();
            Task.Factory.StartNew(() =>
            {
                foreach (var c in catsAll)
                {
                    long n = 0;
                    try { n = c.Scanner(CancellationToken.None); } catch (Exception e) { Logger.Error("Analyse " + c.Id, e); }
                    c.Size = n;
                }
            }).ContinueWith(_ =>
            {
                scanning = false; scanned = true;
                btnScan.IsEnabled = true;
                UpdateTotal();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        async void Clean()
        {
            var sel = catsAll.Where(c => c.Selected && c.Available).ToList();
            if (sel.Count == 0) return;
            var perm = sel.Where(c => c.Permanent).ToList();
            if (perm.Count > 0)
            {
                var ok = await Shell.I.Confirm("Suppression définitive",
                    "Ces éléments ne pourront pas être récupérés : " + string.Join(", ", perm.Select(c => c.Title)) + ".", "Supprimer définitivement", true);
                if (!ok) return;
            }
            await Shell.I.RunJob("Nettoyage", true, job =>
            {
                long freed = 0;
                for (int i = 0; i < sel.Count; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    var c = sel[i];
                    job.Status(c.Title);
                    job.Progress(100.0 * i / sel.Count);
                    try
                    {
                        var n = c.Cleaner(job);
                        freed += n;
                        job.Log("  " + Fmt.Size(n) + " libérés");
                        c.Status = Fmt.Size(n) + " libérés";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { job.Log("  " + e.Message); c.Status = "Partiel : " + e.Message; }
                }
                job.Progress(100);
                job.Summary = Fmt.Size(freed) + " libérés.";
                Store.AddHistory("clean", "Nettoyage", Fmt.Size(freed) + " libérés");
            }, delegate { Scan(); });
        }

        void Analyze()
        {
            if (analyzeCts != null) { analyzeCts.Cancel(); return; }
            var root = driveCombo.SelectedItem as string;
            if (root == null) return;
            analyzeCts = new CancellationTokenSource();
            var ct = analyzeCts.Token;
            btnAnalyze.Content = "Arrêter";
            analyzeBar.Visibility = Visibility.Visible;
            analyzeList.ItemsSource = null;
            DateTime last = DateTime.MinValue;
            Task.Factory.StartNew(() => DiskAnalyzer.Scan(root, ct, d =>
            {
                if ((DateTime.UtcNow - last).TotalMilliseconds < 150) return;
                last = DateTime.UtcNow;
                analyzePath.Dispatcher.BeginInvoke(new Action(() => analyzePath.Text = d));
            }), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith(t =>
            {
                analyzeCts = null;
                btnAnalyze.Content = "Analyser";
                analyzeBar.Visibility = Visibility.Collapsed;
                if (t.IsCanceled || t.IsFaulted) { analyzePath.Text = t.IsFaulted ? t.Exception.GetBaseException().Message : "Analyse arrêtée."; return; }
                ShowNode(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowNode(DirNode n)
        {
            node = n;
            analyzeList.ItemsSource = n.Kids.Take(200).ToList();
            analyzePath.Text = n.FullPath + " — " + Fmt.Size(n.Size) + (Os.IsAdmin ? "" : " (dossiers protégés non comptés hors mode administrateur)");
            btnUp.IsEnabled = n.Parent != null;
        }

        void OpenNode(DirNode n)
        {
            if (n == null) return;
            if (n.IsFile)
            {
                try { Process.Start("explorer.exe", File.Exists(n.FullPath) ? "/select,\"" + n.FullPath + "\"" : "\"" + n.FullPath + "\""); } catch { }
                return;
            }
            if (n.Kids.Count > 0) ShowNode(n);
        }
    }

    // ─────────────────────────── Démarrage ───────────────────────────
    public sealed class StartupPage : Page
    {
        readonly ItemsControl list; readonly TextBlock sub;
        public StartupPage()
        {
            View = (FrameworkElement)Ui.Load("PageStartup.xaml");
            list = F<ItemsControl>("List"); sub = F<TextBlock>("Sub");
            F<Button>("BtnRefresh").Click += delegate { Load(); };
        }
        public override void OnShow() { Load(); }
        void Load()
        {
            Task.Factory.StartNew(() => StartupManager.List()).ContinueWith(t =>
            {
                if (t.IsFaulted) { Shell.I.Toast(t.Exception.GetBaseException().Message, "bad"); return; }
                list.ItemsSource = t.Result;
                var on = t.Result.Count(i => i.IsOn);
                sub.Text = on + " sur " + t.Result.Count + " actifs. Même mécanisme que le Gestionnaire des tâches : rien n'est supprimé, tout se réactive d'un clic.";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    // ─────────────────────────── Applications ───────────────────────────
    public sealed class AppsPage : Page
    {
        readonly ItemsControl removeList, installGroups;
        readonly TextBlock removeInfo, installInfo, installNote;
        readonly Button btnRemove, btnInstall;
        List<BloatApp> bloat = new List<BloatApp>();
        readonly List<SoftApp> soft = AppsCatalog.Software();
        bool loaded, wingetChecked, wingetOk;

        public AppsPage()
        {
            View = (FrameworkElement)Ui.Load("PageApps.xaml");
            removeList = F<ItemsControl>("RemoveList"); installGroups = F<ItemsControl>("InstallGroups");
            removeInfo = F<TextBlock>("RemoveInfo"); installInfo = F<TextBlock>("InstallInfo"); installNote = F<TextBlock>("InstallNote");
            btnRemove = F<Button>("BtnRemove"); btnInstall = F<Button>("BtnInstall");
            var pr = F<Grid>("PanelRemove"); var pi = F<Grid>("PanelInstall");
            F<RadioButton>("TabRemove").Checked += delegate { pr.Visibility = Visibility.Visible; pi.Visibility = Visibility.Collapsed; };
            F<RadioButton>("TabInstall").Checked += delegate { pr.Visibility = Visibility.Collapsed; pi.Visibility = Visibility.Visible; CheckWinget(); };
            installGroups.ItemsSource = soft.GroupBy(s => s.Group).Select(g => new KeyValuePair<string, List<SoftApp>>(g.Key, g.ToList())).ToList();
            foreach (var s in soft) s.PropertyChanged += delegate { UpdateInstall(); };
            btnRemove.Click += delegate { Remove(); };
            btnInstall.Click += delegate { Install(); };
        }

        public override void OnShow() { if (!loaded) LoadBloat(); }

        void LoadBloat()
        {
            loaded = true;
            removeInfo.Text = "Recherche des applications installées…";
            Task.Factory.StartNew(() => { string err; var l = AppsCatalog.ListInstalled(out err); return l; }).ContinueWith(t =>
            {
                if (t.IsFaulted) { removeInfo.Text = "Lecture impossible : " + t.Exception.GetBaseException().Message; return; }
                bloat = t.Result;
                removeList.ItemsSource = bloat;
                foreach (var a in bloat) a.PropertyChanged += delegate { UpdateRemove(); };
                UpdateRemove();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateRemove()
        {
            var n = bloat.Count(a => a.Selected);
            removeInfo.Text = bloat.Count == 0 ? "Aucune application superflue connue sur ce PC." : n + " sélectionnée" + (n > 1 ? "s" : "") + " sur " + bloat.Count + " repérées.";
            btnRemove.IsEnabled = n > 0;
        }

        async void Remove()
        {
            var sel = bloat.Where(a => a.Selected).ToList();
            var ok = await Shell.I.Confirm("Retirer " + sel.Count + " application" + (sel.Count > 1 ? "s" : "") + " ?",
                string.Join(", ", sel.Select(a => a.Label)) + ".\n\nVous pourrez les réinstaller depuis le Microsoft Store.", "Retirer", false);
            if (!ok) return;
            await Shell.I.RunJob("Retrait des applications", true, job =>
            {
                for (int i = 0; i < sel.Count; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    job.Status(sel[i].Label);
                    job.Progress(100.0 * i / sel.Count);
                    AppsCatalog.Remove(sel[i], true, job);
                }
                job.Progress(100);
                job.Summary = sel.Count + " application" + (sel.Count > 1 ? "s" : "") + " traitée" + (sel.Count > 1 ? "s" : "") + ".";
            }, delegate { LoadBloat(); });
        }

        void CheckWinget()
        {
            if (wingetChecked) return;
            wingetChecked = true;
            Task.Factory.StartNew(() => AppsCatalog.WingetAvailable()).ContinueWith(t =>
            {
                wingetOk = t.Result;
                if (!wingetOk)
                {
                    installInfo.Text = "winget n'est pas disponible sur ce PC";
                    installNote.Text = "Installez ou mettez à jour « Programme d'installation d'application » depuis le Microsoft Store, puis relancez OptiWin.";
                }
                UpdateInstall();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateInstall()
        {
            var n = soft.Count(s => s.Selected);
            if (wingetOk || !wingetChecked) installInfo.Text = n == 0 ? "Choisissez vos logiciels" : n + " logiciel" + (n > 1 ? "s" : "") + " à installer";
            btnInstall.IsEnabled = n > 0 && wingetOk;
        }

        async void Install()
        {
            var sel = soft.Where(s => s.Selected).ToList();
            await Shell.I.RunJob("Installation de " + sel.Count + " logiciel" + (sel.Count > 1 ? "s" : ""), true, job =>
            {
                var failed = new List<string>();
                for (int i = 0; i < sel.Count; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    job.Progress(100.0 * i / sel.Count);
                    var code = AppsCatalog.Install(sel[i], job);
                    if (code != 0 && code != -1978335189) failed.Add(sel[i].Label); // déjà installé et à jour : pas une erreur
                    else Store.AddHistory("soft:" + sel[i].Id, "Logiciel : " + sel[i].Label, "Installé");
                }
                job.Progress(100);
                job.Summary = failed.Count == 0 ? "Tout est installé." : "Échec pour : " + string.Join(", ", failed) + ".";
            });
        }
    }

    // ─────────────────────────── USB et ISO ───────────────────────────
    public sealed class DownloadLink
    {
        public string Name { get; set; }
        public string Note { get; set; }
        public ICommand Open { get; set; }
    }

    public sealed class UsbPage : Page
    {
        readonly TextBox imagePath, txtLocal, isoSrc, isoOut, isoLabel, custSrc, custOut, custWork, hashFile, hashExpected;
        readonly TextBlock imageInfo, diskInfo, hashResult;
        readonly ComboBox diskCombo, custEdition;
        readonly CheckBox chkExternal, chkBypassHw, chkBypassAcct, chkLocal, chkNoBitlocker, chkPrivacy, chkCopyOpti, chkVerify, chkNoPrompt;
        readonly RadioButton modeMbr, modeGpt, modeRaw;
        readonly Border winOptions;
        readonly FrameworkElement[] panels;
        readonly MultibootPanel multi;
        readonly WinDownloadPanel download;
        IsoInfo image;
        bool loaded;

        public UsbPage()
        {
            View = (FrameworkElement)Ui.Load("PageUsb.xaml");
            imagePath = F<TextBox>("ImagePath"); txtLocal = F<TextBox>("TxtLocal"); isoSrc = F<TextBox>("IsoSrc"); isoOut = F<TextBox>("IsoOut"); isoLabel = F<TextBox>("IsoLabel");
            custSrc = F<TextBox>("CustSrc"); custOut = F<TextBox>("CustOut"); custWork = F<TextBox>("CustWork"); hashFile = F<TextBox>("HashFile"); hashExpected = F<TextBox>("HashExpected");
            imageInfo = F<TextBlock>("ImageInfo"); diskInfo = F<TextBlock>("DiskInfo"); hashResult = F<TextBlock>("HashResult");
            diskCombo = F<ComboBox>("DiskCombo"); custEdition = F<ComboBox>("CustEdition");
            chkExternal = F<CheckBox>("ChkExternal"); chkBypassHw = F<CheckBox>("ChkBypassHw"); chkBypassAcct = F<CheckBox>("ChkBypassAcct"); chkLocal = F<CheckBox>("ChkLocal");
            chkNoBitlocker = F<CheckBox>("ChkNoBitlocker"); chkPrivacy = F<CheckBox>("ChkPrivacy"); chkCopyOpti = F<CheckBox>("ChkCopyOpti"); chkVerify = F<CheckBox>("ChkVerify"); chkNoPrompt = F<CheckBox>("ChkNoPrompt");
            modeMbr = F<RadioButton>("ModeMbr"); modeGpt = F<RadioButton>("ModeGpt"); modeRaw = F<RadioButton>("ModeRaw");
            winOptions = F<Border>("WinOptions");

            panels = new FrameworkElement[] { F<FrameworkElement>("PanelFlash"), F<FrameworkElement>("PanelIso"), F<FrameworkElement>("PanelCustom"), F<FrameworkElement>("PanelMulti"), F<FrameworkElement>("PanelDownload"), F<FrameworkElement>("PanelHash") };
            var tabs = new[] { "TabFlash", "TabIso", "TabCustom", "TabMulti", "TabDownload", "TabHash" };
            for (int i = 0; i < tabs.Length; i++)
            {
                var idx = i;
                F<RadioButton>(tabs[i]).Checked += delegate { ShowTab(idx); };
            }

            // Clé bootable
            F<Button>("BtnPickImage").Click += delegate
            {
                var p = Dialogs.PickOpen("Choisir une image", "Images disque (*.iso;*.img)|*.iso;*.img|Tous les fichiers (*.*)|*.*");
                if (p != null) SetImage(p);
            };
            F<Button>("BtnRefreshDisks").Click += delegate { LoadDisks(); };
            chkExternal.Checked += delegate { LoadDisks(); };
            chkExternal.Unchecked += delegate { LoadDisks(); };
            diskCombo.SelectionChanged += delegate { UpdateDiskInfo(); };
            modeRaw.Checked += delegate { winOptions.Visibility = Visibility.Collapsed; };
            modeMbr.Checked += delegate { winOptions.Visibility = Visibility.Visible; };
            modeGpt.Checked += delegate { winOptions.Visibility = Visibility.Visible; };
            chkLocal.Checked += delegate { txtLocal.IsEnabled = true; };
            chkLocal.Unchecked += delegate { txtLocal.IsEnabled = false; };
            F<Button>("BtnFlash").Click += delegate { Flash(); };
            txtLocal.Text = Environment.UserName;

            // Créer une ISO
            F<Button>("BtnIsoSrc").Click += delegate { var p = Dialogs.PickFolder("Dossier ou clé à transformer en ISO"); if (p != null) isoSrc.Text = p; };
            F<Button>("BtnIsoOut").Click += delegate { var p = Dialogs.PickSave("Enregistrer l'ISO", "Image ISO (*.iso)|*.iso", (isoLabel.Text.Length > 0 ? isoLabel.Text : "image") + ".iso"); if (p != null) isoOut.Text = p; };
            F<Button>("BtnMakeIso").Click += delegate { MakeIso(); };

            // ISO personnalisée
            custWork.Text = Path.GetTempPath();
            F<Button>("BtnCustSrc").Click += delegate { var p = Dialogs.PickOpen("ISO officielle de Windows", "Image ISO (*.iso)|*.iso"); if (p != null) { custSrc.Text = p; custEdition.ItemsSource = null; ReadEditions(); } };
            F<Button>("BtnCustRead").Click += delegate { ReadEditions(); };
            F<Button>("BtnCustOut").Click += delegate { var p = Dialogs.PickSave("Enregistrer l'ISO personnalisée", "Image ISO (*.iso)|*.iso", "Windows-OptiWin.iso"); if (p != null) custOut.Text = p; };
            F<Button>("BtnCustWork").Click += delegate { var p = Dialogs.PickFolder("Dossier de travail temporaire"); if (p != null) custWork.Text = p; };
            F<Button>("BtnCustGo").Click += delegate { Customize(); };

            // Téléchargements
            Func<string, string, string, DownloadLink> L = (n, note, url) => new DownloadLink { Name = n, Note = note, Open = new RelayCommand(() => { try { Process.Start(url); } catch (Exception e) { Shell.I.Toast(e.Message, "bad"); } }) };
            F<ItemsControl>("LinkList").ItemsSource = new List<DownloadLink>
            {
                L("Windows 11", "ISO officielle Microsoft", "https://www.microsoft.com/software-download/windows11"),
                L("Windows 10", "ISO officielle Microsoft", "https://www.microsoft.com/software-download/windows10ISO"),
                L("Ubuntu", "Distribution Linux la plus répandue", "https://ubuntu.com/download/desktop"),
                L("Linux Mint", "Idéale pour redonner vie à un vieux PC", "https://linuxmint.com/download.php"),
                L("Fedora Workstation", "Linux moderne", "https://fedoraproject.org/workstation/download"),
                L("Clonezilla Live", "Cloner et sauvegarder un disque entier", "https://clonezilla.org/downloads.php"),
                L("GParted Live", "Partitionner un disque depuis une clé", "https://gparted.org/download.php"),
                L("Memtest86+", "Tester la mémoire vive", "https://www.memtest.org/"),
            };

            // Vérifier
            F<Button>("BtnHashFile").Click += delegate { var p = Dialogs.PickOpen("Fichier à vérifier", "Tous les fichiers (*.*)|*.*"); if (p != null) hashFile.Text = p; };
            F<Button>("BtnHashGo").Click += delegate { HashNow(); };

            multi = new MultibootPanel(View);
            download = new WinDownloadPanel(View, iso =>
            {
                // L'ISO fraîchement créée est prête à être mise sur une clé.
                SetImage(iso);
                Shell.I.Toast("ISO prête : elle est déjà sélectionnée dans « Clé bootable ».");
            });
        }

        public override void OnShow()
        {
            if (!loaded) { loaded = true; LoadDisks(); multi.Refresh(); }
            F<Button>("BtnFlash").IsEnabled = Os.IsAdmin;
            F<Button>("BtnCustGo").IsEnabled = Os.IsAdmin;
            F<Button>("BtnCustRead").IsEnabled = Os.IsAdmin;
            if (!Os.IsAdmin) diskInfo.Text = "Écrire sur une clé demande le mode administrateur (bouton en bas à gauche).";
        }

        void ShowTab(int idx)
        {
            for (int i = 0; i < panels.Length; i++) panels[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
        }

        void LoadDisks()
        {
            var ext = chkExternal.IsChecked == true;
            diskInfo.Text = "Recherche des clés USB…";
            Task.Factory.StartNew(() => Disks.List(ext)).ContinueWith(t =>
            {
                if (t.IsFaulted) { diskInfo.Text = t.Exception.GetBaseException().Message; return; }
                diskCombo.ItemsSource = t.Result;
                diskCombo.SelectedIndex = t.Result.Count > 0 ? 0 : -1;
                UpdateDiskInfo();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateDiskInfo()
        {
            var d = diskCombo.SelectedItem as UsbDisk;
            if (d == null) { diskInfo.Text = "Aucune clé USB détectée. Branchez-en une puis actualisez."; return; }
            var usable = d.Size > 32000L * 1000 * 1000 ? " En mode Windows, 32 Go seront utilisés : c'est la limite de formatage FAT32 de Windows." : "";
            diskInfo.Text = "Disque " + d.Index + ", bus " + d.Bus + "." + usable;
        }

        void SetImage(string path)
        {
            imagePath.Text = path;
            try
            {
                image = IsoProbe.Inspect(path);
                imageInfo.Text = image.Describe();
                // Le mode conseillé suit le contenu réel de l'image.
                if (image.Kind == IsoKind.Hybrid || !path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) modeRaw.IsChecked = true;
                else if (modeRaw.IsChecked == true) modeMbr.IsChecked = true;
            }
            catch (Exception e) { imageInfo.Text = "Lecture impossible : " + e.Message; image = null; }
        }

        UnattendOptions ReadUnattend(CheckBox hw, CheckBox acct, CheckBox bl, CheckBox priv, string account)
        {
            return new UnattendOptions
            {
                BypassHardware = hw.IsChecked == true, BypassOnlineAccount = acct.IsChecked == true, NoBitLocker = bl.IsChecked == true, SkipPrivacy = priv.IsChecked == true,
                LocalAccount = account
            };
        }

        async void Flash()
        {
            var d = diskCombo.SelectedItem as UsbDisk;
            if (image == null) { Shell.I.Toast("Choisissez d'abord une image.", "warn"); return; }
            if (d == null) { Shell.I.Toast("Choisissez une clé USB.", "warn"); return; }
            var account = chkLocal.IsChecked == true ? txtLocal.Text.Trim() : "";
            if (account.Length > 0 && !Unattend.IsValidAccountName(account)) { Shell.I.Toast("Ce nom de compte n'est pas valide pour Windows.", "warn"); return; }
            var raw = modeRaw.IsChecked == true;
            var opts = new FlashOptions
            {
                Image = image.Path, Disk = d, Verify = chkVerify.IsChecked == true,
                Mode = raw ? FlashMode.Raw : modeGpt.IsChecked == true ? FlashMode.WindowsGpt : FlashMode.WindowsMbr,
                CopyOptiWin = chkCopyOpti.IsChecked == true && !raw,
                Unattend = raw ? new UnattendOptions() : ReadUnattend(chkBypassHw, chkBypassAcct, chkNoBitlocker, chkPrivacy, account)
            };
            var letters = d.Letters.Count > 0 ? " (lecteur " + string.Join(", ", d.Letters) + ")" : "";
            var rawWarn = raw && image.Kind != IsoKind.Hybrid && image.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? "\n\nAttention : cette image ne semble pas prévue pour une copie brute, la clé risque de ne pas démarrer." : "";
            var ok = await Shell.I.Confirm("Effacer " + d.Model + " ?",
                "Tout le contenu de ce périphérique de " + Fmt.Size(d.Size) + letters + " sera définitivement effacé, puis remplacé par « " + Path.GetFileName(image.Path) + " »." + rawWarn,
                "Effacer et créer la clé", true, "EFFACER");
            if (!ok) return;
            await Shell.I.RunJob("Création de la clé bootable", true, job => BootableWriter.Run(opts, job), delegate { LoadDisks(); });
        }

        async void MakeIso()
        {
            var src = isoSrc.Text.Trim(); var dst = isoOut.Text.Trim(); var label = isoLabel.Text.Trim();
            if (!Directory.Exists(src)) { Shell.I.Toast("Le dossier source est introuvable.", "warn"); return; }
            if (dst.Length == 0) { Shell.I.Toast("Indiquez où enregistrer l'ISO.", "warn"); return; }
            if (!dst.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) dst += ".iso";
            var noPrompt = chkNoPrompt.IsChecked == true;
            await Shell.I.RunJob("Création de l'ISO", true, job =>
            {
                IsoBuilder.Create(src, dst, label, noPrompt, job);
                job.Summary = "ISO créée : " + dst;
            });
        }

        async void ReadEditions()
        {
            var iso = custSrc.Text;
            if (!File.Exists(iso)) { Shell.I.Toast("Choisissez d'abord une ISO.", "warn"); return; }
            if (!Os.IsAdmin) { Shell.I.Toast("Lire l'image demande le mode administrateur.", "warn"); return; }
            List<WimEdition> eds = null;
            var ok = await Shell.I.RunJob("Lecture des éditions", false, job =>
            {
                eds = ImageCustomizer.ReadEditions(iso, job);
                job.Summary = eds.Count + " édition" + (eds.Count > 1 ? "s" : "") + " trouvée" + (eds.Count > 1 ? "s" : "") + ".";
            });
            if (ok && eds != null)
            {
                custEdition.ItemsSource = eds;
                var pro = eds.FirstOrDefault(e => e.Name.EndsWith(" Pro", StringComparison.OrdinalIgnoreCase));
                custEdition.SelectedItem = pro ?? eds.FirstOrDefault();
            }
        }

        async void Customize()
        {
            var ed = custEdition.SelectedItem as WimEdition;
            if (!File.Exists(custSrc.Text)) { Shell.I.Toast("Choisissez d'abord une ISO.", "warn"); return; }
            if (ed == null) { Shell.I.Toast("Lisez les éditions et choisissez-en une.", "warn"); return; }
            if (custOut.Text.Trim().Length == 0) { Shell.I.Toast("Indiquez où enregistrer l'ISO.", "warn"); return; }
            if (!Directory.Exists(custWork.Text)) { Shell.I.Toast("Le dossier de travail est introuvable.", "warn"); return; }
            var o = new CustomIsoOptions
            {
                SourceIso = custSrc.Text, OutIso = custOut.Text.Trim(), WorkDir = custWork.Text, EditionIndex = ed.Index,
                RemoveBloat = F<CheckBox>("CustBloat").IsChecked == true, InjectDrivers = F<CheckBox>("CustDrivers").IsChecked == true, CopyOptiWin = F<CheckBox>("CustOpti").IsChecked == true,
                Unattend = ReadUnattend(F<CheckBox>("CustBypass"), F<CheckBox>("CustAcct"), F<CheckBox>("CustNoBitlocker"), F<CheckBox>("CustPrivacy"), "")
            };
            await Shell.I.RunJob("ISO personnalisée : " + ed.Name, true, job => ImageCustomizer.Run(o, job));
        }

        async void HashNow()
        {
            var path = hashFile.Text;
            if (!File.Exists(path)) { Shell.I.Toast("Choisissez un fichier.", "warn"); return; }
            var expected = new string(hashExpected.Text.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            string hash = null;
            var ok = await Shell.I.RunJob("Calcul de l'empreinte SHA-256", true, job =>
            {
                job.Status(Path.GetFileName(path));
                hash = Hashing.Sha256(path, job);
                job.Log(hash);
                job.Summary = expected.Length == 0 ? "Empreinte calculée." : expected == hash ? "Identique à l'empreinte attendue : le fichier est intact." : "DIFFÉRENTE de l'empreinte attendue : ne faites pas confiance à ce fichier.";
            });
            if (ok && hash != null)
            {
                hashResult.Text = hash;
                hashResult.SetResourceReference(TextBlock.ForegroundProperty, expected.Length == 0 ? "Text" : expected == hash ? "Ok" : "Danger");
            }
        }

        public void DemoSetTab(int idx) { ShowTab(idx); }
    }

    // ─────────────────────────── Outils ───────────────────────────
    public sealed class ToolsPage : Page
    {
        readonly ItemsControl dnsList;
        readonly TextBlock dnsCurrent;
        readonly List<DnsResult> dns = Dns.Candidates();

        public ToolsPage()
        {
            View = (FrameworkElement)Ui.Load("PageTools.xaml");
            var tools = Tools.Catalog().Where(t => t.Id != "battery" || SysInfo.Current.IsLaptop).ToList();
            foreach (var t in tools)
            {
                var tool = t;
                tool.Cmd = new RelayCommand(() => Launch(tool));
            }
            F<ItemsControl>("ToolList").ItemsSource = tools;
            dnsList = F<ItemsControl>("DnsList"); dnsCurrent = F<TextBlock>("DnsCurrent");
            foreach (var d in dns)
            {
                var item = d;
                item.Use = new RelayCommand(() => ApplyDns(item));
            }
            dnsList.ItemsSource = dns;
            F<Button>("BtnDnsTest").Click += delegate { TestDns(); };
            F<Button>("BtnDnsRestore").Click += async delegate
            {
                if (!Os.IsAdmin) { Shell.I.Toast("Modifier le DNS demande le mode administrateur.", "warn"); return; }
                await Shell.I.RunJob("Restauration du DNS", false, job => { Dns.RestoreOriginal(job); job.Summary = "Réglage d'origine restauré."; }, delegate { RefreshDns(); });
            };
        }

        public override void OnShow()
        {
            RefreshDns();
            var list = F<ItemsControl>("Win11List");
            if (list.ItemsSource == null)
                Task.Factory.StartNew(() => Win11Check.Run(SysInfo.Current)).ContinueWith(t => { if (!t.IsFaulted) list.ItemsSource = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void RefreshDns()
        {
            Task.Factory.StartNew(() => Dns.CurrentSummary()).ContinueWith(t => { dnsCurrent.Text = t.IsFaulted ? "" : "Actuel : " + t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        async void Launch(ToolAction t)
        {
            if (t.Confirm != null && !await Shell.I.Confirm(t.Title, t.Confirm, "Continuer", false)) return;
            string folder = null;
            if (t.PickFolder)
            {
                folder = Dialogs.PickFolder(t.Title);
                if (folder == null) return;
            }
            await Shell.I.RunJob(t.Title, true, job => t.Run(job, folder));
        }

        void TestDns()
        {
            var btn = F<Button>("BtnDnsTest");
            btn.IsEnabled = false;
            foreach (var d in dns) { d.Ms = -1; d.IsBest = false; }
            Task.Factory.StartNew(() =>
            {
                foreach (var d in dns) d.Ms = Dns.Measure(d.Primary, CancellationToken.None);
            }).ContinueWith(_ =>
            {
                btn.IsEnabled = true;
                var best = dns.Where(d => !double.IsInfinity(d.Ms) && d.Ms >= 0).OrderBy(d => d.Ms).FirstOrDefault();
                if (best != null) best.IsBest = true;
                else Shell.I.Toast("Aucun serveur n'a répondu : pare-feu ou connexion ?", "warn");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        async void ApplyDns(DnsResult d)
        {
            if (!Os.IsAdmin) { Shell.I.Toast("Modifier le DNS demande le mode administrateur.", "warn"); return; }
            await Shell.I.RunJob("DNS : " + d.Name, false, job => { Dns.Apply(d.Primary, d.Secondary, job); job.Summary = "DNS réglé sur " + d.Name + "."; }, delegate { RefreshDns(); });
        }
    }

    // ─────────────────────────── Historique ───────────────────────────
    public sealed class ActiveTweak
    {
        public string Title { get; set; }
        public string Cat { get; set; }
        public ICommand Undo { get; set; }
    }
    public sealed class LogRow
    {
        public string When { get; set; }
        public string Title { get; set; }
        public string Action { get; set; }
    }

    public sealed class HistoryPage : Page
    {
        readonly ItemsControl activeList, logList;
        readonly TextBlock count, none;

        public HistoryPage()
        {
            View = (FrameworkElement)Ui.Load("PageHistory.xaml");
            activeList = F<ItemsControl>("ActiveList"); logList = F<ItemsControl>("LogList");
            count = F<TextBlock>("ActiveCount"); none = F<TextBlock>("NoneText");
            F<Button>("BtnRstrui").Click += delegate { try { Process.Start("rstrui.exe"); } catch (Exception e) { Shell.I.Toast(e.Message, "bad"); } };
            F<Button>("BtnRestorePoint").IsEnabled = Os.IsAdmin;
            F<Button>("BtnRestorePoint").Click += async delegate
            {
                await Shell.I.RunJob("Point de restauration", false, job =>
                {
                    job.Status("Création en cours");
                    var ok = TweakEngine.CreateRestorePoint("Point manuel OptiWin", job.Log);
                    if (!ok) throw new InvalidOperationException("Windows a refusé de créer le point. La protection du système est peut-être désactivée.");
                    job.Summary = "Point de restauration créé.";
                });
            };
            F<Button>("BtnUndoAll").Click += delegate { UndoAll(); };
        }

        public override void OnShow() { Reload(); }

        void Reload()
        {
            var active = Store.BackupIds().Select(TweakCatalog.ById).Where(t => t != null).ToList();
            activeList.ItemsSource = active.Select(t => new ActiveTweak
            {
                Title = t.Title, Cat = t.Cat,
                Undo = new RelayCommand(() => UndoOne(t))
            }).ToList();
            count.Text = active.Count.ToString();
            none.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            F<Button>("BtnUndoAll").IsEnabled = active.Count > 0;
            logList.ItemsSource = Store.History().AsEnumerable().Reverse().Take(200).Select(h =>
            {
                DateTime d;
                return new LogRow { When = DateTime.TryParse(h.When, out d) ? Fmt.DateTimeText(d) : h.When, Title = h.Title, Action = h.Action };
            }).ToList();
        }

        async void UndoOne(Tweak t)
        {
            try
            {
                await Task.Factory.StartNew(() => TweakEngine.Revert(t));
                Shell.I.Toast("Annulé : " + t.Title + Shell.EffectHint(t.Effect));
            }
            catch (Exception e) { Shell.I.Toast("Échec : " + e.Message, "bad"); }
            Reload();
        }

        async void UndoAll()
        {
            var ok = await Shell.I.Confirm("Tout annuler ?", "Tous les réglages appliqués par OptiWin reviendront à leur valeur d'origine.", "Tout annuler", true);
            if (!ok) return;
            await Shell.I.RunJob("Annulation des réglages", true, job =>
            {
                var ids = Store.BackupIds();
                int fails = 0;
                for (int i = 0; i < ids.Length; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    var t = TweakCatalog.ById(ids[i]);
                    if (t == null) continue;
                    job.Status(t.Title);
                    job.Progress(100.0 * i / ids.Length);
                    try { TweakEngine.Revert(t); }
                    catch (Exception e) { fails++; job.Log("  " + e.Message); }
                }
                job.Progress(100);
                job.Summary = fails == 0 ? "Tout est revenu à l'état d'origine. Redémarrez pour finaliser." : fails + " réglage(s) n'ont pas pu être annulés : voir le journal.";
            }, delegate { Reload(); });
        }
    }
}
