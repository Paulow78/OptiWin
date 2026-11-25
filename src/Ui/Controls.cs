using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OptiTech
{
    public static class Ui
    {
        public static object Load(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("OptiTech.ui." + name))
            {
                if (s == null) throw new FileNotFoundException("Ressource absente : " + name);
                return XamlReader.Load(s);
            }
        }

        /// <summary>
        /// Mode léger : coupe les animations, le flou et les halos translucides pour garder une interface
        /// instantanée sur les vieux PC ou sans accélération matérielle.
        /// </summary>
        public static bool Lightweight
        {
            get
            {
                var v = Store.Setting("ui.lightweight");
                if (v == "1") return true;
                if (v == "0") return false;
                try { return !SystemParameters.ClientAreaAnimation || (RenderCapability.Tier >> 16) == 0; }
                catch { return false; }
            }
            set { Store.SetSetting("ui.lightweight", value ? "1" : "0"); }
        }

        /// <summary>Déclenche une recherche une fois la frappe terminée, pour éviter de reconstruire la liste à chaque lettre.</summary>
        public static System.Windows.Threading.DispatcherTimer Debounce(System.Windows.Controls.TextBox box, int ms, Action action)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); action(); };
            box.TextChanged += delegate { t.Stop(); t.Start(); };
            return t;
        }
    }

    public abstract class Page
    {
        public FrameworkElement View { get; protected set; }
        protected T F<T>(string n) where T : class
        {
            var o = View.FindName(n) as T;
            if (o == null) throw new InvalidOperationException("Élément d'interface manquant : " + n);
            return o;
        }
        public virtual void OnShow() { }
        public virtual void OnHide() { }
        /// <summary>F5 : recharge ce que la page affiche.</summary>
        public virtual void OnRefresh() { }
        /// <summary>Ctrl+F : donne le focus au champ de recherche de la page, s'il y en a un.</summary>
        public virtual bool OnFind() { return false; }
    }

    /// <summary>Taille, position et état de la fenêtre, retrouvés d'une session à l'autre.</summary>
    public static class WindowMemory
    {
        const string Key = "win.bounds";

        public static void Restore(Window w)
        {
            var v = Store.Setting(Key);
            if (v == null) return;
            var p = v.Split(',');
            double x, y, cw, ch;
            if (p.Length < 5 || !TryD(p[0], out x) || !TryD(p[1], out y) || !TryD(p[2], out cw) || !TryD(p[3], out ch)) return;
            if (cw < w.MinWidth || ch < w.MinHeight) return;
            // La fenêtre doit rester attrapable : un écran débranché depuis ne doit pas la faire disparaître.
            var left = SystemParameters.VirtualScreenLeft; var top = SystemParameters.VirtualScreenTop;
            var right = left + SystemParameters.VirtualScreenWidth; var bottom = top + SystemParameters.VirtualScreenHeight;
            if (x + cw < left + 120 || x > right - 120 || y < top - 10 || y > bottom - 120) return;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = x; w.Top = y; w.Width = cw; w.Height = ch;
            if (p[4] == "1") w.WindowState = WindowState.Maximized;
        }

        public static void Save(Window w)
        {
            try
            {
                var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
                if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;
                Store.SetSetting(Key, string.Join(",", new[] { S(b.X), S(b.Y), S(b.Width), S(b.Height), w.WindowState == WindowState.Maximized ? "1" : "0" }));
            }
            catch (Exception e) { Logger.Error("Mémorisation de la fenêtre", e); }
        }

        static string S(double d) { return Math.Round(d).ToString(CultureInfo.InvariantCulture); }
        static bool TryD(string s, out double d) { return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d); }
    }

    /// <summary>Jauge circulaire du score de santé. Les stylos sont figés et mis en cache pour animer à 60 i/s sans allocation.</summary>
    public sealed class RingGauge : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender, OnPenPropChanged));
        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register("Track", typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, OnPenPropChanged));
        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register("Thickness", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPenPropChanged));
        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public Brush Stroke { get { return (Brush)GetValue(StrokeProperty); } set { SetValue(StrokeProperty, value); } }
        public Brush Track { get { return (Brush)GetValue(TrackProperty); } set { SetValue(TrackProperty, value); } }
        public double Thickness { get { return (double)GetValue(ThicknessProperty); } set { SetValue(ThicknessProperty, value); } }

        Pen trackPen, strokePen;
        static void OnPenPropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var g = (RingGauge)d;
            g.trackPen = null; g.strokePen = null;
        }

        public void AnimateTo(double v)
        {
            if (Ui.Lightweight) { BeginAnimation(ValueProperty, null); Value = v; return; }
            BeginAnimation(ValueProperty, new DoubleAnimation(v, new Duration(TimeSpan.FromMilliseconds(650))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        protected override void OnRender(DrawingContext dc)
        {
            var t = Thickness;
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= t) return;
            var r = (size - t) / 2;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            if (trackPen == null && Track != null)
            {
                trackPen = new Pen(Track, t);
                if (trackPen.CanFreeze) trackPen.Freeze();
            }
            if (trackPen != null) dc.DrawEllipse(null, trackPen, c, r, r);
            var v = Math.Max(0, Math.Min(100, Value));
            if (v <= 0) return;
            if (strokePen == null && Stroke != null)
            {
                strokePen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (strokePen.CanFreeze) strokePen.Freeze();
            }
            if (strokePen == null) return;
            if (v >= 99.9) { dc.DrawEllipse(null, strokePen, c, r, r); return; }
            var a = v / 100 * 2 * Math.PI;
            var start = new Point(c.X, c.Y - r);
            var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
            var g = new StreamGeometry();
            using (var x = g.Open())
            {
                x.BeginFigure(start, false, false);
                x.ArcTo(end, new Size(r, r), 0, v > 50, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, strokePen, g);
        }
    }

    /// <summary>Courbe lissée en direct, dessinée sans graphe d'objets et avec pinceaux mis en cache pour rester très légère.</summary>
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender, OnStrokeChanged));
        public Brush Stroke { get { return (Brush)GetValue(StrokeProperty); } set { SetValue(StrokeProperty, value); } }
        const int N = 60;
        readonly double[] data = new double[N];
        readonly Point[] pts = new Point[N];
        int count;

        Pen linePen, gridPen;
        Brush areaBrush, haloBrush;
        static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var s = (Sparkline)d;
            s.linePen = null; s.areaBrush = null; s.haloBrush = null; s.gridPen = null;
        }

        public void Push(double v)
        {
            v = Math.Max(0, Math.Min(100, v));
            if (count < N) data[count++] = v;
            else { Array.Copy(data, 1, data, 0, N - 1); data[N - 1] = v; }
            InvalidateVisual();
        }

        void EnsureBrushes()
        {
            if (linePen != null || Stroke == null) return;
            linePen = new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (linePen.CanFreeze) linePen.Freeze();
            var solid = Stroke as SolidColorBrush;
            if (solid != null)
            {
                var col = solid.Color;
                var g = new LinearGradientBrush(Color.FromArgb(68, col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B), 90);
                g.Freeze();
                areaBrush = g;
                var h = new SolidColorBrush(Color.FromArgb(72, col.R, col.G, col.B));
                h.Freeze();
                haloBrush = h;
                var gp = new Pen(new SolidColorBrush(Color.FromArgb(24, col.R, col.G, col.B)), 1) { DashStyle = DashStyles.Dot };
                gp.Freeze();
                gridPen = gp;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth; var h = ActualHeight;
            if (w <= 2 || h <= 2) return;
            EnsureBrushes();
            if (gridPen != null) dc.DrawLine(gridPen, new Point(0, h * 0.5), new Point(w, h * 0.5));
            if (count < 2) return;

            // Étale proprement la courbe dès les premiers échantillons, puis défile en continu une fois pleine.
            var padRight = 3.5;
            var usableW = Math.Max(1, w - padRight);
            var span = Math.Max(count - 1, 14);
            var offset = span - (count - 1);
            for (int i = 0; i < count; i++)
            {
                var x = usableW * (offset + i) / span;
                var y = h - 2.5 - (h - 5.5) * data[i] / 100.0;
                pts[i] = new Point(x, y);
            }

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lx = line.Open())
            using (var ax = area.Open())
            {
                lx.BeginFigure(pts[0], false, false);
                ax.BeginFigure(new Point(pts[0].X, h), true, true);
                ax.LineTo(pts[0], false, false);
                if (count == 2)
                {
                    lx.LineTo(pts[1], true, true);
                    ax.LineTo(pts[1], false, false);
                }
                else
                {
                    for (int i = 1; i < count - 1; i++)
                    {
                        var mid = new Point((pts[i].X + pts[i + 1].X) * 0.5, (pts[i].Y + pts[i + 1].Y) * 0.5);
                        lx.QuadraticBezierTo(pts[i], mid, true, true);
                        ax.QuadraticBezierTo(pts[i], mid, false, false);
                    }
                    var last = pts[count - 1];
                    lx.LineTo(last, true, true);
                    ax.LineTo(last, false, false);
                }
                ax.LineTo(new Point(pts[count - 1].X, h), false, false);
            }
            line.Freeze();
            area.Freeze();

            if (areaBrush != null) dc.DrawGeometry(areaBrush, null, area);
            if (linePen != null) dc.DrawGeometry(null, linePen, line);
            var tip = pts[count - 1];
            if (haloBrush != null) dc.DrawEllipse(haloBrush, null, tip, 4.8, 4.8);
            if (Stroke != null) dc.DrawEllipse(Stroke, null, tip, 2.3, 2.3);
        }
    }

    public static class Themes
    {
        // Verre liquide (Liquid Glass) : fond nuit profonde avec halos colorés et surfaces vitrées
        // à reflets spéculaires, tout en restant léger (aucun flou par carte, uniquement des dégradés gelés).
        static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
        {
            {"WinBg","#04060C"},{"SidebarBg","#48070B16"},{"ContentBg","#54090E1B"},{"SurfaceHover","#2CFFFFFF"},{"SurfaceAlt","#4A050812"},
            {"Control","#1FFFFFFF"},{"DialogBg","#EC0B101E"},{"Stroke","#24FFFFFF"},{"StrokeStrong","#4EFFFFFF"},{"Text","#F5F7FC"},{"TextMuted","#B4BDD0"},{"TextFaint","#7E89A0"},
            {"Accent","#48E8C7"},{"AccentSoft","#2648E8C7"},{"OnAccent","#031D17"},{"HoverLayer","#1AFFFFFF"},{"SelectedLayer","#26FFFFFF"},
            {"Warn","#FFBD59"},{"WarnSoft","#28FFBD59"},{"Danger","#FF6B72"},{"DangerSoft","#28FF6B72"},{"OnDanger","#2B0407"},
            {"Info","#69B7FF"},{"InfoSoft","#2469B7FF"},{"Ok","#48E8C7"},{"OkSoft","#2448E8C7"},{"ThumbBg","#42FFFFFF"},{"ScrimBg","#8802040A"},
            {"KbdBg","#1CFFFFFF"},
        };
        static readonly Dictionary<string, string> Light = new Dictionary<string, string>
        {
            {"WinBg","#E5EBF7"},{"SidebarBg","#52FFFFFF"},{"ContentBg","#72FFFFFF"},{"SurfaceHover","#241B2A4A"},{"SurfaceAlt","#98F5F8FD"},
            {"Control","#DCFFFFFF"},{"DialogBg","#F6FFFFFF"},{"Stroke","#241B2A4A"},{"StrokeStrong","#4E1B2A4A"},{"Text","#0F1422"},{"TextMuted","#4A5469"},{"TextFaint","#758096"},
            {"Accent","#008770"},{"AccentSoft","#20008770"},{"OnAccent","#FFFFFF"},{"HoverLayer","#10000000"},{"SelectedLayer","#D8FFFFFF"},
            {"Warn","#A85F00"},{"WarnSoft","#20E08A00"},{"Danger","#C72B35"},{"DangerSoft","#1EC72B35"},{"OnDanger","#FFFFFF"},
            {"Info","#1565C0"},{"InfoSoft","#1C1565C0"},{"Ok","#008770"},{"OkSoft","#1C008770"},{"ThumbBg","#4D1B2A4A"},{"ScrimBg","#520E1626"},
            {"KbdBg","#141B2A4A"},
        };

        public static bool IsDark { get; private set; }
        /// <summary>Le fond de la fenêtre est fourni par Windows (acrylique ou Mica) : le nôtre devient transparent.</summary>
        public static bool Mica;

        public static void Apply(bool dark)
        {
            IsDark = dark;
            var light = Ui.Lightweight;
            var res = Application.Current.Resources;
            var map = dark ? Dark : Light;
            foreach (var kv in map) res[kv.Key] = Make(kv.Value);
            if (Mica && !light)
            {
                res["WinBg"] = Brushes.Transparent;
                res["SidebarBg"] = Make(dark ? "#42060912" : "#55FFFFFF");
                res["ContentBg"] = Make(dark ? "#7A070B15" : "#86F4F7FC");
            }
            var accent = Hex(map["Accent"]);
            var blue = Hex(dark ? "#4B93FF" : "#256CE0");
            var violet = Hex(dark ? "#8F64FF" : "#7A52E0");
            var rose = Hex(dark ? "#E855B5" : "#D64BA3");
            var cyan = Hex(dark ? "#22D3EE" : "#0891B2");
            res["AccentColor"] = accent;

            if (light)
            {
                // En mode léger : surfaces opaques et nettes, sans calcul de transparence ni halos.
                res["SidebarBg"] = Make(dark ? "#0A0D16" : "#EAEFF8");
                res["ContentBg"] = Make(dark ? "#0F1320" : "#F4F7FC");
                res["Surface"] = Make(dark ? "#161B2B" : "#FFFFFF");
                res["SurfaceAlt"] = Make(dark ? "#111522" : "#EDF2F9");
                res["GlassBorder"] = Make(dark ? "#2A334C" : "#D0D9EA");
                res["GlassBorderSoft"] = Make(dark ? "#232B42" : "#DCE3F0");
                res["CardSheen"] = Brushes.Transparent;
                res["Control"] = Make(dark ? "#1D2438" : "#FFFFFF");
            }
            else
            {
                // Verre liquide façon Apple : reflet lumineux en haut à gauche, corps givré translucide, légère teinte froide en bas.
                res["Surface"] = LinearStops(new Point(0, 0), new Point(1, 1),
                    new GradientStop(Color.FromArgb((byte)(dark ? 0x26 : 0xE6), 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb((byte)(dark ? 0x13 : 0xBD), 236, 244, 255), 0.45),
                    new GradientStop(Color.FromArgb((byte)(dark ? 0x0C : 0xA2), 215, 228, 255), 1.0));
                // Arête spéculaire : éclat blanc net en haut à gauche, qui s'efface puis reflète subtilement l'accent en bas à droite.
                res["GlassBorder"] = LinearStops(new Point(0, 0), new Point(1, 1),
                    new GradientStop(White((byte)(dark ? 0x82 : 0xFF)), 0.0),
                    new GradientStop(White((byte)(dark ? 0x2C : 0xB8)), 0.28),
                    new GradientStop(White((byte)(dark ? 0x14 : 0x7A)), 0.72),
                    new GradientStop(Color.FromArgb((byte)(dark ? 0x4C : 0x55), accent.R, accent.G, accent.B), 1.0));
                res["GlassBorderSoft"] = LinearStops(new Point(0, 0), new Point(1, 1),
                    new GradientStop(White((byte)(dark ? 0x4A : 0xEA)), 0.0),
                    new GradientStop(White((byte)(dark ? 0x16 : 0x76)), 0.55),
                    new GradientStop(Color.FromArgb((byte)(dark ? 0x2E : 0x38), blue.R, blue.G, blue.B), 1.0));
                // Reflet supérieur discret qui donne l'épaisseur du verre sans aucun flou coûteux.
                res["CardSheen"] = LinearStops(new Point(0, 0), new Point(1, 0),
                    new GradientStop(White(0x00), 0.0),
                    new GradientStop(White((byte)(dark ? 0x52 : 0xCC)), 0.22),
                    new GradientStop(White((byte)(dark ? 0x24 : 0x88)), 0.65),
                    new GradientStop(White(0x00), 1.0));
            }
            res["AccentGradient"] = Linear(new Point(0, 0), new Point(1, 1), accent, blue);
            res["LogoGradient"] = Linear(new Point(0, 0), new Point(1, 1), Hex("#48E8C7"), Hex("#4B93FF"), Hex("#9A6BFF"));
            res["GlassSheen"] = Linear(new Point(0, 0), new Point(0, 1), White(0x78), White(0x00));

            // Halos colorés profonds derrière le verre (nébuleuse sombre : menthe, saphir, violet, touche magenta).
            var k = light ? 0.0 : Mica ? 0.62 : 1.0;
            res["AmbientA"] = Halo(accent, (byte)((dark ? 0x56 : 0x62) * k));
            res["AmbientB"] = Halo(violet, (byte)((dark ? 0x5E : 0x54) * k));
            res["AmbientC"] = Halo(blue, (byte)((dark ? 0x54 : 0x50) * k));
            res["AmbientD"] = Halo(rose, (byte)((dark ? 0x3C : 0x38) * k));
            res["AmbientE"] = Halo(cyan, (byte)((dark ? 0x32 : 0x3A) * k));
            res["HeroGlow"] = Glow(accent, (byte)((dark ? 0x48 : 0x32) * (light ? 0.4 : 1.0)));
        }

        static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static Color White(byte a) { return Color.FromArgb(a, 255, 255, 255); }

        static SolidColorBrush Make(string hex)
        {
            var b = new SolidColorBrush(Hex(hex));
            b.Freeze();
            return b;
        }

        static LinearGradientBrush Linear(Point from, Point to, params Color[] colors)
        {
            var g = new LinearGradientBrush { StartPoint = from, EndPoint = to };
            for (int i = 0; i < colors.Length; i++)
                g.GradientStops.Add(new GradientStop(colors[i], colors.Length == 1 ? 0 : (double)i / (colors.Length - 1)));
            g.Freeze();
            return g;
        }

        static LinearGradientBrush LinearStops(Point from, Point to, params GradientStop[] stops)
        {
            var g = new LinearGradientBrush { StartPoint = from, EndPoint = to };
            foreach (var st in stops) g.GradientStops.Add(st);
            g.Freeze();
            return g;
        }

        /// <summary>Tache de couleur ronde, qui s'éteint vers ses bords.</summary>
        static RadialGradientBrush Halo(Color c, byte alpha)
        {
            var g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.38), c.R, c.G, c.B), 0.50));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            g.Freeze();
            return g;
        }

        /// <summary>Halo radial discret, ancré dans le coin gauche d'une carte.</summary>
        static RadialGradientBrush Glow(Color c, byte alpha)
        {
            var g = new RadialGradientBrush
            {
                Center = new Point(0.1, 0.5), GradientOrigin = new Point(0.1, 0.5), RadiusX = 0.58, RadiusY = 1.85,
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            g.Freeze();
            return g;
        }

        public static bool SystemPrefersDark()
        {
            var v = Reg.GetInt(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme");
            return !v.HasValue || v.Value == 0;
        }
    }

    public static class Backdrop
    {
        [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
        [DllImport("dwmapi")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);

        public static void SetDarkFrame(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, 20, ref v, 4); } catch { }
        }

        /// <summary>Active Mica sur Windows 11 22H2 ou plus ; sinon le thème reste un fond uni.</summary>
        public static bool TryEnableMica(IntPtr hwnd, System.Windows.Interop.HwndSource source)
        {
            if (Os.Build < 22621) return false;
            try
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                int type = 3; // DWMSBT_TRANSIENTWINDOW : acrylique, le vrai verre dépoli de Windows 11
                return DwmSetWindowAttribute(hwnd, 38, ref type, 4) == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Sélecteur de dossier moderne de Windows (WPF n'en fournit pas).</summary>
    public static class Dialogs
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr spec);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem si);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attrs);
            void Compare(IShellItem si, uint hint, out int order);
        }

        public static string PickFolder(string title)
        {
            try
            {
                var d = (IFileDialog)new FileOpenDialogCom();
                uint o; d.GetOptions(out o);
                d.SetOptions(o | 0x20 | 0x40); // choisir un dossier, système de fichiers uniquement
                d.SetTitle(title);
                var owner = new System.Windows.Interop.WindowInteropHelper(Shell.I.Win).Handle;
                if (d.Show(owner) != 0) return null;
                IShellItem si; d.GetResult(out si);
                string path; si.GetDisplayName(0x80058000, out path);
                return path;
            }
            catch (Exception e) { Logger.Error("Sélecteur de dossier", e); return null; }
        }

        public static string PickOpen(string title, string filter)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return d.ShowDialog(Shell.I.Win) == true ? d.FileName : null;
        }

        public static string PickSave(string title, string filter, string name)
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = name, OverwritePrompt = true };
            return d.ShowDialog(Shell.I.Win) == true ? d.FileName : null;
        }
    }
}
