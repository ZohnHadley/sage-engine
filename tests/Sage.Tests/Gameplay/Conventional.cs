#nullable enable

namespace Sage.Tests;

// The engine's own spellings of the conventional ids, as the tests' content writes them (records in
// the `sage` namespace), for tests that assert on them. Gameplay code no longer knows these (issue
// #26): it reads the gameplay_conventions record, which each test's content supplies (the engine's
// is in engine_content/data/conventions.json, and tests do not mount engine content).
internal static class Conventional
{
    public static readonly RecordId Health = new("sage", "health");
    public static readonly RecordId Invulnerable = new("sage", "state.invulnerable");

    public static readonly RecordId Idle = new("sage", "idle");
    public static readonly RecordId Chase = new("sage", "chase");
    public static readonly RecordId MeleeAttack = new("sage", "melee_attack");
    public static readonly RecordId CastSpell = new("sage", "cast_spell");
    public static readonly RecordId HoldGround = new("sage", "hold_ground");
}
