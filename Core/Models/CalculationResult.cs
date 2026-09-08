using System;
using System.Collections.Generic;
using System.Text;
using System.Numerics;

namespace ElectroCalc.Core.Models
{
    public enum CalculationMethod
    {
        MeshCurrents,           // Метод контурных токов (МКТ)
        KirchhoffLaws,          // Законы Кирхгофа
        NodePotentials,         // Метод узловых потенциалов (МУП)
        EquivalentGenerator,    // Метод эквивалентного генератора
        PotentialDiagram,      // Потенциальная диаграмма
        SimplifiedThreeBranch,  // Упрощённый расчёт трёхветвевой схемы
        VectorData,             // Данные для векторных диаграмм
        ThreePhase              // Трёхфазная цепь
    }

    /// <summary>
    /// A single step in the solution — shown in the step-by-step panel.
    /// </summary>
    public class SolutionStep
    {
        public string Title       { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Formula    { get; set; }   // LaTeX-like or plain text
        public string? MatrixText { get; set; }   // Pre-formatted matrix/equation string

        public override string ToString() =>
            string.IsNullOrEmpty(MatrixText)
                ? $"{Title}: {Description}"
                : $"{Title}: {Description}\n{MatrixText}";
    }

    /// <summary>
    /// Branch result: current and power after solve.
    /// </summary>
    public class BranchResult
    {
        public CircuitBranch Branch { get; set; } = null!;

        /// <summary>RMS-фазор тока. Для DC мнимая часть равна нулю.</summary>
        public Complex CurrentPhasor { get; set; } = Complex.Zero;
        /// <summary>RMS-фазор напряжения Start→End. Для DC мнимая часть равна нулю.</summary>
        public Complex VoltagePhasor { get; set; } = Complex.Zero;

        // Совместимость с проверенным DC-кодом. Новые AC-решатели используют Phasor-поля.
        public double Current
        {
            get => CurrentPhasor.Real;
            set => CurrentPhasor = new Complex(value, 0.0);
        }
        public double Voltage
        {
            get => VoltagePhasor.Real;
            set => VoltagePhasor = new Complex(value, 0.0);
        }

        /// <summary>Комплексная мощность, поглощаемая пассивной частью ветви (R/L/C и внутренние сопротивления).</summary>
        public Complex PassiveComplexPower { get; set; } = Complex.Zero;
        /// <summary>Знаковая комплексная мощность источников ветви: плюс = генерация, минус = поглощение.</summary>
        public Complex SourceComplexPowerGenerated { get; set; } = Complex.Zero;
        /// <summary>Комплексная мощность всей ветви по пассивному соглашению: S = U·conj(I).</summary>
        public Complex TerminalComplexPower { get; set; } = Complex.Zero;

        // Совместимость с прежним DC-кодом.
        public double PowerConsumed
        {
            get => PassiveComplexPower.Real;
            set => PassiveComplexPower = new Complex(value, PassiveComplexPower.Imaginary);
        }
        public double PowerGenerated
        {
            get => SourceComplexPowerGenerated.Real;
            set => SourceComplexPowerGenerated = new Complex(value, SourceComplexPowerGenerated.Imaginary);
        }

        public Complex CalculatedTerminalComplexPower => VoltagePhasor * Complex.Conjugate(CurrentPhasor);
        public double ActivePower => CalculatedTerminalComplexPower.Real;
        public double ReactivePower => CalculatedTerminalComplexPower.Imaginary;
        public double ApparentPower => CalculatedTerminalComplexPower.Magnitude;
    }

    /// <summary>
    /// Full result of one calculation run.
    /// </summary>
    public class CalculationResult
    {
        public CalculationMethod Method  { get; set; }
        public CircuitAnalysisSettings Analysis { get; set; } = new();
        public bool PowerBalanceAvailable { get; set; } = true;
        public bool Success              { get; set; }
        public string? ErrorMessage      { get; set; }

        public List<SolutionStep>  Steps          { get; } = new();
        public List<BranchResult>  BranchResults  { get; } = new();
        public List<PhasorDiagramData> VectorDiagrams { get; } = new();

        /// <summary>
        /// Для аварийного 3Φ-расчёта здесь хранится исходный нормальный режим,
        /// чтобы интерфейс и DOCX показывали оба состояния одним результатом.
        /// </summary>
        public ThreePhaseFaultSettings? ThreePhaseFault { get; set; }
        public List<BranchResult> ReferenceBranchResults { get; } = new();
        public Complex ReferenceComplexPowerGenerated { get; set; } = Complex.Zero;
        public Complex ReferenceComplexPowerConsumed { get; set; } = Complex.Zero;

