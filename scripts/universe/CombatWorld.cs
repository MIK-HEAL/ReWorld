using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public class CombatVessel
{
    public required string Name;
    public required ModularShip Ship;
    public int FactionId = FactionRelations.Player;
    public Vector2 Position, PreviousPosition, Velocity;
    public float Heading, PreviousHeading, AngularVelocity;
    public bool Mineral;
    public string DepositId = "";
    public float Energy, Shield;
    public float EnergyCapacity, Generation, ShieldCapacity, ShieldRadius, ShieldRegen;
    public CombatVessel? CombatTarget;
    public readonly Dictionary<int, float> Cooldowns = new();
    public ModularShip.Block[] Active = Array.Empty<ModularShip.Block>();
    public bool HasTractor;
    private ulong _revision = ulong.MaxValue;
    private bool _initialized;
    public bool Alive => Mineral ? Ship.Blocks.Count > 0 : Ship.HasLiveCore;
    public void Refresh()
    {
        if (_revision == Ship.Revision) return;
        Rebuild();
    }
    private void Rebuild()
    {
        Active = Ship.HasLiveCore ? Ship.GetMainComponent().Where(b => b.Enabled && b.Health > 0).ToArray() : Array.Empty<ModularShip.Block>();
        HasTractor = Active.Any(b => (b.Features & BlockFeatures.Tractor) != 0);
        _revision = Ship.Revision;
        EnergyCapacity = 60 + Active.Sum(b => b.Spec.PowerCapacity);
        Generation = 8 + Active.Sum(b => b.Spec.Generation);
        ShieldCapacity = Active.Where(b => (b.Features & BlockFeatures.Shield) != 0).Sum(b => b.Spec.ShieldStrength);
        ShieldRadius = Active.Where(b => (b.Features & BlockFeatures.Shield) != 0)
            .Select(b => b.Spec.ShieldRadius + b.Position.DistanceTo(Ship.CenterOfMass())).DefaultIfEmpty(0).Max();
        ShieldRegen = Active.Where(b => (b.Features & BlockFeatures.Shield) != 0).Sum(b => b.Spec.ShieldRegen);
        Energy = _initialized ? Math.Min(Energy, EnergyCapacity) : EnergyCapacity;
        Shield = _initialized ? Math.Min(Shield, ShieldCapacity) : ShieldCapacity;
        _initialized = true;
        var liveIds = Active.Select(b => b.Id).ToHashSet();
        foreach (var id in Cooldowns.Keys.Where(id => !liveIds.Contains(id)).ToArray()) Cooldowns.Remove(id);
    }
}

/// <summary>Shared manual/AI weapons, power, shields, swept polygon hits, local damage and physical debris.</summary>
public sealed class CombatWorld
{
    public sealed class Projectile
    {
        public required CombatVessel Owner;
        public Vector2 Position, Previous, Velocity;
        public float Damage, Life, Splash, DroneTimer;
        public WeaponKind Kind;
        public int Faction;
        public CombatVessel? Target;
    }
    public sealed class Resource
    {
        public Vector2 Position;
        public int Amount;
        public string DepositId = "";
    }
    public sealed class Debris
    {
        public required ModularShip Ship;
        public Vector2 Position, Velocity;
        public float Heading, Life = 25;
    }
    public sealed class Beam
    {
        public Vector2 Start, End;
        public bool Healing;
        public float Life = 0.12f;
    }
    public readonly List<Projectile> Projectiles = new();
    public readonly List<Resource> Resources = new();
    public readonly List<Debris> Fragments = new();
    public readonly List<Beam> Beams = new();
    public event Action<string>? DepositCollected;
    public CombatVessel? NearestHostile(CombatVessel vessel, IReadOnlyList<CombatVessel> actors, FactionRelations relations, float range = 4500)
    {
        CombatVessel? nearest = null;
        var distance = range * range;
        for (var i = 0; i < actors.Count; i++)
        {
            var other = actors[i];
            if (!other.Alive || other.Mineral || !relations.AtWar(vessel.FactionId, other.FactionId)) continue;
            var d = other.Position.DistanceSquaredTo(vessel.Position);
            if (d < distance) { nearest = other; distance = d; }
        }
        return nearest;
    }

