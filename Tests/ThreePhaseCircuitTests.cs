using System.Numerics;
using ElectroCalc.Core.Models;
using ElectroCalc.Core.Solvers;
using Xunit;

namespace ElectroCalc.Tests;

public class ThreePhaseCircuitTests
{
    private static readonly ThreePhasePhase[] Phases =
        { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C };

    private static ThreePhaseCircuitDefinition Definition(ThreePhaseCircuitMode mode,
        double emf, Complex a, Complex b, Complex c, double frequency = 50) =>
        ThreePhaseCircuitFactory.Create(new ThreePhaseCircuitInput
        {
            Mode = mode,
            FrequencyHz = frequency,
            PhaseEmfRms = emf,
            BranchA = Branch(a),
            BranchB = Branch(b),
            BranchC = Branch(c)
        });

    private static ThreePhaseBranchInput Branch(Complex z) => new()
    {
        ResistanceOhms = z.Real,
        ReactanceOhms = z.Imaginary
    };

    private static Complex Polar(double magnitude, double degrees) =>
        Complex.FromPolarCoordinates(magnitude, degrees * Math.PI / 180.0);

    private static void Near(Complex expected, Complex actual,
        double relative = 1e-9, double absolute = 1e-10)
    {
        Assert.True(double.IsFinite(actual.Real) && double.IsFinite(actual.Imaginary),
            $"Результат не является конечным: {actual}");
        Assert.True((expected - actual).Magnitude <= absolute + relative * expected.Magnitude,
            $"Ожидалось {expected}; получено {actual}; ошибка {(expected - actual).Magnitude}");
    }

    private static BranchResult Row(CalculationResult result, string label) =>
        Assert.Single(result.BranchResults, r => r.Branch.StartNode.Label == label);

    private static CalculationResult Solve(ThreePhaseCircuitDefinition definition)
    {
        var result = new ThreePhaseCircuitSolver(definition.Elements, definition.Settings).Solve();
        AssertHealthy(result);
        return result;
    }

    private static void AssertHealthy(CalculationResult result)
    {
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(CalculationMethod.ThreePhase, result.Method);
        Assert.Equal(6, result.BranchResults.Count);
        Assert.Equal(4, result.Steps.Count);
        Assert.True(result.PowerBalanceAvailable);
        Assert.True(result.PowerBalanceOk, $"Невязка мощности: {result.ComplexPowerImbalance}");
        Near(result.TotalComplexPowerConsumed, result.TotalComplexPowerGenerated);
        foreach (var row in result.BranchResults)
        {
            Assert.True(double.IsFinite(row.CurrentPhasor.Real));
            Assert.True(double.IsFinite(row.CurrentPhasor.Imaginary));
            Assert.True(double.IsFinite(row.VoltagePhasor.Real));
            Assert.True(double.IsFinite(row.VoltagePhasor.Imaginary));
            Near(row.CalculatedTerminalComplexPower, row.TerminalComplexPower);
        }
    }

    [Theory]
    [InlineData(ThreePhaseCircuitMode.StarWithNeutral, ThreePhaseLoadConnection.Star, true)]
    [InlineData(ThreePhaseCircuitMode.StarWithoutNeutral, ThreePhaseLoadConnection.Star, false)]
    [InlineData(ThreePhaseCircuitMode.Delta, ThreePhaseLoadConnection.Delta, false)]
    public void FactoryMapsEveryUiModeToSolverSettings(ThreePhaseCircuitMode mode,
        ThreePhaseLoadConnection connection, bool neutral)
    {
        var definition = Definition(mode, 220, 10, 10, 10);
        Assert.Equal(CircuitAnalysisMode.ThreePhase, definition.Settings.Mode);
        Assert.Equal(connection, definition.Settings.ThreePhaseConnection);
        Assert.Equal(neutral, definition.Settings.ThreePhaseHasNeutral);
    }

