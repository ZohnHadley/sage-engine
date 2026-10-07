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

    [Property(Category = "Attributes", Tooltip = "The attribute that scales a character's walk and run speed (1 = as its movement profile says): what a heavy pack or a slowing spell lowers; empty = nothing does (issue #384)")]
    public RecordRef<AttributeRecord> SpeedAttribute;

    [Property(Category = "Combat", Tooltip = "The damage type of a hit that names none")]
    public RecordRef<DamageTypeRecord> DamageType;
    [Property(Category = "Combat", Tooltip = "The attack of a fighter that was given none")]
    public RecordRef<AttackRecord> Attack;
#pragma warning disable SAGE0127 // hit locations are phase 4e's experimental API
    [Property(Category = "Combat", Tooltip = "Which creatures keep their hitboxes on (issue #273); empty, every creature's are always on")]
    public RecordRef<HitboxBudgetRecord> HitboxBudget;
#pragma warning restore SAGE0127

    [Property(Category = "Profiles", Tooltip = "How a character with no movement profile of its own moves")]
    public RecordRef<MovementProfileRecord> Movement;
    [Property(Category = "Profiles", Tooltip = "How a creature with no AI profile of its own sees and thinks")]
    public RecordRef<AIProfileRecord> AiProfile;

    [Property(Category = "Factions", Tooltip = "The faction a player-controlled entity with no faction of its own belongs to")]
    public RecordRef<FactionRecord> PlayerFaction;

    [Property(Category = "Abilities", Tooltip = "The attribute a spell made in the spellmaker costs (mana)")]
    public RecordRef<AttributeRecord> CostAttribute;

    [Property(Category = "Surfaces", Tooltip = "What ground with no surface, or a surface with no footstep cue, sounds like underfoot (issue #327)")]
    public RecordRef<PhysicsMaterialRecord> Surface;

    // The schedules the AI's built-in choice picks between (16 §3.4).
    public AIScheduleConventions Schedules = new();

    // The input actions gameplay reads, by name (08 §3.2). There is no cast button here: the base casts
    // what it is asked to (world.Cast); a button that fires a readied spell is a kit's model
    // (Sage.Kits.Rpg's `rpg_conventions`, issue #27).
    public ActionConventions Actions = new();

    // The words combat and animation share (12 §3, issues #27, #119): the trigger a swing sets on the
    // fighter's animator and the clip event that lands it. Clip names are the anim_graph's now.
    public AnimationConventions Animations = new();

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
    [Property(Tooltip = "The button that jumps")]
    public string Jump = "Jump";
    [Property(Tooltip = "The button held to run")]
    public string Run = "Run";
    [Property(Tooltip = "The button held to crouch")]
    public string Crouch = "Crouch";
    [Property(Tooltip = "The button that reloads: plays the first-person arms' reload and refills the magazine of an attack with `ammo` (issue #135)")]
    public string Reload = "Reload";
    [Property(Tooltip = "The button held to guard: sets the fighter's AttackStance.Blocking, which an anim_graph reads (`from: Blocking`) to pick its block (issue #359)")]
    public string Block = "Block";

    internal (string Field, string Name)[] All() => new[]
    {
        (nameof(Attack), Attack), (nameof(Use), Use), (nameof(Jump), Jump), (nameof(Run), Run), (nameof(Crouch), Crouch),
        (nameof(Reload), Reload), (nameof(Block), Block),
    };
}

// Since issue #119 combat names no clip: a swing sets a trigger on the fighter's animator, whose
// anim_graph picks the clip (a skinned model's or a sprite sheet's), and the blow lands on the clip's
// `hit` event. `Attack` and `Idle` were the clip names combat played; they are kept, obsolete, so old
// content still loads, and read only by the upgrade: a fighter drawn as an animated sprite with no graph
// of its own is given one that plays them (SpriteFighterUpgrade).
public sealed class AnimationConventions
{
    [Obsolete("Clip names are the anim_graph's since issue #119: a swing sets AttackTrigger. Read only by the upgrade of a sprite fighter with no graph.")]
    [Property(Tooltip = "Obsolete (#119): the clip a sprite fighter with no anim_graph of its own swings with")]
    public string Attack = "attack";
    [Property(Tooltip = "The clip event that lands a blow (anim_events, a sprite sheet's frame events); without one the attack's windup time does")]
    public string Hit = "hit";
    [Obsolete("Clip names are the anim_graph's since issue #119. Read only by the upgrade of a sprite fighter with no graph.")]
    [Property(Tooltip = "Obsolete (#119): the clip a sprite fighter with no anim_graph of its own stands in")]
    public string Idle = "idle";
    [Property(Tooltip = "The trigger param a swing sets on the fighter's animator when its attack names none (Animators.SetTrigger)")]
    public string AttackTrigger = "attack";
    [Property(Tooltip = "The trigger the Reload button sets on the first-person arms' anim_graph (a swing sets `attackTrigger`)")]
    public string Reload = "reload";
    [Property(Tooltip = "The clip event that puts the magazine in: a reload completes on it, else after the attack's reloadTime (issue #135)")]
    public string MagIn = "mag_in";
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
#pragma warning disable CS0618 // the obsolete clip names: said once, where they are written
        var animations = conventions.Animations;
        if (animations != null && (animations.Attack != "attack" || animations.Idle != "idle"))
            check.Warn("Animations", $"`attack` and `idle` are obsolete since issue #119 (clip names are an anim_graph's): a sprite fighter with no graph " +
                                     $"of its own is given one that plays '{animations.Idle}' and swings with '{animations.Attack}'; " +
                                     "give its `sprite` part a `graph` to say so yourself");
#pragma warning restore CS0618
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

// The upgrade (issue #119): until then combat played the conventions' `attack` clip on a sprite when a
// swing started and their `idle` clip when it ended. A fighter drawn as an animated sprite that has no
// animator of its own is given one running a graph that does just that (Animators.SpriteSwingGraph):
// it stands in the idle clip, plays the attack clip once on the conventions' AttackTrigger, and goes back
// when the clip is done. So content from before #119 swings, and lands on its `hit` frame, on the same
// ticks as before. Called where a swing starts, before its trigger is set.
internal static class SpriteFighterUpgrade
{
    public static void Apply(World world, Entity entity, GameplayConventionsRecord conventions, RecordStore records)
    {
        if (world.Has<Animator>(entity) || !world.Has<SpriteAnimator>(entity) || !world.Has<SpriteRenderer>(entity)) return;
        var animations = conventions.Animations;
        if (animations == null || string.IsNullOrEmpty(animations.AttackTrigger)) return;
#pragma warning disable CS0618 // what the upgrade reads
        if (string.IsNullOrEmpty(animations.Attack)) return;
        var graph = Animators.SpriteSwingGraph(records, animations.Idle ?? "", animations.Attack, animations.AttackTrigger);
#pragma warning restore CS0618
        world.Add(entity, new Animator { Graph = graph });
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
