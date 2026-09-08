using System;

namespace ElectroCalc.Core.Models
{
    public enum ThreePhaseOperatingMode
    {
        Normal,
        OpenCircuit,
        ShortCircuit
    }

    public enum ThreePhaseFaultLocation
    {
        LineA,
        LineB,
        LineC,
        BranchAB,
        BranchBC,
        BranchCA
    }

    /// <summary>Режим, который выбирается непосредственно перед расчётом 3Φ.</summary>
    public sealed class ThreePhaseFaultSettings
    {
        public ThreePhaseOperatingMode OperatingMode { get; set; } = ThreePhaseOperatingMode.Normal;
        public ThreePhaseFaultLocation Location { get; set; } = ThreePhaseFaultLocation.LineA;

        /// <summary>
        /// Сопротивление дуги/места КЗ. Нулевое значение с идеальным источником
        /// привело бы к бесконечному току, поэтому задаётся конечное Rк.
        /// </summary>
        public double ShortCircuitResistanceOhms { get; set; } = 0.01;

        public bool IsEmergency => OperatingMode != ThreePhaseOperatingMode.Normal;

        public ThreePhaseFaultSettings Clone() => new()
        {
            OperatingMode = OperatingMode,
            Location = Location,
            ShortCircuitResistanceOhms = ShortCircuitResistanceOhms
        };

        public void Validate(CircuitAnalysisSettings analysis)
        {
            if (!IsEmergency) return;

            bool star = analysis.ThreePhaseConnection == ThreePhaseLoadConnection.Star;
            if (star && Location is (ThreePhaseFaultLocation.BranchAB or
                ThreePhaseFaultLocation.BranchBC or ThreePhaseFaultLocation.BranchCA))
                throw new InvalidOperationException("Для звезды выберите фазную ветвь A, B или C.");

            if (OperatingMode == ThreePhaseOperatingMode.ShortCircuit &&
                (!double.IsFinite(ShortCircuitResistanceOhms) || ShortCircuitResistanceOhms <= 0))
                throw new InvalidOperationException("Сопротивление места КЗ должно быть конечным числом больше 0 Ом.");
        }

        public string Description(CircuitAnalysisSettings analysis)
        {
            if (!IsEmergency) return "Нормальный режим";
            string kind = OperatingMode == ThreePhaseOperatingMode.OpenCircuit
                ? "холостой ход (обрыв)"
                : "короткое замыкание";
            string target = Location switch
            {
                ThreePhaseFaultLocation.LineA => analysis.ThreePhaseConnection == ThreePhaseLoadConnection.Star ? "ветви AN" : "линии A",
                ThreePhaseFaultLocation.LineB => analysis.ThreePhaseConnection == ThreePhaseLoadConnection.Star ? "ветви BN" : "линии B",
                ThreePhaseFaultLocation.LineC => analysis.ThreePhaseConnection == ThreePhaseLoadConnection.Star ? "ветви CN" : "линии C",
                ThreePhaseFaultLocation.BranchAB => "ветви AB",
                ThreePhaseFaultLocation.BranchBC => "ветви BC",
                _ => "ветви CA"
            };
            return $"{kind} {target}";
        }
    }
}
