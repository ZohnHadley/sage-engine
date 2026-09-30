#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Gameplay;

// Spread and recoil (issue #136, phase 4e; docs/design/16 "As built (spread and recoil)").
//
// An `attack` may name a `spread` (how far off the aim a shot may fly, growing with every shot and
// recovering between them) and a `recoil` (how far the muzzle climbs and drifts, and how fast it comes
// back). Both are records; what they do while a weapon fires is a transient `sage:weapon_state` on the
// wielder. Every random number is ShotRandom's: a hash of the tick, the entity and the shot, so a replay
// and a future server throw the same pellets. Recoil is written into the wielder's PawnIntent, so a
// player's view and an AI's aim kick the same way.

// How far a shot may stray. Angles are half-angles of a cone around the aim, in degrees.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Record("spread", Plugin = "sage.gameplay.combat")]
public sealed class SpreadRecord
{
    [Property(Min = 0, Unit = "deg", Tooltip = "The cone at rest, standing still: a shot may stray this far from the aim")]
    public float BaseDegrees = 0.5f;
    [Property(Min = 0, Unit = "deg", Tooltip = "How much wider the cone gets with every shot")]
    public float GrowthPerShot = 0.8f;
    [Property(Min = 0, Unit = "deg", Tooltip = "The widest the cone grows to before the movement multipliers")]
    public float MaxDegrees = 6f;
    [Property(Min = 0, Unit = "deg/s", Tooltip = "How fast the growth shrinks back while not firing")]
    public float RecoveryPerSecond = 6f;
    [Property(Min = 0, Tooltip = "The cone is multiplied by this while moving")]
    public float MovingMultiplier = 1.5f;
    [Property(Min = 0, Tooltip = "The cone is multiplied by this while crouching")]
    public float CrouchingMultiplier = 0.6f;
}

// How a shot kicks the wielder's aim: a pitch kick up and a yaw kick either way, each drawn from its range
// for every shot, which then return at `ReturnSpeedDegrees`.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Record("recoil", Plugin = "sage.gameplay.combat")]
public sealed class RecoilRecord
{
    [Property(Unit = "deg", Tooltip = "The least a shot lifts the aim (negative lowers it)")]
    public float PitchMinDegrees = 0.8f;
    [Property(Unit = "deg", Tooltip = "The most a shot lifts the aim")]
    public float PitchMaxDegrees = 1.4f;
    [Property(Unit = "deg", Tooltip = "The furthest a shot turns the aim left (negative) ...")]
    public float YawMinDegrees = -0.4f;
    [Property(Unit = "deg", Tooltip = "... and right (positive)")]
    public float YawMaxDegrees = 0.4f;
    [Property(Min = 0, Unit = "deg/s", Tooltip = "How fast the aim comes back down after a kick")]
    public float ReturnSpeedDegrees = 8f;
}

// What a firing weapon is doing right now, on the wielder. Never saved: a load starts with a steady aim.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Transient]
[Component("sage:weapon_state")]
public struct WeaponState : IComponent
{
    public float Bloom;          // degrees added to the base cone by the shots so far
    public float Recovery;       // degrees per second the bloom shrinks by (from the spread that grew it)
    public uint Shots;           // shots taken: the third input to ShotRandom
    public float KickPitch;      // radians of recoil still in the aim
    public float KickYaw;
    public float ReturnSpeed;    // radians per second the kick shrinks by
    public float AppliedPitch;   // what the recoil system last added to the PawnIntent, radians: lets it tell
    public float AppliedYaw;     // whether a controller rewrote the intent since (then nothing is taken back)
    public float LastPitch;      // and the intent values it left, to compare with
    public float LastYaw;
}

