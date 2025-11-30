using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace ColorRamp
{
    public class GradientPoint : Animatable
    {
        public double Position { get => position; set => Set(ref position, value); }
        private double position;

        public Color Color { get => color; set => Set(ref color, value); }
        private Color color = Colors.White;

        public ICommand? BeginEditCommand { get; set; }
        public ICommand? EndEditCommand { get; set; }

        public GradientPoint(double position, Color color)
        {
            Position = position;
            Color = color;
        }

        public GradientPoint() { }

        // コピーコンストラクタ（サンプル模倣）
        public GradientPoint(GradientPoint other)
        {
            Position = other.Position;
            Color = other.Color;
            BeginEditCommand = other.BeginEditCommand;
            EndEditCommand = other.EndEditCommand;
        }

        protected override IEnumerable<IAnimatable> GetAnimatables() => Array.Empty<IAnimatable>();
    }
}