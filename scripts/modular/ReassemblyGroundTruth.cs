using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FileAccess = Godot.FileAccess;

/// <summary>
/// 权威 Mod 导出覆盖层。catalog.json 仍承载广泛的舰船引用；本文件承载
/// 2025-07-07 从 game/TheBlocks.h 导出的玩家派系 47-69 号完整字段。
/// </summary>
public static partial class ShipShapeLibrary
{
    private const string GroundTruthPath = "res://data/reassembly/ground_truth_blocks.json";

    private static void ApplyGroundTruthCatalog(Dictionary<string, ShapeSpec> shapes)
    {
        if (!FileAccess.FileExists(GroundTruthPath))
            return;

        using var document = JsonDocument.Parse(FileAccess.GetFileAsString(GroundTruthPath));
        if (!document.RootElement.TryGetProperty("blocks", out var blocks))
            return;

        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var numId))
                continue;
            var id = $"ra_{numId}";
            var featureNames = ReadStringArray(block, "features_list");
            var nativeShape = ReadString(block, "shape");
            // TheBlocks.h omits shape for the ordinary square primitive; Lua's
            // serializer relies on the same default. Keep an explicit name in
            // the runtime spec so editors and exporters never lose geometry.
            if (string.IsNullOrWhiteSpace(nativeShape)) nativeShape = "SQUARE";
            var name = ReadString(block, "name");
            var description = ReadString(block, "blurb");
            var scale = Math.Clamp(ReadInt(block, "scale", 1), 1, 4);
            var stats = block.TryGetProperty("stats", out var statsValue) ? statsValue : default;
            var colors = block.TryGetProperty("colors", out var colorsValue) ? colorsValue : default;
            var sub = block.TryGetProperty("sub", out var subValue) ? subValue : default;
            var flags = ConvertNativeFeatures(featureNames);
            var color = ReadHex(colors, "fillColor", 0x718B9C);

            var geometry = GroundTruthGeometry(id, numId, name, nativeShape, color, flags, scale, ReadFloat(stats, "thrusterForce"));
            var cannon = ReadCannon(block);
            var laser = ReadLaser(block);
            var shield = ReadShield(block);
            var replicateBlock = ReadReplicateBlock(block);
            var shrouds = ReadShrouds(block);
            if (cannon is not null) flags |= BlockFeatures.Weapon;
            if (shield is not null) flags |= BlockFeatures.Shield;

            var weaponKind = cannon is null ? WeaponKind.Cannon
                : featureNames.Contains("AUTOFIRE", StringComparer.Ordinal) ? WeaponKind.PointDefense : WeaponKind.Cannon;
            var damage = cannon?.Damage ?? ReadFloat(stats, "damage");
            var range = cannon?.Range ?? ReadFloat(stats, "range");
            var projectileSpeed = cannon?.MuzzleVelocity ?? ReadFloat(stats, "muzzleVel");
            var rounds = cannon?.RoundsPerSec ?? 0;
            var durability = ReadFloat(stats, "durability");
            var density = ReadFloat(stats, "density", 0.15f);
            var hull = Math.Max(20, durability * 100);
            var thrust = ReadFloat(stats, "thrusterForce");
            var torquer = ReadFloat(stats, "torquerTorque");
            var generation = ReadFloat(stats, "generatorCapacityPerSec");
            var powerCapacity = ReadFloat(stats, "powerCapacity");
            var shieldStrength = shield?.Strength ?? 0;
            var shieldRadius = shield?.Radius ?? Math.Max(80, geometry.Radius * 2);
            var shieldRegen = shield?.Regen ?? 0;
            var capacity = ReadFloat(stats, "capacity");
            var points = ReadFloat(stats, "points");
            var activatePower = ReadFloat(stats, "activatePower");
            var finForce = ReadFloat(stats, "finForce");

            shapes[id] = new ShapeSpec
            {
                Id = id,
                NumId = numId,
                Label = string.IsNullOrWhiteSpace(name) ? $"方块 {numId}" : name,
                Category = CategoryFor(flags),
                ColorUint = color,
                FillColorUint = color,
                FillColor1Uint = ReadHex(colors, "fillColor1", color),
                LineColorUint = ReadHex(colors, "lineColor", color),
                ThrusterColorUint = ReadHex(colors, "thrusterColor", 0x90ECECEC),
                ThrusterColor1Uint = ReadHex(colors, "thrusterColor1", 0x904D4D4D),
                NativeShapeName = nativeShape,
                NativeFeatures = featureNames,
                NativeDefinitionJson = block.GetRawText(),
                Features = flags,
                VerticesField = geometry.Vertices(),
                Ports = geometry.Ports,
                Radius = geometry.Radius,
                Scale = scale,
                Sides = geometry.Sides,
                ExhaustPosition = geometry.ExhaustPosition,
                ExhaustHalfWidth = geometry.ExhaustHalfWidth,
                Mass = Math.Max(1, density * Math.Max(1, points)),
                Hull = hull,
                Durability = durability,
                Density = density,
                Capacity = capacity,
                Points = points,
                Thrust = thrust,
                TorquerTorque = torquer,
                Damage = damage,
                Description = description,
                SourceFaction = 20,
                NativeShape = 0,
                // blocks.lua confirms module attributes, not the native shape
                // vertices. Keep this flag tied to executable geometry evidence.
                ReferenceGeometry = geometry.HasVerifiedGeometry,
                HasVerifiedGeometry = geometry.HasVerifiedGeometry,
                IsRegistered = true,
                WeaponKind = weaponKind,
                Range = range > 0 ? range : 1000,
                ProjectileSpeed = projectileSpeed > 0 ? projectileSpeed : 900,
                FireInterval = rounds > 0 ? 1.0f / rounds : 0.3f,
                FireEnergy = cannon?.Power > 0 ? cannon.Power : 4,
                SplashRadius = cannon?.ExplodeRadius ?? 0,
                Generation = generation,
                GeneratorCapacityPerSec = generation,
                PowerCapacity = powerCapacity,
                Shield = shield,
                ShieldStrength = shieldStrength,
                ShieldRadius = shieldRadius,
                ShieldRegen = shieldRegen,
                Cannon = cannon,
                Laser = laser,
                ReplicateBlock = replicateBlock,
                Shrouds = shrouds,
                BarrelOffset = ReadVector2(sub, "barrelOffset"),
                BarrelTaper = ReadFloat(stats, "barrelTaper", cannon?.BarrelTaper ?? 1.0f),
                GrowRate = ReadFloat(stats, "growRate"),
                TractorRange = ReadFloat(stats, "tractorRange"),
                ReplicateTime = ReadFloat(stats, "replicateTime"),
                LauncherPower = ReadFloat(stats, "launcherPower"),
                LauncherOutSpeed = ReadFloat(stats, "launcherOutSpeed"),
                TurretSpeed = ReadFloat(stats, "turretSpeed"),
                TurretLimit = ReadFloat(stats, "turretLimit"),
                ThrusterBoost = ReadFloat(stats, "thrusterBoost"),
                ThrusterBoostTime = ReadFloat(stats, "thrusterBoostTime"),
                Sort = ReadFloat(stats, "sort"),
                ActivatePower = activatePower,
                FinForce = finForce,
                HasGroundTruth = true,
                ThrustDirection = Vector2.Right,
                WeaponDirection = Vector2.Right,
            };
        }
    }

    private static ShapeSpec GroundTruthGeometry(string id, int numId, string name, string nativeShape,
        uint color, BlockFeatures features, int scale, float thrust)
    {
        var normalized = nativeShape.ToUpperInvariant();
        if (normalized.Length == 0) normalized = "RECT";
        if (normalized == "SQUARE") normalized = "RECT";
        if (!NativeShapeGeometry.TryGet(normalized, scale, out var native))
            throw new InvalidOperationException($"Missing native geometry for shape {normalized} scale {scale} in data/reassembly/native_shape_geometry.json");
        return MakePolygon(id, numId, name, normalized, color, features, scale,
            native.Vertices, native.Ports, thrust, native.ExhaustPosition, native.ExhaustHalfWidth, native.Verified);
    }

    private static ShapeSpec MakePolygon(string id, int numId, string name, string nativeShape, uint color,
        BlockFeatures features, int scale, Vector2[] vertices, List<Port> ports, float thrust,
        Vector2 exhaustPosition, float exhaustHalfWidth, bool verified)
    {
        var centroid = vertices.Aggregate(Vector2.Zero, (sum, vertex) => sum + vertex) / vertices.Length;
        vertices = vertices.Select(vertex => vertex - centroid).ToArray();
        var translatedPorts = ports.Select(port => port with { Offset = port.Offset - centroid }).ToList();
        return new ShapeSpec
        {
            Id = id, NumId = numId, Label = name, Category = CategoryFor(features), ColorUint = color,
            IsRegistered = true,
            Features = features, NativeShapeName = nativeShape, Scale = scale, Sides = vertices.Length,
            VerticesField = vertices, Ports = translatedPorts, Radius = vertices.Max(v => v.Length()),
            Mass = 5 * scale, Hull = 20 * scale, Thrust = thrust,
            ExhaustPosition = exhaustPosition - centroid, ExhaustHalfWidth = exhaustHalfWidth,
            WeaponDirection = Vector2.Right, ThrustDirection = Vector2.Right, HasVerifiedGeometry = verified,
        };
    }

    private static BlockFeatures ConvertNativeFeatures(IEnumerable<string> names)
    {
        var flags = BlockFeatures.None;
        foreach (var name in names)
            flags |= name switch
            {
                "COMMAND" => BlockFeatures.Command,
                "GENERATOR" => BlockFeatures.Generator,
                "THRUSTER" => BlockFeatures.Thruster,
                "TORQUER" => BlockFeatures.Torquer,
                "TURRET" => BlockFeatures.Turret,
                "CANNON" => BlockFeatures.Weapon,
                "LAUNCHER" => BlockFeatures.Launcher | BlockFeatures.Weapon,
                "SHIELD" => BlockFeatures.Shield,
                "FACTORY" => BlockFeatures.Factory,
                "ASSEMBLER" => BlockFeatures.Assembler,
                "TRACTOR" => BlockFeatures.Tractor,
                "ACTIVATE" => BlockFeatures.Activate,
                "FIN" => BlockFeatures.Fin,
                "AUTOFIRE" => BlockFeatures.Autofire,
                "EXPLODE" => BlockFeatures.Explode,
                _ => BlockFeatures.None,
            };
        return flags;
    }

    private static string CategoryFor(BlockFeatures flags) =>
        flags.HasFlag(BlockFeatures.Command) ? "核心" : flags.HasFlag(BlockFeatures.Weapon) ? "武器"
        : flags.HasFlag(BlockFeatures.Thruster) ? "机动" : flags.HasFlag(BlockFeatures.Shield) ? "防御"
        : flags.HasFlag(BlockFeatures.Factory) ? "制造" : flags.HasFlag(BlockFeatures.Generator) ? "能源"
        : flags.HasFlag(BlockFeatures.Tractor) ? "物流" : "骨架";

    private static string ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string[] ReadStringArray(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToArray()
            : Array.Empty<string>();

    private static int ReadInt(JsonElement element, string property, int fallback)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static float ReadFloat(JsonElement element, string property, float fallback = 0)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetSingle(out var result)
            ? result : fallback;

    private static uint ReadHex(JsonElement element, string property, uint fallback)
    {
        var value = ReadString(element, property);
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(value[2..],
            System.Globalization.NumberStyles.HexNumber, null, out var result) ? result : fallback;
    }

    private static ShapeSpec.CannonSpec? ReadCannon(JsonElement block)
    {
        if (!block.TryGetProperty("sub", out var sub) || !sub.TryGetProperty("cannon", out var cannon)) return null;
        return new ShapeSpec.CannonSpec
        {
            RoundsPerSec = ReadFloat(cannon, "roundsPerSec"), RoundsPerBurst = ReadInt(cannon, "roundsPerBurst", 1),
            Burstyness = ReadFloat(cannon, "burstyness"), MuzzleVelocity = ReadFloat(cannon, "muzzleVel"),
            Spread = ReadFloat(cannon, "spread"), RangeStdDev = ReadFloat(cannon, "rangeStdDev"), Power = ReadFloat(cannon, "power", 4),
            Damage = ReadFloat(cannon, "damage"), Range = ReadFloat(cannon, "range", 1000), ExplodeRadius = ReadFloat(cannon, "explodeRadius"),
            Explosive = ReadString(cannon, "explosive"), ColorUint = ReadHex(cannon, "color", 0x5A8E09),
            ProjectileSize = ReadFloat(cannon, "projectileSize"), BarrelTaper = ReadFloat(cannon, "barrelTaper", 1.0f),
        };
    }

    private static ShapeSpec.LaserSpec? ReadLaser(JsonElement block)
    {
        if (!block.TryGetProperty("sub", out var sub) || !sub.TryGetProperty("laser", out var laser)) return null;
        return new ShapeSpec.LaserSpec
        {
            PulsesPerSec = ReadFloat(laser, "pulsesPerSec"), PulsesPerBurst = ReadInt(laser, "pulsesPerBurst", 1),
            Burstyness = ReadFloat(laser, "burstyness"), PulseAvailability = ReadFloat(laser, "pulseAvailability"),
            Decay = ReadFloat(laser, "decay", 0.35f), Power = ReadFloat(laser, "power"), Width = ReadFloat(laser, "width"),
            Damage = ReadFloat(laser, "damage"), ColorUint = ReadHex(laser, "color", 0xFFFFFF),
            Range = ReadFloat(laser, "range"), ExplodeRadius = ReadFloat(laser, "explodeRadius"),
            ImmobilizeForce = ReadFloat(laser, "immobilizeForce"), LinearForce = ReadFloat(laser, "linearForce"),
            BarrelTaper = ReadFloat(laser, "barrelTaper"),
        };
    }

    private static ShapeSpec.ShieldSpec? ReadShield(JsonElement block)
    {
        if (!block.TryGetProperty("sub", out var sub) || !sub.TryGetProperty("shield", out var shield)) return null;
        return new ShapeSpec.ShieldSpec
        {
            Strength = ReadFloat(shield, "strength"), Regen = ReadFloat(shield, "regen"), Delay = ReadFloat(shield, "delay"),
            Radius = ReadFloat(shield, "radius", 100), ColorUint = ReadHex(shield, "color", 0x190D141A),
            LineColorUint = ReadHex(shield, "lineColor", 0xC19272F), DamagedColorUint = ReadHex(shield, "damagedColor", 0x33293E4D),
        };
    }

    private static ShapeSpec.ReplicateBlockSpec? ReadReplicateBlock(JsonElement block)
    {
        if (!block.TryGetProperty("sub", out var sub) || !sub.TryGetProperty("replicateBlock", out var payload)) return null;
        return new ShapeSpec.ReplicateBlockSpec
        {
            ShapeName = ReadString(payload, "shape"),
            Lifetime = ReadFloat(payload, "lifetime"),
            Features = ConvertNativeFeatures(ReadStringArray(payload, "features_list")),
            Capacity = ReadFloat(payload, "capacity"), Durability = ReadFloat(payload, "durability"),
            Density = ReadFloat(payload, "density"), ThrustForce = ReadFloat(payload, "thrusterForce"),
            TorquerTorque = ReadFloat(payload, "torquerTorque"), ExplodeDamage = ReadFloat(payload, "explodeDamage"),
            ExplodeRadius = ReadFloat(payload, "explodeRadius"), FillColorUint = ReadHex(payload, "fillColor", 0),
            FillColor1Uint = ReadHex(payload, "fillColor1", 0), LineColorUint = ReadHex(payload, "lineColor", 0),
            ThrusterColorUint = ReadHex(payload, "thrusterColor", 0), ThrusterColor1Uint = ReadHex(payload, "thrusterColor1", 0),
        };
    }

    private static IReadOnlyList<ShapeSpec.ShroudSpec> ReadShrouds(JsonElement block)
    {
        if (!block.TryGetProperty("sub", out var sub) || !sub.TryGetProperty("shroud", out var shrouds) ||
            shrouds.ValueKind != JsonValueKind.Array)
            return Array.Empty<ShapeSpec.ShroudSpec>();
        return shrouds.EnumerateArray().Select(shroud => new ShapeSpec.ShroudSpec
        {
            Size = ReadVector2(shroud, "size"), Offset = ReadVector3(shroud, "offset"),
            Taper = ReadFloat(shroud, "taper"), TriColorId = ReadInt(shroud, "tri_color_id", 0),
            TriColor1Id = ReadInt(shroud, "tri_color1_id", 0),
        }).ToArray();
    }

    private static Vector2 ReadVector2(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2)
            return Vector2.Zero;
        return new Vector2(value[0].GetSingle(), value[1].GetSingle());
    }

    private static Vector3 ReadVector3(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return Vector3.Zero;
        return new Vector3(value.GetArrayLength() > 0 ? value[0].GetSingle() : 0,
            value.GetArrayLength() > 1 ? value[1].GetSingle() : 0,
            value.GetArrayLength() > 2 ? value[2].GetSingle() : 0);
    }
}
