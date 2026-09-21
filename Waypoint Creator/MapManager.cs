using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

public class MapConfig
{
    public int Id { get; set; }
    public string Directory { get; set; }
    public string Name { get; set; }
    public float MapSize { get; set; } = 34133.33f;
    public float TileSize { get; set; } = 533.3333f;
}

public static class MapManager
{
    private static List<MapConfig> _maps = new List<MapConfig>();
    private static string _baseTilePath = Path.Combine(Application.StartupPath, "world", "minimaps");
    private static DateTime _lastLoadTime;
    private static string _currentCsvPath;

    /// <summary>
    /// Where the minimap tiles live. An absolute path is taken as given, anything else is read
    /// relative to the executable - which is what the old build did, and still the default.
    /// The tiles are client art: several hundred megabytes that nobody should be copying into
    /// bin\Debug on every rebuild, so pointing this at one folder outside the tree is the
    /// normal case rather than the exception.
    /// </summary>
    public static void Initialize(string baseTilePath = null)
    {
        if (string.IsNullOrWhiteSpace(baseTilePath))
            baseTilePath = Path.Combine("world", "minimaps");

        _baseTilePath = Path.IsPathRooted(baseTilePath)
            ? baseTilePath
            : (Climb(baseTilePath) ?? Path.Combine(Application.StartupPath, baseTilePath));

        Console.WriteLine($"Tile base path: {_baseTilePath}");
    }

    /// <summary>
    /// A debug build runs from bin\Debug, and nobody wants half a gigabyte of client art
    /// copied there on every rebuild. Walk up from the executable looking for the folder, so
    /// world\minimaps at the root of the checkout is found without configuring anything.
    /// </summary>
    private static string Climb(string relative)
    {
        var dir = new DirectoryInfo(Application.StartupPath);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// map.csv is written beside the tiles by tools/slice_minimaps.py. Fall back to the folder
    /// next to the executable so an existing install keeps working untouched.
    /// </summary>
    public static string FindMapCsv(string baseTilePath)
    {
        var beside = Path.Combine(Application.StartupPath, "map.csv");

        // Initialize has already resolved the real folder, so ask it rather than guessing again.
        var root = _baseTilePath;
        if (string.IsNullOrWhiteSpace(root)) return beside;

        // world/minimaps -> world -> the folder holding both it and map.csv
        var up = Path.GetFullPath(Path.Combine(root, "..", "..", "map.csv"));
        if (File.Exists(up)) return up;

        var inside = Path.Combine(root, "map.csv");
        return File.Exists(inside) ? inside : beside;
    }

    public static void LoadMaps(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            Console.WriteLine($"[WARN] No map.csv at {csvPath}. Paths will draw over an empty "
                              + "grid. Run tools/slice_minimaps.py to build the tiles.");
            return;
        }

        try
        {
            _currentCsvPath = csvPath;
            _lastLoadTime = File.GetLastWriteTime(csvPath);
            _maps.Clear();

            foreach (var line in File.ReadAllLines(csvPath).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int id))
                {
                    _maps.Add(new MapConfig
                    {
                        Id = id,
                        Directory = parts[1].Trim(),
                        Name = parts.Length >= 3 ? parts[2].Trim() : null,
                        MapSize = 34133.33f,
                        TileSize = 533.3333f
                    });
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading maps: {ex.Message}");
        }
    }

    public static void ReloadIfChanged()
    {
        if (_currentCsvPath == null || !File.Exists(_currentCsvPath)) return;

        var lastWriteTime = File.GetLastWriteTime(_currentCsvPath);
        if (lastWriteTime > _lastLoadTime)
        {
            LoadMaps(_currentCsvPath);
        }
    }

    public static MapConfig GetMapConfig(int mapId) => _maps.FirstOrDefault(m => m.Id == mapId);
    public static string GetTilePath(int mapId) => GetMapConfig(mapId) != null ?
        Path.Combine(_baseTilePath, GetMapConfig(mapId).Directory) : null;

    /// <summary>The readable map name, when map.csv was written with one.</summary>
    public static string GetMapName(int mapId)
    {
        var cfg = GetMapConfig(mapId);
        if (cfg == null) return null;
        return string.IsNullOrEmpty(cfg.Name) ? cfg.Directory : cfg.Name;
    }
}