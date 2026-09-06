using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Универсальный расчёт комплексных мощностей для установившегося режима.
    /// Для DC комплексные величины вырождаются в вещественные (Q=0).
    /// Пассивная часть ветви: Sпасс = |I|² Zпасс.
    /// Терминальная мощность ветви по пассивному соглашению: Sветв = U * conj(I).
    /// Знаковая генерация всех источников ветви: Sист = Sпасс - Sветв.
    /// Такой расчёт корректен и для источников тока, напряжение которых определяется схемой.
    /// </summary>
    internal static class ComplexPowerCalculator
    {
        public static void AddPowerBalance(
            CalculationResult result,
            CircuitAnalysisSettings settings,
            string generalTitle,
            string numericTitle)
        {
            settings.Validate();

            var models = result.BranchResults.ToDictionary(
                r => r.Branch,
                r => SteadyStateBranchModel.Create(r.Branch, settings));

            foreach (var row in result.BranchResults)
            {
                var model = models[row.Branch];
                Complex terminal = row.VoltagePhasor * Complex.Conjugate(row.CurrentPhasor);
                Complex passive = row.CurrentPhasor.Magnitude * row.CurrentPhasor.Magnitude * model.Impedance;
                Complex generated = passive - terminal;

                row.TerminalComplexPower = Clean(terminal);
                row.PassiveComplexPower = Clean(passive);
                row.SourceComplexPowerGenerated = Clean(generated);
            }

            result.TotalComplexPowerConsumed = Clean(result.BranchResults.Aggregate(
                Complex.Zero, (sum, r) => sum + r.PassiveComplexPower));
            result.TotalComplexPowerGenerated = Clean(result.BranchResults.Aggregate(
                Complex.Zero, (sum, r) => sum + r.SourceComplexPowerGenerated));
            result.PowerBalanceAvailable = true;

            result.Steps.Add(new SolutionStep
            {
                Title = generalTitle,
                Description = settings.Mode == CircuitAnalysisMode.AC
                    ? "Используем комплексную мощность S̲=Ũ·Ī*. Активная мощность P=Re(S̲), реактивная Q=Im(S̲), полная |S̲|. Для пассивной части ветви S̲пасс=|Ī|²Z̲."
                    : "Для DC комплексная формула S̲=Ũ·Ī* вырождается в обычный баланс активной мощности; Q=0.",
                MatrixText = BuildGeneral(result.BranchResults, settings)
            });

            result.Steps.Add(new SolutionStep
            {
                Title = numericTitle,
                Description = "Подставляем рассчитанные RMS-фазоры токов/напряжений и проверяем комплексный баланс источников и пассивных элементов:",
                MatrixText = BuildNumeric(result, settings)
            });
        }

        private static string BuildGeneral(IReadOnlyList<BranchResult> rows, CircuitAnalysisSettings settings)
        {
            var pTerms = new List<string>();
            var qTerms = new List<string>();

            for (int i = 0; i < rows.Count; i++)
            {
                string current = settings.Mode == CircuitAnalysisMode.AC ? $"|I̲{i + 1}|²" : $"I{i + 1}²";
                foreach (var e in rows[i].Branch.Elements)
                {
                    switch (e.Type)
                    {
                        case ElementType.Resistor:
                            pTerms.Add($"{current}·{e.Name}");
                            break;
                        case ElementType.VoltageSource:
                        case ElementType.CurrentSource:
                            if (e.InternalResistance > 1e-15)
                                pTerms.Add($"{current}·r_{e.Name}");
                            break;
                        case ElementType.Inductor when settings.Mode == CircuitAnalysisMode.AC:
                            qTerms.Add($"+{current}·ω{e.Name}");
                            break;
                        case ElementType.Capacitor when settings.Mode == CircuitAnalysisMode.AC:
                            qTerms.Add($"−{current}/(ω{e.Name})");
                            break;
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"  Pпотр = {(pTerms.Count == 0 ? "0" : string.Join(" + ", pTerms))}");
            sb.AppendLine($"  Qпотр = {(qTerms.Count == 0 ? "0" : JoinSigned(qTerms))}");
            sb.AppendLine("  S̲потр = Pпотр + jQпотр");
            sb.AppendLine("  Для каждой ветви: S̲ветв,k = U̲k·I̲k*; S̲ист,k = S̲пасс,k − S̲ветв,k");
            sb.AppendLine("  Баланс: ΣS̲ист = ΣS̲потр  ⇔  ΣPист=ΣPпотр и ΣQист=ΣQпотр");
            return sb.ToString();
        }

        private static string BuildNumeric(CalculationResult result, CircuitAnalysisSettings settings)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < result.BranchResults.Count; i++)
            {
                var r = result.BranchResults[i];
                sb.AppendLine($"  Ветвь I{i + 1} [{r.Branch}]:");
                sb.AppendLine($"    S̲ветв = {Phasor.Rectangular(r.TerminalComplexPower)} ВА; |S|={r.TerminalComplexPower.Magnitude:0.######} ВА");

                double i2 = r.CurrentPhasor.Magnitude * r.CurrentPhasor.Magnitude;
                foreach (var e in r.Branch.Elements)
                {
                    Complex se = e.Type switch
                    {
                        ElementType.Resistor => new Complex(i2 * e.Value, 0),
                        ElementType.Inductor when settings.Mode == CircuitAnalysisMode.AC => new Complex(0, i2 * settings.AngularFrequency * e.Value),
                        ElementType.Capacitor when settings.Mode == CircuitAnalysisMode.AC => new Complex(0, -i2 / (settings.AngularFrequency * e.Value)),
                        ElementType.VoltageSource or ElementType.CurrentSource when e.InternalResistance > 1e-15 => new Complex(i2 * e.InternalResistance, 0),
                        _ => Complex.Zero
                    };
                    if (!Phasor.NearlyZero(se))
                        sb.AppendLine($"      {e.Name}: P={se.Real:0.######} Вт; Q={se.Imaginary:0.######} вар; |S|={se.Magnitude:0.######} ВА");
                }

                sb.AppendLine($"    S̲пасс = {Phasor.Rectangular(r.PassiveComplexPower)} ВА");
                if (!Phasor.NearlyZero(r.SourceComplexPowerGenerated))
                    sb.AppendLine($"    S̲ист(ген.) = {Phasor.Rectangular(r.SourceComplexPowerGenerated)} ВА; P={r.SourceComplexPowerGenerated.Real:0.######} Вт; Q={r.SourceComplexPowerGenerated.Imaginary:0.######} вар");
            }

            var g = result.TotalComplexPowerGenerated;
            var c = result.TotalComplexPowerConsumed;
            sb.AppendLine();
            sb.AppendLine($"  ΣS̲ист = {Phasor.Rectangular(g)} ВА = {Phasor.Exponential(g)} ВА");
            sb.AppendLine($"    ΣPист = {g.Real:0.######} Вт; ΣQист = {g.Imaginary:0.######} вар; |ΣSист| = {g.Magnitude:0.######} ВА");
            sb.AppendLine($"  ΣS̲потр = {Phasor.Rectangular(c)} ВА = {Phasor.Exponential(c)} ВА");
            sb.AppendLine($"    ΣPпотр = {c.Real:0.######} Вт; ΣQпотр = {c.Imaginary:0.######} вар; |ΣSпотр| = {c.Magnitude:0.######} ВА");
            sb.AppendLine($"  ΔS̲ = {Phasor.Rectangular(result.ComplexPowerImbalance)} ВА; |ΔS|={result.PowerImbalance:E3} ВА  {(result.PowerBalanceOk ? "✓" : "✗")}");
            return sb.ToString();
        }

        private static Complex Clean(Complex z)
        {
            double re = Math.Abs(z.Real) < 1e-10 ? 0 : z.Real;
            double im = Math.Abs(z.Imaginary) < 1e-10 ? 0 : z.Imaginary;
            return new Complex(re, im);
        }

        private static string JoinSigned(IEnumerable<string> terms)
        {
            string text = string.Join(" ", terms);
            return text.StartsWith("+", StringComparison.Ordinal) ? text[1..] : text;
        }
    }
}