    [Fact]
    public void FactoryCreatesSymmetricSourcesAndConvertsReactanceToLOrC()
    {
        const double f = 50;
        var definition = Definition(ThreePhaseCircuitMode.StarWithNeutral, 230,
            new Complex(10, 4), new Complex(0, -5), new Complex(3, 0), f);

        var sources = definition.Elements.Where(e => e.Type == ElementType.VoltageSource)
            .OrderBy(e => e.PhaseAssignment).ToArray();
        Assert.Equal(3, sources.Length);
        Assert.All(sources, e => Assert.Equal(230, e.Value));
        Assert.Equal(new[] { 0.0, -120.0, 120.0 }, sources.Select(e => e.PhaseDegrees));
        Assert.Equal(Phases, sources.Select(e => e.PhaseAssignment));

        double w = 2 * Math.PI * f;
        var a = definition.Elements.Where(e => e.PhaseAssignment == ThreePhasePhase.A).ToList();
        Assert.Equal(10, Assert.Single(a, e => e.Type == ElementType.Resistor).Value);
        Assert.Equal(4 / w, Assert.Single(a, e => e.Type == ElementType.Inductor).Value, 12);
        var b = definition.Elements.Where(e => e.PhaseAssignment == ThreePhasePhase.B).ToList();
        Assert.Equal(1 / (w * 5), Assert.Single(b, e => e.Type == ElementType.Capacitor).Value, 12);
        Assert.DoesNotContain(b, e => e.Type == ElementType.Resistor);
        var c = definition.Elements.Where(e => e.PhaseAssignment == ThreePhasePhase.C).ToList();
        Assert.Equal(3, Assert.Single(c, e => e.Type == ElementType.Resistor).Value);
    }

    [Theory]
    [InlineData(ThreePhaseCircuitMode.StarWithNeutral)]
    [InlineData(ThreePhaseCircuitMode.StarWithoutNeutral)]
    public void BalancedStarMatchesPhaseOhmsLawWithAndWithoutNeutral(ThreePhaseCircuitMode mode)
    {
        var definition = Definition(mode, 230, 23, 23, 23);
        var result = Solve(definition);

        for (int k = 0; k < 3; k++)
        {
            Complex expectedVoltage = Polar(230, new[] { 0, -120, 120 }[k]);
            var row = Row(result, $"{Phases[k]}N");
            Near(expectedVoltage, row.VoltagePhasor);
            Near(expectedVoltage / 23.0, row.CurrentPhasor);
        }
        Near(new Complex(6900, 0), result.TotalComplexPowerConsumed);
    }

    [Fact]
    public void UnbalancedStarWithoutNeutralMatchesNeutralDisplacementExample()
    {
        var definition = Definition(ThreePhaseCircuitMode.StarWithoutNeutral, 20, 5, 20, 20);
        var result = Solve(definition);
        Complex ea = 20, eb = Polar(20, -120), ec = Polar(20, 120);
        Complex un = (ea / 5.0 + eb / 20.0 + ec / 20.0) / (1 / 5.0 + 1 / 20.0 + 1 / 20.0);

        Near(10, un);
        Near((ea - un) / 5.0, Row(result, "AN").CurrentPhasor);
        Near((eb - un) / 20.0, Row(result, "BN").CurrentPhasor);
        Near((ec - un) / 20.0, Row(result, "CN").CurrentPhasor);
        Near(Complex.Zero, Row(result, "AN").CurrentPhasor +
                           Row(result, "BN").CurrentPhasor +
                           Row(result, "CN").CurrentPhasor);
        Assert.Contains("U̲N = 10", result.Steps[1].MatrixText);
    }

    [Fact]
    public void UnbalancedStarWithNeutralKeepsPhaseVoltagesAndCarriesImbalance()
    {
        var definition = Definition(ThreePhaseCircuitMode.StarWithNeutral, 20, 5, 20, 20);
        var result = Solve(definition);
        Complex ia = 4, ib = Polar(1, -120), ic = Polar(1, 120);

        Near(ia, Row(result, "AN").CurrentPhasor);
        Near(ib, Row(result, "BN").CurrentPhasor);
        Near(ic, Row(result, "CN").CurrentPhasor);
        Near(-3, -(ia + ib + ic));
        Assert.Contains("I̲N", result.Steps[1].MatrixText);
    }

