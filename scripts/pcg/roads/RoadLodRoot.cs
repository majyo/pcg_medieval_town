using Godot;
using System;
using System.Linq;

namespace PcgCitySandbox.Pcg;

// Orthographic zoom changes screen extent without moving the sandbox camera.
public partial class RoadLodRoot : Node3D
{
    [Export] public float DetailDistance {get;set;}=280;
    [Export] public bool BatchOnly {get;set;}
    private MeshInstance3D[] _near=Array.Empty<MeshInstance3D>(),_far=Array.Empty<MeshInstance3D>();
    private MeshInstance3D? _overview;
    private int _mode=-1;
    public override void _Ready(){_near=GetChildren().OfType<MeshInstance3D>().Where(n=>n.Name.ToString().StartsWith("Near_")).ToArray();_far=GetChildren().OfType<MeshInstance3D>().Where(n=>n.Name.ToString().StartsWith("Far_")).ToArray();_overview=GetNodeOrNull<MeshInstance3D>("Overview");UpdateLod();}
    public override void _Process(double delta)=>UpdateLod();
    private void UpdateLod()
    {
        var camera=GetViewport().GetCamera3D();if(camera==null)return;
        int mode=camera.Projection==Camera3D.ProjectionType.Orthogonal?(camera.Size<DetailDistance?1:2):0;if(mode==_mode)return;_mode=mode;
        if(_overview!=null)_overview.Visible=mode==2;
        foreach(var node in _near){node.Visible=mode!=2;node.VisibilityRangeEnd=mode==0&&!BatchOnly?DetailDistance:0;}
        foreach(var node in _far){node.Visible=mode==0||mode==2&&_overview==null;node.VisibilityRangeBegin=mode==0?DetailDistance:0;}
    }
}
