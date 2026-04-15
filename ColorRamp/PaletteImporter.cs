using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColorRamp
{
    internal static class PaletteImporter
    {
        // ================================================================
        // .hex 読み込み（Lospec / GIMP palette 形式）
        //
        // 各行が 6 桁 16 進数の RGB カラー。例:
        //   ff0000
        //   00ff00
        //   0000ff
        // ================================================================

        public static ImmutableList<GradientPoint> ImportHex(string path)
        {
            var colors = new List<Color>();

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                    continue;

                // # プレフィックスがある場合も許容
                if (line.StartsWith('#')) line = line[1..];
                if (line.Length != 6) continue;

                try
                {
                    byte r = Convert.ToByte(line[0..2], 16);
                    byte g = Convert.ToByte(line[2..4], 16);
                    byte b = Convert.ToByte(line[4..6], 16);
                    colors.Add(Color.FromRgb(r, g, b));
                }
                catch { /* 不正行はスキップ */ }
            }

            if (colors.Count == 0)
                throw new InvalidDataException("有効なカラーが見つかりませんでした。");

            return ColorsToPoints(colors);
        }

        // ================================================================
        // .png 読み込み
        //
        // 画像の 1 行目（y=0）を左から右にサンプリングして GradientPoint を作る。
        // ただし隣接する同一色はまとめてスキップし、重複ポイントを減らす。
        // 最大サンプル数は 64 点（それ以上は間引く）。
        // ================================================================

        public static ImmutableList<GradientPoint> ImportPng(string path)
        {
            BitmapSource bmp;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var decoder = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                bmp = decoder.Frames[0];
            }

            int width  = bmp.PixelWidth;
            int height = bmp.PixelHeight;
            if (width == 0 || height == 0)
                throw new InvalidDataException("画像が空です。");

            // ピクセルフォーマットを Pbgra32 に変換して読みやすくする
            var converted = new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);

            // 1 行分のピクセルを読む
            int stride = width * 4; // Pbgra32 = 4 bytes/pixel
            byte[] pixels = new byte[stride];
            converted.CopyPixels(new System.Windows.Int32Rect(0, 0, width, 1), pixels, stride, 0);

            // サンプリング列インデックスを決定（最大 64 点）
            int maxSamples = Math.Min(width, 64);
            var sampleX = Enumerable.Range(0, maxSamples)
                .Select(i => (int)Math.Round((double)i / (maxSamples - 1) * (width - 1)))
                .Distinct()
                .ToList();

            // 各 X 位置のカラーを取得
            var colors = sampleX
                .Select(x =>
                {
                    int idx = x * 4;
                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    byte a = pixels[idx + 3];
                    return Color.FromArgb(a, r, g, b);
                })
                .ToList();

            // 隣接同一色を除去（パレット画像は1色ブロックの連続なことが多い）
            var distinct = new List<Color> { colors[0] };
            for (int i = 1; i < colors.Count; i++)
                if (!ColorsEqual(colors[i], distinct[^1]))
                    distinct.Add(colors[i]);

            if (distinct.Count == 0)
                throw new InvalidDataException("有効なカラーが見つかりませんでした。");

            return ColorsToPoints(distinct);
        }

        // ================================================================
        // カラーリストを均等配置の GradientPoint リストに変換
        // ================================================================

        private static ImmutableList<GradientPoint> ColorsToPoints(List<Color> colors)
        {
            var builder = ImmutableList.CreateBuilder<GradientPoint>();
            int n = colors.Count;

            for (int i = 0; i < n; i++)
            {
                double pos = n == 1 ? 0.5 : (double)i / (n - 1);
                builder.Add(new GradientPoint(pos, colors[i]));
            }

            return builder.ToImmutable();
        }

        private static bool ColorsEqual(Color a, Color b)
            => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
    }
}
