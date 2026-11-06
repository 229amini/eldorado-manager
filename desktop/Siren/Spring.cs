using System;

namespace Siren
{
    /// <summary>
    /// A damped spring pulling one value toward Target. Fixed small sub-steps keep it independent of
    /// the frame rate. Adapted from zab1yak/dynamic-island-windows (MIT), see THIRD-PARTY-NOTICES.md.
    /// </summary>
    public sealed class Spring
    {
        private const double MaxSubStep = 1.0 / 240.0;
        private readonly double restDelta;

        public Spring(double value, double stiffness, double damping, double restDelta = 0.25)
        {
            Value = Target = value;
            Stiffness = stiffness;
            Damping = damping;
            this.restDelta = restDelta;
        }

        public double Value { get; private set; }
        public double Velocity { get; private set; }
        public double Target { get; set; }
        public double Stiffness { get; set; }
        public double Damping { get; set; }
        public bool IsAtRest => Value == Target && Velocity == 0;

        public void SnapTo(double value)
        {
            Value = Target = value;
            Velocity = 0;
        }

        public void Step(double seconds)
        {
            if (IsAtRest || seconds <= 0) return;
            var steps = (int)Math.Ceiling(seconds / MaxSubStep);
            var h = seconds / steps;
            for (var i = 0; i < steps; i++)
            {
                Velocity += (-Stiffness * (Value - Target) - Damping * Velocity) * h;
                Value += Velocity * h;
            }
            if (Math.Abs(Value - Target) < restDelta && Math.Abs(Velocity) < restDelta * 4)
            {
                Value = Target;
                Velocity = 0;
            }
        }

        /// <summary>Settles in about 0.4 s with a light overshoot. For size changes.</summary>
        public static Spring Bouncy(double value) => new Spring(value, 300, 24);

        /// <summary>Critically damped, no overshoot. For fades and color.</summary>
        public static Spring Smooth(double value) => new Spring(value, 400, 40, 0.004);
    }
}
