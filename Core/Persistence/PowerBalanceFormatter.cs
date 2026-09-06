using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    internal static class PowerBalanceFormatter
    {
        public static void AddPowerBalance(
            CalculationResult result,
            string generalTitle,
            string numericTitle)
        {
            double generated = result.BranchResults.Sum(r => r.PowerGenerated);
            double consumed = result.BranchResults.Sum(r => r.PowerConsumed);
            result.TotalPowerGenerated = generated;
            result.TotalPowerConsumed = consumed;

            result.Steps.Add(new SolutionStep
            {
                Title = generalTitle,
                Description = "Записываем баланс мощностей в общем виде. Pист — знаковая мощность источников (плюс — генерация, минус — поглощение), Pпотр — джоулевы потери в сопротивлениях.",
                MatrixText = BuildGeneral(result.BranchResults)
            });

            result.Steps.Add(new SolutionStep
            {
                Title = numericTitle,
                Description = "Подставляем найденные токи и номиналы элементов:",
                MatrixText = BuildNumeric(result.BranchResults, generated, consumed, result.PowerBalanceOk)
            });
        }

        private static string BuildGeneral(IReadOnlyList<BranchResult> rows)
        {
            var sourceTerms = new List<string>();
            var resistorTerms = new List<string>();

            for (int i = 0; i < rows.Count; i++)
            {
                var branch = rows[i].Branch;
                string current = $"I{i + 1}";

                foreach (var element in branch.Elements)
                {
                    if (element.Type == ElementType.Resistor)
                        resistorTerms.Add($"{current}²·{element.Name}");

                    if ((element.Type is ElementType.VoltageSource or ElementType.CurrentSource) &&
                        element.InternalResistance > 1e-15)
                        resistorTerms.Add($"{current}²·r_{element.Name}");

                    if (element.Type == ElementType.VoltageSource)
                    {
                        int orientedEmfSign = branch.GetElementDirection(element) *
                                              (element.IsPositiveAtStart ? 1 : -1);
                        // Pген источника = -Eветви * Iветви.
                        int generationSign = -orientedEmfSign;
                        sourceTerms.Add($"{(generationSign > 0 ? "+" : "−")}{current}·{element.Name}");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"  Pист = {JoinSigned(sourceTerms)}");
            sb.AppendLine($"  Pпотр = {(resistorTerms.Count == 0 ? "0" : string.Join(" + ", resistorTerms))}");
            sb.AppendLine("  Условие баланса: Pист = Pпотр");
            return sb.ToString();
        }

        private static string BuildNumeric(
            IReadOnlyList<BranchResult> rows,
            double generated,
            double consumed,
            bool ok)
        {
            var sourceTerms = new List<string>();
            var resistorTerms = new List<string>();

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                double current = row.Current;
                foreach (var element in row.Branch.Elements)
                {
                    if (element.Type == ElementType.Resistor)
                        resistorTerms.Add($"({current:F6})²·{element.Value:F6}");

                    if ((element.Type is ElementType.VoltageSource or ElementType.CurrentSource) &&
                        element.InternalResistance > 1e-15)
                        resistorTerms.Add($"({current:F6})²·{element.InternalResistance:F6}");

                    if (element.Type == ElementType.VoltageSource)
                    {
                        int orientedEmfSign = row.Branch.GetElementDirection(element) *
                                              (element.IsPositiveAtStart ? 1 : -1);
                        int generationSign = -orientedEmfSign;
                        sourceTerms.Add($"{(generationSign > 0 ? "+" : "−")}({current:F6})·{element.Value:F6}");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"  Pист = {JoinSigned(sourceTerms)} = {generated:F6} Вт");
            sb.AppendLine($"  Pпотр = {(resistorTerms.Count == 0 ? "0" : string.Join(" + ", resistorTerms))} = {consumed:F6} Вт");
            sb.AppendLine($"  ΔP = |Pист − Pпотр| = {Math.Abs(generated - consumed):E3} Вт  {(ok ? "✓" : "✗")}");
            return sb.ToString();
        }

        private static string JoinSigned(List<string> terms)
        {
            if (terms.Count == 0) return "0";
            string text = string.Join(" ", terms);
            if (text.StartsWith("+", StringComparison.Ordinal)) text = text[1..];
            return text;
        }
    }
}
