using System.Reflection;
using System.IO;
using Path = System.IO.Path;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ElectroCalc.Core.Models;
using ElectroCalc.Core.Persistence;
using ElectroCalc.Core.Solvers;
using ElectroCalc.UI.Controls;
using ElectroCalc.UI.Views;
using Xunit;

namespace ElectroCalc.Tests;

public class RotationTests
{
    private static void Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF test timed out");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(ElementType.Resistor)] [InlineData(ElementType.VoltageSource)]
    [InlineData(ElementType.CurrentSource)] [InlineData(ElementType.Capacitor)] [InlineData(ElementType.Inductor)]
    public void FourRotationsPreserveCentreIdentityAndAlignVisualPorts(ElementType type) => Sta(() =>
    {
        var element = new CircuitElement { Type = type, Value = 10, IsPositiveAtStart = false };
        var control = new ElementControl(element);
        var canvas = new Canvas { Width = 600, Height = 600 };
        canvas.Children.Add(control);
        Canvas.SetLeft(control, 200); Canvas.SetTop(control, 200);
        var centre = control.Centre; var id = control.Id;
        var a = control.PortA; var b = control.PortB;
        int moved = 0;
        control.Moved += _ => moved++;
        var offsets = new[] { new Vector(0, 46), new Vector(-46, 0), new Vector(0, -46), new Vector(46, 0) };
        foreach (var offset in offsets)
        {
            control.RotateClockwise();
            canvas.Measure(new Size(600, 600)); canvas.Arrange(new Rect(0, 0, 600, 600)); canvas.UpdateLayout();
            Assert.Equal(centre, control.Centre);
            Assert.Equal(centre - offset, control.PortA);
            Assert.Equal(centre + offset, control.PortB);
            Assert.Equal(id, control.Id);
            Assert.Same(element, control.Element);
            Assert.False(element.IsPositiveAtStart);
            var ports = control.Children.OfType<Ellipse>().ToArray();
            var actualA = ports[0].TransformToAncestor(canvas).Transform(new Point(6, 6));
            var actualB = ports[1].TransformToAncestor(canvas).Transform(new Point(6, 6));
            Assert.True((actualA - control.PortA).Length < 1e-8);
            Assert.True((actualB - control.PortB).Length < 1e-8);
        }
        Assert.Equal(4, moved);
        Assert.Equal(0, control.RotationDegrees);
        Assert.Equal(a, control.PortA); Assert.Equal(b, control.PortB);
        if (type == ElementType.CurrentSource)
        {
            Assert.Contains("B→A", Field<TextBlock>(control, "_topLabel").Text);
            element.IsPositiveAtStart = true;
            control.UpdateVisual();
            Assert.Contains("A→B", Field<TextBlock>(control, "_topLabel").Text);
        }
        // Dragging a rotated element translates both ports equally.
        control.RotateClockwise();
        a = control.PortA; b = control.PortB;
        Canvas.SetLeft(control, 220); Canvas.SetTop(control, 240);
        Assert.Equal(a + new Vector(20, 40), control.PortA);
        Assert.Equal(b + new Vector(20, 40), control.PortB);
        Assert.Throws<ArgumentOutOfRangeException>(() => control.RotationDegrees = 45);
    });

