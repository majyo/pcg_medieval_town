using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>
/// The page of the selected stage, the editor of the town graph: the stage's visualization options, then one card
/// per node with status, the keys it reads and writes, summary, notices and controls generated from its options'
/// [Param] properties. Editing a value invalidates that node and everything downstream of it and raises
/// <see cref="Edited"/>. Pages are built once and swapped on selection.
/// </summary>
public partial class StagePage : VBoxContainer
{
    private readonly List<Card> _cards = new();
    private readonly List<Control> _pages = new();
    private CityGraph _graph = null!;
    private IReadOnlyList<Stage> _stages = null!;
    private Setting<int> _selected = null!;
    private Label _title = null!, _status = null!;
    private ScrollContainer _scroll = null!;

    /// <summary>A node's parameters were changed (the node is already invalidated).</summary>
    public event Action? Edited;

    /// <summary>A node asked to be re-run now.</summary>
    public event Action? RunRequested;

    public void Bind(CityGraph graph, IReadOnlyList<Stage> stages, Setting<int> selected, Action<Inspector>? header = null)
    {
        _graph = graph;
        _stages = stages;
        _selected = selected;
        AddThemeConstantOverride("separation", 6);

        var titleRow = new HBoxContainer();
        _title = UiStyle.Text("", 17, UiStyle.Ink, wrap: false);
        _status = UiStyle.Text("", 12, UiStyle.Muted, wrap: false);
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        titleRow.AddChild(_title);
        titleRow.AddChild(_status);
        AddChild(titleRow);
        if (header != null)
        {
            var common = new Inspector();
            header(common);
            AddChild(common);
        }

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        AddChild(_scroll);
        var pages = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _scroll.AddChild(pages);
        foreach (var stage in stages)
        {
            var page = BuildPage(stage);
            page.Visible = false;
            pages.AddChild(page);
            _pages.Add(page);
        }

        selected.Changed += _ => ShowSelected();
        graph.Changed += Refresh;
        ShowSelected();
    }

    /// <summary>Scrolls the node's card into view and flashes it.</summary>
    public void Reveal(int node)
    {
        var card = _cards.FirstOrDefault(c => c.Node == node);
        if (card == null)
            return;
        card.Body.Visible = true;
        Callable.From(() =>
        {
            _scroll.ScrollVertical = (int)(card.Panel.GlobalPosition.Y - _scroll.GetChild<Control>(0).GlobalPosition.Y) - 4;
            card.Panel.Modulate = new Color(1.6f, 1.6f, 1.3f);
            CreateTween().TweenProperty(card.Panel, "modulate", Colors.White, 0.6);
        }).CallDeferred();
    }

    private void ShowSelected()
    {
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Visible = i == _selected.Value;
        _scroll.ScrollVertical = 0;
        Refresh();
    }

    private Control BuildPage(Stage stage)
    {
        var page = new Inspector();
        page.AddThemeConstantOverride("separation", 6);
        if (stage.Visualization != null)
            stage.Visualization(page.Section("可视化"));
        if (stage.Parameters != null)
            stage.Parameters(page.Section("参数"));
        if (stage.Nodes.Count > 0)
        {
            var nodes = page.Section("节点", tooltip: "每个节点只做一个决策；改动参数只会让它和下游节点过期");
            foreach (int node in stage.Nodes)
                nodes.AddChild(BuildCard(node));
        }

        return page;
    }

    private Control BuildCard(int index)
    {
        var node = _graph.Nodes[index];
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Card, 6, 10, 7));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        panel.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        var dot = new StatusDot(9);
        var title = new Button
        {
            Text = node.Title,
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "折叠 / 展开",
        };
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", UiStyle.Ink);
        title.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        title.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
        title.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
        var state = UiStyle.Text("", 11, UiStyle.Muted, wrap: false);
        state.SizeFlagsHorizontal = SizeFlags.Fill;
        state.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var rerun = UiStyle.Mini("重跑", "让这个节点和下游过期并重新生成");
        rerun.Pressed += () =>
        {
            _graph.Invalidate(index);
            RunRequested?.Invoke();
        };
        head.AddChild(dot);
        head.AddChild(title);
        head.AddChild(state);
        head.AddChild(rerun);
        box.AddChild(head);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 3);
        title.Pressed += () => body.Visible = !body.Visible;
        box.AddChild(body);
        body.AddChild(UiStyle.Text(Ports(node), 11, UiStyle.Faint));
        var summary = UiStyle.Text("", 12, UiStyle.Ink);
        var notices = UiStyle.Text("", 12, UiStyle.Warning);
        body.AddChild(summary);
        body.AddChild(notices);

        var options = node.Options;
        bool hasParams = options.GetType().GetProperties().Any(p => p.GetCustomAttribute<ParamAttribute>() != null);
        Inspector? rows = null;
        if (hasParams)
        {
            body.AddChild(new HSeparator());
            rows = new Inspector();
            rows.Params(options, _ => Changed(index));
            body.AddChild(rows);
            var reset = UiStyle.Mini("默认", "所有参数恢复默认值");
            reset.Pressed += () =>
            {
                var fresh = Activator.CreateInstance(options.GetType())!;
                foreach (var property in options.GetType().GetProperties().Where(p => p.CanWrite))
                    property.SetValue(options, property.GetValue(fresh));
                rows.Refresh();
                Changed(index);
            };
            head.AddChild(reset);
        }

        _cards.Add(new Card(index, panel, body, dot, state, summary, notices));
        return panel;
    }

    private static string Ports(CityNode node)
    {
        static string Names(IEnumerable<IKey> keys) => string.Join("、", keys.Select(k => k.Label));
        var input = node.Reads.Concat(node.Edits).ToList();
        var output = node.Writes.Concat(node.Edits).ToList();
        return $"{(input.Count == 0 ? "地形" : Names(input))} → {Names(output)}";
    }

    private void Changed(int index)
    {
        _graph.Invalidate(index);
        Edited?.Invoke();
    }

    private void Refresh()
    {
        var stage = _stages[_selected.Value];
        _title.Text = stage.Title;
        if (stage.Nodes.Count == 0)
        {
            _status.Text = stage.Caption?.Invoke() ?? "";
        }
        else
        {
            var (status, valid, seconds) = stage.Summarize(_graph);
            _status.Text = $"{valid}/{stage.Nodes.Count} 有效 · {seconds:0.00} s";
            _status.AddThemeColorOverride("font_color", status == NodeStatus.Valid ? UiStyle.Muted : UiStyle.StatusColor(status));
        }

        foreach (var card in _cards)
        {
            var status = _graph.Status(card.Node);
            card.Dot.Status = status;
            card.State.Text = status == NodeStatus.Valid ? $"{_graph.Seconds(card.Node):0.00} s" : UiStyle.StatusText(status);
            card.Summary.Text = status == NodeStatus.Failed ? _graph.Error(card.Node) ?? "" : _graph.Summary(card.Node);
            card.Summary.AddThemeColorOverride("font_color", status == NodeStatus.Failed ? UiStyle.Error : UiStyle.Ink);
            card.Summary.Visible = card.Summary.Text.Length > 0;
            card.Notices.Text = string.Join("\n", _graph.Notices(card.Node).Select(n => "! " + n));
            card.Notices.Visible = card.Notices.Text.Length > 0;
        }
    }

    private sealed record Card(int Node, PanelContainer Panel, VBoxContainer Body, StatusDot Dot, Label State, Label Summary, Label Notices);
}
