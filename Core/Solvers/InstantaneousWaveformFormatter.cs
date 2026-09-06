using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Восстанавливает мгновенные синусоидальные функции из RMS-фазоров.
    /// Принято единое соглашение проекта:
    ///   x(t) = sqrt(2) * |X| * sin(omega*t + phi),
    /// где X — действующее комплексное значение (RMS-фазор).
    /// Модуль не решает схему и поэтому может использоваться любым AC-решателем.
    /// </summary>
    internal static class InstantaneousWaveformFormatter
    {
        public static void AddStep(CalculationResult result, CircuitAnalysisSettings settings, string title)
        {
            if (settings.Mode != CircuitAnalysisMode.AC || result.BranchResults.Count == 0)
                return;

            var sb = new StringBuilder();
            double w = settings.AngularFrequency;
            sb.AppendLine($"  f = {settings.FrequencyHz.ToString("0.######", CultureInfo.InvariantCulture)} Гц");
            sb.AppendLine($"  ω = 2πf = {w.ToString("0.######", CultureInfo.InvariantCulture)} рад/с");
            sb.AppendLine("  Для RMS-фазора X̲=|X|∠φ: x(t)=√2·|X|·sin(ωt+φ).");
            sb.AppendLine();
            sb.AppendLine("  Токи и напряжения расчётных ветвей:");

            for (int i = 0; i < result.BranchResults.Count; i++)
            {
                var row = result.BranchResults[i];
                sb.AppendLine($"  Ветвь {i + 1}: {row.Branch}");
                sb.AppendLine($"    i{i + 1}(t) = {Function(row.CurrentPhasor, w, "А")}");
                sb.AppendLine($"    u{i + 1}(t) = {Function(row.VoltagePhasor, w, "В")}");
            }

            var sources = result.BranchResults
                .SelectMany(r => r.Branch.Elements)
                .Where(e => e.Type is ElementType.VoltageSource or ElementType.CurrentSource)
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .ToList();

            if (sources.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  Заданные мгновенные функции независимых источников (в направлении их собственной полярности):");
                foreach (var source in sources)
                {
                    Complex phasor = source.SourcePhasor(settings);
                    string symbol = source.Type == ElementType.VoltageSource ? "e" : "j";
                    string unit = source.Type == ElementType.VoltageSource ? "В" : "А";
                    sb.AppendLine($"    {symbol}_{source.Name}(t) = {Function(phasor, w, unit)}");
                }
            }

            result.Steps.Add(new SolutionStep
            {
                Title = title,
                Description = "Переходим от действующих комплексных значений к мгновенным синусоидальным функциям. Амплитуда мгновенной функции равна √2·RMS, фаза сохраняется.",
                MatrixText = sb.ToString()
            });
        }

        public static string Function(Complex rmsPhasor, double angularFrequency, string unit)
        {
            double amplitude = Math.Sqrt(2.0) * rmsPhasor.Magnitude;
            double phase = Phasor.PhaseDegrees(rmsPhasor);
            string phaseText = Math.Abs(phase) < 1e-12
                ? string.Empty
                : phase >= 0
                    ? $" + {phase.ToString("0.##", CultureInfo.InvariantCulture)}°"
                    : $" − {Math.Abs(phase).ToString("0.##", CultureInfo.InvariantCulture)}°";

            return $"{amplitude.ToString("0.######", CultureInfo.InvariantCulture)}·sin(" +
                   $"{angularFrequency.ToString("0.######", CultureInfo.InvariantCulture)}·t{phaseText}) {unit}";
        }
    }
}
