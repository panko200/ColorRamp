namespace ColorRamp
{
    /// <summary>
    /// 色補間カーブの制御点。
    /// X: ポイント間の局所位置 (0〜1)
    /// Y: 補間係数 (0〜1)
    /// 端点 (0,0) と (1,1) は常に固定。
    /// </summary>
    public class GradientCurvePoint
    {
        public double X { get; set; }
        public double Y { get; set; }

        public GradientCurvePoint() { }
        public GradientCurvePoint(double x, double y) { X = x; Y = y; }
        public GradientCurvePoint(GradientCurvePoint other) { X = other.X; Y = other.Y; }
    }
}
