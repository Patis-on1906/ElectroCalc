using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ElectroCalc.Core.Export;
using ElectroCalc.Core.Models;
using ElectroCalc.Core.Persistence;
using ElectroCalc.Core.Solvers;
using ElectroCalc.UI.Controls;
using Microsoft.Win32;

namespace ElectroCalc.UI.Views
{
    /*
     * GRAPH MODEL (fixed)
     * ───────────────────
     * The canvas stores visual endpoints and wires. For every recalculation we
     * rebuild the electrical topology from those logical connections:
     *   1) wire endpoints are unioned into conductor nets;
     *   2) each element becomes an edge between its two conductor nets;
     *   3) degree-2 series points are contracted into one CircuitBranch;
     *   4) only real branch/end nodes remain as CircuitNode objects.
     *
     * Therefore connecting R1 in series with R2 no longer creates an extra
     * calculation node, while a true 3+ branch junction remains a node.
     */

    public partial class MainWindow : Window
    {
        // ── Graph ─────────────────────────────────────────────────────────────
        private readonly CircuitGraph _graph = new();

        // ── Canvas objects ────────────────────────────────────────────────────
        private readonly List<ElementControl>  _elements  = new();
        private readonly List<JunctionControl> _junctions = new();
        private readonly List<WireVisual>      _wires     = new();
        private readonly List<TextBlock>       _nodeLabels = new();

        // ── Wire drawing state ────────────────────────────────────────────────
        private bool    _wiringMode;
        private Point   _wireFromPt;        // canvas start position
        private Guid    _wireFromOwner;     // ElementControl.Id or JunctionControl.Id
        private bool    _wireFromIsPortA;   // only relevant for elements
        private bool    _wireFromIsElem;    // true=element port, false=junction
        private Line?   _dragLine;

        // ── Selection ─────────────────────────────────────────────────────────
        private ElementControl?  _selectedElem;
        private WireVisual?      _selectedWire;
        private JunctionControl? _selectedJunction;

        // ── Misc ──────────────────────────────────────────────────────────────
        private CalculationResult? _lastResult;
        private VectorDiagramWindow? _vectorDiagramWindow;
        private readonly CircuitAnalysisSettings _analysisSettings = new();
        private ThreePhaseCircuitInput _threePhaseInput = new();
        private ThreeBranchOpportunity? _simplifiedOpportunity;

        // ════════════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            _analysisSettings.Mode = CircuitAnalysisMode.DC;
            _analysisSettings.FrequencyHz = 50.0;
            UpdateAnalysisModeUi();
            DrawGridLines();
            void UpdateMethodPanels()
            {
                PanelLoadBranch.Visibility = RbEqGen.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                PanelContour.Visibility = RbPotential.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                if (RbPotential.IsChecked == true) RefreshContourCombo();
            }
            RbEqGen.Checked += (_, _) => UpdateMethodPanels();
            RbMKT.Checked   += (_, _) => UpdateMethodPanels();
            RbKirchhoff.Checked += (_, _) => UpdateMethodPanels();
            RbMUP.Checked   += (_, _) => UpdateMethodPanels();
            RbPotential.Checked += (_, _) => UpdateMethodPanels();
            RbVector.Checked += (_, _) => UpdateMethodPanels();
            SchematicCanvas.MouseLeftButtonDown  += Canvas_MouseDown;
            SchematicCanvas.MouseMove            += Canvas_MouseMove;
            SchematicCanvas.MouseLeftButtonUp    += Canvas_MouseUp;
            SchematicCanvas.MouseRightButtonDown += Canvas_RightClick;
            KeyDown += Window_KeyDown;
            SetStatus("2×клик на холсте — добавить элемент/точку. Тяни от ● порта — провод.");
        }

