using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>Комплексный МКТ для синусоидального установившегося режима.</summary>
    internal sealed class PhasorMeshCurrentSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitAnalysisSettings _settings;

        public PhasorMeshCurrentSolver(CircuitGraph graph, CircuitAnalysisSettings settings)
        {
            _graph = graph;
            _settings = settings.Clone();
        }

        public CalculationResult Solve()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.MeshCurrents,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = false
            };

            try
            {
                _settings.Validate();
                if (_settings.Mode != CircuitAnalysisMode.AC)
                    throw new InvalidOperationException("PhasorMeshCurrentSolver предназначен для AC-режима.");

                var models = _graph.Branches.ToDictionary(b => b, b => SteadyStateBranchModel.Create(b, _settings));
                var active = _graph.Branches.Where(b => models[b].Kind != SteadyStateBranchKind.Open).ToList();
                EnsureConnected(_graph.Nodes, active);
                var loops = FindIndependentLoops(_graph.Nodes, active);
                if (loops.Count == 0)
                    return Fail(result, "В схеме нет замкнутого контура. Для МКТ необходима замкнутая цепь.");

                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Выбор независимых контуров",
                    Description = $"AC, f={_settings.FrequencyHz:0.######} Гц, ω={_settings.AngularFrequency:0.######} рад/с. Строим фундаментальные контуры графа.",
                    MatrixText = FormatLoops(loops, active.Count, _graph.Nodes.Count)
                });

                var currentSources = active.Where(b => models[b].Kind == SteadyStateBranchKind.IdealCurrent).ToList();
                foreach (var source in currentSources)
                    if (!loops.Any(loop => loop.Any(x => x.Branch == source)))
                        throw new InvalidOperationException($"Источник тока в ветви {source} не входит ни в один замкнутый контур.");

                int m = loops.Count, s = currentSources.Count, nEq = m + s;
                var a = new Complex[nEq, nEq];
                var rhs = new Complex[nEq];
                var sourceIndex = currentSources.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);

                for (int i = 0; i < m; i++)
                {
                    foreach (var (branch, dirI) in loops[i])
                    {
                        var model = models[branch];
                        if (model.Kind == SteadyStateBranchKind.IdealCurrent)
                        {
                            a[i, m + sourceIndex[branch]] += dirI;
                            continue;
                        }

                        rhs[i] -= dirI * model.Emf;
                        if (model.Impedance.Magnitude <= 1e-15) continue;
                        for (int j = 0; j < m; j++)
                        {
                            var other = loops[j].FirstOrDefault(x => x.Branch == branch);
                            if (other.Branch != null)
                                a[i, j] += model.Impedance * dirI * other.Dir;
                        }
                    }
                }

                for (int src = 0; src < s; src++)
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

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Система уравнений МКТ — общий вид",
                    Description = "В AC-режиме сопротивления заменяются комплексными Z. Для каждого контура Σ(dir·Ũ)=0, где Ũ=Z̲·Ī+Ė.",
                    MatrixText = BuildGeneralEquations(loops, currentSources, models)
                });

                var names = Enumerable.Range(1, m).Select(i => $"Iк{i}")
                    .Concat(currentSources.Select((_, i) => $"U_J{i + 1}"))
                    .ToArray();
                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Подстановка значений в систему МКТ",
                    Description = "Комплексные сопротивления и фазоры источников:",
                    MatrixText = BuildImpedanceTable(active, models) + "\n" + FormatNumericEquations(a, rhs, names)
                });

                Complex[] x = ComplexLinearSystemSolver.Solve(a, rhs, names);
                Complex[] mesh = x.Take(m).ToArray();
                var sourceVoltages = currentSources.Select((b, i) => (b, u: x[m + i])).ToDictionary(x => x.b, x => x.u);

                result.Steps.Add(new SolutionStep
                {
                    Title = "4. Контурные токи",
                    Description = "Итог решения комплексной системы:",
                    MatrixText = string.Join("\n", mesh.Select((v, i) => $"  Iк{i + 1} = {Phasor.Rectangular(v)} А = {Phasor.Polar(v)} А"))
                });

                var branchCurrent = new Dictionary<CircuitBranch, Complex>();
                var branchVoltage = new Dictionary<CircuitBranch, Complex>();
                var text = new StringBuilder();
                foreach (var branch in active)
                {
                    Complex current = Complex.Zero;
                    for (int k = 0; k < loops.Count; k++)
                    {
                        var item = loops[k].FirstOrDefault(xi => xi.Branch == branch);
                        if (item.Branch != null) current += item.Dir * mesh[k];
                    }
                    Complex voltage = models[branch].Kind == SteadyStateBranchKind.IdealCurrent
                        ? sourceVoltages[branch]
                        : models[branch].VoltageFromCurrent(current);
                    branch.CurrentPhasor = current;
                    branchCurrent[branch] = current;
                    branchVoltage[branch] = voltage;
                    text.AppendLine($"  {branch}: I={Phasor.Rectangular(current)} А = {Phasor.Polar(current)} А; U={Phasor.Rectangular(voltage)} В = {Phasor.Polar(voltage)} В.");
                }

                var potentials = ReconstructNodePotentials(_graph.Nodes, active, branchVoltage);
                foreach (var branch in _graph.Branches.Where(b => models[b].Kind == SteadyStateBranchKind.Open))
                {
                    branchCurrent[branch] = Complex.Zero;
                    branchVoltage[branch] = potentials[branch.StartNode] - potentials[branch.EndNode];
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "5. Токи и напряжения ветвей",
                    Description = "Физические токи ветвей и напряжения в RMS-фазорной форме:",
                    MatrixText = text.ToString()
                });

                foreach (var branch in _graph.Branches)
                    result.BranchResults.Add(new BranchResult
                    {
                        Branch = branch,
                        CurrentPhasor = branchCurrent[branch],
                        VoltagePhasor = branchVoltage[branch]
                    });

                ComplexPowerCalculator.AddPowerBalance(result, _settings,
                    "6. Баланс комплексных мощностей — общий вид",
                    "7. Мощности P/Q/S и проверка баланса");

                InstantaneousWaveformFormatter.AddStep(result, _settings,
                    "8. Мгновенные функции токов и напряжений");

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
            IReadOnlyList<CircuitNode> nodes, IReadOnlyList<CircuitBranch> branches)
        {
            var adjacency = nodes.ToDictionary(n => n, _ => new List<CircuitBranch>());
            foreach (var b in branches) { adjacency[b.StartNode].Add(b); adjacency[b.EndNode].Add(b); }
            var visited = new HashSet<CircuitNode>();
            var tree = new HashSet<CircuitBranch>();
            var treeAdj = nodes.ToDictionary(n => n, _ => new List<CircuitBranch>());
            void Dfs(CircuitNode n)
            {
                visited.Add(n);
                foreach (var b in adjacency[n])
                {
                    var o = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (visited.Contains(o)) continue;
                    tree.Add(b); treeAdj[n].Add(b); treeAdj[o].Add(b); Dfs(o);
                }
            }
            Dfs(nodes[0]);
            var loops = new List<List<(CircuitBranch Branch, int Dir)>>();
            foreach (var chord in branches.Where(b => !tree.Contains(b)))
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
            var prev = new Dictionary<CircuitNode, (CircuitNode Node, CircuitBranch Branch)>();
            var seen = new HashSet<CircuitNode> { from };
            var q = new Queue<CircuitNode>(); q.Enqueue(from);
            while (q.Count > 0 && !seen.Contains(to))
            {
                var n = q.Dequeue();
                foreach (var b in treeAdj[n])
                {
                    var o = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (!seen.Add(o)) continue;
                    prev[o] = (n, b); q.Enqueue(o);
                }
            }
            if (!seen.Contains(to)) throw new InvalidOperationException("Не удалось построить контур по остовному дереву.");
            var reverse = new List<(CircuitBranch Branch, int Dir)>();
            for (var cur = to; cur != from;)
            {
                var p = prev[cur];
                int dir = p.Branch.StartNode == p.Node ? +1 : -1;
                reverse.Add((p.Branch, dir)); cur = p.Node;
            }
            reverse.Reverse(); return reverse;
        }

        private static string FormatLoops(List<List<(CircuitBranch Branch, int Dir)>> loops, int b, int n)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Узлов N={n}; активных ветвей B={b}; независимых контуров m={loops.Count}.");
            for (int i = 0; i < loops.Count; i++)
                sb.AppendLine($"  Контур {i + 1}: {string.Join("; ", loops[i].Select(x => $"{(x.Dir > 0 ? "+" : "−")}{x.Branch}"))}");
            return sb.ToString();
        }

        private static string BuildGeneralEquations(
            List<List<(CircuitBranch Branch, int Dir)>> loops,
            List<CircuitBranch> currentSources,
            Dictionary<CircuitBranch, SteadyStateBranchModel> models)
        {
            var sb = new StringBuilder();
            var srcIndex = currentSources.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
            for (int i = 0; i < loops.Count; i++)
            {
                var terms = new List<string>();
                foreach (var (branch, dir) in loops[i])
                {
                    var m = models[branch];
                    if (m.Kind == SteadyStateBranchKind.IdealCurrent)
                    {
                        terms.Add($"{(dir > 0 ? "+" : "−")}U_J{srcIndex[branch] + 1}");
                        continue;
                    }
                    var currents = new List<string>();
                    for (int k = 0; k < loops.Count; k++)
                    {
                        var item = loops[k].FirstOrDefault(x => x.Branch == branch);
                        if (item.Branch != null) currents.Add($"{(item.Dir > 0 ? "+" : "−")}Iк{k + 1}");
                    }
                    string ib = string.Join(" ", currents).TrimStart('+');
                    if (m.Impedance.Magnitude > 1e-15)
                        terms.Add($"{(dir > 0 ? "+" : "−")}({m.SymbolicImpedance()})·({ib})");
                    foreach (var e in branch.Elements.Where(e => e.Type == ElementType.VoltageSource))
                    {
                        int sign = dir * branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
                        terms.Add($"{(sign > 0 ? "+" : "−")}{e.Name}");
                    }
                }
                sb.AppendLine($"  Контур {i + 1}: {string.Join(" ", terms).TrimStart('+')} = 0");
            }
            for (int src = 0; src < currentSources.Count; src++)
            {
                var b = currentSources[src];
                var terms = new List<string>();
                for (int k = 0; k < loops.Count; k++)
                {
                    var item = loops[k].FirstOrDefault(x => x.Branch == b);
                    if (item.Branch != null) terms.Add($"{(item.Dir > 0 ? "+" : "−")}Iк{k + 1}");
                }
                var e = b.Elements.First(x => x.Type == ElementType.CurrentSource);
                int sign = b.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
                sb.AppendLine($"  Источник {e.Name}: {string.Join(" ", terms).TrimStart('+')} = {(sign > 0 ? "" : "−")}{e.Name}");
            }
            return sb.ToString();
        }

        private static string BuildImpedanceTable(IEnumerable<CircuitBranch> branches, Dictionary<CircuitBranch, SteadyStateBranchModel> models)
        {
            var sb = new StringBuilder();
            foreach (var b in branches)
            {
                var m = models[b];
                if (m.Impedance.Magnitude > 1e-15)
                    sb.AppendLine($"  {b}: Z={m.SymbolicImpedance()}={Phasor.Rectangular(m.Impedance)} Ом");
                if (!Phasor.NearlyZero(m.Emf))
                    sb.AppendLine($"           E={Phasor.Rectangular(m.Emf)} В={Phasor.Polar(m.Emf)} В");
            }
            return sb.ToString();
        }

        private static string FormatNumericEquations(Complex[,] a, Complex[] rhs, string[] names)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < rhs.Length; r++)
            {
                var terms = new List<string>();
                for (int c = 0; c < names.Length; c++)
                    if (a[r, c].Magnitude > 1e-14)
                        terms.Add($"({Phasor.Rectangular(a[r, c])})·{names[c]}");
                sb.AppendLine($"  {(terms.Count == 0 ? "0" : string.Join(" + ", terms))} = {Phasor.Rectangular(rhs[r])}");
            }
            return sb.ToString();
        }

        private static Dictionary<CircuitNode, Complex> ReconstructNodePotentials(
            IReadOnlyList<CircuitNode> nodes, IReadOnlyList<CircuitBranch> branches,
            IReadOnlyDictionary<CircuitBranch, Complex> voltages)
        {
            var p = new Dictionary<CircuitNode, Complex> { [nodes[0]] = Complex.Zero };
            var q = new Queue<CircuitNode>(); q.Enqueue(nodes[0]);
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                foreach (var b in branches.Where(b => b.StartNode == n || b.EndNode == n))
                {
                    var o = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (p.ContainsKey(o)) continue;
                    Complex u = voltages[b];
                    p[o] = b.StartNode == n ? p[n] - u : p[n] + u;
                    q.Enqueue(o);
                }
            }
            foreach (var n in nodes)
            {
                if (!p.TryGetValue(n, out var v)) throw new InvalidOperationException("Не удалось восстановить потенциалы AC-схемы.");
                n.PotentialPhasor = v;
            }
            return p;
        }

        private static void EnsureConnected(IReadOnlyList<CircuitNode> nodes, IReadOnlyList<CircuitBranch> branches)
        {
            if (nodes.Count == 0) return;
            var seen = new HashSet<CircuitNode> { nodes[0] };
            var q = new Queue<CircuitNode>(); q.Enqueue(nodes[0]);
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                foreach (var b in branches.Where(b => b.StartNode == n || b.EndNode == n))
                {
                    var o = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (seen.Add(o)) q.Enqueue(o);
                }
            }
            if (seen.Count != nodes.Count) throw new InvalidOperationException("AC-схема несвязна. Проверьте соединения.");
        }

        private static CalculationResult Fail(CalculationResult r, string message)
        {
            r.Success = false; r.ErrorMessage = message; return r;
        }
    }
}
