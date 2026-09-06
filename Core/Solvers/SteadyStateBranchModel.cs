using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    internal enum SteadyStateBranchKind
    {
        Open,
        Impedance,
        IdealVoltage,
        IdealCurrent
    }

    /// <summary>
    /// Универсальный эквивалент ветви для установившегося режима.
    /// DC является частным случаем комплексной модели:
    ///   R -> R+j0, L -> 0, C -> разрыв, источники имеют фазу 0.
    /// AC использует RMS-фазоры и Z_R, Z_L=jωL, Z_C=1/(jωC).
    /// Соглашение направления: U = Vstart - Vend = Z*I + E.
    /// </summary>
    internal sealed class SteadyStateBranchModel
    {
        private const double Eps = 1e-12;

        public CircuitBranch Branch { get; }
        public CircuitAnalysisSettings Settings { get; }
        public SteadyStateBranchKind Kind { get; }
        public Complex Impedance { get; }
        public Complex Emf { get; }
        public Complex PrescribedCurrent { get; }

        private SteadyStateBranchModel(
            CircuitBranch branch,
            CircuitAnalysisSettings settings,
            SteadyStateBranchKind kind,
            Complex impedance,
            Complex emf,
            Complex prescribedCurrent)
        {
            Branch = branch;
            Settings = settings;
            Kind = kind;
            Impedance = impedance;
            Emf = emf;
            PrescribedCurrent = prescribedCurrent;
        }

        public static SteadyStateBranchModel Create(CircuitBranch branch, CircuitAnalysisSettings settings)
        {
            settings.Validate();

            if (settings.Mode == CircuitAnalysisMode.AC)
            {
                var negativeSource = branch.Elements.FirstOrDefault(e =>
                    e.Type is (ElementType.VoltageSource or ElementType.CurrentSource) && e.Value < 0);
                if (negativeSource != null)
                    throw new InvalidOperationException(
                        $"В AC-режиме действующее значение источника {negativeSource.Name} не может быть отрицательным. Используйте положительное RMS и фазу/полярность.");
            }

            if (branch.Elements.Count == 0)
                return new SteadyStateBranchModel(branch, settings, SteadyStateBranchKind.IdealVoltage,
                    Complex.Zero, Complex.Zero, Complex.Zero);

            if (settings.Mode == CircuitAnalysisMode.DC && branch.HasCapacitor)
                return new SteadyStateBranchModel(branch, settings, SteadyStateBranchKind.Open,
                    Complex.Zero, TotalEmf(branch, settings), Complex.Zero);

            Complex z = TotalImpedance(branch, settings);
            Complex emf = TotalEmf(branch, settings);
            var sourceCurrents = OrientedCurrentSources(branch, settings).ToList();

            if (sourceCurrents.Count > 0)
            {
                Complex i0 = sourceCurrents[0];
                if (sourceCurrents.Any(i => !Phasor.NearlyEqual(i, i0)))
                    throw new InvalidOperationException(
                        $"В ветви {branch} последовательно включены идеальные источники тока с различными действующими значениями/фазами. Такая модель несовместима.");

                return new SteadyStateBranchModel(branch, settings, SteadyStateBranchKind.IdealCurrent,
                    z, emf, i0);
            }

            if (z.Magnitude > Eps)
                return new SteadyStateBranchModel(branch, settings, SteadyStateBranchKind.Impedance,
                    z, emf, Complex.Zero);

            return new SteadyStateBranchModel(branch, settings, SteadyStateBranchKind.IdealVoltage,
                Complex.Zero, emf, Complex.Zero);
        }

        public Complex CurrentFromVoltage(Complex voltage) => Kind switch
        {
            SteadyStateBranchKind.Open => Complex.Zero,
            SteadyStateBranchKind.Impedance => (voltage - Emf) / Impedance,
            SteadyStateBranchKind.IdealCurrent => PrescribedCurrent,
            _ => throw new InvalidOperationException("Ток идеальной ветви напряжения определяется дополнительной MNA-переменной.")
        };

        public Complex VoltageFromCurrent(Complex current) => Kind switch
        {
            SteadyStateBranchKind.Open => new Complex(double.NaN, double.NaN),
            SteadyStateBranchKind.Impedance => Impedance * current + Emf,
            SteadyStateBranchKind.IdealVoltage => Emf,
            _ => throw new InvalidOperationException("Напряжение идеального источника тока определяется из системы.")
        };

        public static Complex TotalImpedance(CircuitBranch branch, CircuitAnalysisSettings settings)
        {
            Complex z = Complex.Zero;
            double w = settings.AngularFrequency;

            foreach (var e in branch.Elements)
            {
                switch (e.Type)
                {
                    case ElementType.Resistor:
                        z += Math.Max(0.0, e.Value);
                        break;
                    case ElementType.VoltageSource:
                    case ElementType.CurrentSource:
                        z += Math.Max(0.0, e.InternalResistance);
                        break;
                    case ElementType.Inductor:
                        if (settings.Mode == CircuitAnalysisMode.AC)
                            z += Complex.ImaginaryOne * w * Math.Max(0.0, e.Value);
                        break;
                    case ElementType.Capacitor:
                        if (settings.Mode == CircuitAnalysisMode.AC)
                        {
                            double c = Math.Max(0.0, e.Value);
                            if (c <= Eps)
                                throw new InvalidOperationException($"Ёмкость {e.Name} должна быть больше 0 Ф в AC-режиме.");
                            z += Complex.One / (Complex.ImaginaryOne * w * c);
                        }
                        break;
                }
            }
            return z;
        }

        public static Complex TotalEmf(CircuitBranch branch, CircuitAnalysisSettings settings)
        {
            Complex total = Complex.Zero;
            foreach (var e in branch.Elements.Where(e => e.Type == ElementType.VoltageSource))
            {
                int orientation = branch.GetElementDirection(e);
                int polarity = e.IsPositiveAtStart ? 1 : -1;
                total += orientation * polarity * e.SourcePhasor(settings);
            }
            return total;
        }

        public static IEnumerable<Complex> OrientedCurrentSources(CircuitBranch branch, CircuitAnalysisSettings settings) =>
            branch.Elements
                .Where(e => e.Type == ElementType.CurrentSource)
                .Select(e => branch.GetElementDirection(e) * (e.IsPositiveAtStart ? 1.0 : -1.0) * e.SourcePhasor(settings));

        public string SymbolicImpedance()
        {
            var terms = new List<string>();
            foreach (var e in Branch.Elements)
            {
                switch (e.Type)
                {
                    case ElementType.Resistor:
                        terms.Add(e.Name);
                        break;
                    case ElementType.VoltageSource when e.InternalResistance > Eps:
                    case ElementType.CurrentSource when e.InternalResistance > Eps:
                        terms.Add($"r_{e.Name}");
                        break;
                    case ElementType.Inductor when Settings.Mode == CircuitAnalysisMode.AC:
                        terms.Add($"jω{e.Name}");
                        break;
                    case ElementType.Capacitor when Settings.Mode == CircuitAnalysisMode.AC:
                        terms.Add($"1/(jω{e.Name})");
                        break;
                }
            }
            return terms.Count == 0 ? "0" : string.Join(" + ", terms);
        }
    }
}
