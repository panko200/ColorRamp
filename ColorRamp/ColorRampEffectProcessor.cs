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
        readonly ID2D1Effect inputFormatterEffect;
        readonly ID2D1Effect mapEffect;
        readonly ID2D1Effect compositeEffect;
        readonly ID2D1Effect crossFadeEffect;
        readonly ID2D1Image finalOutput;

        ID2D1Image? input;

        // 配列を再利用してGCを減らします
        readonly float[] tableR = new float[256];
        readonly float[] tableG = new float[256];
        readonly float[] tableB = new float[256];
        readonly float[] tableA = new float[256];

        // 前回の状態を覚えておく変数
        bool? _lastLoadOpacityToggle = null;
        ImmutableList<GradientPoint>? _lastPoints = null;
        ColorRampEffect.GradientType? _lastCalcType = null;
        ColorRampEffect.GradientInterpolation? _lastInterpType = null;
        ColorRampEffect.GradientInterpolationHSLHSV? _lastHueInterp = null;

        public ColorRampEffectProcessor(IGraphicsDevicesAndContext devices, ColorRampEffect item)
        {
            this.item = item;

            inputFormatterEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.ColorMatrix);

            mapEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.TableTransfer);
            mapEffect.SetInputEffect(0, inputFormatterEffect, true);

            compositeEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.Composite);
            compositeEffect.SetValue((int)CompositeProperties.Mode, CompositeMode.DestinationIn);

            crossFadeEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.CrossFade);
            // 初期接続
            crossFadeEffect.SetInputEffect(1, mapEffect, true);

            finalOutput = crossFadeEffect.Output ?? throw new NullReferenceException("Output is null");
        }

        public ID2D1Image Output => finalOutput;

        public void Dispose()
        {
            finalOutput.Dispose();
            crossFadeEffect.Dispose();
            compositeEffect.Dispose();
            mapEffect.Dispose();
            inputFormatterEffect.Dispose();
        }

        public void ClearInput()
        {
            input = null;
            inputFormatterEffect.SetInput(0, null, true);
            compositeEffect.SetInput(0, null, true);
            compositeEffect.SetInput(1, null, true);
            crossFadeEffect.SetInput(0, null, true);
        }

        public void SetInput(ID2D1Image? input)
        {
            this.input = input;
            inputFormatterEffect.SetInput(0, input, true);
            // Compositeのソース(マスク画像)は常に元画像
            compositeEffect.SetInput(1, input, true);
            crossFadeEffect.SetInput(0, input, true);
        }

        public DrawDescription Update(EffectDescription effectDescription)
        {
            var frame = effectDescription.ItemPosition.Frame;
            var length = effectDescription.ItemDuration.Frame;
            var fps = effectDescription.FPS;

            // モード切替の検知
            bool toggleChanged = _lastLoadOpacityToggle != item.LoadOpacityToggle;

            // グラデーション設定の変更検知
            bool gradientChanged = _lastPoints != item.Points ||
                                   _lastCalcType != item.GradientCalculationType ||
                                   _lastInterpType != item.GradientInterpolationType ||
                                   _lastHueInterp != item.GradientInterpolationHSLHSVType;

            // 必要な場合のみ再計算・再設定を行う
            if (toggleChanged)
            {
                UpdateInputMatrix();
                UpdatePipelineConnection();
                _lastLoadOpacityToggle = item.LoadOpacityToggle;
            }

            if (gradientChanged)
            {
                UpdateGradientTables();
                // キャッシュ更新
                _lastPoints = item.Points;
                _lastCalcType = item.GradientCalculationType;
                _lastInterpType = item.GradientInterpolationType;
                _lastHueInterp = item.GradientInterpolationHSLHSVType;
            }

            // --- 2. 合成強度 (Factor) ---
            // これはアニメーションする可能性が高いので毎回計算してもOKですが、
            // 気になるならここも前回値と比較しても良いです。
            float factor = (float)item.ColorRampFactor.GetValue(frame, length, fps) / 100.0f;
            factor = Math.Clamp(factor, 0f, 1f);
            factor = Math.Abs(factor - 1.0f);
            crossFadeEffect.SetValue(0, factor);

            return effectDescription.DrawDescription;
        }

        private void UpdatePipelineConnection()
        {
            if (item.LoadOpacityToggle)
            {
                // [不透明度読み込みモード] -> マスク不要
                crossFadeEffect.SetInputEffect(1, mapEffect, true);
            }
            else
            {
                // [輝度読み込みモード] -> マスク必要
                compositeEffect.SetInputEffect(0, mapEffect, true);
                crossFadeEffect.SetInputEffect(1, compositeEffect, true);
            }
        }

        private void UpdateInputMatrix()
        {
            var matrix = new Matrix5x4();

            if (item.LoadOpacityToggle)
            {
                // Alpha読み込み
                matrix.M41 = 1f; matrix.M42 = 1f; matrix.M43 = 1f; matrix.M44 = 1f;
            }
            else
            {
                // 輝度読み込み
                const float r = 0.299f;
                const float g = 0.587f;
                const float b = 0.114f;
                matrix.M11 = r; matrix.M12 = r; matrix.M13 = r; matrix.M14 = r;
                matrix.M21 = g; matrix.M22 = g; matrix.M23 = g; matrix.M24 = g;
                matrix.M31 = b; matrix.M32 = b; matrix.M33 = b; matrix.M34 = b;
            }

            inputFormatterEffect.SetValue(0, matrix);
        }

        private void UpdateGradientTables()
        {
            var points = item.Points.OrderBy(p => p.Position).ToList();
            if (points.Count < 2)
            {
                // 簡易的なデフォルト値
                points = new List<GradientPoint> {
                    new GradientPoint(0, System.Windows.Media.Colors.Black),
                    new GradientPoint(1, System.Windows.Media.Colors.White)
                };
            }

            // ローカル変数の参照取得
            var calcType = item.GradientCalculationType;
            var interpType = item.GradientInterpolationType;
            var hueInterp = item.GradientInterpolationHSLHSVType;

            // 計算ループ (256回)
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

                // フィールド配列に格納 (newしない)
                tableR[i] = resultColor.ScR;
                tableG[i] = resultColor.ScG;
                tableB[i] = resultColor.ScB;
                tableA[i] = resultColor.ScA;
            }

            // フィールド配列を転送
            SetTableValue(mapEffect, (int)TableTransferProperties.RedTable, tableR);
            SetTableValue(mapEffect, (int)TableTransferProperties.GreenTable, tableG);
            SetTableValue(mapEffect, (int)TableTransferProperties.BlueTable, tableB);
            SetTableValue(mapEffect, (int)TableTransferProperties.AlphaTable, tableA);
        }

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
            else // B-Spline
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
    }
}