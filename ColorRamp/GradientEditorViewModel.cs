using ColorRamp;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace ColorRamp
{
    public record SegmentInterpOption(SegmentInterpolationMode Value, string Name);

    internal class GradientEditorViewModel : Bindable, IDisposable
    {
        readonly ColorRampEffect effectItem;
        readonly ItemProperty[] properties;

        private IEditorInfo? _editorInfo;

        // ★ 追加: UIへ読み込み中に他アイテムへ同期（上書き）してしまうのを防ぐフラグ
        private bool _isUpdating;

        public ColorRampEffect EffectItem => effectItem;

        public ObservableCollection<GradientPoint> Points { get; } = new();

        public event EventHandler? BeginEdit;
        public event EventHandler? EndEdit;

        public ActionCommand AddCommand { get; }
        public ActionCommand RemoveCommand { get; }
        public ActionCommand DistributeCommand { get; }
        public ActionCommand ReverseCommand { get; }
        public ActionCommand ExportGrdCommand { get; }
        public ActionCommand ImportGrdCommand { get; }
        public ActionCommand ImportHexCommand { get; }
        public ActionCommand ImportPngCommand { get; }

        public ICommand ItemBeginEditCommand { get; }
        public ICommand ItemEndEditCommand { get; }

        public bool CanRemove => Points.Count > 2;
        public bool CanDistribute => Points.Count > 2;
        public bool CanReverse => Points.Count >= 2;

        public GradientStopCollection GradientStops { get; private set; } = new();

        public IReadOnlyList<double> SeekLinePositions
        {
            get => _seekLinePositions;
            private set { _seekLinePositions = value; OnPropertyChanged(nameof(SeekLinePositions)); }
        }
        private IReadOnlyList<double> _seekLinePositions = Array.Empty<double>();

        public bool IsCurveMode => effectItem.GradientInterpolationType == ColorRampEffect.GradientInterpolation.Curve;

        public ImmutableList<GradientCurvePoint> CurvePoints => effectItem.CurvePoints;

        public static IReadOnlyList<SegmentInterpOption> SegmentInterpolationOptions { get; } =
            new List<SegmentInterpOption>
            {
                new(SegmentInterpolationMode.Linear,   "リニア"),
                new(SegmentInterpolationMode.Ease,     "イーズ"),
                new(SegmentInterpolationMode.Cardinal, "カーディナル"),
                new(SegmentInterpolationMode.BSpline,  "Bスプライン"),
                new(SegmentInterpolationMode.Constant, "一定"),
            };

        public GradientEditorViewModel(ItemProperty[] properties)
        {
            this.properties = properties;
            effectItem = (ColorRampEffect)properties[0].PropertyOwner;

            effectItem.OnPointsChangedBySystem += EffectItem_OnPointsChangedBySystem;
            effectItem.PropertyChanged += EffectItem_PropertyChanged;

            ItemBeginEditCommand = new ActionCommand(_ => true, _ => BeginEdit?.Invoke(this, EventArgs.Empty));
            ItemEndEditCommand = new ActionCommand(_ => true, _ => EndEdit?.Invoke(this, EventArgs.Empty));

            AddCommand = new ActionCommand(
                o => true,
                o =>
                {
                    BeginEdit?.Invoke(this, EventArgs.Empty);
                    double initialPos = o is double pos ? pos : 0.5;
                    var newPoint = new GradientPoint(initialPos, Colors.Gray)
                    {
                        BeginEditCommand = ItemBeginEditCommand,
                        EndEditCommand = ItemEndEditCommand
                    };
                    newPoint.PropertyChanged += OnPointPropertyChanged;
                    Points.Add(newPoint);
                    EndEdit?.Invoke(this, EventArgs.Empty);
                });

            RemoveCommand = new ActionCommand(
                o => o is GradientPoint p && Points.Contains(p) && Points.Count > 2,
                o =>
                {
                    if (o is GradientPoint pToRemove)
                    {
                        BeginEdit?.Invoke(this, EventArgs.Empty);
                        Points.Remove(pToRemove);
                        EndEdit?.Invoke(this, EventArgs.Empty);
                    }
                });

            DistributeCommand = new ActionCommand(
                _ => Points.Count > 2,
                _ =>
                {
                    BeginEdit?.Invoke(this, EventArgs.Empty);
                    _isUpdating = true; // 連続同期を防ぐ
                    try
                    {
                        var sorted = Points.OrderBy(p => p.PositionValue).ToList();
                        for (int i = 0; i < sorted.Count; i++)
                            sorted[i].PositionValue = (double)i / (sorted.Count - 1);
                    }
                    finally
                    {
                        _isUpdating = false;
                    }
                    SyncToModel();
                    EndEdit?.Invoke(this, EventArgs.Empty);
                });

            ReverseCommand = new ActionCommand(
                _ => Points.Count >= 2,
                _ =>
                {
                    BeginEdit?.Invoke(this, EventArgs.Empty);
                    _isUpdating = true; // 連続同期を防ぐ
                    try
                    {
                        foreach (var p in Points) p.PositionValue = 1.0 - p.PositionValue;
                    }
                    finally
                    {
                        _isUpdating = false;
                    }
                    SyncToModel();
                    EndEdit?.Invoke(this, EventArgs.Empty);
                });

            ExportGrdCommand = new ActionCommand(
                _ => Points.Count >= 2,
                _ =>
                {
                    var dlg = new SaveFileDialog
                    {
                        Title = "グラデーションを .grd として書き出し",
                        Filter = "Photoshop グラデーション|*.grd|すべてのファイル|*.*",
                        DefaultExt = ".grd",
                        FileName = "gradient"
                    };
                    if (dlg.ShowDialog() != true) return;
                    try
                    {
                        GrdFileIO.Export(dlg.FileName, "ColorRamp Gradient",
                            Points.OrderBy(p => p.PositionValue).ToList());
                        MessageBox.Show($"書き出しが完了しました。\n{dlg.FileName}",
                            "書き出し完了", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"書き出しに失敗しました。\n{ex.Message}",
                            "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });

            ImportGrdCommand = new ActionCommand(
                _ => true,
                _ => ImportPalette(
                    "Photoshop グラデーション|*.grd|すべてのファイル|*.*",
                    path => GrdFileIO.Import(path).Points));

            ImportHexCommand = new ActionCommand(
                _ => true,
                _ => ImportPalette(
                    "Lospec パレット (.hex)|*.hex|すべてのファイル|*.*",
                    path => PaletteImporter.ImportHex(path)));

            ImportPngCommand = new ActionCommand(
                _ => true,
                _ => ImportPalette(
                    "PNG パレット画像|*.png|すべてのファイル|*.*",
                    path => PaletteImporter.ImportPng(path)));

            Points.CollectionChanged += OnCollectionChanged;
            LoadFromModel();
        }

        private void ImportPalette(string filter, Func<string, ImmutableList<GradientPoint>> importer)
        {
            var dlg = new OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog() != true) return;

            ImmutableList<GradientPoint> imported;
            try
            {
                imported = importer(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"読み込みに失敗しました。\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var result = MessageBox.Show(
                $"{imported.Count} 色を読み込みます。\n現在のグラデーションは上書きされます。よろしいですか？",
                "パレットの読み込み", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            BeginEdit?.Invoke(this, EventArgs.Empty);

            _isUpdating = true; // 追加途中の連続同期を防ぐ
            try
            {
                foreach (var p in Points) p.PropertyChanged -= OnPointPropertyChanged;
                Points.Clear();
                foreach (var p in imported)
                {
                    p.BeginEditCommand = ItemBeginEditCommand;
                    p.EndEditCommand = ItemEndEditCommand;
                    p.PropertyChanged += OnPointPropertyChanged;
                    Points.Add(p);
                }
            }
            finally
            {
                _isUpdating = false;
            }

            SyncToModel(); // ここで一気に同期
            EndEdit?.Invoke(this, EventArgs.Empty);
        }

        public void SetEditorInfo(IEditorInfo info)
        {
            _editorInfo = info;
            UpdatePreview();
        }

        public void SetCurvePoints(ImmutableList<GradientCurvePoint> pts)
        {
            effectItem.CurvePoints = pts;
            UpdatePreview();
        }

        private void EffectItem_OnPointsChangedBySystem(object? sender, ImmutableList<GradientPoint> newPoints)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(LoadFromModel));
        }

        private void EffectItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ColorRampEffect.GradientCalculationType) ||
                e.PropertyName == nameof(ColorRampEffect.GradientInterpolationType) ||
                e.PropertyName == nameof(ColorRampEffect.GradientInterpolationHSLHSVType) ||
                e.PropertyName == nameof(ColorRampEffect.UsePerPointInterpolation))
            {
                OnPropertyChanged(nameof(IsCurveMode));
                Application.Current.Dispatcher.BeginInvoke(new Action(() => UpdatePreview()));
            }
        }

        private void LoadFromModel()
        {
            _isUpdating = true; // ★ 読み込み開始（複数選択時の意図せぬ上書き同期をブロック）
            try
            {
                var modelPoints = effectItem.Points;
                foreach (var p in Points) p.PropertyChanged -= OnPointPropertyChanged;
                Points.Clear();
                foreach (var p in modelPoints)
                {
                    p.BeginEditCommand = ItemBeginEditCommand;
                    p.EndEditCommand = ItemEndEditCommand;
                    p.PropertyChanged += OnPointPropertyChanged;
                    Points.Add(p);
                }
                UpdatePointStates();
                UpdatePreview();
                UpdateButtons();
            }
            finally
            {
                _isUpdating = false; // ★ 読み込み完了（同期再開）
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isUpdating) return; // ★ 読み込み中は無視

            SyncToModel();
            UpdatePointStates();
            UpdateButtons();
        }

        private void OnPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isUpdating) return; // ★ 読み込み中は無視

            if (e.PropertyName == nameof(GradientPoint.PositionValue) ||
                e.PropertyName == nameof(GradientPoint.Color) ||
                e.PropertyName == nameof(GradientPoint.SegmentInterpolation))
                SyncToModel();
        }

        private void SyncToModel()
        {
            if (_isUpdating) return; // ★ 読み込み中は無視

            var newImmutable = Points.OrderBy(x => x.PositionValue).ToImmutableList();
            effectItem.EditorPoints = newImmutable;
            if (properties.Length > 1)
                foreach (var prop in properties.Skip(1))
                    if (prop.PropertyOwner is ColorRampEffect otherEffect)
                        otherEffect.EditorPoints = newImmutable;
            UpdatePreview();
        }

        private void UpdatePointStates()
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i].IsLastPoint = (i == Points.Count - 1);
        }

        private void UpdateButtons()
        {
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(CanDistribute));
            OnPropertyChanged(nameof(CanReverse));
            AddCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
            DistributeCommand.RaiseCanExecuteChanged();
            ReverseCommand.RaiseCanExecuteChanged();
            ExportGrdCommand.RaiseCanExecuteChanged();
            ImportGrdCommand.RaiseCanExecuteChanged();
            ImportHexCommand.RaiseCanExecuteChanged();
            ImportPngCommand.RaiseCanExecuteChanged();
        }

        public void CopyToOtherItems() => SyncToModel();

        public void UpdatePreview(object? sender = null)
        {
            bool withinItem = false;
            long frame = 0, length = 1;
            int fps = 30;

            if (_editorInfo != null)
            {
                frame = _editorInfo.ItemPosition.Frame;
                length = Math.Max(1, _editorInfo.ItemDuration.Frame);
                fps = _editorInfo.VideoInfo?.FPS ?? 30;
                withinItem = frame >= 0 && frame <= length;
            }

            SeekLinePositions = withinItem
                ? Points
                    .Where(p => p.EnablePositionAnimation && p.PositionAnim.AnimationType != AnimationType.なし)
                    .Select(p => Math.Clamp(p.PositionAnim.GetValue(frame, length, fps) / 100.0, 0.0, 1.0))
                    .ToList()
                : (IReadOnlyList<double>)Array.Empty<double>();

            var resolvedPoints = ResolvePreviewPositions(withinItem, frame, length, fps);
            var newStops = new GradientStopCollection();

            if (resolvedPoints.Count == 0) { GradientStops = newStops; OnPropertyChanged(nameof(GradientStops)); return; }

            var calcType = effectItem.GradientCalculationType;
            var interpType = effectItem.GradientInterpolationType;
            var hueType = effectItem.GradientInterpolationHSLHSVType;
            var curvePts = effectItem.CurvePoints;
            bool usePerPoint = effectItem.UsePerPointInterpolation;
            bool isCurve = interpType == ColorRampEffect.GradientInterpolation.Curve;

            bool needsSampling =
                calcType != ColorRampEffect.GradientType.RGB ||
                (interpType != ColorRampEffect.GradientInterpolation.Linear && !usePerPoint) ||
                usePerPoint || isCurve;

            if (needsSampling)
            {
                for (int i = 0; i < resolvedPoints.Count - 1; i++)
                {
                    var (pos1, col1, seg1) = resolvedPoints[i];
                    var (pos2, col2, _) = resolvedPoints[i + 1];
                    var segMode = usePerPoint ? seg1 : ToSegmentMode(interpType);
                    int steps = segMode == SegmentInterpolationMode.Constant ? 1 : 20;

                    for (int j = 0; j <= steps; j++)
                    {
                        float rawT = (float)j / steps;
                        float interpT = usePerPoint
                            ? ApplySegmentEasing(rawT, segMode)
                            : ApplyGlobalEasing(rawT, interpType, curvePts);
                        float globalPos = (float)(pos1 + (pos2 - pos1) * rawT);
                        newStops.Add(new GradientStop(SampleColor(col1, col2, interpT, calcType, hueType), globalPos));
                    }
                }
            }
            else
            {
                foreach (var (pos, col, _) in resolvedPoints)
                    newStops.Add(new GradientStop(col, pos));
            }

            GradientStops = newStops;
            OnPropertyChanged(nameof(GradientStops));
        }

        private static SegmentInterpolationMode ToSegmentMode(ColorRampEffect.GradientInterpolation g) => g switch
        {
            ColorRampEffect.GradientInterpolation.Ease => SegmentInterpolationMode.Ease,
            ColorRampEffect.GradientInterpolation.Cardinal => SegmentInterpolationMode.Cardinal,
            ColorRampEffect.GradientInterpolation.BSpline => SegmentInterpolationMode.BSpline,
            ColorRampEffect.GradientInterpolation.Constant => SegmentInterpolationMode.Constant,
            _ => SegmentInterpolationMode.Linear
        };

        private static float ApplySegmentEasing(float t, SegmentInterpolationMode m) => m switch
        {
            SegmentInterpolationMode.Ease => t * t * (3 - 2 * t),
            SegmentInterpolationMode.Cardinal => t * t * (3 - 2 * t),
            SegmentInterpolationMode.BSpline => t * t * (3 - 2 * t),
            SegmentInterpolationMode.Constant => 0f,
            _ => t
        };

        private static float ApplyGlobalEasing(float t, ColorRampEffect.GradientInterpolation interp,
            IReadOnlyList<GradientCurvePoint> curvePts) => interp switch
            {
                ColorRampEffect.GradientInterpolation.Ease => t * t * (3 - 2 * t),
                ColorRampEffect.GradientInterpolation.Cardinal => t * t * (3 - 2 * t),
                ColorRampEffect.GradientInterpolation.BSpline => t * t * (3 - 2 * t),
                ColorRampEffect.GradientInterpolation.Constant => 0f,
                ColorRampEffect.GradientInterpolation.Curve => (float)GradientCurveHelper.Evaluate(curvePts, t),
                _ => t
            };

        private Color SampleColor(Color c1, Color c2, float t,
            ColorRampEffect.GradientType calcType, ColorRampEffect.GradientInterpolationHSLHSV hueType)
        {
            if (calcType == ColorRampEffect.GradientType.RGB)
                return Color.FromScRgb(
                    Lerp(c1.ScA, c2.ScA, t), Lerp(c1.ScR, c2.ScR, t),
                    Lerp(c1.ScG, c2.ScG, t), Lerp(c1.ScB, c2.ScB, t));

            var mode = calcType == ColorRampEffect.GradientType.HSL
                ? HsvHslHelper.ColorSpace.HSL : HsvHslHelper.ColorSpace.HSV;
            var h1 = HsvHslHelper.FromColor(c1, mode);
            var h2 = HsvHslHelper.FromColor(c2, mode);
            float targetH2 = h2.H, diffH = h2.H - h1.H;
            switch (hueType)
            {
                case ColorRampEffect.GradientInterpolationHSLHSV.Near:
                    if (diffH > 180) targetH2 -= 360; else if (diffH < -180) targetH2 += 360; break;
                case ColorRampEffect.GradientInterpolationHSLHSV.Far:
                    if (diffH > 0 && diffH <= 180) targetH2 -= 360;
                    else if (diffH <= 0 && diffH > -180) targetH2 += 360; break;
                case ColorRampEffect.GradientInterpolationHSLHSV.Clockwise:
                    if (targetH2 < h1.H) targetH2 += 360; break;
                case ColorRampEffect.GradientInterpolationHSLHSV.CounterClockwise:
                    if (targetH2 > h1.H) targetH2 -= 360; break;
            }
            float h = (Lerp(h1.H, targetH2, t) % 360 + 360) % 360;
            return HsvHslHelper.ToColor(h, Lerp(h1.S, h2.S, t), Lerp(h1.V, h2.V, t), Lerp(h1.A, h2.A, t), mode);
        }

        private List<(double Pos, Color Color, SegmentInterpolationMode SegInterp)>
            ResolvePreviewPositions(bool withinItem, long frame, long length, int fps)
        {
            var result = new List<(double, Color, SegmentInterpolationMode)>();
            foreach (var p in Points)
            {
                double pos = withinItem &&
                             p.EnablePositionAnimation &&
                             p.PositionAnim.AnimationType != AnimationType.なし
                    ? Math.Clamp(p.PositionAnim.GetValue(frame, length, fps) / 100.0, 0.0, 1.0)
                    : p.PositionValue;
                result.Add((pos, p.Color, p.SegmentInterpolation));
            }
            result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return result;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public void Dispose()
        {
            effectItem.OnPointsChangedBySystem -= EffectItem_OnPointsChangedBySystem;
            effectItem.PropertyChanged -= EffectItem_PropertyChanged;
            Points.CollectionChanged -= OnCollectionChanged;
            foreach (var p in Points) p.PropertyChanged -= OnPointPropertyChanged;
        }
    }
}