        public bool HasReferenceScenario => ReferenceBranchResults.Count > 0;

        // Power balance
        public Complex TotalComplexPowerGenerated { get; set; } = Complex.Zero;
        public Complex TotalComplexPowerConsumed  { get; set; } = Complex.Zero;

        public double TotalPowerGenerated
        {
            get => TotalComplexPowerGenerated.Real;
            set => TotalComplexPowerGenerated = new Complex(value, TotalComplexPowerGenerated.Imaginary);
        }
        public double TotalPowerConsumed
        {
            get => TotalComplexPowerConsumed.Real;
            set => TotalComplexPowerConsumed = new Complex(value, TotalComplexPowerConsumed.Imaginary);
        }
        public double TotalReactivePowerGenerated => TotalComplexPowerGenerated.Imaginary;
        public double TotalReactivePowerConsumed  => TotalComplexPowerConsumed.Imaginary;
        public double TotalApparentPowerGenerated => TotalComplexPowerGenerated.Magnitude;
        public double TotalApparentPowerConsumed  => TotalComplexPowerConsumed.Magnitude;
        public Complex ComplexPowerImbalance => TotalComplexPowerGenerated - TotalComplexPowerConsumed;
        public double PowerImbalance => ComplexPowerImbalance.Magnitude;
        public double PowerBalanceTolerance =>
            1e-6 * Math.Max(1.0, Math.Max(TotalComplexPowerGenerated.Magnitude, TotalComplexPowerConsumed.Magnitude));
        public bool PowerBalanceOk => PowerImbalance <= PowerBalanceTolerance;

        /// <summary>
        /// Formats the full solution as plain text (for export/copy).
        /// </summary>
        public string ToPlainText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Метод: {MethodName(Method)} ===");
            sb.AppendLine();

            foreach (var step in Steps)
            {
                sb.AppendLine($"── {step.Title} ──");
                sb.AppendLine(step.Description);
                if (step.Formula    != null) sb.AppendLine(step.Formula);
                if (step.MatrixText != null) sb.AppendLine(step.MatrixText);
                sb.AppendLine();
            }

            if (Method != CalculationMethod.KirchhoffLaws && Method != CalculationMethod.PotentialDiagram)
            {
                sb.AppendLine("=== Результаты по ветвям ===");
                foreach (var r in BranchResults)
                {
                    sb.AppendLine($"  {r.Branch}: I = {Phasor.Compact(r.CurrentPhasor, Analysis.Mode, "А")}, U = {Phasor.Compact(r.VoltagePhasor, Analysis.Mode, "В")}, " +
                                  $"P = {r.ActivePower:F4} Вт, Q = {r.ReactivePower:F4} вар, |S| = {r.ApparentPower:F4} ВА");
                }

                if (PowerBalanceAvailable)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== Баланс мощностей ===");
                    sb.AppendLine($"  ΣP_ист = {TotalComplexPowerGenerated.Real:F4} Вт; ΣQ_ист = {TotalComplexPowerGenerated.Imaginary:F4} вар; |ΣS_ист| = {TotalComplexPowerGenerated.Magnitude:F4} ВА");
                    sb.AppendLine($"  ΣP_потр = {TotalComplexPowerConsumed.Real:F4} Вт; ΣQ_потр = {TotalComplexPowerConsumed.Imaginary:F4} вар; |ΣS_потр| = {TotalComplexPowerConsumed.Magnitude:F4} ВА");
                    sb.AppendLine($"  |ΔS| = {PowerImbalance:E2} ВА  " +
                                  (PowerBalanceOk ? "✓ Баланс сошёлся" : "✗ Проверьте схему"));
                }
            }

            return sb.ToString();
        }

        private static string MethodName(CalculationMethod m) => m switch
        {
            CalculationMethod.MeshCurrents        => "Метод контурных токов (МКТ)",
            CalculationMethod.KirchhoffLaws       => "Законы Кирхгофа",
            CalculationMethod.NodePotentials       => "Метод узловых потенциалов (МУП)",
            CalculationMethod.EquivalentGenerator  => "Метод эквивалентного генератора",
            CalculationMethod.PotentialDiagram     => "Потенциальная диаграмма",
            CalculationMethod.SimplifiedThreeBranch => "Упрощённый расчёт трёхветвевой схемы",
            CalculationMethod.VectorData            => "Данные для векторных диаграмм",
            CalculationMethod.ThreePhase            => "Расчёт трёхфазной цепи",
            _ => m.ToString()
        };
    }
}
