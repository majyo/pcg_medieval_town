using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcgCitySandbox.Pcg;

internal sealed record VerticalProblem(RoadEdge Road,Centerline Line,RoadCrossSection Section,double[] Ground,
    double[] Target,double[] Low,double[] High,List<(int,int)> StraightRuns,double[] PlazaLow,double[] PlazaHigh);

internal sealed record JointPlaza(GradePlane Plane,Vector2[] Points,double[] Low,double[] High,float MaxSlope);

// One convex least-squares problem for the entire network. Adjacent profiles share
// endpoint variables, rather than pinning each road to independently estimated nodes.
// Slopes, grade changes, earthwork bounds and gate straightness are never relaxed.
internal static class JointVerticalSolver
{
    private readonly record struct Row(int A,int B,int C,double U,double V,double W,double Low,double High,int Road,int Station,int D=-1,double Q=0)
    {
        public double Dot(double[] x)=>U*x[A]+(B<0?0:V*x[B])+(C<0?0:W*x[C])+(D<0?0:Q*x[D]);
        public void Add(double[] x,double value){x[A]+=U*value;if(B>=0)x[B]+=V*value;if(C>=0)x[C]+=W*value;if(D>=0)x[D]+=Q*value;}
        public double NormSquared=>U*U+V*V+W*W+Q*Q;
        public double Scale=>Math.Max(Math.Abs(U),Math.Max(Math.Abs(V),Math.Max(Math.Abs(W),Math.Abs(Q))));
        public IEnumerable<(int Index,double Value)> Terms(){yield return(A,U);if(B>=0)yield return(B,V);if(C>=0)yield return(C,W);if(D>=0)yield return(D,Q);}
    }

    // Eliminate interior stations first. Each road has a pentadiagonal interior;
    // only the small network-node block becomes dense. Factorization is reused
    // for all ADMM iterations, including both triangular solves.
    private sealed class Factor
    {
        private readonly double[] _diagonal;
        private readonly (int Index,double Value)[][] _columns;
        public Factor(int count,List<Row> rows,CancellationToken token,double diagonalWeight=1)
        {
            var upper=Enumerable.Range(0,count).Select(i=>new Dictionary<int,double>{{i,diagonalWeight}}).ToArray();
            foreach(var row in rows){var terms=row.Terms().ToArray();for(int a=0;a<terms.Length;a++)for(int b=a;b<terms.Length;b++){
                int i=Math.Min(terms[a].Index,terms[b].Index),j=Math.Max(terms[a].Index,terms[b].Index);
                upper[i][j]=upper[i].GetValueOrDefault(j)+terms[a].Value*terms[b].Value;
            }}
            _diagonal=new double[count];_columns=new (int,double)[count][];
            for(int k=0;k<count;k++){
                if(k%128==0)token.ThrowIfCancellationRequested();double pivot=upper[k][k];
                if(!(pivot>0))throw new InvalidOperationException("竖向联合求解矩阵非正定");
                double diagonal=_diagonal[k]=Math.Sqrt(pivot);
                var entries=upper[k].Where(p=>p.Key>k).OrderBy(p=>p.Key).Select(p=>(Index:p.Key,Value:p.Value/diagonal)).ToArray();_columns[k]=entries;
                for(int i=0;i<entries.Length;i++)for(int j=i;j<entries.Length;j++){
                    var a=entries[i];var b=entries[j];upper[a.Index][b.Index]=upper[a.Index].GetValueOrDefault(b.Index)-a.Value*b.Value;
                }
                upper[k].Clear();
            }
        }
        public void Solve(double[] rhs,double[] x)
        {
            Array.Copy(rhs,x,rhs.Length);
            for(int k=0;k<x.Length;k++){x[k]/=_diagonal[k];foreach(var (index,value) in _columns[k])x[index]-=value*x[k];}
            for(int k=x.Length-1;k>=0;k--){foreach(var (index,value) in _columns[k])x[k]-=value*x[index];x[k]/=_diagonal[k];}
        }
    }

