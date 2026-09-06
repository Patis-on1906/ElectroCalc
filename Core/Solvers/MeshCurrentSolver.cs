using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Метод контурных токов на базе фундаментальных контуров графа.
    /// Для идеальных источников тока автоматически добавляются уравнения
    /// заданного тока и неизвестные напряжения источников (обобщённый supermesh).
    /// </summary>
    public class MeshCurrentSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitAnalysisSettings _settings;

        public MeshCurrentSolver(CircuitGraph graph, CircuitAnalysisSettings? settings = null)
        {
            _graph = graph;
            _settings = (settings ?? new CircuitAnalysisSettings()).Clone();
        }

        public CalculationResult Solve()
        {
            if (_settings.Mode == CircuitAnalysisMode.AC)
                return new PhasorMeshCurrentSolver(_graph, _settings).Solve();

            var result = new CalculationResult { Method = CalculationMethod.MeshCurrents, Analysis = _settings.Clone() };

            try
            {
                if (_graph.Nodes.Count < 2 || _graph.Branches.Count == 0)
                    return Fail(result, "Схема не содержит достаточного количества узлов/ветвей для расчёта.");

                var models = _graph.Branches.ToDictionary(b => b, DcBranchModel.Create);
                var activeBranches = _graph.Branches
                    .Where(b => models[b].Kind != DcBranchKind.Open)
                    .ToList();

                EnsureConnected(_graph.Nodes, activeBranches);
                var loops = FindIndependentLoops(_graph.Nodes, activeBranches, result.Steps);
                int m = loops.Count;
                if (m == 0)
                    return Fail(result, "В схеме нет замкнутого контура. Для МКТ необходима замкнутая цепь.");

                var currentSources = activeBranches
                    .Where(b => models[b].Kind == DcBranchKind.IdealCurrent)
                    .ToList();

                foreach (var source in currentSources)
                {
                    if (!loops.Any(loop => loop.Any(x => x.Branch == source)))
                        throw new InvalidOperationException(
                            $"Источник тока в ветви {source} не входит ни в один замкнутый контур. " +
                            "Проверьте замкнутость схемы.");
                }

                int s = currentSources.Count;
                int nEq = m + s;
                double[,] A = new double[nEq, nEq];
                double[] rhs = new double[nEq];

                var generalEquationText = BuildGeneralEquations(loops, currentSources, models);
                var equationText = BuildEquations(loops, currentSources, models, A, rhs);

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Система уравнений МКТ — общий вид",
                    Description = "Сначала записываем уравнения символически, без подстановки числовых номиналов. " +
                                  "Для каждой ветви Iветви выражается через контурные токи.",
                    MatrixText = generalEquationText
                });

                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Подстановка значений в систему МКТ",
                    Description = "Подставляем сопротивления и ЭДС элементов в символическую систему:",
                    MatrixText = equationText
                });

                var names = Enumerable.Range(1, m).Select(i => $"Iк{i}").ToList();
                names.AddRange(currentSources.Select((b, i) => $"U_J{i + 1}"));
                var units = Enumerable.Repeat("А", m)
                    .Concat(Enumerable.Repeat("В", s))
                    .ToArray();

                // Метод Гаусса используется внутри, но промежуточные преобразования
                // не выводятся: после подстановки сразу показываем найденные токи.
                var hiddenSolveSteps = new List<SolutionStep>();
                double[] x = LinearSystemSolver.Solve(
                    A, rhs, names.ToArray(), hiddenSolveSteps, units);

                double[] meshCurrents = x.Take(m).ToArray();
                var sourceVoltages = currentSources
                    .Select((b, i) => (b, voltage: x[m + i]))
                    .ToDictionary(x => x.b, x => x.voltage);

                result.Steps.Add(new SolutionStep
                {
                    Title = "4. Контурные токи",
                    Description = "Решение системы для независимых контуров:",
                    MatrixText = string.Join("\n",
                        meshCurrents.Select((v, i) => $"  Iк{i + 1} = {v:F6} А"))
                });

                var branchVoltage = new Dictionary<CircuitBranch, double>();
                var branchCurrent = new Dictionary<CircuitBranch, double>();
                var branchText = new StringBuilder();
                branchText.AppendLine("Iветви = Σ(dir · Iк), где dir=+1 при совпадении направлений.");
                branchText.AppendLine();

                foreach (var branch in activeBranches)
                {
                    double current = 0.0;
                    var terms = new List<string>();
                    for (int k = 0; k < loops.Count; k++)
                    {
                        var item = loops[k].FirstOrDefault(xi => xi.Branch == branch);
                        if (item.Branch == null) continue;
                        current += item.Dir * meshCurrents[k];
                        terms.Add($"{(item.Dir > 0 ? "+" : "−")}Iк{k + 1}");
                    }

                    double voltage = models[branch].Kind == DcBranchKind.IdealCurrent
                        ? sourceVoltages[branch]
                        : models[branch].VoltageFromCurrent(current);

                    branch.Current = current;
                    branchCurrent[branch] = current;
                    branchVoltage[branch] = voltage;
                    branchText.AppendLine(
                        $"  {branch}: I={string.Join(" ", terms)} = {current:F6} А; U={voltage:F6} В.");
                }

                var potentials = ReconstructNodePotentials(
                    _graph.Nodes, activeBranches, branchVoltage);

                foreach (var branch in _graph.Branches.Where(b => models[b].Kind == DcBranchKind.Open))
                {
                    branch.Current = 0.0;
                    branchCurrent[branch] = 0.0;
                    branchVoltage[branch] = potentials[branch.StartNode] - potentials[branch.EndNode];
                    branchText.AppendLine(
                        $"  {branch}: C в установившемся DC → I=0 А; U={branchVoltage[branch]:F6} В.");
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "5. Токи и напряжения ветвей",
                    Description = "Переходим от контурных токов к физическим токам ветвей:",
                    MatrixText = branchText.ToString()
                });

                foreach (var branch in _graph.Branches)
                {
                    var power = DcBranchModel.ComputePower(
                        models[branch], branchCurrent[branch], branchVoltage[branch]);
                    result.BranchResults.Add(new BranchResult
                    {
                        Branch = branch,
                        Current = branchCurrent[branch],
                        Voltage = branchVoltage[branch],
                        PassiveVoltagePhasor = branchCurrent[branch] * models[branch].Resistance,
                        PowerConsumed = power.consumed,
                        PowerGenerated = power.generated
                    });
                }

                PowerBalanceFormatter.AddPowerBalance(result,
                    "6. Баланс мощностей — общий вид",
                    "7. Баланс мощностей — подстановка и проверка");
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static List<List<(CircuitBranch Branch, int Dir)>> FindIndependentLoops(
            IReadOnlyList<CircuitNode> nodes,
            IReadOnlyList<CircuitBranch> branches,
            List<SolutionStep> steps)
        {
            var adjacency = nodes.ToDictionary(n => n, _ => new List<CircuitBranch>());
            foreach (var branch in branches)
            {
                adjacency[branch.StartNode].Add(branch);
                adjacency[branch.EndNode].Add(branch);
            }

            var visited = new HashSet<CircuitNode>();
            var treeEdges = new HashSet<CircuitBranch>();
            var treeAdj = nodes.ToDictionary(n => n, _ => new List<CircuitBranch>());

            void Dfs(CircuitNode node)
            {
                visited.Add(node);
                foreach (var branch in adjacency[node])
                {
                    var other = branch.StartNode == node ? branch.EndNode : branch.StartNode;
                    if (visited.Contains(other)) continue;
                    treeEdges.Add(branch);
                    treeAdj[node].Add(branch);
                    treeAdj[other].Add(branch);
                    Dfs(other);
                }
            }

            Dfs(nodes[0]);
            var chords = branches.Where(b => !treeEdges.Contains(b)).ToList();
            var loops = new List<List<(CircuitBranch Branch, int Dir)>>();

            foreach (var chord in chords)
            {
                var loop = new List<(CircuitBranch Branch, int Dir)> { (chord, +1) };
                loop.AddRange(FindTreePath(chord.EndNode, chord.StartNode, treeAdj));
                loops.Add(loop);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"  Узлов N = {nodes.Count}");
            sb.AppendLine($"  Активных ветвей B = {branches.Count}");
            sb.AppendLine($"  Независимых контуров m = B − N + 1 = {branches.Count - nodes.Count + 1}");
            sb.AppendLine();
            for (int i = 0; i < loops.Count; i++)
            {
                sb.Append($"  Контур {i + 1}: ");
                sb.AppendLine(string.Join("; ", loops[i].Select(x =>
                    $"{(x.Dir > 0 ? "+" : "−")}{x.Branch}")));
            }

            steps.Add(new SolutionStep
            {
                Title = "1. Выбор независимых контуров",
                Description = "Строим остовное дерево. Каждая хорда задаёт один фундаментальный контур.",
                MatrixText = sb.ToString()
            });

            return loops;
        }

        private static List<(CircuitBranch Branch, int Dir)> FindTreePath(
            CircuitNode from,
            CircuitNode to,
            Dictionary<CircuitNode, List<CircuitBranch>> treeAdj)
        {
            var queue = new Queue<CircuitNode>();
            var previous = new Dictionary<CircuitNode, (CircuitNode Node, CircuitBranch Branch)>();
            var visited = new HashSet<CircuitNode> { from };
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node == to) break;
                foreach (var branch in treeAdj[node])
                {
                    var other = branch.StartNode == node ? branch.EndNode : branch.StartNode;
                    if (!visited.Add(other)) continue;
                    previous[other] = (node, branch);
                    queue.Enqueue(other);
                }
            }

            if (!visited.Contains(to))
                throw new InvalidOperationException("Не удалось построить путь по остовному дереву.");

            var reverse = new List<(CircuitBranch Branch, int Dir)>();
            var cur = to;
            while (cur != from)
            {
                var prev = previous[cur];
                // Восстанавливаем путь cur <- prev.Node, а затем разворачиваем список.
                int dirFromPrevToCur = prev.Branch.StartNode == prev.Node ? +1 : -1;
                reverse.Add((prev.Branch, dirFromPrevToCur));
                cur = prev.Node;
            }
            reverse.Reverse();
            return reverse;
        }

        private static string BuildGeneralEquations(
            List<List<(CircuitBranch Branch, int Dir)>> loops,
            List<CircuitBranch> currentSources,
            Dictionary<CircuitBranch, DcBranchModel> models)
        {
            int m = loops.Count;
            var currentSourceIndex = currentSources
                .Select((b, i) => (b, i))
                .ToDictionary(x => x.b, x => x.i);
            var sb = new StringBuilder();
            sb.AppendLine("Соглашение: Uветви = R·I + E; для каждого контура Σ(dir·U)=0.");
            sb.AppendLine();

            for (int i = 0; i < m; i++)
            {
                var terms = new List<string>();
                foreach (var (branch, dirLoop) in loops[i])
                {
                    var model = models[branch];
                    if (model.Kind == DcBranchKind.IdealCurrent)
                    {
                        terms.Add($"{(dirLoop > 0 ? "+" : "−")}U_J{currentSourceIndex[branch] + 1}");
                        continue;
                    }

                    var branchCurrentTerms = new List<string>();
                    for (int j = 0; j < m; j++)
                    {
                        var item = loops[j].FirstOrDefault(x => x.Branch == branch);
                        if (item.Branch != null)
                            branchCurrentTerms.Add($"{(item.Dir > 0 ? "+" : "−")}Iк{j + 1}");
                    }
                    string branchCurrent = JoinSigned(branchCurrentTerms);

                    foreach (var element in branch.Elements)
                    {
                        string? resistanceSymbol = element.Type switch
                        {
                            ElementType.Resistor => element.Name,
                            ElementType.VoltageSource when element.InternalResistance > 1e-15 => $"r_{element.Name}",
                            ElementType.CurrentSource when element.InternalResistance > 1e-15 => $"r_{element.Name}",
                            _ => null
                        };
                        if (resistanceSymbol != null)
                            terms.Add($"{(dirLoop > 0 ? "+" : "−")}{resistanceSymbol}·({branchCurrent})");

                        if (element.Type == ElementType.VoltageSource)
                        {
                            int sign = dirLoop * branch.GetElementDirection(element) *
                                       (element.IsPositiveAtStart ? 1 : -1);
                            terms.Add($"{(sign > 0 ? "+" : "−")}{element.Name}");
                        }
                    }
                }

                sb.AppendLine($"  Контур {i + 1}: {JoinSigned(terms)} = 0");
            }

            for (int src = 0; src < currentSources.Count; src++)
            {
                var branch = currentSources[src];
                var terms = new List<string>();
                for (int j = 0; j < m; j++)
                {
                    var item = loops[j].FirstOrDefault(x => x.Branch == branch);
                    if (item.Branch != null)
                        terms.Add($"{(item.Dir > 0 ? "+" : "−")}Iк{j + 1}");
                }
                var source = branch.Elements.First(e => e.Type == ElementType.CurrentSource);
                int sign = branch.GetElementDirection(source) * (source.IsPositiveAtStart ? 1 : -1);
                sb.AppendLine($"  Источник {source.Name}: {JoinSigned(terms)} = {(sign > 0 ? "" : "−")}{source.Name}");
            }

            return sb.ToString();
        }

        private static string JoinSigned(IEnumerable<string> terms)
        {
            string text = string.Join(" ", terms);
            if (string.IsNullOrWhiteSpace(text)) return "0";
            return text.StartsWith("+", StringComparison.Ordinal) ? text[1..] : text;
        }

        private static string BuildEquations(
            List<List<(CircuitBranch Branch, int Dir)>> loops,
            List<CircuitBranch> currentSources,
            Dictionary<CircuitBranch, DcBranchModel> models,
            double[,] a,
            double[] rhs)
        {
            int m = loops.Count;
            var currentSourceIndex = currentSources
                .Select((b, i) => (b, i))
                .ToDictionary(x => x.b, x => x.i);
            var sb = new StringBuilder();
            sb.AppendLine("Соглашение: Uветви = R·I + E; уравнение контура Σ(dir·U)=0.");
            sb.AppendLine();

            for (int i = 0; i < m; i++)
            {
                foreach (var (branch, dirI) in loops[i])
                {
                    var model = models[branch];
                    if (model.Kind == DcBranchKind.IdealCurrent)
                    {
                        a[i, m + currentSourceIndex[branch]] += dirI;
                        continue;
                    }

                    rhs[i] -= dirI * model.Emf;
                    if (model.Resistance <= 1e-15) continue;

                    for (int j = 0; j < m; j++)
                    {
                        var inOther = loops[j].FirstOrDefault(x => x.Branch == branch);
                        if (inOther.Branch != null)
                            a[i, j] += model.Resistance * dirI * inOther.Dir;
                    }
                }
            }

            for (int src = 0; src < currentSources.Count; src++)
            {
                var branch = currentSources[src];
                int row = m + src;
                for (int j = 0; j < m; j++)
                {
                    var item = loops[j].FirstOrDefault(x => x.Branch == branch);
                    if (item.Branch != null) a[row, j] = item.Dir;
                }
                rhs[row] = models[branch].PrescribedCurrent;
            }

            for (int i = 0; i < m; i++)
            {
                sb.Append($"  Контур {i + 1}: ");
                for (int j = 0; j < m; j++)
                    sb.Append($"{a[i, j]:+0.####;-0.####;+0}·Iк{j + 1} ");
                for (int src = 0; src < currentSources.Count; src++)
                    if (Math.Abs(a[i, m + src]) > 1e-15)
                        sb.Append($"{a[i, m + src]:+0.####;-0.####;+0}·U_J{src + 1} ");
                sb.AppendLine($"= {rhs[i]:0.######}");
            }

            for (int src = 0; src < currentSources.Count; src++)
            {
                int row = m + src;
                sb.Append($"  Источник J{src + 1}: ");
                for (int j = 0; j < m; j++)
                    if (Math.Abs(a[row, j]) > 1e-15)
                        sb.Append($"{a[row, j]:+0.####;-0.####;+0}·Iк{j + 1} ");
                sb.AppendLine($"= {rhs[row]:0.######} А");
            }

            return sb.ToString();
        }

        private static Dictionary<CircuitNode, double> ReconstructNodePotentials(
            IReadOnlyList<CircuitNode> nodes,
            IReadOnlyList<CircuitBranch> branches,
            IReadOnlyDictionary<CircuitBranch, double> voltages)
        {
            var potentials = new Dictionary<CircuitNode, double> { [nodes[0]] = 0.0 };
            var queue = new Queue<CircuitNode>();
            queue.Enqueue(nodes[0]);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                foreach (var branch in branches.Where(b => b.StartNode == node || b.EndNode == node))
                {
                    var other = branch.StartNode == node ? branch.EndNode : branch.StartNode;
                    if (potentials.ContainsKey(other)) continue;

                    double u = voltages[branch]; // Vstart - Vend
                    potentials[other] = branch.StartNode == node
                        ? potentials[node] - u
                        : potentials[node] + u;
                    queue.Enqueue(other);
                }
            }

            foreach (var node in nodes)
            {
                if (!potentials.TryGetValue(node, out double p))
                    throw new InvalidOperationException("Не удалось восстановить потенциалы узлов: схема несвязна.");
                node.Potential = p;
            }

            return potentials;
        }

        private static void EnsureConnected(
            IReadOnlyList<CircuitNode> nodes,
            IReadOnlyList<CircuitBranch> branches)
        {
            var visited = new HashSet<CircuitNode> { nodes[0] };
            var queue = new Queue<CircuitNode>();
            queue.Enqueue(nodes[0]);

            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                foreach (var b in branches.Where(b => b.StartNode == n || b.EndNode == n))
                {
                    var other = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (visited.Add(other)) queue.Enqueue(other);
                }
            }

            if (visited.Count != nodes.Count)
                throw new InvalidOperationException(
                    "Схема не является одной замкнутой связной цепью (C в DC считается разрывом). " +
                    "Проверьте соединения.");
        }

        private static CalculationResult Fail(CalculationResult result, string message)
        {
            result.Success = false;
            result.ErrorMessage = message;
            return result;
        }
    }
}
