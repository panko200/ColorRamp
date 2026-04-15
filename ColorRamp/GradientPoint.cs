using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace ColorRamp
{
    public enum SegmentInterpolationMode
    {
        Linear, Ease, Cardinal, BSpline, Constant
    }

    public class GradientPoint : Animatable
    {
        // ★新しいアニメーションプロパティ
        public Animation PositionAnim { get; } = new Animation(50.0, 0.0, 100.0);

        // ★過去の .ymmp (double) を読み込むための救済用裏口
        [Browsable(false)]
        public double Position
        {
            get => PositionValue;
            set
            {
                var av = PositionAnim.ActiveValues.FirstOrDefault();
                if (av != null) av.Value = Math.Clamp(value, 0.0, 1.0) * 100.0;
                Set(ref _positionValueShadow, value, nameof(PositionValue));
            }
        }

        private double _positionValueShadow;
        public double PositionValue
        {
            get => Math.Clamp(
                (PositionAnim.ActiveValues.FirstOrDefault()?.Value ?? PositionAnim.DefaultValue) / 100.0,
                0.0, 1.0);
            set
            {
                var clamped = Math.Clamp(value, 0.0, 1.0);
                var av = PositionAnim.ActiveValues.FirstOrDefault();
                if (av == null) return;

                double newRaw = clamped * 100.0;
                if (Math.Abs(av.Value - newRaw) < 1e-8) return;
                av.Value = newRaw;
                Set(ref _positionValueShadow, clamped, nameof(PositionValue));
            }
        }

        public bool EnablePositionAnimation
        {
            get => _enablePositionAnimation;
            set
            {
                if (!Set(ref _enablePositionAnimation, value, nameof(EnablePositionAnimation))) return;
                if (!value) PositionAnim.AnimationType = AnimationType.なし;
            }
        }
        private bool _enablePositionAnimation = false;

        public SegmentInterpolationMode SegmentInterpolation
        {
            get => _segmentInterpolation;
            set => Set(ref _segmentInterpolation, value, nameof(SegmentInterpolation));
        }
        private SegmentInterpolationMode _segmentInterpolation = SegmentInterpolationMode.Linear;

        private bool _isLastPoint;
        public bool IsLastPoint
        {
            get => _isLastPoint;
            set
            {
                if (_isLastPoint == value) return;
                _isLastPoint = value;
                OnPropertyChanged(nameof(IsLastPoint));
            }
        }

        public Color Color { get => color; set => Set(ref color, value); }
        private Color color = Colors.White;

        public ICommand? BeginEditCommand { get; set; }
        public ICommand? EndEditCommand { get; set; }

        public GradientPoint(double position, Color color)
        {
            var av = PositionAnim.ActiveValues.FirstOrDefault();
            if (av != null) av.Value = Math.Clamp(position, 0.0, 1.0) * 100.0;
            Color = color;
            InitPositionNotification();
        }

        public GradientPoint()
        {
            InitPositionNotification();
        }

        public GradientPoint(GradientPoint other)
        {
            PositionAnim.CopyFrom(other.PositionAnim);
            Color = other.Color;
            BeginEditCommand = other.BeginEditCommand;
            EndEditCommand = other.EndEditCommand;
            _enablePositionAnimation = other._enablePositionAnimation;
            _segmentInterpolation = other._segmentInterpolation;
            InitPositionNotification();
        }

        private readonly List<AnimationValue> _subscribedAvList = new();

        private void InitPositionNotification()
        {
            PositionAnim.PropertyChanged += Position_PropertyChanged;
            ResubscribeActiveValues();
        }

        private void Position_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "ActiveValues" || e.PropertyName == nameof(Animation.Values))
            {
                ResubscribeActiveValues();
                Set(ref _positionValueShadow, PositionValue, nameof(PositionValue));
            }
        }

        private void ResubscribeActiveValues()
        {
            foreach (var av in _subscribedAvList) av.PropertyChanged -= ActiveValue_PropertyChanged;
            _subscribedAvList.Clear();

            foreach (var av in PositionAnim.ActiveValues)
            {
                _subscribedAvList.Add(av);
                av.PropertyChanged += ActiveValue_PropertyChanged;
            }
        }

        private void ActiveValue_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AnimationValue.Value))
                Set(ref _positionValueShadow, PositionValue, nameof(PositionValue));
        }

        protected override IEnumerable<IAnimatable> GetAnimatables()
        {
            yield return PositionAnim;
        }
    }
}