using System;
using System.Collections.Generic;
using System.Linq;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public static class CycleFinder
    {
        // Iterative Tarjan SCC. Returns every component with more than one node, nodes and components in ordinal order.
        public static List<List<string>> Find(IReadOnlyDictionary<string, HashSet<string>> edges)
        {
            var nodes = edges.Keys.Concat(edges.Values.SelectMany(v => v)).Distinct().OrderBy(n => n, StringComparer.Ordinal);
            var index = new Dictionary<string, int>();
            var low = new Dictionary<string, int>();
            var stack = new Stack<string>();
            var onStack = new HashSet<string>();
            var work = new Stack<(string node, IEnumerator<string> next)>();
            var result = new List<List<string>>();

            foreach (var root in nodes)
            {
                if (index.ContainsKey(root)) continue;
                Visit(root);
                while (work.Count > 0)
                {
                    var (v, next) = work.Peek();
                    if (next.MoveNext())
                    {
                        var w = next.Current;
                        if (!index.ContainsKey(w)) Visit(w);
                        else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                        continue;
                    }
                    work.Pop();
                    if (work.Count > 0)
                    {
                        var u = work.Peek().node;
                        low[u] = Math.Min(low[u], low[v]);
                    }
                    if (low[v] != index[v]) continue;
                    var scc = new List<string>();
                    string x;
                    do
                    {
                        x = stack.Pop();
                        onStack.Remove(x);
                        scc.Add(x);
                    } while (x != v);
                    if (scc.Count < 2) continue;
                    scc.Sort(StringComparer.Ordinal);
                    result.Add(scc);
                }
            }
            result.Sort((a, b) => string.CompareOrdinal(a[0], b[0]));
            return result;

            void Visit(string n)
            {
                var i = index.Count;
                index[n] = low[n] = i;
                stack.Push(n);
                onStack.Add(n);
                var targets = edges.TryGetValue(n, out var t) ? t : new HashSet<string>();
                work.Push((n, targets.Where(m => m != n).OrderBy(m => m, StringComparer.Ordinal).GetEnumerator()));
            }
        }
    }
}
