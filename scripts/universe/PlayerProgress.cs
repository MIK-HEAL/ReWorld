using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FileAccess = Godot.FileAccess;

/// <summary>Permanent technology purchases are outside the editor's reversible geometry history.</summary>
public sealed class PlayerProgress
{
    public int Resources { get; set; } = 250;
    public HashSet<int> Unlocked { get; set; } = new() { 47, 48, 49, 54, 56, 58, 60, 30001 };
    public FactionRelations Diplomacy { get; set; } = new();
    public bool Established { get; set; }
    public float BaseX { get; set; }
    public float BaseY { get; set; }
    public HashSet<string> MinedDeposits { get; set; } = new();
    public List<string> FleetDesigns { get; set; } = new();
    public event Action? Changed;
    public bool Autosave { get; set; }
    private const string SavePath = "user://reworld_campaign.json";
    public bool CanBuild(string id)
    {
        var canonical = ShipShapeLibrary.CanonicalTechnologyId(id);
        return ShipShapeLibrary.BuildOrder.Contains(canonical) &&
            ShipShapeLibrary.Shapes.TryGetValue(canonical, out var spec) && Unlocked.Contains(spec.NumId);
    }
    public static int UnlockCost(ShipShapeLibrary.ShapeSpec spec)
        => Math.Max(40, (int)(spec.Mass * 8 + (spec.IsWeapon ? spec.Damage * 5 : 0)
            + ((spec.Features & (BlockFeatures.Shield | BlockFeatures.Factory)) != 0 ? 200 : 0)));
    public bool TryUnlock(string id)
    {
        var canonical = ShipShapeLibrary.CanonicalTechnologyId(id);
        if (!ShipShapeLibrary.BuildOrder.Contains(canonical) || !ShipShapeLibrary.Shapes.TryGetValue(canonical, out var spec)) return false;
        if (Unlocked.Contains(spec.NumId)) return true;
        var cost = UnlockCost(spec);
        if (Resources < cost) return false;
        Resources -= cost;
        Unlocked.Add(spec.NumId);
        Commit();
        return true;
    }
    public bool Spend(int amount)
    {
        if (amount < 0 || Resources < amount) return false;
        Resources -= amount;
        Commit();
        return true;
    }
    public void Collect(int amount)
    {
        if (amount <= 0) return;
        Resources = (int)Math.Min(int.MaxValue, (long)Resources + amount);
        Commit();
    }
    public void Commit()
    {
        Changed?.Invoke();
        if (Autosave) Save();
    }
    public void Save(string path = SavePath)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        file?.StoreString(JsonSerializer.Serialize(this));
    }
    public static PlayerProgress Load(string path = SavePath)
    {
        if (!FileAccess.FileExists(path)) return new();
        try
        {
            var progress = JsonSerializer.Deserialize<PlayerProgress>(FileAccess.GetFileAsString(path)) ?? new();
            progress.Resources = Math.Max(0, progress.Resources);
            progress.Unlocked ??= new(); progress.Diplomacy ??= new(); progress.Diplomacy.States ??= new();
            progress.MinedDeposits ??= new(); progress.FleetDesigns ??= new();
            if (progress.Unlocked.Contains(200)) progress.Unlocked.Add(47);
            if (progress.Unlocked.Contains(219)) progress.Unlocked.Add(48);
            if (progress.Unlocked.Contains(202)) progress.Unlocked.Add(54);
            if (progress.Unlocked.Contains(218)) progress.Unlocked.Add(56);
            if (progress.Unlocked.Contains(205)) progress.Unlocked.Add(49);
            return progress;
        }
        catch (JsonException) { GD.PushWarning("进度存档无效，已使用初始进度"); return new(); }
    }
}
