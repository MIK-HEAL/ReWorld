using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileAccess = Godot.FileAccess;

public enum WeaponKind { Cannon, Beam, Missile, Drone, Healer, PointDefense }

/// <summary>Local reference import. Keep source IDs and evidence gaps explicit; never silently drop a ship block.</summary>
public static partial class ShipShapeLibrary
{
    public sealed class CatalogEntry
    {
        public int Id { get; set; }
        public int Faction { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public uint Color { get; set; } = 0x718B9C;
        public ulong Features { get; set; }
        public int Shape { get; set; } = 1;
        public int Scale { get; set; } = 1;
        public float Thrust { get; set; }
        public float Damage { get; set; }
        public float Range { get; set; }
        public float Speed { get; set; }
        public bool Registration { get; set; }
    }

    public static int RegisteredCount { get; private set; }
    public static int ReferencedCount { get; private set; }
    private const string CatalogPath = "res://data/reassembly/catalog.json";

    private static Dictionary<string, ShapeSpec> LoadCatalog(Dictionary<string, ShapeSpec> shapes)
    {
        List<CatalogEntry> entries;
        if (FileAccess.FileExists(CatalogPath))
            entries = JsonSerializer.Deserialize<List<CatalogEntry>>(FileAccess.GetFileAsString(CatalogPath)) ?? new();
        else
            entries = ImportLocalReference();
        RegisteredCount = entries.Count(e => e.Registration);
        ReferencedCount = entries.Count;
        foreach (var entry in entries)
        {
            // Keep the existing ReWorld equipment and its verified engine/core geometry stable.
            if (shapes.ContainsKey($"ra_{entry.Id}")) continue;
            var flags = ConvertFeatures(entry.Features);
            var name = string.IsNullOrWhiteSpace(entry.Name) ? $"方块 {entry.Id}" : entry.Name;
            if (entry.Thrust > 0) flags |= BlockFeatures.Thruster;
            if (entry.Damage != 0 || (entry.Features & (64UL | 128UL | 2048UL)) != 0) flags |= BlockFeatures.Weapon;
            var kind = entry.Damage < 0 ? WeaponKind.Healer : (entry.Features & 128) != 0 ? WeaponKind.Beam
                : (entry.Features & 2048) != 0 ? (name.Contains("Drone", StringComparison.OrdinalIgnoreCase) ? WeaponKind.Drone : WeaponKind.Missile)
                : name.Contains("Defense", StringComparison.OrdinalIgnoreCase) ? WeaponKind.PointDefense : WeaponKind.Cannon;
            var sides = entry.Shape switch { 2 => 3, 3 => 6, 5 => 6, 6 or 13 => 5, 7 => 3, 49 => 7, 60 or 61 => 4, _ => 4 };
            var scale = Math.Clamp(entry.Scale, 1, 4);
            var geometry = MakeRegular("reference", entry.Id, name, "", entry.Color, flags, sides,
                nativeScale: scale, isRegistered: entry.Registration);
            if (entry.Shape == 8) geometry = MakeCommand(entry.Registration);
            if (entry.Shape == 18) geometry = MakeThruster("reference", entry.Id, name, scale,
                scale <= 2 ? 4.5f : scale == 3 ? 7.5f : 10, scale <= 2 ? 4.194999695f : scale == 3 ? 10 : 15,
                scale <= 2 ? 130 : 150, entry.Thrust, 5, 25, entry.Registration);
            var category = (flags & BlockFeatures.Command) != 0 ? "核心" : (flags & BlockFeatures.Weapon) != 0 ? "武器"
                : (flags & BlockFeatures.Thruster) != 0 ? "机动" : (flags & BlockFeatures.Shield) != 0 ? "防御"
                : (flags & BlockFeatures.Generator) != 0 ? "能源" : (flags & BlockFeatures.Factory) != 0 ? "制造"
                : (flags & BlockFeatures.Tractor) != 0 ? "物流" : "骨架";
            var id = $"ra_{entry.Id}";
            shapes.Add(id, new ShapeSpec
            {
                Id = id, NumId = entry.Id, Label = name, Category = category, ColorUint = entry.Color,
                IsRegistered = entry.Registration,
                SourceFaction = entry.Faction, NativeShape = entry.Shape,
                ReferenceGeometry = entry.Shape is 8 or 18,
                Description = entry.Description + (entry.Registration ? "" : " · 原舰引用的方块；完整规格待还原"),
                Features = flags, VerticesField = geometry.Vertices(), Ports = geometry.Ports,
                Radius = geometry.Radius, Scale = scale, Sides = sides, ExhaustPosition = geometry.ExhaustPosition,
                ExhaustHalfWidth = geometry.ExhaustHalfWidth,
                Mass = Math.Max(2, scale * scale * 5), Hull = Math.Max(20, scale * scale * 35), Thrust = entry.Thrust,
                TorquerTorque = (flags & BlockFeatures.Torquer) != 0 ? 120_000 : 0,
                WeaponKind = kind, Damage = Math.Abs(entry.Damage) > 0 ? Math.Abs(entry.Damage) * 0.12f : 8,
                Range = entry.Range > 0 ? entry.Range : 1000, ProjectileSpeed = entry.Speed > 0 ? entry.Speed : 900,
                FireInterval = kind is WeaponKind.Beam or WeaponKind.Healer ? 0.1f : kind is WeaponKind.Missile or WeaponKind.Drone ? 1.2f : 0.3f,
                FireEnergy = kind is WeaponKind.Missile or WeaponKind.Drone ? 12 : 4,
                SplashRadius = kind == WeaponKind.Missile ? 45 : 0,
                Generation = (flags & BlockFeatures.Generator) != 0 ? 30 * scale : 0,
                PowerCapacity = (flags & BlockFeatures.Generator) != 0 ? 120 * scale : 0,
                ShieldStrength = 100 * scale, ShieldRadius = Math.Max(80, geometry.Radius * 2), ShieldRegen = 10 * scale,
            });
        }
        ApplyGroundTruthCatalog(shapes);
        RegisteredCount += shapes.Values.Count(s => s.NumId is >= 47 and <= 69);
        ReferencedCount = shapes.Values.Count;
        return shapes;
    }

