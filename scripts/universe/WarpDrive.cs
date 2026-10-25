using Godot;
using System;
using System.Collections.Generic;

/// <summary>Continuous, distance-based hyperdrive flight in the existing world.</summary>
public sealed class WarpDrive
{
    public enum Phase { Ready, Charging, Transit, Braking, Cooldown }
    public Phase State { get; private set; }
    public Vector2I Destination { get; private set; }
    public float Elapsed { get; private set; }
    public const float ChargeSeconds = 1.2f, CooldownSeconds = 2;
    public const float CruiseSpeed = 18000, Acceleration = 12000;
    private Vector2[] _route = Array.Empty<Vector2>();
    private float[] _lengths = Array.Empty<float>();
    private float _rampTime, _rampDistance, _cruiseTime, _brakeStart, _brakeSpeed;
    public float TravelSeconds => 2 * _rampTime + _cruiseTime;
    public float TotalDistance { get; private set; }
    public float TravelledDistance { get; private set; }
    public float RemainingDistance => Mathf.Max(0, TotalDistance - TravelledDistance);
    public float Speed { get; private set; }
    public Vector2 Position { get; private set; }
    public Vector2 EndPosition => _route.Length == 0 ? Position : _route[^1];
    public Vector2 Direction { get; private set; } = Vector2.Right;
    public Vector2 Velocity => Direction * Speed;
    public bool Aborted { get; private set; }
    public bool LocksFlight => State is Phase.Charging or Phase.Transit or Phase.Braking;
    public bool IsMoving => State is Phase.Transit or Phase.Braking;
    public float TimeRemaining => State == Phase.Charging ? ChargeSeconds - Elapsed + TravelSeconds
        : State == Phase.Transit ? Math.Max(0, TravelSeconds - Elapsed)
        : State == Phase.Braking ? Math.Max(0, _brakeSpeed / Acceleration - Elapsed) : 0;
    public float Progress => State == Phase.Charging ? Elapsed / ChargeSeconds
        : State == Phase.Cooldown ? Elapsed / CooldownSeconds
        : TotalDistance > 0 ? TravelledDistance / TotalDistance : 0;
    /// <summary>相邻且有已生成的航道才能迁跃；未连通星系需沿航道中转。</summary>
    public static bool InRange(Vector2I source, Vector2I target)
        => source != target && Math.Abs((long)target.X - source.X) <= 1 && Math.Abs((long)target.Y - source.Y) <= 1
           && UniverseGenerator.SystemsLinked(source, target);
    public bool Engage(Vector2I source, Vector2I target, bool hasCore, bool docked, bool surfaceClear,
        IReadOnlyList<Vector2>? route = null)
    {
        if (State != Phase.Ready || !hasCore || docked || !surfaceClear || !InRange(source, target)) return false;
        route ??= new[] { UniverseGenerator.SystemCenter(source), UniverseGenerator.SystemCenter(target) };
        if (route.Count < 2) return false;
        for (var i = 0; i < route.Count; i++)
            if (!route[i].IsFinite() || i > 0 && route[i].DistanceSquaredTo(route[i-1]) < 0.0001f) return false;
        _route = new Vector2[route.Count];
        _lengths = new float[route.Count];
        for (var i = 0; i < route.Count; i++)
        {
            _route[i] = route[i];
            if (i > 0) _lengths[i] = _lengths[i-1] + route[i].DistanceTo(route[i-1]);
        }
        TotalDistance = _lengths[^1];
        TravelledDistance = Speed = Elapsed = 0;
        _rampTime = Mathf.Min(CruiseSpeed / Acceleration, Mathf.Sqrt(TotalDistance / Acceleration));
        _rampDistance = Acceleration * _rampTime * _rampTime * 0.5f;
        _cruiseTime = Mathf.Max(0, (TotalDistance - 2 * _rampDistance) / (Acceleration * _rampTime));
        Aborted = false;
        Destination = target;
        State = Phase.Charging;
        SampleRoute();
        return true;
    }
    public bool Cancel()
    {
        if (State == Phase.Charging)
        {
            State = Phase.Ready; Elapsed = 0; return true;
        }
        if (State != Phase.Transit) return false;
        Aborted = true;
        _brakeStart = TravelledDistance;
        _brakeSpeed = Speed;
        State = Phase.Braking;
        Elapsed = 0;
        return true;
    }
    public bool Advance(float delta)
    {
        if (!float.IsFinite(delta) || delta <= 0) return false;
        var arrived = false;
        var remaining = delta;
        while (State != Phase.Ready)
        {
            var duration = State switch { Phase.Charging => ChargeSeconds, Phase.Transit => TravelSeconds,
                Phase.Braking => _brakeSpeed / Acceleration, _ => CooldownSeconds };
            var consumed = Math.Min(remaining, Math.Max(0, duration - Elapsed));
            Elapsed += consumed;
            remaining -= consumed;
            if (State == Phase.Transit)
            {
                var t = Elapsed;
                if (t < _rampTime)
                { Speed = Acceleration * t; TravelledDistance = Acceleration * t * t * 0.5f; }
                else if (t < _rampTime + _cruiseTime)
                { Speed = Acceleration * _rampTime; TravelledDistance = _rampDistance + Speed * (t - _rampTime); }
                else
                {
                    var left = Math.Max(0, TravelSeconds - t);
                    Speed = Acceleration * left;
                    TravelledDistance = TotalDistance - Acceleration * left * left * 0.5f;
                }
                SampleRoute();
            }
            else if (State == Phase.Braking)
            {
                Speed = Math.Max(0, _brakeSpeed - Acceleration * Elapsed);
                TravelledDistance = Math.Min(TotalDistance, _brakeStart + _brakeSpeed * Elapsed - Acceleration * Elapsed * Elapsed * 0.5f);
                SampleRoute();
            }
            if (Elapsed < duration) break;
            arrived |= State == Phase.Transit;
            State = State == Phase.Charging ? Phase.Transit : State == Phase.Cooldown ? Phase.Ready : Phase.Cooldown;
            Elapsed = 0;
            if (State is Phase.Cooldown or Phase.Ready) Speed = 0;
            if (remaining <= 0) break;
        }
        return arrived;
    }
    private void SampleRoute()
    {
        var index = Array.BinarySearch(_lengths, TravelledDistance);
        if (index < 0) index = ~index;
        index = Math.Clamp(index, 1, _route.Length - 1);
        var fraction = Mathf.Clamp((TravelledDistance - _lengths[index-1]) / (_lengths[index] - _lengths[index-1]), 0, 1);
        Position = _route[index-1].Lerp(_route[index], fraction);
        Direction = (_route[index] - _route[index-1]).Normalized();
    }
}
