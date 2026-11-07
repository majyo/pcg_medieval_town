using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
namespace PcgCitySandbox.Pcg;
public static class SettlementValidation
{
    private sealed class Fixture:IEnvironmentSource{
        public string SourceId=>"settlement_fixture";public long Revision=>1;public Rect2 Bounds=>new(-500,-500,1000,1000);public float? NativeSpacing=>10;
        public EnvironmentLayers Layers=>new(LayerStatus.Provided,LayerStatus.Provided,LayerStatus.Provided,LayerStatus.Provided,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty);
        public IReadOnlyList<ConstraintPolygon> NoBuildZones=>Array.Empty<ConstraintPolygon>();public IReadOnlyList<ConstraintPolygon> Obstacles=>Array.Empty<ConstraintPolygon>();
        public bool Contains(Vector2 p)=>Bounds.HasPoint(p)||p.X==500||p.Y==500;
        public EnvironmentPoint Query(Vector2 p)=>new(10,0,Math.Abs(p.X)<20||p.DistanceTo(new Vector2(-150,0))<25,.2f);
    }
    public static async Task Run(MapSettings mapSettings){int checks=0;void Check(bool b,string m){checks++;if(!b)throw new InvalidOperationException("SETTLEMENT_TEST_FAILED: "+m);}
        var e=EnvironmentSampler.Sample(new Fixture(),new(new(-500,-500,1000,1000)));
        var o=new SettlementOptions(Vector2.Zero,300,new Vector2(-150,100),EntryCount:2,Regional:false);var node=new SettlementLayoutNode();
        var d=await node.RunAsync(e,1,o);Check(d.Area>=o.MinimumArea&&d.Entries.Count>0,"usable layout");Check(d.Core==o.ManualCore,"manual core preserved");
        Check(!d.Contains(new Vector2(150,100)),"disconnected bank excluded");Check(!d.Contains(new Vector2(-150,0)),"internal water hole preserved");Check(d.Boundary.Any(b=>!b.Outer),"hole boundaries classified");
        Check(d.Entries.All(b=>b.Edge.Outer&&d.EntryCandidates.Contains(b.Edge)),"entries are qualified outer boundary targets");Check(d.ReservedArea>=o.PlazaSize*o.PlazaSize&&d.Area>d.ReservedArea,"reservation accounting");
        var repeat=await node.RunAsync(e,1,o);Check(d.Cells.SequenceEqual(repeat.Cells)&&d.Entries.SequenceEqual(repeat.Entries),"deterministic rerun");
        Check(!node.IsCurrent(e,2,o),"sampling output revision invalidates even same map");Check(!node.IsCurrent(e,1,o with{Seed=4}),"parameter signature");
        long rev=node.ResultRevision;using(var cancel=new CancellationTokenSource()){
            try{await node.RunAsync(e,1,o,new(cancel.Token,p=>{cancel.Cancel();return Task.CompletedTask;}));throw new Exception("not cancelled");}catch(OperationCanceledException){}
            Check(node.State==SampleNodeState.Cancelled&&node.ResultRevision==rev&&ReferenceEquals(node.LastResult,repeat),"cancel keeps only old result");}
        try{await node.RunAsync(e,1,o,new(default,p=>{node.Invalidate();return Task.CompletedTask;}));throw new Exception("published changed input");}catch(InvalidOperationException){}
        Check(node.State==SampleNodeState.Failed&&node.ResultRevision==rev,"invalidation during run rejects publication");
        foreach(var bad in new[]{o with{ManualCore=Vector2.Zero},o with{Radius=float.NaN},o with{PlazaSize=900},o with{ManualEntries="0,0"},o with{ManualCore=new Vector2(900,0)}}){bool rejected=false;try{await node.RunAsync(e,1,bad);}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,"invalid authored placement/parameters rejected");}
        var entry=d.Entries[0];var manual=o with{ManualEntries=FormattableString.Invariant($"{entry.Position.X},{entry.Position.Y}")};var authored=await node.RunAsync(e,1,manual);Check(authored.Entries.Count==1&&authored.Entries[0].Position==entry.Position,"manual entry exact");
        var map=new TemperateMapData(mapSettings);var source=new TemperateEnvironmentSource(map,"real",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());
        var real=EnvironmentSampler.Sample(source,new(source.Bounds));var auto=await node.RunAsync(real,1,new(Vector2.Zero));var again=await node.RunAsync(real,1,new(Vector2.Zero));
        Check(auto.Cells.SequenceEqual(again.Cells)&&auto.Core==again.Core&&auto.Entries.SequenceEqual(again.Entries),"real map repeat");Check(auto.Area>=20000&&auto.Entries.Count>0,"default map workflow");
        string dir=ProjectSettings.GlobalizePath("res://artifacts");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"settlement_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,area=auto.Area,entries=auto.Entries.Count,core=new{x=auto.Core.X,z=auto.Core.Y},warnings=auto.Warnings},new JsonSerializerOptions{WriteIndented=true}));
        GD.Print($"SETTLEMENT_VALIDATION_PASS checks={checks} area={auto.Area} entries={auto.Entries.Count}");
    }
}
