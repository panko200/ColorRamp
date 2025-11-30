using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace ColorRamp
{
    [VideoEffect("カラーランプ", ["加工"], ["ColorRamp","カラーランプ"])]
    internal class ColorRampEffect : VideoEffectBase, INotifyPropertyChanged
    {
        public new event PropertyChangedEventHandler? PropertyChanged;

        public override string Label => "カラーランプ";

        // 実体としてのフィールド
        private ImmutableList<GradientPoint> _points = ImmutableList<GradientPoint>.Empty;

        // UIへの通知イベント（ViewModelがこれを購読する）
        public event EventHandler<ImmutableList<GradientPoint>>? OnPointsChangedBySystem;

        [Display(GroupName = "カラーランプ", Name = "強度", Description = "0%=元画像, 100%=完全グラデ適用")]
        [AnimationSlider("F1", "％", 0, 100)]
        public Animation ColorRampFactor { get; } = new Animation(100, 0, 100);

        [Display(GroupName = "カラーランプ", Name = "不透明度読み込み", Description = "不透明度を使ってグラデーションします。透明なほど黒側に近づきます。透明な部分はグラデーションによって塗りつぶしされます。")]
        [ToggleSlider]
        public bool LoadOpacityToggle { get => loadOpacityToggle; set => Set(ref loadOpacityToggle, value); }
        bool loadOpacityToggle = false;

        // 1. 公開プロパティ (YMM4本体、Undo/Redo、保存読み込み用)
        // ここに値が入るのは プロジェクト読み込み時 や Undo/Redo時
        [Display(GroupName = "カラーランプ", Name = "グラデーション", Description = "左が黒側、右が白側。ポイントを追加・編集します")]
        [GradientEditor]
        public ImmutableList<GradientPoint> Points
        {
            get => _points;
            set
            {
                if (_points == value) return;
                Set(ref _points, value);

                // UI側に「システム側から変更があったよ」と通知する
                OnPointsChangedBySystem?.Invoke(this, value);
            }
        }

        // 2. 内部プロパティ (ViewModelからの操作用)
        public ImmutableList<GradientPoint> EditorPoints
        {
            get => _points;
            set
            {
                if (_points == value) return;
                // 第3引数に "Points" を指定することで、「Pointsプロパティが変更された」としてUndo履歴に登録する
                Set(ref _points, value, nameof(Points));
            }
        }

        public ColorRampEffect()
        {
            _points = ImmutableList.Create(
                new GradientPoint(0.0, Colors.Black),
                new GradientPoint(1.0, Colors.White)
            );
        }

        [Display(GroupName = "カラーランプ", Name = "カラーモード", Description = "グラデーションの仕方をRGB方式かHSL方式かHSV方式に切り替えられます。")]
        [EnumComboBox]
        public GradientType GradientCalculationType { get => gradientCalculationType; set => Set(ref gradientCalculationType, value); }
        private GradientType gradientCalculationType = GradientType.RGB;

        public enum GradientType
        {
            [Display(Name = "RGB")] RGB,
            [Display(Name = "HSL")] HSL,
            [Display(Name = "HSV")] HSV
        }

        [Display(GroupName = "カラーランプ", Name = "色の補間", Description = "グラデーションのイージングを切り替えできます。")]
        [EnumComboBox]
        public GradientInterpolation GradientInterpolationType { get => gradientInterpolationType; set => Set(ref gradientInterpolationType, value); }
        private GradientInterpolation gradientInterpolationType = GradientInterpolation.Linear;

        public enum GradientInterpolation
        {
            [Display(Name = "リニア")] Linear,
            [Display(Name = "イーズ")] Ease,
            [Display(Name = "カーディナル")] Cardinal,
            [Display(Name = "Bスプライン")] BSpline,
            [Display(Name = "一定")] Constant
        }

        [Display(GroupName = "カラーランプ", Name = "HSL,HSV色の補間", Description = "グラデーションの色相環の変化方向を設定します。HSL,HSV限定設定です。")]
        [EnumComboBox]
        public GradientInterpolationHSLHSV GradientInterpolationHSLHSVType { get => gradientInterpolationHSLHSVType; set => Set(ref gradientInterpolationHSLHSVType, value); }
        private GradientInterpolationHSLHSV gradientInterpolationHSLHSVType = GradientInterpolationHSLHSV.Near;

        public enum GradientInterpolationHSLHSV
        {
            [Display(Name = "接近")] Near,
            [Display(Name = "最遠")] Far,
            [Display(Name = "時計回り")] Clockwise,
            [Display(Name = "反時計回り")] CounterClockwise
        }

        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
            => new ColorRampEffectProcessor(devices, this);

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
            => [];

        protected override IEnumerable<IAnimatable> GetAnimatables()
        {
            foreach (var p in Points)
                yield return p;
            yield return ColorRampFactor;
        }
    }
}