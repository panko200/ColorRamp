using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ColorRamp
{
    public partial class GradientCurveEditorControl : UserControl
    {
        // ----------------------------------------------------------------
        // イベント
        // ----------------------------------------------------------------

        public event EventHandler? BeginEdit;
        public event EventHandler? EndEdit;
        public event EventHandler<ImmutableList<GradientCurvePoint>>? PointsChanged;

        // ----------------------------------------------------------------
        // 内部状態
        // ----------------------------------------------------------------

        private ImmutableList<GradientCurvePoint> _points = GradientCurveHelper.CreateDefault();

        // ドラッグ中の点インデックス (-1 = なし)
        private int _draggingIndex = -1;
        private Point _dragStartMouse;
        private Point _dragStartPoint;

        // 描画オブジェクト
        private readonly Polyline _curveLine  = new();
        private readonly Line     _diagLine    = new();
        private readonly List<Line>    _gridLines   = new();
        private readonly List<Ellipse> _pointDots   = new();

        // 制御点の半径
        private const double DotRadius      = 5.0;
        private const double EndpointRadius = 4.0;
        private const double HitRadius      = 8.0; // ヒットテスト半径

        // ----------------------------------------------------------------
        // 初期化
        // ----------------------------------------------------------------

        public GradientCurveEditorControl()
        {
            InitializeComponent();
            SetupStaticVisuals();
        }

        private void SetupStaticVisuals()
        {
            // 対角線（参照用）
            _diagLine.Stroke          = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            _diagLine.StrokeThickness = 1;
            _diagLine.StrokeDashArray = new DoubleCollection { 4, 4 };
            _diagLine.IsHitTestVisible = false;
            CurveCanvas.Children.Add(_diagLine);

            // カーブ
            _curveLine.Stroke          = new SolidColorBrush(Color.FromRgb(0x4a, 0x8a, 0xdf));
            _curveLine.StrokeThickness = 1.5;
            _curveLine.IsHitTestVisible = false;
            CurveCanvas.Children.Add(_curveLine);
        }

        // ----------------------------------------------------------------
        // 公開 API
        // ----------------------------------------------------------------

        public void SetPoints(ImmutableList<GradientCurvePoint> pts)
        {
            _points = pts;
            Redraw();
        }

        // ----------------------------------------------------------------
        // 描画
        // ----------------------------------------------------------------

        private void Redraw()
        {
            double w = CurveCanvas.ActualWidth;
            double h = CurveCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            DrawGrid(w, h);
            DrawDiag(w, h);
            DrawCurve(w, h);
            DrawDots(w, h);
        }

        private void DrawGrid(double w, double h)
        {
            // グリッド線の数を合わせる
            int needed = 8; // 縦 4 + 横 4
            while (_gridLines.Count < needed)
            {
                var l = new Line
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                CurveCanvas.Children.Insert(0, l);
                _gridLines.Add(l);
            }

            double[] ratios = { 0.25, 0.5, 0.75 };
            int idx = 0;
            foreach (var r in ratios)
            {
                // 縦線
                _gridLines[idx].X1 = r * w; _gridLines[idx].Y1 = 0;
                _gridLines[idx].X2 = r * w; _gridLines[idx].Y2 = h;
                idx++;
                // 横線
                _gridLines[idx].X1 = 0;   _gridLines[idx].Y1 = r * h;
                _gridLines[idx].X2 = w;   _gridLines[idx].Y2 = r * h;
                idx++;
            }
            // 残りは非表示
            for (; idx < _gridLines.Count; idx++)
                _gridLines[idx].X1 = _gridLines[idx].X2 = 0;
        }

        private void DrawDiag(double w, double h)
        {
            _diagLine.X1 = 0; _diagLine.Y1 = h;
            _diagLine.X2 = w; _diagLine.Y2 = 0;
        }

        private void DrawCurve(double w, double h)
        {
            var pts = GradientCurveHelper.SampleCurve(_points, 128, w, h);
            _curveLine.Points = new PointCollection(pts);
        }

        private void DrawDots(double w, double h)
        {
            // 点の数を合わせる
            while (_pointDots.Count < _points.Count)
            {
                var e = new Ellipse { Cursor = Cursors.SizeAll };
                e.MouseLeftButtonDown += Dot_MouseLeftButtonDown;
                e.MouseRightButtonDown += Dot_MouseRightButtonDown;
                CurveCanvas.Children.Add(e);
                _pointDots.Add(e);
            }
            while (_pointDots.Count > _points.Count)
            {
                CurveCanvas.Children.Remove(_pointDots[^1]);
                _pointDots.RemoveAt(_pointDots.Count - 1);
            }

            for (int i = 0; i < _points.Count; i++)
            {
                bool isEndpoint = (i == 0 || i == _points.Count - 1);
                double r = isEndpoint ? EndpointRadius : DotRadius;
                var cp = GradientCurveHelper.ToCanvas(_points[i].X, _points[i].Y, w, h);

                _pointDots[i].Width  = r * 2;
                _pointDots[i].Height = r * 2;
                _pointDots[i].Fill   = isEndpoint
                    ? new SolidColorBrush(Color.FromRgb(0xaa, 0xcc, 0xff))
                    : Brushes.White;
                _pointDots[i].Stroke = new SolidColorBrush(Color.FromRgb(0x4a, 0x8a, 0xdf));
                _pointDots[i].StrokeThickness = 1.5;
                _pointDots[i].Tag  = i;

                Canvas.SetLeft(_pointDots[i], cp.X - r);
                Canvas.SetTop(_pointDots[i], cp.Y - r);
            }
        }

        // ----------------------------------------------------------------
        // マウスイベント: Canvas
        // ----------------------------------------------------------------

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 空きエリアをクリック → 新しい点を追加
            var pos = e.GetPosition(CurveCanvas);
            double w = CurveCanvas.ActualWidth, h = CurveCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // 既存の点の近くなら無視（Dot イベントで処理）
            var (cx, cy) = GradientCurveHelper.FromCanvas(pos.X, pos.Y, w, h);
            if (NearestPointIndex(pos, w, h) >= 0) return;

            BeginEdit?.Invoke(this, EventArgs.Empty);

            // 挿入位置を X で決定（ソート済みを維持）
            var newPt = new GradientCurvePoint(cx, cy);
            var list  = _points.ToList();
            int ins   = list.Count;
            for (int i = 0; i < list.Count; i++)
                if (list[i].X > cx) { ins = i; break; }

            _points = _points.Insert(ins, newPt);
            PointsChanged?.Invoke(this, _points);
            Redraw();
            EndEdit?.Invoke(this, EventArgs.Empty);

            e.Handled = true;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingIndex < 0) return;

            var pos = e.GetPosition(CurveCanvas);
            double w = CurveCanvas.ActualWidth, h = CurveCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var (cx, cy) = GradientCurveHelper.FromCanvas(pos.X, pos.Y, w, h);

            bool isEndpoint = (_draggingIndex == 0 || _draggingIndex == _points.Count - 1);

            // 端点は X を固定、内部点は X も動かせるが隣の点を越えないよう制限
            double newX;
            if (isEndpoint)
            {
                newX = _draggingIndex == 0 ? 0.0 : 1.0;
            }
            else
            {
                double minX = _points[_draggingIndex - 1].X + 0.01;
                double maxX = _points[_draggingIndex + 1].X - 0.01;
                newX = Math.Clamp(cx, minX, maxX);
            }
            double newY = Math.Clamp(cy, 0.0, 1.0);

            var updated = new GradientCurvePoint(newX, newY);
            _points = _points.SetItem(_draggingIndex, updated);
            PointsChanged?.Invoke(this, _points);
            Redraw();

            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingIndex < 0) return;
            _draggingIndex = -1;
            CurveCanvas.ReleaseMouseCapture();
            EndEdit?.Invoke(this, EventArgs.Empty);
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Dot の右クリックで処理
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        // ----------------------------------------------------------------
        // マウスイベント: 制御点 Ellipse
        // ----------------------------------------------------------------

        private void Dot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Ellipse el || el.Tag is not int idx) return;

            _draggingIndex  = idx;
            _dragStartMouse = e.GetPosition(CurveCanvas);
            _dragStartPoint = new Point(_points[idx].X, _points[idx].Y);

            CurveCanvas.CaptureMouse();
            BeginEdit?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void Dot_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Ellipse el || el.Tag is not int idx) return;

            // 端点は削除不可
            bool isEndpoint = (idx == 0 || idx == _points.Count - 1);
            if (isEndpoint) { e.Handled = true; return; }

            BeginEdit?.Invoke(this, EventArgs.Empty);
            _points = _points.RemoveAt(idx);
            PointsChanged?.Invoke(this, _points);
            Redraw();
            EndEdit?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        // ----------------------------------------------------------------
        // ヘルパー
        // ----------------------------------------------------------------

        /// <summary>Canvas 座標 pos に最も近い点のインデックスを返す。HitRadius 外なら -1。</summary>
        private int NearestPointIndex(Point pos, double w, double h)
        {
            int best  = -1;
            double bestDist = HitRadius;
            for (int i = 0; i < _points.Count; i++)
            {
                var cp   = GradientCurveHelper.ToCanvas(_points[i].X, _points[i].Y, w, h);
                double d = (cp - pos).Length;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }
    }
}
