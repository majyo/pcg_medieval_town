using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

internal sealed record ProfileBounds(double[] Lower,double[] Upper);
internal static class RoadTerrainConstraints
{
    // One bounded repair attempt, only around registered conflicts. Tighten the
    // existing station boxes using nearby immutable lock/other-road elevations.
    public static Dictionary<int,ProfileBounds> FromConflicts(RoadGrading g)
    {
        var terrain=g.Graded!;var result=new Dictionary<int,ProfileBounds>();float slope=Math.Min(1/g.Options.CutRatio,1/g.Options.FillRatio);
        float reach=Math.Min(16,terrain.Earthwork.BandReach*2+2);int radius=(int)Math.Ceiling(reach/terrain.Cell);
        foreach(var road in g.Roads.Edges){var line=new Centerline(road);var profile=g.Profile(road.Id)!;
            var affected=terrain.RetainingWalls.Where(w=>ParcelGeometry.Distance(w.Center,road)<reach+road.Width).Select(w=>g.Locate(road.Id,w.Center).Station).ToArray();if(affected.Length==0)continue;
            var lo=Enumerable.Repeat(double.NegativeInfinity,profile.Design.Count).ToArray();var hi=Enumerable.Repeat(double.PositiveInfinity,profile.Design.Count).ToArray();bool constrained=false;
            for(int i=0;i<profile.Design.Count;i++){
                float s=profile.StationAt(i);if(!affected.Any(a=>Math.Abs(a-s)<20))continue;var p=line.PointAt(s);var normal=line.NormalAt(s);
                foreach(float sign in new[]{-1f,1f}){var edge=p+normal*(sign*road.Width/2);if(!g.TryDesignHeightNear(edge,.05f,out float edgeHeight))continue;float offset=edgeHeight-profile.Design[i];
                    int cx=(int)Math.Round((edge.X-terrain.Origin.X)/terrain.Cell),cz=(int)Math.Round((edge.Y-terrain.Origin.Y)/terrain.Cell);
                    for(int z=cz-radius;z<=cz+radius;z++)for(int x=cx-radius;x<=cx+radius;x++){
                        var kind=terrain.NodeKind(x,z);if(kind is not GradingCell.Lock and not GradingCell.Road)continue;var q=terrain.NodePosition(x,z);float distance=edge.DistanceTo(q);if(distance>reach)continue;
                        if(kind==GradingCell.Road&&g.SurfaceAt(q)?.RoadId==road.Id)continue;
                        float h=terrain.Source.HeightAt(q)+terrain.NodeDelta(x,z);float allowance=slope*Math.Max(0,distance-terrain.Cell*.5f);
                        lo[i]=Math.Max(lo[i],h-allowance-offset);hi[i]=Math.Min(hi[i],h+allowance-offset);constrained=true;
                    }
                }
            }
            if(constrained)result[road.Id]=new(lo,hi);
        }
        return result;
    }
}
