using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;

namespace ElectroCalc.Core.Models
{
    /// <summary>
    /// Существенный электрический узел схемы. Последовательные соединения двух
    /// элементов не обязаны становиться отдельными CircuitNode: такие точки
    /// сворачиваются в одну CircuitBranch при построении расчётного графа.
    /// </summary>
    public class CircuitNode
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Label { get; set; } = string.Empty;
        public Point Position { get; set; }
        public Complex PotentialPhasor { get; set; } = Complex.Zero;
        public double Potential
        {
            get => PotentialPhasor.Real;
            set => PotentialPhasor = new Complex(value, 0.0);
        }

        public override string ToString() => Label;
    }

    /// <summary>
    /// Ветвь между двумя существенными узлами. Внутри ветви может быть несколько
    /// последовательных элементов. Направление ветви: StartNode -> EndNode.
    /// </summary>
    public class CircuitBranch
    {
        private readonly Dictionary<Guid, int> _elementDirections = new();

        public Guid Id { get; } = Guid.NewGuid();
        public CircuitNode StartNode { get; set; } = null!;
        public CircuitNode EndNode   { get; set; } = null!;

        /// <summary>Последовательные элементы в порядке StartNode -> EndNode.</summary>
        public List<CircuitElement> Elements { get; } = new();

        /// <summary>Расчётный ток ветви в направлении StartNode -> EndNode.</summary>
        public Complex CurrentPhasor { get; set; } = Complex.Zero;
        public double Current
        {
            get => CurrentPhasor.Real;
            set => CurrentPhasor = new Complex(value, 0.0);
        }

        /// <summary>
        /// Добавляет элемент в ветвь. direction = +1, если направление ветви
        /// совпадает с PortA -> PortB элемента, иначе -1.
        /// </summary>
        public void AddElement(CircuitElement element, int direction = 1)
        {
            Elements.Add(element);
            _elementDirections[element.Id] = direction >= 0 ? 1 : -1;
        }

        public int GetElementDirection(CircuitElement element) =>
            _elementDirections.TryGetValue(element.Id, out int d) ? d : 1;

        /// <summary>Суммарное последовательное сопротивление ветви для режима DC.</summary>
        public double TotalResistance =>
            Elements.Sum(e => e.Type switch
            {
                ElementType.Resistor      => Math.Max(0.0, e.Value),
                ElementType.VoltageSource => Math.Max(0.0, e.InternalResistance),
                ElementType.CurrentSource => Math.Max(0.0, e.InternalResistance),
                // В установившемся режиме постоянного тока L = КЗ, C = разрыв.
                ElementType.Inductor      => 0.0,
                ElementType.Capacitor     => 0.0,
                _ => 0.0
            });

        /// <summary>
        /// Алгебраическая сумма ЭДС в направлении StartNode -> EndNode.
        /// Положительная ЭДС означает падение напряжения +E от Start к End,
        /// то есть Vstart - Vend = R*I + E.
        /// </summary>
        public double TotalEMF =>
            Elements.Sum(e =>
            {
                if (e.Type != ElementType.VoltageSource) return 0.0;
                int orientation = GetElementDirection(e);
                int polarity = e.IsPositiveAtStart ? 1 : -1;
                return orientation * polarity * e.Value;
            });

        /// <summary>
        /// Заданный ток идеального источника тока в направлении StartNode -> EndNode.
        /// Для нескольких источников в одной последовательной ветви их значения
        /// должны совпадать; проверка выполняется DcBranchModel.
        /// </summary>
        public IEnumerable<double> OrientedCurrentSources =>
            Elements
                .Where(e => e.Type == ElementType.CurrentSource)
                .Select(e => GetElementDirection(e) * (e.IsPositiveAtStart ? 1.0 : -1.0) * e.Value);

        public double TotalCurrentSource => OrientedCurrentSources.Sum();

        public bool HasVoltageSource => Elements.Any(e => e.Type == ElementType.VoltageSource);
        public bool HasCurrentSource => Elements.Any(e => e.Type == ElementType.CurrentSource);
        public bool HasCapacitor     => Elements.Any(e => e.Type == ElementType.Capacitor);
        public bool HasInductor      => Elements.Any(e => e.Type == ElementType.Inductor);

        public string ElementNames => Elements.Count == 0
            ? "провод"
            : string.Join(" + ", Elements.Select(e => e.Name));

        public override string ToString() =>
            $"{StartNode?.Label}→{EndNode?.Label} [{ElementNames}]";
    }

    public class CircuitGraph
    {
        public List<CircuitNode>   Nodes    { get; } = new();
        public List<CircuitBranch> Branches { get; } = new();

        public CircuitNode AddNode(string label, Point position)
        {
            var node = new CircuitNode { Label = label, Position = position };
            Nodes.Add(node);
            return node;
        }

        public CircuitBranch AddBranch(CircuitNode start, CircuitNode end)
        {
            var branch = new CircuitBranch { StartNode = start, EndNode = end };
            Branches.Add(branch);
            return branch;
        }

        public void RemoveNode(CircuitNode node)
        {
            Branches.RemoveAll(b => b.StartNode == node || b.EndNode == node);
            Nodes.Remove(node);
        }

        public void RemoveBranch(CircuitBranch branch) => Branches.Remove(branch);

        public IEnumerable<CircuitBranch> BranchesOf(CircuitNode node) =>
            Branches.Where(b => b.StartNode == node || b.EndNode == node);

        public void RelabelNodes()
        {
            for (int i = 0; i < Nodes.Count; i++)
                Nodes[i].Label = i.ToString();
        }

        public int ConnectedComponentCount
        {
            get
            {
                if (Nodes.Count == 0) return 0;
                var visited = new HashSet<CircuitNode>();
                int count = 0;

                foreach (var start in Nodes)
                {
                    if (!visited.Add(start)) continue;
                    count++;
                    var queue = new Queue<CircuitNode>();
                    queue.Enqueue(start);
                    while (queue.Count > 0)
                    {
                        var n = queue.Dequeue();
                        foreach (var b in BranchesOf(n))
                        {
                            var other = b.StartNode == n ? b.EndNode : b.StartNode;
                            if (visited.Add(other)) queue.Enqueue(other);
                        }
                    }
                }
                return count;
            }
        }

        public int IndependentLoopCount =>
            Branches.Count - Nodes.Count + ConnectedComponentCount;
    }
}
