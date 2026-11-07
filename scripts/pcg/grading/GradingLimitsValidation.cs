using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

public static class GradingLimitsValidation
{
    private sealed class PlaneTerrain(Func<Vector2,float> height):ITerrainSurface
    {
        public string Fingerprint=>"limits-fixture";
        public Rect2 Bounds=>new(0,0,20,20);
        public float Spacing=>1;
        public bool Contains(Vector2 p)=>Bounds.Grow(.001f).HasPoint(p);
        public float HeightAt(Vector2 p)=>height(p);
        public float WaterClearance(Vector2 p)=>float.PositiveInfinity;
    }
    public static async Task Run(MapSettings original)
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("GRADING_LIMITS_FAILED: "+message);}
        var options=new RoadGradingOptions(MaxCut:.2f,MaxFill:.2f);var shape=new ParcelRegion(ParcelGeometry.Rect(new(0,0,10,10)));
        var terrain=new PlaneTerrain(p=>0);var bounded=GradingEarthworkLimits.ConstrainPlane(new("test",null,new(5,5),.6f,new(.1f,0),.1f),terrain,shape,.02f,options,default);
        Check(bounded.Slope<=.02001f&&shape.Rings.SelectMany(r=>r).All(p=>Math.Abs(bounded.HeightAt(p))<=.2001f),"plane is constrained at its full boundary");
        bool impossible=false;try{GradingEarthworkLimits.ConstrainPlane(new("impossible",null,new(5,5),0,Vector2.Zero,0),new PlaneTerrain(p=>p.X),shape,.02f,options,default);}catch(RoadGradingFailure){impossible=true;}
        Check(impossible,"infeasible plane cannot be published");
        var solution=RoadGradingGenerator.Solve(new double[]{0,0,0},new double[]{-.2,-.2,-.2},new double[]{.2,.2,.2},1,0,1,.1,.01,new(),out _,out _);
        Check(solution==null,"pinned endpoint cannot override earthwork bounds");
        VerticalProblem Segment(int id,int from,int to,float start){var line=new Centerline(new[]{new Vector2(start,0),new Vector2(start+10,0)});var road=new RoadEdge(id,from,to,Array.AsReadOnly(line.P),false,3,10,0);
            return new(road,line,RoadCrossSection.Crown,new double[11],Enumerable.Repeat(1.8,11).ToArray(),Enumerable.Repeat(-2d,11).ToArray(),Enumerable.Repeat(2d,11).ToArray(),new(),Enumerable.Repeat(double.NegativeInfinity,11).ToArray(),Enumerable.Repeat(double.PositiveInfinity,11).ToArray());}
        var sharedNodes=new Dictionary<int,float>{{0,0},{1,1.8f},{2,0}};var connected=new[]{Segment(0,0,1,0),Segment(1,1,2,10)};
        if(!OS.GetCmdlineUserArgs().Contains("--dump-joint-model")){var shared=JointVerticalSolver.Solve(connected,sharedNodes,new(){0,2},.1f,2,(0,0),default);
        Check(Math.Abs(shared.Profiles[0][^1]-shared.Profiles[1][0])<1e-9&&sharedNodes[1]<1.01f,"joint solve moves an initially incompatible shared node while preserving connectivity");
        foreach(var elevations in shared.Profiles.Values)for(int i=1;i+1<elevations.Length;i++)Check(Math.Abs(elevations[i-1]-2*elevations[i]+elevations[i+1])<=.002001,"synthetic profile respects the original curvature bound");
        }
        var rows=new List<object>();
        var seedArg=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--validation-seed="));
        foreach(int seed in seedArg!=null?new[]{int.Parse(seedArg.Split('=')[1])}:new[]{73129,7,22017}){
            var settings=(MapSettings)original.Duplicate();settings.Seed=seed;var map=new TemperateMapData(settings);
            var source=new TemperateEnvironmentSource(map,"grading-limits",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());
            var env=EnvironmentSampler.Sample(source,new(source.Bounds));var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var roads=await new RoadNode().RunAsync(env,layout,1,1,new());
            var node=new RoadGradingNode();var grading=await node.RunAsync(roads,1,new NativeTerrainSurface(map),new(),new(default,p=>{GD.Print($"LIMITS_PROGRESS seed={seed} f={p.Fraction:0.00} {p.Message}");return Task.CompletedTask;}));
            Check(grading.Profiles.All(p=>!p.GradeChangeRelaxed&&p.MaxGradeChange<=.02001f&&p.ContinuousMaxGradeChange<=.02001f),"all real profiles retain the original grade-change limit");
            var audit=grading.EarthworkAudit;Check(audit.Samples>0&&audit.Violations==0,"final surface audit");
            rows.Add(new{seed,core=new[]{layout.Core.X,layout.Core.Y},surfaceQuality=GradingSurfaceQuality.Measure(grading),audit,profiles=grading.Profiles.Count,relaxedProfiles=grading.Summary.RelaxedProfiles,maxGradeChange=grading.Profiles.Max(p=>p.MaxGradeChange),continuousGradeChange=grading.Profiles.Max(p=>p.ContinuousMaxGradeChange),plazaAdjustment=grading.Notices.Where(n=>n.Kind=="plaza_adjustment").Select(n=>n.Reason).ToArray()});
            GD.Print($"GRADING_LIMITS_SEED_PASS seed={seed} samples={audit.Samples} cut={audit.MaxCut:0.000} fill={audit.MaxFill:0.000}");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath(seedArg==null?"res://artifacts/joint_grading_validation.json":$"res://artifacts/joint_grading_seed_{int.Parse(seedArg.Split('=')[1])}.json"),JsonSerializer.Serialize(new{passed=true,acceptanceScope="discrete joint profiles and sampled full-surface earthwork; surface curvature/cross-section quality is reported separately",rows},new JsonSerializerOptions{WriteIndented=true}));
    }
}