    [Fact]
    public void ConnectedRotationUpdatesWiresAndSurvivesSaveLoad() => Sta(() =>
    {
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        try
        {
            var sourceId = Guid.NewGuid(); var loadId = Guid.NewGuid();
            var project = new CircuitProjectFile
            {
                Analysis = new SavedAnalysisSettings { Mode = CircuitAnalysisMode.AC, FrequencyHz = 50 },
                Elements = new()
                {
                    new() { Id = sourceId, Type = ElementType.VoltageSource, Name = "E1", Value = 10, IsPositiveAtStart = true, CenterX = 300, CenterY = 150 },
                    new() { Id = loadId, Type = ElementType.Resistor, Name = "R1", Value = 5, CenterX = 300, CenterY = 400 }
                },
                Wires = new()
                {
                    Wire(sourceId, loadId, true), Wire(sourceId, loadId, false)
                }
            };
            Invoke(window, "LoadProjectFile", project);
            var controls = Field<List<ElementControl>>(window, "_elements");
            var source = controls.Single(c => c.Element.Type == ElementType.VoltageSource);
            var wires = Field<List<WireVisual>>(window, "_wires");
            var keys = wires.Select(w => (w.FromKey, w.ToKey)).ToArray();
            for (int turn = 1; turn <= 4; turn++)
            {
                source.RotateClockwise();
                Assert.Equal(keys, wires.Select(w => (w.FromKey, w.ToKey)).ToArray());
                foreach (var wire in wires)
                {
                    var line = Field<Polyline>(wire, "_line");
                    var start = controls.Single(c => c.Id == wire.FromKey.OwnerId);
                    var end = controls.Single(c => c.Id == wire.ToKey.OwnerId);
                    Assert.Equal(wire.FromKey.IsPortA ? start.PortA : start.PortB, line.Points[0]);
                    Assert.Equal(wire.ToKey.IsPortA ? end.PortA : end.PortB, line.Points[^1]);
                }
                var graph = Field<CircuitGraph>(window, "_graph");
                Assert.Equal(2, graph.Nodes.Count); Assert.Equal(2, graph.Branches.Count);
                var result = new NodePotentialSolver(graph, new() { Mode = CircuitAnalysisMode.AC }).Solve();
                Assert.True(result.Success, result.ErrorMessage);
                Assert.All(result.BranchResults, row => Assert.Equal(2, row.CurrentPhasor.Magnitude, 8));
            }
            source.RotateClockwise();
            var saved = (CircuitProjectFile)Invoke(window, "BuildProjectFile")!;
            Assert.Equal(90, saved.Elements.Single(e => e.Type == ElementType.VoltageSource).RotationDegrees);
            string path = Path.GetTempFileName();
            try
            {
                CircuitProjectSerializer.Save(saved, path);
                Invoke(window, "LoadProjectFile", CircuitProjectSerializer.Load(path));
                var restored = Field<List<ElementControl>>(window, "_elements").Single(c => c.Element.Type == ElementType.VoltageSource);
                Assert.Equal(90, restored.RotationDegrees);
                Assert.Equal(new Point(300, 150), restored.Centre);
                Assert.Equal(2, Field<List<WireVisual>>(window, "_wires").Count);
                var graph = Field<CircuitGraph>(window, "_graph");
                var result = new NodePotentialSolver(graph, new() { Mode = CircuitAnalysisMode.AC }).Solve();
                Assert.True(result.Success, result.ErrorMessage);
                Assert.All(result.BranchResults, row => Assert.Equal(2, row.CurrentPhasor.Magnitude, 8));
            }
            finally { File.Delete(path); }
            saved.Elements[0].RotationDegrees = 45;
            var exception = Assert.Throws<TargetInvocationException>(() => Invoke(window, "LoadProjectFile", saved));
            Assert.IsType<InvalidDataException>(exception.InnerException);

            // An explicitly placed degree-2 junction is a deliberate branch
            // boundary and must not be folded away by graph reconstruction.
            var eId = Guid.NewGuid(); var r1Id = Guid.NewGuid(); var r2Id = Guid.NewGuid();
            var junctionId = Guid.NewGuid();
            var explicitNodeProject = new CircuitProjectFile
            {
                Elements = new()
                {
                    new() { Id = eId, Type = ElementType.VoltageSource, Name = "E1", Value = 10, IsPositiveAtStart = true, CenterX = 300, CenterY = 100 },
                    new() { Id = r1Id, Type = ElementType.Resistor, Name = "R1", Value = 2, CenterX = 200, CenterY = 300 },
                    new() { Id = r2Id, Type = ElementType.Resistor, Name = "R2", Value = 3, CenterX = 400, CenterY = 300 }
                },
                Junctions = new() { new() { Id = junctionId, X = 300, Y = 300 } },
                Wires = new()
                {
                    Wire(ElementEndpoint(eId, true), ElementEndpoint(r1Id, true)),
                    Wire(ElementEndpoint(r1Id, false), JunctionEndpoint(junctionId)),
                    Wire(JunctionEndpoint(junctionId), ElementEndpoint(r2Id, true)),
                    Wire(ElementEndpoint(r2Id, false), ElementEndpoint(eId, false))
                }
            };
            Invoke(window, "LoadProjectFile", explicitNodeProject);
            var explicitGraph = Field<CircuitGraph>(window, "_graph");
            Assert.Equal(3, explicitGraph.Nodes.Count);
            Assert.Equal(3, explicitGraph.Branches.Count);
            Assert.All(explicitGraph.Branches, branch => Assert.Single(branch.Elements));
        }
        finally { window.Close(); app.Shutdown(); }
    });

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void LegacyFilesDefaultToHorizontal(int version)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $$"""{"Format":"ElectroCalcCircuit","Version":{{version}},"Elements":[{"Name":"R1","Type":"Resistor","Value":5}]}""");
            var project = CircuitProjectSerializer.Load(path);
            Assert.Equal(0, Assert.Single(project.Elements).RotationDegrees);
            Assert.Equal(CircuitAnalysisMode.DC, project.Analysis.Mode);
        }
        finally { File.Delete(path); }
    }

    private static SavedWire Wire(Guid from, Guid to, bool portA) => new()
    {
        From = new() { OwnerId = from, IsElement = true, IsPortA = portA },
        To = new() { OwnerId = to, IsElement = true, IsPortA = portA }
    };
    private static SavedWire Wire(SavedEndpoint from, SavedEndpoint to) => new() { From = from, To = to };
    private static SavedEndpoint ElementEndpoint(Guid id, bool portA) =>
        new() { OwnerId = id, IsElement = true, IsPortA = portA };
    private static SavedEndpoint JunctionEndpoint(Guid id) =>
        new() { OwnerId = id, IsElement = false, IsPortA = false };
    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
