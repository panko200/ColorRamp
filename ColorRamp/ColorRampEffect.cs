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
        public override string Label => "カラーランプ";

        // 実体としてのフィールド
        private ImmutableList<GradientPoint> _points = ImmutableList<GradientPoint>.Empty;

        // UIへの通知イベント（ViewModelがこれを購読する）
        public event EventHandler<ImmutableList<GradientPoint>>? OnPointsChangedBySystem;

        [Display(GroupName = "カラーランプ", Name = "強度", Description = "0%=元画像, 100%=完全グラデ適用")]
        [AnimationSlider("F1", "％", 0, 100)]
        public Animation ColorRampFactor { get; } = new Animation(100, 0, 100);

        // ★追加: 入力チャンネルの定義
        public enum GradientInputChannel
        {
            [Display(Name = "R (赤)")] R,
            [Display(Name = "G (緑)")] G,
            [Display(Name = "B (青)")] B,
            [Display(Name = "H (色相)")] H,
            [Display(Name = "S (彩度)")] S,
            [Display(Name = "L (輝度)")] L,
            [Display(Name = "V (明度)")] V,
            [Display(Name = "A (不透明度)")] A // ★追加
        }

        // ★追加: 入力チャンネル選択 (デフォルトはL)
        [Display(GroupName = "カラーランプ", Name = "入力値", Description = "グラデーションの参照元となるチャンネルを選択します。")]
        [EnumComboBox]
        public GradientInputChannel InputChannel { get => inputChannel; set => Set(ref inputChannel, value); }
        private GradientInputChannel inputChannel = GradientInputChannel.L;

        // --- ブリッジパターンの実装 ---

        // 1. 公開プロパティ (YMM4本体、Undo/Redo、保存読み込み用)
        [Display(GroupName = "カラーランプ", Name = "グラデーション", Description = "左が黒側、右が白側。ポイントを追加・編集します")]
        [GradientEditor]
        public ImmutableList<GradientPoint> Points
        {
            get => _points;
            set
            {
                if (_points == value) return;
                Set(ref _points, value);
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

        // ★追加: 合成モード設定
        [Display(GroupName = "合成設定", Name = "合成モード", Description = "元画像とグラデーション結果をどの色空間で合成するか選択します。")]
        [EnumComboBox]
        public MixColorSpaceType MixColorSpace { get => mixColorSpace; set => Set(ref mixColorSpace, value); }
        private MixColorSpaceType mixColorSpace = MixColorSpaceType.RGB;

        public enum MixColorSpaceType
        {
            [Display(Name = "RGB")] RGB,
            [Display(Name = "HSL")] HSL,
            [Display(Name = "HSV")] HSV
        }

        [Display(GroupName = "合成設定", Name = "CH1 維持 (R / H)", Description = "ONにすると、このチャンネル成分はグラデーションではなく元画像の値を使用します。\nRGBモード: 赤(Red)\nHSL/HSVモード: 色相(Hue)")]
        [ToggleSlider]
        public bool KeepCh1 { get => keepCh1; set => Set(ref keepCh1, value); }
        bool keepCh1 = false;

        [Display(GroupName = "合成設定", Name = "CH2 維持 (G / S)", Description = "ONにすると、このチャンネル成分はグラデーションではなく元画像の値を使用します。\nRGBモード: 緑(Green)\nHSL/HSVモード: 彩度(Saturation)")]
        [ToggleSlider]
        public bool KeepCh2 { get => keepCh2; set => Set(ref keepCh2, value); }
        bool keepCh2 = false;

        [Display(GroupName = "合成設定", Name = "CH3 維持 (B / L / V)", Description = "ONにすると、このチャンネル成分はグラデーションではなく元画像の値を使用します。\nRGBモード: 青(Blue)\nHSLモード: 輝度(Luminance)\nHSVモード: 明度(Value)")]
        [ToggleSlider]
        public bool KeepCh3 { get => keepCh3; set => Set(ref keepCh3, value); }
        bool keepCh3 = false;

        [Display(GroupName = "合成設定", Name = "不透明度(A) 維持", Description = "ONにすると、グラデーションの不透明度ではなく、元画像の不透明度を使用します。")]
        [ToggleSlider]
        public bool KeepAlpha { get => keepAlpha; set => Set(ref keepAlpha, value); }
        bool keepAlpha = true;

        public enum GradientInterpolationHSLHSV
        {
            [Display(Name = "接近")] Near,
            [Display(Name = "最遠")] Far,
            [Display(Name = "時計回り")] Clockwise,
            [Display(Name = "反時計回り")] CounterClockwise
        }

        // ★変更: 名前と説明を少し調整（機能は「Aトグル」として優先動作）
        [Display(GroupName = "互換性", Name = "互換不透明度を使用", Description = "オンにすると上記の設定に関わらず、不透明度(Alpha)を入力値として使用します。\n過去バージョンのColorRampを保持するための設定です。")]
        [ToggleSlider]
        public bool LoadOpacityToggle { get => loadOpacityToggle; set => Set(ref loadOpacityToggle, value); }
        bool loadOpacityToggle = false;

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