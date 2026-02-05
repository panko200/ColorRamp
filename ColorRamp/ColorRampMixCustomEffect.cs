using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace ColorRamp
{
    internal class ColorRampMixCustomEffect : D2D1CustomShaderEffectBase
    {
        // プロパティ群
        public int MixMode { set => SetValue((int)EffectImpl.Properties.MixMode, value); }
        public bool KeepCh1 { set => SetValue((int)EffectImpl.Properties.KeepCh1, value); }
        public bool KeepCh2 { set => SetValue((int)EffectImpl.Properties.KeepCh2, value); }
        public bool KeepCh3 { set => SetValue((int)EffectImpl.Properties.KeepCh3, value); }
        public bool KeepAlpha { set => SetValue((int)EffectImpl.Properties.KeepAlpha, value); }

        public ColorRampMixCustomEffect(IGraphicsDevicesAndContext devices) : base(Create<EffectImpl>(devices)) { }

        // シェーダーに渡すデータ構造 (16バイトアライメント推奨)
        [StructLayout(LayoutKind.Sequential)]
        struct ConstantBuffer
        {
            public int MixMode;     // 0:RGB, 1:HSL, 2:HSV
            public int KeepCh1;     // boolはint(0/1)として送るのが安全
            public int KeepCh2;
            public int KeepCh3;
            public int KeepAlpha;
            public float Padding1;  // パディング
            public float Padding2;
            public float Padding3;
        }

        // 入力数: 2 (Input0: グラデーション結果, Input1: 元画像)
        [CustomEffect(2)]
        private class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
        {
            private ConstantBuffer constants;

            protected override void UpdateConstants()
            {
                if (drawInformation is not null)
                {
                    drawInformation.SetPixelShaderConstantBuffer(constants);
                }
            }

            private static byte[] LoadShader()
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                // ★注意: リソース名は実際のプロジェクト構造に合わせて調整してください
                using var stream = assembly.GetManifestResourceStream("ColorRamp.Shaders.ColorRampMixShader.cso");
                if (stream is null) throw new FileNotFoundException("Shader resource 'ColorRampMixShader.cso' not found.");
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }

            public EffectImpl() : base(LoadShader()) => constants = new ConstantBuffer();

            public enum Properties
            {
                MixMode, KeepCh1, KeepCh2, KeepCh3, KeepAlpha
            }

            [CustomEffectProperty(PropertyType.Int32, (int)Properties.MixMode)]
            public int MixMode { get => constants.MixMode; set { constants.MixMode = value; UpdateConstants(); } }

            [CustomEffectProperty(PropertyType.Bool, (int)Properties.KeepCh1)]
            public bool KeepCh1 { get => constants.KeepCh1 != 0; set { constants.KeepCh1 = value ? 1 : 0; UpdateConstants(); } }

            [CustomEffectProperty(PropertyType.Bool, (int)Properties.KeepCh2)]
            public bool KeepCh2 { get => constants.KeepCh2 != 0; set { constants.KeepCh2 = value ? 1 : 0; UpdateConstants(); } }

            [CustomEffectProperty(PropertyType.Bool, (int)Properties.KeepCh3)]
            public bool KeepCh3 { get => constants.KeepCh3 != 0; set { constants.KeepCh3 = value ? 1 : 0; UpdateConstants(); } }

            [CustomEffectProperty(PropertyType.Bool, (int)Properties.KeepAlpha)]
            public bool KeepAlpha { get => constants.KeepAlpha != 0; set { constants.KeepAlpha = value ? 1 : 0; UpdateConstants(); } }
        }
    }
}