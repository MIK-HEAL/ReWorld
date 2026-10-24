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
    public HashSet<int> Unlocked { get; set; } = new() { 200, 219, 202, 217, 218, 205, 201, 30001 };
    public FactionRelations Diplomacy { get; set; } = new();
    public bool Established { get; set; }
    public float BaseX { get; set; }
    public float BaseY { get; set; }
    public HashSet<string> MinedDeposits { get; set; } = new();
    public List<string> FleetDesigns { get; set; } = new();
    public event Action? Changed;
    public bool Autosave { get; set; }
    private const string SavePath = "user://reworld_campaign.json";
    public bool CanBuild(string id) => Unlocked.Contains(ShipShapeLibrary.Get(id).NumId);
    public static int UnlockCost(ShipShapeLibrary.ShapeSpec spec)
        => Math.Max(40, (int)(spec.Mass * 8 + (spec.IsWeapon ? spec.Damage * 5 : 0)
            + ((spec.Features & (BlockFeatures.Shield | BlockFeatures.Factory)) != 0 ? 200 : 0)));
    public bool TryUnlock(string id)
    {
        if (!ShipShapeLibrary.Shapes.TryGetValue(id, out var spec)) return false;
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
            return progress;
        }
        catch (JsonException) { GD.PushWarning("进度存档无效，已使用初始进度"); return new(); }
    }
}
