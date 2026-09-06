using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Готовит численные данные для ручного построения векторных диаграмм.
    /// Ничего не рисует. Выводит фазоры в алгебраической и показательной формах,
    /// а также группирует их по ветвям, контурам и узлам.
    /// </summary>
    public sealed class VectorDiagramDataSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitAnalysisSettings _settings;

        public VectorDiagramDataSolver(CircuitGraph graph, CircuitAnalysisSettings settings)
        {
            _graph = graph;
            _settings = settings.Clone();
        }

        public CalculationResult Solve()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.VectorData,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = false
            };

            try
            {
                _settings.Validate();
                if (_settings.Mode != CircuitAnalysisMode.AC)
                    throw new InvalidOperationException("Векторные диаграммы предназначены для синусоидального установившегося режима AC.");

                var baseResult = new NodePotentialSolver(_graph, _settings).Solve();
                if (!baseResult.Success)
                    throw new InvalidOperationException(baseResult.ErrorMessage ?? "Не удалось рассчитать исходную схему методом МУП.");

                foreach (var r in baseResult.BranchResults)
                    result.BranchResults.Add(Clone(r));

                result.TotalComplexPowerGenerated = baseResult.TotalComplexPowerGenerated;
                result.TotalComplexPowerConsumed = baseResult.TotalComplexPowerConsumed;
                result.PowerBalanceAvailable = baseResult.PowerBalanceAvailable;

                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Исходные фазоры ветвей",
                    Description = $"Расчёт выполнен для RMS-фазоров при f={_settings.FrequencyHz:0.######} Гц. Для каждого вектора приводятся алгебраическая форма a+jb и показательная форма M·e^(jφ).",
                    MatrixText = BuildBranchPhasors(result.BranchResults)
                });

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Векторы напряжений отдельных элементов",
                    Description = "Пассивные элементы рассчитываются по U̲=Z̲I̲. Для источников ЭДС учитывается их фаза, полярность и направление элемента внутри ветви.",
                    MatrixText = BuildElementVoltages(result.BranchResults)
                });

                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Данные для векторных диаграмм напряжений по контурам",
                    Description = "Для каждого найденного замкнутого контура перечислены ориентированные векторы напряжений. Их векторная сумма должна быть равна нулю по второму закону Кирхгофа.",
                    MatrixText = BuildContourVectors(result.BranchResults)
                });

                result.Steps.Add(new SolutionStep
                {
                    Title = "4. Данные для векторных диаграмм токов по узлам",
                    Description = "Для каждого существенного узла токи ориентированы от узла наружу. Их векторная сумма должна быть равна нулю по первому закону Кирхгофа.",
                    MatrixText = BuildNodeVectors(result.BranchResults)
                });

                InstantaneousWaveformFormatter.AddStep(result, _settings,
                    "5. Мгновенные функции из рассчитанных фазоров");

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private string BuildBranchPhasors(IReadOnlyList<BranchResult> rows)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.AppendLine($"  Ветвь {i + 1}: {r.Branch}");
                sb.AppendLine($"    I̲{i + 1} = {Both(r.CurrentPhasor, "А")}");
                sb.AppendLine($"    U̲{i + 1} = {Both(r.VoltagePhasor, "В")}");
            }
            return sb.ToString();
        }

        private string BuildElementVoltages(IReadOnlyList<BranchResult> rows)
        {
            var sb = new StringBuilder();
            double w = _settings.AngularFrequency;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                Complex current = row.CurrentPhasor;
                var known = new Dictionary<CircuitElement, Complex>();
                var currentSources = row.Branch.Elements.Where(e => e.Type == ElementType.CurrentSource).ToList();
                Complex knownSum = Complex.Zero;

                foreach (var e in row.Branch.Elements)
                {
                    Complex u = e.Type switch
                    {
                        ElementType.Resistor => current * e.Value,
                        ElementType.Inductor => current * Complex.ImaginaryOne * w * e.Value,
                        ElementType.Capacitor => current / (Complex.ImaginaryOne * w * e.Value),
                        ElementType.VoltageSource => row.Branch.GetElementDirection(e) *
                                                     (e.IsPositiveAtStart ? 1.0 : -1.0) * e.SourcePhasor(_settings) +
                                                     current * e.InternalResistance,
                        ElementType.CurrentSource => current * e.InternalResistance,
                        _ => Complex.Zero
                    };
                    known[e] = u;
                    knownSum += u;
                }

                if (currentSources.Count == 1)
                {
                    // Остаток ветвевого напряжения относится к идеальной части единственного источника тока.
                    var j = currentSources[0];
                    known[j] += row.VoltagePhasor - knownSum;
                }

                sb.AppendLine($"  Ветвь {i + 1}: {row.Branch}, I̲={Both(current, "А")}");
                foreach (var e in row.Branch.Elements)
                {
                    if (e.Type == ElementType.CurrentSource && currentSources.Count > 1)
                    {
                        sb.AppendLine($"    {e.Name}: индивидуальное напряжение не определяется однозначно (несколько идеальных источников тока в одной ветви).");
                        continue;
                    }
                    sb.AppendLine($"    U̲_{e.Name} = {Both(known[e], "В")}");
                }
                sb.AppendLine($"    Проверка ΣU̲элем = {Both(known.Values.Aggregate(Complex.Zero, (a,b) => a+b), "В")}; U̲ветви={Both(row.VoltagePhasor, "В")}");
            }

            return sb.ToString();
        }

        private string BuildContourVectors(IReadOnlyList<BranchResult> rows)
        {
            var map = rows.ToDictionary(r => r.Branch, r => r.VoltagePhasor);
            var contours = CircuitContourFinder.FindAll(_graph);
            if (contours.Count == 0) return "  Замкнутые контуры не найдены.";

            var sb = new StringBuilder();
            foreach (var contour in contours)
            {
                sb.AppendLine($"  Контур {contour.Number}:");
                Complex sum = Complex.Zero;
                foreach (var cb in contour.Branches)
                {
                    Complex u = cb.Direction * map[cb.Branch];
                    sum += u;
                    sb.AppendLine($"    {(cb.Direction > 0 ? "+" : "−")}{cb.Branch}: U̲ = {Both(u, "В")}");
                }
                sb.AppendLine($"    ΣU̲ = {Both(sum, "В")}  {(sum.Magnitude <= 1e-6 ? "✓" : "⚠")}");
            }
            return sb.ToString();
        }

        private string BuildNodeVectors(IReadOnlyList<BranchResult> rows)
        {
            var map = rows.ToDictionary(r => r.Branch, r => r.CurrentPhasor);
            var branchNumber = rows.Select((r, i) => (r.Branch, Number: i + 1)).ToDictionary(x => x.Branch, x => x.Number);
            var sb = new StringBuilder();
            foreach (var node in _graph.Nodes)
            {
                sb.AppendLine($"  Узел {node.Label}:");
                Complex sum = Complex.Zero;
                foreach (var branch in _graph.Branches.Where(b => b.StartNode == node || b.EndNode == node))
                {
                    Complex outward = branch.StartNode == node ? map[branch] : -map[branch];
                    sum += outward;
                    sb.AppendLine($"    I̲{branchNumber[branch]} наружу [{branch}] = {Both(outward, "А")}");
                }
                sb.AppendLine($"    ΣI̲ = {Both(sum, "А")}  {(sum.Magnitude <= 1e-6 ? "✓" : "⚠")}");
            }
            return sb.ToString();
        }

        private static BranchResult Clone(BranchResult r) => new()
        {
            Branch = r.Branch,
            CurrentPhasor = r.CurrentPhasor,
            VoltagePhasor = r.VoltagePhasor,
            PassiveComplexPower = r.PassiveComplexPower,
            SourceComplexPowerGenerated = r.SourceComplexPowerGenerated,
            TerminalComplexPower = r.TerminalComplexPower
        };

        private static string Both(Complex value, string unit) =>
            $"{Phasor.Rectangular(value)} {unit} = {Phasor.Exponential(value)} {unit}";
    }
}
