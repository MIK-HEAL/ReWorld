using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FileAccess = Godot.FileAccess;

/// <summary>
/// Runtime reader for geometry emitted by the offline Reassembly extractor.
/// Shape vertices and ports intentionally live in data/reassembly so the
/// executable evidence can be regenerated without editing simulation code.
/// </summary>
internal static class NativeShapeGeometry
{
    private const string Path = "res://data/reassembly/native_shape_geometry.json";
    private sealed record ShapeRecord(JsonElement Vertices, JsonElement Ports, Dictionary<string, JsonElement>? Variants);
    private static readonly Dictionary<string, ShapeRecord> Records = Load();

    internal readonly record struct Geometry(Vector2[] Vertices, List<ShipShapeLibrary.Port> Ports, bool Verified);

    internal static bool TryGet(string shape, int scale, out Geometry geometry)
    {
        geometry = default;
        if (!Records.TryGetValue(shape, out var record)) return false;
        var verticesElement = record.Vertices;
        var scaleFactor = Math.Max(1, scale);
        if (record.Variants is not null && record.Variants.TryGetValue(scaleFactor.ToString(), out var variant))
        {
            verticesElement = variant;
            scaleFactor = 1;
        }
        if (verticesElement.ValueKind != JsonValueKind.Array || verticesElement.GetArrayLength() < 3)
            return false;

        var vertices = verticesElement.EnumerateArray()
            .Select(pair => new Vector2(pair[0].GetSingle() * scaleFactor, pair[1].GetSingle() * scaleFactor))
            .ToArray();
        var signedArea = 0.0f;
        for (var i = 0; i < vertices.Length; i++)
            signedArea += vertices[i].Cross(vertices[(i + 1) % vertices.Length]);

        var ports = new List<ShipShapeLibrary.Port>();
        if (record.Ports.ValueKind == JsonValueKind.Array)
        {
            foreach (var port in record.Ports.EnumerateArray())
            {
                if (!port.TryGetProperty("edge", out var edgeValue) || !edgeValue.TryGetInt32(out var edge))
                    continue;
                if (edge < 0 || edge >= vertices.Length) continue;
                var a = vertices[edge];
                var b = vertices[(edge + 1) % vertices.Length];
                var tangent = b - a;
                if (tangent.LengthSquared() < 0.000001f) continue;
                var normal = signedArea >= 0 ? new Vector2(tangent.Y, -tangent.X) : new Vector2(-tangent.Y, tangent.X);
                var type = ParseType(port.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null);
                var canAttach = !port.TryGetProperty("canAttach", out var attachValue) || attachValue.GetBoolean();
                ports.Add(new ShipShapeLibrary.Port((a + b) * 0.5f, normal.Normalized(), type, canAttach));
            }
        }
        if (ports.Count == 0) return false;
        geometry = new Geometry(vertices, ports, true);
        return true;
    }

    private static ShipShapeLibrary.PortType ParseType(string? value) => value?.ToUpperInvariant().Replace("_", "") switch
    {
        "WEAPONOUT" => ShipShapeLibrary.PortType.WeaponOut,
        "WEAPONIN" => ShipShapeLibrary.PortType.WeaponIn,
        "THRUSTEROUT" => ShipShapeLibrary.PortType.ThrusterOut,
        "THRUSTERIN" => ShipShapeLibrary.PortType.ThrusterIn,
        _ => ShipShapeLibrary.PortType.Normal,
    };

    private static Dictionary<string, ShapeRecord> Load()
    {
        var result = new Dictionary<string, ShapeRecord>(StringComparer.OrdinalIgnoreCase);
        if (!FileAccess.FileExists(Path)) return result;
        using var document = JsonDocument.Parse(FileAccess.GetFileAsString(Path));
        if (!document.RootElement.TryGetProperty("shapes", out var shapes) || shapes.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in shapes.EnumerateObject())
        {
            var value = property.Value;
            if (!value.TryGetProperty("vertices", out var vertices) || !value.TryGetProperty("ports", out var ports))
                continue;
            Dictionary<string, JsonElement>? variants = null;
            if (value.TryGetProperty("variants", out var variantsValue) && variantsValue.ValueKind == JsonValueKind.Object)
                variants = variantsValue.EnumerateObject().ToDictionary(item => item.Name, item => item.Value,
                    StringComparer.OrdinalIgnoreCase);
            result[property.Name] = new ShapeRecord(vertices.Clone(), ports.Clone(),
                variants?.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.OrdinalIgnoreCase));
        }
        return result;
    }
}
