using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace ColorRamp
{
    internal static class GrdFileIO
    {
        private const string FileSignature = "8BGR";
        private const int MaxOffset = 4096;

        // ================================================================
        // 書き出し (Export) — Version 5
        // ================================================================

        public static void Export(string path, string gradientName, IReadOnlyList<GradientPoint> points)
        {
            if (points == null || points.Count < 2)
                throw new ArgumentException("グラデーションには最低 2 点が必要です。", nameof(points));

            // ★変更: Position (Animation) → PositionValue (double) でソート
            var sorted = new List<GradientPoint>(points);
            sorted.Sort((a, b) => a.PositionValue.CompareTo(b.PositionValue));

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BEWriter(fs);

            w.WriteBytes(Encoding.ASCII.GetBytes(FileSignature));
            w.WriteU16(5);
            w.WriteU32(0x10);

            var rootFields = new List<DescField>();
            var gradEntry = BuildGradientDescriptor(gradientName, sorted);
            var grdlList = new DescList(new List<DescValue> { new DescObjc(string.Empty, "Grdn", new List<DescField>
            {
                new DescField("Grad", new DescObjc(string.Empty, "Grdn", gradEntry))
            })});

            rootFields.Add(new DescField("GrdL", grdlList));
            var rootDesc = new DescObjc(string.Empty, "null", rootFields);
            rootDesc.Write(w);
        }

        private static List<DescField> BuildGradientDescriptor(
            string gradientName, List<GradientPoint> sorted)
        {
            var fields = new List<DescField>();

            fields.Add(new DescField("Nm  ", new DescText(gradientName)));
            fields.Add(new DescField("GrdF", new DescEnum("GrdF", "CstS")));
            fields.Add(new DescField("Intr", new DescDouble(4096.0)));

            var colorStopValues = new List<DescValue>();
            foreach (var pt in sorted)
            {
                // ★変更: pt.Position → pt.PositionValue
                int offset = (int)Math.Clamp(Math.Round(pt.PositionValue * MaxOffset), 0, MaxOffset);
                var clrFields = new List<DescField>
                {
                    new DescField("Rd  ", new DescDouble(pt.Color.R)),
                    new DescField("Grn ", new DescDouble(pt.Color.G)),
                    new DescField("Bl  ", new DescDouble(pt.Color.B)),
                };
                var stopFields = new List<DescField>
                {
                    new DescField("Clr ", new DescObjc(string.Empty, "RGBC", clrFields)),
                    new DescField("Type", new DescEnum("Clry", "UsrS")),
                    new DescField("Lctn", new DescLong(offset)),
                    new DescField("Mdpn", new DescLong(50)),
                };
                colorStopValues.Add(new DescObjc(string.Empty, "Clrt", stopFields));
            }
            fields.Add(new DescField("Clrs", new DescList(colorStopValues)));

            var transStopValues = new List<DescValue>();
            foreach (var pt in sorted)
            {
                // ★変更: pt.Position → pt.PositionValue
                int offset = (int)Math.Clamp(Math.Round(pt.PositionValue * MaxOffset), 0, MaxOffset);
                double pct = pt.Color.A / 255.0 * 100.0;
                var stopFields = new List<DescField>
                {
                    new DescField("Opct", new DescUnitFloat("#Prc", pct)),
                    new DescField("Lctn", new DescLong(offset)),
                    new DescField("Mdpn", new DescLong(50)),
                };
                transStopValues.Add(new DescObjc(string.Empty, "TrnS", stopFields));
            }
            fields.Add(new DescField("Trns", new DescList(transStopValues)));

            return fields;
        }

        private abstract class DescValue
        {
            public abstract string TypeTag { get; }
            public abstract void WriteValue(BEWriter w);
        }

        private sealed class DescField
        {
            public string Key { get; }
            public DescValue Value { get; }
            public DescField(string key, DescValue value) { Key = key; Value = value; }
            public void Write(BEWriter w)
            {
                WriteId(w, Key);
                w.WriteBytes(Encoding.ASCII.GetBytes(Value.TypeTag));
                Value.WriteValue(w);
            }
        }

        private sealed class DescObjc : DescValue
        {
            private readonly string _name;
            private readonly string _classId;
            private readonly List<DescField> _fields;
            public DescObjc(string name, string classId, List<DescField> fields)
            { _name = name; _classId = classId; _fields = fields; }
            public override string TypeTag => "Objc";
            public override void WriteValue(BEWriter w)
            {
                WriteUnicodeString(w, _name);
                WriteId(w, _classId);
                w.WriteU32((uint)_fields.Count);
                foreach (var f in _fields) f.Write(w);
            }
            public void Write(BEWriter w) => WriteValue(w);
        }

        private sealed class DescList : DescValue
        {
            private readonly List<DescValue> _items;
            public DescList(List<DescValue> items) => _items = items;
            public override string TypeTag => "VlLs";
            public override void WriteValue(BEWriter w)
            {
                w.WriteU32((uint)_items.Count);
                foreach (var item in _items)
                {
                    w.WriteBytes(Encoding.ASCII.GetBytes(item.TypeTag));
                    item.WriteValue(w);
                }
            }
        }

        private sealed class DescText : DescValue
        {
            private readonly string _value;
            public DescText(string value) => _value = value;
            public override string TypeTag => "TEXT";
            public override void WriteValue(BEWriter w) => WriteUnicodeString(w, _value);
        }

        private sealed class DescDouble : DescValue
        {
            private readonly double _value;
            public DescDouble(double value) => _value = value;
            public override string TypeTag => "doub";
            public override void WriteValue(BEWriter w) => WriteBEDouble(w, _value);
        }

        private sealed class DescLong : DescValue
        {
            private readonly int _value;
            public DescLong(int value) => _value = value;
            public override string TypeTag => "long";
            public override void WriteValue(BEWriter w) => w.WriteU32((uint)_value);
        }

        private sealed class DescEnum : DescValue
        {
            private readonly string _type;
            private readonly string _val;
            public DescEnum(string type, string val) { _type = type; _val = val; }
            public override string TypeTag => "enum";
            public override void WriteValue(BEWriter w)
            {
                WriteId(w, _type);
                WriteId(w, _val);
            }
        }

        private sealed class DescUnitFloat : DescValue
        {
            private readonly string _unit;
            private readonly double _value;
            public DescUnitFloat(string unit, double value) { _unit = unit; _value = value; }
            public override string TypeTag => "UntF";
            public override void WriteValue(BEWriter w)
            {
                w.WriteBytes(Encoding.ASCII.GetBytes(_unit));
                WriteBEDouble(w, _value);
            }
        }

        // ================================================================
        // 書き込みユーティリティ (変更なし)
        // ================================================================

        private static void WriteId(BEWriter w, string id)
        {
            if (id.Length == 4)
            {
                w.WriteU32(0);
                w.WriteBytes(Encoding.ASCII.GetBytes(id));
            }
            else
            {
                w.WriteU32((uint)id.Length);
                w.WriteBytes(Encoding.ASCII.GetBytes(id));
            }
        }

        private static void WriteUnicodeString(BEWriter w, string s)
        {
            if (s.Length == 0)
            {
                w.WriteU32(1);
                w.WriteByte(0);
                w.WriteByte(0);
            }
            else
            {
                w.WriteU32((uint)s.Length);
                foreach (char c in s)
                {
                    w.WriteByte((byte)(c >> 8));
                    w.WriteByte((byte)(c & 0xFF));
                }
            }
        }

        private static void WriteBEDouble(BEWriter w, double v)
        {
            var buf = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(buf);
            w.WriteBytes(buf);
        }


        public static (string Name, ImmutableList<GradientPoint> Points) Import(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            string sig = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (sig != FileSignature)
                throw new InvalidDataException($"無効なファイルシグネチャ: {sig}");

            ushort version = ReadU16(reader);
            return version >= 5
                ? ImportVersion5(reader)
                : ImportVersion3(reader, version);
        }

        private static (string, ImmutableList<GradientPoint>) ImportVersion3(
            BinaryReader reader, ushort version)
        {
            ushort gradCount = ReadU16(reader);
            if (gradCount == 0)
                throw new InvalidDataException("グラデーションエントリが存在しません。");

            byte nameLen = reader.ReadByte();
            string name = Encoding.ASCII.GetString(reader.ReadBytes(nameLen));

            _ = ReadU16(reader);
            _ = ReadU32(reader);

            ushort colorStopCount = ReadU16(reader);
            var colorStops = new (float Location, float Midpoint, byte R, byte G, byte B)[colorStopCount];
            for (int i = 0; i < colorStopCount; i++)
            {
                float location = ReadU32(reader) / 4096f;
                float midpoint = ReadU32(reader) / 100f;
                ushort model = ReadU16(reader);
                ushort c0 = ReadU16(reader);
                ushort c1 = ReadU16(reader);
                ushort c2 = ReadU16(reader);
                ushort c3 = ReadU16(reader);
                _ = ReadU16(reader);
                var (r, g, b) = ColorToRgb(model, c0, c1, c2, c3);
                colorStops[i] = (location, midpoint, r, g, b);
            }

            ushort transStopCount = ReadU16(reader);
            var transStops = new (float Location, float Midpoint, float Opacity)[transStopCount];
            for (int i = 0; i < transStopCount; i++)
            {
                float location = ReadU32(reader) / 4096f;
                float midpoint = ReadU32(reader) / 100f;
                float opacity = ReadU16(reader) / 65535f;
                transStops[i] = (location, midpoint, opacity);
            }

            _ = reader.ReadBytes(6);
            return (name, StopsToPoints(colorStops, transStops).ToImmutableList());
        }

        private static (string, ImmutableList<GradientPoint>) ImportVersion5(BinaryReader reader)
        {
            reader.BaseStream.Seek(4, SeekOrigin.Current);

            var descriptor = DescriptorReader.ReadDescriptor(reader);
            if (descriptor is null)
                throw new InvalidDataException("Version 5 デスクリプタの読み込みに失敗しました。");

            if (!descriptor.TryGetValue("GrdL", out var grdlObj) ||
                grdlObj is not List<object?> grdList || grdList.Count == 0)
                throw new InvalidDataException("グラデーションリスト (GrdL) が見つかりません。");

            if (grdList[0] is not Dictionary<string, object?> firstGrad)
                throw new InvalidDataException("グラデーションエントリが不正です。");

            Dictionary<string, object?> gradDesc;
            if (firstGrad.TryGetValue("Grad", out var gradObj) &&
                gradObj is Dictionary<string, object?> inner && inner.ContainsKey("Clrs"))
                gradDesc = inner;
            else if (firstGrad.ContainsKey("Clrs"))
                gradDesc = firstGrad;
            else
                throw new InvalidDataException("カラーストップ (Clrs) が見つかりません。");

            string name = gradDesc.TryGetValue("Nm  ", out var nmObj) && nmObj is string nm
                ? nm : "Imported Gradient";

            var colorStops = ExtractColorStops(gradDesc)
                ?? throw new InvalidDataException("カラーストップが空です。");
            var transStops = ExtractTransparencyStops(gradDesc)
                ?? new[] { (0f, 0.5f, 1f), (1f, 0.5f, 1f) };

            return (name, StopsToPoints(colorStops, transStops).ToImmutableList());
        }

        private static List<GradientPoint> StopsToPoints(
            (float Location, float Midpoint, byte R, byte G, byte B)[] colorStops,
            (float Location, float Midpoint, float Opacity)[] transStops)
        {
            var result = new List<GradientPoint>();
            foreach (var cs in colorStops)
            {
                float opacity = SampleOpacity(transStops, cs.Location);
                byte alpha = (byte)Math.Round(opacity * 255f);
                // GradientPoint(double position, Color color) コンストラクタを使用
                // → Position.ActiveValues[0].Value = cs.Location が設定される
                result.Add(new GradientPoint(cs.Location, Color.FromArgb(alpha, cs.R, cs.G, cs.B)));
            }
            return result;
        }

        private static (float Location, float Midpoint, byte R, byte G, byte B)[]? ExtractColorStops(
            Dictionary<string, object?> gradient)
        {
            if (!gradient.TryGetValue("Clrs", out var clrsObj) ||
                clrsObj is not List<object?> clrsList)
                return null;

            var stops = new (float, float, byte, byte, byte)[clrsList.Count];
            for (int i = 0; i < clrsList.Count; i++)
            {
                if (clrsList[i] is not Dictionary<string, object?> stop) return null;
                float location = stop.TryGetValue("Lctn", out var lctn) && lctn is int loc ? loc / 4096f : 0f;
                float midpoint = stop.TryGetValue("Mdpn", out var mdpn) && mdpn is int mid ? mid / 100f : 0.5f;
                var (r, g, b) = ExtractColorFromStop(stop);
                stops[i] = (location, midpoint, r, g, b);
            }
            return stops;
        }

        private static (byte, byte, byte) ExtractColorFromStop(Dictionary<string, object?> stop)
        {
            if (!stop.TryGetValue("Clr ", out var clrObj) ||
                clrObj is not Dictionary<string, object?> clr)
                return (0, 0, 0);
            if (!clr.TryGetValue("_class", out var classObj) || classObj is not string classId)
                return (0, 0, 0);
            return classId switch
            {
                "RGBC" => ExtractRgbc(clr),
                "HSBC" => ExtractHsbc(clr),
                "CMYC" => ExtractCmyc(clr),
                "LbCl" => ExtractLabc(clr),
                "Grsc" => ExtractGrsc(clr),
                _ => (0, 0, 0)
            };
        }

        private static (byte, byte, byte) ExtractRgbc(Dictionary<string, object?> c) =>
            ((byte)Math.Clamp(Math.Round(GetDouble(c, "Rd  ")), 0, 255),
             (byte)Math.Clamp(Math.Round(GetDouble(c, "Grn ")), 0, 255),
             (byte)Math.Clamp(Math.Round(GetDouble(c, "Bl  ")), 0, 255));

        private static (byte, byte, byte) ExtractHsbc(Dictionary<string, object?> c) =>
            HsbToRgbD(GetDouble(c, "H   ") / 360.0, GetDouble(c, "Strt") / 100.0, GetDouble(c, "Brgh") / 100.0);

        private static (byte, byte, byte) ExtractCmyc(Dictionary<string, object?> c)
        {
            double cy = GetDouble(c, "Cyn ") / 100.0, m = GetDouble(c, "Mgnt") / 100.0,
                   y = GetDouble(c, "Ylw ") / 100.0, k = GetDouble(c, "Blck") / 100.0;
            return ((byte)((1 - cy) * (1 - k) * 255), (byte)((1 - m) * (1 - k) * 255), (byte)((1 - y) * (1 - k) * 255));
        }

        private static (byte, byte, byte) ExtractLabc(Dictionary<string, object?> c) =>
            LabToRgbD(GetDouble(c, "Lmnc"), GetDouble(c, "A   "), GetDouble(c, "B   "));

        private static (byte, byte, byte) ExtractGrsc(Dictionary<string, object?> c)
        {
            var v = (byte)Math.Clamp(Math.Round(GetDouble(c, "Gry ") / 100.0 * 255.0), 0, 255);
            return (v, v, v);
        }

        private static (float Location, float Midpoint, float Opacity)[]? ExtractTransparencyStops(
            Dictionary<string, object?> gradient)
        {
            if (!gradient.TryGetValue("Trns", out var trnsObj) ||
                trnsObj is not List<object?> trnsList)
                return null;

            var stops = new (float, float, float)[trnsList.Count];
            for (int i = 0; i < trnsList.Count; i++)
            {
                if (trnsList[i] is not Dictionary<string, object?> stop) continue;
                float location = stop.TryGetValue("Lctn", out var lctn) && lctn is int loc ? loc / 4096f : 0f;
                float midpoint = stop.TryGetValue("Mdpn", out var mdpn) && mdpn is int mid ? mid / 100f : 0.5f;
                float opacity = 1f;
                if (stop.TryGetValue("Opct", out var opctObj))
                {
                    if (opctObj is (string _, double dv)) opacity = (float)(dv / 100.0);
                    else if (opctObj is double dv2) opacity = (float)(dv2 / 100.0);
                }
                stops[i] = (location, midpoint, opacity);
            }
            return stops;
        }


        private static (byte, byte, byte) ColorToRgb(ushort model, ushort c0, ushort c1, ushort c2, ushort c3) =>
            model switch
            {
                0 => ((byte)(c0 >> 8), (byte)(c1 >> 8), (byte)(c2 >> 8)),
                1 => HsbToRgbD(c0 / 65535.0, c1 / 65535.0, c2 / 65535.0),
                2 => CmykToRgbU16(c0, c1, c2, c3),
                3 => LabToRgbU16(c0, c1, c2),
                7 => ((byte)(c0 >> 8), (byte)(c0 >> 8), (byte)(c0 >> 8)),
                _ => (0, 0, 0)
            };

        private static (byte, byte, byte) HsbToRgbD(double h, double s, double b)
        {
            if (s < 1e-6) { var v = (byte)(b * 255); return (v, v, v); }
            double hf = h * 6.0;
            int sec = (int)hf % 6;
            double f = hf - Math.Floor(hf);
            double p = b * (1 - s), q = b * (1 - f * s), tv = b * (1 - (1 - f) * s);
            var (r, g, bv) = sec switch
            {
                0 => (b, tv, p), 1 => (q, b, p), 2 => (p, b, tv),
                3 => (p, q, b), 4 => (tv, p, b), _ => (b, p, q)
            };
            return ((byte)(r * 255), (byte)(g * 255), (byte)(bv * 255));
        }

        private static (byte, byte, byte) CmykToRgbU16(ushort c, ushort m, ushort y, ushort k)
        {
            float cf = c / 65535f, mf = m / 65535f, yf = y / 65535f, kf = k / 65535f;
            return ((byte)((1 - cf) * (1 - kf) * 255), (byte)((1 - mf) * (1 - kf) * 255), (byte)((1 - yf) * (1 - kf) * 255));
        }

        private static (byte, byte, byte) LabToRgbU16(ushort l, ushort a, ushort b) =>
            LabToRgbD(l / 65535.0 * 100, a / 65535.0 * 255 - 128, b / 65535.0 * 255 - 128);

        private static (byte, byte, byte) LabToRgbD(double l, double a, double b)
        {
            double fy = (l + 16) / 116.0, fx = a / 500.0 + fy, fz = fy - b / 200.0;
            static double F(double t) => t > 0.206897 ? t * t * t : (t - 16.0 / 116) / 7.787;
            double x = 0.95047 * F(fx), y = F(fy), z = 1.08883 * F(fz);
            double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
            double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
            double bv = 0.0557 * x - 0.2040 * y + 1.0570 * z;
            static double Gm(double v) => v > 0.0031308 ? 1.055 * Math.Pow(v, 1 / 2.4) - 0.055 : 12.92 * v;
            return ((byte)(Math.Clamp(Gm(r), 0, 1) * 255),
                    (byte)(Math.Clamp(Gm(g), 0, 1) * 255),
                    (byte)(Math.Clamp(Gm(bv), 0, 1) * 255));
        }

        private static float SampleOpacity(
            (float Location, float Midpoint, float Opacity)[] stops, float t)
        {
            if (stops.Length == 0) return 1f;
            if (stops.Length == 1) return stops[0].Opacity;
            if (t <= stops[0].Location) return stops[0].Opacity;
            if (t >= stops[^1].Location) return stops[^1].Opacity;
            for (int i = 0; i < stops.Length - 1; i++)
            {
                var left = stops[i]; var right = stops[i + 1];
                if (t < left.Location || t > right.Location) continue;
                float span = right.Location - left.Location;
                if (span < 1e-6f) return right.Opacity;
                float adj = AdjustMidpoint((t - left.Location) / span, left.Midpoint);
                return left.Opacity + (right.Opacity - left.Opacity) * adj;
            }
            return stops[^1].Opacity;
        }

        private static float AdjustMidpoint(float local, float mid) =>
            local <= mid
                ? (mid > 0f ? 0.5f * local / mid : 0.5f)
                : (1f - mid) > 0f ? 0.5f + 0.5f * (local - mid) / (1f - mid) : 1f;

        private static double GetDouble(Dictionary<string, object?> dict, string key)
        {
            if (dict.TryGetValue(key, out var val))
            {
                if (val is double d) return d;
                if (val is (string _, double dv)) return dv;
            }
            return 0.0;
        }

        private static ushort ReadU16(BinaryReader r)
        {
            var buf = r.ReadBytes(2);
            return (ushort)((buf[0] << 8) | buf[1]);
        }

        private static uint ReadU32(BinaryReader r)
        {
            var buf = r.ReadBytes(4);
            return (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
        }

        private sealed class BEWriter : IDisposable
        {
            private readonly Stream _s;
            public BEWriter(Stream s) => _s = s;
            public void WriteByte(byte v) => _s.WriteByte(v);
            public void WriteBytes(byte[] v) => _s.Write(v);
            public void WriteU16(ushort v)
            {
                _s.WriteByte((byte)(v >> 8));
                _s.WriteByte((byte)(v & 0xFF));
            }
            public void WriteU32(uint v)
            {
                _s.WriteByte((byte)((v >> 24) & 0xFF));
                _s.WriteByte((byte)((v >> 16) & 0xFF));
                _s.WriteByte((byte)((v >> 8) & 0xFF));
                _s.WriteByte((byte)(v & 0xFF));
            }
            public void Dispose() { }
        }
    }
}
