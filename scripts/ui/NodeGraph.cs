using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>What the scene shows: the outputs of one node, of every node of one stage, or (none) the whole result.</summary>
public readonly record struct GraphFocus(int Stage, int Node)
{
    public static readonly GraphFocus None = new(-1, -1);

    public bool IsNone => Stage < 0;
}

/// <summary>
/// The town graph drawn as a graph: one column per stage, a box per node with its status, and a curve from each node
/// to every node that reads its output. Hovering or focusing a node colors what it reads from (blue) and who reads it
/// (amber). Clicking a node focuses it, clicking a stage header focuses the stage, clicking empty space clears the
/// focus. The whole drawing scales down to fit the panel.
/// </summary>
public partial class NodeGraph : Control
{
    private const float Pad = 10, ColumnWidth = 116, TerrainWidth = 60, ColumnGap = 22, HeaderHeight = 24, BoxHeight = 22, BoxGap = 4;
    private const int FontSize = 12;
    private static readonly Color Upstream = new("6fb3e8"), Downstream = new("e3b25f"), Edge = new(1, 1, 1, 0.11f);

    private CityGraph _graph = null!;
    private IReadOnlyList<Stage> _stages = null!;
    private Setting<GraphFocus> _focus = null!;
    private Rect2[] _headers = Array.Empty<Rect2>();
    private Rect2[] _boxes = Array.Empty<Rect2>();
    private int[] _column = Array.Empty<int>();
    private int[] _stageOf = Array.Empty<int>();
    private float _scale = 1;
    private Vector2 _offset;
    private int _hoverNode = -1, _hoverStage = -1;

    public void Bind(CityGraph graph, IReadOnlyList<Stage> stages, Setting<GraphFocus> focus)
    {
        _graph = graph;
        _stages = stages;
        _focus = focus;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        _stageOf = new int[graph.Nodes.Count];
        _column = new int[graph.Nodes.Count];
        for (int s = 0; s < stages.Count; s++)
        {
            foreach (int n in stages[s].Nodes)
                (_stageOf[n], _column[n]) = (s, s);
        }

        graph.Changed += QueueRedraw;
        focus.Changed += _ => QueueRedraw();
        Resized += Arrange;
        Arrange();
    }

