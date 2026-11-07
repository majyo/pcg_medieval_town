using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
namespace PcgCitySandbox.Pcg;
public sealed record TownGate(string Id,string EntryId,Vector2 Position,Vector2 Outward,float Width,ParcelRegion Passage);
public sealed record TownPrecinct(string Id,string Name,ParcelRegion Shape,Vector2 Access)
{
    public ParcelRegion Approach {
        get {var b=Shape.Bounds;var edge=new Vector2(Math.Clamp(Access.X,b.Position.X,b.End.X),Math.Clamp(Access.Y,b.Position.Y,b.End.Y));var inside=edge+(Shape.Center-edge).Normalized();return new ParcelRegion(new Paths64{ParcelGeometry.Path(RoadSpace.Strip(Access,inside,4))});}
    }
}
public sealed class MedievalLayout
{
    public ParcelRegion Interior {get;}
    public ParcelRegion WallFootprint {get;}
    public ParcelRegion WallReserve {get;}
    public ReadOnlyCollection<Vector2> WallLine {get;}
    public ReadOnlyCollection<TownGate> Gates {get;}
    public ReadOnlyCollection<TownPrecinct> Precincts {get;}
    public ParcelRegion RoadObstacles {get;}
    public ParcelRegion BuildingReserve {get;}
    internal MedievalLayout(Paths64 inside,Paths64 wall,Paths64 reserve,Path64 line,List<TownGate> gates,List<TownPrecinct> precincts){
        Interior=new(inside);WallFootprint=new(wall);WallReserve=new(reserve);WallLine=Array.AsReadOnly(line.Select(ParcelGeometry.V).ToArray());Gates=Array.AsReadOnly(gates.ToArray());Precincts=Array.AsReadOnly(precincts.ToArray());
        var fixedAreas=new Paths64(precincts.SelectMany(p=>p.Shape.CopyPaths()));RoadObstacles=new(ParcelGeometry.Union(new Paths64(reserve.Concat(fixedAreas))));
        BuildingReserve=new(ParcelGeometry.Union(new Paths64(reserve.Concat(fixedAreas).Concat(gates.SelectMany(g=>g.Passage.CopyPaths())).Concat(precincts.SelectMany(p=>p.Approach.CopyPaths())))));
    }
}
public static class MedievalPlanner
{
    public static MedievalLayout Build(SettlementLayout layout){
        var o=layout.Options;var land=ParcelGeometry.SettlementCells(layout);var e=layout.Environment;
        Paths64 interior=land,wall=new(),reserve=new();Path64 line=new();var gates=new List<TownGate>();
        if(o.Walls){
            var choices=ParcelGeometry.Components(ParcelGeometry.Offset(land,-o.WallInset));
            var chosen=choices.FirstOrDefault(p=>ParcelGeometry.Contains(p,layout.Core));if(chosen==null)throw new InvalidOperationException("城墙退让后无法围合核心，请减小退让或扩大布局");
            line=new Path64(chosen[0]);
            // Simplification is accepted only if the complete wall buffer remains on allowed land.
            foreach(double epsilon in new[]{8d,4d,2d,0d}){
                var candidate=epsilon>0?Clipper.SimplifyPath(line,epsilon*ParcelGeometry.Scale):new Path64(line);
                var shape=new Paths64{candidate};var candidateOuter=ParcelGeometry.Offset(shape,o.WallWidth/2+o.WallBuffer);
                if(ParcelGeometry.Area(ParcelGeometry.Difference(ParcelGeometry.Difference(candidateOuter,ParcelGeometry.Offset(shape,-o.WallWidth/2-o.WallBuffer)),land))<.01){line=candidate;break;}
            }
            // Remove local staircase corners independently; one constrained corner must not prevent all simplification.
            for(int pass=0;pass<2;pass++)for(int i=line.Count-1;i>=0&&line.Count>6;i--){
                int j=i%line.Count;var a=ParcelGeometry.V(line[(j+line.Count-1)%line.Count]);var b=ParcelGeometry.V(line[j]);var c=ParcelGeometry.V(line[(j+1)%line.Count]);var delta=c-a;
                if(delta.LengthSquared()<1||Math.Abs((b-a).Cross(delta))/delta.Length()>8)continue;
                var candidate=new Path64(line);candidate.RemoveAt(j);var shape=new Paths64{candidate};float clearance=o.WallWidth/2+o.WallBuffer;
                var band=ParcelGeometry.Difference(ParcelGeometry.Offset(shape,clearance),ParcelGeometry.Offset(shape,-clearance));
                if(ParcelGeometry.Contains(shape,layout.Core)&&ParcelGeometry.Area(ParcelGeometry.Difference(band,land))<.01)line=candidate;
            }
            interior=new Paths64{line};float half=o.WallWidth/2+o.WallBuffer;
            var outer=ParcelGeometry.Offset(interior,half);var inner=ParcelGeometry.Offset(interior,-half);
            if(ParcelGeometry.Area(ParcelGeometry.Difference(ParcelGeometry.Difference(outer,inner),land))>.02)throw new InvalidOperationException("城墙走廊越出可用土地，请增大城墙退让");
            reserve=ParcelGeometry.Difference(outer,inner);wall=ParcelGeometry.Difference(ParcelGeometry.Offset(interior,o.WallWidth/2),ParcelGeometry.Offset(interior,-o.WallWidth/2));
            foreach(var entry in layout.Entries){
                var candidates=new List<(Vector2 P,Vector2 N,float Score)>();
                for(int i=0;i<line.Count;i++){
                    var a=ParcelGeometry.V(line[i]);var b=ParcelGeometry.V(line[(i+1)%line.Count]);var delta=b-a;float length=delta.Length();if(length<o.GateWidth+4)continue;
                    var dir=delta/length;float along=Math.Clamp((entry.Position-a).Dot(dir),o.GateWidth/2+2,length-o.GateWidth/2-2);var p=a+dir*along;var normal=new Vector2(dir.Y,-dir.X);
                    if(!ParcelGeometry.Contains(interior,p-normal*2))normal=-normal;
                    var passage=RoadSpace.Strip(p-normal*(half+12),p+normal*(half+12),o.GateWidth);
                    if(ParcelGeometry.Area(ParcelGeometry.Difference(new Paths64{ParcelGeometry.Path(passage)},land))>.01)continue;
                    if(gates.Any(g=>g.Position.DistanceTo(p)<o.GateWidth*2))continue;
                    candidates.Add((p,normal,p.DistanceTo(entry.Position)));
                }
                if(candidates.Count==0)throw new InvalidOperationException($"{entry.Id} 附近没有合格城门走廊，请调整城墙退让/开口");
                var selected=candidates.OrderBy(c=>c.Score).First();var opening=new ParcelRegion(new Paths64{ParcelGeometry.Path(RoadSpace.Strip(selected.P-selected.N*(half+12),selected.P+selected.N*(half+12),o.GateWidth))});
                gates.Add(new($"G{gates.Count+1}",entry.Id,selected.P,selected.N,o.GateWidth,opening));
            }
            var openings=ParcelGeometry.Union(new Paths64(gates.SelectMany(g=>g.Passage.CopyPaths())));wall=ParcelGeometry.Difference(wall,openings);reserve=ParcelGeometry.Difference(reserve,openings);
        }
        var precincts=new List<TownPrecinct>();var plaza=ParcelGeometry.SettlementCells(layout,true);
        if(o.CivicPrecincts){
            var available=ParcelGeometry.Difference(ParcelGeometry.Intersection(land,interior),ParcelGeometry.Union(new Paths64(reserve.Concat(plaza).Concat(gates.SelectMany(g=>g.Passage.CopyPaths())))));
            foreach(var spec in new[]{("church","教堂院区",new Vector2(34,44)),("manor","领主院区",new Vector2(44,38))}){
                var candidates=new List<(Vector2 P,float Score)>();
                for(int z=0;z<e.CellsZ;z+=2)for(int x=0;x<e.CellsX;x+=2){var p=e.VertexPosition(x,z)+new Vector2(e.StepX,e.StepZ)/2;float distance=p.DistanceTo(layout.Core);if(distance<65||distance>o.Radius*.85f)continue;
                    var rect=new Rect2(p-spec.Item3/2,spec.Item3);var footprint=ParcelGeometry.Rect(rect.Grow(10));
                    if(ParcelGeometry.Area(ParcelGeometry.Difference(footprint,available))>.01)continue;e.TryHeightAt(p,out float h);
                    candidates.Add((p,spec.Item1=="church"?Math.Abs(distance-100):Math.Abs(distance-155)-h*2));
                }
                if(candidates.Count==0)throw new InvalidOperationException($"没有空间布置{spec.Item2}，请扩大范围或关闭公共院区");
                var c=candidates.OrderBy(p=>p.Score).ThenBy(p=>p.P.Y).ThenBy(p=>p.P.X).First().P;var shape=ParcelGeometry.Rect(new Rect2(c-spec.Item3/2,spec.Item3));var direction=(layout.Core-c).Normalized();
                var access=Math.Abs(direction.X)>Math.Abs(direction.Y)?c+new Vector2(Math.Sign(direction.X)*(spec.Item3.X/2+8),0):c+new Vector2(0,Math.Sign(direction.Y)*(spec.Item3.Y/2+8));
                precincts.Add(new(spec.Item1,spec.Item2,new ParcelRegion(shape),access));available=ParcelGeometry.Difference(available,ParcelGeometry.Offset(shape,15));
            }
        }
        return new(interior,wall,reserve,line,gates,precincts);
    }
}


