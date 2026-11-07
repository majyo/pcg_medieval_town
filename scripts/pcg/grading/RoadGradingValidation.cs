using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public static class RoadGradingValidation
{
    private sealed class FlatSource:IEnvironmentSource
    {
        public string SourceId=>"grading_fixture";public long Revision=>1;public Rect2 Bounds=>new(0,0,100,100);public float? NativeSpacing=>10;
        public EnvironmentLayers Layers=>new(LayerStatus.Provided,LayerStatus.Provided,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty);
        public IReadOnlyList<ConstraintPolygon> NoBuildZones=>Array.Empty<ConstraintPolygon>();public IReadOnlyList<ConstraintPolygon> Obstacles=>Array.Empty<ConstraintPolygon>();
        public bool Contains(Vector2 p)=>p.X>=0&&p.Y>=0&&p.X<=100&&p.Y<=100;public EnvironmentPoint Query(Vector2 p)=>new(0,0,false,0);
    }
    private sealed class FunctionTerrain:ITerrainSurface
    {
        private readonly Func<Vector2,float> _height;public Func<Vector2,float> Water=p=>float.PositiveInfinity;public FunctionTerrain(string id,Func<Vector2,float> height){Fingerprint=id;_height=height;}
        public float WaterClearance(Vector2 p)=>Water(p);
        public string Fingerprint {get;}public Rect2 Bounds=>new(0,0,100,100);public float Spacing=>1;
        public bool Contains(Vector2 p)=>Bounds.Grow(.001f).HasPoint(p);public float HeightAt(Vector2 p)=>_height(p);
    }

    public static async Task Run(MapSettings settings)
    {
        int checks=0;void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException("ROAD_GRADING_TEST_FAILED: "+message);}
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"grading_test",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());
        var env=EnvironmentSampler.Sample(source,new(source.Bounds));var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));
        var roads=await new RoadNode().RunAsync(env,layout,1,1,new RoadOptions());long chainMs=timer.ElapsedMilliseconds;timer.Restart();
        var terrain=new NativeTerrainSurface(map);var options=new RoadGradingOptions();var node=new RoadGradingNode();
        var result=await node.RunAsync(roads,1,terrain,options);long gradingMs=timer.ElapsedMilliseconds;
        Check(result.Profiles.Count==roads.Edges.Count,"one profile per road");
        Verify(result,Check,"default");
        Check(result.Summary.DesignRoughness<result.Summary.GroundRoughness,"design has less short-range relief than the terrain");

        // Repeatability, invalidation and publication rules.
        var repeat=await node.RunAsync(roads,1,terrain,options);
        Check(result.Graded!.Nodes().SequenceEqual(repeat.Graded!.Nodes())&&result.Graded.Earthwork==repeat.Graded.Earthwork,"deterministic graded terrain");
        Check(result.Profiles.Zip(repeat.Profiles).All(p=>p.First.Design.SequenceEqual(p.Second.Design))&&result.Planes.Select(p=>(p.Height,p.Gradient)).SequenceEqual(repeat.Planes.Select(p=>(p.Height,p.Gradient))),"deterministic design");
        Check(node.IsCurrent(roads,1,terrain,options),"current after run");
        Check(!node.IsCurrent(roads,2,terrain,options)&&!node.IsCurrent(roads,1,new NativeTerrainSurface(map),options)&&!node.IsCurrent(roads,1,terrain,options with{SmoothingScale=30})&&!node.IsCurrent(roads,1,terrain,options with{FillRatio=2}),"road revision, terrain instance and parameters invalidate");
        long revision=node.ResultRevision;
        using(var cancel=new CancellationTokenSource()){try{await node.RunAsync(roads,1,terrain,options,new(cancel.Token,p=>{cancel.Cancel();return Task.CompletedTask;}));}catch(OperationCanceledException){}
            Check(node.State==SampleNodeState.Cancelled&&node.ResultRevision==revision&&ReferenceEquals(node.LastResult,repeat),"cancel publishes nothing");}
        using(var cancelTerrain=new CancellationTokenSource()){try{await node.RunAsync(roads,1,terrain,options,new(cancelTerrain.Token,p=>{if(p.Fraction>=.94f)cancelTerrain.Cancel();return Task.CompletedTask;}));}catch(OperationCanceledException){}
            Check(node.State==SampleNodeState.Cancelled&&node.ResultRevision==revision&&ReferenceEquals(node.LastResult,repeat),"cancellation during terrain writing publishes nothing");}
        try{await node.RunAsync(roads,1,terrain,options,new(default,p=>{node.Invalidate();return Task.CompletedTask;}));}catch(InvalidOperationException){}
        Check(node.State==SampleNodeState.Failed&&node.ResultRevision==revision,"input change during run rejects publication");
        bool rejected=false;try{await node.RunAsync(roads,1,new FunctionTerrain("small",p=>0),options);}catch(ArgumentException){rejected=true;}Check(rejected&&node.ResultRevision==revision,"terrain from another map rejected");
        rejected=false;try{await node.RunAsync(roads,1,terrain,options with{Crossfall=-1});}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"invalid parameters rejected");
        var smoother=await new RoadGradingNode().RunAsync(roads,1,terrain,options with{SmoothingScale=40});Verify(smoother,Check,"smoothing 40");
        var follow=await new RoadGradingNode().RunAsync(roads,1,terrain,options with{SmoothingScale=0});Verify(follow,Check,"smoothing 0");
        Check(follow.Summary.CutVolume+follow.Summary.FillVolume<smoother.Summary.CutVolume+smoother.Summary.FillVolume,"longer smoothing moves more earth");

        // Synthetic cases on a 100 m fixture.
        var fixture=new FlatSource();var flat=EnvironmentSampler.Sample(fixture,new(fixture.Bounds));
        var cells=Enumerable.Range(0,100).Select(k=>new LayoutCell(SettlementCellState.Included,EnvironmentRestriction.None,0,false)).ToArray();
        var fixtureLayout=new SettlementLayout(flat,"fixture",1,new(new(50,50),PlazaSize:1,Walls:false,CivicPrecincts:false,Regional:false),new(97,97),cells,Array.Empty<LayoutEdge>(),Array.Empty<LayoutEntry>(),Array.Empty<LayoutEdge>(),Array.Empty<string>());
        RoadEdge Edge(int id,int from,int to,float width,params Vector2[] points)=>new(id,from,to,Array.AsReadOnly(points),width>=6,width,0,0);
        async Task<RoadNetwork> Shape(params RoadEdge[] edges){var options=new RoadOptions();var nodes=edges.SelectMany(e=>new[]{new RoadVertex(e.From,e.Points[0],0,"fixture"),new RoadVertex(e.To,e.Points[^1],0,"fixture")}).DistinctBy(n=>n.Id).ToArray();
            var shaped=await RoadGeometryPlanner.Finalize(flat,fixtureLayout,nodes,edges,options,new());return new(flat,fixtureLayout,1,1,options,nodes,shaped.Edges,new(){{fixtureLayout.CoreId,nodes[0].Id}},new(),new(),shaped.Geometry);}

        var straight=await Shape(Edge(0,0,1,6,new(5,50),new(95,50)));
        var bumpy=new FunctionTerrain("bumps",p=>.02f*p.X+.25f*MathF.Sin(p.X*Mathf.Tau/6));
        var bumps=await new RoadGradingNode().RunAsync(straight,1,bumpy,options);Verify(bumps,Check,"bumps");
        Check(bumps.Summary.DesignRoughness<bumps.Summary.GroundRoughness*.15f,$"short bumps removed: ground {bumps.Summary.GroundRoughness:0.000} design {bumps.Summary.DesignRoughness:0.000}");
        Check(!bumps.Profiles[0].GradeChangeRelaxed,"bumpy straight road keeps the grade-change limit");

        var tilted=new FunctionTerrain("cross",p=>.08f*(p.Y-50)+.01f*p.X);
        var cross=await new RoadGradingNode().RunAsync(straight,1,tilted,options);Verify(cross,Check,"cross slope");
        bool level=true;for(float x=20;x<=80;x+=5){cross.TryDesignHeight(new(x,48),out float left);cross.TryDesignHeight(new(x,52),out float right);cross.TryDesignHeight(new(x,50),out float center);
            if(Math.Abs(left-right)>.005f||Math.Abs(center-left-.02f*2)>.005f)level=false;}
        Check(level,"cross-section is a symmetric crown, independent of terrain cross slope");
        var ct=cross.Graded!;bool cutUphill=false,fillDownhill=false,farUntouched=true;
        for(float x=20;x<=80;x+=1){if(ct.DeltaAt(new(x,53.9f))<-.01f)cutUphill=true;if(ct.DeltaAt(new(x,46.1f))>.01f)fillDownhill=true;
            foreach(float z in new[]{40f,60f})if(Math.Abs(ct.DeltaAt(new(x,z)))>1e-5f)farUntouched=false;}
        Check(cutUphill&&fillDownhill&&farUntouched,"cut slope uphill, fill slope downhill, terrain untouched beyond reach");
        var steepCross=new FunctionTerrain("cross_water",p=>.3f*(p.Y-50)){Water=p=>Math.Max(56-p.Y,p.X-94)};
        var bank=await new RoadGradingNode().RunAsync(straight,1,steepCross,options);Verify(bank,Check,"water lock");
        var bt=bank.Graded!;Check(bt.Nodes().Where(n=>n.Kind==GradingCell.Lock&&bt.NodePosition(n.I,n.J).Y>55).Count()>0,"water bank nodes locked");
        Check(bank.Notices.Any(n=>n.Kind=="terrain_retry")&&(bt.Earthwork.Conflicts==0||bt.RetainingWalls.Count>0&&bank.Notices.Any(n=>n.Kind=="retaining")),"locked-bank conflicts retried and remaining retaining walls reported");

        var blockedWater=new FunctionTerrain("water_on_road",p=>.08f*(p.Y-50)){Water=p=>48-p.Y};bool waterRejected=false;var lockedNode=new RoadGradingNode();try{await lockedNode.RunAsync(straight,1,blockedWater,options);}catch(RoadGradingFailure){waterRejected=true;}Check(waterRejected&&lockedNode.LastResult==null,"road entering a locked area fails without publication");
        var tee=await Shape(Edge(0,0,1,8,new(50,50),new(10,50)),Edge(1,0,2,8,new(50,50),new(90,50)),Edge(2,0,3,3,new(50,50),new(50,10)));
        var sloped=new FunctionTerrain("slope",p=>.06f*p.X+.04f*p.Y+.1f*MathF.Sin(p.Y*.4f));
        var junction=await new RoadGradingNode().RunAsync(tee,1,sloped,options);Verify(junction,Check,"tee");
        Check(junction.Planes.Count==1&&junction.Planes[0].Slope<=options.JunctionSlope/100+1e-5f,"junction platform within slope limit");
        Check(junction.Notices.Any(n=>n.Kind=="junction"),"steep junction platform reported");
        var plane=junction.Planes[0];var junctionSurface=tee.Geometry.Surfaces.First(s=>s.NodeId.HasValue);
        Check(junctionSurface.Shape.Triangles.All(p=>{var c=p;junction.TryDesignHeight(c,out float h);return Math.Abs(h-plane.HeightAt(c))<.0005f;}),"junction surface follows its plane");

        var steep=new FunctionTerrain("steep",p=>.3f*p.X);rejected=false;var failing=new RoadGradingNode();
        try{await failing.RunAsync(straight,1,steep,options);}catch(RoadGradingFailure f){rejected=f.RoadId==0;}
        Check(rejected&&failing.LastResult==null&&failing.State==SampleNodeState.Failed,"grade beyond cut/fill limits fails without publication");

        var s=result.Summary;string dir=ProjectSettings.GlobalizePath("res://artifacts");Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"road_grading_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,chainMs,gradingMs,profiles=result.Profiles.Count,planes=result.Planes.Count,
            relaxed=s.RelaxedProfiles,cutVolume=Math.Round(s.CutVolume),fillVolume=Math.Round(s.FillVolume),maxCut=s.MaxCut,maxFill=s.MaxFill,maxGradePercent=s.MaxGrade*100,maxGradeChangePercentPer10m=s.MaxGradeChange*100,
            groundRoughness=s.GroundRoughness,designRoughness=s.DesignRoughness,sampledArea=s.SampledArea,notices=result.Notices.GroupBy(n=>n.Kind).ToDictionary(g=>g.Key,g=>g.Count()),
            relaxedProfiles=result.Profiles.Where(p=>p.GradeChangeRelaxed).Select(p=>{int at=1;float m=0;for(int i=1;i+1<p.Design.Count;i++){float c=Math.Abs(p.Design[i-1]-2*p.Design[i]+p.Design[i+1]);if(c>m){m=c;at=i;}}var e=roads.Edges.First(x=>x.Id==p.RoadId);var line=new Centerline(e);var pt=line.PointAt(p.StationAt(at));
                return new{road=p.RoadId,p.Length,station=p.StationAt(at),change=p.MaxGradeChange*100,runs=p.StraightRuns.Count,plazaGap=result.PlazaDistance(pt),from=$"{e.From}:{result.NodeHeights[e.From]:0.00}/{p.Ground[0]:0.00}",to=$"{e.To}:{result.NodeHeights[e.To]:0.00}/{p.Ground[^1]:0.00}",
                    around=string.Join(" ",Enumerable.Range(Math.Max(0,at-4),Math.Min(9,p.Design.Count-Math.Max(0,at-4))).Select(i=>$"{p.Design[i]:0.00}/{p.Ground[i]:0.00}"))};}).ToArray(),
            smoothing40=new{cut=Math.Round(smoother.Summary.CutVolume),fill=Math.Round(smoother.Summary.FillVolume),smoother.Summary.MaxCut,smoother.Summary.MaxFill},
            smoothing0=new{cut=Math.Round(follow.Summary.CutVolume),fill=Math.Round(follow.Summary.FillVolume),follow.Summary.MaxCut,follow.Summary.MaxFill}},new JsonSerializerOptions{WriteIndented=true}));
        var ew=result.Graded!.Earthwork;GD.Print($"GRADED_TERRAIN roadCut={ew.RoadCut:0} roadFill={ew.RoadFill:0} slopeCut={ew.SlopeCut:0} slopeFill={ew.SlopeFill:0} area={ew.ModifiedArea:0} reach={ew.BandReach:0.00} nodes={ew.ModifiedNodes} tiles={ew.Tiles} conflicts={ew.Conflicts} walls={result.Graded.RetainingWalls.Count}");
        GD.Print($"ROAD_GRADING_VALIDATION_PASS checks={checks} profiles={result.Profiles.Count} planes={result.Planes.Count} cut={s.CutVolume:0} fill={s.FillVolume:0} maxCut={s.MaxCut:0.00} maxFill={s.MaxFill:0.00} relaxed={s.RelaxedProfiles} ms={gradingMs}");
    }

    // The written terrain: roads at design height, slopes within the envelope, locks and the far field untouched.
    private static void VerifyTerrain(RoadGrading g,Action<bool,string> Check,string label)
    {
        var t=g.Graded;Check(t!=null,$"{label}: graded terrain published");if(t==null)return;var o=g.Options;float kc=1/o.CutRatio,kf=1/o.FillRatio;
        var nodes=t.Nodes().ToArray();var byIndex=nodes.ToDictionary(n=>(n.I,n.J));int roadBad=0,lockBad=0,envelopeBad=0,reachBad=0,sampled=0;
        var sources=nodes.Where(n=>n.Kind is GradingCell.Road or GradingCell.Shoulder or GradingCell.Lock).ToArray();
        int window=(int)Math.Ceiling(t.Earthwork.BandReach/t.Cell);
        foreach(var n in nodes){var p=t.NodePosition(n.I,n.J);float h=t.RenderHeightAt(p),original=t.Source.HeightAt(p);
            if(n.Kind==GradingCell.Road&&t.Source.Bounds.Grow(-3.6f).HasPoint(p)&&g.TryDesignHeight(p,out float design)&&Math.Abs(h-design)>.001f)roadBad++;
            if(n.Kind==GradingCell.Lock&&Math.Abs(n.Delta)>1e-6f)lockBad++;
            if(n.Kind!=GradingCell.Band||Math.Abs(n.Delta)<1e-5f)continue;
            bool near=false;float upper=float.PositiveInfinity,lower=float.NegativeInfinity;sampled++;
            for(int dj=-window;dj<=window;dj++)for(int di=-window;di<=window;di++){if(!byIndex.TryGetValue((n.I+di,n.J+dj),out var s)||s.Kind is GradingCell.Band or GradingCell.Conflict or GradingCell.None)continue;
                float d=MathF.Sqrt(di*di+dj*dj)*t.Cell;if(d>t.Earthwork.BandReach)continue;var q=t.NodePosition(s.I,s.J);float z=t.Source.HeightAt(q)+s.Delta;
                if(s.Kind!=GradingCell.Lock)near=true;upper=Math.Min(upper,z+kc*d);lower=Math.Max(lower,z-kf*d);}
            if(!near)reachBad++;if(h>upper+.001f||h<lower-.001f||(Math.Abs(h-original)>1e-4f&&Math.Abs(h-upper)>.001f&&Math.Abs(h-lower)>.001f))envelopeBad++;}
        Check(roadBad==0,$"{label}: terrain equals the design inside the road ({roadBad} off)");
        Check(lockBad==0,$"{label}: locked water, wall and border nodes unchanged ({lockBad})");
        Check(reachBad==0,$"{label}: changes stay within reach of the road ({reachBad})");
        Check(envelopeBad==0,$"{label}: slopes follow the cut/fill envelope ({envelopeBad}/{sampled})");
        Check(t.RetainingWalls.Count==g.Notices.Count(n=>n.Kind=="retaining"),$"{label}: every retaining wall reported");
        Check(nodes.Where(n=>n.Kind==GradingCell.Conflict).All(n=>t.RetainingWalls.Any(w=>w.Nodes>0)),$"{label}: conflicts grouped into walls");
    }
    // Structural checks shared by every case.
    private static void Verify(RoadGrading g,Action<bool,string> Check,string label)
    {
        var o=g.Options;var roads=g.Roads;float maxStep=MathF.Tan(Mathf.DegToRad(roads.Options.MaxGrade));
        foreach(var p in g.Profiles){var e=roads.Edges.First(x=>x.Id==p.RoadId);
            Check(Math.Abs(p.Design[0]-g.NodeHeights[e.From])<.001f&&Math.Abs(p.Design[^1]-g.NodeHeights[e.To])<.001f,$"{label}: R{p.RoadId} ends meet node heights");
            Check(p.MaxGrade<=maxStep+1e-4f,$"{label}: R{p.RoadId} grade {p.MaxGrade:0.000} within limit");
            bool bounded=true;for(int i=0;i<p.Design.Count;i++)if(p.Design[i]<p.Ground[i]-o.MaxCut-1e-3f||p.Design[i]>p.Ground[i]+o.MaxFill+1e-3f)bounded=false;Check(bounded,$"{label}: R{p.RoadId} centerline within cut/fill limits");
            if(!p.GradeChangeRelaxed)Check(p.MaxGradeChange<=o.GradeChange/100+2e-3f,$"{label}: R{p.RoadId} grade change {p.MaxGradeChange*100:0.00}%");
            else Check(g.Notices.Any(n=>n.Kind=="grade_change"&&n.RoadId==p.RoadId),$"{label}: relaxed R{p.RoadId} reported");
            foreach(var (a,b) in p.StraightRuns)for(int i=a+1;i<b;i++)Check(Math.Abs(p.Design[i]-(p.Design[a]+(p.Design[b]-p.Design[a])*(i-a)/(b-a)))<.002f,$"{label}: gate passage straight on R{p.RoadId}");
        }
        foreach(var plane in g.Planes)Check(plane.Slope<=o.JunctionSlope/100+1e-5f,$"{label}: junction {plane.Id} slope");
        Check(g.Plaza.Slope<=o.PlazaSlope/100+1e-5f,$"{label}: plaza slope");
        // Continuity: no steps between neighbouring points anywhere in the designed area.
        int samples=0,jumps=0,undefined=0;float worst=0;Vector2 worstAt=default;const float eps=.02f;
        foreach(var s in roads.Geometry.Surfaces){var tri=s.Shape.Triangles;
            for(int i=0;i+2<tri.Count;i+=3){var a=tri[i];var b=tri[i+1];var c=tri[i+2];float den=(b-a).Cross(c-a);if(Math.Abs(den)<1e-9f)continue;var min=a.Min(b).Min(c);var max=a.Max(b).Max(c);
                for(float z=MathF.Floor(min.Y*2)/2+.25f;z<=max.Y;z+=.5f)for(float x=MathF.Floor(min.X*2)/2+.25f;x<=max.X;x+=.5f){var p=new Vector2(x,z);float u=(p-a).Cross(c-a)/den,v=(b-a).Cross(p-a)/den;if(u<0||v<0||u+v>1)continue;
                    if(!g.TryDesignHeight(p,out float h)){undefined++;continue;}samples++;
                    foreach(var d in new[]{new Vector2(eps,0),new Vector2(0,eps)})if(g.TryDesignHeight(p+d,out float q)){float step=Math.Abs(q-h);if(step>worst){worst=step;worstAt=p;}if(step>eps*.35f+.005f)jumps++;}}}}
        string Describe(Vector2 p){var s=g.SurfaceAt(p);g.TryDesignHeight(p,out float h);if(s==null)return $"none plaza={g.InPlaza(p)} h={h:0.000}";var (st,off)=g.Locate(s.RoadId,p);return $"S{s.Id} R{s.RoadId} N{s.NodeId} pure={g.IsPureSweep(p)} h={h:0.000} sweep={g.SweepHeight(s.RoadId,p):0.000} s={st:0.000} o={off:0.000} [{g.DescribeBlend(p)}]";}
        if(jumps>0){var sw=g.SurfaceAt(worstAt);if(sw!=null){var road=roads.Edges.First(e=>e.Id==sw.RoadId);File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/debug_grading_road.txt"),$"R{road.Id} width {road.Width}\n"+string.Join("\n",road.Points.Select(q=>$"{q.X:0.000},{q.Y:0.000}")));}}
        string detail=jumps==0?"":string.Join(" | ",new[]{worstAt,worstAt+new Vector2(eps,0),worstAt+new Vector2(0,eps)}.Select(Describe));
        Check(undefined==0,$"{label}: design defined over the whole road area ({undefined} undefined)");
        Check(samples>0&&jumps==0,$"{label}: design surface continuous ({jumps} jumps, worst {worst*1000:0.0} mm at {worstAt.X:0.0},{worstAt.Y:0.0}) {detail}");
        // Flat cross-sections where no plane is blended in.
        int flatChecked=0,bent=0;var bentInfo=new List<string>();float c2=o.Crossfall/100;
        foreach(var p in g.Profiles){var e=roads.Edges.First(x=>x.Id==p.RoadId);var line=new Centerline(e);
            for(float st=2;st<p.Length-2;st+=5){var center=line.PointAt(st);var normal=line.NormalAt(st);if(!g.IsPureSweep(center))continue;g.TryDesignHeight(center,out float h0);
                float turn=new[]{-8f,-6,-4,-2,2,4,6,8}.Max(k=>Math.Abs(line.TangentAt(st).AngleTo(line.TangentAt(st+k))));
                bool alone=Enumerable.Range(0,(int)p.Length).All(k=>Math.Abs(k-st)<=10||line.PointAt(k).DistanceTo(center)>=6);
                bool straight=turn<Mathf.DegToRad(2)&&alone;float tolerance=straight?.005f:.08f;float radius=straight?float.PositiveInfinity:0;
                foreach(float f in new[]{-.8f,-.4f,.4f,.8f}){float lateral=f*p.Width/2;var q=center+normal*lateral;if(!g.IsPureSweep(q)||!g.TryDesignHeight(q,out float h))continue;flatChecked++;
                    float expected=h0+(p.Section==RoadCrossSection.Crown?-1:1)*c2*Math.Abs(lateral);if(Math.Abs(h-expected)>tolerance){bent++;if(bentInfo.Count<6)bentInfo.Add($"R{p.RoadId} s={st:0.0}/{p.Length:0.0} f={f} r={radius:0} d={(h-expected)*1000:0.0}mm");}}}}
        VerifyTerrain(g,Check,label);
        Check(flatChecked>0&&bent==0,$"{label}: flat cross-sections, 5 mm on straight stretches, 8 cm in bends: {bent}/{flatChecked} off {string.Join("; ",bentInfo)}");
    }
}