    [Fact]
    public void MixedRlcStarUsesEnteredComplexImpedancesAndFrequency()
    {
        var za = new Complex(30, 0);
        var zb = new Complex(0, -29.19);
        var zc = new Complex(25, 20.74);
        var definition = Definition(ThreePhaseCircuitMode.StarWithNeutral, 20, za, zb, zc, 50);
        var result = Solve(definition);

        Near(20 / za, Row(result, "AN").CurrentPhasor);
        Near(Polar(20, -120) / zb, Row(result, "BN").CurrentPhasor);
        Near(Polar(20, 120) / zc, Row(result, "CN").CurrentPhasor);
    }

    [Fact]
    public void BalancedDeltaMatchesLineVoltageAndLineCurrentRelations()
    {
        var definition = Definition(ThreePhaseCircuitMode.Delta, 20, 40, 40, 40);
        var result = Solve(definition);

        Near(Polar(20 * Math.Sqrt(3), 30), Row(result, "AB").VoltagePhasor);
        Near(Polar(20 * Math.Sqrt(3) / 40, 30), Row(result, "AB").CurrentPhasor);
        Near(Polar(1.5, 0), Row(result, "Линия A").CurrentPhasor);
        Near(Polar(1.5, -120), Row(result, "Линия B").CurrentPhasor);
        Near(Polar(1.5, 120), Row(result, "Линия C").CurrentPhasor);
        Near(new Complex(90, 0), result.TotalComplexPowerConsumed);
    }

    [Fact]
    public void UnbalancedDeltaMatchesIndependentBranchAndKclEquations()
    {
        double e = 380 / Math.Sqrt(3);
        Complex zab = 10, zbc = new(6, -8), zca = new(6, 8);
        var definition = Definition(ThreePhaseCircuitMode.Delta, e, zab, zbc, zca);
        var result = Solve(definition);
        Complex ea = e, eb = Polar(e, -120), ec = Polar(e, 120);
        Complex iab = (ea - eb) / zab;
        Complex ibc = (eb - ec) / zbc;
        Complex ica = (ec - ea) / zca;

        Near(iab, Row(result, "AB").CurrentPhasor);
        Near(ibc, Row(result, "BC").CurrentPhasor);
        Near(ica, Row(result, "CA").CurrentPhasor);
        Near(iab - ica, Row(result, "Линия A").CurrentPhasor);
        Near(ibc - iab, Row(result, "Линия B").CurrentPhasor);
        Near(ica - ibc, Row(result, "Линия C").CurrentPhasor);
        Near(Complex.Zero, Row(result, "Линия A").CurrentPhasor +
                           Row(result, "Линия B").CurrentPhasor +
                           Row(result, "Линия C").CurrentPhasor);
    }

    [Theory]
    [InlineData(ThreePhaseCircuitMode.StarWithNeutral, 2400, 1800)]
    [InlineData(ThreePhaseCircuitMode.StarWithoutNeutral, 2400, 1800)]
    [InlineData(ThreePhaseCircuitMode.Delta, 7200, 5400)]
    public void ComplexPowerMatchesIndependentRmsFormula(ThreePhaseCircuitMode mode,
        double expectedP, double expectedQ)
    {
        var definition = Definition(mode, 100, new Complex(8, 6), new Complex(8, 6), new Complex(8, 6));
        var result = Solve(definition);
        Near(new Complex(expectedP, expectedQ), result.TotalComplexPowerConsumed);
    }

    [Fact]
    public void VerySmallPositiveCapacitanceIsAccepted()
    {
        const double c = 1e-12;
        const double f = 1e6;
        var elements = BasicElements(10, 0, 10, 0, 10, 0);
        foreach (var phase in Phases)
        {
            var resistor = elements.Single(e => e.PhaseAssignment == phase && e.Type == ElementType.Resistor);
            elements.Remove(resistor);
            elements.Add(new CircuitElement
            {
                Type = ElementType.Capacitor,
                Name = $"C_{phase}",
                Value = c,
                PhaseAssignment = phase
            });
        }

        var result = new ThreePhaseCircuitSolver(elements, new CircuitAnalysisSettings
        {
            Mode = CircuitAnalysisMode.ThreePhase,
            FrequencyHz = f,
            ThreePhaseConnection = ThreePhaseLoadConnection.Star,
            ThreePhaseHasNeutral = true
        }).Solve();
        AssertHealthy(result);
        Near(new Complex(0, 2 * Math.PI * f * c * 10), Row(result, "AN").CurrentPhasor,
            relative: 1e-9, absolute: 1e-15);
    }

