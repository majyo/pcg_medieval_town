using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;
public static class BuildingGenerator
{
    private sealed record StreetPlot(LandParcel Parcel,StreetFront Front,Vector2 Tangent,Vector2 Inward);
    private sealed record Fit(BuildingMass Building,Vector2 BackCenter);
    public static async Task<BuildingSet> Generate(ParcelLayout input,long revision,BuildingOptions o,PcgContext ctx)
    {
        if(!float.IsFinite(o.HeightScale)||o.HeightScale<.4f||o.HeightScale>2||!float.IsFinite(o.Variation)||o.Variation<0||o.Variation>.5f||
            !float.IsFinite(o.PassageWidth)||o.PassageWidth<.8f||o.PassageWidth>3||!float.IsFinite(o.MaxFoundation)||o.MaxFoundation<.2f||o.MaxFoundation>4||!float.IsFinite(o.RoofAngle)||o.RoofAngle<20||o.RoofAngle>60)
            throw new ArgumentException("建筑高度、变化、通道、基础或屋顶参数无效");
        async Task Yield(float f,string text){ctx.Cancellation.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(f,text));ctx.Cancellation.ThrowIfCancellationRequested();}
        await Yield(0,"组织沿街宅地与建筑规则");
        var byParcel=input.Parcels.ToDictionary(p=>p.Id);var env=input.Terrain;var buildings=new List<BuildingMass>();var access=new List<BuildingAccess>();
        var issues=new List<BuildingIssue>();var open=new List<BuildingOpenSpace>();var processed=new HashSet<int>();
        uint Hash(int value){unchecked{uint h=(uint)(value^o.Seed);h^=h>>16;h*=0x7feb352d;h^=h>>15;return h;}}
        var blocked=input.Settlement.Town?.BuildingReserve.CopyPaths()??new Paths64();
        bool Inward(LandParcel parcel,StreetFront front,out Vector2 direction){
            var tangent=(front.B-front.A).Normalized();direction=new(-tangent.Y,tangent.X);var mid=(front.A+front.B)/2;
            if(parcel.Shape.Contains(mid+direction*.15f))return true;direction=-direction;return parcel.Shape.Contains(mid+direction*.15f);
        }
        var plots=new List<StreetPlot>();
        foreach(var p in input.Parcels){
            var front=p.Frontage.OrderByDescending(f=>f.Length).ThenBy(f=>f.RoadId).First();
            if(!Inward(p,front,out var normal)){issues.Add(new($"P{p.Id}",p.Shape.Center,"无法确定合法临街方向"));processed.Add(p.Id);continue;}
            plots.Add(new(p,front,(front.B-front.A).Normalized(),normal));
            if(p.Yard!=null)open.Add(new($"P{p.Id}","后院与园地",p.Yard));
        }
        var failures=new Dictionary<int,HashSet<string>>();
        void Failed(int id,string reason){if(!failures.ContainsKey(id))failures[id]=new();failures[id].Add(reason);}
        int nextId=0,totalCandidates=0;
        BuildingMass? Make(int id,Vector2[] corners,Paths64 allowed,int? parcelId,string? precinct,string district,string program,
            int floors,bool inside,bool ridgeAlongDepth,bool eaves,Vector2 door,Vector2 street)
        {
            BuildingMass? Reject(string reason){if(parcelId.HasValue)Failed(parcelId.Value,reason);return null;}
            var footprint=BuildingGeometry.Polygon(corners);
            if(!BuildingGeometry.Fits(footprint,allowed))return Reject("完整占地无法放入建筑带");
            if(!BuildingGeometry.Clear(footprint,blocked))return Reject("公共入口或城防预留冲突");
            if(!BuildingGeometry.Ground(env,corners,o.MaxFoundation,out float min,out float max))return Reject("局部地基高差过大或缺少地面数据");
            if(input.Grading!=null&&parcelId.HasValue){
                var offset=street-door;float score=float.NegativeInfinity;Vector2 entrance=door;
                foreach(float f in new[]{.5f,.25f,.75f}){var p=corners[0].Lerp(corners[1],f);float candidate=Math.Min(env.HeightAt(p),env.HeightAt(p+offset));if(candidate>score){score=candidate;entrance=p;}}
                door=entrance;street=door+offset;
            }
            if(!env.TryHeightAt(door,out float ground)||max+.06f-ground>(input.Grading!=null&&parcelId.HasValue?.35f:.8f))return Reject("门口高差超限");
            var tangent=(corners[1]-corners[0]).Normalized();var inward=(corners[3]-corners[0]).Normalized();
            float width=corners[0].DistanceTo(corners[1]),depth=corners[0].DistanceTo(corners[3]);
            var roof=footprint;
            if(eaves){var expanded=BuildingGeometry.Polygon(BuildingGeometry.Rectangle(corners[0]-(tangent+inward)*.25f,tangent,inward,width+.5f,depth+.5f));
                if(BuildingGeometry.Fits(expanded,allowed)&&BuildingGeometry.Clear(expanded,blocked))roof=expanded;}
            uint row=Hash(parcelId.HasValue?byParcel[parcelId.Value].Frontage[0].RoadId:precinct=="church"?391:719);
            float wallHeight=floors*(2.7f+(row%100)/250f)*o.HeightScale;
            float roofAngle=o.RoofAngle+((Hash(id)%101)/100f-.5f)*o.Variation*12;
            float roofHeight=(ridgeAlongDepth?width:depth)/2*Mathf.Tan(Mathf.DegToRad(roofAngle));
            return new(id,parcelId,precinct,district,program,new ParcelRegion(footprint),new ParcelRegion(roof),Array.AsReadOnly(corners),inward,door,street,max+.06f,wallHeight,roofHeight,program=="教堂主殿"?1:floors,inside,min,max,ridgeAlongDepth,program=="教堂主殿");
        }
        Fit? House(StreetPlot plot,float depth,float setback,float left,float right,float widthScale,bool pair,int id)
        {
            if(++totalCandidates>30000)throw new InvalidOperationException("建筑候选超过本次预算，请缩小范围或调整宅地尺寸");
            float available=plot.Front.Length-left-right;float width=available*widthScale;
            if(width<3||width*depth<24){Failed(plot.Parcel.Id,"主屋面宽或占地不足");return null;}
            var front=plot.Front.A+plot.Tangent*left+plot.Inward*setback;
            var corners=BuildingGeometry.Rectangle(front,plot.Tangent,plot.Inward,width,depth);
            var door=(corners[0]+corners[1])/2;var street=door-plot.Inward*setback;
            var road=input.Roads.Edges.First(e=>e.Id==plot.Front.RoadId);
            bool dense=plot.Parcel.InsideWalls&&(road.Main||road.Role==StreetRole.MarketStreet);
            int floors=dense?2+(Hash(road.Id)%5==0?1:0):1+(Hash(road.Id)%3==0?1:0);
            var b=Make(id,corners,plot.Parcel.Buildable.CopyPaths(),plot.Parcel.Id,null,plot.Parcel.DistrictId,
                plot.Parcel.Use switch{ParcelUse.Commercial=>"商住",ParcelUse.Production=>"作坊住宅",_=>"住宅"},floors,plot.Parcel.InsideWalls,pair||depth>=width*1.15f,!pair,door,street);
            if(b==null)return null;
            if(setback<.6f&&b.BaseY-GetHeight(b.Door)>.28f){Failed(plot.Parcel.Id,"门前没有足够台阶空间");return null;}
            return new(b,(corners[2]+corners[3])/2);
        }
        float GetHeight(Vector2 p){env.TryHeightAt(p,out float h);return h;}
        Paths64 Strip(Vector2 a,Vector2 b,float width)=>a.DistanceTo(b)<.001f?new Paths64():BuildingGeometry.Polygon(RoadSpace.Strip(a,b,width));
        bool AccessValid(Paths64 corridor,Paths64 allowed,IEnumerable<BuildingMass> masses)
        {
            if(!BuildingGeometry.Fits(corridor,allowed)||!BuildingGeometry.Clear(corridor,blocked))return false;
            foreach(var b in masses)if(!BuildingGeometry.Clear(corridor,b.RoofProjection.CopyPaths()))return false;return true;
        }
        float YardDepth(StreetPlot plot,Vector2 start,float minimum){
            if(plot.Parcel.Yard==null||plot.Parcel.Yard.Area<1)return minimum;
            float limit=plot.Parcel.Shape.Bounds.Size.Length()+5;
            for(float d=minimum;d<=limit;d+=.5f)if(plot.Parcel.Yard.Contains(start+plot.Inward*d))return d;
            return float.NaN;
        }
        bool TryPair(StreetPlot a,StreetPlot b)
        {
            if(!o.PartyWalls||!a.Parcel.AllowsPartyWalls||!b.Parcel.AllowsPartyWalls||!a.Parcel.InsideWalls||!b.Parcel.InsideWalls||
                a.Front.RoadId!=b.Front.RoadId||a.Tangent.Dot(b.Tangent)<.9999f||a.Inward.Dot(b.Inward)<.9999f||a.Front.B.DistanceTo(b.Front.A)>.015f)return false;
            var allowed=ParcelGeometry.Union(new Paths64(a.Parcel.Shape.CopyPaths().Concat(b.Parcel.Shape.CopyPaths())));
            foreach(float depth in new[]{12f,10f,8f,6f})foreach(float setback in new[]{.05f,.8f,1.6f}){
                var fa=House(a,depth,setback,o.PassageWidth,0,1,true,nextId);
                var fb=House(b,depth,setback,0,0,1,true,nextId+1);
                if(fa==null||fb==null)continue;
                if(!BuildingGeometry.Clear(fa.Building.Footprint.CopyPaths(),fb.Building.Footprint.CopyPaths()))continue;
                var laneStart=a.Front.A+a.Tangent*(o.PassageWidth/2);
                float back=Math.Max(YardDepth(a,laneStart,depth+setback+o.PassageWidth/2+.25f),YardDepth(b,(b.Front.A+b.Front.B)/2,depth+setback+o.PassageWidth/2+.25f));if(!float.IsFinite(back))continue;
                var laneEnd=laneStart+a.Inward*back;
                var bYard=(b.Front.A+b.Front.B)/2+b.Inward*back;
                var corridor=ParcelGeometry.Union(new Paths64(Strip(laneStart,laneEnd,o.PassageWidth).Concat(Strip(laneEnd,bYard,o.PassageWidth))));
                var doorA=Strip(fa.Building.StreetPoint,fa.Building.Door,o.PassageWidth);
                var doorB=Strip(fb.Building.StreetPoint,fb.Building.Door,o.PassageWidth);
                var total=ParcelGeometry.Union(new Paths64(corridor.Concat(doorA).Concat(doorB)));
                if(!AccessValid(total,allowed,new[]{fa.Building,fb.Building}))continue;
                if(!a.Parcel.Yard!.Contains(laneEnd)||!b.Parcel.Yard!.Contains(bYard))continue;
                buildings.Add(fa.Building);buildings.Add(fb.Building);nextId+=2;
                var owners=Array.AsReadOnly(new[]{a.Parcel.Id,b.Parcel.Id});
                access.Add(new(fa.Building.Id,owners,a.Front.RoadId,new ParcelRegion(ParcelGeometry.Union(new Paths64(corridor.Concat(doorA)))),fa.Building.Door,fa.Building.StreetPoint,laneEnd,"共享通院小巷"));
                access.Add(new(fb.Building.Id,owners,b.Front.RoadId,new ParcelRegion(ParcelGeometry.Union(new Paths64(corridor.Concat(doorB)))),fb.Building.Door,fb.Building.StreetPoint,bYard,"共享通院小巷"));
                processed.Add(a.Parcel.Id);processed.Add(b.Parcel.Id);return true;
            }return false;
        }
        var ordered=plots.OrderBy(p=>p.Front.RoadId).ThenBy(p=>p.Front.A.Y).ThenBy(p=>p.Front.A.X).ToArray();
        // Explicit adjacency, rather than assuming that list neighbours share a parcel edge.
        for(int i=0;i<ordered.Length;i++){
            var a=ordered[i];if(processed.Contains(a.Parcel.Id))continue;
            var b=ordered.Where(p=>!processed.Contains(p.Parcel.Id)&&p.Parcel.Id!=a.Parcel.Id&&p.Front.A.DistanceTo(a.Front.B)<.015f).FirstOrDefault(p=>p.Front.RoadId==a.Front.RoadId&&p.Inward.Dot(a.Inward)>.9999f);
            if(b!=null)TryPair(a,b);
            if(i%32==0)await Yield(.2f,"协调共墙与通院小巷");
        }
        int attempted=0;
        foreach(var plot in ordered){
            if(processed.Contains(plot.Parcel.Id))continue;
            Fit? fit=null;BuildingAccess? connection=null;
            foreach(float scale in new[]{1f,.82f})foreach(float depth in new[]{12f,9f,6f})foreach(float setback in new[]{.05f,1.0f}){
                if(fit!=null)continue;
                var candidate=House(plot,depth,setback,o.PassageWidth,0,scale,false,nextId);if(candidate==null)continue;
                var start=plot.Front.A+plot.Tangent*(o.PassageWidth/2);float back=YardDepth(plot,start,setback+depth+o.PassageWidth/2+.3f);if(!float.IsFinite(back))continue;var end=start+plot.Inward*back;
                var corridor=ParcelGeometry.Union(new Paths64(Strip(start,end,o.PassageWidth).Concat(Strip(candidate.Building.StreetPoint,candidate.Building.Door,o.PassageWidth))));
                if(plot.Parcel.Yard?.Contains(end)!=true){Failed(plot.Parcel.Id,"通道未能到达后院");continue;}
                if(!AccessValid(corridor,plot.Parcel.Shape.CopyPaths(),new[]{candidate.Building}))candidate=candidate with{Building=candidate.Building with{RoofProjection=candidate.Building.Footprint}};
                if(!AccessValid(corridor,plot.Parcel.Shape.CopyPaths(),new[]{candidate.Building})){Failed(plot.Parcel.Id,"侧道越界或净宽不足");continue;}
                fit=candidate;connection=new(candidate.Building.Id,Array.AsReadOnly(new[]{plot.Parcel.Id}),plot.Front.RoadId,new ParcelRegion(corridor),candidate.Building.Door,candidate.Building.StreetPoint,end,"自有侧道");
            }
            if(fit!=null){buildings.Add(fit.Building);access.Add(connection!);nextId++;}
            else issues.Add(new($"P{plot.Parcel.Id}",plot.Parcel.Buildable.Center,failures.TryGetValue(plot.Parcel.Id,out var reasons)?string.Join("；",reasons.Order()):"没有合法主屋候选"));
            if(++attempted%32==0)await Yield(.55f,"生成临街主屋与基础");
        }
        var ordinary=buildings.Count;if(ordinary==0)throw new InvalidOperationException("没有普通宅地能够同时容纳主屋与合法通道");
        // Institution masses use their own reserved sites; temporarily remove those sites
        // from the blocker while retaining other institutions and defence reservations.
        if(o.Institutions&&input.Settlement.Town is {} town)foreach(var precinct in town.Precincts){
            var originalBlocked=blocked;blocked=ParcelGeometry.Difference(town.BuildingReserve.CopyPaths(),precinct.Shape.CopyPaths());
            var bounds=precinct.Shape.Bounds;var center=bounds.GetCenter();var towards=(center-precinct.Access).Normalized();
            if(Math.Abs(towards.X)>Math.Abs(towards.Y))towards=new Vector2(Math.Sign(towards.X),0);else towards=new Vector2(0,Math.Sign(towards.Y));
            var tangent=new Vector2(towards.Y,-towards.X);var allowed=precinct.Shape.CopyPaths();var group=new List<BuildingMass>();
            var localAccess=new List<BuildingAccess>();
            var court=ParcelGeometry.Rect(new Rect2(center-new Vector2(bounds.Size.X*.2f,bounds.Size.Y*.2f),bounds.Size*.4f));
            bool TryMass(Vector2 front,Vector2 t,Vector2 normal,float width,float depth,int floors,string role,bool ridge=true){
                foreach(float factor in new[]{1f,.85f,.7f,.55f}){
                    var shift=front+t*(width*(1-factor)/2);var corners=BuildingGeometry.Rectangle(shift,t,normal,width*factor,depth);
                    var door=(corners[0]+corners[1])/2;
                    var b=Make(nextId,corners,allowed,null,precinct.Id,precinct.Id,role,floors,town.Interior.Contains(center),ridge&&depth>=width*factor,false,door,precinct.Access);
                    if(b==null||group.Any(other=>!BuildingGeometry.Clear(b.Footprint.CopyPaths(),other.Footprint.CopyPaths())))continue;
                    group.Add(b);nextId++;return true;
                }return false;
            }
            if(precinct.Id=="church"){
                var front=center-tangent*7-towards*11;
                if(!TryMass(front,tangent,towards,14,22,3,"教堂主殿"))throw new InvalidOperationException("教堂院区主建筑落地失败");
                var main=group[0];if(!TryMass(main.Corners[1]+tangent*1.5f,tangent,towards,5,6,5,"教堂塔楼"))issues.Add(new(precinct.Id,center,"省略可选塔楼：占地或高差不满足"));
                court=ParcelGeometry.Difference(allowed,ParcelGeometry.Union(new Paths64(group.SelectMany(b=>b.Footprint.CopyPaths()))));
            }else{
                // A U-shaped ensemble; the access-facing side remains open.
                float width=Math.Abs(tangent.X)>.5f?bounds.Size.X:bounds.Size.Y;
                float depth=Math.Abs(towards.X)>.5f?bounds.Size.X:bounds.Size.Y;
                var front=center-tangent*(width*.35f)+towards*(depth*.28f);
                TryMass(front,tangent,towards,width*.7f,7,2,"院区主翼");
                TryMass(center-tangent*(width*.38f-7)+towards*(depth*.23f),-towards,-tangent,depth*.48f,7,2,"院区侧翼");
                TryMass(center+tangent*(width*.38f-7)-towards*(depth*.25f),towards,tangent,depth*.48f,7,2,"院区侧翼");
                if(group.Count==0)throw new InvalidOperationException("修道院无法生成合格建筑翼部");
                if(group.Any(b=>!BuildingGeometry.Clear(b.Footprint.CopyPaths(),court)))throw new InvalidOperationException("修道院建筑侵入院场");
            }
            var solid=ParcelGeometry.Union(new Paths64(group.SelectMany(b=>b.Footprint.CopyPaths())));
            // Connect doors and the fixed precinct anchor through free courtyard space.
            var whole=ParcelGeometry.Union(new Paths64(allowed.Concat(precinct.Approach.CopyPaths())));
            Paths64? CourtyardRoute(Vector2 goal){
                float half=o.PassageWidth/2;var entry=new Vector2(Math.Clamp(precinct.Access.X,bounds.Position.X,bounds.End.X),Math.Clamp(precinct.Access.Y,bounds.Position.Y,bounds.End.Y))+towards*(o.PassageWidth+1);
                var xs=new List<float>{entry.X,goal.X,center.X,bounds.Position.X+half+.1f,bounds.End.X-half-.1f};
                var zs=new List<float>{entry.Y,goal.Y,center.Y,bounds.Position.Y+half+.1f,bounds.End.Y-half-.1f};
                foreach(var body in group){var r=body.Footprint.Bounds;xs.Add(r.Position.X-half-.1f);xs.Add(r.End.X+half+.1f);zs.Add(r.Position.Y-half-.1f);zs.Add(r.End.Y+half+.1f);}
                xs=xs.Distinct().Order().ToList();zs=zs.Distinct().Order().ToList();int nx=xs.Count;
                int Index(Vector2 p)=>zs.IndexOf(p.Y)*nx+xs.IndexOf(p.X);Vector2 Position(int i)=>new(xs[i%nx],zs[i/nx]);
                var legal=new HashSet<int>();for(int z=0;z<zs.Count;z++)for(int x=0;x<nx;x++){var square=ParcelGeometry.Rect(new Rect2(new Vector2(xs[x]-half,zs[z]-half),Vector2.One*o.PassageWidth));if(BuildingGeometry.Fits(square,whole)&&BuildingGeometry.Clear(square,solid))legal.Add(z*nx+x);}
                int start=Index(entry),end=Index(goal);if(!legal.Contains(start)||!legal.Contains(end))return null;
                var q=new PriorityQueue<int,float>();var cost=new Dictionary<int,float>{{start,0}};var prev=new Dictionary<int,int>();q.Enqueue(start,0);
                while(q.TryDequeue(out int current,out float score)){
                    if(score>cost[current])continue;if(current==end)break;
                    foreach(var next in new[]{current%nx>0?current-1:-1,current%nx<nx-1?current+1:-1,current>=nx?current-nx:-1,current+nx<nx*zs.Count?current+nx:-1}){
                        if(next<0||!legal.Contains(next))continue;var strip=Strip(Position(current),Position(next),o.PassageWidth);if(!BuildingGeometry.Fits(strip,whole)||!BuildingGeometry.Clear(strip,solid))continue;
                        float value=cost[current]+Position(current).DistanceTo(Position(next));if(cost.TryGetValue(next,out float old)&&old<=value)continue;cost[next]=value;prev[next]=current;q.Enqueue(next,value);
                    }
                }
                if(!cost.ContainsKey(end))return null;var path=new List<Vector2>{goal};for(int i=end;i!=start;){i=prev[i];path.Add(Position(i));}path.Reverse();
                var result=Strip(precinct.Access,entry,o.PassageWidth);for(int i=1;i<path.Count;i++)result.AddRange(Strip(path[i-1],path[i],o.PassageWidth));return ParcelGeometry.Union(result);
            }
            foreach(var mass in group.ToArray()){
                var via=mass.Door-mass.Inward*(o.PassageWidth+1);var route=CourtyardRoute(via);
                var corridor=route==null?new Paths64():ParcelGeometry.Union(new Paths64(route.Concat(Strip(via,mass.Door,o.PassageWidth))));
                if(route==null||!BuildingGeometry.Fits(corridor,whole)||!BuildingGeometry.Clear(corridor,solid)){
                    if(mass.Program=="教堂塔楼"){group.Remove(mass);issues.Add(new(precinct.Id,mass.Footprint.Center,"省略可选塔楼：没有合法入口通路"));continue;}
                    throw new InvalidOperationException(precinct.Name+"入口路径受建筑或边界阻挡");
                }
                localAccess.Add(new(mass.Id,Array.AsReadOnly(Array.Empty<int>()),-1,new ParcelRegion(corridor),mass.Door,precinct.Access,center,"公共院区通道"));
            }
            if(precinct.Id=="church")court=ParcelGeometry.Difference(allowed,ParcelGeometry.Union(new Paths64(group.SelectMany(b=>b.Footprint.CopyPaths()))));
            buildings.AddRange(group);access.AddRange(localAccess);open.Add(new(precinct.Id,"院场",new ParcelRegion(court)));blocked=originalBlocked;
            await Yield(.8f,"检查公共院区体量与通道");
        }
        var shared=new List<SharedBuildingWall>();
        for(int i=0;i<buildings.Count;i++){
            if(i%64==0)await Yield(.9f,"整体碰撞与共享墙面检查");
            for(int j=i+1;j<buildings.Count;j++){
            var a=buildings[i];var b=buildings[j];
            if(a.RoofProjection.Bounds.Intersects(b.RoofProjection.Bounds,true)&&ParcelGeometry.Area(ParcelGeometry.Intersection(a.RoofProjection.CopyPaths(),b.RoofProjection.CopyPaths()))>.01)throw new InvalidOperationException($"建筑或屋顶 B{a.Id} 与 B{b.Id} 穿插");
            if(i>=ordinary||j>=ordinary||!a.Footprint.Bounds.Grow(.02f).Intersects(b.Footprint.Bounds,true))continue;
            for(int edge=0;edge<4;edge++)for(int other=0;other<4;other++){
                var p=a.Corners[edge];var q=a.Corners[(edge+1)%4];var r=b.Corners[other];var s=b.Corners[(other+1)%4];var dir=(q-p).Normalized();
                if(Math.Abs((s-r).Normalized().Dot(dir))<.9999f||BuildingGeometry.DistanceToSegment(r,p,q)>.02f&&BuildingGeometry.DistanceToSegment(s,p,q)>.02f)continue;
                if(Math.Abs((r-p).Cross(dir))>.015f||Math.Abs((s-p).Cross(dir))>.015f)continue;
                float start=Math.Max(0,Math.Min((r-p).Dot(dir),(s-p).Dot(dir))),end=Math.Min(p.DistanceTo(q),Math.Max((r-p).Dot(dir),(s-p).Dot(dir)));
                if(end-start>1)shared.Add(new(a.Id,b.Id,p+dir*start,p+dir*end,Math.Min(a.Id,b.Id)));
            }
        }
        }
        await Yield(1,"建筑占地、通院与共享边检查完成");
        return new(input,revision,o,buildings,access,shared,open,issues);
    }
}
