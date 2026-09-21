# ![logo](images/Fire%20Elemental.png) Waypoint Creator VMaNGOS

Fork of Waypoint Creator with main focus on handling large sniffs better.

Creates NPC paths using parsed packet files and visualizes them on the game map with SkiaSharp rendering.

## Features

- **Map Visualization**: View waypoints on game maps using SkiaSharp
- **Read the ingest database**: load every capture of an entry straight from the raw sniff
  tables, no parsed text files involved
- **All spawns at once**: each guid capture in its own colour, so the ones that path and the
  ones that wander separate on sight
- **Multi-Server Support**: VMaNGOS, TrinityCore, and CMaNGOS SQL output
- **Database Integration**: Auto-fetch creature names from your database
- **Search**: Find NPCs by searching for names or NPC entries
- **Map Navigation**: Zoom and pan around the game world
- **Multi-file Support**: Process multiple sniff files simultaneously

## Requirements

- A 3.3.5a client, for the minimap art (see below)
- Either parsed packet files from WowPacketParser, or a WowPacketParser ingest database
- Database connection (optional, for creature names)

## Minimap tiles

**The Google Drive link this README used to point at is dead.** Build the tiles from your own
client instead - `tools/slice_minimaps.py` does it, and it is quick because it reuses the
stitched PNGs that the WoWTools minimap pipeline already produces.

