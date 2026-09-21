using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MySqlConnector;

namespace Frm_waypoint
{
    /// <summary>
    /// Reads the ingest database - the raw per packet tables, not the published digest.
    ///
    /// The digest answers "what should this creature's path be", which is the question the
    /// heuristics keep getting wrong. The raws answer "what did this creature actually do",
    /// and that is the only thing worth looking at by eye. So everything here reads
    /// creature_waypoint, creature_spawn and creature_movement, and nothing reads a wp_ or
    /// dg_ table.
    ///
    /// The unit is the GUID CAPTURE: one creature in one sniff. An entry pools many of them,
    /// and that pooling is what destroys the per spawn signal, so the viewer never pools.
    /// </summary>
    internal static class SniffDb
    {
        public static string ConnectionString()
        {
            var s = Properties.Settings.Default;
            var db = string.IsNullOrWhiteSpace(s.ingestDatabase) ? "wpp_ingest2" : s.ingestDatabase;
            return "server=" + s.host + ";port=" + s.port + ";user id=" + s.username +
                   ";password=" + s.password + ";database=" + db +
                   ";AllowUserVariables=true;DefaultCommandTimeout=300";
        }

        /// <summary>A single move order point, exactly as the packet carried it.</summary>
        public class Point
        {
            public int SegmentId;
            public int PointIndex;
            public float X, Y, Z;
            public float? O;
            public int SegmentPoints;
            public uint SplineFlags;
            public bool CreationSpline;
            public int? MoveTimeMs;
            public DateTime? SeenUtc;

            /// <summary>A segment of one point is a single destination: wander, or a chase.
            /// More than one is an authored spline the server sent whole.</summary>
            public bool IsSpline { get { return SegmentPoints > 1; } }

            /// <summary>0x400 is the airborne bit. Flight keeps every interior point.</summary>
            public bool IsAirborne { get { return (SplineFlags & 0x400) != 0; } }
        }

        /// <summary>One creature in one sniff, and everything it was seen doing.</summary>
        public class Capture
        {
            public long SniffId;
            public string Guid;
            public uint Entry;
            public uint Map;

            public int Points;
            public int Segments;
            public int SplineSegments;
            public int Pauses;
            public float Radius;
            public float RadiusRobust;
            public DateTime? FirstSeen, LastSeen;

            public bool HasSpawn;
            public float SpawnX, SpawnY, SpawnZ;

            public string SniffName;

            /// <summary>The sha256 of the sniff file, which is what identifies it everywhere
            /// else - the ingest keys on it and a re-parse is asked for by it.</summary>
            public string SniffHash;
            public string SniffBuild;

            public List<Point> Path = new List<Point>();

            public bool Loaded;

            /// <summary>The last 8 hex digits, which is what a GUID is recognised by on sight.</summary>
            public string ShortGuid
            {
                get
                {
                    if (string.IsNullOrEmpty(Guid)) return "";
                    return Guid.Length <= 8 ? Guid : Guid.Substring(Guid.Length - 8);
                }
            }

            public TimeSpan Watched
            {
                get
                {
                    if (!FirstSeen.HasValue || !LastSeen.HasValue) return TimeSpan.Zero;
                    return LastSeen.Value - FirstSeen.Value;
                }
            }