    [Fact]
    public void FactoryRejectsInvalidInputValues()
    {
        var valid = new ThreePhaseCircuitInput();

        var badFrequency = valid.Clone(); badFrequency.FrequencyHz = 0;
        Assert.Throws<InvalidOperationException>(() => ThreePhaseCircuitFactory.Create(badFrequency));
        var badEmf = valid.Clone(); badEmf.PhaseEmfRms = -1;
        Assert.Throws<InvalidOperationException>(() => ThreePhaseCircuitFactory.Create(badEmf));
        var badResistance = valid.Clone(); badResistance.BranchB.ResistanceOhms = -1;
        Assert.Throws<InvalidOperationException>(() => ThreePhaseCircuitFactory.Create(badResistance));
        var zeroBranch = valid.Clone(); zeroBranch.BranchC.ResistanceOhms = 0; zeroBranch.BranchC.ReactanceOhms = 0;
        Assert.Throws<InvalidOperationException>(() => ThreePhaseCircuitFactory.Create(zeroBranch));
        var nan = valid.Clone(); nan.BranchA.ReactanceOhms = double.NaN;
        Assert.Throws<InvalidOperationException>(() => ThreePhaseCircuitFactory.Create(nan));
        var infinity = valid.Clone(); infinity.PhaseEmfRms = double.PositiveInfinity;
        Assert.Throws<InvalidOperationException>(() => ThreePhaseCircuitFactory.Create(infinity));
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("missing-source")]
    [InlineData("duplicate-source")]
    [InlineData("current-source")]
    [InlineData("missing-load")]
    [InlineData("zero-capacitor")]
    [InlineData("negative-load")]
    [InlineData("nan-load")]
    [InlineData("negative-source")]
    [InlineData("nan-phase")]
    [InlineData("internal-resistance")]
    public void SolverRejectsEveryInvalidElementConfiguration(string fault)
    {
        var elements = BasicElements(10, 0, 10, -120, 10, 120);
        switch (fault)
        {
            case "unassigned":
                elements.Add(new CircuitElement { Type = ElementType.Resistor, Name = "R?", Value = 1 });
                break;
            case "missing-source":
                elements.RemoveAll(e => e.Type == ElementType.VoltageSource && e.PhaseAssignment == ThreePhasePhase.C);
                break;
            case "duplicate-source":
                elements.Add(Source("E_A2", 10, 0, ThreePhasePhase.A));
                break;
            case "current-source":
                elements.Add(new CircuitElement { Type = ElementType.CurrentSource, Name = "J_A", Value = 1, PhaseAssignment = ThreePhasePhase.A });
                break;
            case "missing-load":
                elements.RemoveAll(e => e.Type == ElementType.Resistor && e.PhaseAssignment == ThreePhasePhase.B);
                break;
            case "zero-capacitor":
                ReplaceLoad(elements, ThreePhasePhase.A, ElementType.Capacitor, 0);
                break;
            case "negative-load":
                elements.Single(e => e.Type == ElementType.Resistor && e.PhaseAssignment == ThreePhasePhase.A).Value = -1;
                break;
            case "nan-load":
                elements.Single(e => e.Type == ElementType.Resistor && e.PhaseAssignment == ThreePhasePhase.A).Value = double.NaN;
                break;
            case "negative-source":
                elements.Single(e => e.Type == ElementType.VoltageSource && e.PhaseAssignment == ThreePhasePhase.A).Value = -1;
                break;
            case "nan-phase":
                elements.Single(e => e.Type == ElementType.VoltageSource && e.PhaseAssignment == ThreePhasePhase.A).PhaseDegrees = double.NaN;
                break;
            case "internal-resistance":
                elements.Single(e => e.Type == ElementType.VoltageSource && e.PhaseAssignment == ThreePhasePhase.A).InternalResistance = 1;
                break;
        }

        var result = new ThreePhaseCircuitSolver(elements, new CircuitAnalysisSettings
        {
            Mode = CircuitAnalysisMode.ThreePhase,
            FrequencyHz = 50,
            ThreePhaseConnection = ThreePhaseLoadConnection.Star
        }).Solve();
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Single(result.Steps);
        Assert.Equal("Ошибка", result.Steps[0].Title);
    }

