using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ElectroCalc.Core.Models;

namespace ElectroCalc.UI.Controls
{
    /// <summary>
    /// A self-contained visual element on the canvas.
    /// Has two connection ports (left/right or top/bottom).
    /// Supports drag-and-drop movement; fires PortDragStarted when user drags from a port.
    /// </summary>
    public class ElementControl : Canvas
    {
        // ── Public state ─────────────────────────────────────────────────────
        public CircuitElement Element  { get; }
        public Guid           Id       { get; } = Guid.NewGuid();

        public CircuitAnalysisMode DisplayAnalysisMode { get; set; } = CircuitAnalysisMode.DC;

        /// <summary>Canvas position of the element's centre.</summary>
        public Point Centre
        {
            get => new(Canvas.GetLeft(this) + Width / 2,
                       Canvas.GetTop(this)  + Height / 2);
        }

        private int _rotationDegrees;
        /// <summary>Clockwise rotation about Centre; port identities and polarity stay unchanged.</summary>
        public int RotationDegrees
        {
            get => _rotationDegrees;
            set
            {
                if (value is not (0 or 90 or 180 or 270))
                    throw new ArgumentOutOfRangeException(nameof(value), "Угол должен быть 0, 90, 180 или 270°.");
                if (_rotationDegrees == value) return;
                _rotationDegrees = value;
                RenderTransform = new RotateTransform(value);
                Moved?.Invoke(this);
            }
        }

        public void RotateClockwise() => RotationDegrees = (RotationDegrees + 90) % 360;

        private Vector PortOffset => RotationDegrees switch
        {
            90 => new Vector(0, Width / 2),
            180 => new Vector(-Width / 2, 0),
            270 => new Vector(0, -Width / 2),
            _ => new Vector(Width / 2, 0)
        };

