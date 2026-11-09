using Godot;

namespace PcgCitySandbox.Ui;

/// <summary>
/// The screen frame: toolbar across the top, tabbed inspector on the right, the node graph docked along the bottom,
/// status bar across the bottom, and compass and legend floating over the bottom-left of the 3D view. The graph and
/// the inspector can be hidden to give the view more room; the overlays follow. Everything outside the panels passes
/// the mouse through to the 3D view.
/// </summary>
public partial class SandboxLayout : Control
{
    private const float Margin = 8, ToolbarHeight = 42, StatusHeight = 26, GraphHeight = 200, InspectorWidth = 388;

    public SandboxLayout()
    {
        Name = "Interface";
        Theme = UiStyle.CreateTheme();
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        Toolbar = new Toolbar();
        Graph = new NodeGraph { Name = "NodeGraph" };
        GraphDock = new PanelContainer { Name = "GraphDock" };
        GraphDock.AddChild(Graph);
        Tabs = new TabContainer { Name = "Tabs", FocusMode = FocusModeEnum.None };
        Inspector = new PanelContainer { Name = "Inspector" };
        Inspector.AddChild(Tabs);
        Status = new StatusBar();
        Compass = new MapCompass();
        Legend = new Legend();
        Overlay = new HBoxContainer { Name = "Overlay", MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Begin };
        Overlay.AddThemeConstantOverride("separation", 8);
        var legendColumn = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        legendColumn.AddChild(Legend);
        Overlay.AddChild(Compass);
        Overlay.AddChild(legendColumn);
        foreach (var child in new Control[] { Overlay, Toolbar, GraphDock, Inspector, Status })
            AddChild(child);

        ShowGraph.Changed += _ => Arrange();
        ShowInspector.Changed += _ => Arrange();
        Arrange();
    }

    public Toolbar Toolbar { get; }
    public NodeGraph Graph { get; }
    public PanelContainer GraphDock { get; }
    public TabContainer Tabs { get; }
    public PanelContainer Inspector { get; }
    public StatusBar Status { get; }
    public MapCompass Compass { get; }
    public Legend Legend { get; }
    public HBoxContainer Overlay { get; }

    public Setting<bool> ShowGraph { get; } = new(true);
    public Setting<bool> ShowInspector { get; } = new(true);

    /// <summary>Adds an inspector tab holding <paramref name="content"/>.</summary>
    public void AddTab(string title, Control content)
    {
        content.Name = title;
        Tabs.AddChild(content);
    }

    private void Arrange()
    {
        float top = Margin + ToolbarHeight + Margin, bottom = -(Margin + StatusHeight + Margin);
        Place(Toolbar, LayoutPreset.TopWide, Margin, Margin, -Margin, Margin + ToolbarHeight);
        Place(Status, LayoutPreset.BottomWide, Margin, -(Margin + StatusHeight), -Margin, -Margin);
        Inspector.Visible = ShowInspector.Value;
        Place(Inspector, LayoutPreset.RightWide, -(Margin + InspectorWidth), top, -Margin, bottom);
        GraphDock.Visible = ShowGraph.Value;
        float right = ShowInspector.Value ? -(Margin * 2 + InspectorWidth) : -Margin;
        Place(GraphDock, LayoutPreset.BottomWide, Margin, bottom - GraphHeight, right, bottom);
        float above = ShowGraph.Value ? bottom - GraphHeight - Margin : bottom;
        Place(Overlay, LayoutPreset.BottomLeft, Margin, above - 200, Margin + 600, above);
        Overlay.GrowVertical = GrowDirection.Begin;
    }

    private static void Place(Control control, LayoutPreset preset, float left, float top, float right, float bottom)
    {
        control.SetAnchorsPreset(preset);
        control.OffsetLeft = left;
        control.OffsetTop = top;
        control.OffsetRight = right;
        control.OffsetBottom = bottom;
    }
}
