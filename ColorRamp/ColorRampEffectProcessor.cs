using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Collections.Immutable;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace ColorRamp
{
    internal class ColorRampEffectProcessor : IVideoEffectProcessor
    {
        readonly ColorRampEffect item;

        // エフェクトのパイプライン
        readonly ID2D1Effect rgbToHueEffect;     // HSV変換用
        readonly ID2D1Effect inputFormatterEffect; // ColorMatrix (チャンネル抽出・入力調整)
        readonly ID2D1Effect mapEffect;          // TableTransfer (グラデーションマップ)
        readonly ColorRampMixCustomEffect mixEffect;       // 合成用カスタムエフェクト
        readonly ID2D1Effect crossFadeEffect;    // 最終合成用
        readonly ID2D1Image finalOutput;

        ID2D1Image? input;

        // --- キャッシュ用フィールド ---
        readonly float[] tableR = new float[256];
        readonly float[] tableG = new float[256];
        readonly float[] tableB = new float[256];
        readonly float[] tableA = new float[256];

        // 変更検知用
        // ★変更: LoadOpacityToggleは内部でInputChannel.Aとして扱うため、個別の検知変数は削除しても良いが
        // 念のためロジック変更のトリガーとして残すか、まとめて管理します。
        // ここでは「実効的な入力チャンネル」として管理するように変更します。
        ColorRampEffect.GradientInputChannel? _lastEffectiveInputChannel = null;

        ImmutableList<GradientPoint>? _lastPoints = null;
        ColorRampEffect.GradientType? _lastCalcType = null;
        ColorRampEffect.GradientInterpolation? _lastInterpType = null;
        ColorRampEffect.GradientInterpolationHSLHSV? _lastHueInterp = null;

        public ColorRampEffectProcessor(IGraphicsDevicesAndContext devices, ColorRampEffect item)
        {
            this.item = item;

            // 1. RgbToHue (HSV/HSL変換用)
            rgbToHueEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.RgbToHue);
            unsafe
            {
                uint val = (uint)RgbToHueOutputColorSpace.HueSaturationValue;
                rgbToHueEffect.SetValue((int)RgbToHueProperties.OutputColorSpace, PropertyType.Enum, &val, sizeof(uint));
            }

            // 2. ColorMatrix (チャンネル抽出・モノクロ化)
            inputFormatterEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.ColorMatrix);
            unsafe
            {
                uint alphaMode = (uint)ColorMatrixAlphaMode.Straight;
                inputFormatterEffect.SetValue((int)ColorMatrixProperties.AlphaMode, PropertyType.Enum, &alphaMode, sizeof(uint));

                int clamp = 1;
                inputFormatterEffect.SetValue((int)ColorMatrixProperties.ClampOutput, PropertyType.Bool, &clamp, sizeof(int));
            }

            // 3. TableTransfer (グラデーションマップ)
            mapEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.TableTransfer);
            mapEffect.SetInputEffect(0, inputFormatterEffect, true);

            // 4. MixEffect (カスタムシェーダーによる合成)
            mixEffect = new ColorRampMixCustomEffect(devices);
            mixEffect.SetInputEffect(0, mapEffect, true);

            // 5. CrossFade (適用量調整)
            crossFadeEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.CrossFade);
            crossFadeEffect.SetInputEffect(1, mixEffect, true);

            finalOutput = crossFadeEffect.Output ?? throw new NullReferenceException("Output is null");
        }

        public ID2D1Image Output => finalOutput;

        public void Dispose()
        {
            finalOutput.Dispose();
            crossFadeEffect.Dispose();
            mixEffect.Dispose();
            mapEffect.Dispose();
            inputFormatterEffect.Dispose();
            rgbToHueEffect.Dispose();
        }

        public void ClearInput()
        {
            input = null;
            rgbToHueEffect.SetInput(0, null, true);
            inputFormatterEffect.SetInput(0, null, true);
            mixEffect.SetInput(0, null, true);
            mixEffect.SetInput(1, null, true);
            crossFadeEffect.SetInput(0, null, true);
        }

        public void SetInput(ID2D1Image? input)
        {
            this.input = input;

            rgbToHueEffect.SetInput(0, input, true);

            // デフォルト接続（UpdateLogicで上書きされる可能性あり）
            // IsUsingRgbToHueの判定にはitemが必要だが、SetInput時点ではまだUpdateが走っていない可能性があるため
            // 詳細はUpdateで確定させる。ここでは初期化としてつないでおく。
            inputFormatterEffect.SetInput(0, input, true);

            mixEffect.SetInput(1, input, true);
            crossFadeEffect.SetInput(0, input, true);
        }

        // ヘルパー: HSV変換が必要なチャンネルか？
        private bool IsUsingRgbToHue(ColorRampEffect.GradientInputChannel channel)
        {
            return channel == ColorRampEffect.GradientInputChannel.H ||
                   channel == ColorRampEffect.GradientInputChannel.S ||
                   channel == ColorRampEffect.GradientInputChannel.V;
        }

        public DrawDescription Update(EffectDescription effectDescription)
        {
            var frame = effectDescription.ItemPosition.Frame;
            var length = effectDescription.ItemDuration.Frame;
            var fps = effectDescription.FPS;


            // --- 0. 互換性とパラメータの正規化 ---
            // LoadOpacityToggleがONの場合、強制的に「入力=A」「KeepAlpha=OFF」として振る舞う
            var effectiveInputChannel = item.LoadOpacityToggle ? ColorRampEffect.GradientInputChannel.A : item.InputChannel;
            var effectiveKeepAlpha = item.LoadOpacityToggle ? false : item.KeepAlpha;


            // --- 1. 設定変更検知と更新 ---

            // 入力チャンネル設定が変わったか？
            bool inputSettingChanged = _lastEffectiveInputChannel != effectiveInputChannel;

            // グラデーション設定が変わったか？
            bool gradientChanged = _lastPoints != item.Points ||
                                   _lastCalcType != item.GradientCalculationType ||
                                   _lastInterpType != item.GradientInterpolationType ||
                                   _lastHueInterp != item.GradientInterpolationHSLHSVType;

            if (inputSettingChanged)
            {
                // 実効的なチャンネルを渡してパイプライン更新
                UpdateInputPipeline(effectiveInputChannel);
                _lastEffectiveInputChannel = effectiveInputChannel;
            }

            if (gradientChanged)
            {
                UpdateGradientTables();
                _lastPoints = item.Points;
                _lastCalcType = item.GradientCalculationType;
                _lastInterpType = item.GradientInterpolationType;
                _lastHueInterp = item.GradientInterpolationHSLHSVType;
            }

            // --- 2. MixEffectへのパラメータ反映 ---
            // MixModeなどのパラメータは毎フレーム送っても軽量なのでここで送る
            mixEffect.MixMode = (int)item.MixColorSpace;
            mixEffect.KeepCh1 = item.KeepCh1;
            mixEffect.KeepCh2 = item.KeepCh2;
            mixEffect.KeepCh3 = item.KeepCh3;
            // 互換性を考慮した KeepAlpha を渡す
            mixEffect.KeepAlpha = effectiveKeepAlpha;

            // --- 3. 合成強度 (Factor) ---
            float factor = (float)item.ColorRampFactor.GetValue(frame, length, fps) / 100.0f;
            factor = Math.Clamp(factor, 0f, 1f);
            factor = 1.0f - factor;
            crossFadeEffect.SetValue(0, factor);

            return effectDescription.DrawDescription;
        }

        private void UpdateInputPipeline(ColorRampEffect.GradientInputChannel channel)
        {
            // パイプライン構成: [Input] -> (RgbToHue) -> [ColorMatrix] -> [TableTransfer]

            Matrix5x4 matrix = default; // ゼロ初期化

            // 入力チャンネルに応じて行列と接続先を設定
            switch (channel)
            {
                case ColorRampEffect.GradientInputChannel.R:
                    inputFormatterEffect.SetInput(0, input, true);
                    matrix.M11 = 1f; matrix.M12 = 1f; matrix.M13 = 1f; matrix.M14 = 1f;
                    break;

                case ColorRampEffect.GradientInputChannel.G:
                    inputFormatterEffect.SetInput(0, input, true);
                    matrix.M21 = 1f; matrix.M22 = 1f; matrix.M23 = 1f; matrix.M24 = 1f;
                    break;

                case ColorRampEffect.GradientInputChannel.B:
                    inputFormatterEffect.SetInput(0, input, true);
                    matrix.M31 = 1f; matrix.M32 = 1f; matrix.M33 = 1f; matrix.M34 = 1f;
                    break;

                case ColorRampEffect.GradientInputChannel.L:
                    // Rec.601 Luminance
                    inputFormatterEffect.SetInput(0, input, true);
                    const float r = 0.299f;
                    const float g = 0.587f;
                    const float b = 0.114f;
                    matrix.M11 = r; matrix.M12 = r; matrix.M13 = r; matrix.M14 = r;
                    matrix.M21 = g; matrix.M22 = g; matrix.M23 = g; matrix.M24 = g;
                    matrix.M31 = b; matrix.M32 = b; matrix.M33 = b; matrix.M34 = b;
                    break;

                case ColorRampEffect.GradientInputChannel.A: // ★追加: Alpha
                    inputFormatterEffect.SetInput(0, input, true);
                    // 元のAlpha値を、出力のR,G,B,Aすべてにコピー
                    matrix.M41 = 1f; matrix.M42 = 1f; matrix.M43 = 1f; matrix.M44 = 1f;
                    break;

                case ColorRampEffect.GradientInputChannel.H:
                case ColorRampEffect.GradientInputChannel.S:
                case ColorRampEffect.GradientInputChannel.V:
                    // RgbToHueを経由
                    inputFormatterEffect.SetInputEffect(0, rgbToHueEffect, true);

                    if (channel == ColorRampEffect.GradientInputChannel.H)
                    {
                        matrix.M11 = 1f; matrix.M12 = 1f; matrix.M13 = 1f; matrix.M14 = 1f;
                    }
                    else if (channel == ColorRampEffect.GradientInputChannel.S)
                    {
                        matrix.M21 = 1f; matrix.M22 = 1f; matrix.M23 = 1f; matrix.M24 = 1f;
                    }
                    else // V
                    {
                        matrix.M31 = 1f; matrix.M32 = 1f; matrix.M33 = 1f; matrix.M34 = 1f;
                    }
                    break;
            }

            inputFormatterEffect.SetValue(0, matrix);
        }

        private void UpdateGradientTables()
        {
            var points = item.Points.OrderBy(p => p.Position).ToList();
            if (points.Count < 2)
            {
                points = new List<GradientPoint> {
                    new GradientPoint(0, System.Windows.Media.Colors.Black),
                    new GradientPoint(1, System.Windows.Media.Colors.White)
                };
            }

            var calcType = item.GradientCalculationType;
            var interpType = item.GradientInterpolationType;
            var hueInterp = item.GradientInterpolationHSLHSVType;

            for (int i = 0; i < 256; i++)
            {
                float t = i / 255.0f;
                System.Windows.Media.Color resultColor;

                if (interpType == ColorRampEffect.GradientInterpolation.Cardinal ||
                    interpType == ColorRampEffect.GradientInterpolation.BSpline)
                {
                    resultColor = GetSplineColor(points, t, interpType);
                }
                else
                {
                    var p1 = points.LastOrDefault(p => p.Position <= t) ?? points.First();
                    var p2 = points.FirstOrDefault(p => p.Position > t) ?? points.Last();

                    float localT = 0;
                    if (p2.Position > p1.Position)
                        localT = (t - (float)p1.Position) / ((float)p2.Position - (float)p1.Position);

                    float easedT = localT;
                    if (interpType == ColorRampEffect.GradientInterpolation.Ease)
                        easedT = localT * localT * (3 - 2 * localT);
                    else if (interpType == ColorRampEffect.GradientInterpolation.Constant)
                        easedT = 0;

                    resultColor = InterpolateColor(p1.Color, p2.Color, easedT, calcType, hueInterp);
                }

                tableR[i] = resultColor.ScR;
                tableG[i] = resultColor.ScG;
                tableB[i] = resultColor.ScB;
                tableA[i] = resultColor.ScA;
            }

            SetTableValue(mapEffect, (int)TableTransferProperties.RedTable, tableR);
            SetTableValue(mapEffect, (int)TableTransferProperties.GreenTable, tableG);
            SetTableValue(mapEffect, (int)TableTransferProperties.BlueTable, tableB);
            SetTableValue(mapEffect, (int)TableTransferProperties.AlphaTable, tableA);
        }

        // --- 補間ロジック (変更なし) ---
        private System.Windows.Media.Color InterpolateColor(System.Windows.Media.Color c1, System.Windows.Media.Color c2, float t, ColorRampEffect.GradientType type, ColorRampEffect.GradientInterpolationHSLHSV hueType)
        {
            if (type == ColorRampEffect.GradientType.RGB)
            {
                return System.Windows.Media.Color.FromScRgb(
                    Lerp(c1.ScA, c2.ScA, t),
                    Lerp(c1.ScR, c2.ScR, t),
                    Lerp(c1.ScG, c2.ScG, t),
                    Lerp(c1.ScB, c2.ScB, t));
            }
            else
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
        }

        private System.Windows.Media.Color GetSplineColor(List<GradientPoint> points, float t, ColorRampEffect.GradientInterpolation type)
        {
            int i = 0;
            for (; i < points.Count - 1; i++)
            {
                if (t <= points[i + 1].Position) break;
            }

            var p1 = points[i];
            var p2 = points[Math.Min(i + 1, points.Count - 1)];
            var p0 = points[Math.Max(i - 1, 0)];
            var p3 = points[Math.Min(i + 2, points.Count - 1)];

            float localT = 0;
            if (p2.Position > p1.Position)
                localT = (t - (float)p1.Position) / ((float)p2.Position - (float)p1.Position);

            float r, g, b, a;

            if (type == ColorRampEffect.GradientInterpolation.Cardinal)
            {
                r = Cardinal(p0.Color.ScR, p1.Color.ScR, p2.Color.ScR, p3.Color.ScR, localT);
                g = Cardinal(p0.Color.ScG, p1.Color.ScG, p2.Color.ScG, p3.Color.ScG, localT);
                b = Cardinal(p0.Color.ScB, p1.Color.ScB, p2.Color.ScB, p3.Color.ScB, localT);
                a = Cardinal(p0.Color.ScA, p1.Color.ScA, p2.Color.ScA, p3.Color.ScA, localT);
            }
            else
            {
                r = BSpline(p0.Color.ScR, p1.Color.ScR, p2.Color.ScR, p3.Color.ScR, localT);
                g = BSpline(p0.Color.ScG, p1.Color.ScG, p2.Color.ScG, p3.Color.ScG, localT);
                b = BSpline(p0.Color.ScB, p1.Color.ScB, p2.Color.ScB, p3.Color.ScB, localT);
                a = BSpline(p0.Color.ScA, p1.Color.ScA, p2.Color.ScA, p3.Color.ScA, localT);
            }

            return System.Windows.Media.Color.FromScRgb(
                Math.Clamp(a, 0, 1),
                Math.Clamp(r, 0, 1),
                Math.Clamp(g, 0, 1),
                Math.Clamp(b, 0, 1));
        }

        private float Cardinal(float v0, float v1, float v2, float v3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2 * v1) + (-v0 + v2) * t + (2 * v0 - 5 * v1 + 4 * v2 - v3) * t2 + (-v0 + 3 * v1 - 3 * v2 + v3) * t3);
        }

        private float BSpline(float v0, float v1, float v2, float v3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return (1.0f / 6.0f) * ((-t3 + 3 * t2 - 3 * t + 1) * v0 + (3 * t3 - 6 * t2 + 4) * v1 + (-3 * t3 + 3 * t2 + 3 * t + 1) * v2 + (t3) * v3);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private unsafe void SetTableValue(ID2D1Effect effect, int index, float[] data)
        {
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                int size = data.Length * sizeof(float);
                effect.SetValue(index, PropertyType.Blob, (void*)ptr, size);
            }
            finally
            {
                handle.Free();
            }
        }

        // --- Enum定義 ---

        private enum RgbToHueProperties
        {
            OutputColorSpace = 0
        }

        private enum RgbToHueOutputColorSpace
        {
            HueSaturationValue = 0,
            HueSaturationLightness = 1
        }

        private enum ColorMatrixProperties
        {
            Matrix = 0,
            AlphaMode = 1,
            ClampOutput = 2
        }

        private enum ColorMatrixAlphaMode
        {
            Premultiplied = 0,
            Straight = 1
        }
    }
}