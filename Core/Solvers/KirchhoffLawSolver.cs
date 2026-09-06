using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Классическое составление системы по законам Кирхгофа в токах ветвей.
    /// Для DC выводятся вещественные сопротивления, для AC — комплексные импедансы.
    /// Режим предназначен именно для получения системы в общем виде: численное решение
    /// здесь намеренно не выполняется.
    /// </summary>
    public sealed class KirchhoffLawSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitAnalysisSettings _settings;

        public KirchhoffLawSolver(CircuitGraph graph, CircuitAnalysisSettings? settings = null)
        {
            _graph = graph;
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
                Method = CalculationMethod.KirchhoffLaws,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = false
            };
            try
            {
                if (_graph.Nodes.Count < 2 || _graph.Branches.Count == 0)
                    return Fail(result, "Схема не содержит достаточного количества узлов/ветвей для составления уравнений.");

                var models = _graph.Branches.ToDictionary(b => b, DcBranchModel.Create);
                var active = _graph.Branches.Where(b => models[b].Kind != DcBranchKind.Open).ToList();
                EnsureConnected(_graph.Nodes, active, "Расчётный граф несвязен. В режиме DC конденсатор считается разрывом.");

                var branchIndex = active.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
                var currentSources = active.Where(b => models[b].Kind == DcBranchKind.IdealCurrent).ToList();
                var sourceIndex = currentSources.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
                var loops = FindFundamentalLoops(_graph.Nodes, active);

                AddDirectionStep(result, active, false);
                AddKclStep(result, active, branchIndex, false);

                var kvl = new StringBuilder();
                kvl.AppendLine("Для каждого фундаментального контура записываем ΣU = 0, где Uветви = R·I + E:");
                for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
                {
                    var terms = new List<string>();
                    foreach (var (branch, dir) in loops[loopIndex])
                    {
                        int bi = branchIndex[branch];
                        var model = models[branch];
                        if (model.Kind == DcBranchKind.IdealCurrent)
                        {
                            terms.Add($"{(dir > 0 ? "+" : "−")}U_J{sourceIndex[branch] + 1}");
                            continue;
                        }

                        if (model.Resistance > 1e-15)
                            foreach (var symbol in ResistanceSymbols(branch))
                                terms.Add($"{(dir > 0 ? "+" : "−")}{symbol}·I{bi + 1}");

                        foreach (var emf in VoltageSourceTerms(branch, dir))
                            terms.Add(emf);
                    }
                    kvl.AppendLine($"  Контур {loopIndex + 1}: {JoinSigned(terms)} = 0");
                }

                for (int si = 0; si < currentSources.Count; si++)
                {
                    var branch = currentSources[si];
                    int bi = branchIndex[branch];
                    string jName = branch.Elements.FirstOrDefault(e => e.Type == ElementType.CurrentSource)?.Name ?? $"J{si + 1}";
                    kvl.AppendLine($"  Источник тока: I{bi + 1} = {CurrentSourceGeneral(branch, jName)}");
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Второй закон Кирхгофа — общий вид",
                    Description = "Контуры выбираются по остовному дереву; для идеального источника тока его ток задаётся отдельным уравнением.",
                    MatrixText = kvl.ToString()
                });

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        private CalculationResult SolveAc()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.KirchhoffLaws,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = false
            };

            try
            {
                if (_graph.Nodes.Count < 2 || _graph.Branches.Count == 0)
                    return Fail(result, "Схема не содержит достаточного количества узлов/ветвей для составления уравнений.");

                var models = _graph.Branches.ToDictionary(b => b, b => SteadyStateBranchModel.Create(b, _settings));
                var active = _graph.Branches.Where(b => models[b].Kind != SteadyStateBranchKind.Open).ToList();
                EnsureConnected(_graph.Nodes, active, "Расчётный граф AC-схемы несвязен. Проверьте проводники и соединения.");

                var branchIndex = active.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
                var currentSources = active.Where(b => models[b].Kind == SteadyStateBranchKind.IdealCurrent).ToList();
                var sourceIndex = currentSources.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
                var loops = FindFundamentalLoops(_graph.Nodes, active);

                AddDirectionStep(result, active, true);
                AddKclStep(result, active, branchIndex, true);

                var kvl = new StringBuilder();
                kvl.AppendLine($"f = {_settings.FrequencyHz:0.######} Гц; ω = 2πf = {_settings.AngularFrequency:0.######} рад/с.");
                kvl.AppendLine("Используем RMS-фазоры: ZR=R, ZL=jωL, ZC=1/(jωC). Для ветви U̲=Z̲·I̲+E̲ и для каждого контура ΣU̲=0.");
                kvl.AppendLine();

                for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
                {
                    var terms = new List<string>();
                    foreach (var (branch, dir) in loops[loopIndex])
                    {
                        int bi = branchIndex[branch];
                        var model = models[branch];
                        if (model.Kind == SteadyStateBranchKind.IdealCurrent)
                        {
                            terms.Add($"{(dir > 0 ? "+" : "−")}U̲_J{sourceIndex[branch] + 1}");
                            continue;
                        }

                        if (model.Impedance.Magnitude > 1e-15)
                            terms.Add($"{(dir > 0 ? "+" : "−")}({model.SymbolicImpedance()})·I̲{bi + 1}");

                        foreach (var emf in VoltageSourceTerms(branch, dir, phasor: true))
                            terms.Add(emf);
                    }
                    kvl.AppendLine($"  Контур {loopIndex + 1}: {JoinSigned(terms)} = 0");
                }

                for (int si = 0; si < currentSources.Count; si++)
                {
                    var branch = currentSources[si];
                    int bi = branchIndex[branch];
                    string jName = branch.Elements.FirstOrDefault(e => e.Type == ElementType.CurrentSource)?.Name ?? $"J{si + 1}";
                    kvl.AppendLine($"  Источник тока: I̲{bi + 1} = {CurrentSourceGeneral(branch, jName, phasor: true)}");
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Второй закон Кирхгофа — общий вид",
                    Description = "Законы Кирхгофа сохраняют форму для синусоидального установившегося режима, но токи, напряжения, ЭДС и коэффициенты являются комплексными RMS-фазорами.",
                    MatrixText = kvl.ToString()
                });

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        private void AddDirectionStep(CalculationResult result, IReadOnlyList<CircuitBranch> active, bool ac)
        {
            var text = new StringBuilder();
            text.AppendLine(ac
                ? "Положительные направления фазоров токов ветвей принимаем Start→End:"
                : "Положительные направления токов ветвей принимаем Start→End:");
            for (int i = 0; i < active.Count; i++)
                text.AppendLine($"  {(ac ? "I̲" : "I")}{i + 1}: {active[i]}");

            result.Steps.Add(new SolutionStep
            {
                Title = "1. Выбор направлений токов ветвей",
                Description = ac
                    ? "Направления задаются произвольно. Комплексный ток с полученной фазой относится к выбранному направлению Start→End."
                    : "Направления задаются произвольно; отрицательный результат означает противоположное фактическое направление.",
                MatrixText = text.ToString()
            });
        }

        private void AddKclStep(
            CalculationResult result,
            IReadOnlyList<CircuitBranch> active,
            IReadOnlyDictionary<CircuitBranch, int> branchIndex,
            bool ac)
        {
            var kcl = new StringBuilder();
            kcl.AppendLine(ac
                ? "Для N−1 независимых узлов записываем ΣI̲ = 0 (фазоры токов, выходящие из узла, считаем положительными):"
                : "Для N−1 независимых узлов записываем ΣI = 0 (токи, выходящие из узла, считаем положительными):");

            foreach (var node in _graph.Nodes.Skip(1))
            {
                var terms = new List<string>();
                foreach (var branch in active)
                {
                    int sign = branch.StartNode == node ? +1 : branch.EndNode == node ? -1 : 0;
                    if (sign == 0) continue;
                    int col = branchIndex[branch];
                    terms.Add($"{(sign > 0 ? "+" : "−")}{(ac ? "I̲" : "I")}{col + 1}");
                }
                kcl.AppendLine($"  Узел {node.Label}: {JoinSigned(terms)} = 0");
            }

            result.Steps.Add(new SolutionStep
            {
                Title = "2. Первый закон Кирхгофа — общий вид",
                Description = ac
                    ? "Первый закон Кирхгофа записывается для комплексных действующих значений токов точно так же, как для DC."
                    : "Составляем независимые уравнения токов для всех узлов, кроме одного базисного.",
                MatrixText = kcl.ToString()
            });
        }

        private static IEnumerable<string> ResistanceSymbols(CircuitBranch branch)
        {
            foreach (var e in branch.Elements)
            {
                if (e.Type == ElementType.Resistor) yield return e.Name;
                else if ((e.Type is ElementType.VoltageSource or ElementType.CurrentSource) && e.InternalResistance > 1e-15)
                    yield return $"r_{e.Name}";
            }
        }

        private static IEnumerable<string> VoltageSourceTerms(CircuitBranch branch, int loopDir, bool phasor = false)
        {
            foreach (var e in branch.Elements.Where(e => e.Type == ElementType.VoltageSource))
            {
                int sign = loopDir * branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
                yield return $"{(sign > 0 ? "+" : "−")}{(phasor ? "E̲_" : string.Empty)}{e.Name}";
            }
        }

        private static string CurrentSourceGeneral(CircuitBranch branch, string name, bool phasor = false)
        {
            var e = branch.Elements.First(x => x.Type == ElementType.CurrentSource);
            int sign = branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
            string symbol = phasor ? $"J̲_{name}" : name;
            return sign > 0 ? symbol : $"−{symbol}";
        }

        private static List<List<(CircuitBranch Branch, int Dir)>> FindFundamentalLoops(
            IReadOnlyList<CircuitNode> nodes, IReadOnlyList<CircuitBranch> branches)
        {
            var adjacency = nodes.ToDictionary(n => n, _ => new List<CircuitBranch>());
            foreach (var b in branches) { adjacency[b.StartNode].Add(b); adjacency[b.EndNode].Add(b); }
            var visited = new HashSet<CircuitNode>();
            var treeEdges = new HashSet<CircuitBranch>();
            var treeAdj = nodes.ToDictionary(n => n, _ => new List<CircuitBranch>());

            void Dfs(CircuitNode node)
            {
                visited.Add(node);
                foreach (var b in adjacency[node])
                {
                    var other = b.StartNode == node ? b.EndNode : b.StartNode;
                    if (visited.Contains(other)) continue;
                    treeEdges.Add(b); treeAdj[node].Add(b); treeAdj[other].Add(b); Dfs(other);
                }
            }
            Dfs(nodes[0]);

            var loops = new List<List<(CircuitBranch Branch, int Dir)>>();
            foreach (var chord in branches.Where(b => !treeEdges.Contains(b)))
            {
                var loop = new List<(CircuitBranch Branch, int Dir)> { (chord, +1) };
                loop.AddRange(FindTreePath(chord.EndNode, chord.StartNode, treeAdj));
                loops.Add(loop);
            }
            return loops;
        }

        private static List<(CircuitBranch Branch, int Dir)> FindTreePath(
            CircuitNode from, CircuitNode to, Dictionary<CircuitNode, List<CircuitBranch>> treeAdj)
        {
            var q = new Queue<CircuitNode>();
            var prev = new Dictionary<CircuitNode, (CircuitNode Node, CircuitBranch Branch)>();
            var visited = new HashSet<CircuitNode> { from };
            q.Enqueue(from);
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                if (n == to) break;
                foreach (var b in treeAdj[n])
                {
                    var other = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (!visited.Add(other)) continue;
                    prev[other] = (n, b); q.Enqueue(other);
                }
            }
            if (!visited.Contains(to)) throw new InvalidOperationException("Не удалось построить независимые контуры.");

            var reversed = new List<(CircuitBranch Branch, int Dir)>();
            var cur = to;
            while (cur != from)
            {
                var p = prev[cur];
                int dir = p.Branch.StartNode == p.Node && p.Branch.EndNode == cur ? +1 : -1;
                reversed.Add((p.Branch, dir));
                cur = p.Node;
            }
            reversed.Reverse();
            return reversed;
        }

        private static void EnsureConnected(
            IReadOnlyList<CircuitNode> nodes,
            IReadOnlyList<CircuitBranch> branches,
            string message)
        {
            var visited = new HashSet<CircuitNode> { nodes[0] };
            var q = new Queue<CircuitNode>(); q.Enqueue(nodes[0]);
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                foreach (var b in branches.Where(b => b.StartNode == n || b.EndNode == n))
                {
                    var other = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (visited.Add(other)) q.Enqueue(other);
                }
            }
            if (visited.Count != nodes.Count)
                throw new InvalidOperationException(message);
        }

        private static string JoinSigned(IEnumerable<string> terms)
        {
            string text = string.Join(" ", terms);
            if (string.IsNullOrWhiteSpace(text)) return "0";
            return text.StartsWith("+", StringComparison.Ordinal) ? text[1..] : text;
        }

        private static CalculationResult Fail(CalculationResult result, string message)
        {
            result.Success = false;
            result.ErrorMessage = message;
            return result;
        }
    }
}
