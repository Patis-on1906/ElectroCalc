using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Расчёт канонической трёхфазной нагрузки в нормальном режиме, при
    /// обрыве и коротком замыкании. Элементы A/B/C соответствуют AN/BN/CN
    /// для звезды и AB/BC/CA для треугольника.
    /// </summary>
    internal sealed class ThreePhaseCircuitSolver
    {
        private static readonly ThreePhasePhase[] Phases =
            { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C };
        private readonly IReadOnlyList<CircuitElement> _elements;
        private readonly CircuitAnalysisSettings _settings;
        private readonly ThreePhaseFaultSettings _fault;
        private const double Eps = 1e-12;

        public ThreePhaseCircuitSolver(IEnumerable<CircuitElement> elements,
            CircuitAnalysisSettings settings, ThreePhaseFaultSettings? fault = null)
        {
            _elements = elements.ToList();
            _settings = settings.Clone();
            _fault = fault?.Clone() ?? new ThreePhaseFaultSettings();
        }

        public CalculationResult Solve()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.ThreePhase,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = true,
                ThreePhaseFault = _fault.Clone()
            };

            try
            {
                _settings.Validate();
                if (_settings.Mode != CircuitAnalysisMode.ThreePhase)
                    throw new InvalidOperationException("Трёхфазный решатель доступен только в режиме 3Φ.");
                _fault.Validate(_settings);

                var relevant = _elements.Where(e => e.Type is ElementType.Resistor or ElementType.Inductor or
                    ElementType.Capacitor or ElementType.VoltageSource or ElementType.CurrentSource).ToList();
                var unassigned = relevant.Where(e => e.PhaseAssignment == ThreePhasePhase.None).ToList();
                if (unassigned.Count > 0)
                    throw new InvalidOperationException(
                        "Назначьте фазу A/B/C всем элементам 3Φ-схемы. Не назначены: " +
                        string.Join(", ", unassigned.Select(e => e.Name)) + ".");

                var phaseData = Phases.Select(BuildPhase).ToArray();
                AddInputStep(result, phaseData);

                ScenarioData normal = _settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
                    ? CalculateStar(phaseData, new ThreePhaseFaultSettings())
                    : CalculateDelta(phaseData, new ThreePhaseFaultSettings());

                if (!_fault.IsEmergency)
                {
                    AddCalculationStep(result, normal, 2, "Расчёт");
                    ApplyScenario(result, normal);
                    AddPowerStep(result, normal, 3, "Мощности трёхфазной цепи");
                    AddWaveformStep(result, phaseData, normal, 4, "Мгновенные функции");
                    AddDiagrams(result, phaseData, normal, string.Empty);
                }
                else
                {
                    ScenarioData emergency = _settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
                        ? CalculateStar(phaseData, _fault)
                        : CalculateDelta(phaseData, _fault);

                    result.ReferenceBranchResults.AddRange(normal.Results);
                    result.ReferenceComplexPowerGenerated = normal.SourcePower;
                    result.ReferenceComplexPowerConsumed = normal.ConsumedPower;
                    AddCalculationStep(result, normal, 2, "Нормальный режим до аварии");
                    AddCalculationStep(result, emergency, 3, $"Аварийный режим: {_fault.Description(_settings)}");
                    AddComparisonStep(result, normal, emergency, 4);
                    ApplyScenario(result, emergency);
                    AddPowerStep(result, emergency, 5, "Мощности аварийного режима");
                    AddWaveformStep(result, phaseData, emergency, 6, "Мгновенные функции аварийного режима");
                    AddDiagrams(result, phaseData, normal, "До аварии — ");
                    AddDiagrams(result, phaseData, emergency, "Аварийный режим — ");
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.Steps.Clear();
                result.Steps.Add(new SolutionStep { Title = "Ошибка", Description = ex.Message });
                return result;
            }
        }

        private sealed class PhaseData
        {
            public ThreePhasePhase Phase { get; init; }
            public CircuitElement Source { get; init; } = null!;
            public List<CircuitElement> Loads { get; init; } = new();
            public Complex E { get; init; }
            public Complex Z { get; init; }
        }

        private sealed class ScenarioData
        {
            public string Description { get; init; } = string.Empty;
            public string Detail { get; init; } = string.Empty;
            public Complex[] LoadVoltages { get; init; } = new Complex[3];
            public Complex[] LoadCurrents { get; init; } = new Complex[3];
            public Complex[] LineCurrents { get; init; } = new Complex[3];
            public Complex NeutralCurrent { get; init; }
            public Complex SourcePower { get; set; }
            public Complex ConsumedPower { get; set; }
            public List<BranchResult> Results { get; } = new();
            public List<PhasorDiagramVector> ExtraCurrentVectors { get; } = new();
        }

        private PhaseData BuildPhase(ThreePhasePhase phase)
        {
            var assigned = _elements.Where(e => e.PhaseAssignment == phase).ToList();
            var sources = assigned.Where(e => e.Type == ElementType.VoltageSource).ToList();
            if (sources.Count != 1)
                throw new InvalidOperationException(
                    $"Для фазы {phase} должен быть назначен ровно один источник ЭДС E. Сейчас: {sources.Count}.");
            if (assigned.Any(e => e.Type == ElementType.CurrentSource))
                throw new InvalidOperationException(
                    $"Источник тока в фазе {phase} не поддерживается. Используйте фазный источник ЭДС.");

            var source = sources[0];
            if (!double.IsFinite(source.Value) || source.Value < 0)
                throw new InvalidOperationException(
                    $"Действующее значение источника {source.Name} должно быть конечным и неотрицательным.");
            if (!double.IsFinite(source.PhaseDegrees))
                throw new InvalidOperationException($"Фаза источника {source.Name} должна быть конечным числом.");
            if (!double.IsFinite(source.InternalResistance) || source.InternalResistance < 0)
                throw new InvalidOperationException(
                    $"Внутреннее сопротивление источника {source.Name} должно быть конечным и неотрицательным.");

            var load = assigned.Where(e => e.Type is ElementType.Resistor or ElementType.Inductor or ElementType.Capacitor).ToList();
            if (load.Count == 0)
                throw new InvalidOperationException($"Для ветви {PhaseArmName(phase)} не назначено ни одного R/L/C элемента.");

            Complex z = Complex.Zero;
            foreach (var e in load)
            {
                if (!double.IsFinite(e.Value) || e.Value < 0)
                    throw new InvalidOperationException($"Значение {e.Name} должно быть конечным и неотрицательным.");
                z += e.Type switch
                {
                    ElementType.Resistor => new Complex(e.Value, 0),
                    ElementType.Inductor => new Complex(0, _settings.AngularFrequency * e.Value),
                    ElementType.Capacitor when e.Value > 0 => new Complex(0, -1.0 / (_settings.AngularFrequency * e.Value)),
                    ElementType.Capacitor => throw new InvalidOperationException($"Ёмкость {e.Name} должна быть больше нуля."),
                    _ => Complex.Zero
                };
            }

            if (source.InternalResistance > Eps)
                throw new InvalidOperationException(
                    $"В 3Φ-режиме внутреннее сопротивление источника {source.Name} должно быть 0 Ом. " +
                    "Сопротивления задавайте явными R/L/C элементами фазы.");
            if (z.Magnitude <= Eps)
                throw new InvalidOperationException($"Полное сопротивление ветви {PhaseArmName(phase)} равно нулю.");

            Complex ePhasor = source.SourcePhasor(_settings) * (source.IsPositiveAtStart ? 1.0 : -1.0);
            return new PhaseData { Phase = phase, Source = source, Loads = load, E = ePhasor, Z = z };
        }

        private ScenarioData CalculateStar(PhaseData[] p, ThreePhaseFaultSettings fault)
        {
            var z = p.Select(value => value.Z).ToArray();
            var active = new[] { true, true, true };
            int faultIndex = LineIndex(fault.Location);
            bool shortCircuit = fault.OperatingMode == ThreePhaseOperatingMode.ShortCircuit;
            bool openCircuit = fault.OperatingMode == ThreePhaseOperatingMode.OpenCircuit;
            if (openCircuit) active[faultIndex] = false;
            if (shortCircuit) z[faultIndex] = new Complex(fault.ShortCircuitResistanceOhms, 0);

            var y = z.Select((value, index) => active[index] ? 1.0 / value : Complex.Zero).ToArray();
            Complex un = Complex.Zero;
            if (!_settings.ThreePhaseHasNeutral)
            {
                Complex totalY = y.Aggregate(Complex.Zero, (sum, value) => sum + value);
                if (totalY.Magnitude <= Eps)
                    throw new InvalidOperationException(
                        "Для звезды без N сумма проводимостей подключённых фаз равна нулю; напряжение нейтрали не определяется.");
                un = p.Select((value, index) => value.E * y[index])
                    .Aggregate(Complex.Zero, (sum, value) => sum + value) / totalY;
            }

            var u = p.Select(value => value.E - un).ToArray();
            var i = u.Select((value, index) => active[index] ? value / z[index] : Complex.Zero).ToArray();
            Complex iNeutral = -(i[0] + i[1] + i[2]);
            var scenario = new ScenarioData
            {
                Description = fault.IsEmergency ? fault.Description(_settings) :
                    (_settings.ThreePhaseHasNeutral ? "Звезда с нулевым проводом" : "Звезда без нулевого провода"),
                Detail = StarDetail(fault, un, u, i, iNeutral),
                LoadVoltages = u,
                LoadCurrents = i,
                LineCurrents = i.ToArray(),
                NeutralCurrent = iNeutral
            };

            for (int k = 0; k < 3; k++)
            {
                string label = $"{Phases[k]}N";
                IEnumerable<CircuitElement> elements = p[k].Loads;
                if (openCircuit && k == faultIndex) label += " (ХХ)";
                if (shortCircuit && k == faultIndex)
                {
                    label += " (КЗ)";
                    elements = new[] { FaultResistor(label, fault.ShortCircuitResistanceOhms, Phases[k]) };
                }
                scenario.Results.Add(CreateLoadResult(label, elements, u[k], i[k]));
            }
            AddLineResults(scenario, p);
            CalculatePowers(scenario, p);
            return scenario;
        }

        private ScenarioData CalculateDelta(PhaseData[] p, ThreePhaseFaultSettings fault)
        {
            var nodeVoltages = p.Select(value => value.E).ToArray();
            var z = p.Select(value => value.Z).ToArray();
            var active = new[] { true, true, true };
            int branchIndex = BranchIndex(fault.Location);
            int lineIndex = LineIndex(fault.Location);
            bool branchFault = branchIndex >= 0;
            bool open = fault.OperatingMode == ThreePhaseOperatingMode.OpenCircuit;
            bool shortCircuit = fault.OperatingMode == ThreePhaseOperatingMode.ShortCircuit;
            Complex openContactVoltage = Complex.Zero;

            if (branchFault && open) active[branchIndex] = false;
            if (branchFault && shortCircuit) z[branchIndex] = new Complex(fault.ShortCircuitResistanceOhms, 0);

            if (!branchFault && open)
            {
                int firstBranch = lineIndex;
                int secondBranch = (lineIndex + 2) % 3;
                int other1 = (lineIndex + 1) % 3;
                int other2 = (lineIndex + 2) % 3;
                Complex totalY = 1.0 / z[firstBranch] + 1.0 / z[secondBranch];
                if (totalY.Magnitude <= Eps)
                    throw new InvalidOperationException(
                        "При обрыве линии потенциал отключённой вершины треугольника не определяется.");
                nodeVoltages[lineIndex] =
                    (nodeVoltages[other1] / z[firstBranch] + nodeVoltages[other2] / z[secondBranch]) / totalY;
                openContactVoltage = p[lineIndex].E - nodeVoltages[lineIndex];
            }

            var u = new[]
            {
                nodeVoltages[0] - nodeVoltages[1],
                nodeVoltages[1] - nodeVoltages[2],
                nodeVoltages[2] - nodeVoltages[0]
            };
            var iBranch = u.Select((value, index) => active[index] ? value / z[index] : Complex.Zero).ToArray();
            var iLine = new[]
            {
                iBranch[0] - iBranch[2],
                iBranch[1] - iBranch[0],
                iBranch[2] - iBranch[1]
            };
            if (!branchFault && open) iLine[lineIndex] = Complex.Zero;

            var scenario = new ScenarioData
            {
                Description = fault.IsEmergency ? fault.Description(_settings) : "Нагрузка, соединённая треугольником",
                Detail = DeltaDetail(fault, u, iBranch, iLine, openContactVoltage),
                LoadVoltages = u,
                LoadCurrents = iBranch,
                LineCurrents = iLine
            };

            string[] names = { "AB", "BC", "CA" };
            for (int k = 0; k < 3; k++)
            {
                string label = names[k];
                IEnumerable<CircuitElement> elements = p[k].Loads;
                if (branchFault && open && k == branchIndex) label += " (ХХ)";
                if (branchFault && shortCircuit && k == branchIndex)
                {
                    label += " (КЗ)";
                    elements = new[] { FaultResistor(label, fault.ShortCircuitResistanceOhms, Phases[k]) };
                }
                scenario.Results.Add(CreateLoadResult(label, elements, u[k], iBranch[k]));
            }

            if (!branchFault && shortCircuit)
            {
                Complex faultCurrent = p[lineIndex].E / fault.ShortCircuitResistanceOhms;
                scenario.LineCurrents[lineIndex] += faultCurrent;
                scenario.ExtraCurrentVectors.Add(new PhasorDiagramVector
                {
                    Label = $"Iк{Phases[lineIndex]}",
                    Value = faultCurrent
                });
                scenario.Results.Add(CreateLoadResult($"КЗ {Phases[lineIndex]}–N",
                    new[] { FaultResistor($"Rк_{Phases[lineIndex]}", fault.ShortCircuitResistanceOhms, Phases[lineIndex]) },
                    p[lineIndex].E, faultCurrent));
            }

            AddLineResults(scenario, p);
            CalculatePowers(scenario, p);
            return scenario;
        }

        private static int LineIndex(ThreePhaseFaultLocation location) => location switch
        {
            ThreePhaseFaultLocation.LineA => 0,
            ThreePhaseFaultLocation.LineB => 1,
            ThreePhaseFaultLocation.LineC => 2,
            _ => 0
        };

        private static int BranchIndex(ThreePhaseFaultLocation location) => location switch
        {
            ThreePhaseFaultLocation.BranchAB => 0,
            ThreePhaseFaultLocation.BranchBC => 1,
            ThreePhaseFaultLocation.BranchCA => 2,
            _ => -1
        };

        private string StarDetail(ThreePhaseFaultSettings fault, Complex un, Complex[] u,
            Complex[] i, Complex iNeutral)
        {
            var sb = new StringBuilder();
            if (_settings.ThreePhaseHasNeutral)
                sb.AppendLine("  Нулевая точка нагрузки соединена с нейтралью источника: U̲N=0.");
            else
            {
                sb.AppendLine("  U̲N=Σ(E̲k·Y̲k)/ΣY̲k, где для оборванной ветви Y̲k=0.");
                sb.AppendLine($"  U̲N = {Fmt(un, "В")}");
            }
            if (fault.OperatingMode == ThreePhaseOperatingMode.OpenCircuit)
                sb.AppendLine("  В оборванной ветви I̲=0; указанное напряжение приложено к месту разрыва.");
            if (fault.OperatingMode == ThreePhaseOperatingMode.ShortCircuit)
                sb.AppendLine($"  Повреждённая нагрузка заменена сопротивлением Rк={fault.ShortCircuitResistanceOhms:G6} Ом.");
            for (int k = 0; k < 3; k++)
                sb.AppendLine($"  U̲{Phases[k]}N = {Fmt(u[k], "В")}    I̲{Phases[k]} = {Fmt(i[k], "А")}");
            if (_settings.ThreePhaseHasNeutral)
                sb.AppendLine($"  I̲N=−(I̲A+I̲B+I̲C) = {Fmt(iNeutral, "А")}");
            else
                sb.AppendLine($"  Контроль: I̲A+I̲B+I̲C = {Fmt(i[0] + i[1] + i[2], "А")}");
            return sb.ToString();
        }

        private string DeltaDetail(ThreePhaseFaultSettings fault, Complex[] u, Complex[] branchCurrents,
            Complex[] lineCurrents, Complex openVoltage)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  Направления токов ветвей: AB, BC, CA.");
            if (fault.OperatingMode == ThreePhaseOperatingMode.OpenCircuit && BranchIndex(fault.Location) < 0)
                sb.AppendLine($"  Потенциал отключённой вершины найден по первому закону Кирхгофа; U̲разрыва={Fmt(openVoltage, "В")}.");
            else if (fault.OperatingMode == ThreePhaseOperatingMode.OpenCircuit)
                sb.AppendLine("  Проводимость оборванной ветви принята равной нулю.");
            if (fault.OperatingMode == ThreePhaseOperatingMode.ShortCircuit)
                sb.AppendLine($"  Ток КЗ ограничен сопротивлением Rк={fault.ShortCircuitResistanceOhms:G6} Ом.");
            string[] names = { "AB", "BC", "CA" };
            for (int k = 0; k < 3; k++)
                sb.AppendLine($"  U̲{names[k]} = {Fmt(u[k], "В")}    I̲{names[k]} = {Fmt(branchCurrents[k], "А")}");
            for (int k = 0; k < 3; k++)
                sb.AppendLine($"  I̲{Phases[k]} = {Fmt(lineCurrents[k], "А")}");
            sb.AppendLine($"  Контроль: I̲A+I̲B+I̲C = {Fmt(lineCurrents[0] + lineCurrents[1] + lineCurrents[2], "А")}");
            return sb.ToString();
        }

        private void AddInputStep(CalculationResult result, PhaseData[] p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Схема: {_settings}");
            sb.AppendLine($"  Выбранное состояние: {_fault.Description(_settings)}");
            if (_fault.OperatingMode == ThreePhaseOperatingMode.ShortCircuit)
                sb.AppendLine($"  Rк = {_fault.ShortCircuitResistanceOhms:G6} Ом");
            sb.AppendLine($"  ω = 2πf = {_settings.AngularFrequency.ToString("0.######", CultureInfo.InvariantCulture)} рад/с");
            foreach (var phase in p)
            {
                sb.AppendLine($"  {PhaseArmName(phase.Phase)}: {string.Join(" + ", phase.Loads.Select(e => e.Name))}");
                sb.AppendLine($"    E̲{phase.Phase} = {Fmt(phase.E, "В")}; Z̲{PhaseImpedanceSuffix(phase.Phase)} = {Fmt(phase.Z, "Ом")}");
            }
            result.Steps.Add(new SolutionStep
            {
                Title = "1. Формирование трёхфазной расчётной схемы",
                Description = "Исходные ветви представлены последовательными комплексными сопротивлениями; фазоры являются RMS-значениями.",
                MatrixText = sb.ToString()
            });
        }

        private static void AddCalculationStep(CalculationResult result, ScenarioData scenario,
            int number, string title) => result.Steps.Add(new SolutionStep
        {
            Title = $"{number}. {title}",
            Description = scenario.Description,
            MatrixText = scenario.Detail
        });

        private static void AddComparisonStep(CalculationResult result, ScenarioData normal,
            ScenarioData emergency, int number)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  Линейные токи (RMS-фазоры):");
            for (int k = 0; k < 3; k++)
            {
                double ratio = normal.LineCurrents[k].Magnitude <= Eps
                    ? double.NaN
                    : emergency.LineCurrents[k].Magnitude / normal.LineCurrents[k].Magnitude;
                string ratioText = double.IsNaN(ratio)
                    ? "—"
                    : ratio.ToString("0.######", CultureInfo.InvariantCulture);
                sb.AppendLine($"  I̲{Phases[k]}: до {Fmt(normal.LineCurrents[k], "А")}; " +
                              $"после {Fmt(emergency.LineCurrents[k], "А")}; |Iав|/|Iнорм|={ratioText}");
            }
            result.Steps.Add(new SolutionStep
            {
                Title = $"{number}. Сравнение нормального и аварийного режимов",
                Description = "Сопоставление токов до и после возникновения аварии.",
                MatrixText = sb.ToString()
            });
        }

        private static void ApplyScenario(CalculationResult result, ScenarioData scenario)
        {
            result.BranchResults.AddRange(scenario.Results);
            result.TotalComplexPowerGenerated = scenario.SourcePower;
            result.TotalComplexPowerConsumed = scenario.ConsumedPower;
        }

        private static void CalculatePowers(ScenarioData scenario, PhaseData[] p)
        {
            scenario.ConsumedPower = scenario.Results
                .Where(row => !row.Branch.StartNode.Label.StartsWith("Линия ", StringComparison.Ordinal))
                .Aggregate(Complex.Zero, (sum, row) => sum + row.TerminalComplexPower);
            scenario.SourcePower = p.Select((value, index) =>
                    value.E * Complex.Conjugate(scenario.LineCurrents[index]))
                .Aggregate(Complex.Zero, (sum, value) => sum + value);
        }

        private static void AddPowerStep(CalculationResult result, ScenarioData scenario,
            int number, string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  S̲ = U̲·I̲* = P + jQ");
            sb.AppendLine($"  ΣS̲наг = {Fmt(scenario.ConsumedPower, "ВА")}");
            sb.AppendLine($"    Pнаг = {scenario.ConsumedPower.Real:0.######} Вт; " +
                          $"Qнаг = {scenario.ConsumedPower.Imaginary:0.######} вар; " +
                          $"|Sнаг| = {scenario.ConsumedPower.Magnitude:0.######} ВА");
            sb.AppendLine($"  ΣS̲ист = {Fmt(scenario.SourcePower, "ВА")}");
            sb.AppendLine($"  |ΔS| = {(scenario.SourcePower - scenario.ConsumedPower).Magnitude:0.###E+0} ВА");
            result.Steps.Add(new SolutionStep
            {
                Title = $"{number}. {title}",
                Description = "Комплексная мощность рассчитана без предположения о симметрии нагрузки.",
                MatrixText = sb.ToString()
            });
        }

        private void AddWaveformStep(CalculationResult result, PhaseData[] p,
            ScenarioData scenario, int number, string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  x(t)=√2·|X̲|·sin(ωt+φ), значения фазоров — RMS.");
            for (int k = 0; k < 3; k++)
            {
                sb.AppendLine($"  e{Phases[k]}(t) = " +
                    InstantaneousWaveformFormatter.Function(p[k].E, _settings.AngularFrequency, "В"));
                sb.AppendLine($"  i{Phases[k]}(t) = " +
                    InstantaneousWaveformFormatter.Function(scenario.LineCurrents[k], _settings.AngularFrequency, "А"));
            }
            if (_settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star && _settings.ThreePhaseHasNeutral)
                sb.AppendLine("  iN(t) = " + InstantaneousWaveformFormatter.Function(
                    scenario.NeutralCurrent, _settings.AngularFrequency, "А"));
            result.Steps.Add(new SolutionStep
            {
                Title = $"{number}. {title}",
                Description = "Временные функции фазных ЭДС и линейных токов восстановлены из RMS-фазоров.",
                MatrixText = sb.ToString()
            });
        }

        private void AddDiagrams(CalculationResult result, PhaseData[] p,
            ScenarioData scenario, string prefix)
        {
            result.VectorDiagrams.Add(Diagram(prefix + "Фазные ЭДС", "В",
                "Симметричная система фазных ЭДС источника.",
                Phases.Select((phase, k) => ($"E{phase}", p[k].E))));

            if (_settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star)
            {
                result.VectorDiagrams.Add(Diagram(prefix + "Напряжения фаз нагрузки", "В",
                    "Напряжения AN, BN и CN; при обрыве показано напряжение на месте разрыва.",
                    Phases.Select((phase, k) => ($"U{phase}N", scenario.LoadVoltages[k]))));
            }
            else
            {
                string[] names = { "AB", "BC", "CA" };
                result.VectorDiagrams.Add(Diagram(prefix + "Линейные напряжения", "В",
                    "Напряжения на ветвях треугольника.",
                    names.Select((name, k) => ($"U{name}", scenario.LoadVoltages[k]))));
                result.VectorDiagrams.Add(Diagram(prefix + "Токи ветвей треугольника", "А",
                    "Токи направлены AB, BC и CA.",
                    names.Select((name, k) => ($"I{name}", scenario.LoadCurrents[k]))));
            }

            var currentVectors = Phases
                .Select((phase, k) => ($"I{phase}", scenario.LineCurrents[k])).ToList();
            currentVectors.AddRange(scenario.ExtraCurrentVectors.Select(v => (v.Label, v.Value)));
            if (_settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star && _settings.ThreePhaseHasNeutral)
                currentVectors.Add(("IN", scenario.NeutralCurrent));
            result.VectorDiagrams.Add(Diagram(prefix + "Линейные токи", "А",
                "Линейные токи источника" + (_settings.ThreePhaseHasNeutral ? " и ток нейтрали." : "."),
                currentVectors));
        }

        private static PhasorDiagramData Diagram(string title, string unit, string description,
            IEnumerable<(string Label, Complex Value)> values)
        {
            var diagram = new PhasorDiagramData
            {
                Title = title,
                Unit = unit,
                Description = description
            };
            foreach (var value in values)
                diagram.Vectors.Add(new PhasorDiagramVector { Label = value.Label, Value = value.Value });
            return diagram;
        }

        private static void AddLineResults(ScenarioData scenario, PhaseData[] p)
        {
            for (int k = 0; k < 3; k++)
            {
                var branch = SyntheticBranch($"Линия {Phases[k]}", new[] { p[k].Source });
                scenario.Results.Add(new BranchResult
                {
                    Branch = branch,
                    CurrentPhasor = scenario.LineCurrents[k],
                    VoltagePhasor = p[k].E,
                    TerminalComplexPower = p[k].E * Complex.Conjugate(scenario.LineCurrents[k])
                });
            }
        }

        private static BranchResult CreateLoadResult(string name,
            IEnumerable<CircuitElement> elements, Complex voltage, Complex current)
        {
            Complex power = voltage * Complex.Conjugate(current);
            return new BranchResult
            {
                Branch = SyntheticBranch(name, elements),
                CurrentPhasor = current,
                VoltagePhasor = voltage,
                PassiveComplexPower = power,
                TerminalComplexPower = power
            };
        }

        private static CircuitElement FaultResistor(string name, double resistance,
            ThreePhasePhase phase) => new()
        {
            Type = ElementType.Resistor,
            Name = name,
            Value = resistance,
            PhaseAssignment = phase
        };

        private static CircuitBranch SyntheticBranch(string label,
            IEnumerable<CircuitElement> elements)
        {
            var branch = new CircuitBranch
            {
                StartNode = new CircuitNode { Label = label, Position = new Point() },
                EndNode = new CircuitNode { Label = string.Empty, Position = new Point() }
            };
            foreach (var element in elements) branch.AddElement(element, 1);
            return branch;
        }

        private string PhaseArmName(ThreePhasePhase phase) =>
            _settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
                ? $"ветвь {phase}N"
                : phase switch
                {
                    ThreePhasePhase.A => "ветвь AB",
                    ThreePhasePhase.B => "ветвь BC",
                    _ => "ветвь CA"
                };

        private string PhaseImpedanceSuffix(ThreePhasePhase phase) =>
            _settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
                ? phase.ToString()
                : phase switch
                {
                    ThreePhasePhase.A => "AB",
                    ThreePhasePhase.B => "BC",
                    _ => "CA"
                };

        private static string Fmt(Complex value, string unit) =>
            $"{Phasor.Rectangular(value, "0.######")} {unit} = " +
            $"{Phasor.Exponential(value, "0.######", "0.##")} {unit}";
    }
}
