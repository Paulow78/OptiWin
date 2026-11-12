using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("OptiWin")]
[assembly: AssemblyProduct("OptiWin")]
[assembly: AssemblyDescription("Optimisation de Windows et création de clés bootables")]
[assembly: AssemblyVersion("26.11.1.0")]
[assembly: AssemblyFileVersion("26.11.1.0")]
[assembly: AssemblyInformationalVersion("26.11.1")]

namespace OptiWin
{
    public static class Program
    {
        [DllImport("kernel32")] static extern bool AttachConsole(int pid);
        // Conservé pendant toute la vie du processus : relâché, il laisserait démarrer une seconde instance.
        static System.Threading.Mutex instanceMutex;

        [STAThread]
        public static int Main(string[] args)
        {
            // Avant tout accès à Paths, et avant que Start soit compilé à la volée.
            // Les rendus de contrôle sont isolés aussi : ils ne doivent ni lire ni écrire les données de l'utilisateur.
            if (Array.Exists(args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--screenshot", StringComparison.OrdinalIgnoreCase)))
                Environment.SetEnvironmentVariable("OPTIWIN_DATA", Path.Combine(Path.GetTempPath(), "OptiWin-selftest-" + Process.GetCurrentProcess().Id));

            try { return Start(args); }
            catch (Exception e)
            {
                // Dernier rempart : une erreur au démarrage doit se lire, pas juste faire disparaître la fenêtre.
                Logger.Error("Démarrage impossible", e);
                Console.Error.WriteLine(e);
                if (!args.Contains("--screenshot"))
                    MessageBox.Show("OptiWin n'a pas pu démarrer :\n\n" + e.GetBaseException().Message + "\n\nDétails dans " + Paths.Logs, "OptiWin", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
        }

        static int Start(string[] args)
        {
            var has = new Func<string, bool>(a => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase)));
            Func<string, string> val = a =>
            {
                var i = Array.FindIndex(args, x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            };

            if (has("--selftest"))
            {
                AttachConsole(-1);
                return SelfTest.Run(val("--log"), has("--big"), has("--online"));
            }

            if (has("--maintenance"))
            {
                var job = new Job();
                var freed = Cleaner.Maintenance(job);
                Store.AddHistory("maintenance", "Entretien automatique", Fmt.Size(freed) + " libérés");
                return 0;
            }

            var screenshot = val("--screenshot");
            var limited = has("--limited") || screenshot != null;

            // Demande les droits au démarrage ; en cas de refus, l'application reste utile en lecture.
            if (!limited && !has("--elevated") && !Os.IsAdmin)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Paths.Exe) { UseShellExecute = true, Verb = "runas", Arguments = "--elevated" });
                    return 0;
                }
                catch (System.ComponentModel.Win32Exception) { }
            }

            // Une seule fenêtre à la fois : deux instances écriraient le même state.json chacune de son côté.
            // Le nom distingue le mode limité du mode administrateur : l'élévation lance la seconde pendant que la première vit encore.
            bool first;
            instanceMutex = new System.Threading.Mutex(true, @"Local\OptiWin-" + (has("--elevated") || Os.IsAdmin ? "admin" : "user"), out first);
            if (!first && screenshot == null)
            {
                SingleInstance.Activate();
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Logger.Error("Erreur non gérée", e.Exception);
                if (screenshot != null) { Console.Error.WriteLine(e.Exception); File.WriteAllText(Path.Combine(screenshot, "ERREUR.txt"), e.Exception.ToString()); Environment.Exit(3); }
                MessageBox.Show("Une erreur inattendue est survenue :\n\n" + e.Exception.Message + "\n\nDétails dans " + Paths.Logs, "OptiWin", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };

            var theme = val("--theme") ?? Store.Setting("theme");
            Themes.Apply(theme == null ? Themes.SystemPrefersDark() : theme == "dark");
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Ui.Load("Theme.xaml"));

            if (screenshot != null && has("--showcase")) Os.ShowcaseAdmin();
            var shell = new Shell(screenshot != null);
            if (screenshot != null)
            {
                Directory.CreateDirectory(screenshot);
                var w = shell.Win;
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -20000; w.Top = -20000; w.ShowInTaskbar = false; w.ShowActivated = false;
                w.Width = 1280; w.Height = 860;
                w.ContentRendered += delegate { Screens(shell, screenshot, has("--mobile")); };
            }
            // Le contexte de synchronisation de WPF n'existe qu'une fois la boucle de messages lancée.
            shell.Win.Loaded += delegate { shell.Go("home"); };
            app.MainWindow = shell.Win;
            shell.Win.Show();
            var code = app.Run();
            if (screenshot != null) try { Directory.Delete(Paths.Data, true); } catch { }
            return code;
        }

