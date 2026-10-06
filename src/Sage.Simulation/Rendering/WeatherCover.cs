#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Weather that knows where it is (issue #311, docs/design/06 §3.13, TODO F40 and F16).
//
// `Weather` (Weather.cs) says what the sky is doing; this is what the world does to it and what it does back:
//
// - **Cover.** Rain falls around the camera and knew nothing of roofs. `WeatherCover.Sheltered` asks the
//   physics world whether anything solid is overhead (a brush roof, a rock ledge, a tree's collider) and
//   `WeatherSky.Exposure` follows the answer, eased over a third of a second so stepping through a door is a
//   fade and not a switch. The client multiplies the drops' rate by it and the weather sound by the record's
//   `ShelteredVolume` between 1 and that, so walking into the Sandbox hut stops the drops and muffles the rain.
//   An interior scene is covered whatever is above it.
// - **Lightning.** A weather with a `LightningRate` strikes at random (seeded, so a test and a replay agree):
//   a flash that rises at once and dies away in a flicker, as light on the sky and the ambient, outdoors
//   only; and thunder some seconds later by how far off it struck, which the client plays.
// - **The picker.** A `weather_pattern` record says what the weather can be at which hour and in which region,
//   and the world draws from it once every few hours, deterministically, and changes to it over a few
//   seconds. Nothing is picked without a pattern: the weather stays what it was set to.
//
// All of it is simulation, so it is the same on a server as on a screen and a test can ask; the client only
// reads `WeatherSky` and spawns drops and plays sounds by it.

// The picker's data (06 §3.13): which weather, when, where.
//
//   { "type": "weather_pattern", "id": "temperate", "slotHours": 3, "transition": 40, "picks": [
//       { "weather": "sage:clear", "weight": 4 },
//       { "weather": "sage:storm", "from": 14, "to": 22, "weight": 1 },
//       { "weather": "sage:snow", "region": "north", "weight": 3 } ] }
[Record("weather_pattern", Plugin = "sage.client")]
public sealed class WeatherPatternRecord
{
    public string Label = "";

    [Property(Min = 0.1f, Unit = "h", Tooltip = "How long a draw lasts, in game hours: the weather can change this often")]
    public float SlotHours = 3f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the weather takes to change to what was drawn")]
    public float Transition = 30f;
    [Property(Tooltip = "Mixed into every draw: two patterns with different seeds do not share a sequence of weather")]
    public int Seed;

    [Property(Tooltip = "What the weather can be; one is drawn by weight from those that apply to the hour and the region")]
    public List<WeatherPick> Picks = new();
}

// One thing the weather can be, and when it can be it.
public sealed class WeatherPick
{
    public RecordRef<WeatherRecord> Weather;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour of the day this can begin at (inclusive); with To, wraps past midnight")]
    public float From = 0f;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it stops being possible (exclusive); From 0 and To 24 is all day")]
    public float To = 24f;
    [Property(Tooltip = "Only in this region (the world's Weather.Region); empty is everywhere")]
    public string Region = "";
    [Property(Min = 0, Tooltip = "How likely, against the other picks that apply")]
    public float Weight = 1f;
}

// What is overhead and what is flashing, right now. Per world, never saved: a load starts uncovered and
// unlit, and the next tick says which it is.
internal sealed class WeatherSky
{
    // 1 under open sky, 0 under cover; eased. The client's drop rate and sound volume are read off it.
    public float Exposure { get; internal set; } = 1f;
    public bool Sheltered { get; internal set; }       // where Exposure is heading

    // The brightness of the flash now (0 none), before the exposure scales it: `Flash * Exposure` is what
    // reaches the light.
    public float Flash { get; internal set; }
    internal float FlashAge = -1f;
    internal float FlashPeak;
    internal int Strikes;                                // since the world began: for a test and a HUD

    internal readonly List<(float Due, float Volume)> Thunder = new();
    internal ulong RngState = 0x9E3779B97F4A7C15UL;

    // The next thunder that has arrived, if any: the client plays it. Volume is the weather's, before the
    // client mutes it for a roof.
    public bool TakeThunder(out float volume)
    {
        for (int i = 0; i < Thunder.Count; i++)
        {
            if (Thunder[i].Due > 0f) continue;
            volume = Thunder[i].Volume;
            Thunder.RemoveAt(i);
            return true;
        }
        volume = 0f;
        return false;
    }

