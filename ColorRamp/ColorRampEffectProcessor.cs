using ColorRamp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace ColorRamp
{
    internal class ColorRampEffectProcessor : IVideoEffectProcessor
    {
        readonly ColorRampEffect item;

        readonly ID2D1Effect rgbToHueEffect;
        readonly ID2D1Effect inputFormatterEffect;
        readonly ID2D1Effect mapEffect;
        readonly ColorRampMixCustomEffect mixEffect;
        readonly ID2D1Effect crossFadeEffect;
        readonly ID2D1Image finalOutput;

        ID2D1Image? input;

        readonly float[] tableR = new float[256];
        readonly float[] tableG = new float[256];
        readonly float[] tableB = new float[256];
        readonly float[] tableA = new float[256];

        ColorRampEffect.GradientInputChannel? _lastEffectiveInputChannel = null;
        ImmutableList<GradientPoint>? _lastPoints = null;
        ColorRampEffect.GradientType? _lastCalcType = null;
        ColorRampEffect.GradientInterpolation? _lastInterpType = null;
        ColorRampEffect.GradientInterpolationHSLHSV? _lastHueInterp = null;
        ColorRampEffect.CycleMode? _lastCycleMode = null;
        float _lastCycleCount = -1f;
        ImmutableList<GradientCurvePoint>? _lastCurvePoints = null;
        bool? _lastUsePerPoint = null;

        int _lastSegmentInterpolationHash = 0;

        public ColorRampEffectProcessor(IGraphicsDevicesAndContext devices, ColorRampEffect item)
        {
            this.item = item;

            rgbToHueEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.RgbToHue);
            unsafe
            {
                uint val = (uint)RgbToHueOutputColorSpace.HueSaturationValue;
                rgbToHueEffect.SetValue((int)RgbToHueProperties.OutputColorSpace, PropertyType.Enum, &val, sizeof(uint));
            }

            inputFormatterEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.ColorMatrix);
            unsafe
            {
                uint alphaMode = (uint)ColorMatrixAlphaMode.Straight;
                inputFormatterEffect.SetValue((int)ColorMatrixProperties.AlphaMode, PropertyType.Enum, &alphaMode, sizeof(uint));
                int clamp = 1;
                inputFormatterEffect.SetValue((int)ColorMatrixProperties.ClampOutput, PropertyType.Bool, &clamp, sizeof(int));
            }

            mapEffect = (ID2D1Effect)devices.DeviceContext.CreateEffect(EffectGuids.TableTransfer);
            mapEffect.SetInputEffect(0, inputFormatterEffect, true);

            mixEffect = new ColorRampMixCustomEffect(devices);
            mixEffect.SetInputEffect(0, mapEffect, true);

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
            inputFormatterEffect.SetInput(0, input, true);
            mixEffect.SetInput(1, input, true);
            crossFadeEffect.SetInput(0, input, true);
        }

        public DrawDescription Update(EffectDescription effectDescription)
        {
            var frame = effectDescription.ItemPosition.Frame;
            var length = effectDescription.ItemDuration.Frame;
            var fps = effectDescription.FPS;

            var effectiveInputChannel = item.LoadOpacityToggle
                ? ColorRampEffect.GradientInputChannel.A
                : item.InputChannel;
            var effectiveKeepAlpha = item.LoadOpacityToggle ? false : item.KeepAlpha;

            bool inputSettingChanged = _lastEffectiveInputChannel != effectiveInputChannel;

            // ★連携切れ修正
            bool anyPositionAnimated = item.Points.Any(
                p => p.EnablePositionAnimation && p.PositionAnim.AnimationType != AnimationType.なし);

            float currentCycleCount = (float)item.CycleCount.GetValue(frame, length, fps);
            bool cycleChanged = _lastCycleMode != item.GradientCycleMode ||
                                MathF.Abs(_lastCycleCount - currentCycleCount) > 0.01f;

            bool curveChanged = _lastCurvePoints != item.CurvePoints;
            bool perPointChanged = _lastUsePerPoint != item.UsePerPointInterpolation;

            int segHash = ComputeSegmentHash(item.Points);
            bool segInterpChanged = _lastSegmentInterpolationHash != segHash;

            bool gradientChanged =
                anyPositionAnimated || cycleChanged || curveChanged ||
                perPointChanged || segInterpChanged ||
                _lastPoints != item.Points ||
                _lastCalcType != item.GradientCalculationType ||
                _lastInterpType != item.GradientInterpolationType ||
                _lastHueInterp != item.GradientInterpolationHSLHSVType;

            if (inputSettingChanged)
            {
                UpdateInputPipeline(effectiveInputChannel);
                _lastEffectiveInputChannel = effectiveInputChannel;
            }

            if (gradientChanged)
            {
                UpdateGradientTables(frame, length, fps, currentCycleCount);

                if (!anyPositionAnimated)
                {
                    _lastPoints = item.Points;
                    _lastCalcType = item.GradientCalculationType;
                    _lastInterpType = item.GradientInterpolationType;
                    _lastHueInterp = item.GradientInterpolationHSLHSVType;
                    _lastCycleMode = item.GradientCycleMode;
                    _lastCycleCount = currentCycleCount;
                    _lastCurvePoints = item.CurvePoints;
                    _lastUsePerPoint = item.UsePerPointInterpolation;
                    _lastSegmentInterpolationHash = segHash;
                }
            }

            mixEffect.MixMode = (int)item.MixColorSpace;
            mixEffect.KeepCh1 = item.KeepCh1;
            mixEffect.KeepCh2 = item.KeepCh2;
            mixEffect.KeepCh3 = item.KeepCh3;
            mixEffect.KeepAlpha = effectiveKeepAlpha;

            float factor = (float)item.ColorRampFactor.GetValue(frame, length, fps) / 100.0f;
            factor = Math.Clamp(factor, 0f, 1f);
            factor = 1.0f - factor;
            crossFadeEffect.SetValue(0, factor);

            return effectDescription.DrawDescription;
        }

        private static int ComputeSegmentHash(ImmutableList<GradientPoint> points)
        {
            int h = 0;
            foreach (var p in points)
                h = HashCode.Combine(h, (int)p.SegmentInterpolation);
            return h;
        }

        private void UpdateInputPipeline(ColorRampEffect.GradientInputChannel channel)
        {
            Matrix5x4 matrix = default;
            switch (channel)
            {
                case ColorRampEffect.GradientInputChannel.R:
                    inputFormatterEffect.SetInput(0, input, true);
                    matrix.M11 = 1f; matrix.M12 = 1f; matrix.M13 = 1f; matrix.M14 = 1f; break;
                case ColorRampEffect.GradientInputChannel.G:
                    inputFormatterEffect.SetInput(0, input, true);
                    matrix.M21 = 1f; matrix.M22 = 1f; matrix.M23 = 1f; matrix.M24 = 1f; break;
                case ColorRampEffect.GradientInputChannel.B:
                    inputFormatterEffect.SetInput(0, input, true);
                    matrix.M31 = 1f; matrix.M32 = 1f; matrix.M33 = 1f; matrix.M34 = 1f; break;
                case ColorRampEffect.GradientInputChannel.L:
                    inputFormatterEffect.SetInput(0, input, true);
                    const float r = 0.299f, g = 0.587f, b = 0.114f;
                    matrix.M11 = r; matrix.M12 = r; matrix.M13 = r; matrix.M14 = r;
                    matrix.M21 = g; matrix.M22 = g; matrix.M23 = g; matrix.M24 = g;
                    matrix.M31 = b; matrix.M32 = b; matrix.M33 = b; matrix.M34 = b; break;
                case ColorRampEffect.GradientInputChannel.A:
                    inputFormatterEffect.SetInput(0, input, true);
                    matrix.M41 = 1f; matrix.M42 = 1f; matrix.M43 = 1f; matrix.M44 = 1f; break;
                case ColorRampEffect.GradientInputChannel.H:
                case ColorRampEffect.GradientInputChannel.S:
                case ColorRampEffect.GradientInputChannel.V:
                    inputFormatterEffect.SetInputEffect(0, rgbToHueEffect, true);
                    if (channel == ColorRampEffect.GradientInputChannel.H)
                    { matrix.M11 = 1f; matrix.M12 = 1f; matrix.M13 = 1f; matrix.M14 = 1f; }
                    else if (channel == ColorRampEffect.GradientInputChannel.S)
                    { matrix.M21 = 1f; matrix.M22 = 1f; matrix.M23 = 1f; matrix.M24 = 1f; }
                    else
                    { matrix.M31 = 1f; matrix.M32 = 1f; matrix.M33 = 1f; matrix.M34 = 1f; }
                    break;
            }
            inputFormatterEffect.SetValue(0, matrix);
        }

        private readonly struct ResolvedPoint
        {
            public readonly double Pos;
            public readonly Color Color;
            public readonly SegmentInterpolationMode SegInterp;
            public ResolvedPoint(double pos, Color color, SegmentInterpolationMode seg)
            { Pos = pos; Color = color; SegInterp = seg; }
        }

        private void UpdateGradientTables(long frame, long length, int fps, float cycleCount)
        {
            var pts = item.Points
                .Select(p => new ResolvedPoint(
                    // ★連携切れ修正
                    Math.Clamp(p.PositionAnim.GetValue(frame, length, fps) / 100.0, 0.0, 1.0),
                    p.Color,
                    p.SegmentInterpolation))
                .OrderBy(p => p.Pos)
                .ToList();

            if (pts.Count < 2)
                pts = new List<ResolvedPoint>
                {
                    new(0.0, Colors.Black, SegmentInterpolationMode.Linear),
                    new(1.0, Colors.White, SegmentInterpolationMode.Linear)
                };

            var calcType = item.GradientCalculationType;
            var globalInterp = item.GradientInterpolationType;
            var hueInterp = item.GradientInterpolationHSLHSVType;
            var cycleMode = item.GradientCycleMode;
            var curvePts = item.CurvePoints;
            bool usePerPoint = item.UsePerPointInterpolation;

            bool useCurve = !usePerPoint &&
                            globalInterp == ColorRampEffect.GradientInterpolation.Curve;

            bool useGlobalSpline = !usePerPoint && (
                globalInterp == ColorRampEffect.GradientInterpolation.Cardinal ||
                globalInterp == ColorRampEffect.GradientInterpolation.BSpline);

            for (int i = 0; i < 256; i++)
            {
                float t = ApplyCycle(i / 255.0f, cycleMode, cycleCount);

                Color resultColor;

                if (useGlobalSpline)
                {
                    resultColor = GetGlobalSplineColor(pts, t,
                        globalInterp == ColorRampEffect.GradientInterpolation.BSpline,
                        useCurve, curvePts, calcType, hueInterp);
                }
                else
                {
                    int segIdx = FindSegment(pts, t);
                    var rp1 = pts[segIdx];
                    var rp2 = pts[Math.Min(segIdx + 1, pts.Count - 1)];

                    float localT = rp2.Pos > rp1.Pos
                        ? (float)((t - rp1.Pos) / (rp2.Pos - rp1.Pos))
                        : 0f;

                    var segInterp = usePerPoint ? rp1.SegInterp : ToSegmentMode(globalInterp);

                    if (segInterp == SegmentInterpolationMode.Cardinal ||
                        segInterp == SegmentInterpolationMode.BSpline)
                    {
                        resultColor = GetPerSegmentSplineColor(pts, segIdx, localT,
                            segInterp == SegmentInterpolationMode.BSpline,
                            calcType, hueInterp);
                    }
                    else
                    {
                        float interpT = segInterp switch
                        {
                            SegmentInterpolationMode.Ease => localT * localT * (3 - 2 * localT),
                            SegmentInterpolationMode.Constant => 0f,
                            _ => localT
                        };

                        if (useCurve)
                            interpT = (float)GradientCurveHelper.Evaluate(curvePts, localT);

                        resultColor = InterpolateColor(rp1.Color, rp2.Color, interpT, calcType, hueInterp);
                    }
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

        private static SegmentInterpolationMode ToSegmentMode(ColorRampEffect.GradientInterpolation g) => g switch
        {
            ColorRampEffect.GradientInterpolation.Ease => SegmentInterpolationMode.Ease,
            ColorRampEffect.GradientInterpolation.Cardinal => SegmentInterpolationMode.Cardinal,
            ColorRampEffect.GradientInterpolation.BSpline => SegmentInterpolationMode.BSpline,
            ColorRampEffect.GradientInterpolation.Constant => SegmentInterpolationMode.Constant,
            _ => SegmentInterpolationMode.Linear
        };

        private static int FindSegment(List<ResolvedPoint> pts, float t)
        {
            for (int k = 0; k < pts.Count - 1; k++)
                if (pts[k + 1].Pos > t) return k;
            return pts.Count - 2;
        }

        private static float ApplyCycle(float t, ColorRampEffect.CycleMode mode, float count)
        {
            if (mode == ColorRampEffect.CycleMode.None || count <= 1f) return t;
            float raw = t * count;
            float frac = raw - MathF.Floor(raw);
            int cycle = (int)MathF.Floor(raw);
            return mode == ColorRampEffect.CycleMode.Repeat
                ? frac
                : (cycle % 2 == 0 ? frac : 1f - frac);
        }

        private Color GetGlobalSplineColor(List<ResolvedPoint> pts, float t,
            bool isBSpline, bool useCurve, IReadOnlyList<GradientCurvePoint> curvePts,
            ColorRampEffect.GradientType calcType, ColorRampEffect.GradientInterpolationHSLHSV hueType)
        {
            int i = FindSegment(pts, t);
            var p1 = pts[i];
            var p2 = pts[Math.Min(i + 1, pts.Count - 1)];
            var p0 = pts[Math.Max(i - 1, 0)];
            var p3 = pts[Math.Min(i + 2, pts.Count - 1)];

            float localT = p2.Pos > p1.Pos ? (float)((t - p1.Pos) / (p2.Pos - p1.Pos)) : 0f;
            if (useCurve) localT = (float)GradientCurveHelper.Evaluate(curvePts, localT);

            float rv, gv, bv, av;
            if (!isBSpline)
            {
                rv = Cardinal(p0.Color.ScR, p1.Color.ScR, p2.Color.ScR, p3.Color.ScR, localT);
                gv = Cardinal(p0.Color.ScG, p1.Color.ScG, p2.Color.ScG, p3.Color.ScG, localT);
                bv = Cardinal(p0.Color.ScB, p1.Color.ScB, p2.Color.ScB, p3.Color.ScB, localT);
                av = Cardinal(p0.Color.ScA, p1.Color.ScA, p2.Color.ScA, p3.Color.ScA, localT);
            }
            else
            {
                rv = BSpline(p0.Color.ScR, p1.Color.ScR, p2.Color.ScR, p3.Color.ScR, localT);
                gv = BSpline(p0.Color.ScG, p1.Color.ScG, p2.Color.ScG, p3.Color.ScG, localT);
                bv = BSpline(p0.Color.ScB, p1.Color.ScB, p2.Color.ScB, p3.Color.ScB, localT);
                av = BSpline(p0.Color.ScA, p1.Color.ScA, p2.Color.ScA, p3.Color.ScA, localT);
            }
            return Color.FromScRgb(Math.Clamp(av, 0, 1), Math.Clamp(rv, 0, 1), Math.Clamp(gv, 0, 1), Math.Clamp(bv, 0, 1));
        }

        private Color GetPerSegmentSplineColor(List<ResolvedPoint> pts, int segIdx, float localT,
            bool isBSpline, ColorRampEffect.GradientType calcType, ColorRampEffect.GradientInterpolationHSLHSV hueType)
        {
            var p1 = pts[segIdx];
            var p2 = pts[Math.Min(segIdx + 1, pts.Count - 1)];
            var p0 = pts[Math.Max(segIdx - 1, 0)];
            var p3 = pts[Math.Min(segIdx + 2, pts.Count - 1)];

            float rv, gv, bv, av;
            if (!isBSpline)
            {
                rv = Cardinal(p0.Color.ScR, p1.Color.ScR, p2.Color.ScR, p3.Color.ScR, localT);
                gv = Cardinal(p0.Color.ScG, p1.Color.ScG, p2.Color.ScG, p3.Color.ScG, localT);
                bv = Cardinal(p0.Color.ScB, p1.Color.ScB, p2.Color.ScB, p3.Color.ScB, localT);
                av = Cardinal(p0.Color.ScA, p1.Color.ScA, p2.Color.ScA, p3.Color.ScA, localT);
            }
            else
            {
                rv = BSpline(p0.Color.ScR, p1.Color.ScR, p2.Color.ScR, p3.Color.ScR, localT);
                gv = BSpline(p0.Color.ScG, p1.Color.ScG, p2.Color.ScG, p3.Color.ScG, localT);
                bv = BSpline(p0.Color.ScB, p1.Color.ScB, p2.Color.ScB, p3.Color.ScB, localT);
                av = BSpline(p0.Color.ScA, p1.Color.ScA, p2.Color.ScA, p3.Color.ScA, localT);
            }
            return Color.FromScRgb(Math.Clamp(av, 0, 1), Math.Clamp(rv, 0, 1), Math.Clamp(gv, 0, 1), Math.Clamp(bv, 0, 1));
        }

        private Color InterpolateColor(Color c1, Color c2, float t,
            ColorRampEffect.GradientType type, ColorRampEffect.GradientInterpolationHSLHSV hueType)
        {
            if (type == ColorRampEffect.GradientType.RGB)
                return Color.FromScRgb(
                    Lerp(c1.ScA, c2.ScA, t), Lerp(c1.ScR, c2.ScR, t),
                    Lerp(c1.ScG, c2.ScG, t), Lerp(c1.ScB, c2.ScB, t));

            var mode = type == ColorRampEffect.GradientType.HSL
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

        private float Cardinal(float v0, float v1, float v2, float v3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2 * v1) + (-v0 + v2) * t + (2 * v0 - 5 * v1 + 4 * v2 - v3) * t2 + (-v0 + 3 * v1 - 3 * v2 + v3) * t3);
        }

        private float BSpline(float v0, float v1, float v2, float v3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return (1.0f / 6.0f) * ((-t3 + 3 * t2 - 3 * t + 1) * v0 + (3 * t3 - 6 * t2 + 4) * v1 + (-3 * t3 + 3 * t2 + 3 * t + 1) * v2 + t3 * v3);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private unsafe void SetTableValue(ID2D1Effect effect, int index, float[] data)
        {
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                effect.SetValue(index, PropertyType.Blob, (void*)ptr, data.Length * sizeof(float));
            }
            finally { handle.Free(); }
        }

        private enum RgbToHueProperties { OutputColorSpace = 0 }
        private enum RgbToHueOutputColorSpace { HueSaturationValue = 0, HueSaturationLightness = 1 }
        private enum ColorMatrixProperties { Matrix = 0, AlphaMode = 1, ClampOutput = 2 }
        private enum ColorMatrixAlphaMode { Premultiplied = 0, Straight = 1 }
    }
}