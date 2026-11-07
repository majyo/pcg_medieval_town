using Godot;
using System;

namespace PcgCitySandbox.Pcg;

public enum RoadPaving { RuttedEarth, Earth, Alley, Cobble, Stone }

[GlobalClass]
public partial class RoadConstructionSettings : Resource
{
    [Export] public float NearSpacing { get; set; } = .5f;
    [Export] public float FarSpacing { get; set; } = 2;
    [Export] public float ChunkSize { get; set; } = 128;
    [Export] public float DetailDistance { get; set; } = 280;
    [Export] public bool Collision { get; set; } = true;
    [Export] public bool Navigation { get; set; } = true;
    [Export] public RoadPaving Plaza { get; set; } = RoadPaving.Stone;
    [Export] public RoadPaving Gate { get; set; } = RoadPaving.Stone;
    [Export] public RoadPaving MarketStreet { get; set; } = RoadPaving.Cobble;
    [Export] public RoadPaving GateStreet { get; set; } = RoadPaving.Cobble;
    [Export] public RoadPaving TownStreet { get; set; } = RoadPaving.Earth;
    [Export] public RoadPaving BackLane { get; set; } = RoadPaving.Alley;
    [Export] public RoadPaving RuralRoad { get; set; } = RoadPaving.RuttedEarth;
    [Export] public string MaterialKey { get; set; } = "builtin/road/cobble-v1";
    [Export] public Material? MaterialOverride { get; set; }
    public RoadConstructionOptions Snapshot()
    {
        if (!float.IsFinite(NearSpacing) || NearSpacing < .25f || NearSpacing > 1 || !float.IsFinite(FarSpacing) || FarSpacing < NearSpacing || FarSpacing > 4 ||
            !float.IsFinite(ChunkSize) || ChunkSize < 32 || ChunkSize > 256 || !float.IsFinite(DetailDistance) || DetailDistance < 32 ||
            Math.Abs(ChunkSize/NearSpacing-MathF.Round(ChunkSize/NearSpacing))>.001f || Math.Abs(ChunkSize/FarSpacing-MathF.Round(ChunkSize/FarSpacing))>.001f)
            throw new ArgumentException("路面分块必须对齐网格，近景精度 0.25–1 m，远景精度 ≤ 4 m");
        foreach(var p in new[]{Plaza,Gate,MarketStreet,GateStreet,TownStreet,BackLane,RuralRoad})if(!Enum.IsDefined(p))throw new ArgumentException("未知铺装类型");
        return new(NearSpacing,FarSpacing,ChunkSize,DetailDistance,Collision,Navigation,Plaza,Gate,MarketStreet,GateStreet,TownStreet,BackLane,RuralRoad,MaterialKey);
    }
}
public sealed record RoadConstructionOptions(float NearSpacing=.5f,float FarSpacing=2,float ChunkSize=128,float DetailDistance=280,
    bool Collision=true,bool Navigation=true,RoadPaving Plaza=RoadPaving.Stone,RoadPaving Gate=RoadPaving.Stone,
    RoadPaving MarketStreet=RoadPaving.Cobble,RoadPaving GateStreet=RoadPaving.Cobble,RoadPaving TownStreet=RoadPaving.Earth,
    RoadPaving BackLane=RoadPaving.Alley,RoadPaving RuralRoad=RoadPaving.RuttedEarth,string MaterialKey="builtin/road/cobble-v1");