    private static BlockFeatures ConvertFeatures(ulong features)
    {
        var result = BlockFeatures.None;
        foreach (var pair in new (ulong Native, BlockFeatures Internal)[] {
            (1, BlockFeatures.Command), (2, BlockFeatures.Thruster), (4, BlockFeatures.Generator),
            (16, BlockFeatures.Turret), (64, BlockFeatures.Weapon), (128, BlockFeatures.Weapon),
            (256, BlockFeatures.Torquer), (512, BlockFeatures.Shield), (2048, BlockFeatures.Launcher),
            (32768, BlockFeatures.Tractor), (1048576, BlockFeatures.Factory), (8388608, BlockFeatures.Generator) })
            if ((features & pair.Native) != 0) result |= pair.Internal;
        return result;
    }

    /// <summary>Imports all valid registrations plus every ID referenced by bundled ships/fonts/agents.
    /// The old static decoder contains null IDs and flattened subobject fields; those are not treated as verified specs.</summary>
    private static List<CatalogEntry> ImportLocalReference()
    {
        const string decoded = "res://Reassembly/analysis/out/blocks_decoded.json";
        if (!FileAccess.FileExists(decoded))
            throw new InvalidOperationException("Missing data/reassembly/catalog.json. Run the reference import checks before exporting.");
        var entries = new Dictionary<int, CatalogEntry>();
        using var document = JsonDocument.Parse(FileAccess.GetFileAsString(decoded));
        CatalogEntry? previous = null;
        foreach (var block in document.RootElement.GetProperty("blocks").EnumerateArray())
        {
            if (block.GetProperty("id").ValueKind != JsonValueKind.Number || !block.GetProperty("id").TryGetInt32(out var id) || id < 100) continue;
            var stack = block.GetProperty("stack");
            var fields = block.GetProperty("fields");
            var faction = block.GetProperty("r8d").ValueKind == JsonValueKind.Number ? block.GetProperty("r8d").GetInt32() : previous?.Faction ?? 0;
            var inherit = previous?.Faction == faction && stack.EnumerateObject().Count() == 0;
            var shape = stack.TryGetProperty("40", out var s) && s.TryGetInt32(out var shapeId) && shapeId is > 0 and < 72 ? shapeId : inherit ? previous!.Shape : 1;
            var features = stack.TryGetProperty("48", out var f) && f.TryGetUInt64(out var mask) ? mask : inherit ? previous!.Features : 0;
            string Str(string key) => fields.TryGetProperty(key, out var v) && v[0].GetString() == "str" ? v[1].GetString() ?? "" : "";
            float Float(string key) => fields.TryGetProperty(key, out var v) && v[0].GetString() == "f32" ? v[1].GetSingle() : 0;
            var color = block.GetProperty("r9d").ValueKind == JsonValueKind.Number ? block.GetProperty("r9d").GetUInt32() : previous?.Color ?? 0x718B9C;
            var name = Str("16");
            previous = new CatalogEntry { Id = id, Faction = faction, Shape = shape, Features = features,
                Color = color, Name = name, Description = Str("40"), Registration = true,
                Scale = fields.TryGetProperty("8", out var sc) && sc[0].GetString() == "u8" ? Math.Clamp(sc[1].GetInt32(), 1, 4) : 1,
                Thrust = (features & 2) != 0 ? Math.Max(Float("52"), inherit ? previous!.Thrust : 0) : 0,
                Damage = (features & (64UL | 128UL)) != 0 ? Float("28") : 0,
                Range = (features & 128) != 0 ? Float("36") : Float("32"), Speed = Float("12") };
            entries[id] = previous;
        }
        var root = ProjectSettings.GlobalizePath("res://Reassembly/reference");
        foreach (var folder in new[] { "ships", "extra_ships", "blockfonts", "backup_agents" })
        foreach (var path in System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(root, folder), "*.lua", System.IO.SearchOption.AllDirectories))
        foreach (Match match in Regex.Matches(System.IO.File.ReadAllText(path), @"\{\s*(\d+)\s*,\s*\{"))
        {
            var id = int.Parse(match.Groups[1].Value);
            if (entries.ContainsKey(id)) continue;
            var faction = id >= 10000 ? id / 1000 : id >= 5000 ? id / 1000 : id / 100;
            entries[id] = new CatalogEntry { Id = id, Faction = faction, Name = $"结构变体 {id}" };
        }
        var result = entries.Values.OrderBy(e => e.Id).ToList();
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://data/reassembly"));
        using var output = FileAccess.Open(CatalogPath, FileAccess.ModeFlags.Write);
        output?.StoreString(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }
}
