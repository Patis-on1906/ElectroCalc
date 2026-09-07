using System;
using System.Collections.Generic;

namespace ElectroCalc.Core.Models
{
    /// <summary>Три поддерживаемых канонических режима трёхфазной нагрузки.</summary>
    public enum ThreePhaseCircuitMode
    {
        StarWithNeutral,
        StarWithoutNeutral,
        Delta
    }

    /// <summary>Последовательное комплексное сопротивление одной ветви R+jX.</summary>
    public sealed class ThreePhaseBranchInput
    {
        public double ResistanceOhms { get; set; } = 10.0;
        public double ReactanceOhms { get; set; }

        public ThreePhaseBranchInput Clone() => new()
        {
            ResistanceOhms = ResistanceOhms,
            ReactanceOhms = ReactanceOhms
        };
    }

    /// <summary>
    /// Входные данные конструктора 3Φ-схемы. Источник считается симметричным:
    /// одно фазное RMS-значение используется для E_A/E_B/E_C, а фазы задаются
    /// автоматически как 0°, −120° и +120°.
    /// </summary>
    public sealed class ThreePhaseCircuitInput
    {
        public ThreePhaseCircuitMode Mode { get; set; } = ThreePhaseCircuitMode.StarWithNeutral;
        public double FrequencyHz { get; set; } = 50.0;
        public double PhaseEmfRms { get; set; } = 220.0;
        public ThreePhaseBranchInput BranchA { get; set; } = new();
        public ThreePhaseBranchInput BranchB { get; set; } = new();
        public ThreePhaseBranchInput BranchC { get; set; } = new();

        public ThreePhaseCircuitInput Clone() => new()
        {
            Mode = Mode,
            FrequencyHz = FrequencyHz,
            PhaseEmfRms = PhaseEmfRms,
            BranchA = BranchA.Clone(),
            BranchB = BranchB.Clone(),
            BranchC = BranchC.Clone()
        };
    }

    /// <summary>Проверенный набор настроек и элементов для решателя и холста.</summary>
    public sealed class ThreePhaseCircuitDefinition
    {
        public ThreePhaseCircuitInput Input { get; init; } = new();
        public CircuitAnalysisSettings Settings { get; init; } = new();
        public IReadOnlyList<CircuitElement> Elements { get; init; } = Array.Empty<CircuitElement>();
    }

    /// <summary>
    /// Преобразует компактные R+jX-данные пользователя в обычные элементы
    /// ElectroCalc. X&gt;0 создаёт L=X/ω, X&lt;0 создаёт C=−1/(ωX).
    /// </summary>
    public static class ThreePhaseCircuitFactory
    {
        private const double MinimumImpedance = 1e-12;

        public static ThreePhaseCircuitDefinition Create(ThreePhaseCircuitInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            RequireFinite(input.FrequencyHz, "Частота");
            if (input.FrequencyHz <= 0)
                throw new InvalidOperationException("Частота трёхфазной цепи должна быть больше 0 Гц.");

            RequireFinite(input.PhaseEmfRms, "Фазная ЭДС");
            if (input.PhaseEmfRms < 0)
                throw new InvalidOperationException("Действующее значение фазной ЭДС не может быть отрицательным.");

            var branches = new[]
            {
                (Phase: ThreePhasePhase.A, Value: input.BranchA),
                (Phase: ThreePhasePhase.B, Value: input.BranchB),
                (Phase: ThreePhasePhase.C, Value: input.BranchC)
            };
            foreach (var branch in branches)
                ValidateBranch(branch.Phase, branch.Value);

            var settings = new CircuitAnalysisSettings
            {
                Mode = CircuitAnalysisMode.ThreePhase,
                FrequencyHz = input.FrequencyHz,
                ThreePhaseConnection = input.Mode == ThreePhaseCircuitMode.Delta
                    ? ThreePhaseLoadConnection.Delta
                    : ThreePhaseLoadConnection.Star,
                ThreePhaseHasNeutral = input.Mode == ThreePhaseCircuitMode.StarWithNeutral
            };
            settings.Validate();

            var elements = new List<CircuitElement>();
            AddSource(elements, ThreePhasePhase.A, input.PhaseEmfRms, 0.0);
            AddSource(elements, ThreePhasePhase.B, input.PhaseEmfRms, -120.0);
            AddSource(elements, ThreePhasePhase.C, input.PhaseEmfRms, 120.0);

            foreach (var branch in branches)
                AddBranchElements(elements, settings, branch.Phase, branch.Value);

            return new ThreePhaseCircuitDefinition
            {
                Input = input.Clone(),
                Settings = settings,
                Elements = elements
            };
        }

        private static void AddSource(List<CircuitElement> elements, ThreePhasePhase phase,
            double emfRms, double phaseDegrees)
        {
            elements.Add(new CircuitElement
            {
                Type = ElementType.VoltageSource,
                Name = $"E_{phase}",
                Value = emfRms,
                PhaseDegrees = phaseDegrees,
                IsPositiveAtStart = true,
                PhaseAssignment = phase
            });
        }

        private static void AddBranchElements(List<CircuitElement> elements,
            CircuitAnalysisSettings settings, ThreePhasePhase phase, ThreePhaseBranchInput input)
        {
            string suffix = settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
                ? $"{phase}N"
                : phase switch
                {
                    ThreePhasePhase.A => "AB",
                    ThreePhasePhase.B => "BC",
                    _ => "CA"
                };

            if (input.ResistanceOhms > 0)
            {
                elements.Add(new CircuitElement
                {
                    Type = ElementType.Resistor,
                    Name = $"R_{suffix}",
                    Value = input.ResistanceOhms,
                    PhaseAssignment = phase
                });
            }

            if (input.ReactanceOhms > 0)
            {
                elements.Add(new CircuitElement
                {
                    Type = ElementType.Inductor,
                    Name = $"L_{suffix}",
                    Value = input.ReactanceOhms / settings.AngularFrequency,
                    PhaseAssignment = phase
                });
            }
            else if (input.ReactanceOhms < 0)
            {
                elements.Add(new CircuitElement
                {
                    Type = ElementType.Capacitor,
                    Name = $"C_{suffix}",
                    Value = -1.0 / (settings.AngularFrequency * input.ReactanceOhms),
                    PhaseAssignment = phase
                });
            }
        }

        private static void ValidateBranch(ThreePhasePhase phase, ThreePhaseBranchInput input)
        {
            if (input == null)
                throw new InvalidOperationException($"Не заданы параметры ветви {phase}.");

            RequireFinite(input.ResistanceOhms, $"R ветви {phase}");
            RequireFinite(input.ReactanceOhms, $"X ветви {phase}");
            if (input.ResistanceOhms < 0)
                throw new InvalidOperationException($"Активное сопротивление ветви {phase} не может быть отрицательным.");

            double magnitude = Math.Sqrt(input.ResistanceOhms * input.ResistanceOhms +
                                         input.ReactanceOhms * input.ReactanceOhms);
            if (magnitude <= MinimumImpedance)
                throw new InvalidOperationException(
                    $"Полное сопротивление ветви {phase} должно быть отлично от нуля.");
        }

        private static void RequireFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException($"{name} должно быть конечным числом.");
        }
    }
}
