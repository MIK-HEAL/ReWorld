using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// ReWorld 统一游戏命令层：AI 桥（MCP）、回归测试与其它前端共用的观察/行动接口。
/// 观察命令返回 JSON 快照（JsonObject）；行动命令转发 UniverseView / PlayerProgress 公共 API。
/// 约束：低频调用（每秒若干次），且必须在主线程执行（AiBridge 在 _Process 中排队处理）。
/// </summary>
public sealed class GameCommandApi
{
    private readonly UniverseView _view;
    private const int MaxStepSeconds = 30;

    public GameCommandApi(UniverseView view) => _view = view;

    public JsonObject Execute(string command, JsonObject? args)
    {
        var result = command switch
        {
        // —— 观察 ——
        "get_state" => GetState(),
        "list_bodies" => ListBodies(args),
        "list_vessels" => ListVessels(),
        "list_linked_systems" => ListLinkedSystems(),
        "get_missions" => GetMissions(args),
        "get_relations" => GetRelations(),
        "get_ship" => GetShip(),
        "list_techs" => ListTechs(),
        // —— 飞行 / 战斗 ——
        "set_flight" => SetFlight(args),
        "clear_flight" => ClearFlight(),
        "set_fire" => SetFire(args),
        // —— 迁跃 / 停靠 ——
        "begin_warp" => BeginWarp(args),
        "cancel_warp" => Ok(_view.Warp.Cancel(), "warp_cancelled"),
        "toggle_dock" => Ok(_view.ToggleDock(), "toggle_dock"),
        "takeoff" => Ok(_view.TakeOff(), "takeoff"),
        // —— 经营 ——
        "claim_planet" => Ok(_view.ClaimPlanet(), "claim_planet"),
        "build_factory" => Ok(_view.BuildFactory(), "build_factory"),
        "establish_base" => Ok(_view.EstablishBase(), "establish_base"),
        "manufacture_ship" => Ok(_view.IsStationDocked ? _view.ManufactureDocked() : _view.ManufactureFleetShip(), "manufacture_ship"),
        "respawn" => Ok(_view.TryRespawn(), "respawn"),
        // —— 外交 / 任务 / 科技 ——
        "set_relation" => SetRelation(args),
        "aid_faction" => Ok(_view.AidFaction(ArgI(args, "faction")), "aid_faction"),
        "accept_mission" => Ok(_view.Progress.AcceptMission(ArgS(args, "id")), "accept_mission"),
        "claim_mission" => Ok(_view.Progress.ClaimMission(ArgS(args, "id")), "claim_mission"),
        "unlock_tech" => UnlockTech(args),
        // —— 时间控制 ——
        "pause" => Pause(args),
        "step" => Step(args),
        _ => throw new ArgumentException($"unknown command: {command}"),
        };
        // 行动命令普遍通过 HUD Notify 提示成败原因：原样带回给 AI 作为失败语义。
        result["notice"] = _view.AiNotice;
        return result;
    }

    // —— 观察命令 ——

    private JsonObject GetState()
    {
        var cell = _view.CurrentCell;
        var owned = 0;
        foreach (var vessel in _view.Traffic.Vessels)
            if (vessel.PlayerOwned && vessel.Alive) owned++;
        var warp = new JsonObject
        {
            ["state"] = _view.Warp.State.ToString().ToLowerInvariant(),
            ["locks_flight"] = _view.Warp.LocksFlight,
            ["progress"] = _view.Warp.Progress,
            ["destination"] = _view.Warp.State == WarpDrive.Phase.Ready ? null : Cell(_view.Warp.Destination),
        };
        var state = new JsonObject
        {
            ["system"] = UniverseGenerator.SystemName(cell),
            ["cell"] = Cell(cell),
            ["position"] = Vec(_view.PlayerPosition),
            ["velocity"] = Vec(_view.PlayerVelocity),
            ["speed"] = _view.PlayerVelocity.Length(),
            ["heading"] = _view.PlayerHeading,
            ["docked"] = _view.IsStationDocked ? "station" : _view.LandedBody is not null ? "planet" : "none",
            ["alive"] = _view.Ship is { HasLiveCore: true },
            ["resources"] = _view.Progress.Resources,
            ["owned_ships"] = owned + (_view.Ship is { HasLiveCore: true } ? 1 : 0),
            ["warp"] = warp,
            ["paused"] = _view.AiPaused,
        };
        if (_view.IsStationDocked)
        {
            state["docked_station"] = _view.DockedStationName;
            state["docked_station_faction"] = _view.DockedStationFaction;
        }
        if (_view.LandedBody is { } landed)
        {
            state["landed_body"] = landed.Name;
            state["landed_biome"] = landed.Biome;
        }
        if (_view.PlayerCombat is { } player)
        {
            state["energy"] = player.Energy;
            state["energy_capacity"] = player.EnergyCapacity;
            state["shield"] = player.Shield;
            state["shield_capacity"] = player.ShieldCapacity;
        }
        return state;
    }

