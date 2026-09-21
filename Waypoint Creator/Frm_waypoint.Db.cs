using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Frm_waypoint
{
    /// <summary>
    /// The database view: every capture of one entry, drawn together.
    ///
    /// The point is to stop asking "what is this entry's path" and start asking "what did each
    /// of these creatures do". An entry that patrols in one place and wanders in another is one
    /// row in the digest and two obviously different pictures here, and that difference is the
    /// whole reason this window exists.
    /// </summary>
    public partial class Frm_Waypoint
    {
        private List<SniffDb.Capture> _captures = new List<SniffDb.Capture>();
        private List<SniffDb.Capture> _shown = new List<SniffDb.Capture>();
        private SniffDb.Capture _focus;
        private uint _dbEntry;
        private string _dbEntryName = "";
        private bool _dbMode;
        private bool _suspendRedraw;

        /// <summary>What survived the recurrence tests over the captures currently drawn, and
        /// the signature of the state it was computed for so it is not recomputed on every
        /// keystroke through the list.</summary>
        private PathFilters.Confirmed _confirmed;
        private long _confirmedFor = long.MinValue;

        /// <summary>Loop detection is O(n*period) per capture, so the answer is kept until
        /// something it depends on changes.</summary>
        private readonly Dictionary<SniffDb.Capture, PathFilters.Loop> _loopCache =
            new Dictionary<SniffDb.Capture, PathFilters.Loop>();

        private int _loopsFound;

        /// <summary>The pooled graph walked into ordered routes, when Merge is on.</summary>
        private List<PathFilters.Route> _routes = new List<PathFilters.Route>();
        private int _routeFocus = -1;

        /// <summary>Per capture colour. Saturated only - a pastel vanishes into the minimap.</summary>
        private static readonly SKColor[] CaptureColours =
        {
            new SKColor(0xE6, 0x19, 0x4B), new SKColor(0x3C, 0xB4, 0x4B),
            new SKColor(0x43, 0x63, 0xD8), new SKColor(0xF5, 0x82, 0x31),
            new SKColor(0x91, 0x1E, 0xB4), new SKColor(0x00, 0x80, 0x80),
            new SKColor(0xF0, 0x32, 0xE6), new SKColor(0x9A, 0x63, 0x24),
            new SKColor(0x80, 0x00, 0x00), new SKColor(0x00, 0x8B, 0x8B),
            new SKColor(0xCB, 0x8B, 0x00), new SKColor(0x4B, 0x00, 0x82),
            new SKColor(0xB2, 0x22, 0x22), new SKColor(0x2E, 0x8B, 0x57),
            new SKColor(0x19, 0x19, 0x70), new SKColor(0xD2, 0x69, 0x1E)
        };

        /// <summary>One map an entry was seen moving on, and how much of it happened there.</summary>
        private class MapChoice
        {
            public uint Map;
            public int Captures;
            public long Points;

            public override string ToString()
            {
                var name = MapManager.GetMapName((int)Map);
                return string.Format(CultureInfo.InvariantCulture,
                    "map {0,-5} {1,-28} {2,6} captures, {3,8} points",
                    Map, name ?? "(no minimap art)", Captures, Points);
            }
        }

        private class CaptureLayer
        {
            public SniffDb.Capture Capture;
            public int Index;        // its row in _shown, which is also what gives it its colour
            public SKPath Walk;      // consecutive orders
            public SKPath Jumps;     // hops long enough to be a gap in the capture, not a step
            public SKPoint[] Pts;      // every drawn point, for hit testing and for dots
            public SKPoint[] Legs;     // consecutive pairs, one per solid line actually drawn
            public List<SniffDb.Point> Kept;  // what the grid should list for this capture
            public PathFilters.Loop Loop;
            public PathFilters.Route Route;   // set instead of Capture when merging
            public SKColor Colour;
            public SKPoint? Spawn;
            public SKPoint? Start;
        }

        private readonly List<CaptureLayer> _layers = new List<CaptureLayer>();

        /// <summary>A hop further than this is drawn dashed. Nothing is hidden by it - it only
        /// says "these two orders are not one step", which is what a re-entry into visibility
        /// range looks like.</summary>
        private const float GapYards = 120f;

        // -----------------------------------------------------------------------------------
        // Loading
        // -----------------------------------------------------------------------------------

        private async void ToolStripButtonLoadDb_Click(object sender, EventArgs e)
        {
            var term = (toolStripTextBoxEntry.Text ?? "").Trim();
            if (term.Length == 0)
            {
                MessageBox.Show("Type an entry id or part of a creature name in the box first.",
                                "Load Entry", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Cursor = Cursors.WaitCursor;
            toolStripStatusLabel.Text = "Searching " + term + "...";
            try
            {
                List<SniffDb.EntryHit> hits = null;
                await Task.Run(() => { hits = SniffDb.SearchEntries(term, 60); });

                if (hits == null || hits.Count == 0)
                {
                    MessageBox.Show("Nothing in the corpus moved under '" + term + "'.",
                                    "Load Entry", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var hit = hits[0];
                if (hits.Count > 1)
                {
                    hit = Frm_pick.Choose(this, hits, "Which entry?");
                    if (hit == null) return;
                }

                _dbEntry = hit.Entry;
                _dbEntryName = hit.Name;

                List<SniffDb.Capture> caps = null;
                toolStripStatusLabel.Text = "Loading captures of " + _dbEntry + "...";
                await Task.Run(() => { caps = SniffDb.LoadCaptures(_dbEntry, -1); });
                _captures = caps ?? new List<SniffDb.Capture>();

                if (_captures.Count == 0)
                {
                    MessageBox.Show(_dbEntry + " - " + _dbEntryName +
                                    " has no movement in the corpus.",
                                    "Load Entry", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 681 entries move on more than one map - Spider is on 26 of them - and two
                // worlds laid on top of each other is not a picture of anything. Ask, busiest
                // first, rather than picking silently.
                var byMap = _captures.GroupBy(c => c.Map)
                                     .OrderByDescending(g => g.Sum(c => (long)c.Points))
                                     .Select(g => new MapChoice
                                     {
                                         Map = g.Key,
                                         Captures = g.Count(),
                                         Points = g.Sum(c => (long)c.Points)
                                     })
                                     .ToList();

                var chosen = byMap.Count == 1
                    ? byMap[0]
                    : Frm_pick.Choose(this, byMap,
                        _dbEntry + " - " + _dbEntryName + " moves on " + byMap.Count + " maps");
                if (chosen == null) return;

                var otherMaps = byMap.Count - 1;
                _captures = _captures.Where(c => c.Map == chosen.Map).ToList();

                var cap = CaptureCap();
                var take = _captures.Take(cap).ToList();
                toolStripStatusLabel.Text = "Reading " + take.Count + " captures...";
                await Task.Run(() =>
                {
                    SniffDb.LoadPoints(take);
                    SniffDb.LoadSniffInfo(take);
                });

                _shown = take;
                _focus = null;
                _dbMode = true;
                mapID = chosen.Map.ToString(CultureInfo.InvariantCulture);
                _mapProvider.LoadMap((int)chosen.Map);
                listBox.Visible = false;
                checkedListCaptures.Visible = true;
                if (toolStripComboKind.SelectedIndex < 0) toolStripComboKind.SelectedIndex = 0;

                FillCaptureList();
                RebuildLayers();
                ZoomToLayers();

                Text = string.Format(CultureInfo.InvariantCulture,
                    "Waypoint Creator - {0} {1} - {2} captures on {3}{4}",
                    _dbEntry, _dbEntryName, _captures.Count, chosen,
                    otherMaps > 0 ? " (+" + otherMaps + " other maps)" : "");

                toolStripStatusLabel.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0} of {1} captures drawn, {2} points. {3}",
                    _shown.Count, _captures.Count, _shown.Sum(c => (long)c.Path.Count),
                    _captures.Count > _shown.Count
                        ? "Raise Max to draw more."
                        : "All of them.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read the ingest database:" + Environment.NewLine +
                                Environment.NewLine + ex.Message + Environment.NewLine +
                                Environment.NewLine + "Connection: " + SniffDb.ConnectionString()
                                    .Replace("password=" + Properties.Settings.Default.password,
                                             "password=***"),
                                "Database", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private int CaptureCap()
        {
            int n;
            if (int.TryParse((toolStripTextBoxCap.Text ?? "").Trim(), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out n) && n > 0)
                return n;
            return 150;
        }

        private void FillCaptureList()
        {
            _suspendRedraw = true;
            try
            {
                checkedListCaptures.Items.Clear();
                foreach (var c in _shown)
                    checkedListCaptures.Items.Add(c.Label(), true);
            }
            finally
            {
                _suspendRedraw = false;
            }
        }

        // -----------------------------------------------------------------------------------
        // Selection
        // -----------------------------------------------------------------------------------

        private void CaptureList_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suspendRedraw) return;
            // ItemCheck fires before the state changes, so schedule the rebuild after it lands.
            BeginInvoke((MethodInvoker)delegate
            {
                RebuildLayers();
                UpdateDrawnCount();
                _mapControl.Invalidate();
            });
        }

        private void CaptureList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suspendRedraw) return;
            var i = checkedListCaptures.SelectedIndex;
            _focus = (i >= 0 && i < _shown.Count) ? _shown[i] : null;

            // Layers first: the grid reads what the map drew, so it has to exist by then.
            RebuildLayers();

            // While merging, the map is showing routes rather than captures, so a row click
            // re-pools rather than re-focuses and the grid stays on the route.
            if (Merging())
            {
                UpdateDrawnCount();
                _mapControl.Invalidate();
                return;
            }

            if (_focus != null)
            {
                creature_entry = _dbEntry.ToString(CultureInfo.InvariantCulture);
                creature_name = _dbEntryName;
                creature_guid = _focus.Guid;
                mapID = _focus.Map.ToString(CultureInfo.InvariantCulture);
                FillGridFromCapture(_focus);
                toolStripStatusLabel.Text = Describe(_focus);
            }

            _mapControl.Invalidate();
        }

        private void ToolStripButtonAll_Click(object sender, EventArgs e) { SetAllChecked(true); }
        private void ToolStripButtonNone_Click(object sender, EventArgs e) { SetAllChecked(false); }

        /// <summary>The same commands as the toolbar, next to the list they act on.</summary>
        private void InitCaptureList()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Select all", null, delegate { SetAllChecked(true); });
            menu.Items.Add("Deselect all", null, delegate { SetAllChecked(false); });
            menu.Items.Add("Invert", null, delegate { SetAllChecked(null); });
            menu.Items.Add(new ToolStripSeparator());

            var only = new ToolStripMenuItem("Only this one", null,
                delegate { IsolateCapture(checkedListCaptures.SelectedIndex); });
            menu.Items.Add(only);
            menu.Items.Add(new ToolStripSeparator());

            // The hash is how a sniff is asked for by name everywhere else - the ingest keys on
            // it, and "re-parse this one" needs it rather than a file name somebody renamed.
            var hash = new ToolStripMenuItem("Copy sniff hash", null, delegate { CopyFromFocus(0); });
            var guid = new ToolStripMenuItem("Copy guid", null, delegate { CopyFromFocus(1); });
            var file = new ToolStripMenuItem("Copy sniff file name", null, delegate { CopyFromFocus(2); });
            menu.Items.Add(hash);
            menu.Items.Add(guid);
            menu.Items.Add(file);

            menu.Opening += delegate
            {
                var on = checkedListCaptures.SelectedIndex >= 0;
                only.Enabled = on;
                hash.Enabled = on;
                guid.Enabled = on;
                file.Enabled = on;
            };

            checkedListCaptures.ContextMenuStrip = menu;

            // A CheckedListBox does not select the row you right clicked, so "this one" would
            // otherwise mean whichever row happened to be selected before.
            checkedListCaptures.MouseDown += delegate (object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                var i = checkedListCaptures.IndexFromPoint(e.Location);
                if (i >= 0) checkedListCaptures.SelectedIndex = i;
            };
        }

        /// <summary>0 the sniff hash, 1 the guid, 2 the sniff file name.</summary>
        private void CopyFromFocus(int what)
        {
            var i = checkedListCaptures.SelectedIndex;
            if (i < 0 || i >= _shown.Count) return;

            var c = _shown[i];
            var text = what == 0 ? c.SniffHash : what == 1 ? c.Guid : c.SniffName;

            if (string.IsNullOrEmpty(text))
            {
                toolStripStatusLabel.Text = "Nothing to copy - sniff " +
                    c.SniffId.ToString(CultureInfo.InvariantCulture) +
                    " has no row in the sniff table.";
                return;
            }

            Clipboard.SetText(text);
            toolStripStatusLabel.Text = "Copied  " + text +
                (what == 0 && !string.IsNullOrEmpty(c.SniffBuild) ? "   (" + c.SniffBuild + ")" : "");
        }

        /// <summary>true ticks everything, false clears it, null flips each row.</summary>
        private void SetAllChecked(bool? on)
        {
            if (!_dbMode) return;
            _suspendRedraw = true;
            try
            {
                for (var i = 0; i < checkedListCaptures.Items.Count; i++)
                    checkedListCaptures.SetItemChecked(
                        i, on ?? !checkedListCaptures.GetItemChecked(i));
            }
            finally
            {
                _suspendRedraw = false;
            }
            RebuildLayers();
            UpdateDrawnCount();
            _mapControl.Invalidate();
        }

        /// <summary>Untick everything except this one and bring it to the front.</summary>
        private void IsolateCapture(int index)
        {
            if (!_dbMode || index < 0 || index >= checkedListCaptures.Items.Count) return;

            _suspendRedraw = true;
            try
            {
                for (var i = 0; i < checkedListCaptures.Items.Count; i++)
                    checkedListCaptures.SetItemChecked(i, i == index);
            }
            finally
            {
                _suspendRedraw = false;
            }

            checkedListCaptures.TopIndex = Math.Max(0, index - 3);

            // Selecting it fills the grid and the status line and redraws; when it is already
            // the selected row the event will not fire on its own.
            if (checkedListCaptures.SelectedIndex == index)
                CaptureList_SelectedIndexChanged(checkedListCaptures, EventArgs.Empty);
            else
                checkedListCaptures.SelectedIndex = index;
        }

        private void UpdateDrawnCount()
        {
            if (!_dbMode) return;

            var sb = new StringBuilder();

            if (Merging())
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0} positions and {1} steps from {2} captures merged into {3} routes.",
                    _confirmed == null ? 0 : _confirmed.Nodes.Count,
                    _confirmed == null ? 0 : _confirmed.Edges.Count,
                    CheckedCaptures().Count(), _routes.Count);
                if (_routes.Count > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "  |  longest {0} points, {1:0} yd. Click a route to list it.",
                        _routes[0].Count, _routes[0].LengthYards);
                toolStripStatusLabel.Text = sb.ToString();
                return;
            }

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "{0} of {1} captures drawn, {2} points.",
                _layers.Count, _shown.Count, _layers.Sum(l => (long)l.Pts.Length));

            if (_confirmed != null)
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "  |  smoothed: {0} of {1} positions and {2} of {3} steps recur",
                    _confirmed.Nodes.Count, _confirmed.RawNodes,
                    _confirmed.Edges.Count, _confirmed.RawEdges);

            if (LoopOnly())
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "  |  {0} of {1} captures run a circuit", _loopsFound, _layers.Count);

            toolStripStatusLabel.Text = sb.ToString();
        }

        // -----------------------------------------------------------------------------------
        // Picking a capture off the map
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// A click on the map that was not a drag. Landing on a capture keeps that one alone,
        /// which is the quickest way to ask whose route that is; Ctrl adds or removes it
        /// instead. A click on empty ground is not an instruction to clear anything.
        /// </summary>
        private void HandleMapClick(MouseEventArgs e)
        {
            if (!_dbMode) return;

            var hit = HitTestCapture(e.Location, false);
            if (hit < 0) return;

            if (Merging()) { FocusRoute(hit); return; }

            if ((ModifierKeys & Keys.Control) == Keys.Control)
                checkedListCaptures.SetItemChecked(hit, !checkedListCaptures.GetItemChecked(hit));
            else
                IsolateCapture(hit);
        }

        /// <summary>
        /// Which capture is under the cursor, as a row in _shown, or -1. The spawn ring and the
        /// start dot are the deliberate targets so they win over a line passing behind them;
        /// after that it is the nearest leg of a route. Tolerances are in screen pixels, so
        /// what is clickable does not change with the zoom.
        /// </summary>
        private int HitTestCapture(Point screen, bool markersOnly)
        {
            if (!_dbMode || _layers.Count == 0) return -1;

            var w = ScreenToWorld(screen);
            var p = new SKPoint(w.X, w.Y);
            var zoom = Math.Max(_mapProvider.Zoom, 0.0001f);

            var best = -1;
            var bestDist = (12f / zoom) * (12f / zoom);

            foreach (var layer in _layers)
            {
                if (layer.Spawn.HasValue)
                    Closer(ref best, ref bestDist, layer.Index, Dist2(p, layer.Spawn.Value));
                if (layer.Start.HasValue)
                    Closer(ref best, ref bestDist, layer.Index, Dist2(p, layer.Start.Value));
            }

            if (best >= 0 || markersOnly) return best;

            // Only what was actually drawn is clickable. Legs holds the solid lines and
            // nothing else, so the dashed gaps - which are not steps the creature took - cannot
            // swallow a click, and a points-only view leaves just the dots to aim at.
            bestDist = (6f / zoom) * (6f / zoom);
            foreach (var layer in _layers)
            {
                for (var k = 0; k + 1 < layer.Legs.Length; k += 2)
                    Closer(ref best, ref bestDist, layer.Index,
                           Dist2ToSegment(p, layer.Legs[k], layer.Legs[k + 1]));

                foreach (var q in layer.Pts)
                    Closer(ref best, ref bestDist, layer.Index, Dist2(p, q));
            }
            return best;
        }

        private static void Closer(ref int best, ref float bestDist, int index, float dist2)
        {
            if (dist2 >= bestDist) return;
            bestDist = dist2;
            best = index;
        }

        private static float Dist2(SKPoint a, SKPoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static float Dist2ToSegment(SKPoint p, SKPoint a, SKPoint b)
        {
            var vx = b.X - a.X;
            var vy = b.Y - a.Y;
            var len2 = vx * vx + vy * vy;
            if (len2 <= 0f) return Dist2(p, a);

            var t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return Dist2(p, new SKPoint(a.X + t * vx, a.Y + t * vy));
        }

        private void PointFilter_Changed(object sender, EventArgs e)
        {
            if (!_dbMode) return;

            // Smoothing pools every drawn point and loop detection scans each capture against
            // every candidate period, so this is the one control that can take a moment.
            Cursor = Cursors.WaitCursor;
            try
            {
                RebuildLayers();
                UpdateDrawnCount();

                if (Merging())
                {
                    if (_routeFocus >= 0) FillGridFromRoute(_routes[_routeFocus]);
                }
                else if (_focus != null)
                {
                    FillGridFromCapture(_focus);
                    toolStripStatusLabel.Text = Describe(_focus);
                }
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            _mapControl.Invalidate();
        }

        /// <summary>
        /// 0 every point, 1 the destination of each order, 2 splines only, 3 single
        /// destinations only, 4 every point with no lines between them.
        /// </summary>
        private int PointFilter()
        {
            return toolStripComboKind.SelectedIndex < 0 ? 0 : toolStripComboKind.SelectedIndex;
        }

        private IEnumerable<SniffDb.Point> FilteredPoints(SniffDb.Capture c)
        {
            switch (PointFilter())
            {
                case 1: return c.Path.Where(PathFilters.IsOrderDestination);
                case 2: return c.Path.Where(p => p.IsSpline);
                case 3: return c.Path.Where(p => !p.IsSpline);
                default: return c.Path;
            }
        }

        private string Describe(SniffDb.Capture c)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "guid {0}  sniff {1}  map {2}  |  {3} points in {4} segments, {5} of them splines",
                c.Guid, c.SniffId, c.Map, c.Points, c.Segments, c.SplineSegments);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "  |  ranged {0:0.#} yd (p99 {1:0.#})  |  watched {2:0} min",
                c.Radius, c.RadiusRobust, c.Watched.TotalMinutes);
            if (c.HasSpawn)
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "  |  spawn {0:0.##} {1:0.##} {2:0.##}", c.SpawnX, c.SpawnY, c.SpawnZ);

            foreach (var l in _layers)
            {
                if (!ReferenceEquals(l.Capture, c) || l.Loop == null) continue;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "  |  circuit of {0} points, {1:0}% of the capture obeys it",
                    l.Loop.Period, l.Loop.Score * 100f);
                break;
            }

            if (!string.IsNullOrEmpty(c.SniffHash))
                sb.Append("  |  ").Append(c.SniffHash.Substring(0, Math.Min(12, c.SniffHash.Length)));
            if (!string.IsNullOrEmpty(c.SniffName)) sb.Append("  |  ").Append(c.SniffName);
            return sb.ToString();
        }

        // -----------------------------------------------------------------------------------
        // Drawing
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Everything the drawn set depends on, in one number, so the pooled recurrence pass
        /// and the loop cache are thrown away when they go stale and not before.
        /// </summary>
        private long DrawSignature()
        {
            long h = PointFilter() * 31 + (Smoothing() ? 7 : 0) + (LoopOnly() ? 3 : 0) +
                     (Merging() ? 11 : 0);
            h = h * 1000003 + _dbEntry;
            for (var i = 0; i < checkedListCaptures.Items.Count; i++)
                if (checkedListCaptures.GetItemChecked(i)) h = h * 1000003 + i + 1;
            return h;
        }

        /// <summary>Merging has nothing to walk without the recurrence tests, so it turns
        /// them on whether or not Smooth is lit.</summary>
        private bool Smoothing() { return toolStripButtonSmooth.Checked || Merging(); }
        private bool Merging() { return toolStripButtonMerge.Checked; }
        private bool LoopOnly() { return toolStripButtonLoop.Checked && !Merging(); }

        /// <summary>Whether a line between consecutive points means anything worth drawing.</summary>
        private bool DrawLines() { return PointFilter() != 4; }

        private IEnumerable<SniffDb.Capture> CheckedCaptures()
        {
            for (var i = 0; i < _shown.Count; i++)
                if (i >= checkedListCaptures.Items.Count || checkedListCaptures.GetItemChecked(i))
                    yield return _shown[i];
        }

        /// <summary>
        /// Run the recurrence tests over the captures on screen, unless the answer already on
        /// hand was computed for exactly this set and these filters.
        /// </summary>
        private void EnsureConfirmed()
        {
            var sig = DrawSignature();
            if (sig == _confirmedFor && _confirmed != null) return;

            _confirmedFor = sig;
            _loopCache.Clear();
            _confirmed = Smoothing()
                ? PathFilters.Confirm(CheckedCaptures(), FilteredPoints)
                : null;
        }

        /// <summary>
        /// The points of one capture as they will be drawn: the Show filter, then the
        /// recurrence test when Smooth is on, then the loop trim when Loop is on. One list, so
        /// the map, the grid and the hit testing can never disagree about what is on screen.
        /// </summary>
        private List<SniffDb.Point> PointsToDraw(SniffDb.Capture c, out PathFilters.Loop loop)
        {
            loop = null;
            var pts = FilteredPoints(c).ToList();
            if (pts.Count == 0) return pts;

            if (_confirmed != null)
                pts = pts.Where(q => _confirmed.KeepsNode(PathFilters.NodeKey(q.X, q.Y))).ToList();

            if (!LoopOnly() || pts.Count < 8) return pts;

            if (!_loopCache.TryGetValue(c, out loop))
            {
                List<long> keys;
                List<int> idx;
                NodeSequence(pts, out keys, out idx);
                loop = PathFilters.Find(keys);
                if (loop != null)
                {
                    // Translate the circuit back into positions in the point list, and take one
                    // past its end so the lap closes on the map instead of stopping a step short.
                    loop.Start = idx[loop.Start];
                    var end = loop.Start + loop.Period < idx.Count ? idx[loop.Start + loop.Period] : -1;
                    loop.Period = (end < 0 ? pts.Count - 1 : end) - loop.Start;
                }
                _loopCache[c] = loop;
            }

            if (loop == null) return pts;
            var take = Math.Min(loop.Period + 1, pts.Count - loop.Start);
            return pts.GetRange(loop.Start, take);
        }

        /// <summary>
        /// The positions a capture visited, as node keys, with a creature re-sent to where it
        /// already stands collapsed away - a repeat is not a step and it would throw the period
        /// off. idx maps each key back to the point it came from.
        /// </summary>
        private static void NodeSequence(List<SniffDb.Point> pts, out List<long> keys, out List<int> idx)
        {
            keys = new List<long>(pts.Count);
            idx = new List<int>(pts.Count);

            long prev = 0;
            var have = false;
            for (var i = 0; i < pts.Count; i++)
            {
                var k = PathFilters.NodeKey(pts[i].X, pts[i].Y);
                if (have && k == prev) continue;
                keys.Add(k);
                idx.Add(i);
                prev = k;
                have = true;
            }
        }

        /// <summary>
        /// The merged view: one layer per chained route rather than one per capture. The
        /// capture list still decides what goes into the pool, so ticking a bad capture off and
        /// watching a route knit together is why both controls exist.
        /// </summary>
        private void RebuildRoutes()
        {
            _routes = PathFilters.Chain(_confirmed);
            if (_routeFocus >= _routes.Count) _routeFocus = -1;
            if (_routeFocus < 0 && _routes.Count > 0) _routeFocus = 0;  // Chain sorts longest first

            var lines = DrawLines();

            for (var i = 0; i < _routes.Count; i++)
            {
                var r = _routes[i];
                var layer = new CaptureLayer
                {
                    Route = r,
                    Index = i,
                    Walk = new SKPath(),
                    Jumps = new SKPath(),
                    Colour = CaptureColours[i % CaptureColours.Length]
                };

                var px = new List<SKPoint>(r.Count);
                foreach (var k in r.Nodes)
                {
                    PathFilters.Pos q;
                    if (_confirmed.Where.TryGetValue(k, out q)) px.Add(new SKPoint(-q.Y, -q.X));
                }

                var legs = new List<SKPoint>();
                if (lines)
                {
                    for (var k = 1; k < px.Count; k++)
                    {
                        layer.Walk.MoveTo(px[k - 1]);
                        layer.Walk.LineTo(px[k]);
                        legs.Add(px[k - 1]);
                        legs.Add(px[k]);
                    }

                    // The step that closes a ring. Without it a lap is drawn as a line with its
                    // two ends lying next to each other, which reads as an open route.
                    if (r.Closed && px.Count > 1 && r.CloseSeq < px.Count)
                    {
                        layer.Walk.MoveTo(px[px.Count - 1]);
                        layer.Walk.LineTo(px[r.CloseSeq]);
                        legs.Add(px[px.Count - 1]);
                        legs.Add(px[r.CloseSeq]);
                    }
                }

                if (px.Count > 0) layer.Start = px[0];
                layer.Pts = px.ToArray();
                layer.Legs = legs.ToArray();
                _layers.Add(layer);
            }
        }

        private bool IsFocused(CaptureLayer layer)
        {
            return Merging()
                ? layer.Index == _routeFocus
                : _focus != null && ReferenceEquals(layer.Capture, _focus);
        }

        private bool AnythingFocused()
        {
            return Merging() ? _routeFocus >= 0 : _focus != null;
        }

        /// <summary>Bring one merged route forward and list its points in order.</summary>
        private void FocusRoute(int i)
        {
            if (i < 0 || i >= _routes.Count) return;
            _routeFocus = i;
            FillGridFromRoute(_routes[i]);
            toolStripStatusLabel.Text = DescribeRoute(i);
            _mapControl.Invalidate();
        }

        private void FillGridFromRoute(PathFilters.Route r)
        {
            gridWaypoint.Rows.Clear();
            var n = 0;
            foreach (var k in r.Nodes)
            {
                PathFilters.Pos q;
                if (!_confirmed.Where.TryGetValue(k, out q)) continue;

                // No timings: a merged point is a position several captures agreed on, and the
                // moments they each reached it are not one creature's clock.
                gridWaypoint.Rows.Add(
                    ++n,
                    q.X.ToString("0.####", CultureInfo.InvariantCulture),
                    q.Y.ToString("0.####", CultureInfo.InvariantCulture),
                    q.Z.ToString("0.####", CultureInfo.InvariantCulture),
                    "", "", "");
            }
        }

        private string DescribeRoute(int i)
        {
            if (i < 0 || i >= _routes.Count) return "";
            var r = _routes[i];

            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "route {0} of {1}  |  {2} points, {3:0.#} yd", i + 1, _routes.Count,
                r.Count, r.LengthYards);

            sb.Append(r.Closed
                ? (r.CloseSeq == 0
                    ? "  |  a ring"
                    : "  |  the last point leads back to point " +
                      (r.CloseSeq + 1).ToString(CultureInfo.InvariantCulture))
                : "  |  open ended");

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "  |  its weakest step was walked {0} times across {1} sniffs",
                r.MinObs, r.MinSniffs);
            return sb.ToString();
        }

        private void RebuildLayers()
        {
            foreach (var l in _layers)
            {
                l.Walk.Dispose();
                l.Jumps.Dispose();
            }
            _layers.Clear();
            _loopsFound = 0;
            if (!_dbMode) return;

            EnsureConfirmed();
            if (Merging()) { RebuildRoutes(); return; }
            _routes.Clear();
            _routeFocus = -1;
            var lines = DrawLines();

            for (var i = 0; i < _shown.Count; i++)
            {
                if (i < checkedListCaptures.Items.Count && !checkedListCaptures.GetItemChecked(i))
                    continue;

                var c = _shown[i];
                PathFilters.Loop loop;
                var pts = PointsToDraw(c, out loop);
                if (pts.Count == 0) continue;
                if (loop != null) _loopsFound++;

                var layer = new CaptureLayer
                {
                    Capture = c,
                    Index = i,
                    Loop = loop,
                    Kept = pts,
                    Walk = new SKPath(),
                    Jumps = new SKPath(),
                    Colour = CaptureColours[i % CaptureColours.Length]
                };

                // Built as lists and handed over as arrays: DrawPoints takes an array, and
                // converting it on every paint would allocate megabytes a frame.
                var px = new List<SKPoint>(pts.Count);
                var legs = new List<SKPoint>();

                var prev = new SKPoint(-pts[0].Y, -pts[0].X);
                var prevKey = PathFilters.NodeKey(pts[0].X, pts[0].Y);
                layer.Start = prev;
                px.Add(prev);

                var open = false;
                for (var k = 1; k < pts.Count; k++)
                {
                    var cur = new SKPoint(-pts[k].Y, -pts[k].X);
                    var curKey = PathFilters.NodeKey(pts[k].X, pts[k].Y);
                    px.Add(cur);
                    if (!lines) { prev = cur; prevKey = curKey; continue; }

                    // Smoothing draws confirmed steps and nothing else, each on its own, because
                    // two confirmed steps are not necessarily next to each other any more.
                    if (_confirmed != null)
                    {
                        if (curKey != prevKey && _confirmed.KeepsEdge(prevKey, curKey))
                        {
                            layer.Walk.MoveTo(prev);
                            layer.Walk.LineTo(cur);
                            legs.Add(prev);
                            legs.Add(cur);
                        }
                        prev = cur;
                        prevKey = curKey;
                        continue;
                    }

                    var dx = cur.X - prev.X;
                    var dy = cur.Y - prev.Y;
                    if (dx * dx + dy * dy > GapYards * GapYards)
                    {
                        layer.Jumps.MoveTo(prev);
                        layer.Jumps.LineTo(cur);
                        open = false;
                    }
                    else
                    {
                        if (!open) { layer.Walk.MoveTo(prev); open = true; }
                        layer.Walk.LineTo(cur);
                        legs.Add(prev);
                        legs.Add(cur);
                    }
                    prev = cur;
                    prevKey = curKey;
                }

                layer.Pts = px.ToArray();
                layer.Legs = legs.ToArray();
                if (c.HasSpawn) layer.Spawn = new SKPoint(-c.SpawnY, -c.SpawnX);
                _layers.Add(layer);
            }
        }

        /// <summary>
        /// Everything checked, each in its own colour, with the selected capture on top. The
        /// unselected ones stay visible at low alpha so a route can be read against the wander
        /// its neighbours were doing at the same time.
        /// </summary>
        private void DrawCaptures(SKCanvas canvas)
        {
            if (!_dbMode || _layers.Count == 0) return;

            var thin = 1.0f / Math.Max(_mapProvider.Zoom, 0.05f);

            using (var dash = SKPathEffect.CreateDash(new[] { 6f * thin, 6f * thin }, 0))
            {
                var any = AnythingFocused();
                foreach (var layer in _layers)
                {
                    if (IsFocused(layer)) continue;
                    DrawLayer(canvas, layer, thin, any ? (byte)70 : (byte)210, dash);
                }

                foreach (var layer in _layers)
                {
                    if (!IsFocused(layer)) continue;
                    DrawLayer(canvas, layer, thin * 2f, 255, dash);
                }
            }
        }

        private void DrawLayer(SKCanvas canvas, CaptureLayer layer, float width, byte alpha,
                               SKPathEffect dash)
        {
            using (var line = new SKPaint
            {
                Color = layer.Colour.WithAlpha(alpha),
                StrokeWidth = width,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            })
            {
                canvas.DrawPath(layer.Walk, line);

                // Smoothing already threw away everything that was not walked twice, so there
                // is no gap left to mark; drawing them again would put back the web.
                if (_confirmed == null)
                {
                    line.PathEffect = dash;
                    line.Color = layer.Colour.WithAlpha((byte)(alpha / 2));
                    canvas.DrawPath(layer.Jumps, line);
                    line.PathEffect = null;
                }

                // Where the creature actually stopped. Without lines this is the whole picture,
                // and with smoothing it is what survived, so both want it; one DrawPoints puts
                // the entire capture down in a single call.
                if (layer.Pts.Length > 0 && (!DrawLines() || _confirmed != null))
                {
                    line.Color = layer.Colour.WithAlpha(alpha);
                    line.StrokeWidth = 3.5f * width;
                    line.StrokeCap = SKStrokeCap.Round;
                    canvas.DrawPoints(SKPointMode.Points, layer.Pts, line);
                    line.StrokeCap = SKStrokeCap.Butt;
                    line.StrokeWidth = width;
                }

                // A hollow ring on the spawn and a filled dot on the first order. When the two
                // sit on top of each other the creature never left home; when they are far
                // apart the capture began somewhere down the route.
                line.Color = layer.Colour.WithAlpha(alpha);
                if (layer.Spawn.HasValue)
                {
                    line.StrokeWidth = width;
                    canvas.DrawCircle(layer.Spawn.Value, 4f * width, line);
                }
                if (layer.Start.HasValue)
                {
                    line.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(layer.Start.Value, 2.5f * width, line);
                }
            }
        }

        private void ZoomToLayers()
        {
            var pts = _layers.SelectMany(l => new[] { l.Walk.Bounds, l.Jumps.Bounds })
                             .Where(b => b.Width > 0 || b.Height > 0).ToList();
            if (pts.Count == 0) return;

            var minX = pts.Min(b => b.Left);
            var maxX = pts.Max(b => b.Right);
            var minY = pts.Min(b => b.Top);
            var maxY = pts.Max(b => b.Bottom);

            _mapProvider.LoadMap(int.Parse(mapID, CultureInfo.InvariantCulture));
            _mapProvider.Center = new PointF((minX + maxX) / 2f, (minY + maxY) / 2f);

            var spanX = Math.Max(maxX - minX, 20f) * 1.25f;
            var spanY = Math.Max(maxY - minY, 20f) * 1.25f;
            var zoom = Math.Min(_mapControl.Width / spanX, _mapControl.Height / spanY);
            _mapProvider.Zoom = Math.Max(_minZoom, Math.Min(zoom, _maxZoom));
            _mapControl.Invalidate();
        }

        // -----------------------------------------------------------------------------------
        // Grid
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// The grid lists exactly what the map drew for this capture, loop trim and smoothing
        /// included - a list that disagreed with the picture would be worse than no list.
        /// </summary>
        private IEnumerable<SniffDb.Point> GridPoints(SniffDb.Capture c)
        {
            foreach (var l in _layers)
                if (ReferenceEquals(l.Capture, c)) return l.Kept;

            PathFilters.Loop ignored;
            return PointsToDraw(c, out ignored);
        }

        private void FillGridFromCapture(SniffDb.Capture c)
        {
            gridWaypoint.Rows.Clear();
            var n = 0;
            DateTime? prev = null;
            foreach (var p in GridPoints(c))
            {
                var delay = "";
                if (prev.HasValue && p.SeenUtc.HasValue)
                {
                    var ms = (p.SeenUtc.Value - prev.Value).TotalMilliseconds;
                    if (ms > 0) delay = ((int)ms).ToString(CultureInfo.InvariantCulture);
                }
                if (p.SeenUtc.HasValue) prev = p.SeenUtc;

                gridWaypoint.Rows.Add(
                    ++n,
                    p.X.ToString("0.####", CultureInfo.InvariantCulture),
                    p.Y.ToString("0.####", CultureInfo.InvariantCulture),
                    p.Z.ToString("0.####", CultureInfo.InvariantCulture),
                    p.O.HasValue ? p.O.Value.ToString("0.####", CultureInfo.InvariantCulture) : "NULL",
                    p.SeenUtc.HasValue
                        ? p.SeenUtc.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        : "",
                    delay);
            }
        }
    }
}