            /// <summary>
            /// What the list box shows. Deliberately the numbers that separate the behaviour
            /// types by eye: how far it ranged, how much of it arrived as authored splines,
            /// how many orders it took, and how long it was watched.
            /// </summary>
            public string Label()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0}  r{1,-7:0.#} {2,4}/{3,-4} spl {4,5} pts {5,5:0}m  #{6}",
                    ShortGuid, RadiusRobust, SplineSegments, Segments, Points,
                    Watched.TotalMinutes, SniffId);
            }
        }

        public class EntryHit
        {
            public uint Entry;
            public string Name;
            public int Captures;
            public long TotalPoints;

            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture,
                                     "{0} - {1}  ({2} captures, {3} points)",
                                     Entry, Name, Captures, TotalPoints);
            }
        }

        private static MySqlConnection Open()
        {
            var conn = new MySqlConnection(ConnectionString());
            conn.Open();
            return conn;
        }

        public static void TestConnection()
        {
            using (var conn = Open())
            using (var cmd = new MySqlCommand("SELECT 1 FROM creature_waypoint LIMIT 1", conn))
                cmd.ExecuteScalar();
        }

        /// <summary>
        /// Find entries by id or by name. creature_template is per sniff and its name follows
        /// the capturing client's locale, so one entry can carry several - take the one the most
        /// sniffs agree on, and ignore the ones that came back as question marks.
        /// </summary>
        public static List<EntryHit> SearchEntries(string term, int limit)
        {
            var hits = new List<EntryHit>();
            if (string.IsNullOrWhiteSpace(term)) return hits;
            term = term.Trim();

            uint asEntry;
            var isNumber = uint.TryParse(term, NumberStyles.Integer,
                                         CultureInfo.InvariantCulture, out asEntry);

            var sql = isNumber
                ? "SELECT entry, name, COUNT(*) agree FROM creature_template " +
                  "WHERE entry = @entry AND name IS NOT NULL AND name NOT LIKE '%?%' " +
                  "GROUP BY entry, name ORDER BY agree DESC"
                : "SELECT entry, name, COUNT(*) agree FROM creature_template " +
                  "WHERE name LIKE @like AND name NOT LIKE '%?%' " +
                  "GROUP BY entry, name ORDER BY agree DESC LIMIT 400";

            var names = new Dictionary<uint, string>();
            using (var conn = Open())
            {
                using (var cmd = new MySqlCommand(sql, conn))
                {
                    if (isNumber) cmd.Parameters.AddWithValue("@entry", asEntry);
                    else cmd.Parameters.AddWithValue("@like", "%" + term + "%");
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var e = (uint)r.GetInt64(0);
                            if (!names.ContainsKey(e)) names[e] = r.GetString(1);
                        }
                }

                // An entry with no template row is still worth offering if it moved.
                if (isNumber && names.Count == 0) names[asEntry] = "(no name in corpus)";
                if (names.Count == 0) return hits;

                var ids = string.Join(",", names.Keys.Select(
                    k => k.ToString(CultureInfo.InvariantCulture)));
                using (var cmd = new MySqlCommand(
                    "SELECT entry, COUNT(*) caps, SUM(points) pts FROM creature_movement " +
                    "WHERE entry IN (" + ids + ") GROUP BY entry ORDER BY SUM(points) DESC", conn))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var e = (uint)r.GetInt64(0);
                        hits.Add(new EntryHit
                        {
                            Entry = e,
                            Name = names.ContainsKey(e) ? names[e] : "?",
                            Captures = r.GetInt32(1),
                            TotalPoints = r.IsDBNull(2) ? 0 : r.GetInt64(2)
                        });
                    }
            }
            return hits.Take(limit).ToList();
        }

        /// <summary>
        /// Every guid capture of an entry, WITHOUT its points. creature_movement holds one row
        /// per capture and is indexed by entry, so this stays cheap even for the entries with
        /// thousands. The points come afterwards, only for the captures actually drawn.
        /// </summary>
        public static List<Capture> LoadCaptures(uint entry, int mapFilter)
        {
            var list = new List<Capture>();
            var sql =
                "SELECT m.sniff_id, m.guid, m.map, m.points, m.segments, m.multi_point_segments, " +
                "       m.pauses, m.radius, m.radius_robust, m.first_seen_utc, m.last_seen_utc, " +
                "       s.position_x, s.position_y, s.position_z " +
                "FROM creature_movement m " +
                "LEFT JOIN creature_spawn s ON s.sniff_id = m.sniff_id AND s.guid = m.guid " +
                "WHERE m.entry = @entry" + (mapFilter >= 0 ? " AND m.map = @map" : "") +
                " ORDER BY m.points DESC, m.sniff_id, m.guid";

            using (var conn = Open())
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@entry", entry);
                if (mapFilter >= 0) cmd.Parameters.AddWithValue("@map", mapFilter);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var c = new Capture
                        {
                            SniffId = r.GetInt64(0),
                            Guid = r.GetString(1),
                            Entry = entry,
                            Map = (uint)r.GetInt64(2),
                            Points = r.GetInt32(3),
                            Segments = r.GetInt32(4),
                            SplineSegments = r.GetInt32(5),
                            Pauses = r.GetInt32(6),
                            Radius = r.GetFloat(7),
                            RadiusRobust = r.GetFloat(8),
                            FirstSeen = r.IsDBNull(9) ? (DateTime?)null : r.GetDateTime(9),
                            LastSeen = r.IsDBNull(10) ? (DateTime?)null : r.GetDateTime(10),
                            HasSpawn = !r.IsDBNull(11)
                        };
                        if (c.HasSpawn)
                        {
                            c.SpawnX = r.GetFloat(11);
                            c.SpawnY = r.GetFloat(12);
                            c.SpawnZ = r.GetFloat(13);
                        }
                        list.Add(c);
                    }
            }
            return list;
        }

        /// <summary>
        /// Fill in the points for the given captures, in packet order. Keyed on (sniff_id, guid)
        /// so creature_waypoint's segment index is used and nothing scans the entry's history.
        /// </summary>
        public static void LoadPoints(IList<Capture> captures)
        {
            var todo = captures.Where(c => !c.Loaded).ToList();
            if (todo.Count == 0) return;

            var byKey = new Dictionary<string, Capture>();
            foreach (var c in todo) byKey[c.SniffId + "|" + c.Guid] = c;

            // In batches, because a row value IN list of several thousand tuples is its own problem.
            const int batch = 250;
            using (var conn = Open())
            {
                for (var off = 0; off < todo.Count; off += batch)
                {
                    var slice = todo.Skip(off).Take(batch).ToList();
                    var sb = new StringBuilder();
                    sb.Append("SELECT sniff_id, guid, segment_id, point_index, position_x, ");
                    sb.Append("position_y, position_z, orientation, segment_points, spline_flags, ");
                    sb.Append("creation_spline, move_time_ms, seen_utc FROM creature_waypoint ");
                    sb.Append("WHERE (sniff_id, guid) IN (");
                    for (var i = 0; i < slice.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("(@s").Append(i).Append(",@g").Append(i).Append(')');
                    }
                    sb.Append(") ORDER BY sniff_id, guid, segment_id, point_index");

                    using (var cmd = new MySqlCommand(sb.ToString(), conn))
                    {
                        for (var i = 0; i < slice.Count; i++)
                        {
                            cmd.Parameters.AddWithValue("@s" + i, slice[i].SniffId);
                            cmd.Parameters.AddWithValue("@g" + i, slice[i].Guid);
                        }
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                Capture c;
                                var key = r.GetInt64(0) + "|" + r.GetString(1);
                                if (!byKey.TryGetValue(key, out c)) continue;
                                c.Path.Add(new Point
                                {
                                    SegmentId = r.GetInt32(2),
                                    PointIndex = r.GetInt32(3),
                                    X = r.GetFloat(4),
                                    Y = r.GetFloat(5),
                                    Z = r.GetFloat(6),
                                    O = r.IsDBNull(7) ? (float?)null : r.GetFloat(7),
                                    SegmentPoints = r.GetInt32(8),
                                    SplineFlags = (uint)r.GetInt64(9),
                                    CreationSpline = r.GetBoolean(10),
                                    MoveTimeMs = r.IsDBNull(11) ? (int?)null : r.GetInt32(11),
                                    SeenUtc = r.IsDBNull(12) ? (DateTime?)null : r.GetDateTime(12)
                                });
                            }
                    }
                    foreach (var c in slice) c.Loaded = true;
                }
            }
        }

        /// <summary>Which file a capture came from: its name, its hash and what client wrote
        /// it. The hash is the identity - file names get renamed, the sha256 does not.</summary>
        public static void LoadSniffInfo(IList<Capture> captures)
        {
            var ids = captures.Select(c => c.SniffId).Distinct().ToList();
            if (ids.Count == 0) return;

            var info = new Dictionary<long, string[]>();
            using (var conn = Open())
            using (var cmd = new MySqlCommand(
                "SELECT id, file_name, file_hash, client_version, client_build " +
                "FROM sniff WHERE id IN (" +
                string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) +
                ")", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    info[r.GetInt64(0)] = new[]
                    {
                        r.IsDBNull(1) ? "" : r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        (r.IsDBNull(3) ? "" : r.GetString(3)) +
                            (r.IsDBNull(4) ? "" : " (" + r.GetInt32(4).ToString(CultureInfo.InvariantCulture) + ")")
                    };

            foreach (var c in captures)
            {
                string[] v;
                if (!info.TryGetValue(c.SniffId, out v)) continue;
                c.SniffName = v[0];
                c.SniffHash = v[1];
                c.SniffBuild = v[2];
            }
        }
    }
}