        // ── Grid ──────────────────────────────────────────────────────────────
        private void DrawGridLines()
        {
            const double g = 20;
            for (double x = 0; x <= SchematicCanvas.Width; x += g)
                SchematicCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = SchematicCanvas.Height,
                    Stroke = (Brush)FindResource("GridLineBrush"),
                    StrokeThickness = x % 80 == 0 ? 0.8 : 0.3, IsHitTestVisible = false
                });
            for (double y = 0; y <= SchematicCanvas.Height; y += g)
                SchematicCanvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = y, X2 = SchematicCanvas.Width, Y2 = y,
                    Stroke = (Brush)FindResource("GridLineBrush"),
                    StrokeThickness = y % 80 == 0 ? 0.8 : 0.3, IsHitTestVisible = false
                });
        }

        // ── Построение расчётного графа из визуальной схемы ───────────────────
        // Провод объединяет свои концы в один электрический net. Затем все
        // последовательные точки степени 2 сворачиваются, поэтому цепочка
        // R1--R2--E1 между двумя точками разветвления становится ОДНОЙ ветвью.
        private sealed class RawNet
        {
            public int Id { get; init; }
            public Point Position { get; init; }
            public List<PortKey> Members { get; } = new();
            public List<RawEdge> Edges { get; } = new();
        }

        private sealed class RawEdge
        {
            public ElementControl Control { get; init; } = null!;
            public RawNet A { get; init; } = null!; // PortA net
            public RawNet B { get; init; } = null!; // PortB net
            public RawNet Other(RawNet n) => A == n ? B : A;
        }

        private Point PositionOf(PortKey key)
        {
            var element = _elements.FirstOrDefault(e => e.Id == key.OwnerId);
            if (element != null) return key.IsPortA ? element.PortA : element.PortB;

            var junction = _junctions.FirstOrDefault(j => j.Id == key.OwnerId);
            return junction?.Position ?? new Point(0, 0);
        }

        private static bool PointLiesOnSegment(Point p, Point a, Point b, double tolerance)
        {
            Vector ab = b - a;
            double len2 = ab.X * ab.X + ab.Y * ab.Y;
            if (len2 < 1e-9) return (p - a).Length <= tolerance;

            Vector ap = p - a;
            double t = (ap.X * ab.X + ap.Y * ab.Y) / len2;
            if (t < -0.02 || t > 1.02) return false;
            t = Math.Max(0.0, Math.Min(1.0, t));

            var closest = new Point(a.X + t * ab.X, a.Y + t * ab.Y);
            return (p - closest).Length <= tolerance;
        }

        /// <summary>
        /// Полностью пересобирает расчётный граф по проводам и портам элементов.
        /// Это главный источник истины: граф больше не зависит от порядка, в
        /// котором пользователь рисовал провода.
        /// </summary>
        private string? RebuildGraphFromSchematic()
        {
            var selectedLoadIds = (CmbLoadBranch.SelectedItem as CircuitBranch)?
                .Elements.Select(e => e.Id).ToHashSet();

            InvalidateResult();
            _graph.Nodes.Clear();
            _graph.Branches.Clear();

            var parent = new Dictionary<PortKey, PortKey>();
            var rank = new Dictionary<PortKey, int>();

            void Add(PortKey k)
            {
                if (parent.ContainsKey(k)) return;
                parent[k] = k;
                rank[k] = 0;
            }

            PortKey Find(PortKey k)
            {
                var p = parent[k];
                if (p != k) parent[k] = Find(p);
                return parent[k];
            }

            void Union(PortKey a, PortKey b)
            {
                Add(a); Add(b);
                var ra = Find(a); var rb = Find(b);
                if (ra == rb) return;
                if (rank[ra] < rank[rb]) (ra, rb) = (rb, ra);
                parent[rb] = ra;
                if (rank[ra] == rank[rb]) rank[ra]++;
            }

            // Сначала объединяем явные концы каждого провода.
            foreach (var wire in _wires)
                Union(wire.FromKey, wire.ToKey);

            // Визуальная схема должна совпадать с электрической. Если вывод
            // элемента или точка соединения лежит прямо на уже нарисованном
            // проводе, считаем это T-соединением даже в том случае, если этот
            // вывод не был исходным концом WireVisual. Это особенно важно для
            // вертикальной шины, проходящей через несколько выводов элементов.
            //
            // Простое пересечение двух проводов в середине БЕЗ junction здесь
            // намеренно не соединяем: соединение возникает только для реального
            // терминала (порт элемента / junction), лежащего на сегменте.
            const double connectionTolerance = 6.0;

            var terminals = new List<(PortKey Key, Point Position)>();
            foreach (var element in _elements)
            {
                terminals.Add((new PortKey(element.Id, true),  element.PortA));
                terminals.Add((new PortKey(element.Id, false), element.PortB));
            }
            foreach (var junction in _junctions)
                terminals.Add((new PortKey(junction.Id, false), junction.Position));

            foreach (var terminal in terminals)
            {
                foreach (var wire in _wires)
                {
                    Point a = PositionOf(wire.FromKey);
                    Point b = PositionOf(wire.ToKey);
                    if (!PointLiesOnSegment(terminal.Position, a, b, connectionTolerance))
                        continue;

                    Add(terminal.Key);
                    Union(terminal.Key, wire.FromKey);
                    Union(terminal.Key, wire.ToKey);
                }
            }

            var warnings = new List<string>();
            var unconnected = new List<string>();
            var shorted = new List<string>();

            // Нет проводов — нет электрического графа.
            if (parent.Count == 0)
            {
                if (_elements.Count > 0)
                    warnings.Add("Элементы не подключены проводами.");
                RefreshNodeList();
                RefreshBranchCombo(selectedLoadIds);
                RefreshSimplifiedOpportunity();
                return warnings.Count == 0 ? null : string.Join(" ", warnings);
            }

            // Сгруппировать все концы проводов в электрические nets.
            var groups = parent.Keys
                .ToList()
                .GroupBy(Find)
                .ToList();

            var netByRoot = new Dictionary<PortKey, RawNet>();
            int netId = 0;
            foreach (var group in groups)
            {
                var members = group.ToList();
                double x = 0, y = 0;
                foreach (var member in members)
                {
                    var pt = PositionOf(member);
                    x += pt.X; y += pt.Y;
                }

                var net = new RawNet
                {
                    Id = netId++,
                    Position = new Point(x / members.Count, y / members.Count)
                };
                net.Members.AddRange(members);
                netByRoot[group.Key] = net;
            }

            // Каждый элемент — ребро между двумя conductor-net. Элемент считается
            // подключённым только если ОБА его порта участвуют хотя бы в одном проводе.
            var rawEdges = new List<RawEdge>();
            foreach (var element in _elements)
            {
                var keyA = new PortKey(element.Id, true);
                var keyB = new PortKey(element.Id, false);
                bool hasA = parent.ContainsKey(keyA);
                bool hasB = parent.ContainsKey(keyB);
                if (!hasA || !hasB)
                {
                    unconnected.Add(element.Element.Name);
                    continue;
                }

                var netA = netByRoot[Find(keyA)];
                var netB = netByRoot[Find(keyB)];
                if (netA == netB)
                {
                    shorted.Add(element.Element.Name);
                    continue;
                }

                var edge = new RawEdge { Control = element, A = netA, B = netB };
                rawEdges.Add(edge);
                netA.Edges.Add(edge);
                netB.Edges.Add(edge);
            }

            // Wire-only nets without connected element terminals are irrelevant
            // for the circuit equations.
            var usefulNets = netByRoot.Values.Where(n => n.Edges.Count > 0).ToList();
            var visitedNets = new HashSet<RawNet>();
            var essential = new HashSet<RawNet>();

            // Electrical nodes are branch points/endpoints. Degree-2 series
            // connections are intentionally NOT calculation nodes.
            foreach (var net in usefulNets)
                if (net.Edges.Count != 2)
                    essential.Add(net);

            // A pure ring has degree 2 everywhere. Pick exactly two artificial
            // anchors so that it remains a valid 2-node / 2-branch graph instead
            // of creating a node between every pair of elements.
            foreach (var seed in usefulNets)
            {
                if (!visitedNets.Add(seed)) continue;
                var component = new List<RawNet>();
                var queue = new Queue<RawNet>();
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    component.Add(n);
                    foreach (var edge in n.Edges)
                    {
                        var other = edge.Other(n);
                        if (visitedNets.Add(other)) queue.Enqueue(other);
                    }
                }

                if (component.All(n => n.Edges.Count == 2) && component.Count >= 2)
                {
                    // Prefer source terminals as meaningful anchors when possible.
                    var sourceEdge = rawEdges.FirstOrDefault(e =>
                        component.Contains(e.A) && component.Contains(e.B) &&
                        e.Control.Element.Type is ElementType.VoltageSource or ElementType.CurrentSource);
                    if (sourceEdge != null)
                    {
                        essential.Add(sourceEdge.A);
                        essential.Add(sourceEdge.B);
                    }
                    else
                    {
                        essential.Add(component[0]);
                        essential.Add(component[1]);
                    }
                }
            }

            // Rare topology with only one essential node (e.g. several loops
            // touching in one point): add one auxiliary anchor to avoid a graph
            // consisting only of self-loop branches.
            foreach (var componentSeed in usefulNets)
            {
                var component = CollectComponent(componentSeed);
                var compEss = component.Where(essential.Contains).ToList();
                if (compEss.Count == 1 && component.Count > 1)
                {
                    var extra = component.First(n => n != compEss[0]);
                    essential.Add(extra);
                }
            }

            var nodeMap = new Dictionary<RawNet, CircuitNode>();
            foreach (var net in usefulNets.Where(essential.Contains).OrderBy(n => n.Id))
                nodeMap[net] = _graph.AddNode(nodeMap.Count.ToString(), net.Position);

            var visitedEdges = new HashSet<RawEdge>();
            foreach (var start in usefulNets.Where(essential.Contains).OrderBy(n => n.Id))
            {
                foreach (var firstEdge in start.Edges.ToList())
                {
                    if (visitedEdges.Contains(firstEdge)) continue;

                    var path = new List<(CircuitElement Element, int Direction)>();
                    var currentNet = start;
                    var edge = firstEdge;
                    RawNet? endNet = null;

                    while (true)
                    {
                        if (!visitedEdges.Add(edge))
                            throw new InvalidOperationException("Ошибка обхода топологии: повторное ребро ветви.");

                        int direction = edge.A == currentNet ? +1 : -1;
                        path.Add((edge.Control.Element, direction));
                        var next = edge.Other(currentNet);

                        if (essential.Contains(next))
                        {
                            endNet = next;
                            break;
                        }

                        var nextEdges = next.Edges.Where(e => !visitedEdges.Contains(e)).ToList();
                        if (nextEdges.Count != 1)
                            throw new InvalidOperationException(
                                "Не удалось однозначно свернуть последовательную ветвь. Проверьте соединения проводов.");

                        currentNet = next;
                        edge = nextEdges[0];
                    }

                    if (endNet == null) continue;
                    var branch = _graph.AddBranch(nodeMap[start], nodeMap[endNet]);
                    foreach (var item in path)
                        branch.AddElement(item.Element, item.Direction);
                }
            }

            _graph.RelabelNodes();

            if (unconnected.Count > 0)
                warnings.Add("Не подключены оба вывода: " + string.Join(", ", unconnected.Distinct()) + ".");
            if (shorted.Count > 0)
                warnings.Add("Закорочены проводом: " + string.Join(", ", shorted.Distinct()) + ".");

            RefreshNodeList();
            RefreshBranchCombo(selectedLoadIds);
            RefreshContourCombo();
            RefreshSimplifiedOpportunity();
            return warnings.Count == 0 ? null : string.Join(" ", warnings);

            List<RawNet> CollectComponent(RawNet seed)
            {
                var result = new List<RawNet>();
                var seen = new HashSet<RawNet> { seed };
                var q = new Queue<RawNet>();
                q.Enqueue(seed);
                while (q.Count > 0)
                {
                    var n = q.Dequeue();
                    result.Add(n);
                    foreach (var e in n.Edges)
                    {
                        var other = e.Other(n);
                        if (seen.Add(other)) q.Enqueue(other);
                    }
                }
                return result;
            }
        }

        // ── Place element ──────────────────────────────────────────────────────
        private ElementControl PlaceElement(ElementType type, string name, double value,
                                            bool polarity, double internalR, Point pt, double phaseDegrees = 0.0,
                                            int rotationDegrees = 0)
        {
            var circ = new CircuitElement
            {
                Type = type, Name = name, Value = value,
                IsPositiveAtStart = polarity, InternalResistance = internalR, PhaseDegrees = phaseDegrees
            };
            var ctrl = new ElementControl(circ) { DisplayAnalysisMode = _analysisSettings.Mode };
            Canvas.SetLeft(ctrl, pt.X - ctrl.Width / 2);
            Canvas.SetTop (ctrl, pt.Y - ctrl.Height / 2);
            ctrl.RotationDegrees = rotationDegrees;
            ctrl.PortDragStarted += OnPortDragStarted;
            ctrl.Moved           += OnElementMoved;
            ctrl.Selected        += OnElementSelected;
            SchematicCanvas.Children.Add(ctrl);
            _elements.Add(ctrl);
            InvalidateResult();
            SetStatus($"Добавлен {name}. Тяни от ● порта для провода.");
            return ctrl;
        }

        // ── Автоматический конструктор 3Φ-схемы ──────────────────────────────
        private void BtnThreePhaseBuilder_Click(object sender, RoutedEventArgs e) => OpenThreePhaseBuilder();

        private void OpenThreePhaseBuilder()
        {
            var dlg = new ThreePhaseSetupDialog(CurrentThreePhaseInput()) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Definition == null) return;

            if ((_elements.Count > 0 || _junctions.Count > 0 || _wires.Count > 0) &&
                MessageBox.Show("Заменить текущую схему автоматически построенной 3Φ-схемой?",
                    "Построение 3Φ-схемы", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            GenerateThreePhaseSchematic(dlg.Definition);
        }

        /// <summary>
        /// Создаёт на холсте каноническую звезду или треугольник. Метод отделён
        /// от диалога, чтобы построение можно было проверить WPF-автотестом.
        /// </summary>
        private void GenerateThreePhaseSchematic(ThreePhaseCircuitDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            ClearSchematic();
            _threePhaseInput = definition.Input.Clone();
            _analysisSettings.Mode = CircuitAnalysisMode.ThreePhase;
            _analysisSettings.FrequencyHz = definition.Settings.FrequencyHz;
            _analysisSettings.ThreePhaseConnection = definition.Settings.ThreePhaseConnection;
            _analysisSettings.ThreePhaseHasNeutral = definition.Settings.ThreePhaseHasNeutral;
            TxtFrequency.Text = definition.Settings.FrequencyHz.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            RbThreePhase.IsChecked = true;
            UpdateAnalysisModeUi();

            ElementControl Place(CircuitElement element, Point position, int rotation = 0)
            {
                var control = PlaceElement(element.Type, element.Name, element.Value,
                    element.IsPositiveAtStart, element.InternalResistance, position,
                    element.PhaseDegrees, rotation);
                control.Element.PhaseAssignment = element.PhaseAssignment;
                control.UpdateVisual();
                return control;
            }

            var phases = new[] { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C };
            var rowY = new Dictionary<ThreePhasePhase, double>
            {
                [ThreePhasePhase.A] = 200,
                [ThreePhasePhase.B] = 400,
                [ThreePhasePhase.C] = 600
            };
            var sources = new Dictionary<ThreePhasePhase, ElementControl>();
            foreach (var phase in phases)
            {
                var source = definition.Elements.Single(e =>
                    e.Type == ElementType.VoltageSource && e.PhaseAssignment == phase);
                // Поворот 180° оставляет фазный порт A справа, нейтральный B слева.
                sources[phase] = Place(source, new Point(260, rowY[phase]), 180);
            }

            var sourceNeutral = PlaceJunction(new Point(120, 780));
            foreach (var phase in phases)
                Connect(sources[phase], false, sourceNeutral);

            if (definition.Settings.ThreePhaseConnection == ThreePhaseLoadConnection.Star)
                BuildGeneratedStar(definition, sources, sourceNeutral, Place);
            else
                BuildGeneratedDelta(definition, sources, Place);

            foreach (var wire in _wires) wire.UpdateGeometry();
            string? warning = RebuildGraphFromSchematic();
            CanvasScroll.ScrollToHorizontalOffset(0);
            CanvasScroll.ScrollToVerticalOffset(0);
            UpdateThreePhaseSummary();
            SetStatus(warning == null
                ? "✓ Трёхфазная схема сформирована. Нажмите «Рассчитать»."
                : $"⚠ Схема сформирована. {warning}");
        }

        private void BuildGeneratedStar(ThreePhaseCircuitDefinition definition,
            IReadOnlyDictionary<ThreePhasePhase, ElementControl> sources,
            JunctionControl sourceNeutral,
            Func<CircuitElement, Point, int, ElementControl> place)
        {
            var phases = new[] { ThreePhasePhase.A, ThreePhasePhase.B, ThreePhasePhase.C };
            var rowY = new[] { 200.0, 400.0, 600.0 };
            var loadNeutral = PlaceJunction(new Point(1060, 780));

            for (int k = 0; k < phases.Length; k++)
            {
                var loadElements = definition.Elements.Where(e =>
                    e.PhaseAssignment == phases[k] && e.Type != ElementType.VoltageSource).ToList();
                var x = HorizontalPositions(loadElements.Count, 690, 850);
                var load = loadElements.Select((element, index) =>
                    place(element, new Point(x[index], rowY[k]), 0)).ToList();

                Connect(sources[phases[k]], true, load[0], true);
                for (int n = 0; n + 1 < load.Count; n++)
                    Connect(load[n], false, load[n + 1], true);
                Connect(load[^1], false, loadNeutral);
            }

            if (definition.Settings.ThreePhaseHasNeutral)
                Connect(sourceNeutral, loadNeutral);
        }

        private void BuildGeneratedDelta(ThreePhaseCircuitDefinition definition,
            IReadOnlyDictionary<ThreePhasePhase, ElementControl> sources,
            Func<CircuitElement, Point, int, ElementControl> place)
        {
            var nodeA = PlaceJunction(new Point(600, 180));
            var nodeB = PlaceJunction(new Point(1040, 180));
            var nodeC = PlaceJunction(new Point(600, 700));
            Connect(sources[ThreePhasePhase.A], true, nodeA);
            Connect(sources[ThreePhasePhase.B], true, nodeB);
            Connect(sources[ThreePhasePhase.C], true, nodeC);

            var ab = LoadElements(definition, ThreePhasePhase.A)
                .Select((element, index) => place(element,
                    new Point(HorizontalPositions(LoadElements(definition, ThreePhasePhase.A).Count, 750, 910)[index], 180), 0))
                .ToList();
            Connect(nodeA, ab[0], true);
            ConnectSeries(ab);
            Connect(ab[^1], false, nodeB);

            var bcElements = LoadElements(definition, ThreePhasePhase.B);
            var bcY = VerticalPositions(bcElements.Count, 350, 530);
            var bc = bcElements.Select((element, index) =>
                place(element, new Point(1040, bcY[index]), 90)).ToList();
            Connect(nodeB, bc[0], true);
            ConnectSeries(bc);
            Connect(bc[^1], false, nodeC);

            // Ветвь CA ориентирована снизу вверх: порт A каждого элемента смотрит к C.
            var caElements = LoadElements(definition, ThreePhasePhase.C);
            var caY = VerticalPositions(caElements.Count, 530, 350);
            var ca = caElements.Select((element, index) =>
                place(element, new Point(600, caY[index]), 270)).ToList();
            Connect(nodeC, ca[0], true);
            ConnectSeries(ca);
            Connect(ca[^1], false, nodeA);
        }

        private static List<CircuitElement> LoadElements(ThreePhaseCircuitDefinition definition, ThreePhasePhase phase) =>
            definition.Elements.Where(e => e.PhaseAssignment == phase && e.Type != ElementType.VoltageSource).ToList();

        private static double[] HorizontalPositions(int count, double first, double second) =>
            count == 1 ? new[] { (first + second) / 2.0 } : new[] { first, second };

        private static double[] VerticalPositions(int count, double first, double second) =>
            count == 1 ? new[] { (first + second) / 2.0 } : new[] { first, second };

        private void ConnectSeries(IReadOnlyList<ElementControl> controls)
        {
            for (int i = 0; i + 1 < controls.Count; i++)
                Connect(controls[i], false, controls[i + 1], true);
        }

        private void Connect(ElementControl from, bool fromPortA, ElementControl to, bool toPortA) =>
            AddWireVisual(fromPortA ? from.PortA : from.PortB, toPortA ? to.PortA : to.PortB, null,
                from.Id, fromPortA, true, to.Id, toPortA, true);

        private void Connect(ElementControl from, bool fromPortA, JunctionControl to) =>
            AddWireVisual(fromPortA ? from.PortA : from.PortB, to.Position, null,
                from.Id, fromPortA, true, to.Id, false, false);

        private void Connect(JunctionControl from, ElementControl to, bool toPortA) =>
            AddWireVisual(from.Position, toPortA ? to.PortA : to.PortB, null,
                from.Id, false, false, to.Id, toPortA, true);

        private void Connect(JunctionControl from, JunctionControl to) =>
            AddWireVisual(from.Position, to.Position, null,
                from.Id, false, false, to.Id, false, false);

        private ThreePhaseCircuitInput CurrentThreePhaseInput()
        {
            var input = _threePhaseInput.Clone();
            input.Mode = ModeFromSettings(_analysisSettings);
            input.FrequencyHz = _analysisSettings.FrequencyHz;

            var sources = _elements.Where(e => e.Element.Type == ElementType.VoltageSource).ToList();
            var sourceA = sources.FirstOrDefault(e => e.Element.PhaseAssignment == ThreePhasePhase.A);
            if (sourceA != null) input.PhaseEmfRms = sourceA.Element.Value;

            if (_analysisSettings.AngularFrequency > 0)
            {
                input.BranchA = BranchInputFromElements(ThreePhasePhase.A, input.BranchA);
                input.BranchB = BranchInputFromElements(ThreePhasePhase.B, input.BranchB);
                input.BranchC = BranchInputFromElements(ThreePhasePhase.C, input.BranchC);
            }
            return input;
        }

        private ThreePhaseBranchInput BranchInputFromElements(ThreePhasePhase phase, ThreePhaseBranchInput fallback)
        {
            var load = _elements.Select(c => c.Element).Where(e =>
                e.PhaseAssignment == phase &&
                (e.Type is ElementType.Resistor or ElementType.Inductor or ElementType.Capacitor)).ToList();
            if (load.Count == 0) return fallback.Clone();

            double r = load.Where(e => e.Type == ElementType.Resistor).Sum(e => e.Value);
            double x = load.Where(e => e.Type == ElementType.Inductor).Sum(e => _analysisSettings.AngularFrequency * e.Value);
            x += load.Where(e => e.Type == ElementType.Capacitor && e.Value > 0)
                .Sum(e => -1.0 / (_analysisSettings.AngularFrequency * e.Value));
            return new ThreePhaseBranchInput { ResistanceOhms = r, ReactanceOhms = x };
        }

        private static ThreePhaseCircuitMode ModeFromSettings(CircuitAnalysisSettings settings) =>
            settings.ThreePhaseConnection == ThreePhaseLoadConnection.Delta
                ? ThreePhaseCircuitMode.Delta
                : settings.ThreePhaseHasNeutral
                    ? ThreePhaseCircuitMode.StarWithNeutral
                    : ThreePhaseCircuitMode.StarWithoutNeutral;

        private void UpdateThreePhaseSummary()
        {
            if (TxtThreePhaseSummary == null) return;
            var input = CurrentThreePhaseInput();
            string mode = input.Mode switch
            {
                ThreePhaseCircuitMode.StarWithNeutral => "звезда с N",
                ThreePhaseCircuitMode.StarWithoutNeutral => "звезда без N",
                _ => "треугольник"
            };
            TxtThreePhaseSummary.Text = $"{mode}; Eφ={input.PhaseEmfRms:G6} В; " +
                $"A: {FormatImpedance(input.BranchA)}, B: {FormatImpedance(input.BranchB)}, C: {FormatImpedance(input.BranchC)}";
        }

        private static string FormatImpedance(ThreePhaseBranchInput branch) =>
            $"{branch.ResistanceOhms:G5}{(branch.ReactanceOhms < 0 ? "−" : "+")}j{Math.Abs(branch.ReactanceOhms):G5} Ом";

        // ── Place junction ─────────────────────────────────────────────────────
        private JunctionControl PlaceJunction(Point pt)
        {
            // Don't place if there's already a junction within snap range at this point
            var existing = _junctions.FirstOrDefault(j => (j.Position - pt).Length < 8);
            if (existing != null) return existing;

            var jc = new JunctionControl();
            jc.Position = pt;

            jc.DragStarted += OnJunctionDragStarted;
            jc.Selected    += OnJunctionSelected;
            SchematicCanvas.Children.Add(jc.Visual);
            _junctions.Add(jc);
            InvalidateResult();
            RefreshNodeList();
            SetStatus("Точка соединения добавлена. Тяни от неё для провода.");
            return jc;
        }

        // ── Canvas events ──────────────────────────────────────────────────────
        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && !_wiringMode)
            {
                e.Handled = true;
                if (_analysisSettings.Mode == CircuitAnalysisMode.ThreePhase)
                {
                    OpenThreePhaseBuilder();
                    return;
                }
                var pt  = Snap(e.GetPosition(SchematicCanvas));
                var dlg = new ElementPickerDialog(_analysisSettings.Mode) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                if (dlg.SelectedType == null) PlaceJunction(pt);
                else PlaceElement(dlg.SelectedType.Value, dlg.ElementName,
                                  dlg.ElementValue, dlg.Polarity, dlg.InternalR, pt, dlg.PhaseDegrees);
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragLine == null) return;
            var pt = e.GetPosition(SchematicCanvas);
            _dragLine.X2 = pt.X; _dragLine.Y2 = pt.Y;
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_wiringMode || _dragLine == null) return;
            var pt = e.GetPosition(SchematicCanvas);

            // 1. Hit an element port?
            var portHit = HitTestPort(pt);
            if (portHit.HasValue &&
                !(portHit.Value.elemId == _wireFromOwner && portHit.Value.isPortA == _wireFromIsPortA))
            {
                var destPt = portHit.Value.isPortA
                    ? _elements.First(x => x.Id == portHit.Value.elemId).PortA
                    : _elements.First(x => x.Id == portHit.Value.elemId).PortB;
                FinishWire(destPt, portHit.Value.elemId, portHit.Value.isPortA, isElem: true);
                return;
            }

            // 2. Hit a junction?
            var jHit = HitTestJunction(pt);
            if (jHit != null && jHit.Id != _wireFromOwner)
            {
                FinishWire(jHit.Position, jHit.Id, false, isElem: false);
                return;
            }

            // 3. Empty canvas → auto junction
            var snapPt = Snap(pt);
            var newJ   = PlaceJunction(snapPt);
            FinishWire(newJ.Position, newJ.Id, false, isElem: false);
        }

        private void Canvas_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_wiringMode) { CancelWiring(); e.Handled = true; }
        }

        // ── Start wiring from element port ─────────────────────────────────────
        private void OnPortDragStarted(ElementControl elem, bool isPortA)
        {
            var pt = isPortA ? elem.PortA : elem.PortB;
            _wireFromPt      = pt;
            _wireFromOwner   = elem.Id;
            _wireFromIsPortA = isPortA;
            _wireFromIsElem  = true;
            BeginDragLine(pt);
        }

        private void OnJunctionDragStarted(JunctionControl jc)
        {
            _wireFromPt     = jc.Position;
            _wireFromOwner  = jc.Id;
            _wireFromIsElem = false;
            BeginDragLine(jc.Position);
        }

        private void BeginDragLine(Point from)
        {
            _wiringMode = true;
            _dragLine = new Line
            {
                X1 = from.X, Y1 = from.Y, X2 = from.X, Y2 = from.Y,
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };
            SchematicCanvas.Children.Add(_dragLine);
            SchematicCanvas.CaptureMouse();
            TxtMode.Text = "Проведение провода — отпусти на порту/точке  (ПКМ — отмена)";
        }

        // ── Finish wire ────────────────────────────────────────────────────────
        private void FinishWire(Point toPt, Guid toOwner, bool toIsPortA, bool isElem)
        {
            SchematicCanvas.Children.Remove(_dragLine);
            _dragLine = null;
            SchematicCanvas.ReleaseMouseCapture();
            _wiringMode = false;
            TxtMode.Text = "Режим: Выбор";

            var fromKey = new PortKey(_wireFromOwner, _wireFromIsPortA);
            var toKey = new PortKey(toOwner, toIsPortA);
            if (fromKey == toKey)
            {
                SetStatus("❌ Нельзя соединить порт сам с собой.");
                return;
            }

            // Не создаём дубликат того же логического провода.
            if (_wires.Any(w =>
                (w.FromKey == fromKey && w.ToKey == toKey) ||
                (w.FromKey == toKey && w.ToKey == fromKey)))
            {
                SetStatus("❌ Такой провод уже существует.");
                return;
            }

            AddWireVisual(_wireFromPt, toPt, null,
                _wireFromOwner, _wireFromIsPortA, _wireFromIsElem,
                toOwner, toIsPortA, isElem);
            UpdateAfterWire();
        }

        private void AddWireVisual(Point fromPt, Point toPt, CircuitBranch? branch,
            Guid fromOwner, bool fromIsPortA, bool fromIsElem,
            Guid toOwner, bool toIsPortA, bool toIsElem)
        {
            Func<Point> getFrom = MakePosGetter(fromOwner, fromIsPortA, fromIsElem, fromPt);
            Func<Point> getTo   = MakePosGetter(toOwner,   toIsPortA,   toIsElem,   toPt);

            var fromKey = new PortKey(fromOwner, fromIsPortA);
            var toKey   = new PortKey(toOwner,   toIsPortA);

            // Провод сам по себе не является расчётной ветвью; Branch всегда null.
            // Расчётные ветви строятся заново в RebuildGraphFromSchematic().
            var wire = new WireVisual(fromKey, toKey, getFrom, getTo, null);
            wire.AddToCanvas(SchematicCanvas);
            wire.UpdateGeometry();
            wire.Clicked += OnWireClicked;
            _wires.Add(wire);
        }

        private Func<Point> MakePosGetter(Guid ownerId, bool isPortA, bool isElem, Point fallback)
        {
            if (isElem)
            {
                var elem = _elements.FirstOrDefault(e => e.Id == ownerId);
                if (elem != null)
                    return isPortA ? () => elem.PortA : (Func<Point>)(() => elem.PortB);
            }
            else
            {
                var junc = _junctions.FirstOrDefault(j => j.Id == ownerId);
                if (junc != null) return () => junc.Position;
            }
            return () => fallback;
        }

        private void UpdateAfterWire()
        {
            var warning = RebuildGraphFromSchematic();
            SetStatus(warning == null
                ? $"Провод проведён. Расчётный граф: {_graph.Nodes.Count} узлов, {_graph.Branches.Count} ветвей."
                : $"⚠ {warning}");
        }

        private void CancelWiring()
        {
            SchematicCanvas.Children.Remove(_dragLine);
            _dragLine = null;
            _wiringMode = false;
            SchematicCanvas.ReleaseMouseCapture();
            TxtMode.Text = "Режим: Выбор";
        }

        // ── Hit tests ─────────────────────────────────────────────────────────
        private (Guid elemId, bool isPortA)? HitTestPort(Point pt)
        {
            const double r = 16;
            foreach (var e in _elements)
            {
                if ((e.PortA - pt).Length < r) return (e.Id, true);
                if ((e.PortB - pt).Length < r) return (e.Id, false);
            }
            return null;
        }

        private JunctionControl? HitTestJunction(Point pt)
        {
            const double r = 14;
            return _junctions.FirstOrDefault(j => (j.Position - pt).Length < r);
        }

        // ── Element events ─────────────────────────────────────────────────────
        private void BtnRotateElement_Click(object sender, RoutedEventArgs e) => RotateSelectedElement();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Only the focused schematic element handles R; typing in properties is unaffected.
            if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.None && !e.IsRepeat &&
                e.OriginalSource is ElementControl && _selectedElem != null &&
                !_wiringMode && Mouse.LeftButton == MouseButtonState.Released)
            {
                RotateSelectedElement();
                e.Handled = true;
            }
        }

        private void RotateSelectedElement()
        {
            if (_selectedElem == null || _wiringMode) return;
            _selectedElem.RotateClockwise();
            _selectedElem.Focus();
            SetStatus($"{_selectedElem.Element.Name}: поворот {_selectedElem.RotationDegrees}°. R — ещё на 90°.");
        }

        private void OnElementMoved(ElementControl ctrl)
        {
            foreach (var w in _wires.Where(w =>
                w.FromKey.OwnerId == ctrl.Id || w.ToKey.OwnerId == ctrl.Id))
                w.UpdateGeometry();
            RebuildGraphFromSchematic();
        }

        private void OnElementSelected(ElementControl ctrl)
        {
            DeselectAll();
            _selectedElem = ctrl; ctrl.IsSelected = true;
            ShowElementProperties(ctrl);
        }

        private void OnWireClicked(WireVisual wire)
        {
            DeselectAll();
            _selectedWire = wire; wire.IsSelected = true;
            TxtSelectedElement.Text = "Провод";
            PropPanel.Visibility    = Visibility.Collapsed;
        }

        private void OnJunctionSelected(JunctionControl junction)
        {
            DeselectAll();
            _selectedJunction = junction;
            junction.IsSelected = true;
            TxtSelectedElement.Text = "Точка соединения";
            PropPanel.Visibility = Visibility.Collapsed;
        }

        private void DeselectAll()
        {
            if (_selectedElem != null) { _selectedElem.IsSelected = false; _selectedElem = null; }
            if (_selectedWire != null) { _selectedWire.IsSelected = false; _selectedWire = null; }
            if (_selectedJunction != null) { _selectedJunction.IsSelected = false; _selectedJunction = null; }
            HideProperties();
        }

        // ── Properties panel ───────────────────────────────────────────────────
        private bool _suppressPropEvents;

        private void ShowElementProperties(ElementControl ctrl)
        {
            _suppressPropEvents = true;
            var el = ctrl.Element;
            PropPanel.Visibility    = Visibility.Visible;
            bool generatedThreePhase = _analysisSettings.Mode == CircuitAnalysisMode.ThreePhase;
            PropPanel.IsEnabled = !generatedThreePhase;
            TxtSelectedElement.Text = generatedThreePhase
                ? $"{el.Type}: {el.Name} (изменение через конструктор 3Φ)"
                : $"{el.Type}: {el.Name}";
            TxtPropName.Text        = el.Name;
            TxtPropValue.Text       = el.Value.ToString("G");
            TxtPropUnit.Text        = UnitFor(el.Type);
            TxtPropInternalR.Text   = el.InternalResistance.ToString("G");
            TxtPropPhase.Text       = el.PhaseDegrees.ToString("G");
            bool src = el.Type is ElementType.VoltageSource or ElementType.CurrentSource;
            PropInternalR.Visibility = src ? Visibility.Visible  : Visibility.Collapsed;
            bool phasorMode = _analysisSettings.IsPhasorMode;
            PropPhase.Visibility     = src && phasorMode ? Visibility.Visible : Visibility.Collapsed;
            TxtPropValueLabel.Text   = src && phasorMode ? "Действующее значение (RMS):" : "Значение:";
            // В режиме 3Φ назначения создаёт конструктор схемы. Ручное изменение
            // A/B/C скрыто, чтобы видимая топология и расчётная модель не расходились.
            PropThreePhasePhase.Visibility = Visibility.Collapsed;
            SelectThreePhasePhaseCombo(el.PhaseAssignment);
            ChkPolarity.Visibility   = src ? Visibility.Visible  : Visibility.Collapsed;
            ChkPolarity.IsChecked    = el.IsPositiveAtStart;
            _suppressPropEvents = false;
        }

        private void HideProperties()
        {
            PropPanel.Visibility    = Visibility.Collapsed;
            PropPanel.IsEnabled     = true;
            TxtSelectedElement.Text = "Выберите элемент на схеме";
        }

        private void PropName_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressPropEvents || _selectedElem == null) return;
            _selectedElem.Element.Name = TxtPropName.Text;
            _selectedElem.UpdateVisual();
            InvalidateResult();
        }

        private void PropValue_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressPropEvents || _selectedElem == null) return;
            if (double.TryParse(TxtPropValue.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) &&
                !double.IsNaN(v) && !double.IsInfinity(v))
            {
                bool passive = _selectedElem.Element.Type is
                    ElementType.Resistor or ElementType.Capacitor or ElementType.Inductor;
                bool acSource = _analysisSettings.IsPhasorMode &&
                    _selectedElem.Element.Type is (ElementType.VoltageSource or ElementType.CurrentSource);
                if ((!passive || v >= 0) && (!acSource || v >= 0))
                {
                    _selectedElem.Element.Value = v;
                    _selectedElem.UpdateVisual();
                    InvalidateResult();
                }
            }
        }

        private void PropPhase_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressPropEvents || _selectedElem == null) return;
            if (_selectedElem.Element.Type is not (ElementType.VoltageSource or ElementType.CurrentSource)) return;
            if (TryParseUiNumber(TxtPropPhase.Text, out double phase))
            {
                _selectedElem.Element.PhaseDegrees = phase;
                _selectedElem.UpdateVisual();
                InvalidateResult();
            }
        }

        private void PropThreePhasePhase_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPropEvents || _selectedElem == null || CmbPropThreePhasePhase.SelectedItem is not ComboBoxItem item)
                return;
            if (!Enum.TryParse<ThreePhasePhase>(item.Tag?.ToString(), true, out var phase))
                phase = ThreePhasePhase.None;
            _selectedElem.Element.PhaseAssignment = phase;
            _selectedElem.UpdateVisual();
            InvalidateResult();
        }

        private void SelectThreePhasePhaseCombo(ThreePhasePhase phase)
        {
            foreach (var obj in CmbPropThreePhasePhase.Items)
                if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), phase.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    CmbPropThreePhasePhase.SelectedItem = item;
                    return;
                }
            CmbPropThreePhasePhase.SelectedIndex = 0;
        }

        private void PropInternalR_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_suppressPropEvents || _selectedElem == null) return;
            if (double.TryParse(TxtPropInternalR.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) &&
                !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0)
            {
                _selectedElem.Element.InternalResistance = v;
                InvalidateResult();
            }
        }

        private void ChkPolarity_Changed(object s, RoutedEventArgs e)
        {
            if (_suppressPropEvents || _selectedElem == null) return;
            _selectedElem.Element.IsPositiveAtStart = ChkPolarity.IsChecked == true;
            _selectedElem.UpdateVisual();
            InvalidateResult();
        }

        // ── Toolbar ────────────────────────────────────────────────────────────
        private void BtnAddResistor_Click (object s, RoutedEventArgs e) =>
            SetAddElementStatus("2×клик на холсте → «R Резистор».");
        private void BtnAddESource_Click  (object s, RoutedEventArgs e) =>
            SetAddElementStatus("2×клик на холсте → «E Источник ЭДС».");
        private void BtnAddJSource_Click  (object s, RoutedEventArgs e) =>
            SetAddElementStatus("2×клик на холсте → «J Источник тока».");
        private void BtnAddCapacitor_Click(object s, RoutedEventArgs e) =>
            SetAddElementStatus("2×клик на холсте → «C Конденсатор».");
        private void BtnAddInductor_Click (object s, RoutedEventArgs e) =>
            SetAddElementStatus("2×клик на холсте → «L Индуктивность».");
        private void SetAddElementStatus(string ordinaryMessage) =>
            SetStatus(_analysisSettings.Mode == CircuitAnalysisMode.ThreePhase
                ? "В режиме 3Φ элементы создаются автоматически через конструктор."
                : ordinaryMessage);
        private void BtnModeWire_Click  (object s, RoutedEventArgs e) =>
            SetStatus("Тяни от ● порта или точки соединения для провода.");
        private void BtnModeSelect_Click(object s, RoutedEventArgs e) =>
            SetStatus("2×клик — добавить. Клик на элементе — выбрать.");

        private void BtnDelete_Click(object s, RoutedEventArgs e)
        {
            if (_selectedElem != null)
            {
                foreach (var w in _wires.Where(w =>
                    w.FromKey.OwnerId == _selectedElem.Id ||
                    w.ToKey.OwnerId   == _selectedElem.Id).ToList())
                    RemoveWire(w);
                SchematicCanvas.Children.Remove(_selectedElem);
                _elements.Remove(_selectedElem);
                _selectedElem = null;
                HideProperties();
            }
            else if (_selectedWire != null)
            {
                RemoveWire(_selectedWire);
                _selectedWire = null;
                HideProperties();
            }
            else if (_selectedJunction != null)
            {
                var jc = _selectedJunction;
                foreach (var w in _wires.Where(w =>
                    w.FromKey.OwnerId == jc.Id || w.ToKey.OwnerId == jc.Id).ToList())
                    RemoveWire(w);
                SchematicCanvas.Children.Remove(jc.Visual);
                _junctions.Remove(jc);
                _selectedJunction = null;
            }

            RebuildGraphFromSchematic();
        }

        private void RemoveWire(WireVisual w)
        {
            w.RemoveFromCanvas(SchematicCanvas);
            _wires.Remove(w);
        }

        private void ClearSchematic(bool redrawGrid = true)
        {
            CancelWiringIfNeeded();
            DeselectAll();
            SchematicCanvas.Children.Clear();
            if (redrawGrid) DrawGridLines();
            _elements.Clear();
            _junctions.Clear();
            _wires.Clear();
            _nodeLabels.Clear();
            _graph.Nodes.Clear();
            _graph.Branches.Clear();
            _lastResult = null;
            LstSteps.ItemsSource = null;
            TxtStepDetail.Text = string.Empty;
            GridResults.ItemsSource = null;
            HideProperties();
            RefreshNodeList();
            RefreshBranchCombo();
            RefreshSimplifiedOpportunity();
            TxtPowerGen.Text = "ΣS_ист = —";
            TxtPowerCon.Text = "ΣS_потр = —";
            TxtPowerDelta.Text = "|ΔS| = —";
        }

        private void CancelWiringIfNeeded()
        {
            if (_dragLine != null || _wiringMode)
                CancelWiring();
        }

        private void BtnClearCanvas_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show("Очистить схему?", "Подтверждение",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            ClearSchematic();
            SetStatus("Схема очищена.");
        }

        // ── Save / load schematic ──────────────────────────────────────────────
        private void BtnSaveCircuit_Click(object s, RoutedEventArgs e)
        {
            if (_elements.Count == 0 && _junctions.Count == 0 && _wires.Count == 0)
            {
                SetStatus("❌ Нечего сохранять: схема пуста.");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Схема ElectroCalc (*.ecircuit.json)|*.ecircuit.json|JSON (*.json)|*.json",
                FileName = $"Circuit_{DateTime.Now:yyyyMMdd_HHmm}.ecircuit.json",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var project = BuildProjectFile();
                CircuitProjectSerializer.Save(project, dlg.FileName);
                SetStatus($"✓ Схема сохранена: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                SetStatus($"❌ Не удалось сохранить схему: {ex.Message}");
            }
        }

        private CircuitProjectFile BuildProjectFile()
        {
            var project = new CircuitProjectFile();
            var analysis = GetAnalysisSettings();
            analysis.Validate();
            project.Analysis.Mode = analysis.Mode;
            project.Analysis.FrequencyHz = analysis.FrequencyHz;
            project.Analysis.ThreePhaseConnection = analysis.ThreePhaseConnection;
            project.Analysis.ThreePhaseHasNeutral = analysis.ThreePhaseHasNeutral;

            foreach (var ctrl in _elements)
            {
                project.Elements.Add(new SavedElement
                {
                    Id = ctrl.Id,
                    Type = ctrl.Element.Type,
                    Name = ctrl.Element.Name,
                    Value = ctrl.Element.Value,
                    InternalResistance = ctrl.Element.InternalResistance,
                    IsPositiveAtStart = ctrl.Element.IsPositiveAtStart,
                    PhaseDegrees = ctrl.Element.PhaseDegrees,
                    ThreePhasePhase = ctrl.Element.PhaseAssignment,
                    CenterX = ctrl.Centre.X,
                    CenterY = ctrl.Centre.Y,
                    RotationDegrees = ctrl.RotationDegrees
                });
            }

            foreach (var junction in _junctions)
            {
                project.Junctions.Add(new SavedJunction
                {
                    Id = junction.Id,
                    X = junction.Position.X,
                    Y = junction.Position.Y
                });
            }

            bool IsElementOwner(Guid id) => _elements.Any(x => x.Id == id);
            foreach (var wire in _wires)
            {
                project.Wires.Add(new SavedWire
                {
                    From = new SavedEndpoint
                    {
                        OwnerId = wire.FromKey.OwnerId,
                        IsElement = IsElementOwner(wire.FromKey.OwnerId),
                        IsPortA = wire.FromKey.IsPortA
                    },
                    To = new SavedEndpoint
                    {
                        OwnerId = wire.ToKey.OwnerId,
                        IsElement = IsElementOwner(wire.ToKey.OwnerId),
                        IsPortA = wire.ToKey.IsPortA
                    }
                });
            }

            project.UiState.CalculationMethod = analysis.Mode == CircuitAnalysisMode.ThreePhase ? "THREEPHASE" :
                                                RbKirchhoff.IsChecked == true ? "KIRCHHOFF" :
                                                RbMUP.IsChecked == true ? "MUP" :
                                                RbEqGen.IsChecked == true ? "EQGEN" :
                                                RbPotential.IsChecked == true ? "POTENTIAL" :
                                                RbVector.IsChecked == true ? "VECTOR" : "MKT";

            if (CmbLoadBranch.SelectedItem is CircuitBranch selectedBranch)
            {
                var selectedElements = selectedBranch.Elements.ToHashSet();
                project.UiState.LoadBranchElementIds = _elements
                    .Where(c => selectedElements.Contains(c.Element))
                    .Select(c => c.Id)
                    .ToList();
            }

            return project;
        }

        private void BtnOpenCircuit_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Схема ElectroCalc (*.ecircuit.json;*.json)|*.ecircuit.json;*.json|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var project = CircuitProjectSerializer.Load(dlg.FileName);
                string? warning = LoadProjectFile(project);
                SetStatus(string.IsNullOrWhiteSpace(warning)
                    ? $"✓ Схема загружена: {dlg.FileName}. Расчётный граф: {_graph.Nodes.Count} узлов, {_graph.Branches.Count} ветвей."
                    : $"⚠ Схема загружена: {dlg.FileName}. {warning}");
            }
            catch (Exception ex)
            {
                SetStatus($"❌ Не удалось открыть схему: {ex.Message}");
                MessageBox.Show(ex.Message, "Ошибка импорта схемы", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string? LoadProjectFile(CircuitProjectFile project)
        {
            ValidateProjectFile(project);
            ClearSchematic();

            _analysisSettings.Mode = project.Analysis.Mode;
            _analysisSettings.FrequencyHz = project.Analysis.FrequencyHz;
            _analysisSettings.ThreePhaseConnection = project.Analysis.ThreePhaseConnection;
            _analysisSettings.ThreePhaseHasNeutral = project.Analysis.ThreePhaseHasNeutral;
            TxtFrequency.Text = project.Analysis.FrequencyHz.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            if (project.Analysis.Mode == CircuitAnalysisMode.ThreePhase) RbThreePhase.IsChecked = true;
            else if (project.Analysis.Mode == CircuitAnalysisMode.AC) RbAC.IsChecked = true;
            else RbDC.IsChecked = true;
            UpdateAnalysisModeUi();

            // Saved IDs belong to the file. Runtime controls receive new IDs, so
            // all wire endpoints are remapped through these dictionaries.
            var elementMap = new Dictionary<Guid, ElementControl>();
            var junctionMap = new Dictionary<Guid, JunctionControl>();

            foreach (var saved in project.Elements)
            {
                var ctrl = PlaceElement(saved.Type, saved.Name, saved.Value,
                    saved.IsPositiveAtStart, saved.InternalResistance,
                    new Point(saved.CenterX, saved.CenterY), saved.PhaseDegrees, saved.RotationDegrees);
                ctrl.Element.PhaseAssignment = saved.ThreePhasePhase;
                ctrl.UpdateVisual();
                elementMap.Add(saved.Id, ctrl);
            }

            foreach (var saved in project.Junctions)
            {
                var junction = PlaceJunction(new Point(saved.X, saved.Y));
                junctionMap.Add(saved.Id, junction);
            }

            foreach (var savedWire in project.Wires)
            {
                var from = ResolveLoadedEndpoint(savedWire.From, elementMap, junctionMap);
                var to = ResolveLoadedEndpoint(savedWire.To, elementMap, junctionMap);

                if (from.Key == to.Key)
                    throw new InvalidDataException("Файл содержит провод, замкнутый на тот же порт.");

                if (_wires.Any(w =>
                    (w.FromKey == from.Key && w.ToKey == to.Key) ||
                    (w.FromKey == to.Key && w.ToKey == from.Key)))
                    continue; // harmless duplicate in a hand-edited JSON file

                AddWireVisual(from.Position, to.Position, null,
                    from.Key.OwnerId, from.Key.IsPortA, from.IsElement,
                    to.Key.OwnerId, to.Key.IsPortA, to.IsElement);
            }

            string? warning = RebuildGraphFromSchematic();

            switch ((project.UiState.CalculationMethod ?? "MKT").ToUpperInvariant())
            {
                case "THREEPHASE": break; // режим определяется Analysis.Mode
                case "KIRCHHOFF": RbKirchhoff.IsChecked = true; break;
                case "MUP": RbMUP.IsChecked = true; break;
                case "EQGEN": RbEqGen.IsChecked = true; break;
                case "POTENTIAL": RbPotential.IsChecked = true; break;
                case "VECTOR": if (_analysisSettings.Mode == CircuitAnalysisMode.AC) RbVector.IsChecked = true; else RbMUP.IsChecked = true; break;
                default: RbMKT.IsChecked = true; break;
            }

            // Restore the load by the set of elements that constituted the saved
            // branch; graph node/branch IDs are intentionally not persisted.
            if (project.UiState.LoadBranchElementIds.Count > 0)
            {
                var wantedElements = project.UiState.LoadBranchElementIds
                    .Where(elementMap.ContainsKey)
                    .Select(id => elementMap[id].Element)
                    .ToHashSet();

                var restored = _graph.Branches.FirstOrDefault(b =>
                    b.Elements.Count == wantedElements.Count &&
                    b.Elements.All(wantedElements.Contains));
                if (restored != null)
                    CmbLoadBranch.SelectedItem = restored;
            }

            foreach (var wire in _wires) wire.UpdateGeometry();
            DeselectAll();
            InvalidateResult();
            if (_analysisSettings.Mode == CircuitAnalysisMode.ThreePhase)
            {
                _threePhaseInput = CurrentThreePhaseInput();
                UpdateThreePhaseSummary();
            }

            return warning;
        }

        private (PortKey Key, Point Position, bool IsElement) ResolveLoadedEndpoint(
            SavedEndpoint endpoint,
            IReadOnlyDictionary<Guid, ElementControl> elements,
            IReadOnlyDictionary<Guid, JunctionControl> junctions)
        {
            if (endpoint.IsElement)
            {
                if (!elements.TryGetValue(endpoint.OwnerId, out var ctrl))
                    throw new InvalidDataException($"Провод ссылается на отсутствующий элемент {endpoint.OwnerId}.");
                var key = new PortKey(ctrl.Id, endpoint.IsPortA);
                return (key, endpoint.IsPortA ? ctrl.PortA : ctrl.PortB, true);
            }

            if (!junctions.TryGetValue(endpoint.OwnerId, out var junction))
                throw new InvalidDataException($"Провод ссылается на отсутствующую точку {endpoint.OwnerId}.");
            return (new PortKey(junction.Id, false), junction.Position, false);
        }

        private static void ValidateProjectFile(CircuitProjectFile project)
        {
            if (project.Analysis == null) throw new InvalidDataException("В файле отсутствуют настройки режима расчёта.");
            if (!Finite(project.Analysis.FrequencyHz) || project.Analysis.FrequencyHz <= 0)
                throw new InvalidDataException("Частота в файле должна быть положительным числом.");
            static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

            var ids = new HashSet<Guid>();
            foreach (var e in project.Elements)
            {
                if (e.Id == Guid.Empty || !ids.Add(e.Id))
                    throw new InvalidDataException("В файле есть пустой или повторяющийся ID элемента.");
                if (!Finite(e.Value) || !Finite(e.InternalResistance) || !Finite(e.PhaseDegrees) ||
                    !Finite(e.CenterX) || !Finite(e.CenterY))
                    throw new InvalidDataException($"Некорректные числовые данные элемента '{e.Name}'.");
                if (e.InternalResistance < 0)
                    throw new InvalidDataException($"Внутреннее сопротивление '{e.Name}' не может быть отрицательным.");
                if (e.RotationDegrees is not (0 or 90 or 180 or 270))
                    throw new InvalidDataException($"Некорректный угол поворота элемента '{e.Name}'.");
                if ((e.Type is ElementType.Resistor or ElementType.Capacitor or ElementType.Inductor) && e.Value < 0)
                    throw new InvalidDataException($"Значение пассивного элемента '{e.Name}' не может быть отрицательным.");
                if (project.Analysis.Mode != CircuitAnalysisMode.DC &&
                    e.Type is (ElementType.VoltageSource or ElementType.CurrentSource) && e.Value < 0)
                    throw new InvalidDataException($"Действующее значение AC-источника '{e.Name}' не может быть отрицательным.");
            }
            var junctionIds = new HashSet<Guid>();
            foreach (var j in project.Junctions)
            {
                if (j.Id == Guid.Empty || !ids.Add(j.Id))
                    throw new InvalidDataException("В файле есть пустой или повторяющийся ID точки соединения.");
                junctionIds.Add(j.Id);
                if (!Finite(j.X) || !Finite(j.Y))
                    throw new InvalidDataException("Некорректные координаты точки соединения.");
            }

            var elementIds = project.Elements.Select(e => e.Id).ToHashSet();
            void CheckEndpoint(SavedEndpoint endpoint)
            {
                bool exists = endpoint.IsElement
                    ? elementIds.Contains(endpoint.OwnerId)
                    : junctionIds.Contains(endpoint.OwnerId);
                if (!exists)
                    throw new InvalidDataException(
                        $"Провод ссылается на отсутствующий {(endpoint.IsElement ? "элемент" : "узел соединения")} {endpoint.OwnerId}.");
                if (!endpoint.IsElement && endpoint.IsPortA)
                    throw new InvalidDataException("У точки соединения не может быть порта A.");
            }

            foreach (var wire in project.Wires)
            {
                if (wire.From == null || wire.To == null)
                    throw new InvalidDataException("В файле есть провод без одного из концов.");
                CheckEndpoint(wire.From);
                CheckEndpoint(wire.To);
                if (wire.From.OwnerId == wire.To.OwnerId &&
                    wire.From.IsElement == wire.To.IsElement &&
                    wire.From.IsPortA == wire.To.IsPortA)
                    throw new InvalidDataException("В файле есть провод, замкнутый на тот же порт.");
            }
        }

        // ── Calculate ──────────────────────────────────────────────────────────
        private void BtnCalculate_Click(object s, RoutedEventArgs e)
        {
            string? topologyWarning = RebuildGraphFromSchematic();
            int n = _graph.Nodes.Count, b = _graph.Branches.Count;

            CircuitAnalysisSettings analysis;
            try
            {
                analysis = GetAnalysisSettings();
                analysis.Validate();
            }
            catch (Exception ex)
            {
                SetStatus($"❌ {ex.Message}");
                return;
            }

            if (analysis.Mode == CircuitAnalysisMode.ThreePhase)
            {
                try
                {
                    var result3 = new ThreePhaseCircuitSolver(_elements.Select(x => x.Element), analysis).Solve();
                    _lastResult = result3;
                    DisplayResult(result3);
                    foreach (var element in _elements) element.Element.LastCurrentPhasor = System.Numerics.Complex.Zero;
                    foreach (var row in result3.BranchResults)
                        foreach (var element in row.Branch.Elements)
                            element.LastCurrentPhasor = row.CurrentPhasor * row.Branch.GetElementDirection(element);
                    foreach (var control in _elements) control.UpdateVisual();
                    SetStatus(result3.Success
                        ? $"✓ Трёхфазная цепь рассчитана: {(analysis.ThreePhaseConnection == ThreePhaseLoadConnection.Star ? "звезда" : "треугольник")}" +
                          (analysis.ThreePhaseConnection == ThreePhaseLoadConnection.Star ? (analysis.ThreePhaseHasNeutral ? ", с N." : ", без N.") : ".") +
                          (result3.PowerBalanceOk ? " Баланс мощностей ✓" : " ⚠ Баланс мощностей не сошёлся")
                        : $"❌ {result3.ErrorMessage}");
                }
                catch (Exception ex)
                {
                    SetStatus($"❌ {ex.Message}");
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(topologyWarning))
            {
                SetStatus($"❌ {topologyWarning}");
                return;
            }
            if (n < 2)
            {
                SetStatus("❌ Недостаточно расчётных узлов. Проверьте, что схема замкнута и оба вывода каждого элемента подключены.");
                return;
            }
            if (b == 0)
            {
                SetStatus("❌ Нет расчётных ветвей с элементами.");
                return;
            }

            SetStatus($"Расчётный граф: {n} узлов, {b} ветвей. Считаю…");

            try
            {
                if (analysis.Mode == CircuitAnalysisMode.AC && RbPotential.IsChecked == true)
                {
                    SetStatus("❌ Потенциальная диаграмма оставлена только для DC-режима. Для AC используйте данные для векторных диаграмм.");
                    return;
                }

                CalculationResult result;
                if (RbMKT.IsChecked == true)
                    result = new MeshCurrentSolver(_graph, analysis).Solve();
                else if (RbKirchhoff.IsChecked == true)
                    result = new KirchhoffLawSolver(_graph, analysis).Solve();
                else if (RbMUP.IsChecked == true)
                    result = new NodePotentialSolver(_graph, analysis).Solve();
                else if (RbEqGen.IsChecked == true)
                {
                    if (CmbLoadBranch.SelectedItem is not CircuitBranch lb)
                    {
                        SetStatus("❌ Выберите ветвь нагрузки.");
                        return;
                    }
                    result = new EquivalentGeneratorSolver(_graph, lb, analysis).Solve();
                }
                else if (RbPotential.IsChecked == true)
                {
                    if (CmbContour.SelectedItem is not CircuitContour contour)
                    {
                        SetStatus("❌ Выберите контур для потенциальной диаграммы.");
                        return;
                    }
                    result = new PotentialDiagramSolver(_graph, contour).Solve();
                }
                else
                {
                    result = new VectorDiagramDataSolver(_graph, analysis).Solve();
                }

                _lastResult = result;
                DisplayResult(result);

                foreach (var element in _elements)
                    element.Element.LastCurrentPhasor = System.Numerics.Complex.Zero;
                foreach (var row in result.BranchResults)
                    foreach (var element in row.Branch.Elements)
                        element.LastCurrentPhasor = row.CurrentPhasor * row.Branch.GetElementDirection(element);
                foreach (var control in _elements)
                    control.UpdateVisual();

                if (result.Success && result.Method == CalculationMethod.VectorData)
                    ShowVectorDiagrams(result);

                SetStatus(result.Success
                    ? (result.Method == CalculationMethod.KirchhoffLaws
                        ? $"✓ Общая система уравнений Кирхгофа составлена. Существенных узлов: {n}, ветвей: {b}."
                        : result.Method == CalculationMethod.PotentialDiagram
                            ? $"✓ Потенциалы выбранного контура рассчитаны. Существенных узлов: {n}, ветвей: {b}."
                        : result.Method == CalculationMethod.VectorData
                            ? $"✓ Данные для векторных диаграмм рассчитаны. Существенных узлов: {n}, ветвей: {b}."
                            : $"✓ Готово. Существенных узлов: {n}, ветвей: {b}. " +
                              (result.PowerBalanceAvailable
                                  ? (result.PowerBalanceOk ? "Баланс мощностей ✓" : "⚠ Баланс мощностей не сошёлся")
                                  : "Мощности для этого режима не рассчитываются."))
                    : $"❌ {result.ErrorMessage}");
            }
            catch (Exception ex)
            {
                SetStatus($"❌ {ex.Message}");
            }
        }

        private void DisplayResult(CalculationResult result)
        {
            BtnOpenVectorDiagrams.Visibility = result.Success &&
                                               result.Method == CalculationMethod.VectorData &&
                                               result.VectorDiagrams.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            LstSteps.ItemsSource    = result.Steps;
            TxtStepDetail.Text      = "";
            GridResults.ItemsSource = result.BranchResults.Select(r => new
            {
                BranchName = r.Branch.ToString(),
                CurrentStr = result.Analysis.Mode != CircuitAnalysisMode.DC
                    ? Phasor.Polar(r.CurrentPhasor, "0.####", "0.##")
                    : $"{r.Current:F4}",
                VoltageStr = result.Analysis.Mode != CircuitAnalysisMode.DC
                    ? Phasor.Polar(r.VoltagePhasor, "0.####", "0.##")
                    : $"{r.Voltage:F4}",
                ActivePowerStr = result.PowerBalanceAvailable ? $"{r.ActivePower:F4}" : "—",
                ReactivePowerStr = result.PowerBalanceAvailable ? $"{r.ReactivePower:F4}" : "—",
                ApparentPowerStr = result.PowerBalanceAvailable ? $"{r.ApparentPower:F4}" : "—"
            }).ToList();
            if (!result.Success)
            {
                TxtPowerGen.Text = "ΣS_ист = —";
                TxtPowerCon.Text = "ΣS_потр = —";
                TxtPowerDelta.Text = "|ΔS| = —";
                PowerBalanceBorder.Background = new SolidColorBrush(Color.FromRgb(255, 235, 238));
                return;
            }

            if (result.Method == CalculationMethod.KirchhoffLaws ||
                result.Method == CalculationMethod.PotentialDiagram)
            {
                TxtPowerGen.Text = "ΣS_ист = —";
                TxtPowerCon.Text = "ΣS_потр = —";
                TxtPowerDelta.Text = result.Method == CalculationMethod.PotentialDiagram
                    ? "Для диаграммы используется последовательность потенциалов"
                    : "Только общий вид уравнений";
                PowerBalanceBorder.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                return;
            }

            if (!result.PowerBalanceAvailable)
            {
                TxtPowerGen.Text = "ΣS_ист = —";
                TxtPowerCon.Text = "ΣS_потр = —";
                TxtPowerDelta.Text = "Мощности для выбранного режима не рассчитываются";
                PowerBalanceBorder.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                return;
            }

            var sg = result.TotalComplexPowerGenerated;
            var sc = result.TotalComplexPowerConsumed;
            TxtPowerGen.Text = $"ΣPист={sg.Real:F4} Вт; ΣQист={sg.Imaginary:F4} вар; |ΣSист|={sg.Magnitude:F4} ВА";
            TxtPowerCon.Text = $"ΣPпотр={sc.Real:F4} Вт; ΣQпотр={sc.Imaginary:F4} вар; |ΣSпотр|={sc.Magnitude:F4} ВА";
            TxtPowerDelta.Text = $"|ΔS| = {result.PowerImbalance:E2} ВА  " +
                                 (result.PowerBalanceOk ? "✓" : "✗");
            PowerBalanceBorder.Background = result.PowerBalanceOk
                ? new SolidColorBrush(Color.FromRgb(232, 245, 233))
                : new SolidColorBrush(Color.FromRgb(255, 235, 238));
        }

        private void LstSteps_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (LstSteps.SelectedItem is SolutionStep step)
                TxtStepDetail.Text = step.MatrixText ?? step.Formula ?? "";
        }

        private void BtnOpenVectorDiagrams_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResult is { Success: true, Method: CalculationMethod.VectorData } result &&
                result.VectorDiagrams.Count > 0)
                ShowVectorDiagrams(result);
        }

        private void ShowVectorDiagrams(CalculationResult result)
        {
            if (_vectorDiagramWindow?.IsVisible == true)
                _vectorDiagramWindow.Close();

            var window = new VectorDiagramWindow(result) { Owner = this };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_vectorDiagramWindow, window))
                    _vectorDiagramWindow = null;
            };
            _vectorDiagramWindow = window;
            window.Show();
        }

        // ── Export ─────────────────────────────────────────────────────────────
        private void BtnExportWord_Click(object s, RoutedEventArgs e)
        {
            if (_lastResult == null) { SetStatus("❌ Сначала выполните расчёт."); return; }
            var dlg = new SaveFileDialog
            {
                Filter   = "Word документ (*.docx)|*.docx",
                FileName = $"ElectroCalc_{DateTime.Now:yyyyMMdd_HHmm}.docx"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                WordExporter.Export(_lastResult, dlg.FileName);
                SetStatus($"✓ Сохранено: {dlg.FileName}");
                if (MessageBox.Show("Открыть файл?", "Экспорт", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus($"❌ {ex.Message}"); }
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        private void RefreshNodeList()
        {
            LstNodes.ItemsSource = null;
            LstNodes.ItemsSource = _graph.Nodes
                .Select(n => $"Узел {n.Label}  ({n.Position.X:F0}, {n.Position.Y:F0})")
                .ToList();
            RefreshNodeLabelsOnCanvas();
        }

        private void RefreshNodeLabelsOnCanvas()
        {
            foreach (var label in _nodeLabels)
                SchematicCanvas.Children.Remove(label);
            _nodeLabels.Clear();

            foreach (var node in _graph.Nodes)
            {
                var label = new TextBlock
                {
                    Text = $"Узел {node.Label}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(13, 71, 161)),
                    Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
                    Padding = new Thickness(3, 1, 3, 1),
                    IsHitTestVisible = false,
                    ToolTip = $"Существенный узел {node.Label}"
                };
                Canvas.SetLeft(label, node.Position.X + 8);
                Canvas.SetTop(label, node.Position.Y - 24);
                Panel.SetZIndex(label, 1000);
                SchematicCanvas.Children.Add(label);
                _nodeLabels.Add(label);
            }
        }

        private void RefreshBranchCombo(HashSet<Guid>? selectedIds = null)
        {
            selectedIds ??= (CmbLoadBranch.SelectedItem as CircuitBranch)?
                .Elements.Select(e => e.Id).ToHashSet();

            CmbLoadBranch.ItemsSource = null;
            CmbLoadBranch.ItemsSource = _graph.Branches;

            if (selectedIds != null)
            {
                var replacement = _graph.Branches.FirstOrDefault(b =>
                    b.Elements.Count == selectedIds.Count &&
                    b.Elements.All(e => selectedIds.Contains(e.Id)));
                if (replacement != null)
                    CmbLoadBranch.SelectedItem = replacement;
            }
        }

        private void RefreshContourCombo()
        {
            // Пересчитываем список контуров из текущего расчётного графа.
            // Сохранять сами контуры между перестроениями нельзя: ветви создаются заново.
            int previousNumber = (CmbContour.SelectedItem as CircuitContour)?.Number ?? 1;
            var contours = CircuitContourFinder.FindAll(_graph);
            CmbContour.ItemsSource = null;
            CmbContour.ItemsSource = contours;
            if (contours.Count > 0)
                CmbContour.SelectedItem = contours[Math.Min(previousNumber - 1, contours.Count - 1)];
        }

        private static Point Snap(Point p, double g = 20) =>
            new(Math.Round(p.X / g) * g, Math.Round(p.Y / g) * g);

        private CircuitAnalysisSettings GetAnalysisSettings()
        {
            if (RbThreePhase.IsChecked == true)
            {
                if (!TryParseUiNumber(TxtFrequency.Text, out double f) || f <= 0)
                    throw new InvalidOperationException("Для 3Φ задайте частоту больше 0 Гц.");
                _analysisSettings.Mode = CircuitAnalysisMode.ThreePhase;
                _analysisSettings.FrequencyHz = f;
            }
            else if (RbAC.IsChecked == true)
            {
                if (!TryParseUiNumber(TxtFrequency.Text, out double f) || f <= 0)
                    throw new InvalidOperationException("Для AC задайте частоту больше 0 Гц.");
                _analysisSettings.Mode = CircuitAnalysisMode.AC;
                _analysisSettings.FrequencyHz = f;
            }
            else
            {
                _analysisSettings.Mode = CircuitAnalysisMode.DC;
            }
            return _analysisSettings.Clone();
        }

        private void AnalysisMode_Changed(object sender, RoutedEventArgs e)
        {
            if (PanelFrequency == null) return;
            _analysisSettings.Mode = RbThreePhase?.IsChecked == true
                ? CircuitAnalysisMode.ThreePhase
                : RbAC?.IsChecked == true ? CircuitAnalysisMode.AC : CircuitAnalysisMode.DC;
            UpdateAnalysisModeUi();
            RefreshSimplifiedOpportunity();
            InvalidateResult();
            if (IsLoaded)
                SetStatus(_analysisSettings.Mode switch
                {
                    CircuitAnalysisMode.ThreePhase => "3Φ-режим: откройте конструктор, задайте одну фазную ЭДС и сопротивления ветвей R+jX.",
                    CircuitAnalysisMode.AC => "AC-режим: источники задаются RMS и фазой; доступны МУП, МКТ, законы Кирхгофа, МЭГ, P/Q/S, мгновенные функции и векторные данные.",
                    _ => "DC-режим: восстановлены правила C=разрыв, L=КЗ."
                });
        }

        private void TxtFrequency_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtFrequency == null) return;
            if (TryParseUiNumber(TxtFrequency.Text, out double f) && f > 0)
            {
                _analysisSettings.FrequencyHz = f;
                _threePhaseInput.FrequencyHz = f;
                if (_analysisSettings.Mode == CircuitAnalysisMode.ThreePhase)
                    UpdateThreePhaseSummary();
            }
            RefreshSimplifiedOpportunity();
            InvalidateResult();
        }

        private void UpdateAnalysisModeUi()
        {
            // This method can be reached from Checked/SelectionChanged events while
            // InitializeComponent() is still building the named controls.
            if (PanelFrequency == null || PanelThreePhase == null) return;
            bool ac = _analysisSettings.Mode == CircuitAnalysisMode.AC;
            bool three = _analysisSettings.Mode == CircuitAnalysisMode.ThreePhase;
            bool phasor = ac || three;
            PanelFrequency.Visibility = phasor ? Visibility.Visible : Visibility.Collapsed;
            PanelThreePhase.Visibility = three ? Visibility.Visible : Visibility.Collapsed;

            if (three)
                UpdateThreePhaseSummary();

            foreach (var rb in new[] { RbMKT, RbKirchhoff, RbMUP, RbEqGen, RbPotential, RbVector })
                if (rb != null) rb.IsEnabled = !three;

            if (RbVector != null && !three)
            {
                RbVector.IsEnabled = ac;
                if (!ac && RbVector.IsChecked == true) RbMUP.IsChecked = true;
            }
            if (RbPotential != null && !three)
            {
                RbPotential.IsEnabled = !ac;
                if (ac && RbPotential.IsChecked == true) RbMUP.IsChecked = true;
            }
            PanelSimplifiedOffer.Visibility = three ? Visibility.Collapsed : PanelSimplifiedOffer.Visibility;

            Title = _analysisSettings.Mode switch
            {
                CircuitAnalysisMode.ThreePhase => "ElectroCalc — Трёхфазные цепи",
                CircuitAnalysisMode.AC => "ElectroCalc — Синусоидальный установившийся режим",
                _ => "ElectroCalc — Цепи постоянного тока"
            };
            foreach (var ctrl in _elements)
            {
                ctrl.DisplayAnalysisMode = _analysisSettings.Mode;
                ctrl.UpdateVisual();
            }
            if (_selectedElem != null) ShowElementProperties(_selectedElem);
        }

        private static bool TryParseUiNumber(string text, out double value) =>
            double.TryParse(text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) &&
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static string UnitFor(ElementType t) => t switch
        {
            ElementType.Resistor      => "Ом",
            ElementType.VoltageSource => "В",
            ElementType.CurrentSource => "А",
            ElementType.Capacitor     => "Ф",
            ElementType.Inductor      => "Гн",
            _ => ""
        };

        private void InvalidateResult()
        {
            if (_lastResult == null) return;
            _lastResult = null;
            if (_vectorDiagramWindow?.IsVisible == true)
                _vectorDiagramWindow.Close();
            _vectorDiagramWindow = null;
            BtnOpenVectorDiagrams.Visibility = Visibility.Collapsed;
            foreach (var control in _elements)
            {
                control.Element.LastCurrentPhasor = System.Numerics.Complex.Zero;
                control.UpdateVisual();
            }
            LstSteps.ItemsSource = null;
            TxtStepDetail.Text = string.Empty;
            GridResults.ItemsSource = null;
            TxtPowerGen.Text = "ΣS_ист = —";
            TxtPowerCon.Text = "ΣS_потр = —";
            TxtPowerDelta.Text = "|ΔS| = —";
            PowerBalanceBorder.Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));
        }

        private void RefreshSimplifiedOpportunity()
        {
            if (PanelSimplifiedOffer == null) return;
            if (_analysisSettings.Mode == CircuitAnalysisMode.ThreePhase)
            {
                _simplifiedOpportunity = null;
                PanelSimplifiedOffer.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                _simplifiedOpportunity = ThreeBranchSimplifiedDetector.Detect(_graph, _analysisSettings);
            }
            catch
            {
                _simplifiedOpportunity = null;
            }

            PanelSimplifiedOffer.Visibility = _simplifiedOpportunity != null ? Visibility.Visible : Visibility.Collapsed;
            if (_simplifiedOpportunity != null)
                TxtSimplifiedOffer.Text = _simplifiedOpportunity.Description;
        }

        private void BtnSimplifiedCalculate_Click(object sender, RoutedEventArgs e)
        {
            string? warning = RebuildGraphFromSchematic();
            if (!string.IsNullOrWhiteSpace(warning))
            {
                SetStatus($"❌ {warning}");
                return;
            }

            var analysis = GetAnalysisSettings();
            _simplifiedOpportunity = ThreeBranchSimplifiedDetector.Detect(_graph, analysis);
            if (_simplifiedOpportunity == null)
            {
                SetStatus("❌ Схема больше не соответствует условиям упрощённого трёхветвевого расчёта.");
                RefreshSimplifiedOpportunity();
                return;
            }

            var result = new ThreeBranchSimplifiedSolver(_graph, analysis, _simplifiedOpportunity).Solve();
            _lastResult = result;
            DisplayResult(result);

            foreach (var element in _elements)
                element.Element.LastCurrentPhasor = System.Numerics.Complex.Zero;
            foreach (var row in result.BranchResults)
                foreach (var element in row.Branch.Elements)
                    element.LastCurrentPhasor = row.CurrentPhasor * row.Branch.GetElementDirection(element);
            foreach (var control in _elements) control.UpdateVisual();

            SetStatus(result.Success
                ? $"✓ Выполнен упрощённый трёхветвевой расчёт. {(result.PowerBalanceOk ? "Баланс мощностей ✓" : "⚠ Баланс мощностей не сошёлся")}"
                : $"❌ {result.ErrorMessage}");
        }

        private void SetStatus(string msg) => TxtStatus.Text = msg;
    }
}