    private JsonObject ListBodies(JsonObject? args)
    {
        var cell = ArgCell(args) ?? _view.CurrentCell;
        var system = UniverseGenerator.GenerateSystem(cell);
        var bodies = new JsonArray();
        foreach (var body in system.Bodies)
        {
            var entry = new JsonObject
            {
                ["id"] = body.Id,
                ["name"] = body.Name,
                ["kind"] = body.Kind.ToString().ToLowerInvariant(),
                ["faction"] = body.FactionId,
                ["faction_name"] = _view.Progress.Diplomacy.Name(body.FactionId),
                ["position"] = Vec(body.Position),
                ["radius"] = body.Radius,
                ["distance"] = body.Position.DistanceTo(_view.PlayerPosition),
                ["surface_distance"] = body.SurfaceDistance(_view.PlayerPosition),
                ["has_surface"] = body.HasSurface,
                ["hostile"] = _view.Progress.Diplomacy.Hostile(FactionRelations.Player, body.FactionId),
            };
            if (body.Kind == UniverseGenerator.CelestialKind.Planet)
            {
                entry["biome"] = body.Biome;
                entry["territory_radius"] = body.TerritoryRadius;
            }
            bodies.Add(entry);
        }
        return new JsonObject { ["system"] = system.Name, ["cell"] = Cell(cell), ["bodies"] = bodies };
    }

    private JsonObject ListVessels()
    {
        var vessels = new JsonArray();
        foreach (var vessel in _view.Traffic.Vessels)
            vessels.Add(new JsonObject
            {
                ["name"] = vessel.Name,
                ["faction"] = vessel.FactionId,
                ["faction_name"] = _view.Progress.Diplomacy.Name(vessel.FactionId),
                ["relation"] = RelationLabel(vessel.FactionId),
                ["hostile"] = _view.Progress.Diplomacy.Hostile(FactionRelations.Player, vessel.FactionId),
                ["alive"] = vessel.Alive,
                ["owned"] = vessel.PlayerOwned,
                ["mineral"] = vessel.Mineral,
                ["position"] = Vec(vessel.Position),
                ["distance"] = vessel.Position.DistanceTo(_view.PlayerPosition),
                ["radius"] = vessel.Radius,
            });
        foreach (var station in _view.Traffic.StationActors.Values)
            vessels.Add(new JsonObject
            {
                ["name"] = station.Name,
                ["faction"] = station.FactionId,
                ["faction_name"] = _view.Progress.Diplomacy.Name(station.FactionId),
                ["relation"] = RelationLabel(station.FactionId),
                ["hostile"] = _view.Progress.Diplomacy.Hostile(FactionRelations.Player, station.FactionId),
                ["alive"] = station.Alive,
                ["owned"] = false,
                ["mineral"] = false,
                ["position"] = Vec(station.Position),
                ["distance"] = station.Position.DistanceTo(_view.PlayerPosition),
                ["radius"] = station.Ship.BoundingRadius(),
                ["station"] = true,
            });
        return new JsonObject { ["vessels"] = vessels };
    }

