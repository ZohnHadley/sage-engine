#nullable enable
using System;

namespace Sage.Gameplay;

// What a game means by its words (REDESIGN §3.1, §4.1, issue #26).
//
// Gameplay code has to know *some* ids: which attribute is the one that kills you when it runs out,
// which tag says you are dead, what an attack does when nobody said, which schedule a creature idles
// with, which button swings. Those used to be `new RecordId("sage", …)` constants scattered through
// the gameplay assembly, so a game that called its life "hp" or its dead "state.fallen" had to fork
// the engine — and every game repeated the engine's spellings as string literals.
//
// **One record says them all**, and code reads it instead of a constant. The engine ships a default
// (`sage:default_conventions`, engine_content/data/conventions.json); a kit or a game patches it
// (`"patch": true`), which is how a game renames "health" to "hp" by changing one record. A field
// left empty means the game has no such thing: with no `health`, nothing dies; with no `attack`, a
// fighter who was given none cannot swing. The action names are the exception: actions are
// registered by modules in `Init`, before content loads, so they default to the names the engine's
// modules register, and a game that wants its own registers them in its module and names them here
// (checked when content loads).
//
// Without a conventions record at all (a test that mounts no engine content, a game without the
// attributes plugin), every id is empty and the action names are the engine's.
[Record("gameplay_conventions", Plugin = "sage.gameplay.attributes")]
public sealed class GameplayConventionsRecord
{
    [Property(Category = "Attributes", Tooltip = "The attribute that is an entity's life: when it reaches zero the entity dies")]
    public RecordRef<AttributeRecord> Health;
    [Property(Category = "Attributes", Tooltip = "The tag a death adds; the dead cannot swing, cast or be chosen as a target")]
    public RecordRef<TagRecord> Dead;
    [Property(Category = "Attributes", Tooltip = "The tag the `god` cheat toggles on the local player")]
    public RecordRef<TagRecord> Invulnerable;

    [Property(Category = "Combat", Tooltip = "The damage type of a hit that names none")]
    public RecordRef<DamageTypeRecord> DamageType;
    [Property(Category = "Combat", Tooltip = "The attack of a fighter that was given none")]
    public RecordRef<AttackRecord> Attack;

    [Property(Category = "Profiles", Tooltip = "How a character with no movement profile of its own moves")]
    public RecordRef<MovementProfileRecord> Movement;
    [Property(Category = "Profiles", Tooltip = "How a creature with no AI profile of its own sees and thinks")]
    public RecordRef<AIProfileRecord> AiProfile;

    [Property(Category = "Factions", Tooltip = "The faction a player-controlled entity with no faction of its own belongs to")]
    public RecordRef<FactionRecord> PlayerFaction;

    [Property(Category = "Abilities", Tooltip = "The attribute a spell made in the spellmaker costs (mana)")]
    public RecordRef<AttributeRecord> CostAttribute;

    // The schedules the AI's built-in choice picks between (16 §3.4).
    public AIScheduleConventions Schedules = new();

    // The input actions gameplay reads, by name (08 §3.2).
    public ActionConventions Actions = new();

    // The one well-known instance: what the engine reads, and what a game patches. The only
    // engine-namespace record id gameplay code names (NoGameplayCodeNamesAnEngineRecordId).
    public static readonly RecordId DefaultId = new(ComponentSchema.EngineNamespace, "default_conventions");

    // What a world has when there is no conventions record: no ids, the engine's action names.
    public static readonly GameplayConventionsRecord None = new();
}

public sealed class AIScheduleConventions
{
    [Property(Tooltip = "Nothing to do: no enemy seen or remembered")]
    public RecordRef<AIScheduleRecord> Idle;
    [Property(Tooltip = "An enemy seen or remembered, out of reach")]
    public RecordRef<AIScheduleRecord> Chase;
    [Property(Tooltip = "An enemy in melee range, and something to swing")]
    public RecordRef<AIScheduleRecord> MeleeAttack;
    [Property(Tooltip = "A spell ready and an enemy to throw it at, or a cast under way")]
    public RecordRef<AIScheduleRecord> CastSpell;
    [Property(Tooltip = "Waiting for a spell to come back, with nothing to swing")]
    public RecordRef<AIScheduleRecord> HoldGround;
}

public sealed class ActionConventions
{
    [Property(Tooltip = "The button that swings (combat, and the AI's MeleeAttack task)")]
    public string Attack = "Attack";
    [Property(Tooltip = "The button that uses what is in front of you")]
    public string Use = "Use";
    [Property(Tooltip = "The button that casts the readied spell")]
    public string Cast = "Cast";
    [Property(Tooltip = "The button that jumps")]
    public string Jump = "Jump";
    [Property(Tooltip = "The button held to run")]
    public string Run = "Run";
    [Property(Tooltip = "The button held to crouch")]
    public string Crouch = "Crouch";

    internal (string Field, string Name)[] All() => new[]
    {
        (nameof(Attack), Attack), (nameof(Use), Use), (nameof(Cast), Cast),
        (nameof(Jump), Jump), (nameof(Run), Run), (nameof(Crouch), Crouch),
    };
}

public static class GameplayConventions
{
    // The conventions this record store holds: `sage:default_conventions` as the engine ships it and
    // the game patched it, or `None`. A dictionary lookup, so a system asks once per run rather than
    // keeping a copy that a reload would leave stale (05 §3.6).
    public static GameplayConventionsRecord Of(RecordStore records) =>
        records.TypeNameOf(typeof(GameplayConventionsRecord)) != null
        && records.TryGet(GameplayConventionsRecord.DefaultId, out GameplayConventionsRecord conventions)
            ? conventions : GameplayConventionsRecord.None;

    public static GameplayConventionsRecord Conventions(this World world) => Of(world.Records());

    // Action names are checked when content loads: an action nothing registered would be a button that
    // does nothing, silently (08 §3.2).
    internal static void CheckActions(ActionRegistry actions, GameplayConventionsRecord conventions, RecordCheck check)
    {
        foreach (var (field, name) in conventions.Actions.All())
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!actions.TryGet(name, out var info))
                check.Error($"Actions.{field}", $"no input action '{name}' is registered; a module registers it in Init " +
                                                $"(actions.Register(\"{name}\", ActionKind.Button))");
            else if (info.Kind != ActionKind.Button)
                check.Error($"Actions.{field}", $"input action '{name}' is {info.Kind}, not a button");
        }
    }
}

// The character controller's share of the conventions (issue #26). It sits in Sage.Physics3D, below
// gameplay, so it cannot read this record; gameplay installs this in each world, and the controller
// reads the default movement profile and its action names through it.
internal sealed class CharacterConventionsFromRecord : CharacterConventions
{
    private readonly RecordStore _records;

    public CharacterConventionsFromRecord(RecordStore records) { _records = records; }

    public override RecordId DefaultProfile => GameplayConventions.Of(_records).Movement;
    public override string JumpAction => GameplayConventions.Of(_records).Actions.Jump;
    public override string RunAction => GameplayConventions.Of(_records).Actions.Run;
    public override string CrouchAction => GameplayConventions.Of(_records).Actions.Crouch;
}