    private void Arrange()
    {
        if (_stages == null)
            return;
        _headers = new Rect2[_stages.Count];
        _boxes = new Rect2[_graph.Nodes.Count];
        float x = Pad;
        int rows = _stages.Max(s => s.Nodes.Count);
        for (int s = 0; s < _stages.Count; s++)
        {
            float width = _stages[s].Nodes.Count == 0 ? TerrainWidth : ColumnWidth;
            _headers[s] = new Rect2(x, Pad, width, HeaderHeight);
            float y = Pad + HeaderHeight + 6;
            foreach (int n in _stages[s].Nodes)
            {
                _boxes[n] = new Rect2(x, y, width, BoxHeight);
                y += BoxHeight + BoxGap;
            }

            x += width + ColumnGap;
        }

        var natural = new Vector2(x - ColumnGap + Pad, Pad * 2 + HeaderHeight + 6 + rows * (BoxHeight + BoxGap) - BoxGap);
        _scale = Size.X > 0 && Size.Y > 0 ? Mathf.Min(1, Mathf.Min(Size.X / natural.X, Size.Y / natural.Y)) : 1;
        _offset = new Vector2(Mathf.Max(0, (Size.X / _scale - natural.X) / 2), 0);
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_graph != null && _graph.IsRunning)
            QueueRedraw();
    }

    public override void _Draw()
    {
        if (_graph == null)
            return;
        DrawSetTransform(_offset * _scale, 0, new Vector2(_scale, _scale));
        var font = GetThemeDefaultFont();
        int active = _hoverNode >= 0 ? _hoverNode : _focus.Value.Node;
        var upstream = active >= 0 ? _graph.Sources(active).Values.ToHashSet() : new HashSet<int>();
        var downstream = active >= 0 ? _graph.Dependents(active).ToHashSet() : new HashSet<int>();

        // Edges: faint first, then the active node's inputs and outputs on top.
        var edges = Enumerable.Range(0, _graph.Nodes.Count)
            .SelectMany(to => _graph.Sources(to).Values.Distinct().Select(from => (from, to))).ToList();
        foreach (var (from, to) in edges.Where(e => e.to != active && e.from != active))
            DrawPolyline(Curve(from, to), Edge, 1, true);
        foreach (var (from, to) in edges.Where(e => e.to == active || e.from == active))
            DrawPolyline(Curve(from, to), to == active ? Upstream : Downstream, 2, true);

        var focus = _focus.Value;
        for (int s = 0; s < _stages.Count; s++)
        {
            bool selected = focus.Node < 0 && focus.Stage == s;
            var fill = selected ? UiStyle.ButtonPressed.Darkened(0.15f)
                : s == _hoverStage ? UiStyle.ButtonHover : new Color(1, 1, 1, 0.05f);
            DrawStyleBox(UiStyle.Box(fill, 4, 0, 0, selected ? UiStyle.Accent : null), _headers[s]);
            var (status, _, _) = _stages[s].Summarize(_graph);
            float textRight = _stages[s].Nodes.Count > 0 ? 16 : 6;
            var label = _headers[s].GrowSide(Side.Right, -textRight);
            Text(font, $"{s + 1} {_stages[s].Title}", label, UiStyle.Ink, center: _stages[s].Nodes.Count == 0);
            if (_stages[s].Nodes.Count > 0)
                Dot(_headers[s].End - new Vector2(9, HeaderHeight / 2), 4, status);
        }

        for (int n = 0; n < _boxes.Length; n++)
        {
            var box = _boxes[n];
            bool focused = focus.Node == n;
            Color? border = focused ? UiStyle.Accent : upstream.Contains(n) ? Upstream : downstream.Contains(n) ? Downstream : null;
            var fill = focused ? UiStyle.ButtonPressed.Darkened(0.1f) : n == _hoverNode ? UiStyle.ButtonHover : UiStyle.Card;
            DrawStyleBox(UiStyle.Box(fill, 4, 0, 0, border), box);
            Dot(box.Position + new Vector2(10, BoxHeight / 2), 3.5f, _graph.Status(n));
            Text(font, _graph.Nodes[n].Title, new Rect2(box.Position + new Vector2(18, 0), box.Size - new Vector2(22, 0)),
                focused || n == _hoverNode ? Colors.White : UiStyle.Muted);
        }
    }

    /// <summary>
    /// A curve from the right of the source box to the left of the reader's box; between nodes of one column, a loop
    /// out to the left.
    /// </summary>
    private Vector2[] Curve(int from, int to)
    {
        Vector2 a, b, c1, c2;
        if (_column[from] == _column[to])
        {
            a = new Vector2(_boxes[from].Position.X, _boxes[from].GetCenter().Y);
            b = new Vector2(_boxes[to].Position.X, _boxes[to].GetCenter().Y);
            float bulge = 8 + 3 * MathF.Sqrt(Math.Abs(to - from));
            (c1, c2) = (a - new Vector2(bulge, 0), b - new Vector2(bulge, 0));
        }
        else
        {
            a = new Vector2(_boxes[from].End.X, _boxes[from].GetCenter().Y);
            b = new Vector2(_boxes[to].Position.X, _boxes[to].GetCenter().Y);
            float dx = Mathf.Max(16, (b.X - a.X) * 0.45f);
            (c1, c2) = (a + new Vector2(dx, 0), b - new Vector2(dx, 0));
        }

        var points = new Vector2[17];
        for (int i = 0; i < points.Length; i++)
        {
            float t = i / (float)(points.Length - 1), u = 1 - t;
            points[i] = u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
        }

        return points;
    }

    private void Dot(Vector2 center, float radius, NodeStatus status)
    {
        var color = UiStyle.StatusColor(status);
        if (status == NodeStatus.Running)
            color.A = 0.55f + 0.45f * Mathf.Sin((float)Time.GetTicksMsec() / 160f);
        DrawCircle(center, radius, color, antialiased: true);
    }

    private void Text(Font font, string text, Rect2 rect, Color color, bool center = false)
    {
        float baseline = rect.Position.Y + (rect.Size.Y + font.GetAscent(FontSize) - font.GetDescent(FontSize)) / 2;
        float left = center ? rect.Position.X : rect.Position.X + 8;
        DrawString(font, new Vector2(left, baseline), text, center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            rect.End.X - left, FontSize, color);
    }

    private Vector2 Local(Vector2 position) => position / _scale - _offset;

    private (int Stage, int Node) Hit(Vector2 position)
    {
        var p = Local(position);
        for (int n = 0; n < _boxes.Length; n++)
        {
            if (_boxes[n].HasPoint(p))
                return (_stageOf[n], n);
        }

        for (int s = 0; s < _headers.Length; s++)
        {
            if (_headers[s].HasPoint(p))
                return (s, -1);
        }

        return (-1, -1);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_graph == null)
            return;
        if (@event is InputEventMouseMotion motion)
        {
            var (stage, node) = Hit(motion.Position);
            int hoverStage = node < 0 ? stage : -1;
            if (node != _hoverNode || hoverStage != _hoverStage)
            {
                (_hoverNode, _hoverStage) = (node, hoverStage);
                MouseDefaultCursorShape = stage >= 0 ? CursorShape.PointingHand : CursorShape.Arrow;
                QueueRedraw();
            }
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            var (stage, node) = Hit(click.Position);
            _focus.Value = new GraphFocus(stage, node);
            AcceptEvent();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && (_hoverNode >= 0 || _hoverStage >= 0))
        {
            (_hoverNode, _hoverStage) = (-1, -1);
            QueueRedraw();
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        var (stage, node) = Hit(atPosition);
        if (node >= 0)
        {
            var n = _graph.Nodes[node];
            static string Names(IEnumerable<IKey> keys, string none) =>
                keys.Any() ? string.Join("、", keys.Select(k => k.Label)) : none;
            var state = _graph.Status(node);
            var status = state == NodeStatus.Valid ? $"{_graph.Seconds(node):0.00} s" : UiStyle.StatusText(state);
            return UiStyle.Tip(n.Title, n.Description) +
                $"\n\n读　{Names(n.Reads.Concat(n.Edits), "地形")}\n写　{Names(n.Writes.Concat(n.Edits), "—")}\n{status} · 点击只显示它的输出";
        }

        if (stage >= 0)
        {
            return _stages[stage].Nodes.Count == 0
                ? $"{_stages[stage].Title}\n点击只看地形，不显示城市"
                : $"{_stages[stage].Title}\n点击显示这一阶段所有节点的输出";
        }

        return "点击空白处显示完整的城镇";
    }
}
