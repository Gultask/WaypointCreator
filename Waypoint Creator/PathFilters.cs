using System;
using System.Collections.Generic;
using System.Linq;

namespace Frm_waypoint
{
    /// <summary>
    /// The path mining heuristics, ported out of scripts/mine-paths.sql and chain-paths.py so
    /// the viewer can show what the miner would keep rather than making up its own rules.
    ///
    /// Drawing every point of every capture of a busy entry produces a web, not a path, and the
    /// reason is not that there is too much data - it is that most of it was never authored.
    /// Three rules, in the order the miner applies them, take it apart:
    ///
    ///   0. A multi point move packet is the SERVER'S OWN computed route - the navmesh corridor
    ///      it solved and smoothed. Keep the destination of each order and throw the interior
    ///      away. Flight is the exception: an airborne creature is not pathfound, so its spline
    ///      is the authored route.
    ///   1. A position hit by two separate move orders is authored. Random movement rolls a
    ///      fresh float every time and never lands on the same centimetre twice, so recurrence
    ///      IS the signal. This is the rule that turns the web back into a path.
    ///   3. The same, one level up: an ordered PAIR of positions walked twice is a route.
    ///
    /// Then Chain walks what is left into ordered routes, which is what turns dozens of partial
    /// captures of one creature into the single path they are all fragments of.
    ///
    /// Phase 1b separates genuinely stacked X,Y by Z, which a top down view has no use for.
    /// Phase 4 promotes the rest of a walk that already proved itself and needs per capture
    /// radius statistics, so it stays in the miner for now.
    /// </summary>
    internal static class PathFilters
    {
        /// <summary>A hop longer than this is a teleport or a bad join, not a route step.
        /// chain-paths.py calls it MAX_GAP_YD and cuts the chain there.</summary>
        public const float MaxGapYards = 250f;

        /// <summary>Two points is a single edge, not a route.</summary>
        public const int MinRoutePoints = 3;

        /// <summary>
        /// A position at centimetre resolution, packed into one long exactly as the miner packs
        /// it, so a key here and a key in wp_node mean the same place. Z is deliberately not
        /// part of it: the server snaps a ground creature to the terrain height and the same
        /// waypoint comes back a fifth of a yard lower on another capture.
        /// </summary>
        public static long NodeKey(float x, float y)
        {
            long xk = (long)Math.Round((x + 17100.0) * 100.0);
            long yk = (long)Math.Round((y + 17100.0) * 100.0);
            return (xk << 22) + yk;
        }

        /// <summary>
        /// Phase 0. The destination of a move order, which for the ordinary single point case is
        /// the whole row. An airborne spline keeps every point because nothing snapped it.
        /// </summary>
        public static bool IsOrderDestination(SniffDb.Point p)
        {
            return p.PointIndex == p.SegmentPoints - 1 || p.IsAirborne;
        }

        /// <summary>One move order, which is what has to recur for a position to count.</summary>
        private struct OrderRef
        {
            public long Sniff;
            public string Guid;
            public int Segment;
        }

        private sealed class OrderRefComparer : IEqualityComparer<OrderRef>
        {
            public static readonly OrderRefComparer Instance = new OrderRefComparer();

            public bool Equals(OrderRef a, OrderRef b)
            {
                return a.Sniff == b.Sniff && a.Segment == b.Segment &&
                       string.Equals(a.Guid, b.Guid, StringComparison.Ordinal);
            }

            public int GetHashCode(OrderRef o)
            {
                var h = o.Sniff.GetHashCode();
                h = (h * 397) ^ o.Segment;
                if (o.Guid != null) h = (h * 397) ^ o.Guid.GetHashCode();
                return h;
            }
        }

        public struct Pos
        {
            public float X, Y, Z;
        }

        /// <summary>One confirmed step, kept as a pair rather than a hash so it can be walked.</summary>
        public sealed class Edge
        {
            public long From, To;
            public int Obs;       // how many traversals were seen
            public int Sniffs;    // how many of them were independent captures
        }

        /// <summary>What survived the recurrence tests, ready to be asked about per point.</summary>
        public sealed class Confirmed
        {
            public HashSet<long> Nodes = new HashSet<long>();
            public HashSet<long> Edges = new HashSet<long>();

