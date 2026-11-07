using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public enum SceneScheme { EarthAndTimber, GrayStudy }
public enum ScenePropKind { Storage, Shed, Stall, Bench, Well, Fence }
public sealed record ScenePlacementOptions(SceneScheme Scheme=SceneScheme.EarthAndTimber,float DecorationDensity=.4f,
    float YardDensity=.6f,bool Fences=true,bool AvoidVegetation=true,int ObjectBudget=1200,int Seed=73129);
public sealed record SceneGroundPatch(int Id,string Kind,string Source,ParcelRegion Shape,int? RoadId=null);
public sealed record SceneBuildingPlacement(string Id,BuildingMass Mass,string Recipe);
public sealed record ScenePlacementZone(string Id,string Kind,ParcelRegion Shape,ParcelRegion Site,Vector2 Anchor,Vector2 Direction,int? ParcelId=null,string? PrecinctId=null);
public sealed record SceneProtectedSpace(string Source,string Kind,ParcelRegion Shape);
public sealed record ScenePlacementIssue(string Source,Vector2 Position,string Reason,int Attempts=0);
public sealed record ScenePropPlacement(string Id,ScenePropKind Kind,string Recipe,string ZoneId,int? ParcelId,string? PrecinctId,
    ReadOnlyCollection<Vector2> Corners,ParcelRegion Footprint,float BaseY,float MinGround,float MaxGround,float Height,ParcelRegion Approach);

public sealed class ScenePlacementSet
{
    public const int CatalogVersion=1;
    public BuildingSet Buildings {get;}
    public ParcelLayout Parcels=>Buildings.Parcels;
    public RoadNetwork Roads=>Parcels.Roads;
    public CityEnvironmentData Environment=>Roads.Environment;
    public ITerrainSurface Terrain=>Parcels.Terrain;
    public long BuildingRevision {get;}
    public ScenePlacementOptions Options {get;}
    public ReadOnlyCollection<SceneGroundPatch> GroundPatches {get;}
    public ReadOnlyCollection<SceneBuildingPlacement> BuildingPlacements {get;}
    public ReadOnlyCollection<ScenePropPlacement> Props {get;}
    public ReadOnlyCollection<ScenePlacementZone> Zones {get;}
    public ReadOnlyCollection<SceneProtectedSpace> ProtectedSpaces {get;}
    public ParcelRegion ProtectedArea {get;}
    public ParcelRegion VegetationMask {get;}
    public ReadOnlyCollection<ScenePlacementIssue> Issues {get;}
    public int CandidateAttempts {get;}
    internal ScenePlacementSet(BuildingSet buildings,long revision,ScenePlacementOptions options,List<SceneGroundPatch> ground,
        List<ScenePropPlacement> props,List<ScenePlacementZone> zones,List<SceneProtectedSpace> protectedSpaces,ParcelRegion protectedArea,
        ParcelRegion vegetation,List<ScenePlacementIssue> issues,int attempts)
    {
        Buildings=buildings;BuildingRevision=revision;Options=options;
        GroundPatches=Array.AsReadOnly(ground.ToArray());Props=Array.AsReadOnly(props.ToArray());Zones=Array.AsReadOnly(zones.ToArray());
        ProtectedSpaces=Array.AsReadOnly(protectedSpaces.ToArray());ProtectedArea=protectedArea;VegetationMask=vegetation;
        Issues=Array.AsReadOnly(issues.ToArray());CandidateAttempts=attempts;
        BuildingPlacements=Array.AsReadOnly(buildings.Buildings.Select(b=>new SceneBuildingPlacement($"B{b.Id}",b,"builtin/building-v1")).ToArray());
    }
}
public sealed class ScenePlacementNode
{
    public ScenePlacementSet? LastResult {get;private set;}
    public SampleNodeState State {get;private set;}
    public long ResultRevision {get;private set;}
    public string? Error {get;private set;}
    private long _version;
    public void Invalidate(){_version++;if(State!=SampleNodeState.Running)State=LastResult==null?SampleNodeState.Empty:SampleNodeState.Stale;}
    public bool IsCurrent(BuildingSet? buildings,long revision,ScenePlacementOptions options)=>State==SampleNodeState.Valid&&LastResult is {} d&&ReferenceEquals(buildings,d.Buildings)&&d.BuildingRevision==revision&&d.Options==options;
    public async Task<ScenePlacementSet> RunAsync(CityEnvironmentData env,RoadNetwork roads,ParcelLayout parcels,BuildingSet buildings,
        long environmentRevision,long roadRevision,long parcelRevision,long buildingRevision,ScenePlacementOptions options,PcgContext? context=null)
    {
        if(State==SampleNodeState.Running)throw new InvalidOperationException("场景布置节点正在运行");State=SampleNodeState.Running;Error=null;long version=++_version;
        try{
            if(!ReferenceEquals(env,roads.Environment)||!ReferenceEquals(roads,parcels.Roads)||!ReferenceEquals(parcels,buildings.Parcels)||
                roads.EnvironmentRevision!=environmentRevision||parcels.RoadRevision!=roadRevision||buildings.ParcelRevision!=parcelRevision)
                throw new ArgumentException("场景输入不属于同一条有效生成链");
            var ctx=context??new();var result=await ScenePlacementGenerator.Generate(buildings,buildingRevision,options,ctx);
            ctx.Cancellation.ThrowIfCancellationRequested();if(version!=_version)throw new InvalidOperationException("布置期间输入已失效");
            LastResult=result;ResultRevision++;State=SampleNodeState.Valid;return result;
        }catch(OperationCanceledException){State=SampleNodeState.Cancelled;Error="已取消";throw;}
        catch(Exception ex){State=SampleNodeState.Failed;Error=ex.Message;throw;}
    }
}
