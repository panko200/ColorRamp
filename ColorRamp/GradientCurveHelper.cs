using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ColorRamp
{
    internal static class GradientCurveHelper
    {
        /// <summary>
        /// カーブ上の x (0〜1) における y 値を単調三次エルミート（Fritsch-Carlson）で評価する。
        /// Catmull-Rom と違いオーバーシュートが発生しないため、カーブが安定して見える。
        /// pts は X 昇順でソートされていること。
        /// </summary>
        public static double Evaluate(IReadOnlyList<GradientCurvePoint> pts, double x)
        {
            int n = pts.Count;
            if (n == 0) return x;
            if (n == 1) return pts[0].Y;

            x = Math.Clamp(x, pts[0].X, pts[n - 1].X);

            // セグメント探索
            int i = 0;
            for (; i < n - 1; i++)
                if (pts[i + 1].X >= x) break;
            if (i >= n - 1) return pts[n - 1].Y;

            // --- Fritsch-Carlson 単調三次エルミート ---
            // 各区間の傾き
            double[] d = new double[n - 1];
            for (int k = 0; k < n - 1; k++)
            {
                double dx = pts[k + 1].X - pts[k].X;
                d[k] = dx > 1e-12 ? (pts[k + 1].Y - pts[k].Y) / dx : 0;
            }

            // 各点での接線
            double[] m = new double[n];
            m[0]     = d[0];
            m[n - 1] = d[n - 2];
            for (int k = 1; k < n - 1; k++)
                m[k] = (d[k - 1] + d[k]) * 0.5;

            // 単調性保証のスケーリング（Fritsch-Carlson）
            for (int k = 0; k < n - 1; k++)
            {
                if (Math.Abs(d[k]) < 1e-12) { m[k] = m[k + 1] = 0; continue; }
                double alpha = m[k]     / d[k];
                double beta  = m[k + 1] / d[k];
                double r     = alpha * alpha + beta * beta;
                if (r > 9.0)
                {
                    double s = 3.0 / Math.Sqrt(r);
                    m[k]     = s * alpha * d[k];
                    m[k + 1] = s * beta  * d[k];
                }
            }

            // 区間 i における三次エルミート補間
            double h  = pts[i + 1].X - pts[i].X;
            double t  = h > 1e-12 ? (x - pts[i].X) / h : 0;
            double t2 = t * t, t3 = t2 * t;

            double h00 =  2 * t3 - 3 * t2 + 1;
            double h10 =      t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 =      t3 -     t2;

            double y = h00 * pts[i].Y + h10 * h * m[i]
                     + h01 * pts[i + 1].Y + h11 * h * m[i + 1];

            return Math.Clamp(y, 0.0, 1.0);
        }

        public static List<System.Windows.Point> SampleCurve(
            IReadOnlyList<GradientCurvePoint> pts, int sampleCount,
            double canvasW, double canvasH)
        {
            var result = new List<System.Windows.Point>(sampleCount);
            for (int i = 0; i < sampleCount; i++)
            {
                double x = (double)i / (sampleCount - 1);
                double y = Evaluate(pts, x);
                result.Add(ToCanvas(x, y, canvasW, canvasH));
            }
            return result;
        }

        public static System.Windows.Point ToCanvas(double x, double y, double w, double h)
            => new(x * w, (1.0 - y) * h);

        public static (double X, double Y) FromCanvas(double px, double py, double w, double h)
            => (Math.Clamp(px / w, 0.0, 1.0),
                Math.Clamp(1.0 - py / h, 0.0, 1.0));

        public static ImmutableList<GradientCurvePoint> CreateDefault()
            => ImmutableList.Create(
                new GradientCurvePoint(0.0, 0.0),
                new GradientCurvePoint(1.0, 1.0));
    }
}