    public void Step(float dt, IReadOnlyList<CombatVessel> actors, FactionRelations relations,
        CombatVessel? player = null, bool firing = false, Vector2? aim = null)
    {
        dt = Math.Clamp(dt, 0, 0.1f);
        foreach (var beam in Beams) beam.Life -= dt;
        Beams.RemoveAll(b => b.Life <= 0);
        foreach (var fragment in Fragments) { fragment.Position += fragment.Velocity * dt; fragment.Heading += 0.2f * dt; fragment.Life -= dt; }
        Fragments.RemoveAll(f => f.Life <= 0);
        for (var actorIndex = 0; actorIndex < actors.Count; actorIndex++)
        {
            var actor = actors[actorIndex];
            if (!actor.Alive || actor.Mineral) continue;
            actor.Refresh();
            actor.Energy = Math.Min(actor.EnergyCapacity, actor.Energy + actor.Generation * dt);
            var regen = Math.Min(actor.ShieldCapacity - actor.Shield, Math.Min(actor.ShieldRegen * dt, actor.Energy));
            actor.Shield += regen;
            actor.Energy -= regen;
            foreach (var activeBlock in actor.Active)
                if (actor.Cooldowns.TryGetValue(activeBlock.Id, out var cooldown)) actor.Cooldowns[activeBlock.Id] = Math.Max(0, cooldown - dt);
            actor.CombatTarget = NearestHostile(actor, actors, relations);
            foreach (var block in actor.Active)
            {
                if (!block.Spec.IsWeapon || actor.Cooldowns.GetValueOrDefault(block.Id) > 0) continue;
                var spec = block.Spec;
                var kind = spec.WeaponKind;
                var origin = actor.Position + (block.Position - actor.Ship.CenterOfMass()).Rotated(actor.Heading);
                if (kind == WeaponKind.PointDefense)
                {
                    Projectile? missile = null;
                    var nearest = spec.Range * spec.Range;
                    foreach (var p in Projectiles)
                    {
                        var distance = p.Position.DistanceSquaredTo(origin);
                        if (p.Life > 0 && distance < nearest && relations.AtWar(actor.FactionId, p.Faction)) { missile = p; nearest = distance; }
                    }
                    if (missile is not null && actor.Energy >= spec.FireEnergy)
                    {
                        missile.Life = 0; SpendShot(actor, block);
                        Beams.Add(new Beam { Start = origin, End = missile.Position });
                        continue;
                    }
                }
                var target = actor.CombatTarget;
                if (kind == WeaponKind.Healer)
                    target = FindHealingTarget(actor, actors, relations, origin, spec.Range);
                var manual = actor == player && firing && kind != WeaponKind.Healer;
                if (!manual && target is null) continue;
                var destination = manual && aim.HasValue ? aim.Value : target!.Position;
                var offset = destination - origin;
                if (!manual && offset.Length() > spec.Range) continue;
                var direction = offset.Normalized();
                var forward = spec.WeaponDirection.Rotated(actor.Heading + block.Angle);
                var turret = (block.Features & BlockFeatures.Turret) != 0 || spec.Id is "cannon" or "laser" or "beam" or "point_defense";
                if (!turret && forward.Dot(direction) < Mathf.Cos(Mathf.DegToRad(15))) continue;
                if (actor.Energy < spec.FireEnergy) continue;
                SpendShot(actor, block);
                if (kind == WeaponKind.Healer && target is not null)
                {
                    ModularShip.Block? damaged = null;
                    foreach (var candidate in target.Active)
                        if (damaged is null || candidate.Health / candidate.Spec.Hull < damaged.Health / damaged.Spec.Hull) damaged = candidate;
                    if (damaged is not null) damaged.Health = Math.Min(damaged.Spec.Hull, damaged.Health + spec.Damage);
                    Beams.Add(new Beam { Start = origin, End = target.Position, Healing = true });
                }
                else if (kind == WeaponKind.Beam)
                {
                    var end = origin + direction * spec.Range;
                    var hit = FindHit(origin, end, actor, actors, relations);
                    if (hit.Actor is not null)
                    {
                        end = origin.Lerp(end, hit.Fraction);
                        ApplyDamage(hit.Actor, hit.Block, spec.Damage, direction);
                    }
                    Beams.Add(new Beam { Start = origin, End = end });
                }
                else
                    Projectiles.Add(new Projectile { Owner = actor, Faction = actor.FactionId, Kind = kind,
                        Position = origin + direction * (block.Spec.Radius + 2), Previous = origin,
                        Velocity = actor.Velocity + direction * spec.ProjectileSpeed, Damage = spec.Damage,
                        Life = Math.Max(0.2f, spec.Range / spec.ProjectileSpeed), Splash = spec.SplashRadius, Target = target });
            }
        }
        // Snapshot because drones may emit new projectiles.
        foreach (var projectile in Projectiles.ToArray())
        {
            if (projectile.Life <= 0) continue;
            projectile.Life -= dt;
            if (projectile.Target is { } target && (!target.Alive || !relations.AtWar(projectile.Faction, target.FactionId))) projectile.Target = null;
            if (projectile.Kind is WeaponKind.Missile or WeaponKind.Drone && projectile.Target is { } homing)
            {
                var speed = projectile.Velocity.Length();
                var desired = (homing.Position - projectile.Position).Normalized() * speed;
                projectile.Velocity = projectile.Velocity.Lerp(desired, Math.Min(1, dt * 4)).Normalized() * speed;
            }
            projectile.Previous = projectile.Position;
            projectile.Position += projectile.Velocity * dt;
            var hit = FindHit(projectile.Previous, projectile.Position, projectile.Owner, actors, relations);
            if (hit.Actor is null) continue;
            projectile.Position = projectile.Previous.Lerp(projectile.Position, hit.Fraction);
            projectile.Life = 0;
            ApplyDamage(hit.Actor, hit.Block, projectile.Damage, projectile.Velocity.Normalized());
            if (projectile.Splash > 0)
                for (var victimIndex = 0; victimIndex < actors.Count; victimIndex++)
                {
                    var victim = actors[victimIndex];
                    if (victim == hit.Actor || !victim.Alive || victim.Mineral || !relations.AtWar(victim.FactionId, projectile.Faction)) continue;
                    foreach (var block in victim.Ship.Blocks.ToArray())
                    {
                        var pos = victim.Position + (block.Position - victim.Ship.CenterOfMass()).Rotated(victim.Heading);
                        if (pos.DistanceTo(projectile.Position) < projectile.Splash) ApplyDamage(victim, block, projectile.Damage * 0.5f, projectile.Velocity.Normalized());
                    }
                }
        }
        Projectiles.RemoveAll(p => p.Life <= 0);
    }

