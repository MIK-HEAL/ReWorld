using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// =============================================================================
// 照搬 Reassembly 逆向序列化（见 Reassembly/scripts/Serialization.cs）
//   - SerialBlock：{Id, {x,y}, angle, 可选 command={faction=...}}
//   - ShipDesign：{data={name, author, color0, color1}, blocks={...}}
// 舰船 .lua 文件格式（真实例子见 Reassembly/reference/extra_ships/*.lua）：
//   {data={name="Spamaser", author="2", color0=0x800030, color1=0x303030}, blocks={
//       {200, {-11.827, 0.000}, 0.000, command={faction=2}},
//       {410, {125.349, -145.000}, 0.000},
//       ...
//   }}
// =============================================================================

/// <summary>序列化方块（照搬逆向 SerialBlock）。</summary>
public sealed class SerialBlock
{
    public int Id { get; set; }              // Reassembly TheShapes 数字 ID
    public Vector2 Position { get; set; }
    public float Angle { get; set; }
    public int? CommandFaction { get; set; }  // command={faction=...}，仅核心方块有
    public uint? FillColor { get; set; }
    public uint? LineColor { get; set; }

    public override string ToString() => $"{Id}@({Position.X:0},{Position.Y:0})";
}

/// <summary>舰船设计数据（照搬逆向 Blueprint/ShipDesign：name + blocks 列表 + 颜色）。</summary>
public sealed class ShipDesign
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public uint Color0 { get; set; } = 0x404040;
    public uint Color1 { get; set; } = 0x202020;
    public List<SerialBlock> Blocks { get; set; } = new();

    /// <summary>从 ModularShip 导出为设计（照搬逆向 toShipDesign）。</summary>
    public static ShipDesign FromShip(ModularShip ship, string name = "Custom", uint color0 = 0x404040, uint color1 = 0x202020)
    {
        var design = new ShipDesign { Name = name, Color0 = color0, Color1 = color1 };
        foreach (var block in ship.Blocks)
        {
            var sb = new SerialBlock
            {
                Id = block.NumId,
                Position = block.Position,
                Angle = block.Angle,
            };
            if (block.Spec.IsCore)
                sb.CommandFaction = FactionRelations.Player;
            design.Blocks.Add(sb);
        }
        return design;
    }

    /// <summary>从设计导入到 ModularShip（照搬逆向 fromShipDesign）。</summary>
    public ModularShip ToShip()
    {
        var ship = new ModularShip();
        foreach (var sb in Blocks)
        {
            var shapeId = ShapeIdFromNum(sb.Id);
            if (shapeId is null)
                continue;
            ship.AddBlock(shapeId, sb.Position, sb.Angle);
        }
        return ship;
    }

    /// <summary>数字 ID → 字符串 ID（对齐 TheShapes 表）。</summary>
    private static string? ShapeIdFromNum(int numId)
    {
        return ShipShapeLibrary.Shapes.Values
            .Where(spec => spec.NumId == numId)
            .OrderByDescending(spec => spec.IsRegistered)
            .Select(spec => spec.Id)
            .FirstOrDefault();
    }

    /// <summary>解析舰船 .lua 文件（照搬逆向 ShipDesign.ParseLua）。</summary>
    public static ShipDesign ParseLua(string lua)
    {
        var design = new ShipDesign();
        var nameMatch = Regex.Match(lua, @"name\s*=\s*""([^""]*)""");
        if (nameMatch.Success) design.Name = nameMatch.Groups[1].Value;
        var authorMatch = Regex.Match(lua, @"author\s*=\s*""?([^"",}]*)""?");
        if (authorMatch.Success) design.Author = authorMatch.Groups[1].Value;
        var color0Match = Regex.Match(lua, @"color0\s*=\s*0x([0-9a-fA-F]+)");
        if (color0Match.Success) design.Color0 = Convert.ToUInt32(color0Match.Groups[1].Value, 16);
        var color1Match = Regex.Match(lua, @"color1\s*=\s*0x([0-9a-fA-F]+)");
        if (color1Match.Success) design.Color1 = Convert.ToUInt32(color1Match.Groups[1].Value, 16);

        // 匹配每个方块：{ID, {x, y}, angle, 可选 command={faction=...}}
        var blockRegex = new Regex(
            @"\{\s*(\d+)\s*,\s*\{\s*(-?[\d.eE+]+)\s*,\s*(-?[\d.eE+]+)\s*\}(?:\s*,\s*(-?[\d.eE+]+))?\s*(?:,\s*command\s*=\s*\{\s*faction\s*=\s*(\d+))?",
            RegexOptions.Singleline);
        foreach (Match m in blockRegex.Matches(lua))
        {
            var sb = new SerialBlock
            {
                Id = int.Parse(m.Groups[1].Value),
                Position = new Vector2(
                    float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)),
                Angle = m.Groups[4].Success ? float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0,
            };
            if (m.Groups[5].Success)
                sb.CommandFaction = int.Parse(m.Groups[5].Value);
            design.Blocks.Add(sb);
        }
        return design;
    }

    /// <summary>导出为 .lua 文本（照搬逆向舰船文件格式）。</summary>
    public string ToLua()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"{{data={{name=\"{Name}\", author=\"{Author}\", color0=0x{Color0:X6}, color1=0x{Color1:X6}}}, blocks={{");
        for (var i = 0; i < Blocks.Count; i++)
        {
            var b = Blocks[i];
            var pos = $"{{{b.Position.X.ToString("0.###", CultureInfo.InvariantCulture)}, {b.Position.Y.ToString("0.###", CultureInfo.InvariantCulture)}}}";
            var ang = b.Angle.ToString("0.000", CultureInfo.InvariantCulture);
            var cmd = b.CommandFaction.HasValue ? $", command={{faction={b.CommandFaction}}}" : "";
            var line = $"    {{{b.Id}, {pos}, {ang}{cmd}}}";
            if (i < Blocks.Count - 1) line += ",";
            sb.AppendLine(line);
        }
        sb.AppendLine("}}");
        return sb.ToString();
    }

    /// <summary>获取设计包围盒半径（照搬逆向 getBRadius）。</summary>
    public float GetBoundingRadius()
    {
        if (Blocks.Count == 0) return 0;
        var center = Blocks.Aggregate(Vector2.Zero, (acc, b) => acc + b.Position) / Blocks.Count;
        return Blocks.Max(b => b.Position.DistanceTo(center));
    }
}