    [Fact]
    public void FloatingStarRejectsSingularTotalAdmittance()
    {
        // 1/(j1) + 1/(-j1) + 1/R tends to zero only with R=∞. Use three
        // non-zero impedances whose complex admittances cancel exactly.
        var elements = new List<CircuitElement>
        {
            Source("E_A", 10, 0, ThreePhasePhase.A),
            Source("E_B", 10, -120, ThreePhasePhase.B),
            Source("E_C", 10, 120, ThreePhasePhase.C),
            Load("R_A", ElementType.Resistor, 1, ThreePhasePhase.A),
            Load("R_B", ElementType.Resistor, 1, ThreePhasePhase.B),
            Load("R_C", ElementType.Resistor, 0.5, ThreePhasePhase.C)
        };
        // Negative resistance is invalid, therefore an exact cancellation is not
        // reachable with passive R/L/C branches. This assertion keeps the solver's
        // denominator guard covered by a lossless set: Z_A=j, Z_B=j, Z_C=-j/2.
        elements.RemoveRange(3, 3);
        double w = 1;
        elements.Add(Load("L_A", ElementType.Inductor, 1 / w, ThreePhasePhase.A));
        elements.Add(Load("L_B", ElementType.Inductor, 1 / w, ThreePhasePhase.B));
        elements.Add(Load("C_C", ElementType.Capacitor, 2 / w, ThreePhasePhase.C));

        var result = new ThreePhaseCircuitSolver(elements, new CircuitAnalysisSettings
        {
            Mode = CircuitAnalysisMode.ThreePhase,
            FrequencyHz = 1 / (2 * Math.PI),
            ThreePhaseConnection = ThreePhaseLoadConnection.Star,
            ThreePhaseHasNeutral = false
        }).Solve();
        Assert.False(result.Success);
        Assert.Contains("сумма проводимостей", result.ErrorMessage);
    }

    private static List<CircuitElement> BasicElements(double eA, double pA,
        double eB, double pB, double eC, double pC) => new()
    {
        Source("E_A", eA, pA, ThreePhasePhase.A),
        Source("E_B", eB, pB, ThreePhasePhase.B),
        Source("E_C", eC, pC, ThreePhasePhase.C),
        Load("R_A", ElementType.Resistor, 5, ThreePhasePhase.A),
        Load("R_B", ElementType.Resistor, 5, ThreePhasePhase.B),
        Load("R_C", ElementType.Resistor, 5, ThreePhasePhase.C)
    };

    private static CircuitElement Source(string name, double value, double phase, ThreePhasePhase assignment) => new()
    {
        Type = ElementType.VoltageSource,
        Name = name,
        Value = value,
        PhaseDegrees = phase,
        PhaseAssignment = assignment,
        IsPositiveAtStart = true
    };

    private static CircuitElement Load(string name, ElementType type, double value, ThreePhasePhase assignment) => new()
    {
        Type = type,
        Name = name,
        Value = value,
        PhaseAssignment = assignment
    };

    private static void ReplaceLoad(List<CircuitElement> elements, ThreePhasePhase phase,
        ElementType type, double value)
    {
        elements.RemoveAll(e =>
            (e.Type is ElementType.Resistor or ElementType.Inductor or ElementType.Capacitor) &&
            e.PhaseAssignment == phase);
        elements.Add(Load($"{type}_{phase}", type, value, phase));
    }
}
