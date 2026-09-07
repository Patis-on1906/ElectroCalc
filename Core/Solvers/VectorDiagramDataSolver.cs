using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Готовит численные и структурированные данные для векторных диаграмм.
    /// Выводит фазоры в алгебраической и показательной формах, а также формирует
    /// наборы векторов для автоматического построения по ветвям, контурам и узлам.
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

                BuildDiagramData(result);

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

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var voltages = CalculateElementVoltages(row);

                sb.AppendLine($"  Ветвь {i + 1}: {row.Branch}, I̲={Both(row.CurrentPhasor, "А")}");
                foreach (var e in row.Branch.Elements)
                {
                    if (!voltages[e].HasValue)
                    {
                        sb.AppendLine($"    {e.Name}: индивидуальное напряжение не определяется однозначно (несколько идеальных источников тока в одной ветви).");
                        continue;
                    }
                    sb.AppendLine($"    U̲_{e.Name} = {Both(voltages[e]!.Value, "В")}");
                }
                if (voltages.Values.All(value => value.HasValue))
                {
                    Complex sum = voltages.Values.Aggregate(Complex.Zero, (a, b) => a + b!.Value);
                    sb.AppendLine($"    Проверка ΣU̲элем = {Both(sum, "В")}; U̲ветви={Both(row.VoltagePhasor, "В")}");
                }
            }

            return sb.ToString();
        }

        private Dictionary<CircuitElement, Complex?> CalculateElementVoltages(BranchResult row)
        {
            Complex current = row.CurrentPhasor;
            double w = _settings.AngularFrequency;
            var voltages = new Dictionary<CircuitElement, Complex?>();
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
                voltages[e] = u;
                knownSum += u;
            }

            if (currentSources.Count == 1)
            {
                // Остаток ветвевого напряжения относится к идеальной части
                // единственного источника тока.
                var source = currentSources[0];
                voltages[source] = voltages[source]!.Value + row.VoltagePhasor - knownSum;
            }
            else if (currentSources.Count > 1)
            {
                // Суммарное напряжение нескольких последовательных идеальных J
                // известно, но распределение между ними не единственно.
                foreach (var source in currentSources)
                    voltages[source] = null;
            }

            return voltages;
        }

        private void BuildDiagramData(CalculationResult result)
        {
            var rows = result.BranchResults;
            var branchNumbers = rows.Select((row, index) => (row.Branch, Number: index + 1))
                .ToDictionary(item => item.Branch, item => item.Number);

            var currents = new PhasorDiagramData
            {
                Title = "Токи ветвей",
                Description = "Все токи показаны из начала координат в положительных направлениях расчётных ветвей.",
                Unit = "А"
            };
            var voltages = new PhasorDiagramData
            {
                Title = "Напряжения ветвей",
                Description = "Все напряжения U=φstart−φend показаны из начала координат.",
                Unit = "В"
            };
            for (int i = 0; i < rows.Count; i++)
            {
                currents.Vectors.Add(new PhasorDiagramVector { Label = $"I{i + 1}", Value = rows[i].CurrentPhasor });
                voltages.Vectors.Add(new PhasorDiagramVector { Label = $"U{i + 1}", Value = rows[i].VoltagePhasor });
            }
            result.VectorDiagrams.Add(currents);
            result.VectorDiagrams.Add(voltages);

            for (int i = 0; i < rows.Count; i++)
            {
                var elementVoltages = CalculateElementVoltages(rows[i]);
                if (elementVoltages.Values.Any(value => !value.HasValue)) continue;

                var diagram = new PhasorDiagramData
                {
                    Title = $"Ветвь {i + 1}: напряжения элементов",
                    Description = $"Векторы элементов {rows[i].Branch.ElementNames} сложены голова к хвосту; результирующий вектор равен U{i + 1}.",
                    Unit = "В",
                    HeadToTail = true,
                    HasExpectedResultant = true,
                    ExpectedResultant = rows[i].VoltagePhasor,
                    ExpectedResultantLabel = $"U{i + 1}"
                };
                foreach (var element in rows[i].Branch.Elements)
                    diagram.Vectors.Add(new PhasorDiagramVector
                    {
                        Label = $"U{element.Name}",
                        Value = elementVoltages[element]!.Value
                    });
                result.VectorDiagrams.Add(diagram);
            }

            var voltageMap = rows.ToDictionary(row => row.Branch, row => row.VoltagePhasor);
            foreach (var contour in CircuitContourFinder.FindAll(_graph))
            {
                var diagram = new PhasorDiagramData
                {
                    Title = $"Контур {contour.Number}: баланс напряжений",
                    Description = "Ориентированные напряжения сложены голова к хвосту. Замкнутый многоугольник подтверждает второй закон Кирхгофа.",
                    Unit = "В",
                    HeadToTail = true,
                    HasExpectedResultant = true,
                    ExpectedResultant = Complex.Zero,
                    ExpectedResultantLabel = "ΣU=0"
                };
                foreach (var branch in contour.Branches)
                    diagram.Vectors.Add(new PhasorDiagramVector
                    {
                        Label = $"{(branch.Direction > 0 ? "+" : "−")}U{branchNumbers[branch.Branch]}",
                        Value = branch.Direction * voltageMap[branch.Branch]
                    });
                result.VectorDiagrams.Add(diagram);
            }

            var currentMap = rows.ToDictionary(row => row.Branch, row => row.CurrentPhasor);
            foreach (var node in _graph.Nodes)
            {
                var diagram = new PhasorDiagramData
                {
                    Title = $"Узел {node.Label}: баланс токов",
                    Description = "Токи ориентированы от узла наружу и сложены голова к хвосту. Замыкание подтверждает первый закон Кирхгофа.",
                    Unit = "А",
                    HeadToTail = true,
                    HasExpectedResultant = true,
                    ExpectedResultant = Complex.Zero,
                    ExpectedResultantLabel = "ΣI=0"
                };
                foreach (var branch in _graph.Branches.Where(b => b.StartNode == node || b.EndNode == node))
                    diagram.Vectors.Add(new PhasorDiagramVector
                    {
                        Label = $"{(branch.StartNode == node ? "+" : "−")}I{branchNumbers[branch]}",
                        Value = branch.StartNode == node ? currentMap[branch] : -currentMap[branch]
                    });
                result.VectorDiagrams.Add(diagram);
            }
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