// The deterministic random source: a hash (SplitMix64) of the tick, the entity and the shot, never
// System.Random, so the same shots throw the same directions on every run, machine and server.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public static class ShotRandom
{
    // 64 well-mixed bits for (tick, entity, shot, salt).
    public static ulong Hash(long tick, int entity, uint shot, uint salt)
    {
        ulong x = (ulong)tick * 0x9E3779B97F4A7C15UL;
        x ^= (ulong)(uint)entity * 0xBF58476D1CE4E5B9UL;
        x ^= (((ulong)shot << 32) | salt) * 0x94D049BB133111EBUL;
        return Mix(x);
    }

    // 0 <= value < 1.
    public static float Value(long tick, int entity, uint shot, uint salt) =>
        (Hash(tick, entity, shot, salt) >> 40) * (1f / (1 << 24));

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public static class Spread
{
    private const float Deg = MathF.PI / 180f;
    private const float MovingSpeed = 0.5f;   // m/s of horizontal velocity that counts as moving

    // `aim` turned by up to `coneRadians` (a half-angle), spread evenly over the cone's disc. A cone of 0
    // returns `aim` untouched. `pellet` separates the rays of one shot.
    public static Vector3 Deflect(Vector3 aim, float coneRadians, long tick, int entity, uint shot, int pellet)
    {
        if (coneRadians <= 0f) return aim;
        uint salt = (uint)pellet * 2u;
        float angle = coneRadians * MathF.Sqrt(ShotRandom.Value(tick, entity, shot, salt));
        float around = ShotRandom.Value(tick, entity, shot, salt + 1u) * (2f * MathF.PI);

        // Any axes perpendicular to the aim do: the spin is random anyway.
        var up = MathF.Abs(aim.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(aim, up));
        var above = Vector3.Cross(right, aim);
        var off = right * MathF.Cos(around) + above * MathF.Sin(around);
        return Vector3.Normalize(aim * MathF.Cos(angle) + off * MathF.Sin(angle));
    }

    // The cone (half-angle, radians) the wielder's next shot may stray within.
    public static float ConeOf(World world, Entity shooter, SpreadRecord spread)
    {
        float bloom = world.TryGet<WeaponState>(shooter, out var state) ? state.Bloom : 0f;
        float degrees = MathF.Min(spread.BaseDegrees + bloom, MathF.Max(spread.MaxDegrees, spread.BaseDegrees));
        if (world.TryGet<CharacterController>(shooter, out var body))
        {
            if (body.Crouching) degrees *= spread.CrouchingMultiplier;
            if (new Vector2(body.Velocity.X, body.Velocity.Z).LengthSquared() > MovingSpeed * MovingSpeed) degrees *= spread.MovingMultiplier;
        }
        return degrees * Deg;
    }

    // The shot has left: the cone widens and the aim kicks. Draws the kick from the tick, the entity and
    // the shot, like the pellets.
    internal static void Fired(World world, Entity shooter, SpreadRecord? spread, RecoilRecord? recoil)
    {
        if (spread == null && recoil == null) return;
        if (!world.Has<WeaponState>(shooter)) world.Add(shooter, new WeaponState());
        ref var state = ref world.Get<WeaponState>(shooter);
        uint shot = state.Shots++;
        if (spread != null)
        {
            state.Bloom = MathF.Min(state.Bloom + spread.GrowthPerShot, MathF.Max(spread.MaxDegrees - spread.BaseDegrees, 0f));
            state.Recovery = spread.RecoveryPerSecond;
        }
        if (recoil != null)
        {
            float pitch = Lerp(recoil.PitchMinDegrees, recoil.PitchMaxDegrees, ShotRandom.Value(world.Tick, shooter.Id, shot, 0xFFFF_FFF0u));
            float yaw = Lerp(recoil.YawMinDegrees, recoil.YawMaxDegrees, ShotRandom.Value(world.Tick, shooter.Id, shot, 0xFFFF_FFF1u));
            state.KickPitch += pitch * Deg;
            state.KickYaw += yaw * Deg;
            state.ReturnSpeed = recoil.ReturnSpeedDegrees * Deg;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

// Commands phase, after whatever writes PawnIntent (the player's controller, the AI, a camera lock): lets
// the bloom recover and the kick return, and adds what is left of the kick to the intent's yaw and
// pitch, so everything downstream — the shot, the camera, the animator's aim — sees the lifted aim. The
// controller rewrites the intent every tick, so the kick is an offset and never accumulates in the view;
// an intent nothing rewrites is handed back what was added first.
[System("sage.combat.recoil", Phase.Commands, After = new[] { "?sage.character.player_control", "?sage.ai.think", "?sage.camera.input_lock" })]
internal sealed class RecoilSystem : ISystem
{
    private readonly Query<PawnIntent, WeaponState> _weapons;

    public RecoilSystem(World world) => _weapons = world.Query<PawnIntent, WeaponState>();

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        foreach (var (intents, states, _) in _weapons.Chunks)
        {
            var intent = intents.Span;
            var state = states.Span;
            for (int n = 0; n < state.Length; n++)
            {
                ref var s = ref state[n];
                ref var i = ref intent[n];

                // A controller that did not write this tick left our own value: take our offset back first.
                float basePitch = i.Pitch == s.LastPitch ? i.Pitch - s.AppliedPitch : i.Pitch;
                float baseYaw = i.Yaw == s.LastYaw ? i.Yaw - s.AppliedYaw : i.Yaw;

                if (s.Bloom > 0f) s.Bloom = MathF.Max(0f, s.Bloom - s.Recovery * dt);
                float length = MathF.Sqrt(s.KickPitch * s.KickPitch + s.KickYaw * s.KickYaw);
                if (length > 0f)
                {
                    float left = MathF.Max(0f, length - s.ReturnSpeed * dt);
                    float scale = left / length;
                    s.KickPitch = left > 0f ? s.KickPitch * scale : 0f;
                    s.KickYaw = left > 0f ? s.KickYaw * scale : 0f;
                }

                s.AppliedPitch = s.KickPitch;
                s.AppliedYaw = s.KickYaw;
                i.Pitch = basePitch + s.KickPitch;
                i.Yaw = baseYaw + s.KickYaw;
                s.LastPitch = i.Pitch;
                s.LastYaw = i.Yaw;
            }
        }
    }
}