            /// <summary>Where each confirmed position is, for drawing and for measuring hops.</summary>
            public Dictionary<long, Pos> Where = new Dictionary<long, Pos>();

            /// <summary>The confirmed steps, walkable. Edges above is the same set hashed flat
            /// for the per point question "should this line be drawn".</summary>
            public List<Edge> EdgeList = new List<Edge>();

            public int RawNodes;
            public int RawEdges;

            /// <summary>An ordered pair of nodes, hashed the way a dictionary key wants.</summary>
            public static long EdgeKey(long from, long to)
            {
                unchecked
                {
                    const long mix = (long)0x9E3779B97F4A7C15UL;
                    return (from * 1000003L) ^ (to + mix);
                }
            }

            public bool KeepsNode(long k) { return Nodes.Contains(k); }

            public bool KeepsEdge(long from, long to)
            {
                return Edges.Contains(EdgeKey(from, to));
            }

            public float Distance(long a, long b)
            {
                Pos pa, pb;
                if (!Where.TryGetValue(a, out pa) || !Where.TryGetValue(b, out pb)) return 0f;
                var dx = pa.X - pb.X;
                var dy = pa.Y - pb.Y;
                var dz = pa.Z - pb.Z;
                return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        /// <summary>
        /// Phases 1 and 3 over whatever is on screen. Scoped to the drawn captures on purpose:
        /// the question being asked is "does what I am looking at hold up", and pooling in the
        /// captures that are ticked off would answer a different one.
        ///
        /// `points` is called once per capture and must return that capture's points in order,
        /// already through whatever kind filter is set.
        /// </summary>
        public static Confirmed Confirm(IEnumerable<SniffDb.Capture> captures,
                                        Func<SniffDb.Capture, IEnumerable<SniffDb.Point>> points)
        {
            var list = captures as IList<SniffDb.Capture> ?? captures.ToList();
            var res = new Confirmed();

            // Phase 1. Distinct move orders per position, not distinct sniffs: a second lap by
            // the same player is the same evidence as a second capture. Two points of ONE
            // spline landing on the same centimetre cannot confirm each other, which is what
            // keying on (sniff, guid, segment) buys.
            var nodeOrders = new Dictionary<long, HashSet<OrderRef>>();

            foreach (var c in list)
            {
                foreach (var p in points(c))
                {
                    var k = NodeKey(p.X, p.Y);
                    HashSet<OrderRef> seen;
                    if (!nodeOrders.TryGetValue(k, out seen))
                    {
                        seen = new HashSet<OrderRef>(OrderRefComparer.Instance);
                        nodeOrders[k] = seen;
                        res.Where[k] = new Pos { X = p.X, Y = p.Y, Z = p.Z };
                    }
                    if (seen.Count < 2)
                        seen.Add(new OrderRef { Sniff = c.SniffId, Guid = c.Guid, Segment = p.SegmentId });
                }
            }

            res.RawNodes = nodeOrders.Count;
            foreach (var kv in nodeOrders)
                if (kv.Value.Count >= 2) res.Nodes.Add(kv.Key);

            // Phase 3. Consecutive pairs where both ends survived phase 1, counted and kept at
            // two. Directed, as the miner has it - A to B and B to A are separate evidence.
            var obs = new Dictionary<long, Edge>();
            var sniffsPerEdge = new Dictionary<long, HashSet<long>>();

            foreach (var c in list)
            {
                long prev = 0;
                var have = false;
                foreach (var p in points(c))
                {
                    var k = NodeKey(p.X, p.Y);
                    if (have && prev != k && res.Nodes.Contains(prev) && res.Nodes.Contains(k))
                    {
                        var ek = Confirmed.EdgeKey(prev, k);
                        Edge e;
                        if (!obs.TryGetValue(ek, out e))
                        {
                            e = new Edge { From = prev, To = k };
                            obs[ek] = e;
                            sniffsPerEdge[ek] = new HashSet<long>();
                        }
                        e.Obs++;
                        sniffsPerEdge[ek].Add(c.SniffId);
                    }
                    prev = k;
                    have = true;
                }
            }

            res.RawEdges = obs.Count;
            foreach (var kv in obs)
            {
                if (kv.Value.Obs < 2) continue;
                kv.Value.Sniffs = sniffsPerEdge[kv.Key].Count;
                res.Edges.Add(kv.Key);
                res.EdgeList.Add(kv.Value);
            }

            return res;
        }

        // ---------------------------------------------------------------------------------
        // Chaining the confirmed steps into ordered routes
        // ---------------------------------------------------------------------------------

        /// <summary>One walkable route: positions in the order the creature took them.</summary>
        public sealed class Route
        {
            public List<long> Nodes = new List<long>();

            /// <summary>Which point the last one leads back to, or -1 for an open route.
            /// A walk stops when it reaches a position it has already stood on, and that is not
            /// necessarily the one it started from: A B C D B is a lasso, and calling it closed
            /// would have the reader draw D to A, a step nobody walked.</summary>
            public int CloseSeq = -1;

            public float LengthYards;
            public int MinObs, MaxObs, MinSniffs;

            public bool Closed { get { return CloseSeq >= 0; } }
            public int Count { get { return Nodes.Count; } }
        }

        /// <summary>
        /// Walk the confirmed steps into routes, strongest edge first, exactly as
        /// chain-paths.py does it.
        ///
        /// This is what answers "dozens of sniffs, all fragments". Each capture saw a slice of
        /// the route and none of them saw the whole thing, but the slices overlap, and the
        /// pooled graph is the union they were all fragments of. Drawn per capture it is
        /// confetti; walked as one graph it is the path.
        ///
        /// Independent captures break ties ahead of raw traversals: a second person seeing the
        /// same step is better evidence than the same person seeing it twice.
        /// </summary>
        public static List<Route> Chain(Confirmed c)
        {
            var routes = new List<Route>();
            if (c == null || c.EdgeList.Count == 0) return routes;

            var outs = new Dictionary<long, List<Edge>>();
            var indeg = new Dictionary<long, int>();

            foreach (var e in c.EdgeList)
            {
                List<Edge> l;
                if (!outs.TryGetValue(e.From, out l)) { l = new List<Edge>(); outs[e.From] = l; }
                l.Add(e);

                int d;
                indeg.TryGetValue(e.To, out d);
                indeg[e.To] = d + 1;
            }

            foreach (var l in outs.Values)
                l.Sort(delegate (Edge a, Edge b)
                {
                    var bySniff = b.Sniffs.CompareTo(a.Sniffs);
                    return bySniff != 0 ? bySniff : b.Obs.CompareTo(a.Obs);
                });

            var used = new HashSet<long>();

            // Heads of open routes first, then whatever cycles are left over. Starting a walk
            // in the middle of an open path would publish it as two pieces.
            var order = outs.Keys.Where(k => !indeg.ContainsKey(k))
                                 .Concat(outs.Keys.Where(indeg.ContainsKey))
                                 .ToList();

            foreach (var start in order)
            {
                if (outs[start].All(e => used.Contains(Confirmed.EdgeKey(e.From, e.To)))) continue;

                var chain = new List<long> { start };
                var hops = new List<float>();
                var edges = new List<Edge>();
                var seen = new HashSet<long> { start };
                var node = start;
                var closed = false;

                while (true)
                {
                    List<Edge> cand;
                    if (!outs.TryGetValue(node, out cand)) break;

                    Edge next = null;
                    foreach (var e in cand)
                        if (!used.Contains(Confirmed.EdgeKey(e.From, e.To))) { next = e; break; }
                    if (next == null) break;

                    used.Add(Confirmed.EdgeKey(next.From, next.To));
                    hops.Add(c.Distance(node, next.To));
                    edges.Add(next);
                    chain.Add(next.To);

                    if (seen.Contains(next.To)) { closed = true; break; }
                    seen.Add(next.To);
                    node = next.To;
                }

                if (edges.Count == 0) continue;

                if (closed && hops.All(h => h <= MaxGapYards))
                {
                    // Every point keeps its own outgoing edge and the repeated one is dropped,
                    // so no position appears twice and CloseSeq says where the last one leads.
                    var r = Build(chain.Take(chain.Count - 1).ToList(), hops, edges);
                    r.CloseSeq = chain.IndexOf(chain[chain.Count - 1]);
                    if (r.Count >= MinRoutePoints) routes.Add(r);
                    continue;
                }

                foreach (var part in SplitOnGaps(chain, hops, edges))
                    if (part.Count >= MinRoutePoints) routes.Add(part);
            }

            routes.Sort(delegate (Route a, Route b) { return b.Count.CompareTo(a.Count); });
            return routes;
        }

        private static Route Build(List<long> nodes, IList<float> hops, IList<Edge> edges)
        {
            return new Route
            {
                Nodes = nodes,
                LengthYards = hops.Sum(),
                MinObs = edges.Count == 0 ? 0 : edges.Min(e => e.Obs),
                MaxObs = edges.Count == 0 ? 0 : edges.Max(e => e.Obs),
                MinSniffs = edges.Count == 0 ? 0 : edges.Min(e => e.Sniffs)
            };
        }

        private static IEnumerable<Route> SplitOnGaps(List<long> chain, List<float> hops,
                                                      List<Edge> edges)
        {
            var parts = new List<Route>();
            var curNodes = new List<long> { chain[0] };
            var curHops = new List<float>();
            var curEdges = new List<Edge>();

            for (var i = 0; i < hops.Count; i++)
            {
                if (hops[i] > MaxGapYards)
                {
                    parts.Add(Build(curNodes, curHops, curEdges));
                    curNodes = new List<long> { chain[i + 1] };
                    curHops = new List<float>();
                    curEdges = new List<Edge>();
                }
                else
                {
                    curNodes.Add(chain[i + 1]);
                    curHops.Add(hops[i]);
                    curEdges.Add(edges[i]);
                }
            }

            parts.Add(Build(curNodes, curHops, curEdges));
            return parts;
        }

        // ---------------------------------------------------------------------------------
        // Loop detection
        // ---------------------------------------------------------------------------------

        /// <summary>Where a capture's repeating circuit sits inside it.</summary>
        public sealed class Loop
        {
            public int Start;     // index of the first point of the circuit
            public int Period;    // how many points before it comes back round
            public float Score;   // how much of the capture actually obeys that period
        }

        /// <summary>
        /// The shortest circuit the creature keeps re-walking, or null.
        ///
        /// Periodicity over the whole sequence rather than "when did it first come back to a
        /// place it had been". A web-like capture revisits positions constantly without ever
        /// running a circuit, and the first-revisit reading calls that a loop of length two;
        /// asking whether position k and position k+p agree ALL THE WAY DOWN does not.
        ///
        /// A line patrol reads as a loop of its full there-and-back, which is the honest
        /// answer - that is the shortest thing the creature actually repeats.
        /// </summary>
        public static Loop Find(IList<long> seq)
        {
            var n = seq.Count;
            if (n < 8) return null;

            // A circuit means positions come back. When nearly every one is new there is no
            // period to find and the scan below would be a long walk for nothing - this is the
            // Spider case, 4,789 orders inside a five yard circle, all of them fresh floats.
            var distinct = new HashSet<long>(seq).Count;
            if (distinct * 10 > n * 6) return null;

            var maxPeriod = Math.Min(n / 2, 1024);
            for (var p = 2; p <= maxPeriod; p++)
            {
                var compared = n - p;
                var hits = 0;
                for (var k = 0; k < compared; k++)
                    if (seq[k] == seq[k + p]) hits++;

                var score = (float)hits / compared;
                if (score < 0.85f) continue;

                // Where the circuit actually begins. A capture that started on the way to the
                // route has a prefix that belongs to no lap, and the 0.85 above tolerates it
                // rather than rejecting the loop, so it has to be stepped over here.
                var start = 0;
                for (var i = 0; i + 2 * p <= n; i++)
                {
                    var clean = true;
                    for (var k = 0; k < p; k++)
                        if (seq[i + k] != seq[i + k + p]) { clean = false; break; }
                    if (clean) { start = i; break; }
                }

                return new Loop { Start = start, Period = p, Score = score };
            }

            return null;
        }
    }
}