    private JsonObject ListLinkedSystems()
    {
        var origin = _view.CurrentCell;
        var systems = new JsonArray();
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            var cell = new Vector2I(origin.X + dx, origin.Y + dy);
            var linked = UniverseGenerator.SystemsLinked(origin, cell);
            var system = UniverseGenerator.GenerateSystem(cell);
            systems.Add(new JsonObject
            {
                ["cell"] = Cell(cell),
                ["name"] = system.Name,
                ["linked"] = linked,
                ["in_range"] = WarpDrive.InRange(origin, cell),
                ["pirate_zone"] = system.Bodies.Any(b => b.Kind == UniverseGenerator.CelestialKind.PirateZone),
            });
        }
        return new JsonObject { ["origin"] = Cell(origin), ["systems"] = systems };
    }

    private JsonObject GetMissions(JsonObject? args)
    {
        var cell = ArgCell(args) ?? _view.CurrentCell;
        var missions = new JsonArray();
        foreach (var def in MissionCatalog.Generate(cell))
        {
            MissionState? state = null;
            foreach (var existing in _view.Progress.Missions)
                if (existing.Id == def.Id) { state = existing; break; }
            missions.Add(new JsonObject
            {
                ["id"] = def.Id,
                ["kind"] = def.Kind.ToString().ToLowerInvariant(),
                ["title"] = def.Title,
                ["detail"] = def.Detail,
                ["issuer"] = def.Issuer,
                ["issuer_name"] = _view.Progress.Diplomacy.Name(def.Issuer),
                ["target_faction"] = def.Faction,
                ["goal"] = def.Goal,
                ["reward"] = def.Reward,
                ["accepted"] = state?.Accepted ?? false,
                ["claimed"] = state?.Claimed ?? false,
                ["progress"] = state?.Progress ?? 0,
                ["ready_to_claim"] = state is not null && state.Accepted && !state.Claimed && state.Progress >= def.Goal,
            });
        }
        return new JsonObject { ["cell"] = Cell(cell), ["missions"] = missions };
    }

    private JsonObject GetRelations()
    {
        var factions = new JsonArray();
        foreach (var faction in new[] { FactionRelations.League, FactionRelations.Red, FactionRelations.Crystal })
            factions.Add(new JsonObject
            {
                ["id"] = faction,
                ["name"] = _view.Progress.Diplomacy.Name(faction),
                ["state"] = RelationLabel(faction),
                ["hostile"] = _view.Progress.Diplomacy.Hostile(FactionRelations.Player, faction),
                ["kills"] = _view.Progress.Kills.GetValueOrDefault(faction),
            });
        return new JsonObject { ["factions"] = factions };
    }

    private JsonObject GetShip()
    {
        var ship = _view.Ship;
        if (ship is null) return new JsonObject { ["ship"] = null };
        var blocks = new JsonArray();
        var weapons = 0;
        var hyperdrive = false;
        var tractor = false;
        foreach (var block in ship.Blocks)
        {
            if (block.Spec.IsWeapon) weapons++;
            if ((block.Features & BlockFeatures.Hyperdrive) != 0) hyperdrive = true;
            if ((block.Features & BlockFeatures.Tractor) != 0) tractor = true;
            blocks.Add(new JsonObject
            {
                ["shape_id"] = block.ShapeId,
                ["label"] = block.Spec.Label,
                ["health"] = block.Health,
                ["hull"] = block.Spec.Hull,
                ["enabled"] = block.Enabled,
            });
        }
        return new JsonObject
        {
            ["alive"] = ship.HasLiveCore,
            ["block_count"] = ship.Blocks.Count,
            ["mass"] = ship.TotalMass,
            ["bounding_radius"] = ship.BoundingRadius(),
            ["weapons"] = weapons,
            ["hyperdrive"] = hyperdrive,
            ["tractor"] = tractor,
            ["blocks"] = blocks,
        };
    }

    private JsonObject ListTechs()
    {
        var docked = _view.IsStationDocked;
        var discount = docked && _view.Progress.Diplomacy.Get(FactionRelations.Player, _view.DockedStationFaction)
            == DiplomaticState.Friendly ? 85 : 95;
        var techs = new JsonArray();
        foreach (var id in ShipShapeLibrary.BuildOrder)
        {
            var spec = ShipShapeLibrary.Get(id);
            if (!spec.IsRegistered) continue;
            techs.Add(new JsonObject
            {
                ["id"] = id,
                ["num_id"] = spec.NumId,
                ["label"] = spec.Label,
                ["mass"] = spec.Mass,
                ["unlocked"] = _view.Progress.Unlocked.Contains(spec.NumId),
                ["base_cost"] = PlayerProgress.UnlockCost(spec),
                ["station_cost"] = docked ? PlayerProgress.StationUnlockCost(spec, discount) : null,
            });
        }
        return new JsonObject { ["docked"] = docked, ["techs"] = techs };
    }

    // —— 行动命令 ——

    private JsonObject SetFlight(JsonObject? args)
    {
        var forward = Math.Clamp(ArgF(args, "forward"), -1f, 1f);
        var horizontal = Math.Clamp(ArgF(args, "horizontal"), -1f, 1f);
        var strafe = Math.Clamp(ArgF(args, "strafe"), -1f, 1f);
        _view.AiControls = new ShipFlightController.Controls(forward, horizontal, strafe);
        var aim = ArgVec(args);
        if (aim is { } target) _view.AiAimPoint = target;
        return new JsonObject
        {
            ["forward"] = forward,
            ["horizontal"] = horizontal,
            ["strafe"] = strafe,
        };
    }

    private JsonObject ClearFlight()
    {
        _view.AiControls = null;
        _view.AiAimPoint = null;
        _view.AiFiring = false;
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject SetFire(JsonObject? args)
    {
        _view.AiFiring = ArgB(args, "enabled");
        var aim = ArgVec(args);
        if (aim is { } target) _view.AiAimPoint = target;
        return new JsonObject { ["firing"] = _view.AiFiring };
    }

    private JsonObject BeginWarp(JsonObject? args)
    {
        var cell = ArgCell(args) ?? throw new ArgumentException("begin_warp requires a destination cell");
        var engaged = _view.BeginWarp(cell);
        return new JsonObject { ["engaged"] = engaged, ["destination"] = Cell(cell) };
    }

    private JsonObject SetRelation(JsonObject? args)
    {
        var faction = ArgI(args, "faction");
        var state = ArgS(args, "state") switch
        {
            "war" => DiplomaticState.War,
            "wanted" => DiplomaticState.Wanted,
            "friendly" => DiplomaticState.Friendly,
            "peace" or "neutral" => DiplomaticState.Neutral,
            _ => throw new ArgumentException("state must be war/wanted/friendly/peace"),
        };
        _view.ChangeRelation(faction, state);
        return new JsonObject { ["faction"] = faction, ["state"] = state.ToString().ToLowerInvariant() };
    }

    private JsonObject UnlockTech(JsonObject? args)
    {
        var id = ArgS(args, "id");
        if (id.Length == 0) throw new ArgumentException("unlock_tech requires an id");
        if (_view.IsStationDocked)
        {
            var discount = _view.Progress.Diplomacy.Get(FactionRelations.Player, _view.DockedStationFaction)
                == DiplomaticState.Friendly ? 85 : 95;
            return new JsonObject
            {
                ["unlocked"] = _view.Progress.TryStationUnlock(id, discount),
                ["cost"] = PlayerProgress.StationUnlockCost(ShipShapeLibrary.Get(id), discount),
                ["docked"] = true,
            };
        }
        return new JsonObject
        {
            ["unlocked"] = _view.Progress.TryUnlock(id),
            ["cost"] = PlayerProgress.UnlockCost(ShipShapeLibrary.Get(id)),
            ["docked"] = false,
        };
    }

    private JsonObject Pause(JsonObject? args)
    {
        var paused = ArgB(args, "paused", true);
        _view.AiPaused = paused;
        if (!paused) _view.AiStepBudget = 0;
        return new JsonObject { ["paused"] = _view.AiPaused };
    }

    private JsonObject Step(JsonObject? args)
    {
        var seconds = Math.Clamp(ArgF(args, "seconds", 0.1f), 0.01f, MaxStepSeconds);
        _view.AiPaused = true;
        _view.AiStepBudget += seconds;
        return new JsonObject { ["step"] = seconds, ["paused"] = true };
    }

    // —— 序列化与参数辅助 ——

    private static JsonObject Ok(bool result, string action) => new() { ["ok"] = result, ["action"] = action };

    private static JsonObject Vec(Vector2 v) => new() { ["x"] = v.X, ["y"] = v.Y };

    private static JsonObject Cell(Vector2I c) => new() { ["x"] = c.X, ["y"] = c.Y };

    private string RelationLabel(int faction)
        => FactionRelations.Label(_view.Progress.Diplomacy.Get(FactionRelations.Player, faction));

    private static Vector2I? ArgCell(JsonObject? args)
    {
        if (args is null) return null;
        if (args["cell"] is JsonObject nested)
        {
            var x = ArgI(nested, "x", int.MinValue);
            var y = ArgI(nested, "y", int.MinValue);
            return x == int.MinValue || y == int.MinValue ? null : new Vector2I(x, y);
        }
        var topX = ArgI(args, "x", int.MinValue);
        var topY = ArgI(args, "y", int.MinValue);
        return topX == int.MinValue || topY == int.MinValue ? null : new Vector2I(topX, topY);
    }

    private static Vector2? ArgVec(JsonObject? args)
    {
        if (args? ["aim"] is JsonObject aim)
            return new Vector2(ArgF(aim, "x"), ArgF(aim, "y"));
        if (args? ["aim_x"] is not null || args? ["aim_y"] is not null)
            return new Vector2(ArgF(args, "aim_x"), ArgF(args, "aim_y"));
        return null;
    }

    private static float ArgF(JsonObject? args, string key, float fallback = 0)
        => args is not null && args.TryGetPropertyValue(key, out var node) && node is not null
           && node.GetValueKind() == JsonValueKind.Number ? (float)node.GetValue<double>() : fallback;

    private static int ArgI(JsonObject? args, string key, int fallback = 0)
        => args is not null && args.TryGetPropertyValue(key, out var node) && node is not null
           && node.GetValueKind() == JsonValueKind.Number ? (int)node.GetValue<double>() : fallback;

    private static string ArgS(JsonObject? args, string key, string fallback = "")
        => args is not null && args.TryGetPropertyValue(key, out var node) && node is not null
           && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : fallback;

    private static bool ArgB(JsonObject? args, string key, bool fallback = false)
        => args is not null && args.TryGetPropertyValue(key, out var node) && node is not null
           && node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? node.GetValue<bool>() : fallback;
}
