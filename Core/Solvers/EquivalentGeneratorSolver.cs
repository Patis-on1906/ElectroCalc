using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Метод эквивалентного генератора (Тевенина) относительно выбранной ветви нагрузки.
    /// Последовательность решения ориентирована на учебное оформление:
    /// 1) разрыв нагрузки и уравнение Кирхгофа с Uхх;
    /// 2) расчёт разомкнутой схемы МУП;
    /// 3) подстановка токов в уравнение и нахождение Uхх;
    /// 4) входное сопротивление;
    /// 5) ток выбранной ветви.
    /// </summary>
    public class EquivalentGeneratorSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitBranch _loadBranch;
        private readonly CircuitAnalysisSettings _settings;

        public EquivalentGeneratorSolver(
            CircuitGraph graph,
            CircuitBranch loadBranch,
            CircuitAnalysisSettings? settings = null)
        {
            _graph = graph;
            _loadBranch = loadBranch;
            _settings = (settings ?? new CircuitAnalysisSettings()).Clone();
        }

        public CalculationResult Solve()
        {
            _settings.Validate();
            return _settings.Mode == CircuitAnalysisMode.AC ? SolveAc() : SolveDc();
        }

        private CalculationResult SolveDc()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.EquivalentGenerator,
                Analysis = _settings.Clone()
            };

            try
            {
                var loadModel = DcBranchModel.Create(_loadBranch);
                if (_loadBranch.HasCurrentSource || _loadBranch.HasCapacitor ||
                    (loadModel.Kind != DcBranchKind.Resistive && loadModel.Kind != DcBranchKind.IdealVoltage))
                {
                    return Fail(result,
                        "Для метода эквивалентного генератора в режиме DC выберите ветвь без J и C. " +
                        "Допускаются R, L и источник ЭДС E (в том числе последовательная ветвь E+R).");
                }

                double rLoad = loadModel.Resistance;
                double eLoad = loadModel.Emf;

                // Разрываем выбранную ветвь и ищем путь от её Start к End через оставшуюся схему.
                // Вместе с самим разрывом этот путь образует контур для записи Uхх по 2-му закону Кирхгофа.
                var graphOc = CloneWithoutBranch(_loadBranch);
                var aOc = graphOc.Nodes.First(n => n.Label == _loadBranch.StartNode.Label);
                var bOc = graphOc.Nodes.First(n => n.Label == _loadBranch.EndNode.Label);
                var openPath = FindVoltageExpressiblePath(graphOc, aOc, bOc);
                if (openPath == null)
                {
                    return Fail(result,
                        "После разрыва выбранной ветви не удалось построить контур для Uхх, состоящий из ветвей, " +
                        "напряжение которых выражается через R, E и ток ветви. Проверьте связность схемы. " +
                        "Если единственный путь проходит через идеальный источник тока J, для такого контура " +
                        "нужно отдельно учитывать неизвестное напряжение источника тока.");
                }

                string kvlGeneral = BuildOpenCircuitKvl(openPath);
                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Уравнение контура с напряжением холостого хода",
                    Description =
                        $"Разрываем выбранную ветвь {_loadBranch}. Между её зажимами возникает Uхх = " +
                        $"φ{_loadBranch.StartNode.Label} − φ{_loadBranch.EndNode.Label}. " +
                        "Для контура, проходящего через оставшуюся схему, записываем 2-й закон Кирхгофа. " +
                        "Обозначение Iкх означает ток соответствующей исходной ветви в режиме холостого хода.",
                    MatrixText = "  " + kvlGeneral
                });

                // МУП для разомкнутой схемы. Используем тот же NodePotentialSolver, что и для обычного МУП,
                // но в итог метода Тевенина переносим только учебные шаги до токов включительно.
                var oc = new NodePotentialSolver(graphOc).Solve();
                if (!oc.Success)
                    return Fail(result, "Не удалось рассчитать разомкнутую схему методом МУП: " + oc.ErrorMessage);

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Расчёт токов холостого хода методом МУП",
                    Description =
                        "Рассчитываем разомкнутую схему тем же методом узловых потенциалов, что и в отдельном режиме МУП: " +
                        "выбираем опорный узел, записываем систему в общем виде, подставляем значения, находим потенциалы и токи."
                });
                AddMupSubsteps(result, oc);

                // Вычисляем Uхх именно из записанного уравнения Кирхгофа.
                // Потенциалы используются лишь как независимая внутренняя проверка знака.
                double uOc = EvaluateOpenCircuitPathVoltage(openPath, oc);
                double uOcByPotentials = aOc.Potential - bOc.Potential;
                if (Math.Abs(uOc - uOcByPotentials) > 1e-6 * Math.Max(1.0, Math.Abs(uOcByPotentials)))
                {
                    throw new InvalidOperationException(
                        $"Внутренняя проверка Uхх не сошлась: по контуру {uOc:F6} В, " +
                        $"по потенциалам {uOcByPotentials:F6} В.");
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Подстановка в уравнение Uхх",
                    Description = "Подставляем найденные методом МУП токи холостого хода в исходное уравнение контура:",
                    MatrixText = BuildOpenCircuitNumericSubstitution(openPath, oc)
                });

                // Входное сопротивление остальной схемы относительно зажимов разрыва.
                var graphReq = CloneDeactivatedSources();
                var aReq = graphReq.Nodes.First(n => n.Label == _loadBranch.StartNode.Label);
                var bReq = graphReq.Nodes.First(n => n.Label == _loadBranch.EndNode.Label);
                double rEq = ComputeInputResistance(graphReq, aReq, bReq);

                if (double.IsNaN(rEq) || double.IsInfinity(rEq) || rEq < -1e-9)
                    return Fail(result, "Не удалось определить конечное Rвх. Проверьте топологию и источники схемы.");

                result.Steps.Add(new SolutionStep
                {
                    Title = "4. Поиск входного сопротивления Rвх",
                    Description =
                        "Удалённая ветвь нагрузки остаётся разомкнутой. Независимые источники остальной схемы " +
                        "деактивируем: идеальные E заменяем коротким замыканием, E с внутренним сопротивлением — " +
                        "этим сопротивлением, идеальные J — разрывом. Между зажимами разрыва подключаем тестовый ток 1 А.",
                    MatrixText =
                        "  Jтест = 1 А\n" +
                        $"  Rвх = Uтест / Jтест = {rEq:F6} / 1 = {rEq:F6} Ом"
                });

                // Для выбранной ветви Uн = Rн*Iн + Eн, а для эквивалентного генератора
                // Uн = Uхх - Rвх*Iн. Следовательно Iн = (Uхх-Eн)/(Rвх+Rн).
                double denominator = rEq + rLoad;
                if (Math.Abs(denominator) < 1e-12)
                    return Fail(result,
                        "Rвх + Rн = 0. Для двух идеальных источников напряжения без сопротивления ток не определяется " +
                        "идеальной DC-моделью; задайте внутреннее/последовательное сопротивление.");

                double iLoad = (uOc - eLoad) / denominator;
                var loadCalc = new StringBuilder();
                loadCalc.AppendLine("  Uн = Uхх − Iн·Rвх");
                loadCalc.AppendLine("  Uн = Iн·Rн + Eн");
                loadCalc.AppendLine("  Uхх − Iн·Rвх = Iн·Rн + Eн");
                loadCalc.AppendLine("  Iн = (Uхх − Eн) / (Rвх + Rн)");
                loadCalc.AppendLine($"     = ({uOc:F6} − {eLoad:F6}) / ({rEq:F6} + {rLoad:F6})");
                loadCalc.AppendLine($"     = {iLoad:F6} А");

                result.Steps.Add(new SolutionStep
                {
                    Title = "5. Нахождение тока в ветви разрыва",
                    Description =
                        Math.Abs(eLoad) > 1e-12
                            ? "Возвращаем выбранную активную ветвь в схему и учитываем как её сопротивление, так и собственную ЭДС."
                            : "Возвращаем выбранную ветвь нагрузки в схему и находим её ток от эквивалентного генератора.",
                    MatrixText = loadCalc.ToString()
                });

                // Для таблицы результатов выполняем полный МУП, но его шаги не добавляем в решение Тевенина.
                // Это также позволяет проверить вычисленный ток нагрузки без загрязнения требуемой последовательности.
                var full = new NodePotentialSolver(_graph).Solve();
                if (!full.Success)
                    return Fail(result, "Проверочный расчёт исходной схемы не выполнен: " + full.ErrorMessage);

                foreach (var row in full.BranchResults)
                    result.BranchResults.Add(row);
                result.TotalPowerGenerated = full.TotalPowerGenerated;
                result.TotalPowerConsumed = full.TotalPowerConsumed;

                var actualLoad = full.BranchResults.First(r => r.Branch.Id == _loadBranch.Id);
                if (Math.Abs(iLoad - actualLoad.Current) > 1e-5 * Math.Max(1.0, Math.Abs(actualLoad.Current)))
                {
                    return Fail(result,
                        $"Внутренняя проверка метода эквивалентного генератора не сошлась: " +
                        $"Iн={iLoad:F6} А, МУП исходной схемы даёт {actualLoad.Current:F6} А.");
                }

                _loadBranch.Current = iLoad;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }


        /// <summary>
        /// Комплексный метод эквивалентного генератора для синусоидального установившегося режима.
        /// Последовательность полностью совпадает с DC-оформлением, но вместо Rвх используется Z̲вх,
        /// а Uхх, E, I и все промежуточные величины являются RMS-фазорами.
        /// </summary>
        private CalculationResult SolveAc()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.EquivalentGenerator,
                Analysis = _settings.Clone()
            };

            try
            {
                var loadModel = SteadyStateBranchModel.Create(_loadBranch, _settings);
                if (_loadBranch.HasCurrentSource || loadModel.Kind == SteadyStateBranchKind.Open)
                {
                    return Fail(result,
                        "Для метода эквивалентного генератора в AC выберите ветвь без идеального источника тока J. " +
                        "Допускаются R, L, C, источник ЭДС E и их последовательные сочетания.");
                }

                Complex zLoad = loadModel.Impedance;
                Complex eLoad = loadModel.Emf;

                var graphOc = CloneWithoutBranch(_loadBranch);
                var aOc = graphOc.Nodes.First(n => n.Label == _loadBranch.StartNode.Label);
                var bOc = graphOc.Nodes.First(n => n.Label == _loadBranch.EndNode.Label);
                var openPath = FindVoltageExpressiblePathAc(graphOc, aOc, bOc);
                if (openPath == null)
                {
                    return Fail(result,
                        "После разрыва выбранной ветви не удалось построить путь для уравнения U̲хх. " +
                        "Путь не должен проходить через идеальный источник тока, поскольку его напряжение является дополнительной неизвестной.");
                }

                string kvlGeneral = BuildOpenCircuitKvlAc(openPath);
                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Уравнение контура с напряжением холостого хода",
                    Description =
                        $"Разрываем выбранную ветвь {_loadBranch}. Возникает комплексное напряжение холостого хода " +
                        $"U̲хх = φ̲{_loadBranch.StartNode.Label} − φ̲{_loadBranch.EndNode.Label}. " +
                        "Для замыкающего пути через оставшуюся схему записываем второй закон Кирхгофа в символической форме.",
                    MatrixText = "  " + kvlGeneral
                });

                var oc = new NodePotentialSolver(graphOc, _settings).Solve();
                if (!oc.Success)
                    return Fail(result, "Не удалось рассчитать разомкнутую AC-схему методом МУП: " + oc.ErrorMessage);

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Расчёт токов холостого хода методом МУП",
                    Description =
                        "Разомкнутую схему рассчитываем общим комплексным МУП: выбор базисного узла → общий вид → " +
                        "подстановка комплексных значений → потенциалы → токи."
                });
                AddMupSubsteps(result, oc);

                Complex uOc = EvaluateOpenCircuitPathVoltageAc(openPath, oc);
                Complex uOcByPotentials = aOc.PotentialPhasor - bOc.PotentialPhasor;
                if (!Phasor.NearlyEqual(uOc, uOcByPotentials, 1e-7))
                {
                    throw new InvalidOperationException(
                        $"Внутренняя проверка U̲хх не сошлась: по контуру {Phasor.Rectangular(uOc)} В, " +
                        $"по потенциалам {Phasor.Rectangular(uOcByPotentials)} В.");
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Подстановка в уравнение U̲хх",
                    Description = "Подставляем найденные комплексные токи холостого хода в исходное уравнение контура и определяем фазор U̲хх:",
                    MatrixText = BuildOpenCircuitNumericSubstitutionAc(openPath, oc)
                });

                var graphZeq = CloneDeactivatedSources();
                var aZeq = graphZeq.Nodes.First(n => n.Label == _loadBranch.StartNode.Label);
                var bZeq = graphZeq.Nodes.First(n => n.Label == _loadBranch.EndNode.Label);
                Complex zEq = ComputeInputImpedance(graphZeq, aZeq, bZeq);

                if (!IsFinite(zEq))
                    return Fail(result, "Не удалось определить конечный комплексный входной импеданс Z̲вх. Проверьте топологию и источники схемы.");

                result.Steps.Add(new SolutionStep
                {
                    Title = "4. Поиск входного импеданса Z̲вх",
                    Description =
                        "Удалённая нагрузка остаётся разомкнутой. Независимые источники остальной схемы деактивируем: " +
                        "идеальный E → короткое замыкание, E с внутренним сопротивлением → его внутреннее сопротивление, " +
                        "идеальный J → разрыв. К зажимам прикладываем тестовый RMS-фазор тока 1∠0° А.",
                    MatrixText =
                        "  I̲тест = 1∠0° А\n" +
                        $"  U̲тест = {Both(zEq, "В")}\n" +
                        $"  Z̲вх = U̲тест / I̲тест = {Both(zEq, "Ом")}"
                });

                Complex denominator = zEq + zLoad;
                if (denominator.Magnitude < 1e-12)
                    return Fail(result,
                        "Z̲вх + Z̲н ≈ 0. Ток идеальной модели становится неопределённым/неограниченным. " +
                        "Проверьте параметры схемы и наличие физических потерь.");

                Complex iLoad = (uOc - eLoad) / denominator;
                var loadCalc = new StringBuilder();
                loadCalc.AppendLine("  U̲н = U̲хх − I̲н·Z̲вх");
                loadCalc.AppendLine("  U̲н = I̲н·Z̲н + E̲н");
                loadCalc.AppendLine("  I̲н = (U̲хх − E̲н) / (Z̲вх + Z̲н)");
                loadCalc.AppendLine($"  U̲хх = {Both(uOc, "В")}");
                loadCalc.AppendLine($"  E̲н  = {Both(eLoad, "В")}");
                loadCalc.AppendLine($"  Z̲вх = {Both(zEq, "Ом")}");
                loadCalc.AppendLine($"  Z̲н  = {Both(zLoad, "Ом")}");
                loadCalc.AppendLine($"  I̲н  = {Both(iLoad, "А")}");

                result.Steps.Add(new SolutionStep
                {
                    Title = "5. Нахождение тока в ветви разрыва",
                    Description = Phasor.NearlyZero(eLoad)
                        ? "Возвращаем выбранную нагрузочную ветвь и определяем её комплексный RMS-ток от эквивалентного генератора."
                        : "Возвращаем выбранную ветвь и учитываем одновременно её комплексный импеданс и собственную ЭДС.",
                    MatrixText = loadCalc.ToString()
                });

                var full = new NodePotentialSolver(_graph, _settings).Solve();
                if (!full.Success)
                    return Fail(result, "Проверочный расчёт исходной AC-схемы не выполнен: " + full.ErrorMessage);

                foreach (var row in full.BranchResults)
                    result.BranchResults.Add(row);
                result.TotalComplexPowerGenerated = full.TotalComplexPowerGenerated;
                result.TotalComplexPowerConsumed = full.TotalComplexPowerConsumed;
                result.PowerBalanceAvailable = full.PowerBalanceAvailable;

                var actualLoad = full.BranchResults.First(r => r.Branch.Id == _loadBranch.Id);
                if (!Phasor.NearlyEqual(iLoad, actualLoad.CurrentPhasor, 1e-6))
                {
                    return Fail(result,
                        "Внутренняя проверка комплексного МЭГ не сошлась: " +
                        $"I̲н={Phasor.Rectangular(iLoad)} А, МУП исходной схемы даёт " +
                        $"{Phasor.Rectangular(actualLoad.CurrentPhasor)} А.");
                }

                _loadBranch.CurrentPhasor = iLoad;
                InstantaneousWaveformFormatter.AddStep(result, _settings,
                    "6. Мгновенные функции исходной схемы");

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private List<PathEdge>? FindVoltageExpressiblePathAc(CircuitGraph graph, CircuitNode start, CircuitNode end)
        {
            var queue = new Queue<CircuitNode>();
            var visited = new HashSet<CircuitNode> { start };
            var previous = new Dictionary<CircuitNode, (CircuitNode node, CircuitBranch branch, int direction)>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node == end) break;

                foreach (var branch in graph.BranchesOf(node))
                {
                    var model = SteadyStateBranchModel.Create(branch, _settings);
                    if (model.Kind is SteadyStateBranchKind.Open or SteadyStateBranchKind.IdealCurrent)
                        continue;

                    CircuitNode other;
                    int direction;
                    if (branch.StartNode == node)
                    {
                        other = branch.EndNode;
                        direction = +1;
                    }
                    else
                    {
                        other = branch.StartNode;
                        direction = -1;
                    }

                    if (!visited.Add(other)) continue;
                    previous[other] = (node, branch, direction);
                    queue.Enqueue(other);
                }
            }

            if (!visited.Contains(end)) return null;
            var reverse = new List<PathEdge>();
            var current = end;
            while (current != start)
            {
                var p = previous[current];
                reverse.Add(new PathEdge(p.branch, p.direction));
                current = p.node;
            }
            reverse.Reverse();
            return reverse;
        }

        private string BuildOpenCircuitKvlAc(IReadOnlyList<PathEdge> path)
        {
            var terms = new List<SignedSymbol> { new(+1, "U̲хх") };
            foreach (var edge in path)
            {
                var model = SteadyStateBranchModel.Create(edge.Branch, _settings);
                int branchNumber = GetOriginalBranchNumber(edge.Branch);
                string current = $"I̲{branchNumber}х";

                if (model.Impedance.Magnitude > 1e-15)
                    AddTerm(terms, -edge.Direction, $"{current}·({model.SymbolicImpedance()})");

                foreach (var element in edge.Branch.Elements.Where(e => e.Type == ElementType.VoltageSource))
                {
                    int orientation = edge.Branch.GetElementDirection(element) * (element.IsPositiveAtStart ? 1 : -1);
                    AddTerm(terms, -edge.Direction * orientation, $"E̲_{element.Name}");
                }
            }
            return FormatSignedExpression(terms) + " = 0";
        }

        private string BuildOpenCircuitNumericSubstitutionAc(
            IReadOnlyList<PathEdge> path,
            CalculationResult oc)
        {
            var byBranch = oc.BranchResults.ToDictionary(r => r.Branch.Id);
            var symbolic = new List<SignedSymbol> { new(+1, "U̲хх") };
            var numeric = new List<SignedSymbol> { new(+1, "U̲хх") };
            Complex knownSum = Complex.Zero;

            foreach (var edge in path)
            {
                if (!byBranch.TryGetValue(edge.Branch.Id, out var row))
                    throw new InvalidOperationException($"Не найден ток холостого хода для ветви {edge.Branch}.");

                var model = SteadyStateBranchModel.Create(edge.Branch, _settings);
                int branchNumber = GetOriginalBranchNumber(edge.Branch);
                string currentName = $"I̲{branchNumber}х";

                if (model.Impedance.Magnitude > 1e-15)
                {
                    int sign = -edge.Direction;
                    AddTerm(symbolic, sign, $"{currentName}·({model.SymbolicImpedance()})");
                    AddTerm(numeric, sign,
                        $"({Phasor.Rectangular(row.CurrentPhasor)})·({Phasor.Rectangular(model.Impedance)})");
                    knownSum += sign * row.CurrentPhasor * model.Impedance;
                }

                foreach (var element in edge.Branch.Elements.Where(e => e.Type == ElementType.VoltageSource))
                {
                    int orientation = edge.Branch.GetElementDirection(element) * (element.IsPositiveAtStart ? 1 : -1);
                    int sign = -edge.Direction * orientation;
                    Complex source = element.SourcePhasor(_settings);
                    AddTerm(symbolic, sign, $"E̲_{element.Name}");
                    AddTerm(numeric, sign, $"({Phasor.Rectangular(source)})");
                    knownSum += sign * source;
                }
            }

            Complex uOc = -knownSum;
            var sb = new StringBuilder();
            sb.AppendLine("  " + FormatSignedExpression(symbolic) + " = 0");
            sb.AppendLine("  " + FormatSignedExpression(numeric) + " = 0");
            sb.AppendLine($"  Сумма известных членов = {Phasor.Rectangular(knownSum)} В");
            sb.AppendLine($"  U̲хх = {Both(uOc, "В")}");
            return sb.ToString();
        }

        private static Complex EvaluateOpenCircuitPathVoltageAc(
            IReadOnlyList<PathEdge> path,
            CalculationResult oc)
        {
            var byBranch = oc.BranchResults.ToDictionary(r => r.Branch.Id);
            Complex sum = Complex.Zero;
            foreach (var edge in path)
            {
                if (!byBranch.TryGetValue(edge.Branch.Id, out var row))
                    throw new InvalidOperationException($"Не найден результат ветви {edge.Branch} в расчёте МУП.");
                sum += edge.Direction * row.VoltagePhasor;
            }
            return sum;
        }

        private Complex ComputeInputImpedance(CircuitGraph graph, CircuitNode a, CircuitNode b)
        {
            // Источник направлен B→A, поэтому через исследуемую пассивную сеть течёт тестовый ток A→B = 1∠0° А.
            var test = graph.AddBranch(a, b);
            test.AddElement(new CircuitElement
            {
                Name = "J_test",
                Type = ElementType.CurrentSource,
                Value = 1.0,
                PhaseDegrees = 0.0,
                IsPositiveAtStart = false,
                InternalResistance = 0.0
            });

            var solved = new NodePotentialSolver(graph, _settings).Solve();
            Complex zeq = new(double.NaN, double.NaN);
            if (solved.Success)
                zeq = a.PotentialPhasor - b.PotentialPhasor;

            graph.RemoveBranch(test);
            return zeq;
        }

        private static bool IsFinite(Complex value) =>
            !double.IsNaN(value.Real) && !double.IsNaN(value.Imaginary) &&
            !double.IsInfinity(value.Real) && !double.IsInfinity(value.Imaginary);

        private static string Both(Complex value, string unit) =>
            $"{Phasor.Rectangular(value)} {unit} = {Phasor.Exponential(value)} {unit}";

        /// <summary>
        /// Переносит учебные шаги обычного МУП в пункт 2 метода Тевенина.
        /// Баланс мощностей разомкнутой вспомогательной схемы намеренно не выводится.
        /// </summary>
        private static void AddMupSubsteps(CalculationResult target, CalculationResult mup)
        {
            int sub = 1;
            foreach (var step in mup.Steps)
            {
                if (step.Title.Contains("Баланс", StringComparison.OrdinalIgnoreCase) ||
                    step.Title.Contains("Мгновенные функции", StringComparison.OrdinalIgnoreCase))
                    break;

                string cleanTitle = RemoveLeadingStepNumber(step.Title);
                target.Steps.Add(new SolutionStep
                {
                    Title = $"2.{sub}. {cleanTitle}",
                    Description = step.Description,
                    Formula = step.Formula,
                    MatrixText = step.MatrixText
                });
                sub++;
            }
        }

        private static string RemoveLeadingStepNumber(string title)
        {
            int dot = title.IndexOf('.');
            if (dot > 0 && title.Take(dot).All(char.IsDigit))
                return title[(dot + 1)..].TrimStart();
            return title;
        }

        /// <summary>
        /// Ищет кратчайший путь A→B, на котором напряжения ветвей можно записать через R, E и I.
        /// Идеальные источники тока и разомкнутые C в такой путь не включаются.
        /// </summary>
        private static List<PathEdge>? FindVoltageExpressiblePath(CircuitGraph graph, CircuitNode start, CircuitNode end)
        {
            var queue = new Queue<CircuitNode>();
            var visited = new HashSet<CircuitNode> { start };
            var previous = new Dictionary<CircuitNode, (CircuitNode node, CircuitBranch branch, int direction)>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node == end) break;

                foreach (var branch in graph.BranchesOf(node))
                {
                    var model = DcBranchModel.Create(branch);
                    if (model.Kind == DcBranchKind.Open || model.Kind == DcBranchKind.IdealCurrent)
                        continue;

                    CircuitNode other;
                    int direction;
                    if (branch.StartNode == node)
                    {
                        other = branch.EndNode;
                        direction = +1;
                    }
                    else
                    {
                        other = branch.StartNode;
                        direction = -1;
                    }

                    if (!visited.Add(other)) continue;
                    previous[other] = (node, branch, direction);
                    queue.Enqueue(other);
                }
            }

            if (!visited.Contains(end)) return null;

            var reverse = new List<PathEdge>();
            var current = end;
            while (current != start)
            {
                var p = previous[current];
                reverse.Add(new PathEdge(p.branch, p.direction));
                current = p.node;
            }
            reverse.Reverse();
            return reverse;
        }

        /// <summary>
        /// Строит Uхх - Σ(Uпадений по пути A→B) = 0 в общем виде.
        /// </summary>
        private string BuildOpenCircuitKvl(IReadOnlyList<PathEdge> path)
        {
            var terms = new List<SignedSymbol> { new(+1, "Uхх") };

            foreach (var edge in path)
            {
                int branchNumber = GetOriginalBranchNumber(edge.Branch);
                string current = $"I{branchNumber}х";

                foreach (var element in edge.Branch.Elements)
                {
                    switch (element.Type)
                    {
                        case ElementType.Resistor:
                            AddTerm(terms, -edge.Direction, $"{current}·{element.Name}");
                            break;

                        case ElementType.VoltageSource:
                        {
                            if (element.InternalResistance > 1e-12)
                                AddTerm(terms, -edge.Direction, $"{current}·r_{element.Name}");

                            int sourceSignInBranch = edge.Branch.GetElementDirection(element) *
                                                     (element.IsPositiveAtStart ? 1 : -1);
                            AddTerm(terms, -edge.Direction * sourceSignInBranch, element.Name);
                            break;
                        }

                        case ElementType.Inductor:
                            // L = 0 Ом в установившемся DC, член в уравнении равен нулю.
                            break;
                    }
                }
            }

            return FormatSignedExpression(terms) + " = 0";
        }

        private string BuildOpenCircuitNumericSubstitution(
            IReadOnlyList<PathEdge> path,
            CalculationResult oc)
        {
            var byBranch = oc.BranchResults.ToDictionary(r => r.Branch.Id);
            var symbolicTerms = new List<SignedSymbol> { new(+1, "Uхх") };
            var numericTerms = new List<SignedSymbol> { new(+1, "Uхх") };
            double knownSum = 0.0; // сумма всех членов уравнения, кроме Uхх

            foreach (var edge in path)
            {
                if (!byBranch.TryGetValue(edge.Branch.Id, out var branchResult))
                    throw new InvalidOperationException($"Не найден ток холостого хода для ветви {edge.Branch}.");

                int branchNumber = GetOriginalBranchNumber(edge.Branch);
                string currentName = $"I{branchNumber}х";
                double current = branchResult.Current;

                foreach (var element in edge.Branch.Elements)
                {
                    switch (element.Type)
                    {
                        case ElementType.Resistor:
                        {
                            int sign = -edge.Direction;
                            AddTerm(symbolicTerms, sign, $"{currentName}·{element.Name}");
                            AddTerm(numericTerms, sign, $"({current:F6})·{element.Value:F6}");
                            knownSum += sign * current * element.Value;
                            break;
                        }

                        case ElementType.VoltageSource:
                        {
                            if (element.InternalResistance > 1e-12)
                            {
                                int rSign = -edge.Direction;
                                AddTerm(symbolicTerms, rSign, $"{currentName}·r_{element.Name}");
                                AddTerm(numericTerms, rSign, $"({current:F6})·{element.InternalResistance:F6}");
                                knownSum += rSign * current * element.InternalResistance;
                            }

                            int sourceSignInBranch = edge.Branch.GetElementDirection(element) *
                                                     (element.IsPositiveAtStart ? 1 : -1);
                            int sign = -edge.Direction * sourceSignInBranch;
                            AddTerm(symbolicTerms, sign, element.Name);
                            AddTerm(numericTerms, sign, element.Value.ToString("F6"));
                            knownSum += sign * element.Value;
                            break;
                        }
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("  " + FormatSignedExpression(symbolicTerms) + " = 0");
            sb.AppendLine("  " + FormatSignedExpression(numericTerms) + " = 0");
            sb.AppendLine($"  Uхх {FormatSignedNumber(knownSum)} = 0");
            sb.AppendLine($"  Uхх = {-knownSum:F6} В");
            return sb.ToString();
        }

        /// <summary>
        /// Сумма падений напряжения по пути A→B. Это Uхх = φA-φB.
        /// </summary>
        private static double EvaluateOpenCircuitPathVoltage(
            IReadOnlyList<PathEdge> path,
            CalculationResult oc)
        {
            var byBranch = oc.BranchResults.ToDictionary(r => r.Branch.Id);
            double sum = 0.0;
            foreach (var edge in path)
            {
                if (!byBranch.TryGetValue(edge.Branch.Id, out var row))
                    throw new InvalidOperationException($"Не найден результат ветви {edge.Branch} в расчёте МУП.");
                sum += edge.Direction * row.Voltage;
            }
            return sum;
        }

        private int GetOriginalBranchNumber(CircuitBranch clonedBranch)
        {
            // При клонировании элементы сохраняют свои Id, поэтому это наиболее надёжная привязка.
            if (clonedBranch.Elements.Count > 0)
            {
                var ids = clonedBranch.Elements.Select(e => e.Id).ToHashSet();
                for (int i = 0; i < _graph.Branches.Count; i++)
                {
                    var original = _graph.Branches[i];
                    if (original.Elements.Count == ids.Count && original.Elements.All(e => ids.Contains(e.Id)))
                        return i + 1;
                }
            }

            // Резерв для ветви-провода.
            for (int i = 0; i < _graph.Branches.Count; i++)
            {
                var original = _graph.Branches[i];
                bool sameDirection = original.StartNode.Label == clonedBranch.StartNode.Label &&
                                     original.EndNode.Label == clonedBranch.EndNode.Label;
                bool reverseDirection = original.StartNode.Label == clonedBranch.EndNode.Label &&
                                        original.EndNode.Label == clonedBranch.StartNode.Label;
                if (original.Elements.Count == 0 && clonedBranch.Elements.Count == 0 &&
                    (sameDirection || reverseDirection))
                    return i + 1;
            }

            throw new InvalidOperationException($"Не удалось сопоставить ветвь {clonedBranch} с исходной схемой.");
        }

        private static void AddTerm(List<SignedSymbol> terms, int sign, string text)
        {
            if (sign == 0 || string.IsNullOrWhiteSpace(text)) return;
            terms.Add(new SignedSymbol(sign >= 0 ? +1 : -1, text));
        }

        private static string FormatSignedExpression(IReadOnlyList<SignedSymbol> terms)
        {
            if (terms.Count == 0) return "0";
            var sb = new StringBuilder();
            for (int i = 0; i < terms.Count; i++)
            {
                var term = terms[i];
                if (i == 0)
                {
                    if (term.Sign < 0) sb.Append("-");
                    sb.Append(term.Text);
                }
                else
                {
                    sb.Append(term.Sign >= 0 ? " + " : " - ");
                    sb.Append(term.Text);
                }
            }
            return sb.ToString();
        }

        private static string FormatSignedNumber(double value)
        {
            return value >= 0 ? $"+ {value:F6}" : $"- {Math.Abs(value):F6}";
        }

        private CircuitGraph CloneWithoutBranch(CircuitBranch excluded)
        {
            var g = CloneNodes(out var nodeMap);
            foreach (var branch in _graph.Branches)
            {
                if (branch.Id == excluded.Id) continue;
                CopyBranch(g, branch, nodeMap);
            }
            return g;
        }

        private CircuitGraph CloneDeactivatedSources()
        {
            var g = CloneNodes(out var nodeMap);

            foreach (var branch in _graph.Branches)
            {
                if (branch.Id == _loadBranch.Id) continue;

                // Идеальный источник тока при деактивации = разрыв.
                if (branch.HasCurrentSource) continue;

                var copy = g.AddBranch(nodeMap[branch.StartNode.Id], nodeMap[branch.EndNode.Id]);
                foreach (var element in branch.Elements)
                {
                    int direction = branch.GetElementDirection(element);
                    if (element.Type == ElementType.VoltageSource)
                    {
                        copy.AddElement(new CircuitElement
                        {
                            Name = $"r_{element.Name}",
                            Type = ElementType.Resistor,
                            Value = Math.Max(0.0, element.InternalResistance)
                        }, direction);
                    }
                    else
                    {
                        copy.AddElement(element, direction);
                    }
                }
            }

            return g;
        }

        private CircuitGraph CloneNodes(out Dictionary<Guid, CircuitNode> nodeMap)
        {
            var g = new CircuitGraph();
            nodeMap = new Dictionary<Guid, CircuitNode>();
            foreach (var node in _graph.Nodes)
                nodeMap[node.Id] = g.AddNode(node.Label, node.Position);
            return g;
        }

        private static void CopyBranch(
            CircuitGraph target,
            CircuitBranch source,
            IReadOnlyDictionary<Guid, CircuitNode> nodeMap)
        {
            var copy = target.AddBranch(nodeMap[source.StartNode.Id], nodeMap[source.EndNode.Id]);
            foreach (var element in source.Elements)
                copy.AddElement(element, source.GetElementDirection(element));
        }

        private static double ComputeInputResistance(CircuitGraph graph, CircuitNode a, CircuitNode b)
        {
            // Тестовый ток 1 А работает и при Rвх=0.
            var test = graph.AddBranch(a, b);
            test.AddElement(new CircuitElement
            {
                Name = "J_test",
                Type = ElementType.CurrentSource,
                Value = 1.0,
                IsPositiveAtStart = true,
                InternalResistance = 0.0
            });

            var solved = new NodePotentialSolver(graph).Solve();
            double req;
            if (!solved.Success)
            {
                req = double.NaN;
            }
            else
            {
                var na = graph.Nodes.First(n => n.Id == a.Id);
                var nb = graph.Nodes.First(n => n.Id == b.Id);
                req = Math.Abs(na.Potential - nb.Potential); // U / 1 A
            }

            graph.RemoveBranch(test);
            return req;
        }

        private static CalculationResult Fail(CalculationResult result, string message)
        {
            result.Success = false;
            result.ErrorMessage = message;
            return result;
        }

        private sealed record PathEdge(CircuitBranch Branch, int Direction);
        private sealed record SignedSymbol(int Sign, string Text);
    }
}
