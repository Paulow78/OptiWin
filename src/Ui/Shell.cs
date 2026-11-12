using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OptiWin
{
    public sealed class PlanItem : Bindable
    {
        public Tweak Tweak { get; set; }
        bool sel;
        public bool Selected { get { return sel; } set { if (Set(ref sel, value) && Changed != null) Changed(); } }
        public Action Changed;
    }

    /// <summary>Fenêtre principale : navigation, dialogues, tâches longues.</summary>
    public sealed class Shell
    {
        public static Shell I;
        public readonly Window Win;
        public readonly Grid Root;
        readonly ContentControl host;
        readonly Dictionary<string, RadioButton> nav = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, Page> pages = new Dictionary<string, Page>();
        Page current; string currentId;
        readonly bool solid;
        volatile bool jobRunning;
        IntPtr hwnd;
        readonly Button btnMax;

        T F<T>(string n) where T : class
        {
            var o = Win.FindName(n) as T;
            if (o == null) throw new InvalidOperationException("Élément d'interface manquant : " + n);
            return o;
        }

        public Shell(bool solidBackground)
        {
            I = this;
            solid = solidBackground;
            Win = (Window)Ui.Load("MainWindow.xaml");
            Root = F<Grid>("Root");
            host = F<ContentControl>("PageHost");
            btnMax = F<Button>("BtnMax");

            foreach (var kv in new Dictionary<string, string> { {"home","NavHome"},{"optimize","NavOptimize"},{"clean","NavClean"},{"startup","NavStartup"},{"apps","NavApps"},{"reinstall","NavReinstall"},{"usb","NavUsb"},{"diagnostic","NavDiagnostic"},{"tools","NavTools"},{"history","NavHistory"} })
            {
                var id = kv.Key;
                var r = F<RadioButton>(kv.Value);
                nav[id] = r;
                r.Checked += delegate { Go(id); };
            }

            F<Button>("BtnMin").Click += delegate { Win.WindowState = WindowState.Minimized; };
            btnMax.Click += delegate { ToggleMax(); };
            F<Button>("BtnClose").Click += delegate { Win.Close(); };
            F<Button>("BtnTheme").Click += delegate { SetTheme(!Themes.IsDark); };

            Win.SourceInitialized += OnSourceInitialized;
            Win.StateChanged += delegate { UpdateMaxMargin(); };
            Win.Loaded += delegate { UpdateMaxMargin(); };
            Win.PreviewKeyDown += OnKey;
            Win.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (jobRunning) { e.Cancel = true; Toast("Une opération est en cours : attendez la fin ou annulez-la.", "warn"); return; }
                if (!solid) WindowMemory.Save(Win);
            };
            if (!solid) WindowMemory.Restore(Win);

            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            F<TextBlock>("VersionText").Text = "Version " + VersionText(ver);

            if (!Os.IsAdmin)
            {
                F<Border>("AdminBadge").Visibility = Visibility.Visible;
                F<Button>("BtnElevate").Click += delegate { Elevate(); };
            }

            Tweak.ToggleRequested = OnToggle;
            StartupItem.Failed = (it, e) => Toast("Impossible de modifier " + it.Name + " : " + e.Message, "bad");
            WireConfirm();
            WirePlan();
            WireJob();
            UpdateThemeButton();
        }

        /// <summary>« 26.11.1 » : année, mois, puis numéro de publication dans le mois (omis s'il vaut 0).</summary>
        public static string VersionText(Version v)
        {
            return v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : "");
        }

        static readonly string[] PageOrder = { "home", "optimize", "clean", "startup", "apps", "reinstall", "usb", "diagnostic", "tools", "history" };

        bool DialogOpen { get { return F<Grid>("ConfirmLayer").IsVisible || F<Grid>("PlanLayer").IsVisible || F<Grid>("JobLayer").IsVisible; } }

        // Raccourcis : Ctrl+1 à Ctrl+0 pour les pages, F5 pour actualiser, Ctrl+F pour chercher, Ctrl+T pour le thème, Échap pour fermer.
        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (F<Grid>("ConfirmLayer").IsVisible) { CloseConfirm(false); e.Handled = true; }
                else if (F<Grid>("PlanLayer").IsVisible) { F<Grid>("PlanLayer").Visibility = Visibility.Collapsed; e.Handled = true; }
                else if (F<Grid>("JobLayer").IsVisible && !jobRunning) { F<Grid>("JobLayer").Visibility = Visibility.Collapsed; e.Handled = true; }
                return;
            }
            if (DialogOpen) return;
            if (e.Key == Key.F5) { if (current != null) current.OnRefresh(); e.Handled = true; return; }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            if (e.Key == Key.F) { if (current != null && current.OnFind()) e.Handled = true; return; }
            if (e.Key == Key.T) { SetTheme(!Themes.IsDark); e.Handled = true; return; }
            int n = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9) n = e.Key - Key.D1;
            else if (e.Key == Key.D0) n = 9;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) n = e.Key - Key.NumPad1;
            else if (e.Key == Key.NumPad0) n = 9;
            if (n >= 0 && n < PageOrder.Length) { Go(PageOrder[n]); e.Handled = true; }
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(Win).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            Backdrop.SetDarkFrame(hwnd, Themes.IsDark);
            if (!solid && Backdrop.TryEnableMica(hwnd, src))
            {
                Themes.Mica = true;
                Themes.Apply(Themes.IsDark);
                // Sous Mica, Windows dessine lui-même ses boutons de fenêtre dans le cadre étendu : les nôtres
                // feraient double emploi. Les siens apportent en plus les dispositions d'ancrage de Windows 11.
                F<StackPanel>("CaptionButtons").Visibility = Visibility.Collapsed;
                System.Windows.Shell.WindowChrome.GetWindowChrome(Win).UseAeroCaptionButtons = true;
            }
            src.AddHook(WndProc);
        }

        // Survol du bouton Agrandir : renvoie la zone système pour que Windows 11 affiche ses dispositions d'ancrage.
        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, HTMAXBUTTON = 9;
            if (Os.Build < 22000) return IntPtr.Zero;
            if (msg == WM_NCHITTEST)
            {
                try
                {
                    // Les 32 bits bas suffisent : ToInt32 déborderait sur un lParam 64 bits non étendu.
                    var v = unchecked((int)(l.ToInt64() & 0xFFFFFFFF));
                    var pt = btnMax.PointFromScreen(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF)));
                    if (pt.X >= 0 && pt.Y >= 0 && pt.X < btnMax.ActualWidth && pt.Y < btnMax.ActualHeight && btnMax.IsVisible)
                    {
                        handled = true;
                        return (IntPtr)HTMAXBUTTON;
                    }
                }
                catch { }
            }
            else if (msg == WM_NCLBUTTONDOWN && w.ToInt64() == HTMAXBUTTON) { handled = true; return IntPtr.Zero; }
            else if (msg == WM_NCLBUTTONUP && w.ToInt64() == HTMAXBUTTON) { handled = true; ToggleMax(); return IntPtr.Zero; }
            return IntPtr.Zero;
        }

        void ToggleMax() { Win.WindowState = Win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }

        void UpdateMaxMargin()
        {
            // Une fenêtre sans cadre agrandie déborde de l'écran de l'épaisseur du cadre.
            Root.Margin = Win.WindowState == WindowState.Maximized ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 2) : new Thickness(0);
        }

        public void SetTheme(bool dark)
        {
            Themes.Apply(dark);
            Store.SetSetting("theme", dark ? "dark" : "light");
            if (hwnd != IntPtr.Zero) Backdrop.SetDarkFrame(hwnd, dark);
            UpdateThemeButton();
        }

        void UpdateThemeButton()
        {
            F<TextBlock>("ThemeLabel").Text = Themes.IsDark ? "Thème clair" : "Thème sombre";
            F<System.Windows.Shapes.Path>("ThemeIcon").Data = (Geometry)Application.Current.Resources[Themes.IsDark ? "IcoSun" : "IcoMoon"];
        }

        void Elevate()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Paths.Exe) { UseShellExecute = true, Verb = "runas", Arguments = "--elevated" });
                Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception) { Toast("Élévation refusée : OptiWin reste en mode limité.", "warn"); }
        }

        // ──────── Navigation ────────
        Page Create(string id)
        {
            switch (id)
            {
                case "home": return new HomePage();
                case "optimize": return new OptimizePage();
                case "clean": return new CleanPage();
                case "startup": return new StartupPage();
                case "apps": return new AppsPage();
                case "reinstall": return new ReinstallPage();
                case "usb": return new UsbPage();
                case "diagnostic": return new DiagnosticPage();
                case "tools": return new ToolsPage();
                default: return new HistoryPage();
            }
        }

        public T PageOf<T>(string id) where T : Page { Go(id); return (T)pages[id]; }

        public void Go(string id)
        {
            if (id == currentId) return;
            Page p;
            if (!pages.TryGetValue(id, out p)) { p = Create(id); pages[id] = p; }
            if (current != null) current.OnHide();
            current = p; currentId = id;
            host.Content = p.View;
            var r = nav[id];
            if (r.IsChecked != true) r.IsChecked = true;
            p.OnShow();
            host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1, new Duration(TimeSpan.FromMilliseconds(160))));
        }

        // ──────── Message éphémère ────────
        DispatcherTimer toastTimer;
        public void Toast(string text, string kind = "ok")
        {
            var t = F<Border>("Toast");
            F<TextBlock>("ToastText").Text = text;
            F<System.Windows.Shapes.Ellipse>("ToastDot").Fill = (Brush)Application.Current.Resources[kind == "bad" ? "Danger" : kind == "warn" ? "Warn" : kind == "info" ? "Info" : "Ok"];
            t.Visibility = Visibility.Visible;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            t.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))) { EasingFunction = ease });
            var slide = new TranslateTransform(0, 14);
            t.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = ease });
            if (toastTimer != null) toastTimer.Stop();
            // Un message long reste plus longtemps lisible, une erreur aussi.
            var seconds = Math.Max(kind == "bad" ? 7 : 4, Math.Min(12, text.Length / 18));
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            toastTimer.Tick += delegate
            {
                toastTimer.Stop();
                var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(220)));
                fade.Completed += delegate { if (t.Opacity < 0.05) t.Visibility = Visibility.Collapsed; };
                t.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            toastTimer.Start();
        }

        // ──────── Réglage individuel ────────
        async void OnToggle(Tweak t, bool on)
        {
            if (t.Busy) return;
            if (on && t.Warning != null)
            {
                var ok = await Confirm(t.Title, t.Warning, "Appliquer quand même", true);
                if (!ok) { t.RefreshState(); return; }
            }
            t.Busy = true;
            SettingProtectedException blocked = null;
            try
            {
                await Task.Factory.StartNew(() => { if (on) TweakEngine.Apply(t); else TweakEngine.Revert(t); }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                if (on && !t.IsApplied) Toast("Réglage appliqué partiellement : voir le journal.", "warn");
                else Toast((on ? "Appliqué : " : "Annulé : ") + t.Title + EffectHint(t.Effect));
                var o = CurrentOptimize();
                if (o != null) { o.NotePending(t.Effect); o.UpdateCount(); }
            }
            catch (SettingProtectedException e) { Logger.Error("Réglage protégé " + t.Id, e); blocked = e; }
            catch (Exception e)
            {
                Logger.Error("Réglage " + t.Id, e);
                Toast("Échec : " + e.Message, "bad");
            }
            finally
            {
                t.Busy = false;
                t.RefreshState();
            }
            if (blocked == null) return;
            if (t.SettingsUri == null) { Toast(blocked.Message, "warn"); return; }
            if (await Confirm(t.Title, "Windows protège ce réglage : il refuse que des programmes le modifient, même avec les droits administrateur. Vous pouvez le faire vous-même dans les Paramètres de Windows.", "Ouvrir les Paramètres", false))
                try { Process.Start(t.SettingsUri); } catch (Exception e) { Toast(e.Message, "bad"); }
        }

        OptimizePage CurrentOptimize() { Page p; return pages.TryGetValue("optimize", out p) ? p as OptimizePage : null; }

        public static string EffectHint(Effect e)
        {
            switch (e)
            {
                case Effect.Reboot: return " (effectif au prochain redémarrage)";
                case Effect.Logoff: return " (effectif à la prochaine connexion)";
                case Effect.Explorer: return " (effectif après redémarrage de l'Explorateur)";
                default: return "";
            }
        }

        // ──────── Confirmation ────────
        TaskCompletionSource<bool> confirmTcs;
        void WireConfirm()
        {
            var yes = F<Button>("ConfirmYes");
            var input = F<TextBox>("ConfirmInput");
            yes.Click += delegate { CloseConfirm(true); };
            F<Button>("ConfirmNo").Click += delegate { CloseConfirm(false); };
            input.TextChanged += delegate
            {
                var w = input.Tag as string;
                if (w != null) yes.IsEnabled = string.Equals(input.Text.Trim(), w, StringComparison.OrdinalIgnoreCase);
            };
        }
        void CloseConfirm(bool v)
        {
            F<Grid>("ConfirmLayer").Visibility = Visibility.Collapsed;
            var t = confirmTcs; confirmTcs = null;
            if (t != null) t.TrySetResult(v);
        }

        /// <param name="typed">Si renseigné, ce mot doit être saisi pour confirmer (actions destructrices).</param>
        public Task<bool> Confirm(string title, string body, string okLabel, bool danger, string typed = null)
        {
            F<TextBlock>("ConfirmTitle").Text = title;
            F<TextBlock>("ConfirmBody").Text = body;
            var yes = F<Button>("ConfirmYes");
            yes.Content = okLabel;
            yes.Style = (Style)Application.Current.Resources[danger ? "BtnDanger" : "BtnPrimary"];
            var input = F<TextBox>("ConfirmInput");
            input.Tag = typed;
            input.Text = "";
            F<StackPanel>("ConfirmTyped").Visibility = typed != null ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("ConfirmTypedLabel").Text = "Pour confirmer, saisissez : " + typed;
            yes.IsEnabled = typed == null;
            F<Grid>("ConfirmLayer").Visibility = Visibility.Visible;
            if (typed != null) input.Focus();
            confirmTcs = new TaskCompletionSource<bool>();
            return confirmTcs.Task;
        }

        // ──────── Plan d'un profil ────────
        List<PlanItem> planItems;
        Prof planProfile;
        void WirePlan()
        {
            F<Button>("PlanNo").Click += delegate { F<Grid>("PlanLayer").Visibility = Visibility.Collapsed; };
            F<Button>("PlanYes").Click += delegate { F<Grid>("PlanLayer").Visibility = Visibility.Collapsed; RunPlan(); };
        }

        public void ShowProfile(Prof p)
        {
            planProfile = p;
            var tweaks = TweakCatalog.ForProfile(p).ToList();
            foreach (var t in tweaks) t.RefreshState();
            planItems = tweaks.Where(t => !t.IsApplied).Select(t => new PlanItem { Tweak = t, Selected = t.CanToggle }).ToList();
            foreach (var i in planItems) i.Changed = UpdatePlanCount;
            F<TextBlock>("PlanTitle").Text = "Profil « " + SysInfo.ProfileName(p) + " »";
            F<TextBlock>("PlanSub").Text = planItems.Count == 0
                ? "Tous les réglages de ce profil sont déjà en place."
                : "Décochez ce que vous préférez garder. Tout pourra être annulé depuis l'Historique.";
            F<ItemsControl>("PlanList").ItemsSource = planItems;
            F<CheckBox>("PlanRestore").IsEnabled = Os.IsAdmin;
            F<CheckBox>("PlanRestore").IsChecked = Os.IsAdmin;
            UpdatePlanCount();
            F<Grid>("PlanLayer").Visibility = Visibility.Visible;
        }

        void UpdatePlanCount()
        {
            var n = planItems.Count(i => i.Selected);
            F<TextBlock>("PlanCount").Text = n + " réglage" + (n > 1 ? "s" : "") + " sélectionné" + (n > 1 ? "s" : "");
            F<Button>("PlanYes").IsEnabled = n > 0 || F<CheckBox>("PlanClean").IsChecked == true;
        }

        async void RunPlan()
        {
            var chosen = planItems.Where(i => i.Selected).Select(i => i.Tweak).ToList();
            var restore = F<CheckBox>("PlanRestore").IsChecked == true;
            var clean = F<CheckBox>("PlanClean").IsChecked == true;
            var name = SysInfo.ProfileName(planProfile);
            var maxEffect = Effect.Immediate;
            await RunJob("Optimisation : " + name, true, job =>
            {
                // Mesure de référence, pour pouvoir juger du gain dans Diagnostic.
                job.Status("Mesure de l'état actuel");
                try { Store.AddMetric(Metrics.Capture("Avant « " + name + " »", job.Token)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { job.Log("Mesure impossible : " + e.Message); }

                if (restore && chosen.Count > 0)
                {
                    job.Status("Création d'un point de restauration");
                    TweakEngine.CreateRestorePoint("Avant OptiWin : " + name, job.Log);
                }
                int ok = 0, ko = 0;
                for (int i = 0; i < chosen.Count; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    var t = chosen[i];
                    job.Status(t.Title);
                    job.Progress(100.0 * i / Math.Max(1, chosen.Count) * (clean ? 0.6 : 1));
                    try
                    {
                        TweakEngine.Apply(t);
                        ok++;
                        if (t.Effect > maxEffect) maxEffect = t.Effect;
                    }
                    catch (SettingProtectedException) { ko++; job.Log("  Windows protège ce réglage contre les programmes : faites-le dans les Paramètres de Windows. Il ne sera plus proposé."); }
                    catch (Exception e) { ko++; job.Log("  Échec : " + e.Message); }
                }
                long freed = 0;
                if (clean)
                {
                    var cats = Cleaner.BuildCatalog().Where(c => c.Selected && c.Available && !c.Permanent && !c.SizeUnknown).ToList();
                    for (int i = 0; i < cats.Count; i++)
                    {
                        job.Token.ThrowIfCancellationRequested();
                        job.Status(cats[i].Title);
                        job.Progress(60 + 40.0 * i / cats.Count);
                        try { var n = cats[i].Cleaner(job); freed += n; job.Log("  " + Fmt.Size(n) + " libérés"); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { job.Log("  " + e.Message); }
                    }
                }
                job.Progress(100);
                var parts = new List<string>();
                if (ok > 0) parts.Add(ok + " réglage" + (ok > 1 ? "s" : "") + " appliqué" + (ok > 1 ? "s" : ""));
                if (ko > 0) parts.Add(ko + " en échec");
                if (clean) parts.Add(Fmt.Size(freed) + " libérés");
                job.Summary = string.Join(", ", parts) + "." + (maxEffect == Effect.Reboot ? " Redémarrez pour tout activer." : maxEffect == Effect.Logoff ? " Reconnectez-vous pour tout activer." : "");
            }, delegate { var o = CurrentOptimize(); if (o != null) o.NotePending(maxEffect); });
            Page home;
            if (pages.TryGetValue("home", out home)) ((HomePage)home).RefreshAnalysis();
        }

        // ──────── Tâches longues ────────
        Job activeJob;
        readonly ConcurrentQueue<string> logQueue = new ConcurrentQueue<string>();
        string pendingStatus, pendingDetail;
        double pendingProgress = -2;
        DispatcherTimer jobTimer;

        void WireJob()
        {
            F<Button>("JobCancel").Click += delegate
            {
                if (activeJob != null) { activeJob.Cancel(); F<TextBlock>("JobStatus").Text = "Annulation en cours…"; F<Button>("JobCancel").IsEnabled = false; }
            };
            F<Button>("JobClose").Click += delegate { F<Grid>("JobLayer").Visibility = Visibility.Collapsed; };
            jobTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            jobTimer.Tick += delegate { FlushJob(); };
        }

        void FlushJob()
        {
            var log = F<TextBox>("JobLog");
            var b = new System.Text.StringBuilder();
            string l;
            while (logQueue.TryDequeue(out l)) b.AppendLine(l);
            if (b.Length > 0)
            {
                if (log.Text.Length > 300000) log.Text = log.Text.Substring(log.Text.Length - 150000);
                log.AppendText(b.ToString());
                log.ScrollToEnd();
            }
            var st = Interlocked.Exchange(ref pendingStatus, null);
            if (st != null) F<TextBlock>("JobStatus").Text = st;
            var de = Interlocked.Exchange(ref pendingDetail, null);
            if (de != null) F<TextBlock>("JobDetail").Text = de;
            var p = pendingProgress;
            if (p != -2)
            {
                var bar = F<ProgressBar>("JobBar");
                bar.IsIndeterminate = p < 0;
                if (p >= 0) bar.Value = p;
                pendingProgress = -2;
            }
        }

        /// <summary>Exécute une tâche en arrière-plan, avec progression, journal et annulation.</summary>
        public async Task<bool> RunJob(string title, bool cancellable, Action<Job> work, Action<bool> after = null)
        {
            if (jobRunning) { Toast("Une opération est déjà en cours.", "warn"); return false; }
            jobRunning = true;
            var job = new Job();
            activeJob = job;
            job.OnLog += s => logQueue.Enqueue(s);
            job.OnStatus += s => pendingStatus = s;
            job.OnDetail += s => pendingDetail = s;
            job.OnProgress += p => pendingProgress = p;

            F<TextBlock>("JobTitle").Text = title;
            F<TextBlock>("JobStatus").Text = "Préparation…";
            F<TextBlock>("JobDetail").Text = "";
            F<TextBlock>("JobResult").Text = "";
            F<TextBox>("JobLog").Clear();
            var bar = F<ProgressBar>("JobBar");
            bar.Foreground = (Brush)Application.Current.Resources["Accent"];
            bar.IsIndeterminate = true;
            var cancel = F<Button>("JobCancel");
            cancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
            cancel.IsEnabled = true;
            F<Button>("JobClose").IsEnabled = false;
            F<Grid>("JobLayer").Visibility = Visibility.Visible;
            jobTimer.Start();

            bool ok = false;
            string msg;
            try
            {
                await Task.Factory.StartNew(() => work(job), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                ok = true;
                msg = job.Summary ?? "Terminé.";
            }
            catch (OperationCanceledException) { msg = "Annulé."; }
            catch (Exception e)
            {
                Logger.Error(title, e);
                job.Log("Erreur : " + e.Message);
                msg = e.Message;
            }
            jobRunning = false;
            activeJob = null;
            FlushJob();
            jobTimer.Stop();
            bar.IsIndeterminate = false;
            if (ok) bar.Value = 100;
            else bar.Foreground = (Brush)Application.Current.Resources["Danger"];
            F<TextBlock>("JobStatus").Text = ok ? "Terminé" : "Interrompu";
            F<TextBlock>("JobDetail").Text = "";
            var res = F<TextBlock>("JobResult");
            res.Text = msg;
            res.Foreground = (Brush)Application.Current.Resources[ok ? "Ok" : "Danger"];
            cancel.Visibility = Visibility.Collapsed;
            F<Button>("JobClose").IsEnabled = true;
            F<Button>("JobClose").Focus();
            if (after != null) try { after(ok); } catch (Exception e) { Logger.Error("Après tâche", e); }
            return ok;
        }

        /// <summary>Affiche des dialogues avec des données d'exemple, pour les captures de contrôle.</summary>
        public void DemoJob()
        {
            var job = F<Grid>("JobLayer");
            F<TextBlock>("JobTitle").Text = "Création de la clé";
            F<TextBlock>("JobStatus").Text = "Copie des fichiers de Windows";
            F<TextBlock>("JobDetail").Text = "38 Mo/s — reste 2 min 14 s";
            var bar = F<ProgressBar>("JobBar"); bar.IsIndeterminate = false; bar.Value = 46;
            F<TextBox>("JobLog").Text = "› Montage de l'image\n› Vérification du périphérique\n› Partitionnement et formatage (MBR, FAT32)\nDiskPart a correctement formaté le volume.\n› Copie des fichiers de Windows\n";
            job.Visibility = Visibility.Visible;
        }
        public void HideDemo()
        {
            F<Grid>("JobLayer").Visibility = Visibility.Collapsed;
            F<Grid>("PlanLayer").Visibility = Visibility.Collapsed;
            F<Grid>("ConfirmLayer").Visibility = Visibility.Collapsed;
        }
    }
}