1. Extract `textures/Minimap/*` from the client with
   [WoWTools.Minimaps](https://github.com/Marlamin/WoWTools.Minimaps) or Ladik's MPQ Editor,
   sort the hash named BLPs into per map folders, and compile one PNG per map. If you already
   did this once, you only need the PNGs and `md5translate.trs` - the loose BLPs can stay
   deleted.
2. Pull `md5translate.trs` back out of the client. It is small, and it is the only thing that
   says where each compiled PNG sits on the world grid:

   ```
   MPQEditor.exe extract <client>\Data\patch-3.MPQ textures\Minimap\md5translate.trs <dir> /fp
   ```

3. Cut the PNGs into tiles:

   ```
   python tools/slice_minimaps.py ^
       --trs       <dir>\md5translate.trs ^
       --compiled  <your compiled PNG folder> ^
       --dbc       <client>\dbc\Map.dbc ^
       --out       world\minimaps
   ```

That writes `world/minimaps/<MapDirectory>/mapXX_YY.webp` plus a `map.csv` taken from
`Map.dbc`, which is what the viewer reads. Roughly 7,400 tiles and 50 MB for a 3.3.5a client.

The tiles are found automatically if `world/minimaps` sits anywhere from the executable up to
the root of the checkout, so a debug build needs no configuration. Set an explicit folder in
the login dialog if you keep them elsewhere.

Instance interiors have WMO minimaps rather than a grid of terrain tiles, so 37 of the maps
the corpus holds movement on will never have art. Their paths still draw, over an empty grid.

## Reading the ingest database

Put the ingest schema name in the login dialog (`wpp_ingest2` by default), type an entry id or
part of a creature name, and press **Load Entry**. The viewer reads `creature_waypoint`,
`creature_spawn` and `creature_movement` - the raws, never the published digest, because the
digest is the thing being checked.

Every guid capture of that entry is drawn at once, each in its own colour. The list on the
right is one row per capture, showing how far it ranged, how many of its segments arrived as
authored splines, how many orders it took and how long it was watched. Tick and untick to
compare, click one to bring it to the front and fill the grid with its points.

**Click a capture on the map** and everything else is unticked, which is the quickest way to
ask whose route that is - the rings and dots are the easiest targets but anywhere on the line
works, and the cursor turns into a hand when something is under it. Ctrl-click adds or removes
one instead of isolating it. Clicking empty ground does nothing, so a missed click costs you
nothing. **Select All** puts them all back; right clicking the list has the same commands plus
**Invert** and **Only this one**.

The wheel zooms around the cursor, by a fixed proportion per notch rather than a fixed amount,
so it moves at the same apparent speed whether you are looking at a continent or at the gap
between two waypoints. Hold Ctrl for finer steps.

- **Show** filters by what the packet actually was. A segment of one point is a single
  destination - wander, or a creature moving to somewhere it was told to go once. More than one
  point is a spline the server authored and sent whole. **Order destinations** throws away the
  interior of every multi-point packet, which is the navmesh corridor the server solved rather
  than anything authored; flight keeps its whole spline because nothing snapped it to the
  ground. **Points only** draws no lines at all, for when the lines have become a web.
- **Max** caps how many captures are drawn, strongest first. Some entries have thousands.

### Smooth, and why it works

Enough captures of one entry stop looking like a path and start looking like a web. The problem
is not the amount of data, it is that most of it was never authored - so **Smooth** runs the
same tests `scripts/mine-paths.sql` runs, scoped to whatever is on screen:

1. a position is kept when two separate move orders landed on it, at centimetre resolution.
   Random movement rolls a fresh float every time and never picks the same centimetre twice, so
   recurrence is the whole signal. A second lap by the same creature counts as readily as a
   second capture.
2. a step is kept when that ordered pair of kept positions was walked twice.

Nothing else is drawn, so what is left on the map is what the miner would publish. Measured on
the corpus:

| entry | positions kept | steps kept |
|---|---|---|
| 8480 Kalaran the Deceiver, a flight | 50 of 50 | 48 of 50 |
| 27500 Conquest Hold Berserker, a line patrol | 808 of 7,698 | 227 of 928 |
| 1976 Stormwind City Patroller, a city circuit | 290 of 333 | 672 of 1,457 |
| 14881 Spider, pure wander | 39,214 of 258,165 | 1,451 of 52,827 |

An authored route comes through untouched and a wanderer loses 97% of its steps, which is the
only result worth having.

### Merge

**Smooth** still draws one line per capture, which is the right picture for "did these creatures
behave the same way" and the wrong one for "what is the path". **Merge** walks the pooled
confirmed steps into ordered routes instead, strongest edge first, the way `chain-paths.py` does
it - independent captures breaking ties ahead of raw traversals, because a second person seeing
a step is better evidence than the same person seeing it twice. It turns Smooth on implicitly,
since there is nothing to walk without it.

This is what answers a fragmented entry. Each capture saw a slice of the route and none of them
saw the whole thing, but the slices overlap, and the pooled graph is the union they were all
fragments of:

- **26290 Jotun** - 82 captures, 80 sniffs, median 4 confirmed positions each. Merged: **one
  70-point route, 2,131 yd**, plus three short leftovers which are the same mountainside walked
  in the other direction (edges are directed, so the return is its own route).
- **3375 Bael'dun Foreman** - draws as a star, which is what a per-capture polyline through
  unconfirmed wander looks like when 139 captures overlap. The confirmed graph was never a star:
  51 of its 65 connected positions are degree 2, and the busiest is degree 4 with only 10% of
  captures touching it. Merged it comes out as **three closed rings** of 20, 17 and 14 points -
  three guards, three patrol loops.

A route that closes is reported as a ring only when it returns to its own first point. `A B C D
B` is a lasso, and calling that closed would have the reader draw `D → A`, a step nobody walked;
the status bar says which point the last one leads back to instead.

Click a route to bring it forward and list its points in order in the grid, which is the
publishable form. The grid carries no timings for a merged route - a merged point is a position
several captures agreed on, and the moments they each reached it are not one creature's clock.

The capture list still decides what goes into the pool, so ticking a bad capture off and
watching a route knit together is why both controls are there.

### Loop

**Loop** cuts each capture down to the circuit it repeats, so the map and the grid show one lap
instead of every lap. It looks for periodicity across the whole sequence rather than for the
first time a position comes back: a web-like capture revisits positions constantly without ever
running a circuit, and the first-revisit reading calls that a loop of length two.

A line patrol reads as its full there-and-back, which is the shortest thing it actually repeats.
The status bar says how long the circuit is and how much of the capture obeys it. Spider finds
no loop in any of its 150 largest captures, which is the answer that matters.

### Tracing a capture back

Right-click a row for **Copy sniff hash**. The hash is how a sniff is identified everywhere else
- the ingest table keys on it and file names get renamed - so it is what to quote when a capture
needs re-parsing. The guid and the file name are on the same menu.
- A hollow ring marks the spawn point and a filled dot the first order, so a creature that
  never left home and one whose capture began mid route are told apart at a glance.
- Long hops are drawn dashed rather than hidden. They are usually the creature leaving and
  re-entering visibility range, not a step it took.

Some combat movement is missing rather than filtered: the ingest drops every creature that sent
the sniffer a hostile `SMSG_AI_REACTION`, for the whole capture, patrol included. That only
covers creatures that attacked the sniffer - `AI_REACTION` goes to the client that was aggroed -
so chases by creatures fighting somebody else are still here, and still drawn.

## Output Support

- VMaNGOS (`creature_movement`)
- TrinityCore (`waypoint_path`)
- CMaNGOS (`creature_movement`)
- C++ code generation

![main_window](images/main_window.png)
