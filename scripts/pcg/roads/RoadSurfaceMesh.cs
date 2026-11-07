using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
namespace PcgCitySandbox.Pcg;

// Preview tessellation of the canonical area, clipped to terrain triangles.
public sealed class RoadSurfaceMesh
{
    public ReadOnlyCollection<Vector3> Positions {get;}
    public ReadOnlyCollection<int> SurfaceIds {get;}
    private RoadSurfaceMesh(List<Vector3> positions,List<int> owners){Positions=Array.AsReadOnly(positions.ToArray());SurfaceIds=Array.AsReadOnly(owners.ToArray());}
    public static RoadSurfaceMesh Build(RoadNetwork data)=>Build(data.Environment,data.Geometry.Surfaces.Select(s=>(s.Id,s.Shape)));
    public static RoadSurfaceMesh Build(CityEnvironmentData env,IEnumerable<(int Id,ParcelRegion Shape)> surfaces)
    {
        var positions=new List<Vector3>();var owners=new List<int>();
        foreach(var surface in surfaces){var paths=surface.Shape.CopyPaths();var bounds=surface.Shape.Bounds;
            int x0=Math.Clamp((int)Math.Floor((bounds.Position.X-env.Bounds.Position.X)/env.StepX),0,env.CellsX-1),x1=Math.Clamp((int)Math.Floor((bounds.End.X-env.Bounds.Position.X)/env.StepX),0,env.CellsX-1);
            int z0=Math.Clamp((int)Math.Floor((bounds.Position.Y-env.Bounds.Position.Y)/env.StepZ),0,env.CellsZ-1),z1=Math.Clamp((int)Math.Floor((bounds.End.Y-env.Bounds.Position.Y)/env.StepZ),0,env.CellsZ-1);
            for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++){
                var a=env.VertexPosition(x,z);var d=env.VertexPosition(x+1,z+1);var b=new Vector2(d.X,a.Y);var c=new Vector2(a.X,d.Y);
                foreach(var triangle in new[]{new[]{a,b,c},new[]{b,d,c}}){
                    var part=ParcelGeometry.Intersection(paths,new Paths64{ParcelGeometry.Path(triangle)});if(ParcelGeometry.Area(part)<.000001)continue;
                    var region=new ParcelRegion(part);for(int i=0;i<region.Triangles.Count;i+=3){
                        var p=region.Triangles[i];var q=region.Triangles[i+1];var r=region.Triangles[i+2];if((q-p).Cross(r-p)<0)(q,r)=(r,q);
                        foreach(var point in new[]{p,q,r}){if(!env.TryHeightAt(point,out float height))throw new InvalidOperationException("道路预览缺少地面高程");positions.Add(new(point.X,height,point.Y));owners.Add(surface.Id);}
                    }
                }
            }
        }
        return new(positions,owners);
    }
}
