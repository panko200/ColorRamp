using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;

namespace ColorRamp
{
    public partial class GradientEditorControl : UserControl, IPropertyEditorControl, IPropertyEditorControl2
    {
        public event EventHandler? BeginEdit;
        public event EventHandler? EndEdit;

        private IEditorInfo? _editorInfo;
        private readonly List<AnimationSlider> _activeSliders = new();
        private readonly List<Rectangle> _seekLineRects = new();

        public GradientEditorControl()
        {
            InitializeComponent();
            DataContextChanged += DataContextChangedHandler;
            SeekLineCanvas.SizeChanged += (s, e) => RedrawSeekLines();
        }

        public void SetEditorInfo(IEditorInfo info)
        {
            _editorInfo = info;
            foreach (var slider in _activeSliders)
                slider.SetEditorInfo(info);
            if (DataContext is GradientEditorViewModel vm)
                vm.SetEditorInfo(info);
        }

        private void DataContextChangedHandler(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is GradientEditorViewModel oldVm)
                oldVm.PropertyChanged -= Vm_PropertyChanged;

            if (e.NewValue is GradientEditorViewModel newVm)
            {
                newVm.BeginEdit += (s, args) => BeginEdit?.Invoke(this, args);
                newVm.EndEdit += (s, args) => EndEdit?.Invoke(this, args);
                newVm.PropertyChanged += Vm_PropertyChanged;

                if (_editorInfo != null)
                    newVm.SetEditorInfo(_editorInfo);

                CurveEditor.SetPoints(newVm.CurvePoints);
            }
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GradientEditorViewModel.SeekLinePositions))
                RedrawSeekLines();

            if (e.PropertyName == nameof(GradientEditorViewModel.IsCurveMode))
            {
                if (DataContext is GradientEditorViewModel vm)
                    CurveEditor.SetPoints(vm.CurvePoints);
            }
        }

        private void CurveEditor_BeginEdit(object? sender, EventArgs e) => BeginEdit?.Invoke(this, e);
        private void CurveEditor_EndEdit(object? sender, EventArgs e) => EndEdit?.Invoke(this, e);

        private void CurveEditor_PointsChanged(object? sender, ImmutableList<GradientCurvePoint> pts)
        {
            if (DataContext is GradientEditorViewModel vm)
                vm.SetCurvePoints(pts);
        }

        private void CurveResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not GradientEditorViewModel vm) return;
            BeginEdit?.Invoke(this, EventArgs.Empty);
            var defaultPts = GradientCurveHelper.CreateDefault();
            vm.SetCurvePoints(defaultPts);
            CurveEditor.SetPoints(defaultPts);
            EndEdit?.Invoke(this, EventArgs.Empty);
        }

        private void RedrawSeekLines()
        {
            if (DataContext is not GradientEditorViewModel vm) return;
            double canvasWidth = SeekLineCanvas.ActualWidth;
            var positions = vm.SeekLinePositions;
            int needed = positions.Count;

            while (_seekLineRects.Count < needed * 2)
            {
                var back = new Rectangle { Width = 3, Height = 26, Fill = Brushes.Black, Opacity = 0.7 };
                var front = new Rectangle { Width = 1, Height = 26, Fill = Brushes.White, Opacity = 0.9 };
                SeekLineCanvas.Children.Add(back);
                SeekLineCanvas.Children.Add(front);
                _seekLineRects.Add(back);
                _seekLineRects.Add(front);
            }

            for (int i = 0; i < _seekLineRects.Count / 2; i++)
            {
                var back = _seekLineRects[i * 2];
                var front = _seekLineRects[i * 2 + 1];
                if (i < needed && canvasWidth > 0)
                {
                    double x = positions[i] * canvasWidth;
                    Canvas.SetLeft(back, x - 1.0);
                    Canvas.SetLeft(front, x);
                    back.Visibility = Visibility.Visible;
                    front.Visibility = Visibility.Visible;
                }
                else
                {
                    back.Visibility = Visibility.Collapsed;
                    front.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ColorPicker_EndEdit(object? sender, EventArgs e)
        {
            if (DataContext is GradientEditorViewModel vm)
            {
                vm.CopyToOtherItems();
                EndEdit?.Invoke(this, e);
            }
        }

        private void GradientPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && DataContext is GradientEditorViewModel vm)
            {
                var pos = e.GetPosition(element);
                double effectiveWidth = element.ActualWidth - 8;
                if (effectiveWidth <= 0) return;
                double percent = Math.Clamp(pos.X / effectiveWidth, 0.0, 1.0);
                if (vm.AddCommand.CanExecute(percent)) vm.AddCommand.Execute(percent);
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.DataContext is GradientPoint point)
                point.BeginEditCommand?.Execute(null);
        }

        private void Thumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is Thumb thumb && thumb.DataContext is GradientPoint point)
            {
                var parentCanvas = FindParent<Canvas>(thumb);
                if (parentCanvas != null && parentCanvas.ActualWidth > 0)
                {
                    double deltaPercent = e.HorizontalChange / parentCanvas.ActualWidth;
                    point.PositionValue = Math.Clamp(point.PositionValue + deltaPercent, 0.0, 1.0);
                }
            }
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.DataContext is GradientPoint point)
                point.EndEditCommand?.Execute(null);
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox t)
            {
                t.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }

        private void PositionSlider_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not AnimationSlider slider) return;
            if (slider.DataContext is not GradientPoint point) return;

            // ★ XAMLとの連携切れをここで修正！
            slider.Animation = point.PositionAnim;
            slider.Animations = new[] { point.PositionAnim };

            if (_editorInfo != null) slider.SetEditorInfo(_editorInfo);
            slider.BeginEdit += PositionSlider_BeginEdit;
            slider.EndEdit += PositionSlider_EndEdit;
            _activeSliders.Add(slider);
        }

        private void PositionSlider_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not AnimationSlider slider) return;
            slider.BeginEdit -= PositionSlider_BeginEdit;
            slider.EndEdit -= PositionSlider_EndEdit;
            slider.Animations = null;
            _activeSliders.Remove(slider);
        }

        private void PositionSlider_BeginEdit(object? sender, EventArgs e) => BeginEdit?.Invoke(this, e);
        private void PositionSlider_EndEdit(object? sender, EventArgs e) => EndEdit?.Invoke(this, e);

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindParent<T>(parentObject);
        }
    }
}

