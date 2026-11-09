using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>
/// One step of the workflow as the interface presents it: a group of graph nodes (or none, e.g. the terrain), plus
/// any parameters its page shows besides the nodes, supplied by the composition.
/// </summary>
public sealed class Stage
{
    public Stage(string title, IReadOnlyList<int> nodes)
    {
        Title = title;
        Nodes = nodes;
    }

    public string Title { get; }
    public IReadOnlyList<int> Nodes { get; }

    /// <summary>Fills a "参数" section above the node cards (for stages with settings outside the graph).</summary>
    public Action<Inspector>? Parameters { get; set; }

    /// <summary>Status line for a stage without nodes.</summary>
    public Func<string>? Caption { get; set; }

    /// <summary>One stage per run of consecutive nodes with the same group, in graph order.</summary>
    public static List<Stage> FromGraph(CityGraph graph)
    {
        var stages = new List<Stage>();
        for (int i = 0; i < graph.Nodes.Count;)
        {
            int start = i;
            string group = graph.Nodes[i].Group;
            while (i < graph.Nodes.Count && graph.Nodes[i].Group == group)
                i++;
            stages.Add(new Stage(group, Enumerable.Range(start, i - start).ToArray()));
        }

        return stages;
    }

    /// <summary>The status that most needs attention, how many nodes are valid, and their total run time.</summary>
    public (NodeStatus Status, int Valid, double Seconds) Summarize(CityGraph graph)
    {
        if (Nodes.Count == 0)
            return (NodeStatus.Valid, 0, 0);
        var statuses = Nodes.Select(graph.Status).ToList();
        var worst = new[] { NodeStatus.Running, NodeStatus.Failed, NodeStatus.Stale, NodeStatus.Blocked, NodeStatus.Empty }
            .FirstOrDefault(s => statuses.Contains(s), NodeStatus.Valid);
        return (worst, statuses.Count(s => s == NodeStatus.Valid), Nodes.Sum(graph.Seconds));
    }
}

/// <summary>A round status light; a running node pulses.</summary>
public partial class StatusDot : Control
{
    private NodeStatus _status;

    public StatusDot(float size = 10)
    {
        CustomMinimumSize = new Vector2(size, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public NodeStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            QueueRedraw();
        }
    }

    public override void _Process(double delta)
    {
        if (_status == NodeStatus.Running)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var color = UiStyle.StatusColor(_status);
        float r = Size.X / 2;
        if (_status == NodeStatus.Running)
            color.A = 0.55f + 0.45f * Mathf.Sin((float)Time.GetTicksMsec() / 160f);
        DrawCircle(Size / 2, r, color, antialiased: true);
    }
}