    private static void SpendShot(CombatVessel actor, ModularShip.Block block)
    {
        actor.Energy -= block.Spec.FireEnergy;
        actor.Cooldowns[block.Id] = block.Spec.FireInterval;
    }
    private static CombatVessel? FindHealingTarget(CombatVessel actor, IReadOnlyList<CombatVessel> actors, FactionRelations relations, Vector2 origin, float range)
    {
        CombatVessel? target = null;
        var nearest = range * range;
        for (var i = 0; i < actors.Count; i++)
        {
            var candidate = actors[i];
            if (candidate == actor || !candidate.Alive || candidate.Mineral || !relations.Friendly(actor.FactionId, candidate.FactionId)) continue;
            var distance = candidate.Position.DistanceSquaredTo(origin);
            if (distance >= nearest) continue;
            foreach (var block in candidate.Ship.Blocks)
                if (block.Enabled && block.Health < block.Spec.Hull) { target = candidate; nearest = distance; break; }
        }
        return target;
    }

    public readonly record struct Hit(CombatVessel? Actor, ModularShip.Block? Block, float Fraction);
    public static Hit FindHit(Vector2 start, Vector2 end, CombatVessel owner, IReadOnlyList<CombatVessel> actors, FactionRelations relations)
    {
        var nearest = new Hit(null, null, 1);
        foreach (var actor in actors)
        {
            if (actor == owner || !actor.Alive || !actor.Mineral && !relations.AtWar(owner.FactionId, actor.FactionId)) continue;
            if (actor.Shield > 0)
            {
                var t = CircleFraction(start - actor.Position, end - actor.Position, actor.ShieldRadius);
                if (t < nearest.Fraction) nearest = new Hit(actor, null, t);
                continue;
            }
            var center = actor.Ship.CenterOfMass();
            var a = (start - actor.Position).Rotated(-actor.Heading) + center;
            var b = (end - actor.Position).Rotated(-actor.Heading) + center;
            foreach (var block in actor.Ship.Blocks)
            {
                if (!block.Enabled || block.Health <= 0) continue;
                var p = (a - block.Position).Rotated(-block.Angle);
                var q = (b - block.Position).Rotated(-block.Angle);
                var vertices = block.Spec.Vertices();
                var t = Geometry2D.IsPointInPolygon(p, vertices) ? 0 : 2f;
                for (var i = 0; i < vertices.Length; i++)
                    t = Math.Min(t, SegmentFraction(p, q, vertices[i], vertices[(i + 1) % vertices.Length]));
                if (t < nearest.Fraction) nearest = new Hit(actor, block, t);
            }
        }
        return nearest;
    }
    private static float SegmentFraction(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var r = b - a; var s = d - c;
        var cross = r.Cross(s);
        if (Mathf.Abs(cross) < 0.00001f) return 2;
        var t = (c - a).Cross(s) / cross; var u = (c - a).Cross(r) / cross;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : 2;
    }
    private static float CircleFraction(Vector2 a, Vector2 b, float radius)
    {
        if (a.LengthSquared() <= radius * radius) return 0;
        var d = b - a; var aa = d.LengthSquared();
        if (aa < 0.00001f) return 2;
        var bb = 2 * a.Dot(d); var cc = a.LengthSquared() - radius * radius;
        var disc = bb * bb - 4 * aa * cc;
        if (disc < 0) return 2;
        var t = (-bb - Mathf.Sqrt(disc)) / (2 * aa);
        return t >= 0 && t <= 1 ? t : 2;
    }
    public void ApplyDamage(CombatVessel actor, ModularShip.Block? block, float damage, Vector2 direction)
    {
        if (damage <= 0) return;
        if (actor.Shield > 0) { actor.Shield = Math.Max(0, actor.Shield - damage); return; }
        if (block is null || !actor.Ship.Blocks.Contains(block)) return;
        var center = actor.Ship.CenterOfMass();
        var at = actor.Position + (block.Position - center).Rotated(actor.Heading);
        var fragments = actor.Ship.DamageBlock(block, damage);
        foreach (var fragment in fragments)
            Fragments.Add(new Debris { Ship = fragment, Position = actor.Position + (fragment.CenterOfMass() - center).Rotated(actor.Heading),
                Heading = actor.Heading, Velocity = actor.Velocity + direction * 30 });
        actor.Position += (actor.Ship.CenterOfMass() - center).Rotated(actor.Heading);
        actor.PreviousPosition = actor.Position;
        if (block.Health <= 0)
            Resources.Add(new Resource { Position = at, Amount = actor.Mineral ? 90 : Math.Max(5, (int)(block.Spec.Mass * 4)), DepositId = actor.DepositId });
    }
    public int Collect(Vector2 position, float radius)
    {
        var total = 0;
        for (var i = Resources.Count - 1; i >= 0; i--)
            if (Resources[i].Position.DistanceSquaredTo(position) <= radius * radius)
            { total += Resources[i].Amount; DepositCollected?.Invoke(Resources[i].DepositId); Resources.RemoveAt(i); }
        return total;
    }
}