    public static (Dictionary<int,double[]> Profiles,float PlazaOffset,Vector2 PlazaGradientOffset) Solve(IReadOnlyList<VerticalProblem> problems,Dictionary<int,float> nodeHeights,
        HashSet<int> fixedNodes,float maxGrade,float gradeChange,(double Low,double High) plazaBounds,CancellationToken token,JointPlaza? plazaModel=null)
    {
        int count=0;var indices=new Dictionary<int,int[]>();
        foreach(var p in problems){var ids=new int[p.Target.Length];for(int i=1;i<ids.Length-1;i++)ids[i]=count++;indices[p.Road.Id]=ids;}
        var nodeIds=nodeHeights.Keys.OrderBy(i=>i).ToDictionary(id=>id,_=>count++);int plazaId=count++,plazaX=count++,plazaZ=count++;const double planeScale=32;
        var target=new double[count];var weights=new int[count];var low=Enumerable.Repeat(double.NegativeInfinity,count).ToArray();var high=Enumerable.Repeat(double.PositiveInfinity,count).ToArray();
        var owners=new (int Road,int Station)[count];var rows=new List<Row>();
        foreach(var p in problems){var ids=indices[p.Road.Id];ids[0]=nodeIds[p.Road.From];ids[^1]=nodeIds[p.Road.To];
            for(int i=0;i<ids.Length;i++){int id=ids[i];target[id]+=p.Target[i];weights[id]++;low[id]=Math.Max(low[id],p.Low[i]);high[id]=Math.Min(high[id],p.High[i]);owners[id]=(p.Road.Id,i);}
            for(int i=0;i<ids.Length;i++)if(double.IsFinite(p.PlazaLow[i])){
                var point=p.Line.PointAt(i*p.Line.Length/(ids.Length-1));var delta=plazaModel==null?Vector2.Zero:point-plazaModel.Plane.Origin;
                rows.Add(new(ids[i],plazaId,plazaX,100,-100,-100*delta.X/planeScale,p.PlazaLow[i]*100,p.PlazaHigh[i]*100,p.Road.Id,i,plazaZ,-100*delta.Y/planeScale));}
            double step=p.Line.Length/(ids.Length-1);const double gradeScale=100,curveScale=100;double gradeLimit=maxGrade*step*gradeScale,curveLimit=gradeChange/100/10*step*step*curveScale;
            for(int i=1;i<ids.Length;i++)rows.Add(new(ids[i-1],ids[i],-1,-gradeScale,gradeScale,0,-gradeLimit,gradeLimit,p.Road.Id,i));
            for(int i=1;i<ids.Length-1;i++){bool straight=p.StraightRuns.Any(r=>i>r.Item1&&i<r.Item2);double limit=straight?0:curveLimit;
                rows.Add(new(ids[i-1],ids[i],ids[i+1],curveScale,-2*curveScale,curveScale,-limit,limit,p.Road.Id,i));}
            Dictionary<int,double> Slope(int i){foreach(var run in p.StraightRuns)if(i>=run.Item1&&i<=run.Item2)return new(){{ids[run.Item1],-1/((run.Item2-run.Item1)*step)},{ids[run.Item2],1/((run.Item2-run.Item1)*step)}};
                if(i==0)return new(){{ids[0],-1/step},{ids[1],1/step}};if(i==ids.Length-1)return new(){{ids[^2],-1/step},{ids[^1],1/step}};return new(){{ids[i-1],-1/(2*step)},{ids[i+1],1/(2*step)}};}
            void CurveRow(int station,double startSlope,double endSlope,double direction){var coefficients=new Dictionary<int,double>();void Add(int id,double coefficient){coefficients[id]=coefficients.GetValueOrDefault(id)+coefficient;}
                Add(ids[station],-6*direction/(step*step));Add(ids[station+1],6*direction/(step*step));
                foreach(var item in Slope(station))Add(item.Key,startSlope*item.Value/step);foreach(var item in Slope(station+1))Add(item.Key,endSlope*item.Value/step);
                var terms=coefficients.Where(k=>Math.Abs(k.Value)>1e-12).OrderBy(k=>k.Key).ToArray();if(terms.Length==0)return;if(terms.Length>4)throw new InvalidOperationException("竖曲线约束项超过四项");
                int Index(int i)=>i<terms.Length?terms[i].Key:-1;double Coefficient(int i)=>i<terms.Length?100*terms[i].Value:0;
                double bound=gradeChange/100/10*100;rows.Add(new(Index(0),Index(1),Index(2),Coefficient(0),Coefficient(1),Coefficient(2),-bound,bound,p.Road.Id,station,Index(3),Coefficient(3)));}
            for(int i=0;i+1<ids.Length;i++){CurveRow(i,-4,-2,1);CurveRow(i,2,4,-1);}
            for(int i=0;i+1<ids.Length;i++){
                var coefficients=new Dictionary<int,double>{{ids[i],-3/step},{ids[i+1],3/step}};
                foreach(var slope in new[]{Slope(i),Slope(i+1)})foreach(var item in slope)coefficients[item.Key]=coefficients.GetValueOrDefault(item.Key)-item.Value;
                var terms=coefficients.Where(t=>Math.Abs(t.Value)>1e-12).OrderBy(t=>t.Key).ToArray();if(terms.Length>4)throw new InvalidOperationException("竖曲线导数约束项超过四项");
                int Index(int k)=>k<terms.Length?terms[k].Key:-1;double Value(int k)=>k<terms.Length?100*terms[k].Value:0;
                rows.Add(new(Index(0),Index(1),Index(2),Value(0),Value(1),Value(2),-maxGrade*100,maxGrade*100,p.Road.Id,i,Index(3),Value(3)));
            }

        }
        low[plazaId]=plazaModel==null?plazaBounds.Low:-100;high[plazaId]=plazaModel==null?plazaBounds.High:100;owners[plazaId]=(problems[0].Road.Id,0);
        low[plazaX]=low[plazaZ]=plazaModel==null?0:-10;high[plazaX]=high[plazaZ]=plazaModel==null?0:10;owners[plazaX]=owners[plazaZ]=owners[plazaId];
        if(plazaModel!=null){
            for(int i=0;i<plazaModel.Points.Length;i++){var delta=plazaModel.Points[i]-plazaModel.Plane.Origin;rows.Add(new(plazaId,plazaX,plazaZ,100,100*delta.X/planeScale,100*delta.Y/planeScale,plazaModel.Low[i]*100,plazaModel.High[i]*100,problems[0].Road.Id,0));}
            for(int side=0;side<32;side++){double angle=(side+.5)*Math.PI*2/32,dx=Math.Cos(angle),dz=Math.Sin(angle),projection=dx*plazaModel.Plane.Gradient.X+dz*plazaModel.Plane.Gradient.Y;
                double bound=plazaModel.MaxSlope*Math.Cos(Math.PI/32);rows.Add(new(plazaX,plazaZ,-1,100*dx/planeScale,100*dz/planeScale,0,(-bound-projection)*100,(bound-projection)*100,problems[0].Road.Id,0));}
        }
        foreach(var (node,id) in nodeIds)if(fixedNodes.Contains(node)){
            double h=nodeHeights[node];if(h<low[id]-.0001||h>high[id]+.0001)Fail(owners[id],"固定节点与挖填范围冲突");low[id]=high[id]=h;
        }
        for(int i=0;i<count;i++){
            if(low[i]>high[i]+1e-7)Fail(owners[i],"相连道路没有公共高程范围");
            target[i]=weights[i]>0?target[i]/weights[i]:0;
            rows.Add(new(i,-1,-1,100,0,0,low[i]*100,high[i]*100,owners[i].Road,owners[i].Station));
        }
        void Fail((int Road,int Station) at,string reason){var p=problems.First(p=>p.Road.Id==at.Road);throw new RoadGradingFailure(p.Line.PointAt(at.Station*p.Line.Length/(p.Target.Length-1)),at.Road,reason);}
        if(OS.GetCmdlineUserArgs().Contains("--dump-joint-model")){
            System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/joint_vertical_model.json"),System.Text.Json.JsonSerializer.Serialize(new{count,target,rows,plaza=plazaModel==null?null:new{origin=new[]{plazaModel.Plane.Origin.X,plazaModel.Plane.Origin.Y},gradient=new[]{plazaModel.Plane.Gradient.X,plazaModel.Plane.Gradient.Y},plazaModel.Plane.Height},profiles=problems.Select(p=>new{p.Road.Id,p.Road.From,p.Road.To,p.Line.Length,points=p.Line.P.Select(v=>new[]{v.X,v.Y}),samples=Enumerable.Range(0,p.Target.Length).Select(i=>{var q=p.Line.PointAt(i*p.Line.Length/(p.Target.Length-1));return new[]{q.X,q.Y};}),plazaLow=p.PlazaLow.Select(v=>double.IsFinite(v)?(double?)v:null),plazaHigh=p.PlazaHigh.Select(v=>double.IsFinite(v)?(double?)v:null),p.StraightRuns})}));
            throw new InvalidOperationException("JOINT_MODEL_DUMPED: diagnostic mode does not publish a result");
        }
        var factor=new Factor(count,rows,token);var x=target.Select((h,i)=>Math.Clamp(h,low[i],high[i])).ToArray();
        var z=rows.Select(r=>Math.Clamp(r.Dot(x),r.Low,r.High)).ToArray();var dual=new double[rows.Count];var rhs=new double[count];
        double worst=double.PositiveInfinity;int offender=0,iteration=0;
        for(;iteration<24000;iteration++){
            if(iteration%32==0)token.ThrowIfCancellationRequested();Array.Copy(target,rhs,count);
            for(int i=0;i<rows.Count;i++)rows[i].Add(rhs,z[i]-dual[i]);factor.Solve(rhs,x);worst=0;
            for(int i=0;i<rows.Count;i++){
                var row=rows[i];double value=row.Dot(x),projected=Math.Clamp(value+dual[i],row.Low,row.High);dual[i]+=value-projected;z[i]=projected;
                double violation=Math.Max(row.Low-value,value-row.High)/row.Scale;if(violation>worst){worst=violation;offender=i;}
            }
            if(iteration>40&&worst<2e-7)break;
        }
        // Semismooth Newton feasibility polish. Solve the active violated slabs
        // together; line search uses the original constraints, never enlarged limits.
        double Score(double[] values){double score=0;foreach(var row in rows){double v=row.Dot(values);double r=v-Math.Clamp(v,row.Low,row.High);score+=r*r;}return score;}
        for(int polish=0;worst>=2e-7&&polish<200;polish++){
            token.ThrowIfCancellationRequested();const double damping=.01;var active=new List<Row>();var b=new double[count];
            for(int i=0;i<count;i++)b[i]=damping*x[i];
            foreach(var row in rows){double value=row.Dot(x);if(value<row.Low||value>row.High||row.Low==row.High){active.Add(row);row.Add(b,Math.Clamp(value,row.Low,row.High));}}
            var candidate=new double[count];new Factor(count,active,token,damping).Solve(b,candidate);double before=Score(x);bool accepted=false;
            for(int search=0;search<24;search++){double alpha=Math.Pow(.5,search);var trial=new double[count];for(int i=0;i<count;i++)trial[i]=x[i]+alpha*(candidate[i]-x[i]);if(Score(trial)<before){x=trial;accepted=true;break;}}
            if(!accepted)break;worst=0;for(int i=0;i<rows.Count;i++){var row=rows[i];double v=row.Dot(x),error=Math.Max(row.Low-v,v-row.High)/row.Scale;if(error>worst){worst=error;offender=i;}}
        }
        if(worst>=2e-7){System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/joint_vertical_failure.json"),System.Text.Json.JsonSerializer.Serialize(new{worst,iteration,rows=rows.Select((r,i)=>new{r.Road,r.Station,r.Low,r.High,value=r.Dot(x),violation=Math.Max(r.Low-r.Dot(x),r.Dot(x)-r.High)/r.Scale}).OrderByDescending(r=>r.violation).Take(12),profiles=problems.Select(p=>new{p.Road.Id,p.Line.Length,ground=p.Ground,target=p.Target,lo=p.Low,hi=p.High,solution=indices[p.Road.Id].Select(i=>x[i]).ToArray()})},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Fail((rows[offender].Road,rows[offender].Station),$"严格竖向联合求解未达到约束容差（残差 {worst:0.000000} m，未放宽坡度变化）");}
        foreach(var (node,id) in nodeIds)nodeHeights[node]=(float)x[id];
        return (problems.ToDictionary(p=>p.Road.Id,p=>indices[p.Road.Id].Select(i=>x[i]).ToArray()),(float)x[plazaId],new Vector2((float)(x[plazaX]/planeScale),(float)(x[plazaZ]/planeScale)));
    }
}
