using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Описание обнаруженного учебного упрощения для трёхветвевой двухузловой цепи:
    /// одна питающая ветвь и две пассивные параллельные ветви. Работает и в DC, и в AC.
    /// </summary>
    public sealed class ThreeBranchOpportunity
    {
        public CircuitNode NodeA { get; init; } = null!;
        public CircuitNode NodeB { get; init; } = null!;
        public CircuitBranch FeedBranch { get; init; } = null!;
        public CircuitBranch LoadBranch1 { get; init; } = null!;
        public CircuitBranch LoadBranch2 { get; init; } = null!;
        public string Description { get; init; } = string.Empty;
    }

    public static class ThreeBranchSimplifiedDetector
    {
        public static ThreeBranchOpportunity? Detect(CircuitGraph graph, CircuitAnalysisSettings settings)
        {
            if (graph.Nodes.Count != 2 || graph.Branches.Count != 3)
                return null;

            try
            {
                var models = graph.Branches.ToDictionary(b => b, b => SteadyStateBranchModel.Create(b, settings));
                if (models.Values.Any(m => m.Kind == SteadyStateBranchKind.Open))
                    return null;

                bool IsPassiveLoad(CircuitBranch b)
                {
                    var m = models[b];
                    return m.Kind == SteadyStateBranchKind.Impedance &&
                           Phasor.NearlyZero(m.Emf) &&
                           b.Elements.All(e => e.Type is not (ElementType.VoltageSource or ElementType.CurrentSource));
                }

                var loads = graph.Branches.Where(IsPassiveLoad).ToList();
                if (loads.Count != 2)
                    return null;

                var feed = graph.Branches.Single(b => !loads.Contains(b));
                var feedModel = models[feed];
                bool hasSource = feed.Elements.Any(e => e.Type is ElementType.VoltageSource or ElementType.CurrentSource);
                if (!hasSource || feedModel.Kind == SteadyStateBranchKind.Open)
                    return null;

                // Для правила деления тока обе нагрузки должны иметь конечные ненулевые Z.
                if (models[loads[0]].Impedance.Magnitude <= 1e-12 ||
                    models[loads[1]].Impedance.Magnitude <= 1e-12)
                    return null;

                return new ThreeBranchOpportunity
                {
                    NodeA = graph.Nodes[0],
                    NodeB = graph.Nodes[1],
                    FeedBranch = feed,
                    LoadBranch1 = loads[0],
                    LoadBranch2 = loads[1],
                    Description = "Обнаружена двухузловая схема из трёх ветвей: одна питающая ветвь и две пассивные параллельные ветви. " +
                                  "Можно заменить две нагрузки эквивалентным сопротивлением/импедансом и разнести общий ток по правилу деления токов."
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Короткий расчёт обнаруженной трёхветвевой схемы. Все вычисления выполняются
    /// на комплексной модели; DC автоматически является её вещественным частным случаем.
    /// </summary>
    public sealed class ThreeBranchSimplifiedSolver
    {
        private readonly CircuitGraph _graph;
        private readonly CircuitAnalysisSettings _settings;
        private readonly ThreeBranchOpportunity _opportunity;

        public ThreeBranchSimplifiedSolver(CircuitGraph graph, CircuitAnalysisSettings settings, ThreeBranchOpportunity opportunity)
        {
            _graph = graph;
            _settings = settings.Clone();
            _opportunity = opportunity;
        }

        public CalculationResult Solve()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.SimplifiedThreeBranch,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = false
            };

            try
            {
                var current = ThreeBranchSimplifiedDetector.Detect(_graph, _settings);
                if (current == null)
                    throw new InvalidOperationException("Текущая схема больше не соответствует условиям упрощённого трёхветвевого расчёта.");

                var a = current.NodeA;
                var b = current.NodeB;
                var feed = current.FeedBranch;
                var l1 = current.LoadBranch1;
                var l2 = current.LoadBranch2;
                var mf = SteadyStateBranchModel.Create(feed, _settings);
                var m1 = SteadyStateBranchModel.Create(l1, _settings);
                var m2 = SteadyStateBranchModel.Create(l2, _settings);

                Complex z1 = m1.Impedance;
                Complex z2 = m2.Impedance;
                // Work in admittances: at parallel LC resonance Y1+Y2=0,
                // while the individual load currents are still finite.
                Complex yLoad = Complex.One / z1 + Complex.One / z2;
                string equivalentText = yLoad == Complex.Zero
                    ? "∞ (параллельный резонанс, Y̲экв = 0)"
                    : Format(Complex.One / yLoad, "Ом");

                result.Steps.Add(new SolutionStep
                {
                    Title = "1. Автоматическое обнаружение упрощения",
                    Description = current.Description,
                    MatrixText = $"  Узлы: {a.Label} и {b.Label}\n  Питающая ветвь: {feed}\n  Параллельные ветви: {l1}; {l2}\n" +
                                 $"  Z̲1 = {Format(z1, "Ом")}\n  Z̲2 = {Format(z2, "Ом")}"
                });

                result.Steps.Add(new SolutionStep
                {
                    Title = "2. Эквивалент двух параллельных ветвей",
                    Description = "Две пассивные ветви заменяем эквивалентным импедансом. Для DC эта формула автоматически становится формулой параллельного сопротивления.",
                    MatrixText = $"  Y̲экв = 1/Z̲1 + 1/Z̲2 = {Format(yLoad, "См")}\n  Z̲экв = 1/Y̲экв = {equivalentText}"
                });

                int df = DirectionFromA(feed, a);
                Complex emfAB = df * mf.Emf;
                Complex ifeedAB;
                Complex vab;

                switch (mf.Kind)
                {
                    case SteadyStateBranchKind.IdealCurrent:
                        ifeedAB = df * mf.PrescribedCurrent;
                        // KCL в узле A: Ifeed + Iнаг = 0.
                        if (yLoad == Complex.Zero)
                            throw new InvalidOperationException(
                                "Параллельный резонанс без потерь: идеальный источник тока не задаёт конечное однозначное напряжение. Добавьте физические потери.");
                        vab = -ifeedAB / yLoad;
                        break;
                    case SteadyStateBranchKind.IdealVoltage:
                        vab = emfAB;
                        ifeedAB = -vab * yLoad;
                        break;
                    case SteadyStateBranchKind.Impedance:
                    {
                        Complex zf = mf.Impedance;
                        // (V-E)/Zf + V*Yload = 0, including Yload=0.
                        Complex denominator = Complex.One + zf * yLoad;
                        if (denominator.Magnitude < 1e-12)
                            throw new InvalidOperationException(
                                "Резонанс идеальной схемы: токи не определены однозначно. Добавьте физические потери.");
                        vab = emfAB / denominator;
                        ifeedAB = -vab * yLoad;
                        break;
                    }
                    default:
                        throw new InvalidOperationException("Питающая ветвь не поддерживается коротким способом.");
                }

                Complex totalLoad = -ifeedAB;
                Complex i1AB = vab / z1;
                Complex i2AB = vab / z2;
                a.PotentialPhasor = Complex.Zero;
                b.PotentialPhasor = -vab;

                var totalText = new StringBuilder();
                totalText.AppendLine($"  U̲{a.Label}{b.Label} = {Format(vab, "В")}");
                totalText.AppendLine($"  I̲общ = {Format(totalLoad, "А")}");
                result.Steps.Add(new SolutionStep
                {
                    Title = "3. Общий ток нагрузки",
                    Description = mf.Kind == SteadyStateBranchKind.IdealCurrent
                        ? "Общий ток двух параллельных ветвей задаётся питающим источником тока с противоположным знаком по первому закону Кирхгофа."
                        : "После замены параллельной части на Z̲экв определяем напряжение между узлами и общий ток нагрузки.",
                    MatrixText = totalText.ToString()
                });

                result.Steps.Add(new SolutionStep
                {
                    Title = "4. Разнесение тока по двум ветвям",
                    Description = "Определяем токи параллельных ветвей по общему напряжению U̲. Это эквивалентно правилу деления токов и применимо также при параллельном резонансе.",
                    MatrixText = $"  I̲1 = U̲/Z̲1 = {Format(i1AB, "А")}\n" +
                                 $"  I̲2 = U̲/Z̲2 = {Format(i2AB, "А")}\n" +
                                 $"  Проверка: I̲1 + I̲2 = {Format(i1AB + i2AB, "А")}"
                });

                var currentAB = new Dictionary<CircuitBranch, Complex>
                {
                    [feed] = ifeedAB,
                    [l1] = i1AB,
                    [l2] = i2AB
                };

                foreach (var branch in _graph.Branches)
                {
                    int d = DirectionFromA(branch, a);
                    Complex ib = currentAB[branch] * d;
                    Complex ub = vab * d;
                    branch.CurrentPhasor = ib;
                    result.BranchResults.Add(new BranchResult
                    {
                        Branch = branch,
                        CurrentPhasor = ib,
                        VoltagePhasor = ub
                    });
                }

                result.Steps.Add(new SolutionStep
                {
                    Title = "5. Итоговые токи ветвей",
                    Description = "Знаки приведены к штатному направлению каждой расчётной ветви Start→End.",
                    MatrixText = string.Join("\n", result.BranchResults.Select((r, i) =>
                        $"  I{i + 1} [{r.Branch}] = {Format(r.CurrentPhasor, "А")}; U = {Format(r.VoltagePhasor, "В")}"))
                });

                ComplexPowerCalculator.AddPowerBalance(result, _settings,
                    "6. Баланс мощностей — общий вид",
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

        private static int DirectionFromA(CircuitBranch branch, CircuitNode a) =>
            branch.StartNode == a ? +1 : -1;

        private string Format(Complex value, string unit) =>
            _settings.Mode == CircuitAnalysisMode.AC
                ? $"{Phasor.Rectangular(value)} {unit} = {Phasor.Exponential(value)} {unit}"
                : $"{value.Real:0.######} {unit}";
    }
}
