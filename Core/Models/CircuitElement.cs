using System;
using System.Numerics;

namespace ElectroCalc.Core.Models
{
    public enum ElementType
    {
        Resistor,       // R
        VoltageSource,  // E (ЭДС)
        CurrentSource,  // J (источник тока)
        Capacitor,      // C
        Inductor        // L
    }

    /// <summary>
    /// Represents a single circuit element (branch element).
    /// </summary>
    public class CircuitElement
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public ElementType Type { get; set; }

        /// <summary>
        /// Resistance in Ohms (for R), EMF in Volts (for E),
        /// Current in Amps (for J), Capacitance in Farads (for C),
        /// Inductance in Henrys (for L).
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Internal resistance of a voltage/current source (Ohms).
        /// </summary>
        public double InternalResistance { get; set; } = 0.0;

        /// <summary>
        /// Ориентация относительно собственных портов элемента. Для E:
        /// true = положительный вывод A, false = положительный вывод B.
        /// Для J: true = ток A→B, false = ток B→A.
        /// Имя свойства оставлено для совместимости с файлами схем прежних версий.
        /// </summary>
        public bool IsPositiveAtStart { get; set; } = true;

        /// <summary>
        /// Начальная фаза источника в градусах. В AC Value трактуется как
        /// действующее значение (RMS), в DC фаза игнорируется.
        /// </summary>
        public double PhaseDegrees { get; set; } = 0.0;

        /// <summary>Назначение элемента фазе/ветви в режиме 3Φ. Для звезды A/B/C = AN/BN/CN; для треугольника = AB/BC/CA.</summary>
        public ThreePhasePhase PhaseAssignment { get; set; } = ThreePhasePhase.None;

        /// <summary>Комплексное действующее значение источника для выбранного режима.</summary>
        public Complex SourcePhasor(CircuitAnalysisSettings settings)
        {
            if (Type is not (ElementType.VoltageSource or ElementType.CurrentSource))
                return Complex.Zero;
            return settings.IsPhasorMode
                ? Phasor.FromMagnitudePhase(Value, PhaseDegrees)
                : new Complex(Value, 0.0);
        }

        /// <summary>Последний рассчитанный ток элемента (RMS-фазор для AC).</summary>
        public Complex LastCurrentPhasor { get; set; } = Complex.Zero;

        /// <summary>Совместимость со старым DC-кодом.</summary>
        public double LastCurrent
        {
            get => LastCurrentPhasor.Real;
            set => LastCurrentPhasor = new Complex(value, 0.0);
        }

        public override string ToString() =>
            $"{Name} ({Type}, {Value})";
    }
}