    internal float NextUnit()
    {
        RngState += 0x9E3779B97F4A7C15UL;
        ulong z = RngState;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 40) / (float)(1UL << 24);
    }
}

internal static class WeatherCover
{
    [ThreadStatic] private static RayHit[]? Hits;
    private const float Reach = 200f;        // how far up a roof can be and still keep the rain off
    private const float FadePerSecond = 3f;  // exposure's rate: a third of a second from open to covered

    // Whether the point has something solid over it. An interior scene is always covered; otherwise a ray goes
    // up through whatever moves (a character, a dynamic prop is no roof) to the first static thing, if any.
    public static bool Sheltered(World world, Vector3 point)
    {
        if (Interiors.Active(world)) return true;
        if (!world.Resources.TryGet<IPhysicsWorld>(out var physics) || physics == null) return false;

        // Every collider along the way, nearest first: the first that is not something moving is a roof.
        var hits = Hits ??= new RayHit[16];
        int count = physics.RaycastAll(point, Vector3.UnitY, Reach, hits);
        for (int i = 0; i < count; i++)
            if (IsRoof(world, hits[i].Entity)) return true;
        return false;
    }

    private static bool IsRoof(World world, Entity entity)
    {
        if (!world.IsAlive(entity)) return true;      // a brush the level built is not always an entity of its own
        if (world.Has<CharacterController>(entity)) return false;
        return !(world.Has<RigidBody>(entity) && world.Get<RigidBody>(entity).Kind == BodyKind.Dynamic);
    }

    // Moves exposure toward what the world above the eye says.
    public static void Update(World world, WeatherSky sky, float dt)
    {
        bool covered = world.TryGetMainView(out var view) && Sheltered(world, view.Position);
        sky.Sheltered = covered;
        float target = covered ? 0f : 1f;
        sky.Exposure = MoveToward(sky.Exposure, target, FadePerSecond * dt);
    }

    private static float MoveToward(float value, float target, float step) =>
        value < target ? MathF.Min(target, value + step) : MathF.Max(target, value - step);
}

internal static class LightningRules
{
    public const float SpeedOfSound = 343f;
    private const float Lifetime = 1.4f;

    // How bright a strike is `age` seconds after it: a hard first flash and a dimmer second one, as a real
    // bolt flickers (1 at the strike, 0 after a second or so).
    public static float Envelope(float age)
    {
        if (age < 0f || age > Lifetime) return 0f;
        float e = MathF.Exp(-age * 9f);
        if (age > 0.18f) e += 0.6f * MathF.Exp(-(age - 0.18f) * 9f);
        return MathF.Min(1f, e);
    }

    public static void Strike(WeatherSky sky, WeatherRecord record)
    {
        sky.FlashAge = 0f;
        sky.FlashPeak = record.LightningFlash;
        sky.Strikes++;
        if (!record.Thunder.IsEmpty)
        {
            float distance = 200f + sky.NextUnit() * 2300f;
            sky.Thunder.Add((distance / SpeedOfSound, record.ThunderVolume));
        }
    }

    // One tick: a strike at random by the weather's rate, the flash running down, the thunder travelling.
    public static void Step(WeatherSky sky, WeatherRecord record, float rate, float dt)
    {
        if (rate > 0f && sky.NextUnit() < rate * dt / 60f) Strike(sky, record);

        if (sky.FlashAge >= 0f)
        {
            sky.FlashAge += dt;
            sky.Flash = sky.FlashPeak * Envelope(sky.FlashAge);
            if (sky.FlashAge > Lifetime) { sky.FlashAge = -1f; sky.Flash = 0f; }
        }

        // Thunder nobody took (a client that is not listening, a player indoors) is dropped after a while
        // rather than piling up.
        for (int i = sky.Thunder.Count - 1; i >= 0; i--)
        {
            var (due, volume) = sky.Thunder[i];
            due -= dt;
            if (due < -10f) sky.Thunder.RemoveAt(i);
            else sky.Thunder[i] = (due, volume);
        }
    }

