using System;
using System.Collections.Generic;
using System.Linq;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>Ориентированная ветвь выбранного замкнутого контура.</summary>
    public sealed class ContourBranch
    {
        public CircuitBranch Branch { get; }
        public int Direction { get; }

        public ContourBranch(CircuitBranch branch, int direction)
        {
            Branch = branch;
            Direction = direction >= 0 ? 1 : -1;
        }
    }

    /// <summary>
    /// Замкнутый контур схемы. Direction=+1 означает обход ветви StartNode→EndNode.
    /// </summary>
    public sealed class CircuitContour
    {
        public int Number { get; internal set; }
        public List<ContourBranch> Branches { get; } = new();

        public override string ToString()
        {
            string path = string.Join("; ", Branches.Select(x =>
                $"{(x.Direction > 0 ? "+" : "−")}{x.Branch}"));
            return $"Контур {Number}: {path}";
        }
    }

    /// <summary>
    /// Поиск простых замкнутых контуров небольших учебных схем.
    /// Контуры, отличающиеся только начальной точкой или направлением обхода,
    /// считаются одинаковыми.
    /// </summary>
    public static class CircuitContourFinder
    {
        private const int MaxContours = 100;

        public static List<CircuitContour> FindAll(CircuitGraph graph)
        {
            var nodes = graph.Nodes;
            var branches = graph.Branches;
            var branchIndex = branches.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
            var nodeIndex = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
            var adjacency = nodes.ToDictionary(n => n, _ => new List<CircuitBranch>());
            foreach (var b in branches)
            {
                adjacency[b.StartNode].Add(b);
                adjacency[b.EndNode].Add(b);
            }

            var signatures = new HashSet<string>();
            var result = new List<CircuitContour>();

            foreach (var start in nodes)
            {
                if (result.Count >= MaxContours) break;
                var visitedNodes = new HashSet<CircuitNode> { start };
                var usedBranches = new HashSet<CircuitBranch>();
                var path = new List<ContourBranch>();

                void Dfs(CircuitNode current)
                {
                    if (result.Count >= MaxContours) return;
                    foreach (var branch in adjacency[current])
                    {
                        if (usedBranches.Contains(branch)) continue;
                        var next = branch.StartNode == current ? branch.EndNode : branch.StartNode;
                        int dir = branch.StartNode == current ? +1 : -1;

                        if (next == start)
                        {
                            // Две параллельные ветви уже образуют допустимый контур.
                            if (path.Count < 1) continue;
                            var cycleBranches = path.Select(x => x.Branch).Append(branch).ToList();
                            string signature = string.Join(",", cycleBranches
                                .Select(b => branchIndex[b])
                                .OrderBy(i => i));
                            if (!signatures.Add(signature)) continue;

                            // start должен быть узлом с минимальным индексом контура:
                            // это резко сокращает число дублирующих обходов ещё до signature.
                            var cycleNodes = new HashSet<CircuitNode> { start, current };
                            foreach (var item in path)
                            {
                                cycleNodes.Add(item.Branch.StartNode);
                                cycleNodes.Add(item.Branch.EndNode);
                            }
                            if (cycleNodes.Min(n => nodeIndex[n]) != nodeIndex[start])
                            {
                                signatures.Remove(signature);
                                continue;
                            }

                            var contour = new CircuitContour();
                            contour.Branches.AddRange(path.Select(x => new ContourBranch(x.Branch, x.Direction)));
                            contour.Branches.Add(new ContourBranch(branch, dir));
                            result.Add(contour);
                            if (result.Count >= MaxContours) return;
                            continue;
                        }

                        if (visitedNodes.Contains(next)) continue;
                        // Начальный узел должен быть минимальным в цикле.
                        if (nodeIndex[next] < nodeIndex[start]) continue;

                        visitedNodes.Add(next);
                        usedBranches.Add(branch);
                        path.Add(new ContourBranch(branch, dir));
                        Dfs(next);
                        path.RemoveAt(path.Count - 1);
                        usedBranches.Remove(branch);
                        visitedNodes.Remove(next);
                    }
                }

                Dfs(start);
            }

            // Стабильный порядок: сначала короткие контуры, затем по номерам ветвей.
            result = result
                .OrderBy(c => c.Branches.Count)
                .ThenBy(c => string.Join(",", c.Branches.Select(x => branchIndex[x.Branch]).OrderBy(i => i)))
                .ToList();
            for (int i = 0; i < result.Count; i++) result[i].Number = i + 1;
            return result;
        }
    }
}
