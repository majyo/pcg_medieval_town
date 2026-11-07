using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

public sealed record BuildingOptions(float HeightScale=1,float Variation=.2f,float PassageWidth=1.2f,
    float MaxFoundation=1.5f,float RoofAngle=42,int Seed=73129,bool PartyWalls=true,bool Institutions=true);
public sealed record BuildingIssue(string Source,Vector2 Position,string Reason);
public sealed record BuildingMass(int Id,int? ParcelId,string? PrecinctId,string DistrictId,string Program,
    ParcelRegion Footprint,ParcelRegion RoofProjection,ReadOnlyCollection<Vector2> Corners,
    Vector2 Inward,Vector2 Door,Vector2 StreetPoint,float BaseY,float WallHeight,float RoofHeight,
    int Floors,bool InsideCore,float MinGround,float MaxGround,bool RidgeAlongDepth,bool OpenHall=false);
public sealed record BuildingAccess(int BuildingId,ReadOnlyCollection<int> Parcels,int RoadId,
    ParcelRegion Corridor,Vector2 Door,Vector2 StreetPoint,Vector2 YardPoint,string Kind);
public sealed record SharedBuildingWall(int A,int B,Vector2 Start,Vector2 End,int Owner);
public sealed record BuildingOpenSpace(string Source,string Kind,ParcelRegion Shape);

public sealed class BuildingSet
{
    public ParcelLayout Parcels {get;}
    public CityEnvironmentData Environment=>Parcels.Environment;
    public ITerrainSurface Terrain=>Parcels.Terrain;
    public long ParcelRevision {get;}
    public BuildingOptions Options {get;}
    public ReadOnlyCollection<BuildingMass> Buildings {get;}
    public ReadOnlyCollection<BuildingAccess> Access {get;}
    public ReadOnlyCollection<SharedBuildingWall> SharedWalls {get;}
    public ReadOnlyCollection<BuildingOpenSpace> OpenSpaces {get;}
    public ReadOnlyCollection<BuildingIssue> Issues {get;}
    public double FootprintArea=>Buildings.Sum(b=>b.Footprint.Area);
    public int OrdinaryCount=>Buildings.Count(b=>b.ParcelId.HasValue);
    public int InsideCount=>Buildings.Count(b=>b.InsideCore);
    public int OutsideCount=>Buildings.Count-InsideCount;
    public float FacadeCoverage {get;}
    internal BuildingSet(ParcelLayout parcels,long revision,BuildingOptions options,List<BuildingMass> buildings,
        List<BuildingAccess> access,List<SharedBuildingWall> shared,List<BuildingOpenSpace> open,List<BuildingIssue> issues)
    {
        Parcels=parcels;ParcelRevision=revision;Options=options;
        Buildings=Array.AsReadOnly(buildings.ToArray());Access=Array.AsReadOnly(access.ToArray());
        SharedWalls=Array.AsReadOnly(shared.ToArray());OpenSpaces=Array.AsReadOnly(open.ToArray());Issues=Array.AsReadOnly(issues.ToArray());
        float available=parcels.Parcels.Sum(p=>p.Frontage.Max(f=>f.Length));
        FacadeCoverage=available>0?buildings.Where(b=>b.ParcelId.HasValue).Sum(b=>b.Corners[0].DistanceTo(b.Corners[1]))/available:0;
    }
}
public sealed class BuildingNode
{
    public BuildingSet? LastResult {get;private set;}
    public SampleNodeState State {get;private set;}
    public long ResultRevision {get;private set;}
    public string? Error {get;private set;}
    private long _version;
    public void Invalidate(){_version++;if(State!=SampleNodeState.Running)State=LastResult==null?SampleNodeState.Empty:SampleNodeState.Stale;}
    public bool IsCurrent(ParcelLayout? parcels,long revision,BuildingOptions options)=>State==SampleNodeState.Valid&&
        LastResult is {} d&&ReferenceEquals(parcels,d.Parcels)&&d.ParcelRevision==revision&&d.Options==options;
    public async Task<BuildingSet> RunAsync(CityEnvironmentData env,ParcelLayout parcels,long samplingRevision,
        long parcelRevision,BuildingOptions options,PcgContext? context=null)
    {
        if(State==SampleNodeState.Running)throw new InvalidOperationException("建筑体量节点正在运行");
        State=SampleNodeState.Running;Error=null;long version=++_version;
        try{
            if(!ReferenceEquals(env,parcels.Environment)||samplingRevision!=parcels.Roads.EnvironmentRevision)
                throw new ArgumentException("环境与地块版本不一致");
            var result=await BuildingGenerator.Generate(parcels,parcelRevision,options,context??new());
            if(version!=_version)throw new InvalidOperationException("生成期间输入已失效");
            LastResult=result;ResultRevision++;State=SampleNodeState.Valid;return result;
        }catch(OperationCanceledException){State=SampleNodeState.Cancelled;Error="已取消";throw;}
        catch(Exception ex){State=SampleNodeState.Failed;Error=ex.Message;throw;}
    }
}

public static class BuildingGeometry
{
    public static Paths64 Polygon(IEnumerable<Vector2> points)=>new(){ParcelGeometry.Path(points)};
    public static bool Fits(Paths64 shape,Paths64 allowed)=>ParcelGeometry.Area(ParcelGeometry.Difference(shape,allowed))<.002;
    public static bool Clear(Paths64 shape,Paths64 occupied)=>ParcelGeometry.Area(ParcelGeometry.Intersection(shape,occupied))<.002;
    public static Vector2[] Rectangle(Vector2 front,Vector2 tangent,Vector2 inward,float width,float depth)=>
        new[]{front,front+tangent*width,front+tangent*width+inward*depth,front+inward*depth};
    public static bool Ground(CityEnvironmentData env,Vector2[] corners,float maxExposure,out float min,out float max)=>Ground(new SampledTerrainSurface(env),corners,maxExposure,out min,out max);
    public static bool Ground(ITerrainSurface env,Vector2[] corners,float maxExposure,out float min,out float max)
    {
        min=float.PositiveInfinity;max=float.NegativeInfinity;
        float width=corners[0].DistanceTo(corners[1]),depth=corners[0].DistanceTo(corners[3]);
        int nx=Math.Max(1,(int)Math.Ceiling(width/Math.Max(1,Math.Min(3,env.Spacing))));
        int nz=Math.Max(1,(int)Math.Ceiling(depth/Math.Max(1,Math.Min(3,env.Spacing))));
        for(int z=0;z<=nz;z++)for(int x=0;x<=nx;x++){
            var p=corners[0]+(corners[1]-corners[0])*((float)x/nx)+(corners[3]-corners[0])*((float)z/nz);
            if(!env.TryHeightAt(p,out float h))return false;min=Math.Min(min,h);max=Math.Max(max,h);
        }
        return max-min+.06f<=maxExposure;
    }
    public static float DistanceToSegment(Vector2 p,Vector2 a,Vector2 b){var d=b-a;return p.DistanceTo(a+d*Math.Clamp((p-a).Dot(d)/Math.Max(.000001f,d.LengthSquared()),0,1));}
}
