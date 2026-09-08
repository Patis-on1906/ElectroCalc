using System.IO;
using System.Numerics;
using DocumentFormat.OpenXml.Packaging;
using ElectroCalc.Core.Export;
using ElectroCalc.Core.Models;
using ElectroCalc.Core.Solvers;
using Xunit;

namespace ElectroCalc.Tests;

public class ThreePhaseEmergencyAndUnitsTests
{
    [Theory]
    [InlineData(ElementType.Capacitor, "мФ", 2.2, 0.0022)]
    [InlineData(ElementType.Capacitor, "мкФ", 47, 0.000047)]
    [InlineData(ElementType.Capacitor, "нФ", 330, 0.000000330)]
    [InlineData(ElementType.Capacitor, "пФ", 22, 0.000000000022)]
    [InlineData(ElementType.Inductor, "мГн", 15, 0.015)]
    [InlineData(ElementType.Inductor, "мкГн", 470, 0.000470)]
    public void EngineeringUnitsConvertToAndFromSi(ElementType type, string symbol,
        double displayed, double expectedSi)
    {
        var unit = Assert.Single(ElementValueUnits.For(type), value => value.Symbol == symbol);
        Assert.Equal(expectedSi, unit.ToSi(displayed), 12);
        Assert.Equal(displayed, unit.FromSi(expectedSi), 10);
    }

    [Fact]
    public void PreferredUnitsAreMicrofaradsAndMillihenries()
    {
        Assert.Equal("мкФ", ElementValueUnits.DefaultFor(ElementType.Capacitor).Symbol);
        Assert.Equal("мГн", ElementValueUnits.DefaultFor(ElementType.Inductor).Symbol);
        Assert.Equal("47 мкФ", ElementValueUnits.Format(47e-6, ElementType.Capacitor));
        Assert.Equal("15 мГн", ElementValueUnits.Format(15e-3, ElementType.Inductor));
    }

