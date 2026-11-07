using Godot;

namespace PcgCitySandbox.Pcg;

// Runtime-only component saved with the scene. No PCG inputs or generation needed.
public partial class TownNavigation : Node3D
{
    public override void _EnterTree()
    {
        var map=GetWorld3D().NavigationMap;
        NavigationServer3D.MapSetCellSize(map,.01f);NavigationServer3D.MapSetCellHeight(map,.01f);
        NavigationServer3D.MapSetMergeRasterizerCellScale(map,.01f);NavigationServer3D.MapSetLinkConnectionRadius(map,2);
    }
    public Vector3[] Path(Vector3 start,Vector3 target)=>FindPath(GetWorld3D().NavigationMap,start,target);
    public static Vector3[] FindPath(Rid map,Vector3 start,Vector3 target)
    {
        using var query=new NavigationPathQueryParameters3D{Map=map,StartPosition=start,TargetPosition=target,PathSearchMaxPolygons=200000};
        using var result=new NavigationPathQueryResult3D();NavigationServer3D.QueryPath(query,result);return result.Path;
    }
}
