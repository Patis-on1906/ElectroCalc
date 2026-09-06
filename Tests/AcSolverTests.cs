using System.Numerics;
using System.Windows;
using ElectroCalc.Core.Models;
using ElectroCalc.Core.Solvers;
using Xunit;

namespace ElectroCalc.Tests;

public class AcSolverTests
{
    // Expected values use independent textbook equations, never solver internals.
    private static CircuitAnalysisSettings Ac(double f = 50) => new() { Mode = CircuitAnalysisMode.AC, FrequencyHz = f };
    private static CircuitElement E(ElementType type, double value, double phase = 0, double r = 0) =>
        new() { Type = type, Name = type.ToString(), Value = value, PhaseDegrees = phase, InternalResistance = r };
    private static CircuitGraph Graph(int count = 2)
    {
        var g = new CircuitGraph();
        for (int n = 0; n < count; n++) g.AddNode(n.ToString(), new Point(n * 100, 0));
        return g;
    }
    private static CircuitBranch Branch(CircuitGraph g, int from, int to, params CircuitElement[] elements)
    {
        var b = g.AddBranch(g.Nodes[from], g.Nodes[to]);
        foreach (var e in elements) b.AddElement(e);
        return b;
    }
    private static Complex Polar(double value, double degrees) =>
        value * new Complex(Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180));
    internal static void Near(Complex expected, Complex actual, double relative = 1e-9, double absolute = 1e-10)
    {
        Assert.True(double.IsFinite(actual.Real) && double.IsFinite(actual.Imaginary), $"Non-finite result: {actual}");
        Assert.True((expected - actual).Magnitude <= absolute + relative * expected.Magnitude,
            $"Expected {expected}; actual {actual}; error {(expected - actual).Magnitude}");
    }
    private static BranchResult Row(CalculationResult result, CircuitBranch branch) =>
        Assert.Single(result.BranchResults, r => r.Branch == branch);
    private static CalculationResult Solve(CircuitGraph g, bool mesh, CircuitAnalysisSettings? settings = null)
    {
        var result = mesh ? new MeshCurrentSolver(g, settings ?? Ac()).Solve() : new NodePotentialSolver(g, settings ?? Ac()).Solve();
        Healthy(g, result);
        return result;
    }
    private static void Healthy(CircuitGraph g, CalculationResult result)
    {
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(g.Branches.Count, result.BranchResults.Count);
        Assert.True(result.PowerBalanceAvailable);
        Assert.True(result.PowerBalanceOk, $"Power imbalance: {result.ComplexPowerImbalance}");
        Near(Complex.Zero, result.ComplexPowerImbalance);
        foreach (var node in g.Nodes)
        {
            Complex sum = Complex.Zero;
            foreach (var row in result.BranchResults)
            {
                if (row.Branch.StartNode == node) sum += row.CurrentPhasor;
                if (row.Branch.EndNode == node) sum -= row.CurrentPhasor;
            }
            Near(Complex.Zero, sum); // KCL, independent of how loops were selected
        }
        foreach (var row in result.BranchResults)
        {
            Near(row.Branch.StartNode.PotentialPhasor - row.Branch.EndNode.PotentialPhasor, row.VoltagePhasor);
            Near(row.VoltagePhasor * Complex.Conjugate(row.CurrentPhasor), row.TerminalComplexPower);
        }
    }

    public static IEnumerable<object[]> SeriesCases()
    {
        foreach (bool mesh in new[] { false, true })
        foreach (string kind in new[] { "R", "L", "C", "RL", "RC", "RLC", "resonance" })
            yield return new object[] { mesh, kind };
    }

    [Theory]
    [MemberData(nameof(SeriesCases))]
    public void SeriesLoadsMatchOhmsLawAndRmsPower(bool mesh, string kind)
    {
        // At 50 Hz: XL=4 Ω, XC=2 Ω (or 4 Ω at resonance).
        var g = Graph();
        var source = Branch(g, 0, 1, E(ElementType.VoltageSource, 10, 30));
        var load = Branch(g, 0, 1);
        double r = kind.Contains('R') || kind == "resonance" ? 3 : 0;
        double xl = kind.Contains('L') || kind == "resonance" ? 4 : 0;
        double xc = kind.Contains('C') ? 2 : kind == "resonance" ? 4 : 0;
        if (r > 0) load.AddElement(E(ElementType.Resistor, r));
        if (xl > 0) load.AddElement(E(ElementType.Inductor, xl / (100 * Math.PI)));
        if (xc > 0) load.AddElement(E(ElementType.Capacitor, 1 / (100 * Math.PI * xc)));
        Complex voltage = Polar(10, 30), z = new(r, xl - xc), current = voltage / z;
        var result = Solve(g, mesh);
        var row = Row(result, load);
        Near(current, row.CurrentPhasor);
        Near(voltage, row.VoltagePhasor);
        Near(-current, Row(result, source).CurrentPhasor);
        Near(current.Magnitude * current.Magnitude * z, row.PassiveComplexPower);
        Near(row.PassiveComplexPower, result.TotalComplexPowerGenerated);
        Near(Complex.Zero, row.SourceComplexPowerGenerated);
        Assert.Equal(voltage.Magnitude * current.Magnitude, row.ApparentPower, 8);
    }

    [Theory]
    [InlineData(false, 10)] [InlineData(true, 10)]
    [InlineData(false, 50)] [InlineData(true, 50)]
    [InlineData(false, 1000)] [InlineData(true, 1000)]
    public void FrequencyChangesReactance(bool mesh, double frequency)
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, 100));
        var load = Branch(g, 0, 1, E(ElementType.Resistor, 20), E(ElementType.Inductor, .1), E(ElementType.Capacitor, 1e-4));
        var result = Solve(g, mesh, Ac(frequency));
        double w = 2 * Math.PI * frequency;
        Near(100 / new Complex(20, w * .1 - 1 / (w * 1e-4)), Row(result, load).CurrentPhasor);
    }

    [Theory]
    [InlineData(false, 1, true)] [InlineData(true, 1, true)]
    [InlineData(false, -1, true)] [InlineData(true, -1, true)]
    [InlineData(false, 1, false)] [InlineData(true, 1, false)]
    [InlineData(false, -1, false)] [InlineData(true, -1, false)]
    public void VoltageSourceOrientationAndInternalResistance(bool mesh, int direction, bool positive)
    {
        var g = Graph();
        var e = E(ElementType.VoltageSource, 12, -45, 2);
        e.IsPositiveAtStart = positive;
        var feed = Branch(g, 0, 1); feed.AddElement(e, direction);
        var load = Branch(g, 0, 1, E(ElementType.Resistor, 4), E(ElementType.Inductor, 3 / (100 * Math.PI)));
        Complex emf = direction * (positive ? 1 : -1) * Polar(12, -45);
        Complex i = emf / new Complex(6, 3);
        var result = Solve(g, mesh);
        Near(i, Row(result, load).CurrentPhasor);
        Near(i * new Complex(4, 3), Row(result, feed).VoltagePhasor);
        Near(i.Magnitude * i.Magnitude * 2, Row(result, feed).PassiveComplexPower);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, -1)] [InlineData(true, -1)]
    public void CurrentSourceSetsCurrentAndAbsorbsOrGeneratesPower(bool mesh, int direction)
    {
        var g = Graph();
        var feed = Branch(g, 0, 1, E(ElementType.Resistor, 2), E(ElementType.VoltageSource, 3, 90));
        feed.AddElement(E(ElementType.CurrentSource, 2, 40), direction);
        var load = Branch(g, 0, 1, E(ElementType.Resistor, 5), E(ElementType.Inductor, 4 / (100 * Math.PI)));
        Complex j = direction * Polar(2, 40), voltage = -j * new Complex(5, 4);
        var result = Solve(g, mesh);
        Near(j, Row(result, feed).CurrentPhasor);
        Near(-j, Row(result, load).CurrentPhasor);
        Near(voltage, Row(result, feed).VoltagePhasor);
        Near(4 * new Complex(7, 4), result.TotalComplexPowerConsumed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MultiLoopBridgeMatchesAnalyticTwoByTwoSystem(bool mesh)
    {
        var g = Graph(4);
        Complex v = Polar(20, 25), z1 = new(3, 4), z2 = new(5, -2), z3 = 6, z4 = new(2, 3), z5 = 7;
        Branch(g, 0, 3, E(ElementType.VoltageSource, 20, 25));
        var b1 = Branch(g, 0, 1, E(ElementType.Resistor, 3), E(ElementType.Inductor, 4 / (100 * Math.PI)));
        var b2 = Branch(g, 1, 3, E(ElementType.Resistor, 5), E(ElementType.Capacitor, 1 / (200 * Math.PI)));
        var b3 = Branch(g, 0, 2, E(ElementType.Resistor, 6));
        var b4 = Branch(g, 2, 3, E(ElementType.Resistor, 2), E(ElementType.Inductor, 3 / (100 * Math.PI)));
        var b5 = Branch(g, 1, 2, E(ElementType.Resistor, 7));
        // Cramer's rule for the two midpoint potentials relative to node 3.
        Complex a = 1 / z1 + 1 / z2 + 1 / z5, d = 1 / z3 + 1 / z4 + 1 / z5, b = -1 / z5;
        Complex p = (v / z1 * d - b * v / z3) / (a * d - b * b);
        Complex q = (a * v / z3 - b * v / z1) / (a * d - b * b);
        var result = Solve(g, mesh);
        Near((v - p) / z1, Row(result, b1).CurrentPhasor);
        Near(p / z2, Row(result, b2).CurrentPhasor);
        Near((v - q) / z3, Row(result, b3).CurrentPhasor);
        Near(q / z4, Row(result, b4).CurrentPhasor);
        Near((p - q) / z5, Row(result, b5).CurrentPhasor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EtalonLabCircuitMatchesDocumentCurrentsVoltagesAndPower(bool mesh)
    {
        // Исходные данные и ожидаемые результаты взяты из эталонного документа
        // «АБс-426_Гапон_Бекренёв_ЛР2(1).docx», а не со снимка схемы.
        // В документе принято округлённое omega=314 рад/с.
        var settings = Ac(314.0 / (2 * Math.PI));
        var g = Graph(6); // индексы 0..5 соответствуют узлам 1..6 эталона

        CircuitElement Part(string name, ElementType type, double value, double phase = 0)
        {
            var element = E(type, value, phase);
            element.Name = name;
            return element;
        }

        var b1 = Branch(g, 4, 2, Part("R1", ElementType.Resistor, 50));
        var b2 = Branch(g, 5, 3,
            Part("R2", ElementType.Resistor, 60),
            Part("C2", ElementType.Capacitor, 15e-6));
        var b3 = Branch(g, 3, 2, Part("R3", ElementType.Resistor, 78));
        var b4 = Branch(g, 2, 1,
            Part("R4", ElementType.Resistor, 76),
            Part("C4", ElementType.Capacitor, 60e-6));
        var b5 = Branch(g, 3, 1,
            Part("R5", ElementType.Resistor, 25),
            Part("L5", ElementType.Inductor, 0.32));

        var e6 = Part("E6", ElementType.VoltageSource, 100, 75);
        e6.IsPositiveAtStart = false; // E6 направлена противоположно I6
        var b6 = Branch(g, 0, 4, e6,
            Part("R6", ElementType.Resistor, 45),
            Part("L6", ElementType.Inductor, 0.32));
        var b7 = Branch(g, 0, 1, Part("R7", ElementType.Resistor, 25));
        var b8 = Branch(g, 4, 5,
            Part("L8", ElementType.Inductor, 0.4),
            Part("R8", ElementType.Resistor, 45));

        // На вертикальном элементе порт A находится сверху (узел 2), B снизу
        // (узел 1). Эталонная стрелка направлена вверх, то есть B→A.
        var currentSource = Part("J", ElementType.CurrentSource, 5, 10);
        currentSource.IsPositiveAtStart = false;
        var sourceBranch = g.AddBranch(g.Nodes[0], g.Nodes[1]);
        sourceBranch.AddElement(currentSource, -1);

        var result = Solve(g, mesh, settings);

        static void EtalonNear(Complex expected, Complex actual, double tolerance = 0.0015) =>
            Assert.True((expected - actual).Magnitude <= tolerance,
                $"Etalon expected {expected}; actual {actual}; error {(expected - actual).Magnitude}");
        static void EtalonScalarNear(double expected, double actual, double tolerance = 0.0015) =>
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"Etalon expected {expected}; actual {actual}; error {Math.Abs(expected - actual)}");

        var branches = new[] { b1, b2, b3, b4, b5, b6, b7, b8 };
        var expectedCurrents = new[]
        {
            new Complex(-0.134533,  0.371170),
            new Complex(-0.078409,  0.223216),
            new Complex(-0.228842, -0.149723),
            new Complex(-0.363376,  0.221447),
            new Complex( 0.150433,  0.372940),
            new Complex(-0.212942,  0.594387),
            new Complex(-4.711096, -1.462628),
            new Complex(-0.078409,  0.223216)
        };
        var expectedPassiveVoltages = new[]
            { 19.739972, 52.198143, 21.330673, 39.447252, 41.638622, 69.512657, 123.323021, 31.565020 };
        var expectedPassivePowers = new[]
        {
            new Complex(  7.793330,   0.000000),
            new Complex(  3.358417, -11.883995),
            new Complex(  5.833303,   0.000000),
            new Complex( 13.762124,  -9.611495),
            new Complex(  4.042854,  16.249039),
            new Complex( 17.938808,  40.055366),
            new Complex(608.342701,   0.000000),
            new Complex(  2.518813,   7.030286)
        };

        for (int i = 0; i < branches.Length; i++)
        {
            var row = Row(result, branches[i]);
            EtalonNear(expectedCurrents[i], row.CurrentPhasor);
            EtalonScalarNear(expectedPassiveVoltages[i], row.PassiveVoltagePhasor.Magnitude);
            EtalonNear(expectedPassivePowers[i], row.PassiveComplexPower);
        }

        EtalonNear(Polar(5, 10), Row(result, sourceBranch).CurrentPhasor, 1e-9);
        EtalonScalarNear(51.902001, Row(result, b6).SourceComplexPowerGenerated.Real);
        EtalonScalarNear(611.688348, Row(result, sourceBranch).SourceComplexPowerGenerated.Real);
        EtalonNear(new Complex(663.590349, 41.839201), result.TotalComplexPowerConsumed);
        EtalonNear(result.TotalComplexPowerConsumed, result.TotalComplexPowerGenerated, 1e-9);

        // U6 эталона — напряжение на R6+L6, а не полное напряжение ветви с E6.
        EtalonScalarNear(69.512657, Row(result, b6).PassiveVoltagePhasor.Magnitude);
        EtalonScalarNear(131.855410, Row(result, b6).VoltagePhasor.Magnitude);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MultipleSourcesWithDifferentPhasesSuperpose(bool mesh)
    {
        var g = Graph();
        var e1 = Branch(g, 0, 1, E(ElementType.VoltageSource, 10, 0, 2));
        var e2 = Branch(g, 1, 0, E(ElementType.VoltageSource, 6, 90, 3));
        var load = Branch(g, 0, 1, E(ElementType.Resistor, 4));
        Complex u = (10.0 / 2 - new Complex(0, 6) / 3) / (1.0 / 2 + 1.0 / 3 + 1.0 / 4);
        var result = Solve(g, mesh);
        Near(u / 4, Row(result, load).CurrentPhasor);
        Near((u - 10) / 2, Row(result, e1).CurrentPhasor);
        Near((-u - new Complex(0, 6)) / 3, Row(result, e2).CurrentPhasor);
    }

    [Theory]
    [InlineData("MUP")] [InlineData("MKT")] [InlineData("MEG")] [InlineData("short")] [InlineData("vector")]
    public void AllAcNumericModesMatchParallelLoadFormula(string mode)
    {
        var g = Graph();
        var feed = Branch(g, 0, 1, E(ElementType.VoltageSource, 30, 20, 2));
        var l1 = Branch(g, 0, 1, E(ElementType.Resistor, 3), E(ElementType.Inductor, 4 / (100 * Math.PI)));
        var l2 = Branch(g, 1, 0, E(ElementType.Resistor, 5), E(ElementType.Capacitor, 1 / (200 * Math.PI)));
        Complex z1 = new(3, 4), z2 = new(5, -2), zp = z1 * z2 / (z1 + z2);
        Complex u = Polar(30, 20) * zp / (2 + zp);
        var settings = Ac();
        var result = mode switch
        {
            "MUP" => new NodePotentialSolver(g, settings).Solve(),
            "MKT" => new MeshCurrentSolver(g, settings).Solve(),
            "MEG" => new EquivalentGeneratorSolver(g, l1, settings).Solve(),
            "short" => new ThreeBranchSimplifiedSolver(g, settings, ThreeBranchSimplifiedDetector.Detect(g, settings)!).Solve(),
            _ => new VectorDiagramDataSolver(g, settings).Solve()
        };
        Healthy(g, result);
        Near(u / z1, Row(result, l1).CurrentPhasor);
        Near(-u / z2, Row(result, l2).CurrentPhasor);
        Near(-(u / z1 + u / z2), Row(result, feed).CurrentPhasor);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void ParallelResonanceHasFiniteOpposingLoadCurrents(double internalR)
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, 10, 0, internalR));
        var l = Branch(g, 0, 1, E(ElementType.Inductor, 1));
        var c = Branch(g, 0, 1, E(ElementType.Capacitor, 1));
        var settings = Ac(1 / (2 * Math.PI)); // omega = 1, ZL=j, ZC=-j
        var result = new ThreeBranchSimplifiedSolver(g, settings,
            ThreeBranchSimplifiedDetector.Detect(g, settings)!).Solve();
        Assert.True(result.Success, result.ErrorMessage);
        Near(new Complex(0, -10), Row(result, l).CurrentPhasor);
        Near(new Complex(0, 10), Row(result, c).CurrentPhasor);
        Healthy(g, result);
        Near(Complex.Zero, result.TotalComplexPowerConsumed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ShortCalculationHandlesCurrentFeedInEitherDirection(bool reversed)
    {
        var g = Graph();
        var feed = Branch(g, reversed ? 1 : 0, reversed ? 0 : 1, E(ElementType.CurrentSource, 2, 30, 1));
        var l1 = Branch(g, 0, 1, E(ElementType.Resistor, 3), E(ElementType.Inductor, 4 / (100 * Math.PI)));
        var l2 = Branch(g, 1, 0, E(ElementType.Resistor, 5), E(ElementType.Capacitor, 1 / (200 * Math.PI)));
        Complex z1 = new(3, 4), z2 = new(5, -2);
        Complex u = (reversed ? 1 : -1) * Polar(2, 30) * z1 * z2 / (z1 + z2);
        var settings = Ac();
        var result = new ThreeBranchSimplifiedSolver(g, settings, ThreeBranchSimplifiedDetector.Detect(g, settings)!).Solve();
        Healthy(g, result);
        Near(Polar(2, 30), Row(result, feed).CurrentPhasor);
        Near(u / z1, Row(result, l1).CurrentPhasor);
        Near(-u / z2, Row(result, l2).CurrentPhasor);
    }

    [Fact]
    public void ShortCalculationRejectsCurrentFedLosslessParallelResonance()
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.CurrentSource, 2));
        Branch(g, 0, 1, E(ElementType.Inductor, 1));
        Branch(g, 0, 1, E(ElementType.Capacitor, 1));
        var settings = Ac(1 / (2 * Math.PI));
        var result = new ThreeBranchSimplifiedSolver(g, settings, ThreeBranchSimplifiedDetector.Detect(g, settings)!).Solve();
        Assert.False(result.Success);
        Assert.Contains("резонанс", result.ErrorMessage);
    }

    [Fact]
    public void ShortDcCalculationStillMatchesParallelResistance()
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, 10, 0, 1));
        var l1 = Branch(g, 0, 1, E(ElementType.Resistor, 6));
        var l2 = Branch(g, 0, 1, E(ElementType.Resistor, 3));
        var settings = new CircuitAnalysisSettings();
        var result = new ThreeBranchSimplifiedSolver(g, settings, ThreeBranchSimplifiedDetector.Detect(g, settings)!).Solve();
        Healthy(g, result);
        Near(10.0 / 9, Row(result, l1).CurrentPhasor);
        Near(20.0 / 9, Row(result, l2).CurrentPhasor);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void TheveninAccountsForSourceInLoadAndZeroInputImpedance(double internalR)
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, 12, 30, internalR));
        var load = Branch(g, 0, 1, E(ElementType.VoltageSource, 4, -20), E(ElementType.Resistor, 3),
            E(ElementType.Capacitor, 1 / (200 * Math.PI)));
        var result = new EquivalentGeneratorSolver(g, load, Ac()).Solve();
        Healthy(g, result);
        Near((Polar(12, 30) - Polar(4, -20)) / new Complex(internalR + 3, -2), Row(result, load).CurrentPhasor);
    }

    [Theory]
    [InlineData(false, 1e-12)] [InlineData(true, 1e-12)]
    [InlineData(false, 1e-13)] [InlineData(true, 1e-13)]
    public void PositivePicofaradCapacitanceIsValid(bool mesh, double capacitance)
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, 10));
        var load = Branch(g, 0, 1, E(ElementType.Capacitor, capacitance));
        var result = Solve(g, mesh, Ac(1e6));
        Near(new Complex(0, 10 * 2 * Math.PI * 1e6 * capacitance), Row(result, load).CurrentPhasor, 1e-9, 1e-15);
    }

    [Theory]
    [InlineData(false, "frequency")] [InlineData(true, "frequency")]
    [InlineData(false, "capacitor")] [InlineData(true, "capacitor")]
    [InlineData(false, "negative RMS")] [InlineData(true, "negative RMS")]
    [InlineData(false, "conflicting E")] [InlineData(true, "conflicting E")]
    [InlineData(false, "conflicting J")] [InlineData(true, "conflicting J")]
    [InlineData(false, "disconnected")] [InlineData(true, "disconnected")]
    public void InvalidCircuitsReturnFailure(bool mesh, string invalid)
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, invalid == "negative RMS" ? -10 : 10));
        var load = Branch(g, 0, 1, E(ElementType.Resistor, 5));
        var settings = Ac();
        switch (invalid)
        {
            case "frequency": settings.FrequencyHz = 0; break;
            case "capacitor": load.AddElement(E(ElementType.Capacitor, 0)); break;
            case "conflicting E": Branch(g, 0, 1, E(ElementType.VoltageSource, 20)); break;
            case "conflicting J": load.AddElement(E(ElementType.CurrentSource, 2)); load.AddElement(E(ElementType.CurrentSource, 3)); break;
            case "disconnected": g.AddNode("isolated", new Point()); break;
        }
        var result = mesh ? new MeshCurrentSolver(g, settings).Solve() : new NodePotentialSolver(g, settings).Solve();
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ResistiveAcAtZeroPhaseAgreesWithDc(bool mesh)
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, 10, 0, 1));
        var load = Branch(g, 0, 1, E(ElementType.Resistor, 4));
        var ac = Solve(g, mesh);
        var dc = mesh ? new MeshCurrentSolver(g).Solve() : new NodePotentialSolver(g).Solve();
        Assert.True(dc.Success, dc.ErrorMessage);
        Near(2, Row(ac, load).CurrentPhasor);
        Near(Row(dc, load).CurrentPhasor, Row(ac, load).CurrentPhasor);
        Near(dc.TotalComplexPowerConsumed, ac.TotalComplexPowerConsumed);
    }

    [Fact]
    public void WaveformOutputUsesPeakAmplitudeAndSourcePhase()
    {
        var g = Graph();
        Branch(g, 0, 1, E(ElementType.VoltageSource, 10, 30));
        Branch(g, 0, 1, E(ElementType.Resistor, 5));
        string text = Solve(g, false).ToPlainText();
        Assert.Contains("14.142136·sin(314.159265·t + 30°) В", text);
        Assert.Contains("2.828427·sin(314.159265·t + 30°) А", text);
    }
}
