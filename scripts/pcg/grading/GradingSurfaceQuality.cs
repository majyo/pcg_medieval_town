using Godot;
using System;
using System.Linq;

namespace PcgCitySandbox.Pcg;

// Diagnose the published height function separately from the solver's stations.
// A feasible profile alone does not certify junction blending or bent cross-sections.
public sealed record GradingSurfaceQuality(int LongitudinalSamples,int CrossSectionSamples,
    float MaxLongitudinalGrade,float MaxGradeChangePer10m,float MaxCrossSectionResidual,
    int WorstGradeChangeRoad,float WorstGradeChangeStation,int WorstCrossSectionRoad,float WorstCrossSectionStation)
{
    public static GradingSurfaceQuality Measure(RoadGrading grading)
    {
        int longitudinal=0,sections=0,curveRoad=-1,crossRoad=-1;float maxGrade=0,maxChange=0,maxCross=0,curveAt=0,crossAt=0;
        foreach(var road in grading.Roads.Edges){
            var line=new Centerline(road);var profile=grading.Profile(road.Id)!;
            int count=Math.Max(2,(int)Math.Ceiling(line.Length/.5f));float step=line.Length/count;
            var heights=new double[count+1];
            for(int i=0;i<=count;i++){var point=line.PointAt(i*step);if(!grading.TryDesignHeight(point,out float h))throw new RoadGradingFailure(point,road.Id,"纵向复查点缺少设计高程");heights[i]=h;longitudinal++;}
            for(int i=1;i<=count;i++)maxGrade=Math.Max(maxGrade,(float)(Math.Abs(heights[i]-heights[i-1])/step));
            for(int i=1;i<count;i++){
                float change=(float)(Math.Abs(heights[i-1]-2*heights[i]+heights[i+1])/(step*step)*10);
                if(change>maxChange){maxChange=change;curveRoad=road.Id;curveAt=i*step;}
            }
            for(float station=2;station<line.Length-2;station+=2){var center=line.PointAt(station);if(!grading.IsPureSweep(center))continue;var normal=line.NormalAt(station);grading.TryDesignHeight(center,out float middle);
                foreach(float side in new[]{-.8f,-.4f,.4f,.8f}){float lateral=side*road.Width/2;var p=center+normal*lateral;
                    if(!grading.IsPureSweep(p)||!grading.TryDesignHeight(p,out float h))continue;sections++;
                    float expected=middle+profile.SectionOffset(lateral,grading.Options.Crossfall/100),error=Math.Abs(expected-h);
                    if(error>maxCross){maxCross=error;crossRoad=road.Id;crossAt=station;}
                }
            }
        }
        return new(longitudinal,sections,maxGrade,maxChange,maxCross,curveRoad,curveAt,crossRoad,crossAt);
    }
}
