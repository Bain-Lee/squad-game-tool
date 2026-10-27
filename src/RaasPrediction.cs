using System;
using System.Collections.Generic;
using System.Linq;

namespace SquadTools
{
    public sealed class RaasConstraint { public List<string> Ids; public int Step; }
    public sealed class RaasResult
    {
        public int Alive, Total;
        public readonly Dictionary<int,Dictionary<string,double>> Steps = new Dictionary<int,Dictionary<string,double>>();
        public double Chance(RaasPoint flag,int step)
        {
            Dictionary<string,double> values; if (!Steps.TryGetValue(step,out values)) return 0;
            double sum = 0, value; foreach (string id in flag.CandidateIds) if (values.TryGetValue(id,out value)) sum += value;
            return sum;
        }
    }
    // Route enumeration and equal-route/equal-candidate model adapted from SquadCalc (non-commercial).
    public sealed class RaasSolver
    {
        public const int MaximumRoutes = 2000;
        public readonly List<List<RaasCluster>> Routes = new List<List<RaasCluster>>();
        public string Start { get; private set; }
        public string End { get; private set; }
        public int StepCount { get; private set; }
        public RaasSolver(RaasLayer layer)
        {
            var incoming = new HashSet<string>(layer.Links.Select(l=>l.To));
            var outgoing = new HashSet<string>(layer.Links.Select(l=>l.From));
            var starts = outgoing.Where(n=>!incoming.Contains(n)).ToList(); var ends = incoming.Where(n=>!outgoing.Contains(n)).ToList();
            if (starts.Count != 1 || ends.Count != 1 || !layer.Mains.Any(m=>m.Id==starts[0]) || !layer.Mains.Any(m=>m.Id==ends[0])) throw new FormatException("RAAS 路线没有明确的双方主基地。");
            Start = starts[0]; End = ends[0];
            var clusters = layer.Clusters.ToDictionary(c=>c.Id);
            var adjacency = layer.Links.GroupBy(l=>l.From).ToDictionary(g=>g.Key,g=>g.Select(l=>l.To).ToList());
            Walk(Start,new List<string>{Start},adjacency,clusters);
            if (Routes.Count == 0) throw new FormatException("本图层没有可用 RAAS 路线。");
            StepCount = Routes.Max(r=>r.Count);
        }
        private void Walk(string node,List<string> trail,Dictionary<string,List<string>> adjacency,Dictionary<string,RaasCluster> clusters)
        {
            if (node == End) {
                if (Routes.Count >= MaximumRoutes) throw new FormatException("RAAS 路线超过计算上限，暂停概率显示。");
                var route = trail.Where(clusters.ContainsKey).Select(id=>clusters[id]).ToList();
                if (route.Count > 0) Routes.Add(route); return;
            }
            if (trail.Count > 128) throw new FormatException("RAAS 路线深度异常。");
            List<string> neighbors; if (!adjacency.TryGetValue(node,out neighbors)) return;
            foreach (string next in neighbors) {
                if (trail.Contains(next)) throw new FormatException("RAAS 路线包含循环。");
                trail.Add(next); Walk(next,trail,adjacency,clusters); trail.RemoveAt(trail.Count-1);
            }
        }
        public RaasResult Solve(IList<RaasConstraint> constraints,bool reverse)
        {
            if(constraints.GroupBy(c=>c.Step).Any(g=>g.Count()>1)) return new RaasResult{Alive=0,Total=Routes.Count};
            var survivors = new List<List<RaasCluster>>();
            foreach (var route in Routes) {
                bool possible = constraints.All(c => c.Step >= 1 && c.Step <= route.Count && route[reverse?route.Count-c.Step:c.Step-1].Ids.Any(c.Ids.Contains));
                if (possible) survivors.Add(route);
            }
            var result = new RaasResult { Alive = survivors.Count, Total = Routes.Count };
            if (result.Alive == 0) return result;
            foreach (var route in survivors) for (int i=0;i<route.Count;i++) {
                int step = reverse?route.Count-i:i+1;
                var constraint = constraints.FirstOrDefault(c=>c.Step==step);
                var ids = constraint == null ? route[i].Ids : route[i].Ids.Where(constraint.Ids.Contains).ToList();
                Dictionary<string,double> values;
                if (!result.Steps.TryGetValue(step,out values)) { values = new Dictionary<string,double>(); result.Steps[step] = values; }
                double share = 1.0/result.Alive/ids.Count;
                foreach (string id in ids) { double previous; values.TryGetValue(id,out previous); values[id] = previous+share; }
            }
            return result;
        }
    }
    public sealed class RaasSession
    {
        public readonly RaasLayer Layer;
        public readonly RaasSolver Solver;
        public readonly List<RaasPoint> Confirmed = new List<RaasPoint>();
        public readonly HashSet<RaasPoint> Automatic = new HashSet<RaasPoint>();
        public RaasPoint Main { get; private set; }
        public bool Reverse { get; private set; }
        public RaasResult Result { get; private set; }
        public int NextStep { get { return Confirmed.Count+1; } }
        public bool Complete { get { return Main != null && Result.Alive>0 && NextStep>Result.Steps.Keys.Max(); } }
        public RaasSession(RaasLayer layer) { Layer=layer; Solver=new RaasSolver(layer); Recalculate(); }
        private List<RaasConstraint> Constraints() { return Confirmed.Select((f,i)=>new RaasConstraint{Ids=f.CandidateIds,Step=i+1}).ToList(); }
        private void Recalculate() { Result=Solver.Solve(Constraints(),Reverse); }
        public void ChooseMain(RaasPoint main)
        {
            if (!Layer.Mains.Contains(main)) throw new ArgumentException("请选择本图层的主基地。");
            Main=main; Reverse=main.Id==Solver.End; Confirmed.Clear(); Automatic.Clear(); Recalculate();
        }
        public bool Confirm(RaasPoint flag)
        {
            if (Main==null || Complete || !Layer.Flags.Contains(flag) || Confirmed.Contains(flag) || Result.Chance(flag,NextStep)<=0) return false;
            var constraints=Constraints(); constraints.Add(new RaasConstraint{Ids=flag.CandidateIds,Step=NextStep});
            var next=Solver.Solve(constraints,Reverse); if(next.Alive==0) return false;
            Confirmed.Add(flag); Result=next; return true;
        }
        public RaasResult Preview(RaasPoint flag)
        {
            if (Main==null || Complete || flag==null || !Layer.Flags.Contains(flag) || Confirmed.Contains(flag) || Result.Chance(flag,NextStep)<=0) return null;
            var constraints=Constraints(); constraints.Add(new RaasConstraint{Ids=flag.CandidateIds,Step=NextStep});
            var preview=Solver.Solve(constraints,Reverse); return preview.Alive>0?preview:null;
        }
        public int AdvanceUnique()
        {
            int added=0;
            while(Main!=null && !Complete && added<Layer.Flags.Count) {
                var candidates=Layer.Flags.Where(f=>!Confirmed.Contains(f) && Result.Chance(f,NextStep)>0).Take(2).ToList();
                // A single physical point can contain several source candidate IDs.
                if(candidates.Count!=1 || Result.Chance(candidates[0],NextStep)<1-1e-9 || !Confirm(candidates[0])) break;
                Automatic.Add(candidates[0]); added++;
            }
            return added;
        }
        public void Undo() { if(Confirmed.Count>0) { Automatic.Remove(Confirmed[Confirmed.Count-1]); Confirmed.RemoveAt(Confirmed.Count-1); } Recalculate(); }
        public void Reset() { Confirmed.Clear(); Automatic.Clear(); Recalculate(); }
    }
    public sealed class RaasMarker
    {
        public RaasPoint Point;
        public string Label, Caption, Color;
        public string FactionId;
        public bool Clickable;
        public int ConfirmedStep;
        public bool SelectedMain;
        public bool IsPreview, Automatic;
    }
}
