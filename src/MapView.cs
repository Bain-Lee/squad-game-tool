using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SquadTools
{
    public sealed class MapView : FrameworkElement
    {
        public double WorldSize = 3000;
        public double WorldHeight = 3000;
        public Vec2 Weapon;
        public Vec2 Target;
        public bool HasMarkers;
        public BitmapSource MapImage;
        public bool PlaceWeapon;
        public double RangeMeters = Ballistics.Velocity * Ballistics.Velocity / Ballistics.Gravity;
        public event Action<Vec2, bool> PointPlaced;
        private double zoom = 1;
        private Vector pan;
        private Point dragStart;
        private Vector panStart;
        private bool dragging, pendingViewport, listeningForFrame;
        private MouseButton dragButton = MouseButton.Middle;
        private bool dragMoved, rightClickPending, weaponDoubleClick;
        private readonly Typeface font = new Typeface("Microsoft YaHei UI");
        private readonly DrawingVisual scene = new DrawingVisual();
        private readonly DrawingVisual columnLabels = new DrawingVisual();
        private readonly DrawingVisual rowLabels = new DrawingVisual();
        private readonly DrawingVisual overlay = new DrawingVisual();
        private readonly DrawingVisual hud = new DrawingVisual();
        private readonly TranslateTransform sceneOffset = new TranslateTransform();
        private readonly TranslateTransform columnOffset = new TranslateTransform();
        private readonly TranslateTransform rowOffset = new TranslateTransform();
        private readonly RectangleGeometry columnClip = new RectangleGeometry();
        private readonly RectangleGeometry rowClip = new RectangleGeometry();
        private readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();
        private readonly Dictionary<string, Pen> pens = new Dictionary<string, Pen>();
        private readonly Dictionary<string, FormattedText> texts = new Dictionary<string, FormattedText>();
        private bool sceneReady, hudReady;
        private double drawnScale, drawnWidth, drawnHeight, drawnDpi, hudWidth, hudHeight, hudScale, drawnRange;
        private BitmapSource drawnImage, hudImage;
        private Vec2 drawnWeapon, drawnTarget;
        private bool drawnMarkers, hudMarkers;
        internal int SceneBuildCount { get; private set; }
        internal int HudBuildCount { get; private set; }
        internal int ViewportUpdateCount { get; private set; }

        public MapView()
        {
            ClipToBounds = true;
            Focusable = true;
            Cursor = Cursors.Cross;
            AddVisualChild(scene); AddVisualChild(columnLabels); AddVisualChild(rowLabels); AddVisualChild(overlay); AddVisualChild(hud);
            scene.Transform = sceneOffset;
            overlay.Transform = sceneOffset;
            columnLabels.Transform = columnOffset; columnLabels.Clip = columnClip;
            rowLabels.Transform = rowOffset; rowLabels.Clip = rowClip;
            // Keep the decoded source and drawing commands resident while panning.
            RenderOptions.SetBitmapScalingMode(scene, BitmapScalingMode.HighQuality);
            MouseWheel += OnWheel;
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;
            LostMouseCapture += delegate { EndDrag(); };
            Unloaded += delegate { EndDrag(); };
            SizeChanged += delegate { InvalidateVisual(); };
        }

        protected override int VisualChildrenCount { get { return 5; } }
        protected override Visual GetVisualChild(int index)
        {
            switch (index) { case 0: return scene; case 1: return columnLabels; case 2: return rowLabels; case 3: return overlay; case 4: return hud; }
            throw new ArgumentOutOfRangeException("index");
        }

        private double BaseScale { get { return Math.Max(0.001, Math.Min((ActualWidth - 70) / WorldSize, (ActualHeight - 56) / WorldHeight)); } }
        private double Scale { get { return BaseScale * zoom; } }
        private Point Origin { get { return new Point((ActualWidth - WorldSize * Scale) / 2 + pan.X, (ActualHeight - WorldHeight * Scale) / 2 + pan.Y); } }
        private Point Screen(Vec2 p) { Point o = Origin; return new Point(o.X + p.X * Scale, o.Y + p.Y * Scale); }
        private Vec2 World(Point p) { Point o = Origin; return new Vec2((p.X - o.X) / Scale, (p.Y - o.Y) / Scale); }
        private static Point Local(Vec2 p, double scale) { return new Point(p.X * scale, p.Y * scale); }
        public void ResetView() { EndDrag(); zoom = 1; pan = new Vector(); InvalidateVisual(); }
        public void ZoomBy(double factor) { rightClickPending = weaponDoubleClick = false; zoom = Math.Max(0.7, Math.Min(12, zoom * factor)); InvalidateVisual(); }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.2 : 1 / 1.2);
            e.Handled = true;
        }

        private void ZoomAt(Point p, double factor)
        {
            rightClickPending = weaponDoubleClick = false;
            Vec2 before = World(p);
            zoom = Math.Max(0.7, Math.Min(12, zoom * factor));
            pan += p - Screen(before);
            if (dragging) { dragStart = p; panStart = pan; BeginPan(); }
            InvalidateVisual();
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            e.Handled = HandlePointerDown(e.ChangedButton, e.GetPosition(this), e.ClickCount);
        }

        private bool HandlePointerDown(MouseButton button, Point position, int clickCount)
        {
            // Another button must not place a marker or replace the active drag.
            if (dragging) return true;
            if (button == MouseButton.Right || button == MouseButton.Middle)
            {
                // WPF supplies the system double-click time and distance checks.
                // Also require a completed, unmoved first click: drags count as
                // presses to WPF, but must never count as our first click.
                weaponDoubleClick = button == MouseButton.Right && clickCount > 1 && rightClickPending;
                rightClickPending = false;
                dragging = true; dragMoved = false; dragButton = button;
                dragStart = position; panStart = pan;
                CaptureMouse(); Cursor = Cursors.SizeAll;
                return true;
            }
            rightClickPending = false;
            return button == MouseButton.Left && PlaceAt(position, PlaceWeapon);
        }

        private bool PlaceAt(Point position, bool weapon)
        {
            Vec2 p = World(position);
            if (p.X < 0 || p.Y < 0 || p.X >= WorldSize || p.Y >= WorldHeight) return false;
            if (PointPlaced != null) PointPlaced(p, weapon);
            return true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            MoveDrag(e.GetPosition(this));
        }

        private void BeginPan()
        {
            if (!dragMoved) RenderOptions.SetBitmapScalingMode(scene, BitmapScalingMode.LowQuality);
            dragMoved = true; weaponDoubleClick = false;
        }

        private void MoveDrag(Point position)
        {
            if (!dragging) return;
            Vector delta = position - dragStart;
            // Ignore click jitter so a double-click keeps the map stationary.
            if (!dragMoved && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            BeginPan();
            pan = panStart + delta;
            pendingViewport = true;
            // Several mouse events can arrive within a display frame. Commit the newest
            // position once, without invalidating/rebuilding the map or its text.
            if (!listeningForFrame) { CompositionTarget.Rendering += OnFrame; listeningForFrame = true; }
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = HandlePointerUp(e.ChangedButton, e.GetPosition(this));
        }

        private bool HandlePointerUp(MouseButton button, Point position)
        {
            if (!dragging || button != dragButton) return false;
            MoveDrag(position);
            bool placeWeapon = weaponDoubleClick && !dragMoved;
            bool firstRightClick = button == MouseButton.Right && !dragMoved && !placeWeapon;
            // Release capture and commit any pending pan before notifying the UI.
            // LostMouseCapture also clears click state, so restore it afterwards.
            EndDrag();
            rightClickPending = firstRightClick;
            if (placeWeapon) PlaceAt(position, true);
            return true;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (pendingViewport) { pendingViewport = false; UpdateViewport(); }
            if (listeningForFrame) { CompositionTarget.Rendering -= OnFrame; listeningForFrame = false; }
        }

        private void EndDrag()
        {
            dragging = false;
            dragMoved = rightClickPending = weaponDoubleClick = false;
            if (listeningForFrame) { CompositionTarget.Rendering -= OnFrame; listeningForFrame = false; }
            if (pendingViewport) { pendingViewport = false; UpdateViewport(); }
            RenderOptions.SetBitmapScalingMode(scene, BitmapScalingMode.HighQuality);
            Cursor = Cursors.Cross;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private SolidColorBrush Brush(string hex)
        {
            SolidColorBrush value;
            if (!brushes.TryGetValue(hex, out value))
            {
                value = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                value.Freeze(); brushes.Add(hex, value);
            }
            return value;
        }

        private Pen Line(string hex, double width, bool dashed, bool dotted)
        {
            string key = hex + ":" + width.ToString(CultureInfo.InvariantCulture) + ":" + dashed + ":" + dotted;
            Pen value;
            if (!pens.TryGetValue(key, out value))
            {
                value = new Pen(Brush(hex), width);
                if (dashed) value.DashStyle = DashStyles.Dash;
                if (dotted) value.DashStyle = DashStyles.Dot;
                value.Freeze(); pens.Add(key, value);
            }
            return value;
        }
        private Pen Line(string hex, double width) { return Line(hex, width, false, false); }

        private void Text(DrawingContext dc, string value, double x, double y, double size, string color)
        {
            string key = value + ":" + size.ToString(CultureInfo.InvariantCulture) + ":" + color;
            FormattedText text;
            if (!texts.TryGetValue(key, out text))
            {
                text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, size, Brush(color), drawnDpi);
                texts.Add(key, text);
            }
            dc.DrawText(text, new Point(x, y));
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brush("#101B24"), null, new Rect(RenderSize));
            UpdateViewport();
        }

        private void UpdateViewport()
        {
            if (ActualWidth < 10 || ActualHeight < 10) return;
            double scale = Scale, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            bool rebuild = !sceneReady || drawnScale != scale || drawnWidth != WorldSize || drawnHeight != WorldHeight ||
                drawnImage != MapImage || drawnMarkers != HasMarkers || drawnDpi != dpi || drawnRange != RangeMeters ||
                drawnWeapon.X != Weapon.X || drawnWeapon.Y != Weapon.Y || drawnTarget.X != Target.X || drawnTarget.Y != Target.Y;
            if (rebuild)
            {
                // Only scale, source, DPI, or marker changes rebuild drawing commands.
                // Bound the text cache across marker changes and preserve it on pans.
                texts.Clear(); drawnDpi = dpi;
                BuildScene(scale);
                drawnScale = scale; drawnWidth = WorldSize; drawnHeight = WorldHeight; drawnImage = MapImage;
                drawnMarkers = HasMarkers; drawnWeapon = Weapon; drawnTarget = Target; drawnRange = RangeMeters; sceneReady = true;
            }
            Point origin = Origin;
            double width = WorldSize * scale, height = WorldHeight * scale;
            sceneOffset.X = origin.X; sceneOffset.Y = origin.Y;
            columnOffset.X = origin.X; columnOffset.Y = Math.Max(6, origin.Y + 5);
            rowOffset.X = Math.Max(8, origin.X + 6); rowOffset.Y = origin.Y;
            columnClip.Rect = new Rect(0, origin.Y - columnOffset.Y, width, height);
            rowClip.Rect = new Rect(origin.X - rowOffset.X, 0, width, height);
            ViewportUpdateCount++;
            if (!hudReady || rebuild || hudWidth != ActualWidth || hudHeight != ActualHeight || hudScale != scale || hudMarkers != HasMarkers || hudImage != MapImage)
            {
                BuildHud(scale); hudReady = true;
                hudWidth = ActualWidth; hudHeight = ActualHeight; hudScale = scale; hudMarkers = HasMarkers; hudImage = MapImage;
            }
        }

        private void BuildScene(double scale)
        {
            SceneBuildCount++;
            double width = WorldSize * scale, height = WorldHeight * scale;
            Rect bounds = new Rect(0, 0, width, height);
            RectangleGeometry clip = new RectangleGeometry(bounds); clip.Freeze(); scene.Clip = clip; overlay.Clip = clip;
            using (DrawingContext dc = scene.RenderOpen())
            {
                dc.DrawRectangle(Brush("#182630"), Line("#3C5262", 1), bounds);
                if (MapImage != null) dc.DrawImage(MapImage, bounds);
                if (HasMarkers)
                {
                    double radius = RangeMeters * scale;
                    dc.DrawEllipse(Brush("#1CAEDE75"), null, Local(Weapon, scale), radius, radius);
                }
                int columns = (int)Math.Ceiling(WorldSize / 300), rows = (int)Math.Ceiling(WorldHeight / 300);
                Pen grid = Line(MapImage == null ? "#344653" : "#50CFD9DA", 1);
                for (int i = 0; i <= columns; i++)
                {
                    double x = Math.Min(i * 300, WorldSize) * scale;
                    dc.DrawLine(grid, new Point(x, 0), new Point(x, height));
                }
                for (int i = 0; i <= rows; i++)
                {
                    double y = Math.Min(i * 300, WorldHeight) * scale;
                    dc.DrawLine(grid, new Point(0, y), new Point(width, y));
                }
                if (300 * scale > 160)
                {
                    Pen minor = Line("#273D4B", 0.6, false, true);
                    for (double m = 100; m < WorldSize; m += 100)
                        if (Math.Abs(m % 300) >= 0.1) dc.DrawLine(minor, new Point(m * scale, 0), new Point(m * scale, height));
                    for (double m = 100; m < WorldHeight; m += 100)
                        if (Math.Abs(m % 300) >= 0.1) dc.DrawLine(minor, new Point(0, m * scale), new Point(width, m * scale));
                }
            }
            using (DrawingContext dc = overlay.RenderOpen())
            {
                if (HasMarkers)
                {
                    double radius = RangeMeters * scale;
                    Point a = Local(Weapon, scale), b = Local(Target, scale);
                    dc.DrawEllipse(null, Line("#E619251B", 4.5), a, radius, radius);
                    dc.DrawEllipse(null, Line("#D4F58C", 2), a, radius, radius);
                    dc.DrawLine(Line("#E2D57C", 2, true, false), a, b);
                    Marker(dc, a, "M", "#B8EB7E"); Marker(dc, b, "T", "#FFA88C");
                    double distance = Math.Sqrt(Math.Pow(Target.X - Weapon.X, 2) + Math.Pow(Target.Y - Weapon.Y, 2));
                    Point mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    dc.DrawRoundedRectangle(Brush("#ED101B24"), null, new Rect(mid.X - 38, mid.Y - 43, 92, 26), 5, 5);
                    Text(dc, distance.ToString("F0") + " m", mid.X - 26, mid.Y - 39, 12, "#EFE9C8");
                }
            }
            using (DrawingContext dc = columnLabels.RenderOpen())
                if (300 * scale > 16)
                    for (int i = 0; i < Math.Ceiling(WorldSize / 300); i++)
                    {
                        double center = (i * 300 + Math.Min((i + 1) * 300, WorldSize)) / 2 * scale;
                        Text(dc, ((char)('A' + i)).ToString(), center - 5, 0, 12, "#EDF3F5");
                    }
            using (DrawingContext dc = rowLabels.RenderOpen())
                if (300 * scale > 16)
                    for (int i = 0; i < Math.Ceiling(WorldHeight / 300); i++)
                    {
                        double center = (i * 300 + Math.Min((i + 1) * 300, WorldHeight)) / 2 * scale;
                        Text(dc, (i + 1).ToString("00"), 0, center - 7, 11, "#EDF3F5");
                    }
        }

        private void BuildHud(double scale)
        {
            HudBuildCount++;
            using (DrawingContext dc = hud.RenderOpen())
            {
                Text(dc, "N", ActualWidth - 35, 17, 15, "#C4D0D6");
                dc.DrawLine(Line("#C4D0D6", 2), new Point(ActualWidth - 29, 41), new Point(ActualWidth - 29, 62));
                dc.DrawLine(Line("#C4D0D6", 2), new Point(ActualWidth - 29, 41), new Point(ActualWidth - 34, 49));
                double scaleLength = 300 * scale;
                while (scaleLength > ActualWidth / 3) scaleLength /= 3;
                while (scaleLength < 45) scaleLength *= 3;
                dc.DrawRoundedRectangle(Brush("#E8101B24"), null, new Rect(15, ActualHeight - 48, scaleLength + 28, 36), 4, 4);
                dc.DrawLine(Line("#BECDD5", 2), new Point(28, ActualHeight - 22), new Point(28 + scaleLength, ActualHeight - 22));
                Text(dc, (scaleLength / scale).ToString("F0") + " m", 29, ActualHeight - 44, 10, "#BECDD5");
                if (HasMarkers)
                {
                    dc.DrawRoundedRectangle(Brush("#D9101B24"), null, new Rect(ActualWidth - 181, ActualHeight - 41, 166, 26), 4, 4);
                    dc.DrawLine(Line("#D4F58C", 2), new Point(ActualWidth - 171, ActualHeight - 28), new Point(ActualWidth - 155, ActualHeight - 28));
                    Text(dc, "绿色圈：平地理论射程", ActualWidth - 149, ActualHeight - 36, 10, "#D9E8CF");
                }
                if (MapImage == null) Text(dc, "坐标网格 · 可导入本地地图图片", Math.Max(18, ActualWidth - 258), ActualHeight - (HasMarkers ? 63 : 37), 12, "#8196A5");
            }
        }

        private void Marker(DrawingContext dc, Point p, string label, string color)
        {
            dc.DrawEllipse(null, Line("#F0101B24", 3.5), p, 5, 5);
            dc.DrawEllipse(null, Line(color, 1.5), p, 5, 5);
            dc.DrawEllipse(Brush(color), null, p, 1, 1);
            Pen crosshair = Line(color, 1);
            dc.DrawLine(crosshair, new Point(p.X - 7.5, p.Y), new Point(p.X - 6, p.Y));
            dc.DrawLine(crosshair, new Point(p.X + 6, p.Y), new Point(p.X + 7.5, p.Y));
            dc.DrawLine(crosshair, new Point(p.X, p.Y - 7.5), new Point(p.X, p.Y - 6));
            dc.DrawLine(crosshair, new Point(p.X, p.Y + 6), new Point(p.X, p.Y + 7.5));
            dc.DrawRoundedRectangle(Brush("#D9101B24"), null, new Rect(p.X + 7, p.Y + 2, 15, 16), 2, 2);
            Text(dc, label, p.X + 9, p.Y + 3, 10, color);
        }
    }
}
