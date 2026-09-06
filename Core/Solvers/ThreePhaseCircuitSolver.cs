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
    /// Универсальный расчёт канонической трёхфазной нагрузки в установившемся
    /// синусоидальном режиме. Элементы нагрузки назначаются ветвям A/B/C:
    ///   звезда: A=AN, B=BN, C=CN;
    ///   треугольник: A=AB, B=BC, C=CA.
    /// В каждой ветви допускается произвольное последовательное сочетание R/L/C.
    /// Источник каждой фазы задаётся отдельным E, также назначенным A/B/C.
    /// Источники могут быть несимметричными по модулю и фазе.
    /// </summary>
    internal sealed class ThreePhaseCircuitSolver
    {
        private readonly IReadOnlyList<CircuitElement> _elements;
        private readonly CircuitAnalysisSettings _settings;
        private const double Eps = 1e-12;

        public ThreePhaseCircuitSolver(IEnumerable<CircuitElement> elements, CircuitAnalysisSettings settings)
        {
            _elements = elements.ToList();
            _settings = settings.Clone();
        }

        public CalculationResult Solve()
        {
            var result = new CalculationResult
            {
                Method = CalculationMethod.ThreePhase,
                Analysis = _settings.Clone(),
                PowerBalanceAvailable = true
            };

            try
            {
                _settings.Validate();
                if (_settings.Mode != CircuitAnalysisMode.ThreePhase)
                    throw new InvalidOperationException("Трёхфазный решатель доступен только в режиме 3Φ.");

                var relevant = _elements.Where(e => e.Type is ElementType.Resistor or ElementType.Inductor or
                    ElementType.Capacitor or ElementType.VoltageSource or ElementType.CurrentSource).ToList();
                var unassigned = relevant.Where(e => e.PhaseAssignment == ThreePhasePhase.None).ToList();
                if (unassigned.Count > 0)
                    throw new InvalidOperationException(
                        "Назначьте фазу A/B/C всем элементам 3Φ-схемы. Не назначены: " +
                        string.Join(", ", unassigned.Select(e => e.Name)) + ".");

                var phaseData = new Dictionary<ThreePhasePhase, PhaseData>();
                foreach (var phase in new[] { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C })
                    phaseData[phase] = BuildPhase(phase);

                AddInputStep(result, phaseData);

                return _settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
                    ? SolveStar(result, phaseData)
                    : SolveDelta(result, phaseData);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
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

        private PhaseData BuildPhase(ThreePhasePhase phase)
        {
            var assigned = _elements.Where(e => e.PhaseAssignment == phase).ToList();
            var sources = assigned.Where(e => e.Type == ElementType.VoltageSource).ToList();
            if (sources.Count != 1)
                throw new InvalidOperationException(
                    $"Для фазы {phase} должен быть назначен ровно один источник ЭДС E. Сейчас: {sources.Count}.");

            if (assigned.Any(e => e.Type == ElementType.CurrentSource))
                throw new InvalidOperationException(
                    $"В базовом 3Φ-режиме источник тока в фазе {phase} пока не поддерживается. Используйте фазный источник ЭДС.");

            var load = assigned.Where(e => e.Type is ElementType.Resistor or ElementType.Inductor or ElementType.Capacitor).ToList();
            if (load.Count == 0)
                throw new InvalidOperationException(
                    $"Для ветви {PhaseArmName(phase)} не назначено ни одного R/L/C элемента.");

            Complex z = Complex.Zero;
            foreach (var e in load)
            {
                if (e.Value < 0)
                    throw new InvalidOperationException($"Значение {e.Name} не может быть отрицательным.");
                z += e.Type switch
                {
                    ElementType.Resistor => new Complex(e.Value, 0),
                    ElementType.Inductor => new Complex(0, _settings.AngularFrequency * e.Value),
                    ElementType.Capacitor when e.Value > Eps => new Complex(0, -1.0 / (_settings.AngularFrequency * e.Value)),
                    ElementType.Capacitor => throw new InvalidOperationException($"Ёмкость {e.Name} должна быть больше нуля."),
                    _ => Complex.Zero
                };
            }

            // На первом 3Φ-этапе фазные источники считаются идеальными. Линейные
            // импедансы R/L/C задаются отдельными элементами нагрузки/линии; смешивать
            // их со скрытым InternalResistance источника нельзя, особенно для Δ.
            if (sources[0].InternalResistance > Eps)
                throw new InvalidOperationException(
                    $"В 3Φ-режиме внутреннее сопротивление источника {sources[0].Name} пока должно быть 0 Ом. " +
                    "Сопротивления задавайте явными R/L/C элементами фазы.");
            if (z.Magnitude <= Eps)
                throw new InvalidOperationException($"Полное сопротивление ветви {PhaseArmName(phase)} равно нулю.");

            // В 3Φ-режиме порт A источника считается фазным выводом, порт B — нейтральным.
            Complex ePhasor = sources[0].SourcePhasor(_settings) * (sources[0].IsPositiveAtStart ? 1.0 : -1.0);

            return new PhaseData { Phase = phase, Source = sources[0], Loads = load, E = ePhasor, Z = z };
        }

        private CalculationResult SolveStar(CalculationResult result, Dictionary<ThreePhasePhase, PhaseData> p)
        {
            Complex ea = p[ThreePhasePhase.A].E, eb = p[ThreePhasePhase.B].E, ec = p[ThreePhasePhase.C].E;
            Complex za = p[ThreePhasePhase.A].Z, zb = p[ThreePhasePhase.B].Z, zc = p[ThreePhasePhase.C].Z;
            Complex ya = 1.0 / za, yb = 1.0 / zb, yc = 1.0 / zc;

            Complex un = _settings.ThreePhaseHasNeutral
                ? Complex.Zero
                : (ea * ya + eb * yb + ec * yc) / (ya + yb + yc);

            Complex ua = ea - un, ub = eb - un, uc = ec - un;
            Complex ia = ua / za, ib = ub / zb, ic = uc / zc;
            Complex inCurrent = -(ia + ib + ic);

            var sb = new StringBuilder();
            if (_settings.ThreePhaseHasNeutral)
            {
                sb.AppendLine("  Нулевая точка нагрузки соединена с нейтралью источника: U̲N=0.");
                sb.AppendLine("  I̲A=E̲A/Z̲A; I̲B=E̲B/Z̲B; I̲C=E̲C/Z̲C.");
            }
            else
            {
                sb.AppendLine("  Нулевая точка нагрузки плавающая. Напряжение смещения нейтрали:");
                sb.AppendLine("  U̲N=(E̲A/Z̲A + E̲B/Z̲B + E̲C/Z̲C)/(1/Z̲A + 1/Z̲B + 1/Z̲C).");
                sb.AppendLine($"  U̲N = {Fmt(un, "В")}");
            }
            sb.AppendLine();
            sb.AppendLine($"  U̲AN = {Fmt(ua, "В")}    I̲A = {Fmt(ia, "А")}");
            sb.AppendLine($"  U̲BN = {Fmt(ub, "В")}    I̲B = {Fmt(ib, "А")}");
            sb.AppendLine($"  U̲CN = {Fmt(uc, "В")}    I̲C = {Fmt(ic, "А")}");
            if (_settings.ThreePhaseHasNeutral)
                sb.AppendLine($"  I̲N = −(I̲A+I̲B+I̲C) = {Fmt(inCurrent, "А")}");
            else
                sb.AppendLine($"  Контроль: I̲A+I̲B+I̲C = {Fmt(ia + ib + ic, "А")}");

            result.Steps.Add(new SolutionStep
            {
                Title = "2. Расчёт звезды",
                Description = _settings.ThreePhaseHasNeutral
                    ? "Фазные ветви независимы; нулевой провод переносит ток несимметрии."
                    : "Для несимметричной звезды без нулевого провода сначала определяется смещение нейтрали нагрузки.",
                MatrixText = sb.ToString()
            });

            AddStarResults(result, p, new[] { ua, ub, uc }, new[] { ia, ib, ic });
            AddPowerAndWaveformSteps(result, p, new[] { ua, ub, uc }, new[] { ia, ib, ic }, new[] { ia, ib, ic }, inCurrent);
            result.Success = true;
            return result;
        }

        private CalculationResult SolveDelta(CalculationResult result, Dictionary<ThreePhasePhase, PhaseData> p)
        {
            Complex ea = p[ThreePhasePhase.A].E, eb = p[ThreePhasePhase.B].E, ec = p[ThreePhasePhase.C].E;
            Complex uab = ea - eb, ubc = eb - ec, uca = ec - ea;
            Complex iab = uab / p[ThreePhasePhase.A].Z;
            Complex ibc = ubc / p[ThreePhasePhase.B].Z;
            Complex ica = uca / p[ThreePhasePhase.C].Z;

            // Направления ветвей: AB, BC, CA.
            Complex ia = iab - ica;
            Complex ib = ibc - iab;
            Complex ic = ica - ibc;

            var sb = new StringBuilder();
            sb.AppendLine("  Ветви нагрузки: A→AB, B→BC, C→CA.");
            sb.AppendLine("  U̲AB=E̲A−E̲B; U̲BC=E̲B−E̲C; U̲CA=E̲C−E̲A.");
            sb.AppendLine("  I̲AB=U̲AB/Z̲AB; I̲BC=U̲BC/Z̲BC; I̲CA=U̲CA/Z̲CA.");
            sb.AppendLine("  Линейные токи: I̲A=I̲AB−I̲CA; I̲B=I̲BC−I̲AB; I̲C=I̲CA−I̲BC.");
            sb.AppendLine();
            sb.AppendLine($"  U̲AB = {Fmt(uab, "В")}    I̲AB = {Fmt(iab, "А")}");
            sb.AppendLine($"  U̲BC = {Fmt(ubc, "В")}    I̲BC = {Fmt(ibc, "А")}");
            sb.AppendLine($"  U̲CA = {Fmt(uca, "В")}    I̲CA = {Fmt(ica, "А")}");
            sb.AppendLine();
            sb.AppendLine($"  I̲A = {Fmt(ia, "А")}");
            sb.AppendLine($"  I̲B = {Fmt(ib, "А")}");
            sb.AppendLine($"  I̲C = {Fmt(ic, "А")}");
            sb.AppendLine($"  Контроль: I̲A+I̲B+I̲C = {Fmt(ia + ib + ic, "А")}");

            result.Steps.Add(new SolutionStep
            {
                Title = "2. Расчёт треугольника",
                Description = "Сначала определяются токи ветвей треугольника по линейным напряжениям, затем — линейные токи источника.",
                MatrixText = sb.ToString()
            });

            AddDeltaResults(result, p,
                new[] { uab, ubc, uca }, new[] { iab, ibc, ica },
                new[] { ia, ib, ic });
            AddPowerAndWaveformSteps(result, p,
                new[] { uab, ubc, uca }, new[] { iab, ibc, ica },
                new[] { ia, ib, ic }, Complex.Zero);
            result.Success = true;
            return result;
        }

        private void AddInputStep(CalculationResult result, Dictionary<ThreePhasePhase, PhaseData> p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Режим: {_settings}");
            sb.AppendLine($"  ω = 2πf = {_settings.AngularFrequency.ToString("0.######", CultureInfo.InvariantCulture)} рад/с");
            sb.AppendLine();
            foreach (var phase in new[] { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C })
            {
                var d = p[phase];
                sb.AppendLine($"  {PhaseArmName(phase)}: {string.Join(" + ", d.Loads.Select(e => e.Name))}");
                sb.AppendLine($"    E̲{phase} = {Fmt(d.E, "В")}");
                sb.AppendLine($"    Z̲{PhaseImpedanceSuffix(phase)} = {Fmt(d.Z, "Ом")}");
            }
            result.Steps.Add(new SolutionStep
            {
                Title = "1. Формирование трёхфазной расчётной схемы",
                Description = "Каждая ветвь может содержать любое количество последовательно назначенных R/L/C; их импедансы суммируются.",
                MatrixText = sb.ToString()
            });
        }

        private void AddStarResults(CalculationResult result, Dictionary<ThreePhasePhase, PhaseData> p, Complex[] u, Complex[] i)
        {
            var phases = new[] { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C };
            for (int k = 0; k < 3; k++)
                result.BranchResults.Add(CreateSyntheticLoadResult($"{phases[k]}N", p[phases[k]], u[k], i[k]));
            for (int k = 0; k < 3; k++)
            {
                var d = p[phases[k]];
                var b = SyntheticBranch($"Линия {phases[k]}", new[] { d.Source });
                result.BranchResults.Add(new BranchResult
                {
                    Branch = b,
                    CurrentPhasor = i[k],
                    VoltagePhasor = d.E,
                    TerminalComplexPower = d.E * Complex.Conjugate(i[k])
                });
            }
        }

        private void AddDeltaResults(CalculationResult result, Dictionary<ThreePhasePhase, PhaseData> p,
            Complex[] uBranch, Complex[] iBranch, Complex[] iLine)
        {
            var phases = new[] { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C };
            var names = new[] { "AB", "BC", "CA" };
            for (int k = 0; k < 3; k++)
                result.BranchResults.Add(CreateSyntheticLoadResult(names[k], p[phases[k]], uBranch[k], iBranch[k]));

            // Линейные токи источников выводим отдельными синтетическими строками.
            for (int k = 0; k < 3; k++)
            {
                var d = p[phases[k]];
                var b = SyntheticBranch($"Линия {phases[k]}", new[] { d.Source });
                result.BranchResults.Add(new BranchResult
                {
                    Branch = b,
                    CurrentPhasor = iLine[k],
                    VoltagePhasor = d.E,
                    TerminalComplexPower = d.E * Complex.Conjugate(iLine[k])
                });
            }
        }

        private BranchResult CreateSyntheticLoadResult(string name, PhaseData d, Complex u, Complex i)
        {
            var b = SyntheticBranch(name, d.Loads);
            Complex s = u * Complex.Conjugate(i);
            return new BranchResult
            {
                Branch = b,
                CurrentPhasor = i,
                VoltagePhasor = u,
                PassiveComplexPower = s,
                TerminalComplexPower = s
            };
        }

        private static CircuitBranch SyntheticBranch(string label, IEnumerable<CircuitElement> elements)
        {
            var b = new CircuitBranch
            {
                StartNode = new CircuitNode { Label = label, Position = new Point() },
                EndNode = new CircuitNode { Label = "", Position = new Point() }
            };
            foreach (var e in elements) b.AddElement(e, 1);
            return b;
        }

        private void AddPowerAndWaveformSteps(
            CalculationResult result,
            Dictionary<ThreePhasePhase, PhaseData> p,
            Complex[] loadVoltages,
            Complex[] loadBranchCurrents,
            Complex[] lineCurrents,
            Complex neutralCurrent)
        {
            var phases = new[] { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C };
            Complex sLoad = Complex.Zero;
            for (int k = 0; k < 3; k++) sLoad += loadVoltages[k] * Complex.Conjugate(loadBranchCurrents[k]);
            Complex sSource = Complex.Zero;
            for (int k = 0; k < 3; k++) sSource += p[phases[k]].E * Complex.Conjugate(lineCurrents[k]);

            result.TotalComplexPowerConsumed = sLoad;
            result.TotalComplexPowerGenerated = sSource;

            var sb = new StringBuilder();
            sb.AppendLine("  S̲ = U̲·I̲* = P + jQ");
            sb.AppendLine($"  ΣS̲наг = {Fmt(sLoad, "ВА")}");
            sb.AppendLine($"    Pнаг = {sLoad.Real.ToString("0.######", CultureInfo.InvariantCulture)} Вт");
            sb.AppendLine($"    Qнаг = {sLoad.Imaginary.ToString("0.######", CultureInfo.InvariantCulture)} вар");
            sb.AppendLine($"    |Sнаг| = {sLoad.Magnitude.ToString("0.######", CultureInfo.InvariantCulture)} ВА");
            sb.AppendLine($"  ΣS̲ист = {Fmt(sSource, "ВА")}");
            sb.AppendLine($"  |ΔS| = {(sSource - sLoad).Magnitude.ToString("0.###E+0", CultureInfo.InvariantCulture)} ВА");
            result.Steps.Add(new SolutionStep
            {
                Title = "3. Мощности трёхфазной цепи",
                Description = "Комплексная мощность рассчитывается без предположения о симметрии нагрузки.",
                MatrixText = sb.ToString()
            });

            var wf = new StringBuilder();
            double w = _settings.AngularFrequency;
            wf.AppendLine("  x(t)=√2·|X̲|·sin(ωt+φ), значения фазоров — RMS.");
            for (int k = 0; k < 3; k++)
            {
                string label = phases[k].ToString();
                wf.AppendLine($"  e{label}(t) = {InstantaneousWaveformFormatter.Function(p[phases[k]].E, w, "В")}");
                wf.AppendLine($"  i{label}(t) = {InstantaneousWaveformFormatter.Function(lineCurrents[k], w, "А")}");
            }
            if (_settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star && _settings.ThreePhaseHasNeutral)
                wf.AppendLine($"  iN(t) = {InstantaneousWaveformFormatter.Function(neutralCurrent, w, "А")}");

            result.Steps.Add(new SolutionStep
            {
                Title = "4. Мгновенные функции",
                Description = "Восстановление временных функций фазных ЭДС и линейных токов из RMS-фазоров.",
                MatrixText = wf.ToString()
            });
        }

        private string PhaseArmName(ThreePhasePhase p) => _settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
            ? $"ветвь {p}N"
            : p switch
            {
                ThreePhasePhase.A => "ветвь AB",
                ThreePhasePhase.B => "ветвь BC",
                ThreePhasePhase.C => "ветвь CA",
                _ => "ветвь"
            };

        private string PhaseImpedanceSuffix(ThreePhasePhase p) => _settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star
            ? p.ToString()
            : p switch
            {
                ThreePhasePhase.A => "AB",
                ThreePhasePhase.B => "BC",
                ThreePhasePhase.C => "CA",
                _ => ""
            };

        private static string Fmt(Complex x, string unit) =>
            $"{Phasor.Rectangular(x, "0.######")} {unit} = {Phasor.Exponential(x, "0.######", "0.##")} {unit}";
    }
}
