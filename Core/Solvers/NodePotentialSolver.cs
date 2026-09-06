using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Метод узловых потенциалов для установившегося режима DC.
    /// Базисный узел выбирается автоматически так, чтобы упростить систему:
    /// 1) узел идеального источника ЭДС; 2) максимальная степень узла;
    /// 3) максимальное число подключённых источников.
    /// Идеальные ветви напряжения, связанные с базисным узлом, используются для
    /// непосредственного определения соседних потенциалов и исключаются из числа неизвестных.
    /// </summary>
    public class NodePotentialSolver
    {
        private const double Eps = 1e-12;
        private readonly CircuitGraph _graph;
        private readonly CircuitAnalysisSettings _settings;

        public NodePotentialSolver(CircuitGraph graph, CircuitAnalysisSettings? settings = null)
        {
            _graph = graph;
            _settings = (settings ?? new CircuitAnalysisSettings()).Clone();
        }

        public CalculationResult Solve()
        {
            if (_settings.Mode == CircuitAnalysisMode.AC)
                return new PhasorNodePotentialSolver(_graph, _settings).Solve();

            var result = new CalculationResult { Method = CalculationMethod.NodePotentials, Analysis = _settings.Clone() };

            try
            {
                var nodes = _graph.Nodes;
                if (nodes.Count < 2)
                    return Fail(result, "Схема должна содержать минимум два расчётных узла.");
                if (_graph.Branches.Count == 0)
                    return Fail(result, "В схеме нет ветвей с элементами.");

                var models = _graph.Branches.ToDictionary(b => b, DcBranchModel.Create);
                var activeBranches = _graph.Branches
                    .Where(b => models[b].Kind != DcBranchKind.Open)
                    .ToList();

                EnsureConnected(nodes, activeBranches, models.Values.Any(m => m.Kind == DcBranchKind.Open));

                CircuitNode reference = SelectReferenceNode(nodes, activeBranches, models, out string referenceReason);

                // Потенциалы, которые можно получить сразу от выбранного базисного узла
                // через идеальные ветви напряжения (в том числе E без внутреннего R и КЗ/L).
                var fixedPotential = new Dictionary<CircuitNode, double> { [reference] = 0.0 };
                var fixedSymbol = new Dictionary<CircuitNode, string> { [reference] = "0" };
                var eliminatedVoltageBranches = new HashSet<CircuitBranch>();
                PropagateKnownPotentials(reference, activeBranches, models, fixedPotential, fixedSymbol, eliminatedVoltageBranches);

                var unknownNodes = nodes.Where(n => !fixedPotential.ContainsKey(n)).ToList();
                var nodeVar = unknownNodes
                    .Select((node, index) => (node, index))
                    .ToDictionary(x => x.node, x => x.index);

                // Идеальные источники напряжения, не связанные идеальной цепочкой с базисным узлом,
                // требуют стандартной дополнительной переменной MNA.
                var voltageBranches = activeBranches
                    .Where(b => models[b].Kind == DcBranchKind.IdealVoltage && !eliminatedVoltageBranches.Contains(b))
                    .ToList();

                if (voltageBranches.Any(b => fixedPotential.ContainsKey(b.StartNode) && fixedPotential.ContainsKey(b.EndNode)))
                    throw new InvalidOperationException(
                        "Обнаружен замкнутый контур, состоящий только из идеальных ветвей напряжения. " +
                        "Потенциалы в нём определимы, но токи отдельных идеальных источников без сопротивлений могут быть неопределимы.");

                var voltageVar = voltageBranches
                    .Select((branch, index) => (branch, index))
                    .ToDictionary(x => x.branch, x => unknownNodes.Count + x.index);

                int nPhi = unknownNodes.Count;
                int nEq = nPhi + voltageBranches.Count;

                var knownText = new StringBuilder();
                knownText.AppendLine($"Узел {reference.Label}: φ{reference.Label} = 0 В — базисный.");
                foreach (var node in nodes.Where(n => n != reference && fixedPotential.ContainsKey(n)))
                    knownText.AppendLine($"Узел {node.Label}: φ{node.Label} = {fixedSymbol[node]} = {fixedPotential[node]:0.######} В — известен из идеальной ветви напряжения.");
                knownText.AppendLine($"Неизвестных узловых потенциалов после упрощения: {nPhi}.");

                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Выбор базисного узла",
                    Description = referenceReason,
                    MatrixText = knownText.ToString()
                });

                double[,] a = new double[nEq, nEq];
                double[] rhs = new double[nEq];

                foreach (var branch in activeBranches)
                {
                    var model = models[branch];
                    int i = nodeVar.TryGetValue(branch.StartNode, out int si) ? si : -1;
                    int j = nodeVar.TryGetValue(branch.EndNode, out int sj) ? sj : -1;

                    switch (model.Kind)
                    {
                        case DcBranchKind.Resistive:
                        {
                            double g = 1.0 / model.Resistance;

                            if (i >= 0)
                            {
                                a[i, i] += g;
                                if (j >= 0) a[i, j] -= g;
                                else if (fixedPotential.TryGetValue(branch.EndNode, out double vEnd)) rhs[i] += g * vEnd;
                                rhs[i] += g * model.Emf;
                            }

                            if (j >= 0)
                            {
                                a[j, j] += g;
                                if (i >= 0) a[j, i] -= g;
                                else if (fixedPotential.TryGetValue(branch.StartNode, out double vStart)) rhs[j] += g * vStart;
                                rhs[j] -= g * model.Emf;
                            }
                            break;
                        }

                        case DcBranchKind.IdealVoltage:
                        {
                            if (fixedPotential.ContainsKey(branch.StartNode) && fixedPotential.ContainsKey(branch.EndNode))
                            {
                                double actual = fixedPotential[branch.StartNode] - fixedPotential[branch.EndNode];
                                if (Math.Abs(actual - model.Emf) > 1e-8)
                                    throw new InvalidOperationException($"Несовместимые идеальные источники напряжения в ветви {branch}.");
                                break;
                            }

                            int k = voltageVar[branch];
                            if (i >= 0) { a[i, k] += 1.0; a[k, i] += 1.0; }
                            else if (fixedPotential.TryGetValue(branch.StartNode, out double vStart)) rhs[k] -= vStart;

                            if (j >= 0) { a[j, k] -= 1.0; a[k, j] -= 1.0; }
                            else if (fixedPotential.TryGetValue(branch.EndNode, out double vEnd)) rhs[k] += vEnd;

                            rhs[k] += model.Emf;
                            break;
                        }

                        case DcBranchKind.IdealCurrent:
                        {
                            double current = model.PrescribedCurrent;
                            if (i >= 0) rhs[i] -= current;
                            if (j >= 0) rhs[j] += current;
                            break;
                        }
                    }
                }

                var variableNames = unknownNodes.Select(n => $"φ{n.Label}").ToList();
                variableNames.AddRange(voltageBranches.Select((_, index) => $"I_E{index + 1}"));

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Система уравнений МУП — общий вид",
                    Description = "Записываем уравнения узловых потенциалов символически. Уже известные потенциалы от идеальных источников ЭДС сразу подставляются в правую часть и отдельного уравнения для этих узлов не требуется.",
                    MatrixText = FormatGeneralSystem(unknownNodes, voltageBranches, activeBranches, models, fixedSymbol)
                });

                double[] x = Array.Empty<double>();
                if (nEq > 0)
                {
                    result.Steps.Add(new SolutionStep
                    {
                        Title = "3. Система после подстановки значений",
                        Description = "Подставляем численные значения сопротивлений, ЭДС и токов источников:",
                        MatrixText = FormatNumericEquations(a, rhs, variableNames.ToArray())
                    });

                    var hiddenSteps = new List<SolutionStep>();
                    var units = Enumerable.Repeat("В", nPhi)
                        .Concat(Enumerable.Repeat("А", voltageBranches.Count))
                        .ToArray();
                    x = LinearSystemSolver.Solve(a, rhs, variableNames.ToArray(), hiddenSteps, units);
                }

                foreach (var node in fixedPotential.Keys)
                    node.Potential = fixedPotential[node];
                foreach (var node in unknownNodes)
                    node.Potential = x[nodeVar[node]];

                var potentials = new StringBuilder();
                foreach (var node in nodes)
                {
                    string note = node == reference
                        ? "  (базисный)"
                        : fixedPotential.ContainsKey(node) ? "  (известен из идеальной ЭДС)" : string.Empty;
                    potentials.AppendLine($"  φ{node.Label} = {node.Potential:F6} В{note}");
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = nEq > 0 ? "4. Узловые потенциалы" : "3. Узловые потенциалы",
                    Description = nEq > 0
                        ? "Итог решения системы (промежуточные этапы метода Гаусса не выводятся):"
                        : "Все узловые потенциалы определены непосредственно идеальными ветвями напряжения.",
                    MatrixText = potentials.ToString()
                });

                var eliminatedCurrents = SolveEliminatedVoltageCurrents(
                    reference, fixedPotential.Keys.ToList(), eliminatedVoltageBranches.ToList(),
                    activeBranches, models, voltageVar, x);

                FillBranchResults(result, models, voltageVar, eliminatedCurrents, x,
                    nEq > 0 ? "5. Токи ветвей" : "4. Токи ветвей");

                int powerGeneralIndex = result.Steps.Count + 1;
                PowerBalanceFormatter.AddPowerBalance(result,
                    $"{powerGeneralIndex}. Баланс мощностей — общий вид",
                    $"{powerGeneralIndex + 1}. Баланс мощностей — подстановка и проверка");
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private void FillBranchResults(
            CalculationResult result,
            IReadOnlyDictionary<CircuitBranch, DcBranchModel> models,
            IReadOnlyDictionary<CircuitBranch, int> voltageVar,
            IReadOnlyDictionary<CircuitBranch, double> eliminatedCurrents,
            double[] x,
            string title)
        {
            var currents = new StringBuilder();
            currents.AppendLine("Положительное направление тока каждой ветви: Start→End.");
            currents.AppendLine();

            foreach (var branch in _graph.Branches)
            {
                var model = models[branch];
                double voltage = branch.StartNode.Potential - branch.EndNode.Potential;
                double current;
                string formula;

                switch (model.Kind)
                {
                    case DcBranchKind.Open:
                        current = 0.0;
                        formula = "C в установившемся DC → разрыв";
                        break;
                    case DcBranchKind.Resistive:
                        current = model.CurrentFromVoltage(voltage);
                        formula = $"({voltage:F6} − {model.Emf:F6}) / {model.Resistance:F6}";
                        break;
                    case DcBranchKind.IdealCurrent:
                        current = model.PrescribedCurrent;
                        formula = $"задан источником = {current:F6}";
                        break;
                    case DcBranchKind.IdealVoltage:
                        if (voltageVar.TryGetValue(branch, out int k))
                        {
                            current = x[k];
                            formula = "дополнительная MNA-переменная";
                        }
                        else if (eliminatedCurrents.TryGetValue(branch, out double eliminatedCurrent))
                        {
                            current = eliminatedCurrent;
                            formula = "из 1-го закона Кирхгофа после определения потенциалов";
                        }
                        else
                            throw new InvalidOperationException($"Не удалось определить ток идеальной ветви {branch}.");
                        break;
                    default:
                        throw new InvalidOperationException("Неизвестный тип DC-ветви.");
                }

                branch.Current = current;
                var power = DcBranchModel.ComputePower(model, current, voltage);
                result.BranchResults.Add(new BranchResult
                {
                    Branch = branch,
                    Current = current,
                    Voltage = voltage,
                    PowerConsumed = power.consumed,
                    PowerGenerated = power.generated
                });

                currents.AppendLine($"  {branch}: U={voltage:F6} В; I={formula} = {current:F6} А.");
            }

            result.Steps.Add(new SolutionStep
            {
                Title = title,
                Description = "Токи определяем по найденным потенциалам и типу ветви:",
                MatrixText = currents.ToString()
            });
        }

        private static Dictionary<CircuitBranch, double> SolveEliminatedVoltageCurrents(
            CircuitNode reference,
            IReadOnlyList<CircuitNode> fixedNodes,
            IReadOnlyList<CircuitBranch> eliminatedBranches,
            IReadOnlyList<CircuitBranch> activeBranches,
            IReadOnlyDictionary<CircuitBranch, DcBranchModel> models,
            IReadOnlyDictionary<CircuitBranch, int> voltageVar,
            double[] x)
        {
            var result = new Dictionary<CircuitBranch, double>();
            if (eliminatedBranches.Count == 0) return result;

            var rows = fixedNodes.Where(n => n != reference).ToList();
            if (rows.Count != eliminatedBranches.Count)
                throw new InvalidOperationException("Идеальная сеть источников напряжения содержит контур, в котором токи отдельных идеальных источников не определяются однозначно.");

            var col = eliminatedBranches.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
            double[,] a = new double[rows.Count, eliminatedBranches.Count];
            double[] rhs = new double[rows.Count];

            for (int r = 0; r < rows.Count; r++)
            {
                var node = rows[r];
                double knownOutward = 0.0;
                foreach (var branch in activeBranches.Where(b => b.StartNode == node || b.EndNode == node))
                {
                    int dir = branch.StartNode == node ? +1 : -1;
                    if (col.TryGetValue(branch, out int c))
                    {
                        a[r, c] += dir;
                        continue;
                    }

                    var model = models[branch];
                    double voltage = branch.StartNode.Potential - branch.EndNode.Potential;
                    double current = model.Kind switch
                    {
                        DcBranchKind.Resistive => model.CurrentFromVoltage(voltage),
                        DcBranchKind.IdealCurrent => model.PrescribedCurrent,
                        DcBranchKind.IdealVoltage when voltageVar.TryGetValue(branch, out int k) => x[k],
                        DcBranchKind.Open => 0.0,
                        _ => 0.0
                    };
                    knownOutward += dir * current;
                }
                rhs[r] = -knownOutward;
            }

            var names = eliminatedBranches.Select((_, i) => $"I_Eknown{i + 1}").ToArray();
            var hidden = new List<SolutionStep>();
            var solution = LinearSystemSolver.Solve(a, rhs, names, hidden, Enumerable.Repeat("А", names.Length).ToArray());
            for (int i = 0; i < eliminatedBranches.Count; i++)
                result[eliminatedBranches[i]] = solution[i];
            return result;
        }

        private static CircuitNode SelectReferenceNode(
            IReadOnlyList<CircuitNode> nodes,
            IReadOnlyList<CircuitBranch> activeBranches,
            IReadOnlyDictionary<CircuitBranch, DcBranchModel> models,
            out string reason)
        {
            int Degree(CircuitNode n) => activeBranches.Count(b => b.StartNode == n || b.EndNode == n);
            int SourceCount(CircuitNode n) => activeBranches
                .Where(b => b.StartNode == n || b.EndNode == n)
                .Sum(b => b.Elements.Count(e => e.Type is ElementType.VoltageSource or ElementType.CurrentSource));
            bool HasIdealEmf(CircuitNode n) => activeBranches.Any(b =>
                (b.StartNode == n || b.EndNode == n) &&
                models[b].Kind == DcBranchKind.IdealVoltage &&
                b.Elements.Any(e => e.Type == ElementType.VoltageSource));

            var idealCandidates = nodes.Where(HasIdealEmf).ToList();
            CircuitNode selected;
            if (idealCandidates.Count > 0)
            {
                selected = idealCandidates
                    .OrderByDescending(Degree)
                    .ThenByDescending(SourceCount)
                    .ThenBy(n => ParseLabel(n.Label))
                    .First();
                reason = $"Базисный узел выбран автоматически: узел {selected.Label} подключён к идеальному источнику ЭДС. " +
                         $"Среди таких узлов предпочтён узел с большей степенью ({Degree(selected)} ветвей) и числом источников ({SourceCount(selected)}). " +
                         "Это позволяет сразу определить потенциал соседнего узла через ЭДС и уменьшить число неизвестных.";
            }
            else
            {
                selected = nodes
                    .OrderByDescending(Degree)
                    .ThenByDescending(SourceCount)
                    .ThenBy(n => ParseLabel(n.Label))
                    .First();
                reason = $"Идеальных источников ЭДС без последовательного сопротивления нет. Базисным выбран узел {selected.Label}: " +
                         $"к нему подключено {Degree(selected)} ветвей и {SourceCount(selected)} источников. " +
                         "Выбор узла высокой степени обнуляет потенциал в большем числе слагаемых и упрощает систему.";
            }
            return selected;
        }

        private static int ParseLabel(string label) => int.TryParse(label, out int value) ? value : int.MaxValue;

        private static void PropagateKnownPotentials(
            CircuitNode reference,
            IReadOnlyList<CircuitBranch> activeBranches,
            IReadOnlyDictionary<CircuitBranch, DcBranchModel> models,
            IDictionary<CircuitNode, double> fixedPotential,
            IDictionary<CircuitNode, string> fixedSymbol,
            ISet<CircuitBranch> eliminatedVoltageBranches)
        {
            var q = new Queue<CircuitNode>();
            q.Enqueue(reference);

            while (q.Count > 0)
            {
                var known = q.Dequeue();
                foreach (var branch in activeBranches.Where(b =>
                             models[b].Kind == DcBranchKind.IdealVoltage &&
                             (b.StartNode == known || b.EndNode == known)))
                {
                    var other = branch.StartNode == known ? branch.EndNode : branch.StartNode;
                    double emf = models[branch].Emf;
                    string emfSymbol = EmfSymbol(branch);

                    double candidate;
                    string candidateSymbol;
                    if (branch.StartNode == known)
                    {
                        // φstart - φend = E => φend = φstart - E
                        candidate = fixedPotential[known] - emf;
                        candidateSymbol = SubtractExpression(fixedSymbol[known], emfSymbol);
                    }
                    else
                    {
                        // φstart - φend = E => φstart = φend + E
                        candidate = fixedPotential[known] + emf;
                        candidateSymbol = AddExpression(fixedSymbol[known], emfSymbol);
                    }

                    if (fixedPotential.TryGetValue(other, out double existing))
                    {
                        if (Math.Abs(existing - candidate) > 1e-8)
                            throw new InvalidOperationException("Контур идеальных источников ЭДС содержит несовместимые напряжения.");
                        continue;
                    }

                    fixedPotential[other] = candidate;
                    fixedSymbol[other] = candidateSymbol;
                    eliminatedVoltageBranches.Add(branch);
                    q.Enqueue(other);
                }
            }
        }

        private static string FormatGeneralSystem(
            IReadOnlyList<CircuitNode> unknownNodes,
            IReadOnlyList<CircuitBranch> voltageBranches,
            IReadOnlyList<CircuitBranch> activeBranches,
            IReadOnlyDictionary<CircuitBranch, DcBranchModel> models,
            IReadOnlyDictionary<CircuitNode, string> fixedSymbol)
        {
            var sb = new StringBuilder();
            if (unknownNodes.Count == 0 && voltageBranches.Count == 0)
            {
                sb.AppendLine("Дополнительная система не требуется: все потенциалы определены непосредственно идеальными источниками напряжения.");
                return sb.ToString();
            }

            sb.AppendLine("Соглашение: для ветви Start→End U = φstart − φend = R·I + E.");
            sb.AppendLine();

            var voltageIndex = voltageBranches.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);

            foreach (var node in unknownNodes)
            {
                var lhs = new List<string>();
                var rhs = new List<string>();

                // Диагональный коэффициент φ_node: сумма проводимостей всех резистивных ветвей узла.
                var ownConductances = new List<string>();
                foreach (var branch in activeBranches.Where(b => b.StartNode == node || b.EndNode == node))
                {
                    var model = models[branch];
                    bool nodeIsStart = branch.StartNode == node;
                    var other = nodeIsStart ? branch.EndNode : branch.StartNode;

                    if (model.Kind == DcBranchKind.Resistive)
                    {
                        string r = ResistanceSymbol(branch);
                        ownConductances.Add($"1/({r})");

                        if (unknownNodes.Contains(other))
                            lhs.Add($"−φ{other.Label}/({r})");
                        else if (fixedSymbol.TryGetValue(other, out string knownPhi) && knownPhi != "0")
                            rhs.Add($"+({knownPhi})/({r})");

                        string e = EmfSymbol(branch);
                        if (e != "0")
                            rhs.Add(nodeIsStart ? $"+({e})/({r})" : $"−({e})/({r})");
                    }
                    else if (model.Kind == DcBranchKind.IdealCurrent)
                    {
                        string j = CurrentSourceSymbol(branch);
                        rhs.Add(nodeIsStart ? $"−({j})" : $"+({j})");
                    }
                    else if (model.Kind == DcBranchKind.IdealVoltage && voltageIndex.TryGetValue(branch, out int vi))
                    {
                        lhs.Add(nodeIsStart ? $"+I_E{vi + 1}" : $"−I_E{vi + 1}");
                    }
                }

                if (ownConductances.Count > 0)
                    lhs.Insert(0, $"φ{node.Label}·({string.Join(" + ", ownConductances)})");

                sb.AppendLine($"  {JoinEquationSide(lhs)} = {JoinEquationSide(rhs)}");
            }

            foreach (var branch in voltageBranches)
            {
                string left = fixedSymbol.TryGetValue(branch.StartNode, out string fs)
                    ? fs
                    : $"φ{branch.StartNode.Label}";
                string right = fixedSymbol.TryGetValue(branch.EndNode, out string fe)
                    ? fe
                    : $"φ{branch.EndNode.Label}";
                sb.AppendLine($"  {left} − ({right}) = {EmfSymbol(branch)}");
            }

            return sb.ToString();
        }

        private static string FormatNumericEquations(double[,] a, double[] rhs, string[] names)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < rhs.Length; r++)
            {
                var terms = new List<string>();
                for (int c = 0; c < names.Length; c++)
                {
                    double coef = a[r, c];
                    if (Math.Abs(coef) <= 1e-14) continue;
                    string abs = Math.Abs(coef).ToString("0.######");
                    string term = Math.Abs(Math.Abs(coef) - 1.0) < 1e-12 ? names[c] : $"{abs}·{names[c]}";
                    terms.Add((coef < 0 ? "−" : "+") + term);
                }
                sb.AppendLine($"  {JoinSignedTerms(terms)} = {rhs[r]:0.######}");
            }
            return sb.ToString();
        }

        private static string ResistanceSymbol(CircuitBranch branch)
        {
            var terms = new List<string>();
            foreach (var e in branch.Elements)
            {
                if (e.Type == ElementType.Resistor)
                    terms.Add(e.Name);
                else if ((e.Type is ElementType.VoltageSource or ElementType.CurrentSource) && e.InternalResistance > Eps)
                    terms.Add($"r_{e.Name}");
            }
            return terms.Count == 0 ? "0" : string.Join(" + ", terms);
        }

        private static string EmfSymbol(CircuitBranch branch)
        {
            var terms = new List<(int Sign, string Name)>();
            foreach (var e in branch.Elements.Where(e => e.Type == ElementType.VoltageSource))
            {
                int sign = branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
                terms.Add((sign, e.Name));
            }
            return FormatSignedSymbolSum(terms);
        }

        private static string CurrentSourceSymbol(CircuitBranch branch)
        {
            var e = branch.Elements.First(x => x.Type == ElementType.CurrentSource);
            int sign = branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1 : -1);
            return sign > 0 ? e.Name : $"−{e.Name}";
        }

        private static string FormatSignedSymbolSum(IEnumerable<(int Sign, string Name)> terms)
        {
            var list = terms.ToList();
            if (list.Count == 0) return "0";
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                var (sign, name) = list[i];
                if (i == 0) sb.Append(sign < 0 ? $"−{name}" : name);
                else sb.Append(sign < 0 ? $" − {name}" : $" + {name}");
            }
            return sb.ToString();
        }

        private static string AddExpression(string left, string right)
        {
            if (right == "0") return left;
            if (left == "0") return right;
            return $"{left} + {right}";
        }

        private static string SubtractExpression(string left, string right)
        {
            if (right == "0") return left;
            if (left == "0") return NegateExpression(right);
            return $"{left} − ({right})";
        }

        private static string NegateExpression(string expression)
        {
            if (expression.StartsWith("−") && !expression.Contains(" + ") && !expression.Contains(" − "))
                return expression.Substring(1);
            return $"−({expression})";
        }

        private static string JoinEquationSide(IReadOnlyList<string> terms)
        {
            if (terms.Count == 0) return "0";
            return JoinSignedTerms(terms);
        }

        private static string JoinSignedTerms(IReadOnlyList<string> terms)
        {
            if (terms.Count == 0) return "0";
            var sb = new StringBuilder();
            for (int i = 0; i < terms.Count; i++)
            {
                string t = terms[i];
                bool neg = t.StartsWith("−");
                string body = (t.StartsWith("+") || neg) ? t.Substring(1) : t;
                if (i == 0)
                {
                    if (neg) sb.Append('−');
                    sb.Append(body);
                }
                else
                {
                    sb.Append(neg ? " − " : " + ");
                    sb.Append(body);
                }
            }
            return sb.ToString();
        }

        private static void EnsureConnected(
            IReadOnlyList<CircuitNode> nodes,
            IReadOnlyList<CircuitBranch> activeBranches,
            bool hasOpenDcBranches)
        {
            if (nodes.Count == 0) return;
            var visited = new HashSet<CircuitNode> { nodes[0] };
            var queue = new Queue<CircuitNode>();
            queue.Enqueue(nodes[0]);

            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                foreach (var b in activeBranches.Where(b => b.StartNode == n || b.EndNode == n))
                {
                    var other = b.StartNode == n ? b.EndNode : b.StartNode;
                    if (visited.Add(other)) queue.Enqueue(other);
                }
            }

            if (visited.Count != nodes.Count)
            {
                string reason = hasOpenDcBranches
                    ? "После замены конденсаторов на разрыв в установившемся режиме DC расчётный граф оказался несвязным."
                    : "Расчётный граф оказался несвязным. В схеме нет конденсаторов, поэтому причина не связана с моделью C; проверьте T-соединения и проводники.";
                throw new InvalidOperationException(
                    $"{reason} Активных ветвей: {activeBranches.Count}, узлов: {nodes.Count}. " +
                    "Визуально касающийся вывода провод должен быть электрически присоединён к той же шине.");
            }
        }

        private static CalculationResult Fail(CalculationResult result, string message)
        {
            result.Success = false;
            result.ErrorMessage = message;
            return result;
        }
    }
}
