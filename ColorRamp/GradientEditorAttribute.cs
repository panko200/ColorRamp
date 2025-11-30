using System.Windows;
using System.Windows.Data;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Views.Converters;

namespace ColorRamp
{
    internal class GradientEditorAttribute : PropertyEditorAttribute2
    {
        public override FrameworkElement Create()
        {
            return new GradientEditorControl();
        }

        public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
        {
            if (control is not GradientEditorControl editor)
                return;
            // ★サンプル通り: ViewModel作成・セット
            editor.DataContext = new GradientEditorViewModel(itemProperties);
        }

        public override void ClearBindings(FrameworkElement control)
        {
            if (control is not GradientEditorControl editor)
                return;
            // ★サンプル通り: Dispose
            if (editor.DataContext is IDisposable disposable)
                disposable.Dispose();
            editor.DataContext = null;
        }
    }
}