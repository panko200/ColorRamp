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
    internal class GradientEditorViewModel : Bindable, IDisposable
    {
        readonly ColorRampEffect effectItem;
        readonly ItemProperty[] properties;

        // ★追加: XAMLからバインドするためにEffect本体を公開
        public ColorRampEffect EffectItem => effectItem;

        public ObservableCollection<GradientPoint> Points { get; } = new();

        public event EventHandler? BeginEdit;
        public event EventHandler? EndEdit;

        public ActionCommand AddCommand { get; }
        public ActionCommand RemoveCommand { get; }
        public ActionCommand DistributeCommand { get; }
        public ActionCommand ReverseCommand { get; }
        public ICommand ItemBeginEditCommand { get; }
        public ICommand ItemEndEditCommand { get; }

        public bool CanRemove => Points.Count > 2;
        public bool CanDistribute => Points.Count > 2;
        public bool CanReverse => Points.Count >= 2;

        public GradientStopCollection GradientStops { get; private set; } = new();

        public GradientEditorViewModel(ItemProperty[] properties)
        {
            this.properties = properties;

            // Effectインスタンスの取得
            effectItem = (ColorRampEffect)properties[0].PropertyOwner;

            // イベント購読
            effectItem.OnPointsChangedBySystem += EffectItem_OnPointsChangedBySystem;
            effectItem.PropertyChanged += EffectItem_PropertyChanged;

            // --- コマンド定義 ---
            ItemBeginEditCommand = new ActionCommand(_ => true, _ => BeginEdit?.Invoke(this, EventArgs.Empty));
            ItemEndEditCommand = new ActionCommand(_ => true, _ => EndEdit?.Invoke(this, EventArgs.Empty));

            AddCommand = new ActionCommand(
                o => true,
                o =>
                {
                    BeginEdit?.Invoke(this, EventArgs.Empty);
                    double initialPos = (o is double pos) ? pos : 0.5;
                    var newPoint = new GradientPoint(initialPos, Colors.Gray);

                    newPoint.BeginEditCommand = ItemBeginEditCommand;
                    newPoint.EndEditCommand = ItemEndEditCommand;
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
                    var sorted = Points.OrderBy(p => p.Position).ToList();
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        sorted[i].Position = (double)i / (sorted.Count - 1);
                    }
                    SyncToModel();
                    EndEdit?.Invoke(this, EventArgs.Empty);
                });

            ReverseCommand = new ActionCommand(
                _ => Points.Count >= 2,
                _ =>
                {
                    BeginEdit?.Invoke(this, EventArgs.Empty);
                    foreach (var p in Points) p.Position = 1.0 - p.Position;
                    SyncToModel();
                    EndEdit?.Invoke(this, EventArgs.Empty);
                });

            Points.CollectionChanged += OnCollectionChanged;

            // 初期ロード
            LoadFromModel();
        }

        // --- イベントハンドラ ---

        private void EffectItem_OnPointsChangedBySystem(object? sender, ImmutableList<GradientPoint> newPoints)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => LoadFromModel()));
        }

        private void EffectItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // カラーモード等が変更されたらプレビューを更新する
            if (e.PropertyName == nameof(ColorRampEffect.GradientCalculationType) ||
                e.PropertyName == nameof(ColorRampEffect.GradientInterpolationHSLHSVType) ||
                e.PropertyName == nameof(ColorRampEffect.GradientInterpolationType))
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => UpdatePreview()));
            }
        }

        // --- データ同期ロジック ---

        private void LoadFromModel()
        {
            var modelPoints = effectItem.Points;

            foreach (var p in Points) p.PropertyChanged -= OnPointPropertyChanged;
            Points.Clear();

            foreach (var p in modelPoints)
            {
                var vmPoint = new GradientPoint(p);
                vmPoint.BeginEditCommand = ItemBeginEditCommand;
                vmPoint.EndEditCommand = ItemEndEditCommand;
                vmPoint.PropertyChanged += OnPointPropertyChanged;
                Points.Add(vmPoint);
            }

            UpdatePreview();
            UpdateButtons();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            SyncToModel();
            UpdateButtons();
        }

        private void OnPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GradientPoint.Position) || e.PropertyName == nameof(GradientPoint.Color))
            {
                SyncToModel();
            }
        }

        private void SyncToModel()
        {
            var newImmutable = Points.OrderBy(x => x.Position).Select(x => new GradientPoint(x)).ToImmutableList();

            // EffectItemへセット (Undo履歴対応)
            effectItem.EditorPoints = newImmutable;

            // 複数選択時の対応
            if (properties.Length > 1)
            {
                foreach (var prop in properties.Skip(1))
                {
                    if (prop.PropertyOwner is ColorRampEffect otherEffect)
                    {
                        otherEffect.EditorPoints = newImmutable;
                    }
                }
            }

            UpdatePreview();
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
        }

        public void CopyToOtherItems()
        {
            SyncToModel();
        }

        // --- プレビュー生成 ---

        public void UpdatePreview(object? sender = null)
        {
            var newStops = new GradientStopCollection();
            var sortedPoints = Points.OrderBy(x => x.Position).ToList();
            if (sortedPoints.Count == 0) return;

            if (effectItem.GradientCalculationType != ColorRampEffect.GradientType.RGB)
            {
                // HSV/HSLの場合は細かく分割してストップを追加し、滑らかに見せる
                for (int i = 0; i < sortedPoints.Count - 1; i++)
                {
                    var p1 = sortedPoints[i];
                    var p2 = sortedPoints[i + 1];
                    int steps = 10;
                    for (int j = 0; j <= steps; j++)
                    {
                        float t = (float)j / steps;
                        float globalPos = (float)(p1.Position + (p2.Position - p1.Position) * t);
                        Color c = InterpolateColor(p1.Color, p2.Color, t, effectItem.GradientCalculationType, effectItem.GradientInterpolationHSLHSVType);
                        newStops.Add(new GradientStop(c, globalPos));
                    }
                }
            }
            else
            {
                // RGBの場合はそのまま
                foreach (var p in sortedPoints) newStops.Add(new GradientStop(p.Color, p.Position));
            }
            GradientStops = newStops;
            OnPropertyChanged(nameof(GradientStops));
        }

        private Color InterpolateColor(Color c1, Color c2, float t, ColorRampEffect.GradientType type, ColorRampEffect.GradientInterpolationHSLHSV hueType)
        {
            var mode = type == ColorRampEffect.GradientType.HSL ? HsvHslHelper.ColorSpace.HSL : HsvHslHelper.ColorSpace.HSV;
            var h1 = HsvHslHelper.FromColor(c1, mode);
            var h2 = HsvHslHelper.FromColor(c2, mode);

            float targetH2 = h2.H;
            float diffH = h2.H - h1.H;

            switch (hueType)
            {
                case ColorRampEffect.GradientInterpolationHSLHSV.Near:
                    if (diffH > 180) targetH2 -= 360; else if (diffH < -180) targetH2 += 360; break;
                case ColorRampEffect.GradientInterpolationHSLHSV.Far:
                    if (diffH > 0 && diffH <= 180) targetH2 -= 360; else if (diffH <= 0 && diffH > -180) targetH2 += 360; break;
                case ColorRampEffect.GradientInterpolationHSLHSV.Clockwise:
                    if (targetH2 < h1.H) targetH2 += 360; break;
                case ColorRampEffect.GradientInterpolationHSLHSV.CounterClockwise:
                    if (targetH2 > h1.H) targetH2 -= 360; break;
            }

            float h = (Lerp(h1.H, targetH2, t) % 360 + 360) % 360;
            float s = Lerp(h1.S, h2.S, t);
            float v = Lerp(h1.V, h2.V, t);
            float a = Lerp(h1.A, h2.A, t);

            return HsvHslHelper.ToColor(h, s, v, a, mode);
        }
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public void Dispose()
        {
            effectItem.OnPointsChangedBySystem -= EffectItem_OnPointsChangedBySystem;
            effectItem.PropertyChanged -= EffectItem_PropertyChanged;
            if (Points != null)
            {
                Points.CollectionChanged -= OnCollectionChanged;
                foreach (var p in Points)
                    p.PropertyChanged -= OnPointPropertyChanged;
            }
        }
    }
}