using Godot;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

[GlobalClass]
public partial class RoadRuntimeGraph : Resource
{
    [Export] public string TerrainFingerprint {get;set;}="";
    [Export] public long RoadRevision {get;set;}
    [Export] public int[] RoadIds {get;set;}=System.Array.Empty<int>();
    [Export] public int[] From {get;set;}=System.Array.Empty<int>();
    [Export] public int[] To {get;set;}=System.Array.Empty<int>();
    [Export] public int[] Offsets {get;set;}=System.Array.Empty<int>();
    [Export] public Vector3[] Points {get;set;}=System.Array.Empty<Vector3>();
    [Export] public float[] Widths {get;set;}=System.Array.Empty<float>();
    [Export] public float[] MaxGrades {get;set;}=System.Array.Empty<float>();
    [Export] public int[] Paving {get;set;}=System.Array.Empty<int>();
    [Export] public string[] AnchorIds {get;set;}=System.Array.Empty<string>();
    [Export] public int[] AnchorNodes {get;set;}=System.Array.Empty<int>();
    [Export] public Vector3[] AnchorPositions {get;set;}=System.Array.Empty<Vector3>();
    public static RoadRuntimeGraph Build(RoadGrading g,RoadConstructionOptions options)
    {
        var points=new List<Vector3>();var offsets=new List<int>();
        foreach(var edge in g.Roads.Edges){offsets.Add(points.Count);var line=new Centerline(edge);var profile=g.Profile(edge.Id)!;
            for(int i=0;i<profile.Design.Count;i++){var p=line.PointAt(profile.StationAt(i));g.TryDesignHeight(p,out float h);points.Add(new(p.X,h,p.Y));}}
        offsets.Add(points.Count);var nodes=g.Roads.Nodes.ToDictionary(n=>n.Id);var anchors=g.Roads.Anchors.OrderBy(a=>a.Key).ToArray();
        return new(){TerrainFingerprint=g.Graded!.Fingerprint,RoadRevision=g.RoadRevision,RoadIds=g.Roads.Edges.Select(e=>e.Id).ToArray(),From=g.Roads.Edges.Select(e=>e.From).ToArray(),To=g.Roads.Edges.Select(e=>e.To).ToArray(),
            Offsets=offsets.ToArray(),Points=points.ToArray(),Widths=g.Roads.Edges.Select(e=>e.Width).ToArray(),MaxGrades=g.Roads.Edges.Select(e=>g.Profile(e.Id)!.MaxGrade).ToArray(),Paving=g.Roads.Edges.Select(e=>(int)RoadSceneBuilder.Classify(g.Roads,e,options)).ToArray(),
            AnchorIds=anchors.Select(a=>a.Key).ToArray(),AnchorNodes=anchors.Select(a=>a.Value).ToArray(),AnchorPositions=anchors.Select(a=>{var p=nodes[a.Value].Position;return new Vector3(p.X,g.Graded.HeightAt(p),p.Y);}).ToArray()};
    }
}
