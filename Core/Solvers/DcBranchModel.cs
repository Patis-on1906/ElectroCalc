using System;
using System.Linq;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    internal enum DcBranchKind
    {
        Open,
        Resistive,
        IdealVoltage,
        IdealCurrent
    }

    /// <summary>
    /// DC-эквивалент ветви. C считается разрывом, L — коротким замыканием.
    /// Для обычной ветви используется соглашение:
    ///     U = Vstart - Vend = R * I + E.
    /// Для ветви с идеальным источником тока ток задаётся источником, а напряжение
    /// ветви является неизвестным.
    /// </summary>
    internal sealed class DcBranchModel
    {
        private const double Eps = 1e-12;

        public CircuitBranch Branch { get; }
        public DcBranchKind Kind { get; }
        public double Resistance { get; }
        public double Emf { get; }
        public double PrescribedCurrent { get; }

        private DcBranchModel(
            CircuitBranch branch,
            DcBranchKind kind,
            double resistance,
            double emf,
            double prescribedCurrent)
        {
            Branch = branch;
            Kind = kind;
            Resistance = resistance;
            Emf = emf;
            PrescribedCurrent = prescribedCurrent;
        }

        public static DcBranchModel Create(CircuitBranch branch)
        {
            if (branch.Elements.Count == 0)
                return new DcBranchModel(branch, DcBranchKind.IdealVoltage, 0, 0, 0);

            if (branch.HasCapacitor)
                return new DcBranchModel(branch, DcBranchKind.Open, 0, branch.TotalEMF, 0);

            var sourceCurrents = branch.OrientedCurrentSources.ToList();
            if (sourceCurrents.Count > 0)
            {
                double i0 = sourceCurrents[0];
                if (sourceCurrents.Any(i => Math.Abs(i - i0) > 1e-9))
                    throw new InvalidOperationException(
                        $"В ветви {branch} последовательно включены источники тока с разными токами. " +
                        "Такая идеальная модель несовместима.");

                return new DcBranchModel(
                    branch,
                    DcBranchKind.IdealCurrent,
                    branch.TotalResistance,
                    branch.TotalEMF,
                    i0);
            }

            double r = branch.TotalResistance;
            if (r > Eps)
                return new DcBranchModel(branch, DcBranchKind.Resistive, r, branch.TotalEMF, 0);

            // Идеальный источник напряжения, чистая индуктивность или нулевое R:
            // Vstart - Vend = E (для L и R=0 E=0, т.е. КЗ).
            return new DcBranchModel(branch, DcBranchKind.IdealVoltage, 0, branch.TotalEMF, 0);
        }

        public double CurrentFromVoltage(double voltage) => Kind switch
        {
            DcBranchKind.Open         => 0.0,
            DcBranchKind.Resistive    => (voltage - Emf) / Resistance,
            DcBranchKind.IdealCurrent => PrescribedCurrent,
            _ => throw new InvalidOperationException("Ток идеальной ветви напряжения определяется MNA-переменной.")
        };

        public double VoltageFromCurrent(double current) => Kind switch
        {
            DcBranchKind.Open         => double.NaN,
            DcBranchKind.Resistive    => Resistance * current + Emf,
            DcBranchKind.IdealVoltage => Emf,
            _ => throw new InvalidOperationException("Напряжение идеального источника тока определяется из системы МКТ.")
        };

        public static (double consumed, double generated) ComputePower(
            DcBranchModel model, double current, double voltage)
        {
            if (model.Kind == DcBranchKind.Open || double.IsNaN(voltage))
                return (0.0, 0.0);

            // Мощность пассивной нагрузки: только джоулевы потери в сопротивлениях.
            // Для R (включая внутренние сопротивления источников): P_R = I^2 * R >= 0.
            double resistorLoss = model.Resistance * current * current;

            // По пассивному соглашению U*I — суммарная мощность, поглощаемая всей ветвью.
            // После вычитания резистивных потерь остаётся знаковая мощность источников:
            // sourceAbsorbed > 0  -> источник поглощает энергию;
            // sourceAbsorbed < 0  -> источник генерирует энергию.
            double totalAbsorbed = voltage * current;
            double sourceAbsorbed = totalAbsorbed - resistorLoss;

            // В поле PowerGenerated храним ЗНАКОВУЮ генерируемую мощность источников:
            // > 0 генерация, < 0 поглощение. Тогда
            // ΣP_ист(знаковая генерация) = Σ(I^2 R).
            double sourceGenerated = -sourceAbsorbed;
            return (resistorLoss, sourceGenerated);
        }
    }
}
