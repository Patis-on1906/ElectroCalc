using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Комплексный МУП для синусоидального установившегося режима.
    /// Все напряжения/токи — действующие комплексные значения (RMS-фазоры).
    /// </summary>
    internal sealed class PhasorNodePotentialSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitAnalysisSettings _settings;

        public PhasorNodePotentialSolver(CircuitGraph graph, CircuitAnalysisSettings settings)
        {
            _graph = graph;
            _settings = settings.Clone();
        }

        public CalculationResult Solve()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.NodePotentials,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = false
            };

            try
            {
                _settings.Validate();
                if (_settings.Mode != CircuitAnalysisMode.AC)
                    throw new InvalidOperationException("PhasorNodePotentialSolver предназначен для AC-режима.");
                if (_graph.Nodes.Count < 2 || _graph.Branches.Count == 0)
                    return Fail(result, "Схема должна содержать минимум два узла и одну ветвь.");

                var models = _graph.Branches.ToDictionary(b => b, b => SteadyStateBranchModel.Create(b, _settings));
                var active = _graph.Branches.Where(b => models[b].Kind != SteadyStateBranchKind.Open).ToList();
                EnsureConnected(_graph.Nodes, active);

                CircuitNode reference = SelectReferenceNode(_graph.Nodes, active, models, out string reason);
                var unknownNodes = _graph.Nodes.Where(n => n != reference).ToList();
                var nodeVar = unknownNodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
                var voltageBranches = active.Where(b => models[b].Kind == SteadyStateBranchKind.IdealVoltage).ToList();
                var voltageVar = voltageBranches.Select((b, i) => (b, i: unknownNodes.Count + i)).ToDictionary(x => x.b, x => x.i);

                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Выбор режима и базисного узла",
                    Description = $"Синусоидальный установившийся режим: f={_settings.FrequencyHz:0.######} Гц, ω={_settings.AngularFrequency:0.######} рад/с. Источники задаются действующими значениями (RMS) и фазами.",
                    MatrixText = $"{reason}\n  φ{reference.Label} = 0 В\n  Неизвестных узловых потенциалов: {unknownNodes.Count}."
                });

                int nEq = unknownNodes.Count + voltageBranches.Count;
                var a = new Complex[nEq, nEq];
                var rhs = new Complex[nEq];

                foreach (var branch in active)
                {
                    var model = models[branch];
                    int i = nodeVar.TryGetValue(branch.StartNode, out int si) ? si : -1;
                    int j = nodeVar.TryGetValue(branch.EndNode, out int sj) ? sj : -1;

                    switch (model.Kind)
                    {
                        case SteadyStateBranchKind.Impedance:
                        {
                            Complex y = Complex.One / model.Impedance;
                            if (i >= 0)
                            {
                                a[i, i] += y;
                                if (j >= 0) a[i, j] -= y;
                                rhs[i] += y * model.Emf;
                            }
                            if (j >= 0)
                            {
                                a[j, j] += y;
                                if (i >= 0) a[j, i] -= y;
                                rhs[j] -= y * model.Emf;
                            }
                            break;
                        }
                        case SteadyStateBranchKind.IdealCurrent:
                            if (i >= 0) rhs[i] -= model.PrescribedCurrent;
                            if (j >= 0) rhs[j] += model.PrescribedCurrent;
                            break;
                        case SteadyStateBranchKind.IdealVoltage:
                        {
                            int k = voltageVar[branch];
                            if (i >= 0) { a[i, k] += Complex.One; a[k, i] += Complex.One; }
                            if (j >= 0) { a[j, k] -= Complex.One; a[k, j] -= Complex.One; }
                            rhs[k] = model.Emf;
                            break;
                        }
                    }
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Система уравнений МУП — общий вид",
                    Description = "Используем комплексные проводимости Y=1/Z. Соглашение для ветви Start→End: Ũ = φ̲start − φ̲end = Z̲·Ī + Ė.",
                    MatrixText = BuildGeneralSystem(unknownNodes, reference, active, models, voltageBranches)
                });

                var names = unknownNodes.Select(n => $"φ{n.Label}").Concat(voltageBranches.Select((_, i) => $"I_E{i + 1}")).ToArray();
                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Система после подстановки значений",
                    Description = "Сначала рассчитываем комплексные сопротивления элементов, затем подставляем их в систему:",
                    MatrixText = BuildImpedanceTable(_graph.Branches, models) + "\n" + FormatNumericEquations(a, rhs, names)
                });

                Complex[] x = nEq == 0
                    ? Array.Empty<Complex>()
                    : ComplexLinearSystemSolver.Solve(a, rhs, names);

                reference.PotentialPhasor = Complex.Zero;
                foreach (var node in unknownNodes)
                    node.PotentialPhasor = x[nodeVar[node]];

                var potentialText = new StringBuilder();
                foreach (var node in _graph.Nodes)
                {
                    string note = node == reference ? " (базисный)" : string.Empty;
                    potentialText.AppendLine($"  φ{node.Label} = {Phasor.Rectangular(node.PotentialPhasor)} В = {Phasor.Polar(node.PotentialPhasor)} В{note}");
                }
                result.Steps.Add(new SolutionStep
                {
                    Title = "4. Узловые потенциалы",
                    Description = "Итог решения комплексной системы (промежуточные этапы исключены):",
                    MatrixText = potentialText.ToString()
                });

                var branchText = new StringBuilder();
                foreach (var branch in _graph.Branches)
                {
                    var model = models[branch];
                    Complex voltage = branch.StartNode.PotentialPhasor - branch.EndNode.PotentialPhasor;
                    Complex current = model.Kind switch
                    {
                        SteadyStateBranchKind.Open => Complex.Zero,
                        SteadyStateBranchKind.Impedance => model.CurrentFromVoltage(voltage),
                        SteadyStateBranchKind.IdealCurrent => model.PrescribedCurrent,
                        SteadyStateBranchKind.IdealVoltage => x[voltageVar[branch]],
                        _ => Complex.Zero
                    };

                    branch.CurrentPhasor = current;
                    result.BranchResults.Add(new BranchResult
                    {
                        Branch = branch,
                        CurrentPhasor = current,
                        VoltagePhasor = voltage
                    });
                    branchText.AppendLine($"  {branch}: I = {Phasor.Rectangular(current)} А = {Phasor.Polar(current)} А; U = {Phasor.Rectangular(voltage)} В = {Phasor.Polar(voltage)} В.");
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "5. Токи ветвей",
                    Description = "Токи и напряжения представлены как RMS-фазоры в прямоугольной и полярной формах:",
                    MatrixText = branchText.ToString()
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

        private static CircuitNode SelectReferenceNode(
            IReadOnlyList<CircuitNode> nodes,
            IReadOnlyList<CircuitBranch> branches,
            IReadOnlyDictionary<CircuitBranch, SteadyStateBranchModel> models,
            out string reason)
        {
            var score = new Dictionary<CircuitNode, (int ideal, int degree, int sources)>();
            foreach (var n in nodes)
            {
                var incident = branches.Where(b => b.StartNode == n || b.EndNode == n).ToList();
                score[n] = (
                    incident.Count(b => models[b].Kind == SteadyStateBranchKind.IdealVoltage && b.HasVoltageSource),
                    incident.Count,
                    incident.Sum(b => b.Elements.Count(e => e.Type is ElementType.VoltageSource or ElementType.CurrentSource)));
            }

            var selected = nodes
                .OrderByDescending(n => score[n].ideal > 0)
                .ThenByDescending(n => score[n].degree)
                .ThenByDescending(n => score[n].sources)
                .ThenBy(n => n.Label)
                .First();

            var s = score[selected];
            reason = s.ideal > 0
                ? $"Узел {selected.Label} выбран базисным: он подключён к идеальному источнику ЭДС; дополнительно степень узла={s.degree}, источников={s.sources}."
                : $"Идеальных источников ЭДС, дающих приоритет базису, нет. Выбран узел {selected.Label}: степень={s.degree}, подключённых источников={s.sources}.";
            return selected;
        }

        private static string BuildGeneralSystem(
            IReadOnlyList<CircuitNode> unknownNodes,
            CircuitNode reference,
            IReadOnlyList<CircuitBranch> branches,
            IReadOnlyDictionary<CircuitBranch, SteadyStateBranchModel> models,
            IReadOnlyList<CircuitBranch> voltageBranches)
        {
            var sb = new StringBuilder();
            var voltageIndex = voltageBranches.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
            foreach (var node in unknownNodes)
            {
                var terms = new List<string>();
                foreach (var b in branches.Where(b => b.StartNode == node || b.EndNode == node))
                {
                    var model = models[b];
                    bool start = b.StartNode == node;
                    CircuitNode other = start ? b.EndNode : b.StartNode;
                    if (model.Kind == SteadyStateBranchKind.Impedance)
                    {
                        string z = model.SymbolicImpedance();
                        string otherPhi = other == reference ? "0" : $"φ{other.Label}";
                        string e = SymbolicEmf(b);
                        terms.Add(start
                            ? $"(φ{node.Label} − {otherPhi} − ({e}))/({z})"
                            : $"(φ{node.Label} − {otherPhi} + ({e}))/({z})");
                    }
                    else if (model.Kind == SteadyStateBranchKind.IdealCurrent)
                    {
                        string j = SymbolicCurrent(b);
                        terms.Add(start ? j : $"−({j})");
                    }
                    else if (model.Kind == SteadyStateBranchKind.IdealVoltage && voltageIndex.TryGetValue(b, out int vi))
                        terms.Add(start ? $"I_E{vi + 1}" : $"−I_E{vi + 1}");
                }
                sb.AppendLine($"  Узел {node.Label}: {string.Join(" + ", terms)} = 0");
            }
            foreach (var b in voltageBranches)
            {
                string a = b.StartNode == reference ? "0" : $"φ{b.StartNode.Label}";
                string c = b.EndNode == reference ? "0" : $"φ{b.EndNode.Label}";
                sb.AppendLine($"  {a} − {c} = {SymbolicEmf(b)}");
            }
            return sb.ToString();
        }

        private static string BuildImpedanceTable(
            IEnumerable<CircuitBranch> branches,
            IReadOnlyDictionary<CircuitBranch, SteadyStateBranchModel> models)
        {
            var sb = new StringBuilder();
            foreach (var b in branches)
            {
                var m = models[b];
                if (m.Kind == SteadyStateBranchKind.Impedance)
                    sb.AppendLine($"  {b}: Z = {m.SymbolicImpedance()} = {Phasor.Rectangular(m.Impedance)} Ом");
                if (!Phasor.NearlyZero(m.Emf))
                    sb.AppendLine($"           E = {Phasor.Rectangular(m.Emf)} В = {Phasor.Polar(m.Emf)} В");
                if (m.Kind == SteadyStateBranchKind.IdealCurrent)
                    sb.AppendLine($"           J = {Phasor.Rectangular(m.PrescribedCurrent)} А = {Phasor.Polar(m.PrescribedCurrent)} А");
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

        private static string SymbolicEmf(CircuitBranch branch)
        {
            var terms = new List<string>();
            foreach (var e in branch.Elements.Where(e => e.Type == ElementType.VoltageSource))
            {
                int sign = branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
                terms.Add(sign > 0 ? e.Name : $"−{e.Name}");
            }
            return terms.Count == 0 ? "0" : string.Join(" + ", terms);
        }

        private static string SymbolicCurrent(CircuitBranch branch)
        {
            var e = branch.Elements.First(x => x.Type == ElementType.CurrentSource);
            int sign = branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
            return sign > 0 ? e.Name : $"−{e.Name}";
        }

        private static void EnsureConnected(IReadOnlyList<CircuitNode> nodes, IReadOnlyList<CircuitBranch> branches)
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
                throw new InvalidOperationException("Расчётный граф AC-схемы несвязен. Проверьте проводники и соединения.");
        }

        private static CalculationResult Fail(CalculationResult r, string message)
        {
            r.Success = false; r.ErrorMessage = message; return r;
        }
    }
}
