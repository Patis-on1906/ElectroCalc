using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Расчёт данных для потенциальной диаграммы выбранного контура в режиме DC.
    /// График не строится: выдаётся последовательность потенциалов после каждого
    /// элемента при обходе контура. Начальная точка условно принимается за 0 В.
    /// </summary>
    public sealed class PotentialDiagramSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitContour _contour;

        public PotentialDiagramSolver(CircuitGraph graph, CircuitContour contour)
        {
            _graph = graph;
            _contour = contour;
        }

        public CalculationResult Solve()
        {
            var result = new CalculationResult { Method = CalculationMethod.PotentialDiagram };
            try
            {
                if (_contour.Branches.Count < 2)
                    return Fail(result, "Выбранный путь не образует замкнутый контур.");

                // Токи и напряжения получаем тем же проверенным МУП, который используется
                // в основном расчёте. Его промежуточные шаги здесь не показываем.
                var mup = new NodePotentialSolver(_graph).Solve();
                if (!mup.Success)
                    return Fail(result, "Не удалось рассчитать исходную схему для потенциальной диаграммы: " + mup.ErrorMessage);

                var rows = mup.BranchResults.ToDictionary(x => x.Branch);
                var branchNumber = _graph.Branches
                    .Select((b, i) => (b, i: i + 1))
                    .ToDictionary(x => x.b, x => x.i);

                ValidateContinuity();

                var startItem = _contour.Branches[0];
                CircuitNode startNode = startItem.Direction > 0
                    ? startItem.Branch.StartNode
                    : startItem.Branch.EndNode;

                var intro = new StringBuilder();
                intro.AppendLine($"Выбран {_contour}.");
                intro.AppendLine($"Направление обхода задаётся порядком ветвей в выбранном контуре.");
                intro.AppendLine($"Начальная точка: узел {startNode.Label}; для потенциальной диаграммы принимаем φнач = 0 В.");
                intro.AppendLine("Изменение потенциала при переходе по элементу равно минус падению напряжения в направлении обхода.");
                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Выбор контура и начала отсчёта",
                    Description = "Фиксируем направление обхода и нулевой уровень потенциальной диаграммы.",
                    MatrixText = intro.ToString()
                });

                var calculation = new StringBuilder();
                var points = new StringBuilder();
                double phi = 0.0;
                int point = 0;
                points.AppendLine($"Точка {point}: узел {startNode.Label} — φ = {phi:F6} В");

                foreach (var contourBranch in _contour.Branches)
                {
                    var branch = contourBranch.Branch;
                    int dir = contourBranch.Direction;
                    var row = rows[branch];
                    int iNum = branchNumber[branch];
                    var elements = dir > 0
                        ? branch.Elements.ToList()
                        : branch.Elements.AsEnumerable().Reverse().ToList();

                    var branchDrops = BuildElementDrops(branch, row, iNum);
                    calculation.AppendLine($"Ветвь I{iNum}: {branch}; I{iNum} = {row.Current:F6} А; " +
                                           $"обход {(dir > 0 ? "Start→End" : "End→Start")}.");

                    foreach (var element in elements)
                    {
                        double dropBranchDirection = branchDrops[element];
                        double deltaPhi = -dir * dropBranchDirection;
                        double oldPhi = phi;
                        phi += deltaPhi;
                        point++;

                        calculation.AppendLine(
                            $"  {element.Name}: {BuildDeltaFormula(branch, element, row.Current, iNum, dir)} " +
                            $"= {deltaPhi:+0.000000;-0.000000;0.000000} В; " +
                            $"φ{point} = {oldPhi:F6} + ({deltaPhi:F6}) = {phi:F6} В.");
                        points.AppendLine($"Точка {point}: после {element.Name} — φ = {phi:F6} В");
                    }
                    calculation.AppendLine();
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Последовательный расчёт потенциалов",
                    Description = "Обходим выбранный контур элемент за элементом, используя рассчитанные токи ветвей.",
                    MatrixText = calculation.ToString()
                });

                double closure = phi;
                points.AppendLine();
                points.AppendLine($"Контроль замыкания: φкон = {closure:F6} В, φнач = 0,000000 В.");
                points.AppendLine($"Невязка замкнутого контура = {Math.Abs(closure):E3} В " +
                                  (Math.Abs(closure) <= 1e-6 ? "✓" : "⚠"));
                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Данные для построения потенциальной диаграммы",
                    Description = "По оси X вручную откладывайте точки в указанном порядке, по оси Y — рассчитанный потенциал φ.",
                    MatrixText = points.ToString()
                });

                foreach (var r in mup.BranchResults)
                    result.BranchResults.Add(new BranchResult
                    {
                        Branch = r.Branch,
                        Current = r.Current,
                        Voltage = r.Voltage,
                        PowerConsumed = r.PowerConsumed,
                        PowerGenerated = r.PowerGenerated
                    });
                result.TotalPowerGenerated = mup.TotalPowerGenerated;
                result.TotalPowerConsumed = mup.TotalPowerConsumed;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        private Dictionary<CircuitElement, double> BuildElementDrops(
            CircuitBranch branch, BranchResult row, int branchNumber)
        {
            var drops = new Dictionary<CircuitElement, double>();
            var unresolved = new List<CircuitElement>();
            double known = 0.0;

            foreach (var e in branch.Elements)
            {
                double drop;
                switch (e.Type)
                {
                    case ElementType.Resistor:
                        drop = row.Current * Math.Max(0.0, e.Value);
                        break;
                    case ElementType.VoltageSource:
                    {
                        int orientation = branch.GetElementDirection(e);
                        int polarity = e.IsPositiveAtStart ? 1 : -1;
                        drop = row.Current * Math.Max(0.0, e.InternalResistance) +
                               orientation * polarity * e.Value;
                        break;
                    }
                    case ElementType.Inductor:
                        drop = 0.0; // установившийся DC
                        break;
                    case ElementType.CurrentSource:
                    case ElementType.Capacitor:
                        unresolved.Add(e);
                        continue;
                    default:
                        unresolved.Add(e);
                        continue;
                }
                drops[e] = drop;
                known += drop;
            }

            if (unresolved.Count == 1)
            {
                // Напряжение единственного элемента с неопределённым локальным падением
                // восстанавливаем из полного напряжения ветви.
                drops[unresolved[0]] = row.Voltage - known;
            }
            else if (unresolved.Count > 1)
            {
                throw new InvalidOperationException(
                    $"В ветви I{branchNumber} ({branch.ElementNames}) несколько элементов, " +
                    "индивидуальное напряжение на которых нельзя однозначно восстановить " +
                    "из расчёта DC (например, несколько идеальных источников тока/конденсаторов). " +
                    "Для потенциальной диаграммы выберите контур без такой ветви.");
            }

            // Численная самопроверка декомпозиции напряжения ветви.
            double sum = drops.Values.Sum();
            if (Math.Abs(sum - row.Voltage) > 1e-6 * Math.Max(1.0, Math.Abs(row.Voltage)))
                throw new InvalidOperationException(
                    $"Не удалось согласовать падения напряжений внутри ветви I{branchNumber}: " +
                    $"ΣUэлем={sum:F6} В, Uветви={row.Voltage:F6} В.");
            return drops;
        }

        private static string BuildDeltaFormula(
            CircuitBranch branch, CircuitElement e, double current, int iNum, int traverseDir)
        {
            string sign = traverseDir > 0 ? "−" : "+";
            switch (e.Type)
            {
                case ElementType.Resistor:
                    return $"Δφ = {sign}I{iNum}·{e.Name}; {sign}({current:F6})·{e.Value:F6}";
                case ElementType.VoltageSource:
                {
                    int orientation = branch.GetElementDirection(e);
                    int polarity = e.IsPositiveAtStart ? 1 : -1;
                    int emfAlongTraverse = traverseDir * orientation * polarity;
                    string emfTerm = emfAlongTraverse > 0 ? $"−{e.Name}" : $"+{e.Name}";
                    if (e.InternalResistance > 1e-12)
                    {
                        string rTerm = traverseDir > 0 ? $"−I{iNum}·r_{e.Name}" : $"+I{iNum}·r_{e.Name}";
                        return $"Δφ = {rTerm} {emfTerm}";
                    }
                    return $"Δφ = {emfTerm}";
                }
                case ElementType.Inductor:
                    return "Δφ = 0 (L — короткое замыкание в установившемся DC)";
                case ElementType.Capacitor:
                    return "Δφ = −U_C по рассчитанному напряжению ветви";
                case ElementType.CurrentSource:
                    return "Δφ = −U_J по рассчитанному напряжению ветви";
                default:
                    return "Δφ";
            }
        }

        private void ValidateContinuity()
        {
            for (int i = 0; i < _contour.Branches.Count; i++)
            {
                var a = _contour.Branches[i];
                var b = _contour.Branches[(i + 1) % _contour.Branches.Count];
                CircuitNode endA = a.Direction > 0 ? a.Branch.EndNode : a.Branch.StartNode;
                CircuitNode startB = b.Direction > 0 ? b.Branch.StartNode : b.Branch.EndNode;
                if (endA != startB)
                    throw new InvalidOperationException("Выбранный контур имеет нарушенный порядок обхода ветвей.");
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
