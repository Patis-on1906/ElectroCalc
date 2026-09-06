using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Учебное определение входного сопротивления пассивной DC-сети.
    /// Сначала выполняются КЗ идеальных ветвей, затем последовательно применяются
    /// параллельные, последовательные и Y/Δ-преобразования. Если сеть не удаётся
    /// полностью свернуть, вызывающая сторона может использовать универсальный
    /// метод тестового источника.
    /// </summary>
    internal sealed class ResistanceReductionSolver
    {
        private const double Eps = 1e-10;
        private const int MaxDepth = 10;
        private const int MaxStates = 4000;

        private sealed class Edge
        {
            public int A;
            public int B;
            public double R;
            public string Expr = string.Empty;
        }

        private sealed class Net
        {
            public HashSet<int> Nodes { get; } = new();
            public List<Edge> Edges { get; } = new();
            public int A;
            public int B;
            public int NextNode;

            public Net Clone()
            {
                var c = new Net { A = A, B = B, NextNode = NextNode };
                foreach (int n in Nodes) c.Nodes.Add(n);
                foreach (var e in Edges)
                    c.Edges.Add(new Edge { A = e.A, B = e.B, R = e.R, Expr = e.Expr });
                return c;
            }
        }

        internal sealed class ReductionResult
        {
            public bool Success { get; init; }
            public double Resistance { get; init; }
            public string Log { get; init; } = string.Empty;
            public string FailureReason { get; init; } = string.Empty;
        }

        private int _states;
        private readonly HashSet<string> _visited = new();

        public ReductionResult TryReduce(CircuitGraph graph, CircuitNode terminalA, CircuitNode terminalB)
        {
            var build = BuildNetwork(graph, terminalA, terminalB);
            if (!build.ok)
                return new ReductionResult { FailureReason = build.message };

            if (build.net.A == build.net.B)
            {
                return new ReductionResult
                {
                    Success = true,
                    Resistance = 0.0,
                    Log = build.preamble + "\n  Клеммы A и B соединены идеальным КЗ.\n  Rэкв = 0 Ом"
                };
            }

            var steps = new List<string>();
            bool ok = ReduceRecursive(build.net, steps, 0, out double req);
            if (!ok)
            {
                return new ReductionResult
                {
                    Success = false,
                    Log = build.preamble + (steps.Count > 0 ? "\n" + string.Join("\n", steps) : string.Empty),
                    FailureReason = "Сеть не удалось полностью свести разрешёнными преобразованиями за ограниченное число шагов."
                };
            }

            var sb = new StringBuilder();
            sb.Append(build.preamble);
            if (steps.Count > 0) sb.AppendLine().Append(string.Join("\n", steps));
            sb.AppendLine();
            sb.AppendLine($"  Получено: Rэкв = {F(req)} Ом");
            return new ReductionResult { Success = true, Resistance = req, Log = sb.ToString() };
        }

        private static (bool ok, Net net, string preamble, string message) BuildNetwork(
            CircuitGraph graph, CircuitNode terminalA, CircuitNode terminalB)
        {
            var nodes = graph.Nodes.ToList();
            var index = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n.Id, x => x.i);
            var parent = Enumerable.Range(0, nodes.Count).ToArray();

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }
            void Union(int x, int y)
            {
                x = Find(x); y = Find(y);
                if (x != y) parent[y] = x;
            }

            var raw = new List<(int a, int b, double r, string expr)>();
            var pre = new StringBuilder();
            pre.AppendLine("  После удаления нагрузки деактивируем независимые источники:");
            pre.AppendLine("  E → внутреннее сопротивление (идеальный E → КЗ), J → разрыв; C в установившемся DC → разрыв, L → КЗ.");

            foreach (var branch in graph.Branches)
            {
                var model = DcBranchModel.Create(branch);
                if (model.Kind == DcBranchKind.Open || model.Kind == DcBranchKind.IdealCurrent)
                    continue;

                int a = index[branch.StartNode.Id];
                int b = index[branch.EndNode.Id];
                double r = model.Resistance;
                if (r <= Eps)
                {
                    Union(a, b);
                    continue;
                }

                string expr = BuildResistanceExpression(branch);
                raw.Add((a, b, r, expr));
            }

            var rootToNew = new Dictionary<int, int>();
            int GetNew(int old)
            {
                int root = Find(old);
                if (!rootToNew.TryGetValue(root, out int id))
                {
                    id = rootToNew.Count;
                    rootToNew[root] = id;
                }
                return id;
            }

            var net = new Net();
            foreach (int i in Enumerable.Range(0, nodes.Count)) net.Nodes.Add(GetNew(i));
            net.A = GetNew(index[terminalA.Id]);
            net.B = GetNew(index[terminalB.Id]);
            net.NextNode = rootToNew.Count;

            foreach (var e in raw)
            {
                int a = GetNew(e.a), b = GetNew(e.b);
                if (a == b) continue;
                net.Edges.Add(new Edge { A = a, B = b, R = e.r, Expr = e.expr });
            }

            RemoveIsolated(net);
            if (net.A != net.B && !AreConnected(net, net.A, net.B))
                return (false, net, pre.ToString(), "После деактивации источников между клеммами A и B нет проводящего пути (Rэкв = ∞)." );

            return (true, net, pre.ToString(), string.Empty);
        }

        private bool ReduceRecursive(Net net, List<string> steps, int depth, out double req)
        {
            req = double.NaN;
            if (++_states > MaxStates || depth > MaxDepth) return false;

            // Детерминированные преобразования всегда выгодны: сначала параллель, затем последовательное соединение.
            bool changed;
            do
            {
                changed = TryParallel(net, steps) || TrySeries(net, steps);
                RemoveIsolated(net);
                if (TryFinish(net, out req)) return true;
            } while (changed);

            string sig = Signature(net);
            if (!_visited.Add(sig)) return false;

            // Y -> Δ уменьшает число внутренних узлов, поэтому пробуем прежде Δ -> Y.
            foreach (int center in net.Nodes.Where(n => n != net.A && n != net.B).ToList())
            {
                if (!TryGetStar(net, center, out var star)) continue;
                var clone = net.Clone();
                var local = new List<string>();
                ApplyStarToDelta(clone, center, star, local);
                var childSteps = new List<string>(steps);
                childSteps.AddRange(local);
                if (ReduceRecursive(clone, childSteps, depth + 1, out req))
                {
                    steps.Clear(); steps.AddRange(childSteps); return true;
                }
            }

            // Δ -> Y может открыть новые последовательные/параллельные сокращения.
            foreach (var tri in FindTriangles(net))
            {
                var clone = net.Clone();
                var local = new List<string>();
                ApplyDeltaToStar(clone, tri, local);
                var childSteps = new List<string>(steps);
                childSteps.AddRange(local);
                if (ReduceRecursive(clone, childSteps, depth + 1, out req))
                {
                    steps.Clear(); steps.AddRange(childSteps); return true;
                }
            }

            return false;
        }

        private static bool TryFinish(Net net, out double req)
        {
            req = double.NaN;
            if (net.A == net.B) { req = 0; return true; }
            if (net.Edges.Count == 1)
            {
                var e = net.Edges[0];
                if ((e.A == net.A && e.B == net.B) || (e.A == net.B && e.B == net.A))
                { req = e.R; return true; }
            }
            return false;
        }

        private static bool TryParallel(Net net, List<string> steps)
        {
            var group = net.Edges
                .GroupBy(e => e.A < e.B ? (e.A, e.B) : (e.B, e.A))
                .FirstOrDefault(g => g.Count() >= 2);
            if (group == null) return false;

            var es = group.ToList();
            double inv = es.Sum(e => 1.0 / e.R);
            double r = 1.0 / inv;
            string expr = "(" + string.Join(" || ", es.Select(e => e.Expr)) + ")";
            string symbol = NextSymbol(steps);
            steps.Add($"  {symbol}: параллельное соединение {string.Join(", ", es.Select(e => e.Expr))}");
            steps.Add($"    1/{symbol} = {string.Join(" + ", es.Select(e => $"1/{e.Expr}"))}");
            steps.Add($"    {symbol} = {F(r)} Ом");
            foreach (var e in es) net.Edges.Remove(e);
            net.Edges.Add(new Edge { A = group.Key.Item1, B = group.Key.Item2, R = r, Expr = symbol });
            return true;
        }

        private static bool TrySeries(Net net, List<string> steps)
        {
            foreach (int n in net.Nodes.Where(n => n != net.A && n != net.B).ToList())
            {
                var inc = net.Edges.Where(e => e.A == n || e.B == n).ToList();
                if (inc.Count != 2) continue;
                var e1 = inc[0]; var e2 = inc[1];
                int x = e1.A == n ? e1.B : e1.A;
                int y = e2.A == n ? e2.B : e2.A;
                if (x == y) continue;
                double r = e1.R + e2.R;
                string symbol = NextSymbol(steps);
                steps.Add($"  {symbol}: последовательное соединение {e1.Expr} и {e2.Expr}");
                steps.Add($"    {symbol} = {e1.Expr} + {e2.Expr} = {F(r)} Ом");
                net.Edges.Remove(e1); net.Edges.Remove(e2); net.Nodes.Remove(n);
                net.Edges.Add(new Edge { A = x, B = y, R = r, Expr = symbol });
                return true;
            }
            return false;
        }

        private sealed record StarData(Edge E1, Edge E2, Edge E3, int N1, int N2, int N3);

        private static bool TryGetStar(Net net, int center, out StarData star)
        {
            var inc = net.Edges.Where(e => e.A == center || e.B == center).ToList();
            if (inc.Count != 3) { star = null!; return false; }
            var ns = inc.Select(e => e.A == center ? e.B : e.A).ToList();
            if (ns.Distinct().Count() != 3) { star = null!; return false; }
            star = new StarData(inc[0], inc[1], inc[2], ns[0], ns[1], ns[2]);
            return true;
        }

        private static void ApplyStarToDelta(Net net, int center, StarData s, List<string> steps)
        {
            double r1=s.E1.R, r2=s.E2.R, r3=s.E3.R;
            double r12 = r1 + r2 + r1*r2/r3;
            double r23 = r2 + r3 + r2*r3/r1;
            double r31 = r3 + r1 + r3*r1/r2;
            string a=NextSymbol(steps), b=NextSymbol(steps,1), c=NextSymbol(steps,2);
            steps.Add($"  Y→Δ в узле {center}: ветви {s.E1.Expr}, {s.E2.Expr}, {s.E3.Expr}");
            steps.Add($"    {a} = {s.E1.Expr}+{s.E2.Expr}+{s.E1.Expr}·{s.E2.Expr}/{s.E3.Expr} = {F(r12)} Ом");
            steps.Add($"    {b} = {s.E2.Expr}+{s.E3.Expr}+{s.E2.Expr}·{s.E3.Expr}/{s.E1.Expr} = {F(r23)} Ом");
            steps.Add($"    {c} = {s.E3.Expr}+{s.E1.Expr}+{s.E3.Expr}·{s.E1.Expr}/{s.E2.Expr} = {F(r31)} Ом");
            net.Edges.Remove(s.E1); net.Edges.Remove(s.E2); net.Edges.Remove(s.E3); net.Nodes.Remove(center);
            net.Edges.Add(new Edge{A=s.N1,B=s.N2,R=r12,Expr=a});
            net.Edges.Add(new Edge{A=s.N2,B=s.N3,R=r23,Expr=b});
            net.Edges.Add(new Edge{A=s.N3,B=s.N1,R=r31,Expr=c});
        }

        private sealed record Triangle(int A, int B, int C, Edge AB, Edge BC, Edge CA);

        private static IEnumerable<Triangle> FindTriangles(Net net)
        {
            var ns = net.Nodes.OrderBy(x=>x).ToList();
            for(int i=0;i<ns.Count;i++) for(int j=i+1;j<ns.Count;j++) for(int k=j+1;k<ns.Count;k++)
            {
                var ab=FindEdge(net,ns[i],ns[j]); var bc=FindEdge(net,ns[j],ns[k]); var ca=FindEdge(net,ns[k],ns[i]);
                if(ab!=null && bc!=null && ca!=null) yield return new Triangle(ns[i],ns[j],ns[k],ab,bc,ca);
            }
        }

        private static Edge? FindEdge(Net net,int a,int b) => net.Edges.FirstOrDefault(e => (e.A==a&&e.B==b)||(e.A==b&&e.B==a));

        private static void ApplyDeltaToStar(Net net, Triangle t, List<string> steps)
        {
            double sum=t.AB.R+t.BC.R+t.CA.R;
            double ra=t.AB.R*t.CA.R/sum, rb=t.AB.R*t.BC.R/sum, rc=t.BC.R*t.CA.R/sum;
            int center=net.NextNode++; net.Nodes.Add(center);
            string a=NextSymbol(steps), b=NextSymbol(steps,1), c=NextSymbol(steps,2);
            steps.Add($"  Δ→Y для треугольника ({t.A}, {t.B}, {t.C}): {t.AB.Expr}, {t.BC.Expr}, {t.CA.Expr}");
            steps.Add($"    {a} = {t.AB.Expr}·{t.CA.Expr}/({t.AB.Expr}+{t.BC.Expr}+{t.CA.Expr}) = {F(ra)} Ом");
            steps.Add($"    {b} = {t.AB.Expr}·{t.BC.Expr}/({t.AB.Expr}+{t.BC.Expr}+{t.CA.Expr}) = {F(rb)} Ом");
            steps.Add($"    {c} = {t.BC.Expr}·{t.CA.Expr}/({t.AB.Expr}+{t.BC.Expr}+{t.CA.Expr}) = {F(rc)} Ом");
            net.Edges.Remove(t.AB); net.Edges.Remove(t.BC); net.Edges.Remove(t.CA);
            net.Edges.Add(new Edge{A=t.A,B=center,R=ra,Expr=a});
            net.Edges.Add(new Edge{A=t.B,B=center,R=rb,Expr=b});
            net.Edges.Add(new Edge{A=t.C,B=center,R=rc,Expr=c});
        }

        private static string BuildResistanceExpression(CircuitBranch branch)
        {
            var names = new List<string>();
            foreach (var e in branch.Elements)
            {
                switch (e.Type)
                {
                    case ElementType.Resistor when e.Value > Eps: names.Add(e.Name); break;
                    case ElementType.VoltageSource when e.InternalResistance > Eps: names.Add($"r_{e.Name}"); break;
                    case ElementType.CurrentSource when e.InternalResistance > Eps: names.Add($"r_{e.Name}"); break;
                }
            }
            return names.Count switch { 0 => $"R({branch.ElementNames})", 1 => names[0], _ => "(" + string.Join(" + ", names) + ")" };
        }

        private static void RemoveIsolated(Net net)
        {
            var used = net.Edges.SelectMany(e => new[]{e.A,e.B}).ToHashSet();
            net.Nodes.RemoveWhere(n => n != net.A && n != net.B && !used.Contains(n));
        }

        private static bool AreConnected(Net net,int a,int b)
        {
            var seen=new HashSet<int>{a}; var q=new Queue<int>(); q.Enqueue(a);
            while(q.Count>0){int n=q.Dequeue(); foreach(var e in net.Edges.Where(e=>e.A==n||e.B==n)){int o=e.A==n?e.B:e.A;if(seen.Add(o))q.Enqueue(o);}}
            return seen.Contains(b);
        }

        private static string Signature(Net net)
        {
            var es=net.Edges.Select(e=>($"{Math.Min(e.A,e.B)}-{Math.Max(e.A,e.B)}:{Math.Round(e.R,10):G17}" )).OrderBy(x=>x);
            return $"A{net.A}B{net.B}|"+string.Join("|",es);
        }

        private static string NextSymbol(List<string> steps,int offset=0)
        {
            int count=steps.Count(s=>s.TrimStart().StartsWith("Rэкв",StringComparison.Ordinal));
            // Используем номер строки-преобразования как устойчивую уникальную метку.
            return $"Rэкв{count + 1 + offset}";
        }

        private static string F(double x) => x.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');
    }
}
