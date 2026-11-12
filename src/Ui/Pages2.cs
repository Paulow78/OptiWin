using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace OptiWin
{
    // ─────────────────────────── Réinstaller ───────────────────────────
    public sealed class ReinstallPage : Page
    {
        readonly TextBlock exportInfo, importInfo, planTitle, planSub;
        readonly Border planCard;
        readonly ItemsControl planRows;
        readonly Button btnApply;
        readonly CheckBox chkRestore;
        List<PlanRow> rows = new List<PlanRow>();

        public ReinstallPage()
        {
            View = (FrameworkElement)Ui.Load("PageReinstall.xaml");
            exportInfo = F<TextBlock>("ExportInfo"); importInfo = F<TextBlock>("ImportInfo"); planTitle = F<TextBlock>("PlanTitle"); planSub = F<TextBlock>("PlanSub");
            planCard = F<Border>("PlanCard"); planRows = F<ItemsControl>("PlanRows"); btnApply = F<Button>("BtnApply"); chkRestore = F<CheckBox>("ChkRestore");
            chkRestore.IsEnabled = Os.IsAdmin;
            chkRestore.IsChecked = Os.IsAdmin;
            F<Button>("BtnExport").Click += delegate { Export(); };
            F<Button>("BtnImport").Click += delegate { Import(); };
            F<Button>("BtnDrivers").Click += delegate { Shell.I.Go("tools"); };
            btnApply.Click += delegate { Apply(); };
        }

        async void Export()
        {
            var name = "OptiWin-profil-" + Environment.MachineName + "-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json";
            var path = Dialogs.PickSave("Enregistrer le profil", "Profil OptiWin (*.json)|*.json", name);
            if (path == null) return;
            PcProfile p = null;
            var ok = await Shell.I.RunJob("Création du profil", true, job =>
            {
                p = PcProfiles.Capture(job);
                PcProfiles.Save(p, path);
                job.Summary = "Profil enregistré : " + p.Summary + ".";
            });
            if (ok) exportInfo.Text = Path.GetFileName(path) + " : " + p.Summary + ".";
        }

        async void Import()
        {
            var path = Dialogs.PickOpen("Ouvrir un profil OptiWin", "Profil OptiWin (*.json)|*.json|Tous les fichiers (*.*)|*.*");
            if (path == null) return;
            PcProfile p;
            try { p = PcProfiles.Load(path); }
            catch (Exception e) { Shell.I.Toast(e.Message, "bad"); return; }
            List<PlanRow> plan = null;
            var ok = await Shell.I.RunJob("Comparaison du profil avec ce PC", true, job =>
            {
                plan = PcProfiles.BuildPlan(p, job);
                job.Summary = plan.Count == 0 ? "Ce PC est déjà conforme à ce profil." : plan.Count + " changements possibles.";
            });
            if (!ok) return;
            importInfo.Text = Path.GetFileName(path) + (p.Machine != null ? ", créé sur " + p.Machine : "");
            ShowPlan(plan);
        }

        void ShowPlan(List<PlanRow> plan)
        {
            rows = plan;
            planCard.Visibility = plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var cv = new ListCollectionView(rows);
            cv.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            planRows.ItemsSource = cv;
            foreach (var r in rows) r.PropertyChanged += delegate { UpdateCount(); };
            planTitle.Text = "Ce que ce profil changerait sur ce PC";
            UpdateCount();
            if (plan.Count == 0) Shell.I.Toast("Ce PC est déjà conforme à ce profil.");
        }

        void UpdateCount()
        {
            var n = rows.Count(r => r.Selected && r.Available);
            var locked = rows.Count(r => !r.Available);
            planSub.Text = n + " sélectionné" + (n > 1 ? "s" : "") + " sur " + rows.Count + (locked > 0 ? ". " + locked + " demande" + (locked > 1 ? "nt" : "") + " le mode administrateur." : ".");
            btnApply.IsEnabled = n > 0;
        }

        async void Apply()
        {
            var n = rows.Count(r => r.Selected && r.Available);
            var soft = rows.Count(r => r.Selected && r.Available && r.Group == "Logiciels à installer");
            var body = n + " changement" + (n > 1 ? "s" : "") + " vont être appliqués." + (soft > 0 ? "\n\nInstaller un logiciel vaut acceptation de la licence de son éditeur." : "");
            if (!await Shell.I.Confirm("Appliquer ce profil ?", body, "Appliquer", false)) return;
            var restore = chkRestore.IsChecked == true;
            var plan = rows;
            await Shell.I.RunJob("Application du profil", true, job => PcProfiles.Apply(plan, restore, job));
            ShowPlan(new List<PlanRow>());
            importInfo.Text = "Profil appliqué. Un redémarrage termine l'opération.";
        }
    }

    // ─────────────────────────── Diagnostic ───────────────────────────
    public sealed class DiagnosticPage : Page
    {
        readonly ComboBox baseCombo, afterCombo;
        readonly StackPanel table;
        readonly ItemsControl metricRows, diskList, procList;
        readonly TextBlock note, empty, diskStatus, procStatus;
        readonly Button btnMeasure, btnClear, btnProcs;
        bool disksLoaded, filling, procsBusy;

        public DiagnosticPage()
        {
            View = (FrameworkElement)Ui.Load("PageDiagnostic.xaml");
            baseCombo = F<ComboBox>("BaseCombo"); afterCombo = F<ComboBox>("AfterCombo"); table = F<StackPanel>("MetricTable");
            metricRows = F<ItemsControl>("MetricRows"); diskList = F<ItemsControl>("DiskList"); procList = F<ItemsControl>("ProcList");
            note = F<TextBlock>("MetricNote"); empty = F<TextBlock>("MetricEmpty"); diskStatus = F<TextBlock>("DiskStatus"); procStatus = F<TextBlock>("ProcStatus");
            btnMeasure = F<Button>("BtnMeasure"); btnClear = F<Button>("BtnClearMetrics"); btnProcs = F<Button>("BtnProcs");
            btnProcs.Click += delegate { LoadProcs(); };
            baseCombo.SelectionChanged += delegate { if (!filling) Compare(); };
            afterCombo.SelectionChanged += delegate { if (!filling) Compare(); };
            btnMeasure.Click += delegate { Measure(); };
            btnClear.Click += async delegate
            {
                if (!await Shell.I.Confirm("Effacer les mesures ?", "L'historique des mesures sera supprimé. Vos réglages ne sont pas concernés.", "Effacer", false)) return;
                Store.ClearMetrics();
                Reload();
            };
            F<Button>("BtnDisks").Click += delegate { LoadDisks(); };
        }

        public override void OnShow()
        {
            Reload();
            LoadProcs();
            if (!disksLoaded) LoadDisks();
        }
        public override void OnRefresh() { Reload(); LoadProcs(); LoadDisks(); }

        /// <summary>Les programmes qui pèsent le plus en mémoire, instances regroupées.</summary>
        void LoadProcs()
        {
            if (procsBusy) return;
            procsBusy = true;
            btnProcs.IsEnabled = false;
            Task.Factory.StartNew(() => TopProcesses.Read(8)).ContinueWith(t =>
            {
                procsBusy = false;
                btnProcs.IsEnabled = true;
                if (t.IsFaulted) { procStatus.Text = t.Exception.GetBaseException().Message; return; }
                procList.ItemsSource = t.Result;
                procStatus.Text = "Mémoire de travail à " + DateTime.Now.ToString("HH:mm:ss") + ", pages partagées comprises : un total peut dépasser la mémoire réellement occupée.";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Reload()
        {
            var list = Store.Metrics();
            filling = true;
            baseCombo.ItemsSource = list;
            afterCombo.ItemsSource = list;
            baseCombo.SelectedIndex = list.Count >= 2 ? list.Count - 2 : list.Count - 1;
            afterCombo.SelectedIndex = list.Count - 1;
            filling = false;
            btnClear.IsEnabled = list.Count > 0;
            baseCombo.IsEnabled = afterCombo.IsEnabled = list.Count > 1;
            Compare();
        }

        void Compare()
        {
            var a = baseCombo.SelectedItem as MetricSnapshot;
            var b = afterCombo.SelectedItem as MetricSnapshot;
            var has = a != null && b != null;
            table.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has) return;
            metricRows.ItemsSource = Metrics.Compare(a, b);
            note.Text = ReferenceEquals(a, b)
                ? "Première mesure enregistrée. Reprenez-en une après vos changements et un redémarrage pour voir l'écart."
                : Metrics.FairnessNote(a, b);
        }

        async void Measure()
        {
            btnMeasure.IsEnabled = false;
            try
            {
                var s = await Task.Factory.StartNew(() => Metrics.Capture("Mesure", CancellationToken.None));
                Store.AddMetric(s);
                Reload();
                Shell.I.Toast("Mesure enregistrée.");
            }
            catch (Exception e) { Shell.I.Toast("Mesure impossible : " + e.Message, "bad"); }
            btnMeasure.IsEnabled = true;
        }

        void LoadDisks()
        {
            disksLoaded = true;
            diskStatus.Text = "Lecture des disques…";
            Task.Factory.StartNew(() => { string err; return DiskHealthReader.Read(out err); }).ContinueWith(t =>
            {
                if (t.IsFaulted) { diskStatus.Text = "Lecture impossible : " + t.Exception.GetBaseException().Message; return; }
                diskList.ItemsSource = t.Result;
                diskStatus.Text = t.Result.Count == 0 ? "Aucun disque n'a pu être lu." : "";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    // ─────────────────────────── USB : téléchargement de Windows ───────────────────────────
    public sealed class WinDownloadPanel
    {
        readonly FrameworkElement view;
        readonly ComboBox product, lang, arch;
        readonly TextBlock info;
        readonly TextBox output;
        readonly CheckBox homePro;
        readonly Button btnRead, btnGo;
        readonly Action<string> onIsoReady;
        List<EsdFile> files = new List<EsdFile>();
        // Un emplacement choisi à la main n'est plus écrasé quand la langue ou l'architecture change.
        bool outputCustom;

        T F<T>(string n) where T : class
        {
            var o = view.FindName(n) as T;
            if (o == null) throw new InvalidOperationException("Élément d'interface manquant : " + n);
            return o;
        }

        public WinDownloadPanel(FrameworkElement view, Action<string> onIsoReady)
        {
            this.view = view; this.onIsoReady = onIsoReady;
            product = F<ComboBox>("DlProduct"); lang = F<ComboBox>("DlLang"); arch = F<ComboBox>("DlArch");
            info = F<TextBlock>("DlInfo"); output = F<TextBox>("DlOut"); homePro = F<CheckBox>("DlHomePro");
            btnRead = F<Button>("BtnDlRead"); btnGo = F<Button>("BtnDlGo");
            product.ItemsSource = WinDownload.Products;
            product.SelectedIndex = 0;
            product.SelectionChanged += delegate { files = new List<EsdFile>(); lang.ItemsSource = null; arch.ItemsSource = null; outputCustom = false; UpdateInfo(); };
            lang.SelectionChanged += delegate { UpdateArch(); };
            arch.SelectionChanged += delegate { UpdateInfo(); };
            btnRead.Click += delegate { ReadCatalog(); };
            btnGo.Click += delegate { Go(); };
            F<Button>("BtnDlOut").Click += delegate
            {
                var p = Dialogs.PickSave("Enregistrer l'ISO", "Image ISO (*.iso)|*.iso", output.Text.Length > 0 ? Path.GetFileName(output.Text) : "Windows.iso");
                if (p != null) { output.Text = p; outputCustom = true; }
            };
            UpdateInfo();
        }

        EsdFile Current()
        {
            var l = lang.SelectedItem as LanguageChoice;
            var a = arch.SelectedItem as string;
            return l == null || a == null ? null : WinDownload.Choose(files, l.Code, a);
        }

        async void ReadCatalog()
        {
            var p = product.SelectedItem as WinProduct;
            if (p == null) return;
            List<EsdFile> list = null;
            var ok = await Shell.I.RunJob("Catalogue de " + p.Name, true, job =>
            {
                list = WinDownload.FetchCatalog(p, job);
                job.Summary = "Catalogue lu : " + list.Select(f => f.LanguageCode).Distinct().Count() + " langues disponibles.";
            });
            if (!ok) return;
            files = list;
            var langs = WinDownload.Languages(files);
            lang.ItemsSource = langs;
            var mine = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
            lang.SelectedItem = langs.FirstOrDefault(x => x.Code == mine) ?? langs.FirstOrDefault(x => x.Code.StartsWith(mine.Split('-')[0])) ?? langs.FirstOrDefault();
        }

        void UpdateArch()
        {
            var l = lang.SelectedItem as LanguageChoice;
            arch.ItemsSource = l == null ? null : WinDownload.Architectures(files, l.Code);
            if (arch.Items.Count > 0) arch.SelectedIndex = 0;
            UpdateInfo();
        }

        void UpdateInfo()
        {
            var f = Current();
            var p = product.SelectedItem as WinProduct;
            btnGo.IsEnabled = f != null && Os.IsAdmin;
            if (f == null)
            {
                info.Text = "Lisez d'abord le catalogue : c'est un petit fichier de Microsoft, qui liste les images disponibles.";
                return;
            }
            info.Text = p.Name + ", build " + f.Build + " — téléchargement de " + Fmt.Size(f.Size) + ", " + Fmt.Size(WinDownload.SpaceNeeded(f)) + " d'espace libre requis le temps de l'opération."
                + " Ce catalogue peut avoir une version de retard : Windows Update fera le reste après l'installation."
                + (Os.IsAdmin ? "" : " Créer l'ISO demande le mode administrateur.");
            if (!outputCustom)
                output.Text = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), p.IsoLabel + "-" + f.LanguageCode + "-" + f.Arch + ".iso");
        }

        async void Go()
        {
            var f = Current();
            var p = product.SelectedItem as WinProduct;
            var path = output.Text.Trim();
            if (f == null || p == null) return;
            if (path.Length == 0) { Shell.I.Toast("Indiquez où enregistrer l'ISO.", "warn"); return; }
            var only = homePro.IsChecked == true;
            var ok = await Shell.I.RunJob("Téléchargement de " + p.Name, true, job => WinDownload.DownloadAndBuild(f, path, p.IsoLabel, only, job));
            if (ok && File.Exists(path)) onIsoReady(path);
        }
    }

    // ─────────────────────────── USB : multiboot ───────────────────────────
    public sealed class ImageRow
    {
        public string FullPath;
        public string Name { get; set; }
        public string SizeText { get; set; }
        public ICommand Remove { get; set; }
    }

    public sealed class MultibootPanel
    {
        readonly FrameworkElement view;
        readonly ObservableCollection<ImageRow> images = new ObservableCollection<ImageRow>();
        readonly RadioButton modeNew, modeExisting;
        readonly ComboBox disk, key;
        readonly CheckBox gpt;
        readonly TextBlock total, info;
        readonly Button btnGo;
        readonly FrameworkElement newBox, existingBox;

        T F<T>(string n) where T : class
        {
            var o = view.FindName(n) as T;
            if (o == null) throw new InvalidOperationException("Élément d'interface manquant : " + n);
            return o;
        }

        public MultibootPanel(FrameworkElement view)
        {
            this.view = view;
            modeNew = F<RadioButton>("MultiNew"); modeExisting = F<RadioButton>("MultiExisting");
            disk = F<ComboBox>("MultiDisk"); key = F<ComboBox>("MultiKey"); gpt = F<CheckBox>("MultiGpt");
            total = F<TextBlock>("MultiTotal"); info = F<TextBlock>("MultiInfo"); btnGo = F<Button>("BtnMultiGo");
            newBox = F<FrameworkElement>("MultiNewBox"); existingBox = F<FrameworkElement>("MultiExistingBox");
            F<ItemsControl>("MultiList").ItemsSource = images;
            F<Button>("BtnMultiAdd").Click += delegate { Add(); };
            F<Button>("BtnMultiRefresh").Click += delegate { Refresh(); };
            modeNew.Checked += delegate { UpdateMode(); };
            modeExisting.Checked += delegate { UpdateMode(); };
            disk.SelectionChanged += delegate { UpdateMode(); };
            key.SelectionChanged += delegate { UpdateMode(); };
            btnGo.Click += delegate { Go(); };
            UpdateTotal();
        }

        void Add()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Ajouter des images", Filter = "Images disque (*.iso;*.img;*.wim;*.vhd;*.vhdx)|*.iso;*.img;*.wim;*.vhd;*.vhdx|Tous les fichiers (*.*)|*.*", Multiselect = true };
            if (d.ShowDialog(Shell.I.Win) != true) return;
            foreach (var p in d.FileNames)
            {
                if (images.Any(i => string.Equals(i.FullPath, p, StringComparison.OrdinalIgnoreCase))) continue;
                ImageRow row = null;
                row = new ImageRow { FullPath = p, Name = Path.GetFileName(p), SizeText = Fmt.Size(new FileInfo(p).Length), Remove = new RelayCommand(() => { images.Remove(row); UpdateTotal(); }) };
                images.Add(row);
            }
            UpdateTotal();
        }

        void UpdateTotal()
        {
            total.Text = images.Count == 0 ? "Aucune image pour l'instant : vous pourrez aussi les copier plus tard, comme de simples fichiers." :
                images.Count + " image" + (images.Count > 1 ? "s" : "") + ", " + Fmt.Size(images.Sum(i => new FileInfo(i.FullPath).Length));
            UpdateMode();
        }

        public void Refresh()
        {
            Task.Factory.StartNew(() => new { Disks = Disks.List(false), Keys = Multiboot.ExistingKeys() }).ContinueWith(t =>
            {
                if (t.IsFaulted) { info.Text = t.Exception.GetBaseException().Message; return; }
                disk.ItemsSource = t.Result.Disks;
                disk.SelectedIndex = t.Result.Disks.Count > 0 ? 0 : -1;
                key.ItemsSource = t.Result.Keys;
                key.SelectedIndex = t.Result.Keys.Count > 0 ? 0 : -1;
                if (t.Result.Keys.Count > 0 && t.Result.Disks.Count == 0) modeExisting.IsChecked = true;
                UpdateMode();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateMode()
        {
            var isNew = modeNew.IsChecked == true;
            newBox.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            existingBox.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
            if (isNew)
            {
                info.Text = !Os.IsAdmin ? "Préparer une clé demande le mode administrateur."
                    : disk.SelectedItem == null ? "Aucune clé USB détectée. Branchez-en une puis actualisez."
                    : "Toutes les données de la clé seront effacées.";
                btnGo.IsEnabled = Os.IsAdmin && disk.SelectedItem != null;
                btnGo.Style = (Style)Application.Current.Resources["BtnDanger"];
            }
            else
            {
                info.Text = key.SelectedItem == null ? "Aucune clé Ventoy détectée." : "Rien n'est effacé : les images sont simplement ajoutées.";
                btnGo.IsEnabled = key.SelectedItem != null && images.Count > 0;
                btnGo.Style = (Style)Application.Current.Resources["BtnPrimary"];
            }
        }

        async void Go()
        {
            var list = images.Select(i => i.FullPath).ToList();
            if (modeExisting.IsChecked == true)
            {
                var root = key.SelectedItem as string;
                if (root == null) return;
                await Shell.I.RunJob("Ajout d'images sur la clé", true, job =>
                {
                    Multiboot.CopyImages(root, list, job);
                    RawImage.FlushVolume(root.Substring(0, 2));
                    job.Summary = list.Count + " image" + (list.Count > 1 ? "s" : "") + " ajoutée" + (list.Count > 1 ? "s" : "") + " sur " + root;
                }, delegate { Refresh(); });
                return;
            }

            var d = disk.SelectedItem as UsbDisk;
            if (d == null) return;
            var letters = d.Letters.Count > 0 ? " (lecteur " + string.Join(", ", d.Letters) + ")" : "";
            var ok = await Shell.I.Confirm("Effacer " + d.Model + " ?",
                "Tout le contenu de ce périphérique de " + Fmt.Size(d.Size) + letters + " sera définitivement effacé, puis remplacé par une clé multiboot Ventoy.\n\nVentoy est un logiciel libre tiers, récupéré via winget s'il n'est pas déjà présent. Le récupérer vaut acceptation de sa licence.",
                "Effacer et préparer la clé", true, "EFFACER");
            if (!ok) return;
            var useGpt = gpt.IsChecked == true;
            await Shell.I.RunJob("Création de la clé multiboot", true, job =>
            {
                var exe = Multiboot.EnsureVentoy(job);
                var root = Multiboot.Install(d, useGpt, exe, job);
                if (list.Count > 0) Multiboot.CopyImages(root, list, job);
                RawImage.FlushVolume(root.Substring(0, 2));
                job.Progress(100);
                job.Summary = "Clé multiboot prête sur " + root + ". Copiez-y d'autres ISO quand vous voulez. Avec Secure Boot, le premier démarrage demande d'enregistrer la clé de Ventoy : suivez l'écran.";
            }, delegate { Refresh(); });
        }
    }
}