        // Rend chaque page dans un PNG, sans rien modifier : sert à contrôler visuellement l'interface.
        static async void Screens(Shell sh, string dir, bool quick)
        {
            try
            {
                sh.Root.SetResourceReference(Panel.BackgroundProperty, "WinBg");
                await Task.Delay(9000);
                Save(sh, dir, "1-accueil");
                sh.Go("optimize"); await Task.Delay(2000); Save(sh, dir, "2-optimiser");
                sh.ShowProfile(Prof.OldPc); await Task.Delay(500); Save(sh, dir, "2b-plan"); sh.HideDemo();
                sh.Go("clean"); await Task.Delay(9000); Save(sh, dir, "3-nettoyer");
                sh.Go("startup"); await Task.Delay(2000); Save(sh, dir, "4-demarrage");
                sh.Go("apps"); await Task.Delay(5000); Save(sh, dir, "5-applications");
                var usb = sh.PageOf<UsbPage>("usb"); await Task.Delay(3000); Save(sh, dir, "6-usb");
                usb.DemoSetTab(1); await Task.Delay(300); Save(sh, dir, "6b-iso");
                usb.DemoSetTab(2); await Task.Delay(300); Save(sh, dir, "6c-iso-perso");
                usb.DemoSetTab(3); await Task.Delay(300); Save(sh, dir, "6g-multiboot");
                usb.DemoSetTab(4); await Task.Delay(300); Save(sh, dir, "6d-telecharger");
                usb.DemoSetTab(5); await Task.Delay(300); Save(sh, dir, "6e-verifier");
                usb.DemoSetTab(0);
                sh.Go("reinstall"); await Task.Delay(500); Save(sh, dir, "5b-reinstaller");
                sh.Go("diagnostic"); await Task.Delay(5000); Save(sh, dir, "6h-diagnostic");
                sh.DemoJob(); await Task.Delay(400); Save(sh, dir, "6f-tache"); sh.HideDemo();
                sh.Go("tools"); await Task.Delay(2500); Save(sh, dir, "7-outils");
                sh.Go("history"); await Task.Delay(800); Save(sh, dir, "8-historique");
                // Déroulé réel d'une tâche, réussie puis en échec, et de la confirmation destructrice : aucun effet sur le système.
                var flow = sh.RunJob("Contrôle d'une tâche", true, j => { j.Status("Étape de contrôle"); j.Progress(40); j.Log("Ligne de journal"); System.Threading.Thread.Sleep(500); j.Summary = "Tâche terminée sans erreur."; });
                await flow; await Task.Delay(300); Save(sh, dir, "9a-tache-reussie"); sh.HideDemo();
                await sh.RunJob("Contrôle d'un échec", true, j => { j.Status("Étape de contrôle"); throw new InvalidOperationException("Erreur simulée pour le contrôle."); });
                await Task.Delay(300); Save(sh, dir, "9b-tache-echec"); sh.HideDemo();
                var pending = sh.Confirm("Effacer SanDisk Ultra ?", "Tout le contenu de ce périphérique de 28,6 Go sera définitivement effacé.", "Effacer et créer la clé", true, "EFFACER");
                await Task.Delay(300); Save(sh, dir, "9c-confirmation"); sh.HideDemo();
                sh.Toast("Appliqué : exemple de message."); await Task.Delay(300); Save(sh, dir, "9d-message");
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "ERREUR.txt"), e.ToString()); }
            Application.Current.Shutdown();
        }

        static void Save(Shell sh, string dir, string name)
        {
            var r = sh.Root;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(r.ActualWidth), (int)Math.Ceiling(r.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(r);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(f);
        }
    }

    /// <summary>Ramène au premier plan la fenêtre d'OptiWin déjà ouverte, plutôt que d'en ouvrir une seconde.</summary>
    static class SingleInstance
    {
        [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32")] static extern bool IsIconic(IntPtr h);

        public static void Activate()
        {
            try
            {
                var me = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                {
                    try
                    {
                        if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                        if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(p.MainWindowHandle);
                        return;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
    }
}