    // The flash as light: the sky, the fog and the ambient brighten toward a cold white, by how much is
    // reaching this spot (so nothing under a roof flickers).
    public static void Apply(WeatherSky sky, RenderEnvironment environment)
    {
        float f = sky.Flash * sky.Exposure;
        if (f <= 0f) return;
        var light = new Vector3(0.85f, 0.9f, 1f) * f;
        environment.AmbientSky += light;
        environment.AmbientGround += light * 0.5f;
        environment.ClearColor += light * 0.6f;
        environment.Zenith += light * 0.6f;
        environment.FogColor += light * 0.5f;
    }
}

// The deterministic draw (issue #311): what the pattern makes of this slot of game time.
internal static class WeatherPicker
{
    public static long SlotOf(WorldClock clock, WeatherPatternRecord pattern) =>
        (long)Math.Floor(clock.Elapsed / Math.Max(0.1f, pattern.SlotHours));

    // The pick for slot `slot` in `region`: by the hour the slot begins at, weighted, and the same every time
    // it is asked. Empty when nothing applies (the weather then stays as it is).
    public static RecordId Pick(WeatherPatternRecord pattern, long slot, string region)
    {
        double hour = slot * (double)Math.Max(0.1f, pattern.SlotHours);
        hour -= Math.Floor(hour / 24.0) * 24.0;

        float total = 0f;
        foreach (var pick in pattern.Picks)
            if (Applies(pick, hour, region)) total += pick.Weight;
        if (total <= 0f) return default;

        ulong z = (ulong)slot * 0x9E3779B97F4A7C15UL + (ulong)(uint)pattern.Seed * 0xD1B54A32D192ED03UL + 0x2545F4914F6CDD1DUL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        float roll = (z >> 40) / (float)(1UL << 24) * total;

        RecordId last = default;
        foreach (var pick in pattern.Picks)
        {
            if (!Applies(pick, hour, region)) continue;
            last = pick.Weather.Id;
            roll -= pick.Weight;
            if (roll < 0f) return last;
        }
        return last;
    }

    private static bool Applies(WeatherPick pick, double hour, string region)
    {
        if (pick.Weight <= 0f || pick.Weather.Id.IsEmpty) return false;
        if (pick.Region.Length > 0 && !string.Equals(pick.Region, region, StringComparison.OrdinalIgnoreCase)) return false;
        return pick.To - pick.From >= 24f || WorldClock.Between(hour, pick.From, pick.To);
    }
}

// Fixed Late, before the sky: the weather moves, the picker draws, the ground wets or dries, the camera looks
// up, lightning strikes.
// Headless, so a server's weather and a test's are the client's.
[System(Id, Phase.Late, Before = new[] { "sage.world.sky" })]
internal sealed class WeatherSystem : ISystem
{
    public const string Id = "sage.world.weather";

    private readonly World _world;

    public WeatherSystem(World world) => _world = world;

    public void Run(in SystemContext ctx)
    {
        var resources = _world.Resources;
        if (!resources.TryGet<Weather>(out var weather) || weather == null) return;
        float dt = ctx.Tick.Dt;
        weather.Advance(dt);
        if (!resources.TryGet<RecordStore>(out var records) || records == null) return;

        Draw(weather, records, resources);
        WetnessRules.Step(weather, records, dt);

        if (!resources.TryGet<WeatherSky>(out var sky) || sky == null) return;
        WeatherCover.Update(_world, sky, dt);
        LightningRules.Step(sky, WeatherRules.Falling(records, weather), WeatherRules.LightningRateNow(records, weather), dt);
    }

    private static void Draw(Weather weather, RecordStore records, WorldResources resources)
    {
        if (weather.Pattern.IsEmpty || !resources.TryGet<WorldClock>(out var clock) || clock == null) return;
        if (records.TypeNameOf(typeof(WeatherPatternRecord)) == null || !records.TryGet(weather.Pattern, out WeatherPatternRecord pattern)) return;

        long slot = WeatherPicker.SlotOf(clock, pattern);
        if (slot == weather.PickedSlot) return;
        weather.PickedSlot = slot;
        var pick = WeatherPicker.Pick(pattern, slot, weather.Region);
        if (!pick.IsEmpty) weather.Set(pick, pattern.Transition);
    }
}