    [Theory]
    [InlineData(ThreePhaseCircuitMode.StarWithNeutral, 3)]
    [InlineData(ThreePhaseCircuitMode.StarWithoutNeutral, 3)]
    [InlineData(ThreePhaseCircuitMode.Delta, 4)]
    public void NormalThreePhaseResultContainsVoltageAndCurrentDiagrams(
        ThreePhaseCircuitMode mode, int expectedCount)
    {
        var result = Solve(Definition(mode), new ThreePhaseFaultSettings());
        Assert.Equal(expectedCount, result.VectorDiagrams.Count);
        Assert.Contains(result.VectorDiagrams, diagram => diagram.Title.Contains("напряж", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.VectorDiagrams, diagram => diagram.Title.Contains("ток", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.VectorDiagrams, diagram => Assert.NotEmpty(diagram.Vectors));
    }

    [Theory]
    [InlineData(ThreePhaseCircuitMode.StarWithNeutral)]
    [InlineData(ThreePhaseCircuitMode.StarWithoutNeutral)]
    public void OpenStarPhaseHasZeroCurrentAndKeepsNormalReference(ThreePhaseCircuitMode mode)
    {
        var result = Solve(Definition(mode), Fault(ThreePhaseOperatingMode.OpenCircuit,
            ThreePhaseFaultLocation.LineB));

        Near(Complex.Zero, Row(result, "BN (ХХ)").CurrentPhasor);
        Assert.Equal(6, result.ReferenceBranchResults.Count);
        Assert.Equal(6, result.Steps.Count);
        Assert.Equal(6, result.VectorDiagrams.Count);
        Assert.True(result.PowerBalanceOk, result.ComplexPowerImbalance.ToString());
        if (mode == ThreePhaseCircuitMode.StarWithoutNeutral)
            Near(Complex.Zero, SumLineCurrents(result));
    }

    [Theory]
    [InlineData(ThreePhaseCircuitMode.StarWithNeutral)]
    [InlineData(ThreePhaseCircuitMode.StarWithoutNeutral)]
    public void ShortedStarPhaseUsesFaultResistance(ThreePhaseCircuitMode mode)
    {
        const double faultResistance = 0.5;
        var definition = Definition(mode, emf: 100);
        var result = Solve(definition, Fault(ThreePhaseOperatingMode.ShortCircuit,
            ThreePhaseFaultLocation.LineC, faultResistance));

        var faultRow = Row(result, "CN (КЗ)");
        if (mode == ThreePhaseCircuitMode.StarWithNeutral)
            Near(Polar(100 / faultResistance, 120), faultRow.CurrentPhasor);
        else
            Near(Complex.Zero, SumLineCurrents(result));
        Assert.True(result.TotalComplexPowerConsumed.Real > 0);
        Assert.True(result.PowerBalanceOk, result.ComplexPowerImbalance.ToString());
    }

    [Theory]
    [InlineData(ThreePhaseFaultLocation.BranchAB, "AB (ХХ)")]
    [InlineData(ThreePhaseFaultLocation.BranchBC, "BC (ХХ)")]
    [InlineData(ThreePhaseFaultLocation.BranchCA, "CA (ХХ)")]
    public void OpenDeltaBranchHasZeroBranchCurrent(ThreePhaseFaultLocation location, string label)
    {
        var result = Solve(Definition(ThreePhaseCircuitMode.Delta),
            Fault(ThreePhaseOperatingMode.OpenCircuit, location));
        Near(Complex.Zero, Row(result, label).CurrentPhasor);
        Near(Complex.Zero, SumLineCurrents(result));
        Assert.True(result.PowerBalanceOk, result.ComplexPowerImbalance.ToString());
    }

    [Theory]
    [InlineData(ThreePhaseFaultLocation.LineA, "Линия A")]
    [InlineData(ThreePhaseFaultLocation.LineB, "Линия B")]
    [InlineData(ThreePhaseFaultLocation.LineC, "Линия C")]
    public void OpenDeltaLineIsSolvedByKcl(ThreePhaseFaultLocation location, string label)
    {
        var result = Solve(Definition(ThreePhaseCircuitMode.Delta,
                new Complex(10, 0), new Complex(6, -8), new Complex(6, 8)),
            Fault(ThreePhaseOperatingMode.OpenCircuit, location));

        Near(Complex.Zero, Row(result, label).CurrentPhasor, absolute: 1e-8);
        Near(Complex.Zero, SumLineCurrents(result), absolute: 1e-8);
        Assert.True(result.PowerBalanceOk, result.ComplexPowerImbalance.ToString());
    }

    [Theory]
    [InlineData(ThreePhaseFaultLocation.BranchAB, "AB (КЗ)", 0)]
    [InlineData(ThreePhaseFaultLocation.BranchBC, "BC (КЗ)", 1)]
    [InlineData(ThreePhaseFaultLocation.BranchCA, "CA (КЗ)", 2)]
    public void ShortedDeltaBranchUsesLineVoltage(ThreePhaseFaultLocation location,
        string label, int branchIndex)
    {
        const double faultResistance = 0.25;
        var result = Solve(Definition(ThreePhaseCircuitMode.Delta, emf: 100),
            Fault(ThreePhaseOperatingMode.ShortCircuit, location, faultResistance));
        var row = Row(result, label);
        Near(row.VoltagePhasor / faultResistance, row.CurrentPhasor);
        Assert.Equal(branchIndex, Array.IndexOf(new[] { "AB (КЗ)", "BC (КЗ)", "CA (КЗ)" }, label));
        Near(Complex.Zero, SumLineCurrents(result), absolute: 1e-8);
        Assert.True(result.PowerBalanceOk, result.ComplexPowerImbalance.ToString());
    }

    [Theory]
    [InlineData(ThreePhaseFaultLocation.LineA, "КЗ A–N", 0)]
    [InlineData(ThreePhaseFaultLocation.LineB, "КЗ B–N", 1)]
    [InlineData(ThreePhaseFaultLocation.LineC, "КЗ C–N", 2)]
    public void DeltaPhaseToNeutralFaultAddsReturnCurrent(ThreePhaseFaultLocation location,
        string label, int phaseIndex)
    {
        const double faultResistance = 2;
        var result = Solve(Definition(ThreePhaseCircuitMode.Delta, emf: 100),
            Fault(ThreePhaseOperatingMode.ShortCircuit, location, faultResistance));
        var faultRow = Row(result, label);
        Near(Polar(100 / faultResistance, new[] { 0, -120, 120 }[phaseIndex]), faultRow.CurrentPhasor);
        Near(faultRow.CurrentPhasor, SumLineCurrents(result), absolute: 1e-8);
        Assert.True(result.PowerBalanceOk, result.ComplexPowerImbalance.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShortCircuitRejectsNonPositiveFaultResistance(double resistance)
    {
        var definition = Definition(ThreePhaseCircuitMode.StarWithNeutral);
        var result = new ThreePhaseCircuitSolver(definition.Elements, definition.Settings,
            Fault(ThreePhaseOperatingMode.ShortCircuit, ThreePhaseFaultLocation.LineA, resistance)).Solve();
        Assert.False(result.Success);
        Assert.Contains("сопротивление", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmergencyDocxContainsNormalAndFaultSections()
    {
        var result = Solve(Definition(ThreePhaseCircuitMode.Delta),
            Fault(ThreePhaseOperatingMode.OpenCircuit, ThreePhaseFaultLocation.BranchBC));
        string path = Path.Combine(Path.GetTempPath(), $"ElectroCalc_{Guid.NewGuid():N}.docx");
        try
        {
            WordExporter.Export(result, path);
            using var document = WordprocessingDocument.Open(path, false);
            string text = document.MainDocumentPart!.Document.InnerText;
            Assert.Contains("Нормальный режим до аварии", text);
            Assert.Contains("Аварийный режим", text);
            Assert.Contains("холостой ход", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Мощности аварийного режима", text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static ThreePhaseCircuitDefinition Definition(ThreePhaseCircuitMode mode,
        Complex? a = null, Complex? b = null, Complex? c = null, double emf = 100) =>
        ThreePhaseCircuitFactory.Create(new ThreePhaseCircuitInput
        {
            Mode = mode,
            FrequencyHz = 50,
            PhaseEmfRms = emf,
            BranchA = Branch(a ?? new Complex(10, 0)),
            BranchB = Branch(b ?? new Complex(10, 0)),
            BranchC = Branch(c ?? new Complex(10, 0))
        });

    private static ThreePhaseBranchInput Branch(Complex value) => new()
    {
        ResistanceOhms = value.Real,
        ReactanceOhms = value.Imaginary
    };

    private static ThreePhaseFaultSettings Fault(ThreePhaseOperatingMode mode,
        ThreePhaseFaultLocation location, double resistance = 0.01) => new()
    {
        OperatingMode = mode,
        Location = location,
        ShortCircuitResistanceOhms = resistance
    };

    private static CalculationResult Solve(ThreePhaseCircuitDefinition definition,
        ThreePhaseFaultSettings fault)
    {
        var result = new ThreePhaseCircuitSolver(definition.Elements, definition.Settings, fault).Solve();
        Assert.True(result.Success, result.ErrorMessage);
        return result;
    }

    private static BranchResult Row(CalculationResult result, string label) =>
        Assert.Single(result.BranchResults, row => row.Branch.StartNode.Label == label);

    private static Complex SumLineCurrents(CalculationResult result) =>
        result.BranchResults
            .Where(row => row.Branch.StartNode.Label.StartsWith("Линия ", StringComparison.Ordinal))
            .Aggregate(Complex.Zero, (sum, row) => sum + row.CurrentPhasor);

    private static Complex Polar(double magnitude, double degrees) =>
        Complex.FromPolarCoordinates(magnitude, degrees * Math.PI / 180.0);

    private static void Near(Complex expected, Complex actual,
        double relative = 1e-9, double absolute = 1e-9) =>
        Assert.True((expected - actual).Magnitude <= absolute + relative * expected.Magnitude,
            $"Ожидалось {expected}; получено {actual}");
}
