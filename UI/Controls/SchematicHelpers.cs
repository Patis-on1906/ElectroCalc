using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ElectroCalc.Core.Models;

namespace ElectroCalc.UI.Views
{
    // ── Port key: uniquely identifies one connection point ───────────────────
    internal readonly struct PortKey : IEquatable<PortKey>
    {
        public Guid OwnerId { get; }   // ElementControl.Id or JunctionControl.Id
        public bool IsPortA { get; }   // true=A, false=B/junction

        public PortKey(Guid id, bool isPortA) { OwnerId = id; IsPortA = isPortA; }
        public bool Equals(PortKey o) => OwnerId == o.OwnerId && IsPortA == o.IsPortA;
        public override bool Equals(object? o) => o is PortKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(OwnerId, IsPortA);
        public static bool operator ==(PortKey a, PortKey b) => a.Equals(b);
        public static bool operator !=(PortKey a, PortKey b) => !a.Equals(b);
    }

    // ── Junction dot visual ──────────────────────────────────────────────────
    internal class JunctionControl
    {
        public Ellipse Visual { get; }
        public Guid    Id     { get; } = Guid.NewGuid();

        public Point Position
        {
            get => new(Canvas.GetLeft(Visual) + 5, Canvas.GetTop(Visual) + 5);
            set { Canvas.SetLeft(Visual, value.X - 5); Canvas.SetTop(Visual, value.Y - 5); }
        }

        public event Action<JunctionControl>? DragStarted;
        public event Action<JunctionControl>? Selected;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                Visual.Stroke = value
                    ? new SolidColorBrush(Colors.OrangeRed)
                    : new SolidColorBrush(Color.FromRgb(21, 101, 192));
            }
        }

        public JunctionControl()
        {
            Visual = new Ellipse
            {
                Width = 10, Height = 10,
                Fill   = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness  = 1.5,
                IsHitTestVisible = true,
                Cursor = Cursors.Cross,
                ToolTip = "Точка соединения — тяни ЛКМ для провода; ПКМ — выбрать"
            };
            Visual.MouseLeftButtonDown += (_, e) => { e.Handled = true; DragStarted?.Invoke(this); };
            Visual.MouseRightButtonDown += (_, e) => { e.Handled = true; Selected?.Invoke(this); };
        }
    }

    // ── Wire visual ──────────────────────────────────────────────────────────
    internal class WireVisual
    {
        public PortKey        FromKey { get; }
        public PortKey        ToKey   { get; }
        public CircuitBranch? Branch  { get; private set; }

        public void SetBranch(CircuitBranch b) => Branch = b;

        private readonly Polyline    _line;
        private readonly Func<Point> _getFrom;
        private readonly Func<Point> _getTo;

        public event Action<WireVisual>? Clicked;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected  = value;
                _line.Stroke = value
                    ? new SolidColorBrush(Colors.OrangeRed)
                    : new SolidColorBrush(Color.FromRgb(30, 30, 30));
            }
        }

        public WireVisual(PortKey fromKey, PortKey toKey,
                          Func<Point> getFrom, Func<Point> getTo,
                          CircuitBranch? branch)
        {
            FromKey  = fromKey;
            ToKey    = toKey;
            _getFrom = getFrom;
            _getTo   = getTo;
            Branch   = branch;

            _line = new Polyline
            {
                Stroke           = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                StrokeThickness  = 2,
                StrokeLineJoin   = PenLineJoin.Round,
                IsHitTestVisible = true,
                Cursor           = Cursors.Hand
            };
            _line.MouseLeftButtonDown += (_, e) => { e.Handled = true; Clicked?.Invoke(this); };
        }

        public void AddToCanvas(Canvas c)
        {
            if (!c.Children.Contains(_line))
                c.Children.Insert(0, _line);
        }

        public void RemoveFromCanvas(Canvas c) => c.Children.Remove(_line);

        public void UpdateGeometry()
        {
            var s  = _getFrom();
            var e  = _getTo();
            double mx = (s.X + e.X) / 2;
            _line.Points.Clear();
            _line.Points.Add(s);
            _line.Points.Add(new Point(mx, s.Y));
            _line.Points.Add(new Point(mx, e.Y));
            _line.Points.Add(e);
        }
    }
}
