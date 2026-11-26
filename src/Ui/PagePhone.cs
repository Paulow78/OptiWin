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
    // ─────────────────────────── Téléphone Android ───────────────────────────
    /// <summary>Optimiser, débloater et nettoyer un smartphone Android par câble USB ou en Wi-Fi / Réseau.</summary>
    public sealed class PhonePage : Page
    {
        readonly TextBox host, pairHost, pairCode, planSearch, appsSearch;
        readonly ComboBox found;
        readonly TextBlock status, phoneName, planTitle, planSub, disabledEmpty, animInfo, dnsInfo, appsSub, appsEmpty;
        readonly ItemsControl facts, planList, disabledList, thirdAppsList;
        readonly Button btnToWifi, btnClean, btnRestore, btnAnim1, btnAnim05, btnAnim025, btnAnim0;
        readonly FrameworkElement panelUsb, panelWifi, pairBox, deviceCard, workspace, panelDebloat, panelTune, panelApps;
        string adb, serial, currentAnim = "1", currentDns = "opportunistic";
        PhoneInfo phone;
        List<TvItem> plan = new List<TvItem>();
        List<string> thirdApps = new List<string>();

        public PhonePage()
        {
            View = (FrameworkElement)Ui.Load("PagePhone.xaml");
            host = F<TextBox>("PhoneHost"); pairHost = F<TextBox>("PhonePairHost"); pairCode = F<TextBox>("PhonePairCode");
            planSearch = F<TextBox>("PhonePlanSearch"); appsSearch = F<TextBox>("PhoneAppsSearch");
            found = F<ComboBox>("PhoneFound");
            status = F<TextBlock>("PhoneStatus"); phoneName = F<TextBlock>("PhoneName"); planTitle = F<TextBlock>("PhonePlanTitle"); planSub = F<TextBlock>("PhonePlanSub");
            disabledEmpty = F<TextBlock>("PhoneDisabledEmpty"); animInfo = F<TextBlock>("PhoneAnimInfo"); dnsInfo = F<TextBlock>("PhoneDnsInfo");
            appsSub = F<TextBlock>("PhoneAppsSub"); appsEmpty = F<TextBlock>("PhoneAppsEmpty");
            facts = F<ItemsControl>("PhoneFacts"); planList = F<ItemsControl>("PhonePlan"); disabledList = F<ItemsControl>("PhoneDisabled"); thirdAppsList = F<ItemsControl>("PhoneThirdApps");
            btnToWifi = F<Button>("BtnPhoneToWifi"); btnClean = F<Button>("BtnPhoneClean"); btnRestore = F<Button>("BtnPhoneRestore");
            btnAnim1 = F<Button>("BtnPhoneAnim1"); btnAnim05 = F<Button>("BtnPhoneAnim05"); btnAnim025 = F<Button>("BtnPhoneAnim025"); btnAnim0 = F<Button>("BtnPhoneAnim0");
            panelUsb = F<FrameworkElement>("PanelPhoneUsb"); panelWifi = F<FrameworkElement>("PanelPhoneWifi"); pairBox = F<FrameworkElement>("PhonePairBox");
            deviceCard = F<FrameworkElement>("PhoneDeviceCard"); workspace = F<FrameworkElement>("PhoneWorkspace");
            panelDebloat = F<FrameworkElement>("PanelPhoneDebloat"); panelTune = F<FrameworkElement>("PanelPhoneTune"); panelApps = F<FrameworkElement>("PanelPhoneApps");

            F<RadioButton>("PhoneModeUsb").Checked += delegate { panelUsb.Visibility = Visibility.Visible; panelWifi.Visibility = Visibility.Collapsed; };
            F<RadioButton>("PhoneModeWifi").Checked += delegate { panelUsb.Visibility = Visibility.Collapsed; panelWifi.Visibility = Visibility.Visible; };

            F<Button>("BtnPhoneConnectUsb").Click += delegate { Connect(""); };
            btnToWifi.Click += delegate { SwitchToWifi(); };
            F<Button>("BtnPhoneConnectWifi").Click += delegate { Connect((host.Text ?? "").Trim()); };
            F<Button>("BtnPhoneScan").Click += delegate { Scan(); };
            F<Button>("BtnPhonePairToggle").Click += delegate { pairBox.Visibility = pairBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
            F<Button>("BtnPhonePair").Click += delegate { Pair(); };

            F<Button>("BtnPhoneQuickOpt").Click += delegate { QuickOptimize(); };
            F<Button>("BtnPhoneCaches").Click += delegate { Caches(); };
            F<Button>("BtnPhoneKill").Click += delegate { KillBackground(); };
            F<Button>("BtnPhoneShot").Click += delegate { CaptureScreen(); };
            F<Button>("BtnPhoneReboot").Click += delegate { Reboot(); };

            F<Button>("BtnPhoneRec").Click += delegate { foreach (var i in plan) i.Selected = i.Recommended; };
            F<Button>("BtnPhoneAll").Click += delegate { foreach (var i in plan) i.Selected = true; };
            F<Button>("BtnPhoneNone").Click += delegate { foreach (var i in plan) i.Selected = false; };
            F<Button>("BtnPhoneAnalyze").Click += delegate { Reanalyze("Analyse du téléphone", null); };
            btnClean.Click += delegate { Clean(); };
            btnRestore.Click += delegate { RestoreAll(); };

            btnAnim1.Click += delegate { SetAnim("1"); };
            btnAnim05.Click += delegate { SetAnim("0.5"); };
            btnAnim025.Click += delegate { SetAnim("0.25"); };
            btnAnim0.Click += delegate { SetAnim("0"); };

            F<Button>("BtnPhoneDnsAdguard").Click += delegate { SetDns("hostname", "dns.adguard-dns.com", "AdGuard (anti-pub)"); };
            F<Button>("BtnPhoneDnsCf").Click += delegate { SetDns("hostname", "one.one.one.one", "Cloudflare"); };
            F<Button>("BtnPhoneDnsQuad9").Click += delegate { SetDns("hostname", "dns.quad9.net", "Quad9"); };
            F<Button>("BtnPhoneDnsAuto").Click += delegate { SetDns("opportunistic", null, "Automatique"); };
            F<Button>("BtnPhoneDexOpt").Click += delegate { OptimizeApps(); };
            F<Button>("BtnPhoneApk").Click += delegate { InstallApk(); };
            F<Button>("BtnPhoneAppsRefresh").Click += delegate { LoadThirdPartyApps(); };

            F<RadioButton>("TabPhoneDebloat").Checked += delegate { ShowTab(0); };
            F<RadioButton>("TabPhoneTune").Checked += delegate { ShowTab(1); };
            F<RadioButton>("TabPhoneApps").Checked += delegate { ShowTab(2); if (Ready(false) && thirdApps.Count == 0) LoadThirdPartyApps(); };

            Ui.Debounce(planSearch, 120, FilterPlan);
            Ui.Debounce(appsSearch, 120, FilterThirdApps);
            found.SelectionChanged += delegate { var s = found.SelectedItem as string; if (s != null) host.Text = s; };
            host.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Connect((host.Text ?? "").Trim()); } };
        }

        void ShowTab(int index)
        {
            panelDebloat.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            panelTune.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            panelApps.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        public override bool OnFind()
        {
            if (workspace.Visibility == Visibility.Visible && panelDebloat.Visibility == Visibility.Visible) { planSearch.Focus(); planSearch.SelectAll(); return true; }
            if (panelWifi.Visibility == Visibility.Visible) { host.Focus(); host.SelectAll(); return true; }
            return false;
        }

        public override void OnRefresh() { if (Ready(false)) Reanalyze("Analyse du téléphone", null); }

        bool Ready(bool tell = true)
        {
            if (adb != null && serial != null && phone != null) return true;
            if (tell) Shell.I.Toast("Connectez d'abord un smartphone Android (USB ou Wi-Fi).", "warn");
            return false;
        }

        async Task<bool> ConfirmAdbIfMissing()
        {
            if (Adb.Find() != null) return true;
            return await Shell.I.Confirm("Télécharger les outils Android de Google ?",
                "Pour dialoguer avec votre smartphone, OptiTech utilise adb, l'outil officiel de Google (Android SDK Platform-Tools, ~7 Mo). Il sera téléchargé en HTTPS directement depuis Google (dl.google.com) dans le dossier d'OptiTech.",
                "Télécharger", false);
        }

        async void Pair()
        {
            var h = (pairHost.Text ?? "").Trim();
            var c = (pairCode.Text ?? "").Trim();
            if (!Adb.IsValidHost(h) || !h.Contains(":")) { Shell.I.Toast("Indiquez l'IP et le port d'appairage (ex. 192.168.1.25:37421).", "warn"); return; }
            if (!Adb.IsValidPairCode(c)) { Shell.I.Toast("Saisissez le code à 6 chiffres affiché sur le téléphone.", "warn"); return; }
            if (!await ConfirmAdbIfMissing()) return;
            var ok = await Shell.I.RunJob("Appairage sans fil Android", true, job =>
            {
                var exe = Adb.Ensure(job);
                Adb.Pair(exe, h, c, job);
                job.Summary = "Smartphone appairé ! Indiquez maintenant l'adresse IP:port de connexion (écran « Débogage sans fil »), puis cliquez sur Connecter en Wi-Fi.";
            });
            if (ok) { pairCode.Text = ""; status.Text = "Appairage réussi : entrez l'IP:port de connexion ci-dessus puis cliquez sur Connecter en Wi-Fi."; }
        }

        async void Scan()
        {
            List<string> list = null;
            var ok = await Shell.I.RunJob("Recherche d'appareils Android sur le réseau", true, job =>
            {
                job.Status("Recherche des appareils ouverts sur le port 5555");
                list = Adb.Scan(job);
                job.Summary = list.Count == 0 ? "Aucun appareil trouvé sur le port 5555. Pour Android 11+, utilisez l'appairage par code à 6 chiffres, ou branchez d'abord en USB puis cliquez sur « Passer en Wi-Fi »."
                    : Fmt.N(list.Count, "appareil trouvé", "appareils trouvés") + " : " + string.Join(", ", list) + ".";
            });
            if (!ok || list == null) return;
            found.ItemsSource = list;
            found.Visibility = list.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count >= 1) { host.Text = list[0]; if (list.Count > 1) found.SelectedIndex = 0; }
            status.Text = list.Count == 0 ? "Aucun appareil trouvé sur le port 5555." : "Appareil trouvé : cliquez sur Connecter en Wi-Fi.";
        }

        async void Connect(string address)
        {
            if (address.Length > 0 && !Adb.IsValidHost(address)) { Shell.I.Toast("Adresse invalide : ex. 192.168.1.25:41234 ou 192.168.1.25.", "warn"); return; }
            if (!await ConfirmAdbIfMissing()) return;
            string exe = null, id = null, anim = "1", dns = "opportunistic"; PhoneInfo info = null; List<TvItem> items = null;
            var ok = await Shell.I.RunJob(address.Length == 0 ? "Connexion USB au smartphone" : "Connexion Wi-Fi au smartphone", true, job =>
            {
                exe = Adb.Ensure(job);
                id = TvOptimizer.Connect(exe, address, job);
                job.Status("Lecture du smartphone (batterie, stockage, applications)");
                info = PhoneOptimizer.ReadInfo(exe, id, job.Token);
                items = PhoneOptimizer.Analyze(exe, id, job.Token);
                anim = Adb.GetAnimScale(exe, id, job.Token);
                dns = Adb.GetPrivateDns(exe, id, job.Token);
                job.Progress(100);
                job.Summary = "Connecté à " + info.Name + ". " + (items.Count == 0 ? "Aucun bloatware connu détecté." : Fmt.N(items.Count, "élément") + " à examiner : rien n'a encore été modifié.");
            });
            if (!ok || info == null) { status.Text = "Connexion non établie. Consultez le journal pour le détail."; return; }
            adb = exe; serial = id; phone = info; currentAnim = anim; currentDns = dns;
            thirdApps = new List<string>();
            status.Text = "Connecté à " + phone.Name + " (" + (serial.Contains(":") ? "Wi-Fi" : "USB") + ").";
            ShowDevice();
            ShowPlan(items);
        }

        async void SwitchToWifi()
        {
            if (!Ready()) return;
            if (serial.Contains(":")) { Shell.I.Toast("Ce smartphone est déjà connecté en Wi-Fi.", "info"); return; }
            string wifiSerial = null;
            var ok = await Shell.I.RunJob("Passage du téléphone en Wi-Fi", true, job =>
            {
                wifiSerial = Adb.EnableTcpIp(adb, serial, job);
                job.Summary = "Connexion Wi-Fi établie sur " + wifiSerial + ". Vous pouvez débrancher le câble USB !";
            });
            if (!ok || wifiSerial == null) return;
            serial = wifiSerial;
            host.Text = wifiSerial;
            status.Text = "Connecté en Wi-Fi à " + phone.Name + " (" + wifiSerial + "). Vous pouvez débrancher le câble USB.";
            ShowDevice();
        }

        void ShowDevice()
        {
            phoneName.Text = phone.Name.Length > 0 ? phone.Name : "Smartphone Android";
            var f = new List<KeyValuePair<string, string>>();
            var sys = "Android " + phone.Android + (phone.Sdk > 0 ? " (API " + phone.Sdk + ")" : "");
            if (phone.SecurityPatch.Length > 0) sys += "  ·  Patch " + phone.SecurityPatch;
            f.Add(new KeyValuePair<string, string>("Système", sys));
            if (phone.BatteryText.Length > 0) f.Add(new KeyValuePair<string, string>("Batterie", phone.BatteryText));
            if (phone.StorageTotal > 0) f.Add(new KeyValuePair<string, string>("Stockage", Fmt.Size(phone.StorageFree) + " libres sur " + Fmt.Size(phone.StorageTotal)));
            if (phone.RamTotal > 0) f.Add(new KeyValuePair<string, string>("Mémoire", (phone.RamFree >= 0 ? Fmt.Size(phone.RamFree) + " dispo sur " : "") + Fmt.Size(phone.RamTotal)));
            if (phone.Soc.Length > 0 || phone.Resolution.Length > 0)
                f.Add(new KeyValuePair<string, string>("Matériel", string.Join("  ·  ", new[] { phone.Soc, phone.Resolution }.Where(x => x.Length > 0))));
            f.Add(new KeyValuePair<string, string>("Liaison", serial + (serial.Contains(":") ? " (Wi-Fi)" : " (USB)")));
            facts.ItemsSource = f;
            btnToWifi.IsEnabled = !serial.Contains(":");
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
                + (phone != null && phone.Sdk > 0 && phone.Sdk < 28 ? " (requiert Android 9+)" : "");
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
            planTitle.Text = plan.Count == 0 ? "Aucun bloatware connu actif sur ce smartphone" : Fmt.N(plan.Count, "élément") + " à examiner";
            planSub.Text = plan.Count == 0 ? "Votre téléphone est déjà très propre d'après la liste d'OptiTech."
                : Fmt.N(n, "élément", "sélectionné", "sélectionnés") + ". Appels, SMS, contacts, caméra, clavier, lanceur actif et Play Store sont toujours protégés.";
            btnClean.IsEnabled = n > 0;
        }

        void ShowDisabled()
        {
            var list = phone == null ? new List<PhoneDisabledEntry>() : PhoneOptimizer.DisabledByUs(phone.Id);
            disabledList.ItemsSource = list.Select(e =>
            {
                var entry = e;
                return new TvDisabledRow
                {
                    Package = entry.Package + (entry.WasUninstalled ? "  (retiré pour utilisateur 0)" : "  (désactivé)"),
                    Label = PhoneOptimizer.LabelOf(entry.Package),
                    Enable = new RelayCommand(() => RestoreOne(entry))
                };
            }).ToList();
            disabledEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            btnRestore.IsEnabled = list.Count > 0;
        }

        async void Reanalyze(string title, Action<Job> before)
        {
            if (!Ready()) return;
            PhoneInfo info = null; List<TvItem> items = null; string anim = currentAnim, dns = currentDns;
            var ok = await Shell.I.RunJob(title, true, job =>
            {
                if (before != null) before(job);
                var summary = job.Summary;
                job.Status("Actualisation des infos du smartphone");
                info = PhoneOptimizer.ReadInfo(adb, serial, job.Token);
                items = PhoneOptimizer.Analyze(adb, serial, job.Token);
                anim = Adb.GetAnimScale(adb, serial, job.Token);
                dns = Adb.GetPrivateDns(adb, serial, job.Token);
                job.Progress(100);
                job.Summary = summary ?? (items.Count == 0 ? "Aucun bloatware connu actif." : Fmt.N(items.Count, "élément") + " à examiner.");
            });
            if (!ok || info == null) return;
            info.Serial = phone.Serial; info.Brand = phone.Brand; info.Model = phone.Model;
            phone = info; currentAnim = anim; currentDns = dns;
            ShowDevice();
            ShowPlan(items);
        }

        async void QuickOptimize()
        {
            if (!Ready()) return;
            var rec = plan.Where(i => i.Recommended).ToList();
            var ok = await Shell.I.Confirm("Optimiser " + phone.Name + " en 1 clic ?",
                "Cette opération va :\n• Alléger " + Fmt.N(rec.Count, "bloatware recommandé", "bloatwares recommandés") + " (publicités, télémétrie, installeurs fantômes)\n• Accélérer les animations à 0,5×\n• Vider tous les caches d'applications\n• Fermer les tâches inactives en arrière-plan\n\nTout reste 100 % réversible depuis cette page.",
                "Optimiser maintenant", false);
            if (!ok) return;
            Reanalyze("Optimisation rapide du smartphone", job =>
            {
                int done = 0;
                if (rec.Count > 0) done = PhoneOptimizer.Disable(adb, serial, phone, rec, job);
                job.Status("Accélération des animations (0,5×)");
                Adb.SetAnimScale(adb, serial, "phone.anim." + phone.Id, "Téléphone : " + phone.Name, "0.5", job);
                job.Status("Vidage des caches");
                var freed = TvOptimizer.TrimCaches(adb, serial, job);
                job.Status("Fermeture de l'arrière-plan");
                Adb.Shell(adb, serial, "am kill-all", job.Token);
                job.Summary = Fmt.N(done, "bloatware allégé", "bloatwares allégés") + ", animations à 0,5×" + (freed > 0 ? ", " + Fmt.Size(freed) + " libérés" : "") + ".";
            });
        }

        async void Clean()
        {
            if (!Ready()) return;
            var sel = plan.Where(i => i.Selected).ToList();
            if (sel.Count == 0) return;
            var ok = await Shell.I.Confirm("Alléger " + Fmt.N(sel.Count, "élément") + " sur " + phone.Name + " ?",
                string.Join(", ", sel.Select(i => i.Label)) + ".\n\nTout est mémorisé et réversible en un clic depuis la section « Allégé par OptiTech ».", "Alléger", false);
            if (!ok) return;
            var ramBefore = phone.RamFree;
            Reanalyze("Débloatage du smartphone", job =>
            {
                var n = PhoneOptimizer.Disable(adb, serial, phone, sel, job);
                var ramAfter = TvOptimizer.ParseMeminfo(Adb.Shell(adb, serial, "cat /proc/meminfo", job.Token), "MemAvailable");
                job.Summary = Fmt.N(n, "élément allégé", "éléments allégés") + " sur " + sel.Count + "."
                    + (ramBefore > 0 && ramAfter > 0 ? " RAM disponible : " + Fmt.Size(ramBefore) + " avant, " + Fmt.Size(ramAfter) + " après." : "");
            });
        }

        void RestoreOne(PhoneDisabledEntry entry)
        {
            if (!Ready()) return;
            Reanalyze("Rétablissement", job =>
            {
                var ok = PhoneOptimizer.RestoreOne(adb, serial, phone, entry, job);
                job.Summary = ok ? PhoneOptimizer.LabelOf(entry.Package) + " est rétabli." : "Le téléphone a refusé : voir le journal.";
            });
        }

        async void RestoreAll()
        {
            if (!Ready()) return;
            var ok = await Shell.I.Confirm("Tout rétablir sur " + phone.Name + " ?", "Toutes les applications désactivées ou retirées par OptiTech sur ce smartphone seront rétablies.", "Tout rétablir", false);
            if (!ok) return;
            Reanalyze("Rétablissement du smartphone", job =>
            {
                var n = PhoneOptimizer.RestoreAll(adb, serial, phone, job);
                job.Summary = Fmt.N(n, "application rétablie", "applications rétablies") + ".";
            });
        }

        void Caches()
        {
            if (!Ready()) return;
            Reanalyze("Vidage des caches du smartphone", job =>
            {
                job.Status("Vidage des caches de toutes les applications");
                var freed = TvOptimizer.TrimCaches(adb, serial, job);
                job.Summary = freed < 0 ? "Caches vidés." : freed == 0 ? "Caches vidés : très peu de données temporaires à libérer." : Fmt.Size(freed) + " libérés sur le smartphone.";
            });
        }

        void KillBackground()
        {
            if (!Ready()) return;
            Reanalyze("Fermeture de l'arrière-plan", job =>
            {
                Adb.Shell(adb, serial, "am kill-all", job.Token);
                job.Summary = "Applications inactives fermées.";
            });
        }

        async void SetAnim(string scale)
        {
            if (!Ready()) return;
            var ok = await Shell.I.RunJob("Vitesse des animations", false, job =>
            {
                Adb.SetAnimScale(adb, serial, "phone.anim." + phone.Id, "Téléphone : " + phone.Name, scale, job);
                job.Summary = scale == "1" ? "Animations d'origine rétablies." : "Animations réglées à " + scale + "×.";
            });
            if (ok) { currentAnim = scale; UpdateTuningStatus(); }
        }

        async void SetDns(string mode, string hostName, string label)
        {
            if (!Ready()) return;
            if (phone.Sdk > 0 && phone.Sdk < 28) { Shell.I.Toast("Le DNS privé chiffré requiert Android 9 (API 28) ou supérieur.", "warn"); return; }
            var ok = await Shell.I.RunJob("DNS privé : " + label, false, job =>
            {
                Adb.SetPrivateDns(adb, serial, "phone.dns." + phone.Id, "Téléphone : " + phone.Name, mode, hostName, job);
                job.Summary = "DNS privé configuré sur " + label + ".";
            });
            if (ok) { currentDns = mode == "hostname" ? hostName : mode; UpdateTuningStatus(); }
        }

        void OptimizeApps()
        {
            if (!Ready()) return;
            Reanalyze("Optimisation ART / DEX du smartphone", job =>
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
                Store.AddHistory("phone:" + phone.Id, "Téléphone : " + phone.Name, "APK installé : " + Path.GetFileName(path));
                job.Summary = Path.GetFileName(path) + " installé sur " + phone.Name + ".";
            });
            if (panelApps.Visibility == Visibility.Visible) LoadThirdPartyApps();
        }

        async void CaptureScreen()
        {
            if (!Ready()) return;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OptiTech-Phone-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png");
            var ok = await Shell.I.RunJob("Capture d'écran du smartphone", true, job =>
            {
                job.Status("Capture de l'écran en cours");
                Adb.CaptureScreenshot(adb, serial, path, job.Token);
                job.Summary = "Capture enregistrée : " + path;
            });
            if (ok && File.Exists(path)) try { Process.Start(path); } catch { }
        }

        void LoadThirdPartyApps()
        {
            if (!Ready(false)) return;
            var exe = adb; var id = serial;
            appsSub.Text = "Chargement des applications tierces…";
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
            if (!await Shell.I.Confirm("Redémarrer " + phone.Name + " ?", "Le smartphone redémarre tout de suite. Il faudra le reconnecter ensuite.", "Redémarrer", false)) return;
            var exe = adb; var id = serial;
            await Shell.I.RunJob("Redémarrage du smartphone", false, job =>
            {
                Adb.Out(exe, "-s " + id + " reboot", job.Token);
                job.Summary = "Redémarrage demandé. Reconnectez le téléphone une fois déverrouillé.";
            });
            adb = null; serial = null; phone = null;
            deviceCard.Visibility = workspace.Visibility = Visibility.Collapsed;
            btnToWifi.IsEnabled = false;
            status.Text = "Le smartphone redémarre.";
        }
    }
}