        public Point PortA => Centre - PortOffset;
        public Point PortB => Centre + PortOffset;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; UpdateVisual(); }
        }

        // ── Events ───────────────────────────────────────────────────────────
        /// <summary>Fired when user starts dragging from a port. bool = isPortA.</summary>
        public event Action<ElementControl, bool>? PortDragStarted;
        /// <summary>Fired when element is moved.</summary>
        public event Action<ElementControl>?       Moved;
        /// <summary>Fired on left click (not drag, not port).</summary>
        public event Action<ElementControl>?       Selected;

        // ── Visual parts ─────────────────────────────────────────────────────
        private readonly Border    _body;
        private readonly TextBlock _topLabel;
        private readonly TextBlock _botLabel;
        private readonly Ellipse   _portA;
        private readonly Ellipse   _portB;
        private readonly TextBlock _portALabel;
        private readonly TextBlock _portBLabel;

        private const double PORT_R    = 6;
        private const double ELEM_W    = 80;
        private const double ELEM_H    = 36;

        // ── Drag state ───────────────────────────────────────────────────────
        private bool  _dragging;
        private Point _dragStart;       // mouse position at drag start
        private Point _elemStartPos;    // element Left/Top at drag start
        private bool  _dragMoved;

        // ── Ctor ─────────────────────────────────────────────────────────────
        public ElementControl(CircuitElement element)
        {
            Element = element;
            Width   = ELEM_W + PORT_R * 2;
            Height  = ELEM_H;
            Cursor  = Cursors.SizeAll;
            Focusable = true;
            RenderTransformOrigin = new Point(0.5, 0.5);
            ToolTip = "Порты: A слева, B справа. Выберите элемент и нажмите R — поворот на 90° по часовой стрелке";

            // Body rectangle
            _body = new Border
            {
                Width           = ELEM_W,
                Height          = ELEM_H,
                CornerRadius    = new CornerRadius(4),
                BorderThickness = new Thickness(2),
            };
            var inner = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3)
            };
            _topLabel = new TextBlock
            {
                FontSize      = 11,
                FontWeight    = FontWeights.Bold,
                TextAlignment = TextAlignment.Center
            };
            _botLabel = new TextBlock
            {
                FontSize      = 9,
                TextAlignment = TextAlignment.Center,
                Opacity       = 0.85
            };
            inner.Children.Add(_topLabel);
            inner.Children.Add(_botLabel);
            _body.Child = inner;
            SetLeft(_body, PORT_R);
            SetTop (_body, 0);
            Children.Add(_body);

            // Port A (left)
            _portA = MakePort();
            SetLeft(_portA, -PORT_R);
            SetTop (_portA, ELEM_H / 2 - PORT_R);
            Children.Add(_portA);

            // Port B (right)
            _portB = MakePort();
            SetLeft(_portB, ELEM_W + PORT_R);
            SetTop (_portB, ELEM_H / 2 - PORT_R);
            Children.Add(_portB);

            // Wire port events
            _portA.MouseLeftButtonDown += (s, e) => { e.Handled = true; PortDragStarted?.Invoke(this, true);  };
            _portB.MouseLeftButtonDown += (s, e) => { e.Handled = true; PortDragStarted?.Invoke(this, false); };
            _portA.Cursor = Cursors.Cross;
            _portB.Cursor = Cursors.Cross;
            _portA.ToolTip = "Порт A";
            _portB.ToolTip = "Порт B";

            // Port identities remain visible after rotation so source
            // orientation A→B/B→A can be read directly from the schematic.
            _portALabel = MakePortLabel("A");
            SetLeft(_portALabel, -PORT_R);
            SetTop(_portALabel, ELEM_H / 2 - PORT_R);
            Children.Add(_portALabel);
            _portBLabel = MakePortLabel("B");
            SetLeft(_portBLabel, ELEM_W + PORT_R);
            SetTop(_portBLabel, ELEM_H / 2 - PORT_R);
            Children.Add(_portBLabel);

            // Drag events on body
            _body.MouseLeftButtonDown += Body_MouseDown;
            _body.MouseMove           += Body_MouseMove;
            _body.MouseLeftButtonUp   += Body_MouseUp;

            UpdateVisual();
        }

        // ── Port factory ─────────────────────────────────────────────────────
        private static Ellipse MakePort() => new()
        {
            Width  = PORT_R * 2,
            Height = PORT_R * 2,
            Fill   = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
            StrokeThickness = 2,
            Cursor = Cursors.Cross
        };

        private static TextBlock MakePortLabel(string text) => new()
        {
            Width = PORT_R * 2,
            Height = PORT_R * 2,
            Text = text,
            FontSize = 7,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };

        // ── Drag ─────────────────────────────────────────────────────────────
        private void Body_MouseDown(object s, MouseButtonEventArgs e)
        {
            // Do not bubble double clicks to the canvas's element picker.
            e.Handled = true;
            if (e.ClickCount == 1)
            {
                _dragging     = true;
                _dragMoved    = false;
                _dragStart    = e.GetPosition(Parent as IInputElement);
                _elemStartPos = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                _body.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Body_MouseMove(object s, MouseEventArgs e)
        {
            if (!_dragging) return;
            var cur  = e.GetPosition(Parent as IInputElement);
            var dx   = cur.X - _dragStart.X;
            var dy   = cur.Y - _dragStart.Y;

            if (!_dragMoved && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                _dragMoved = true;

            if (_dragMoved)
            {
                double nx = Snap(_elemStartPos.X + dx);
                double ny = Snap(_elemStartPos.Y + dy);
                Canvas.SetLeft(this, nx);
                Canvas.SetTop (this, ny);
                Moved?.Invoke(this);
            }
        }

        private void Body_MouseUp(object s, MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                _body.ReleaseMouseCapture();
                _dragging = false;
                if (!_dragMoved)
                {
                    Selected?.Invoke(this);
                    Focus();
                }
                e.Handled = true;
            }
        }

        // ── Visual update ─────────────────────────────────────────────────────
        public void UpdateVisual()
        {
            var (color, symbol) = ElementStyle(Element.Type);
            var brush = new SolidColorBrush(color);

            _body.Background   = IsSelected
                ? new SolidColorBrush(Color.FromRgb(255, 243, 224))
                : new SolidColorBrush(color);
            _body.BorderBrush  = IsSelected
                ? new SolidColorBrush(Colors.OrangeRed)
                : new SolidColorBrush(DarkenColor(color));

            _topLabel.Foreground = IsSelected ? brush : Brushes.White;
            _botLabel.Foreground = IsSelected
                ? new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B))
                : new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));

            string phasePrefix = DisplayAnalysisMode == CircuitAnalysisMode.ThreePhase && Element.PhaseAssignment != ThreePhasePhase.None
                ? $"[{Element.PhaseAssignment}] " : string.Empty;
            string orientation = Element.Type switch
            {
                ElementType.CurrentSource => Element.IsPositiveAtStart ? " A→B" : " B→A",
                ElementType.VoltageSource => Element.IsPositiveAtStart ? " +A" : " +B",
                _ => string.Empty
            };
            _topLabel.Text = $"{phasePrefix}{symbol} {Element.Name}{orientation}";
            _botLabel.Text = FormatValue(Element);

            ToolTip = Element.Type switch
            {
                ElementType.CurrentSource => $"Источник тока: {(Element.IsPositiveAtStart ? "A → B" : "B → A")}. R — поворот на 90°.",
                ElementType.VoltageSource => $"Источник ЭДС: положительный вывод {(Element.IsPositiveAtStart ? "A" : "B")}. R — поворот на 90°.",
                _ => "Порты: A и B. R — поворот на 90° по часовой стрелке."
            };

            // Show current after solve
            if (Element.LastCurrentPhasor.Magnitude > 1e-12)
                _botLabel.Text += DisplayAnalysisMode != CircuitAnalysisMode.DC
                    ? $"\nI={Phasor.Polar(Element.LastCurrentPhasor, "0.###", "0.#")} А"
                    : $"\n{Element.LastCurrent:F3} А";

            // Port color
            var portStroke = new SolidColorBrush(IsSelected
                ? Colors.OrangeRed : Color.FromRgb(21, 101, 192));
            _portA.Stroke = portStroke;
            _portB.Stroke = portStroke;
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static (Color color, string symbol) ElementStyle(ElementType t) => t switch
        {
            ElementType.Resistor       => (Color.FromRgb(21,  101, 192), "R"),
            ElementType.VoltageSource  => (Color.FromRgb(183,  28,  28), "E"),
            ElementType.CurrentSource  => (Color.FromRgb( 27,  94,  32), "J"),
            ElementType.Capacitor      => (Color.FromRgb( 74,  20, 140), "C"),
            ElementType.Inductor       => (Color.FromRgb(230,  81,   0), "L"),
            _ => (Colors.Gray, "?")
        };

        private string FormatValue(CircuitElement e) => e.Type switch
        {
            ElementType.Resistor       => $"{e.Value} Ом",
            ElementType.VoltageSource  => DisplayAnalysisMode != CircuitAnalysisMode.DC ? $"{e.Value} В RMS ∠{e.PhaseDegrees:0.##}°" : $"{e.Value} В",
            ElementType.CurrentSource  => DisplayAnalysisMode != CircuitAnalysisMode.DC ? $"{e.Value} А RMS ∠{e.PhaseDegrees:0.##}°" : $"{e.Value} А",
            ElementType.Capacitor      => $"{e.Value} Ф",
            ElementType.Inductor       => $"{e.Value} Гн",
            _ => e.Value.ToString("G")
        };

        private static Color DarkenColor(Color c) =>
            Color.FromRgb((byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7));

        private static double Snap(double v, double grid = 20) =>
            Math.Round(v / grid) * grid;
    }
}
