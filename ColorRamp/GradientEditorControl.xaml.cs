using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace ColorRamp
{
    public partial class GradientEditorControl : UserControl, IPropertyEditorControl
    {
        public event EventHandler? BeginEdit;
        public event EventHandler? EndEdit;

        public GradientEditorControl()
        {
            InitializeComponent();
            DataContextChanged += DataContextChangedHandler;
        }

        private void DataContextChangedHandler(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is GradientEditorViewModel newVm)
            {
                newVm.BeginEdit += (s, args) => BeginEdit?.Invoke(this, args);
                newVm.EndEdit += (s, args) => EndEdit?.Invoke(this, args);
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
                    point.Position = Math.Clamp(point.Position + deltaPercent, 0.0, 1.0);
                }
            }
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.DataContext is GradientPoint point)
                point.EndEditCommand?.Execute(null);
        }

        // TextBoxでEnterキーが押されたら値を確定させる
        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (sender is TextBox t)
                {
                    // バインディングソースを更新して値を確定
                    t.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    // キー入力を処理済みにする（ビープ音防止など）
                    e.Handled = true;
                }
            }
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindParent<T>(parentObject);
        }
    }
}