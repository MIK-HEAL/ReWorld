using Godot;
using System;

/// <summary>Bounded interstellar travel. Position changes exactly once at tunnel exit.</summary>
public sealed class WarpDrive
{
    public enum Phase { Ready, Charging, Transit, Cooldown }
    public Phase State { get; private set; }
    public Vector2I Destination { get; private set; }
    public float Elapsed { get; private set; }
    public const float ChargeSeconds = 3, TransitSeconds = 4, CooldownSeconds = 2;
    public bool LocksFlight => State is Phase.Charging or Phase.Transit;
    public float Progress => State switch
    {
        Phase.Charging => Elapsed / ChargeSeconds,
        Phase.Transit => Elapsed / TransitSeconds,
        Phase.Cooldown => Elapsed / CooldownSeconds,
        _ => 0
    };
    public static bool InRange(Vector2I source, Vector2I target)
        => source != target && Math.Abs((long)target.X - source.X) <= 1 && Math.Abs((long)target.Y - source.Y) <= 1;
    public bool Engage(Vector2I source, Vector2I target, bool hasCore, bool docked, bool surfaceClear)
    {
        if (State != Phase.Ready || !hasCore || docked || !surfaceClear || !InRange(source, target)) return false;
        Destination = target;
        State = Phase.Charging;
        Elapsed = 0;
        return true;
    }
    public bool Cancel()
    {
        if (State != Phase.Charging) return false;
        State = Phase.Ready;
        Elapsed = 0;
        return true;
    }
    public bool Advance(float delta)
    {
        var arrived = false;
        var remaining = Math.Max(0, delta);
        while (State != Phase.Ready)
        {
            var duration = State == Phase.Charging ? ChargeSeconds : State == Phase.Transit ? TransitSeconds : CooldownSeconds;
            var consumed = Math.Min(remaining, duration - Elapsed);
            Elapsed += consumed;
            remaining -= consumed;
            if (Elapsed < duration) break;
            arrived |= State == Phase.Transit;
            State = State == Phase.Charging ? Phase.Transit : State == Phase.Transit ? Phase.Cooldown : Phase.Ready;
            Elapsed = 0;
            if (remaining <= 0) break;
        }
        return arrived;
    }
}
