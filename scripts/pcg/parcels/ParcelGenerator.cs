using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;
public sealed class ParcelNode
{
    public ParcelLayout? LastResult {get;private set;}public SampleNodeState State {get;private set;}public long ResultRevision {get;private set;}public string? Error {get;private set;}private long _version;
    public void Invalidate(){_version++;if(State!=SampleNodeState.Running)State=LastResult==null?SampleNodeState.Empty:SampleNodeState.Stale;}
    public bool IsCurrent(RoadNetwork? roads,long revision,ParcelOptions options)=>State==SampleNodeState.Valid&&LastResult is {} d&&ReferenceEquals(d.Roads,roads)&&d.RoadRevision==revision&&d.Options==options;
    public bool IsCurrent(RoadGrading? grading,long revision,ParcelOptions options)=>State==SampleNodeState.Valid&&grading?.Graded!=null&&LastResult is {} d&&ReferenceEquals(d.Grading,grading)&&d.GradingRevision==revision&&d.Options==options;
    public Task<ParcelLayout> RunAsync(RoadGrading grading,long gradingRevision,ParcelOptions options,PcgContext? context=null)=>RunAsync(grading.Roads.Environment,grading.Roads.Settlement,grading.Roads,grading.Roads.EnvironmentRevision,grading.Roads.SettlementRevision,grading.RoadRevision,options,context,grading,gradingRevision);
    public async Task<ParcelLayout> RunAsync(CityEnvironmentData env,SettlementLayout settlement,RoadNetwork roads,long envRevision,long settlementRevision,long roadRevision,ParcelOptions options,PcgContext? context=null,RoadGrading? grading=null,long gradingRevision=0){
        if(State==SampleNodeState.Running)throw new InvalidOperationException("街区地块节点正在运行");State=SampleNodeState.Running;Error=null;long version=++_version;
        try{if(!ReferenceEquals(env,roads.Environment)||!ReferenceEquals(settlement,roads.Settlement)||envRevision!=roads.EnvironmentRevision||settlementRevision!=roads.SettlementRevision)throw new ArgumentException("环境、聚落和道路版本不一致");
            if(grading!=null&&(grading.Graded==null||!ReferenceEquals(grading.Roads,roads)||grading.RoadRevision!=roadRevision))throw new ArgumentException("地块输入的道路竖向版本不一致");
            var result=await ParcelGenerator.Generate(roads,roadRevision,options,context??new(),grading,gradingRevision);(context??new()).Cancellation.ThrowIfCancellationRequested();if(version!=_version)throw new InvalidOperationException("上游或参数已失效");LastResult=result;ResultRevision++;State=SampleNodeState.Valid;return result;
        }catch(OperationCanceledException){State=SampleNodeState.Cancelled;Error="已取消";throw;}catch(Exception e){State=SampleNodeState.Failed;Error=e.Message;throw;}
    }
}
public static class ParcelGenerator
{
    public static async Task<ParcelLayout> Generate(RoadNetwork roads,long revision,ParcelOptions o,PcgContext ctx,RoadGrading? grading=null,long gradingRevision=0){
        if(!float.IsFinite(o.Width)||o.Width<4||!float.IsFinite(o.Depth)||o.Depth<4||!double.IsFinite(o.MinimumArea)||o.MinimumArea<=0||!float.IsFinite(o.MinimumFrontage)||o.MinimumFrontage<1||o.MinimumFrontage>o.Width||!float.IsFinite(o.RoadSetback)||o.RoadSetback<0||o.RoadSetback>50||!float.IsFinite(o.SideSetback)||o.SideSetback<0||o.SideSetback>20||!float.IsFinite(o.CommercialShare)||!float.IsFinite(o.ProductionShare)||o.CommercialShare<0||o.ProductionShare<0||o.CommercialShare+o.ProductionShare>1)throw new ArgumentException("地块尺寸、退界或用途比例无效");
        async Task Yield(float f,string text){ctx.Cancellation.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(f,text));ctx.Cancellation.ThrowIfCancellationRequested();}
        await Yield(0,"合并道路和广场占地");var layout=roads.Settlement;var env=roads.Environment;ITerrainSurface terrain=grading?.Graded??(ITerrainSurface)new SampledTerrainSurface(env);
        var land=layout.Region?.DevelopmentArea.CopyPaths()??ParcelGeometry.SettlementCells(layout);var reserved=ParcelGeometry.SettlementCells(layout,true);
        var roadArea=roads.Geometry.Area.CopyPaths();
        if(layout.Town!=null)reserved=ParcelGeometry.Union(new Paths64(reserved.Concat(layout.Town.BuildingReserve.CopyPaths())));
        var obstacles=ParcelGeometry.Union(new Paths64(roadArea.Concat(reserved)));var open=ParcelGeometry.Difference(land,obstacles);
        var roadBuffer=o.RoadSetback>0?ParcelGeometry.Offset(roadArea,o.RoadSetback):roadArea;
        var blocks=new List<CityBlock>();var parcels=new List<LandParcel>();var remainders=new List<ParcelRemainder>();int attempts=0;
        foreach(var component in ParcelGeometry.Components(open)){
            if(ParcelGeometry.Area(component)<.001)continue;var block=new CityBlock(blocks.Count,new ParcelRegion(component));blocks.Add(block);var remaining=component;
            var fronts=new List<(Vector2 A,Vector2 B,Vector2 Inward,int Road)>();
            foreach(var ring in block.Shape.Rings)for(int i=0;i<ring.Count;i++){
                foreach(var frontage in roads.Geometry.FrontagesOn(ring[i],ring[(i+1)%ring.Count])){
                    var a=frontage.A;var b=frontage.B;if(a.DistanceTo(b)<o.MinimumFrontage)continue;var dir=(b-a).Normalized();var normal=new Vector2(-dir.Y,dir.X);var mid=(a+b)/2;
                    if(!block.Shape.Contains(mid+normal*.05f))normal=-normal;
                    if(!block.Shape.Contains(mid+normal*.05f)||!ParcelGeometry.Contains(roadArea,mid-normal*.05f))continue;
                    fronts.Add((a,b,normal,frontage.Source.RoadId));
                }
            }
            Paths64 Domain((Vector2 A,Vector2 B,Vector2 Inward,int Road) front){
                var domain=component;var mid=(front.A+front.B)/2;
                foreach(var opposite in fronts){
                    if(front.Inward.Dot(opposite.Inward)>-.5f||((opposite.A+opposite.B)/2-mid).Dot(front.Inward)<8)continue;
                    var tangent=(front.B-front.A).Normalized();float lo=Math.Min((opposite.A-front.A).Dot(tangent),(opposite.B-front.A).Dot(tangent)),hi=Math.Max((opposite.A-front.A).Dot(tangent),(opposite.B-front.A).Dot(tangent));
                    if(hi<0||lo>front.A.DistanceTo(front.B))continue;
                    Vector2 normal=front.Inward-opposite.Inward;float limit=front.A.Dot(front.Inward)-opposite.A.Dot(opposite.Inward);
                    float extent=layout.Options.Radius*5+100;var c=layout.Options.Center;
                    var square=new[]{c+new Vector2(-extent,-extent),c+new Vector2(extent,-extent),c+new Vector2(extent,extent),c+new Vector2(-extent,extent)};var clipped=new List<Vector2>();
                    for(int k=0;k<4;k++){var a=square[k];var b=square[(k+1)%4];float da=a.Dot(normal)-limit,db=b.Dot(normal)-limit;if(da<=0)clipped.Add(a);if((da<0)!=(db<0))clipped.Add(a.Lerp(b,da/(da-db)));}
                    if(clipped.Count>=3)domain=ParcelGeometry.Intersection(domain,new Paths64{ParcelGeometry.Path(clipped)});
                }return domain;
            }
            foreach(var front in fronts.OrderBy(f=>f.Road).ThenBy(f=>f.A.X).ThenBy(f=>f.A.Y)){
                var domain=Domain(front);var district=layout.Region?.DistrictAt((front.A+front.B)/2+front.Inward*.1f);
                float plotDepth=(district?.PlotDepth??70)*o.Depth/70;
                float plotWidth=o.Width*(district?.Role==TownDistrictRole.GateSuburb?1.25f:district?.Role==TownDistrictRole.InstitutionSuburb?1.4f:1);
                float length=front.A.DistanceTo(front.B);int count=Math.Max(1,(int)Math.Ceiling(length/plotWidth));while(count>1&&length/count<o.MinimumFrontage)count--;
                float slot=length/count;float shift=Math.Min(slot*.12f,Math.Max(0,(slot-o.MinimumFrontage)*.45f));
                var splits=new float[count+1];splits[count]=1;
                for(int cut=1;cut<count;cut++){uint hash;unchecked{hash=(uint)(o.Seed^block.Id*73856093^front.Road*19349663^cut*83492791^(int)Math.Round(front.A.X*100)^(int)Math.Round(front.A.Y*1000));hash^=hash>>16;hash*=0x7feb352d;hash^=hash>>15;}splits[cut]=(slot*cut+((hash&65535)/65535f*2-1)*shift)/length;}
                for(int j=0;j<count;j++){
                    if(++attempts>15000)throw new InvalidOperationException("地块候选超过预算，请增大地块宽度或缩小聚落");
                    var a=front.A.Lerp(front.B,splits[j]);var b=front.A.Lerp(front.B,splits[j+1]);
                    var rectangle=new Paths64{ParcelGeometry.Path(new[]{a,b,b+front.Inward*plotDepth,a+front.Inward*plotDepth})};
                    foreach(var piece in ParcelGeometry.Components(ParcelGeometry.Intersection(ParcelGeometry.Intersection(remaining,domain),rectangle))){
                        double area=ParcelGeometry.Area(piece);if(area<o.MinimumArea)continue;
                        var frontage=new List<StreetFront>();var dir=(b-a).Normalized();float segmentLength=a.DistanceTo(b);
                        // Only a real, shared line on the road-facing boundary establishes frontage.
                        foreach(var path in piece)for(int k=0;k<path.Count;k++){
                            var pa=ParcelGeometry.V(path[k]);var pb=ParcelGeometry.V(path[(k+1)%path.Count]);
                            if(Math.Abs((pa-a).Cross(dir))>.005f||Math.Abs((pb-a).Cross(dir))>.005f)continue;
                            float u=Math.Clamp((pa-a).Dot(dir),0,segmentLength),v=Math.Clamp((pb-a).Dot(dir),0,segmentLength);if(Math.Abs(u-v)>.01f)frontage.Add(new(front.Road,a+dir*Math.Min(u,v),a+dir*Math.Max(u,v)));
                        }
                        float frontLength=frontage.Sum(f=>f.Length);if(frontLength<o.MinimumFrontage||frontage.Max(f=>f.Length)<o.MinimumFrontage||area/frontLength<4)continue;
                        var inner=o.SideSetback>0?ParcelGeometry.Offset(piece,-o.SideSetback):piece;inner=ParcelGeometry.Difference(inner,roadBuffer);
                        // The frontage building band is separate from the owned back yard/garden.
                        float buildingDepth=Math.Min(district?.Role==TownDistrictRole.FortifiedCore?14:12,plotDepth*.45f);var band=new Paths64{ParcelGeometry.Path(new[]{a,b,b+front.Inward*buildingDepth,a+front.Inward*buildingDepth})};inner=ParcelGeometry.Intersection(inner,band);
                        if(ParcelGeometry.Area(inner)<20)continue;
                        // All sampled constraints remain inherited; record local height and slope limits.
                        var shape=new ParcelRegion(piece);var buildable=new ParcelRegion(inner);float min=float.PositiveInfinity,max=float.NegativeInfinity,slope=0;
                        int x0=Math.Clamp((int)((shape.Bounds.Position.X-env.Bounds.Position.X)/env.StepX),0,env.CellsX-1),x1=Math.Clamp((int)((shape.Bounds.End.X-env.Bounds.Position.X)/env.StepX),0,env.CellsX-1);
                        int z0=Math.Clamp((int)((shape.Bounds.Position.Y-env.Bounds.Position.Y)/env.StepZ),0,env.CellsZ-1),z1=Math.Clamp((int)((shape.Bounds.End.Y-env.Bounds.Position.Y)/env.StepZ),0,env.CellsZ-1);
                        for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++){
                            var r=new Rect2(env.VertexPosition(x,z),new Vector2(env.StepX,env.StepZ));if(ParcelGeometry.Area(ParcelGeometry.Intersection(piece,ParcelGeometry.Rect(r)))<.0001)continue;
                            slope=Math.Max(slope,grading==null?env.Cell(x,z).MaxSlope??90:terrain.SlopeAt(r.GetCenter()));foreach(var p in new[]{r.Position,r.End,new Vector2(r.Position.X,r.End.Y),new Vector2(r.End.X,r.Position.Y)})if(terrain.TryHeightAt(p,out float h)){min=Math.Min(min,h);max=Math.Max(max,h);}
                        }
                        if(grading!=null)foreach(var p in shape.Rings.SelectMany(r=>r).Concat(frontage.SelectMany(f=>new[]{f.A,(f.A+f.B)/2,f.B}))){float h=terrain.HeightAt(p);min=Math.Min(min,h);max=Math.Max(max,h);slope=Math.Max(slope,terrain.SlopeAt(p));}
                        if(!float.IsFinite(min)||!float.IsFinite(max))throw new InvalidOperationException("地块高度数据不完整");
                        parcels.Add(new(parcels.Count,block.Id,ParcelUse.Residential,shape,buildable,Array.AsReadOnly(frontage.ToArray()),slope,min,max,new ParcelRegion(ParcelGeometry.Difference(piece,inner)),layout.Town?.Interior.Contains(shape.Center)??false,o.SideSetback==0,district?.Id??"legacy"));remaining=ParcelGeometry.Difference(remaining,piece);
                    }
                    if(attempts%24==0)await Yield(.5f,"沿临街边细分地块");
                }
            }
            // Reconcile once against the original block, avoiding chained subtraction artifacts at touching boundaries.
            var occupiedParcels=ParcelGeometry.Union(new Paths64(parcels.Where(p=>p.BlockId==block.Id).SelectMany(p=>p.Shape.CopyPaths())));
            remaining=ParcelGeometry.Difference(component,occupiedParcels);
            var remainderParts=ParcelGeometry.DifferenceRegions(component,occupiedParcels);
            foreach(var region in remainderParts){var probe=region.Triangles.Count>=3?(region.Triangles[0]+region.Triangles[1]+region.Triangles[2])/3:region.Center;bool outside=layout.Town!=null&&!layout.Town.Interior.Contains(probe);remainders.Add(new(block.Id,region,outside?"城外未开发土地":fronts.Count==0?"未分配土地：没有可用临街边":"公共院地 / 无合法临街宅地 / 尺寸不足"));}
            await Yield(.8f,"街区检查完成");
        }
        uint Tie(int id){unchecked{uint h=(uint)(id^o.Seed);h^=h>>16;h*=0x7feb352d;h^=h>>15;return h;}}
        double total=parcels.Sum(p=>p.Shape.Area),commercial=0,production=0;
        var mainRoads=roads.Edges.Where(e=>e.Main).Select(e=>e.Id).ToHashSet();
        var ordered=parcels.OrderByDescending(p=>p.Frontage.Any(f=>mainRoads.Contains(f.RoadId))).ThenBy(p=>p.Shape.Center.DistanceSquaredTo(layout.Core)).ThenBy(p=>Tie(p.Id)).ToArray();
        foreach(var p in ordered)if(commercial<total*o.CommercialShare){parcels[p.Id]=p with{Use=ParcelUse.Commercial};commercial+=p.Shape.Area;}
        foreach(var p in parcels.OrderBy(p=>p.Frontage.Any(f=>mainRoads.Contains(f.RoadId))).ThenBy(p=>Tie(p.Frontage[0].RoadId)).ToArray())if(production<total*o.ProductionShare&&parcels[p.Id].Use==ParcelUse.Residential){parcels[p.Id]=p with{Use=ParcelUse.Production};production+=p.Shape.Area;}
        await Yield(1,"用途与面积账目检查");var result=new ParcelLayout(roads,revision,o,blocks,parcels,remainders,grading,gradingRevision);
        if(Math.Abs(result.AvailableArea-result.ParcelArea-result.RemainderArea)>Math.Max(1,result.AvailableArea*.00001))throw new InvalidOperationException($"街区面积账目不一致: {result.AvailableArea} - {result.ParcelArea} - {result.RemainderArea}");
        return result;
    }
}

