#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace sage_engine;

// Making your own spells (docs/design/16 §3.3, TODO F21). The Daggerfall feature: you buy effects,
// choose how strong and how they are delivered, name the result, and it goes in your book for ever.
//
// It costs the engine almost nothing, which is the point of everything F18 through F21 built. A spell
// is already "a cost, a cooldown, targeting and a list of effects" — so composing one is *filling in
// an `ability` record*, and the cast system cannot tell the difference between a spell that came from
// a content file and one the player invented ten seconds ago.
//
// What the save holds is the **draft**, not the record (09 §3.1): the player's choices are the data,
// the record is derived from them. That way a custom spell keeps working when the effects it is made
// of are rebalanced, and a save does not carry a frozen copy of content it does not own.

// What the player chose. This is the saved shape.
public sealed class SpellDraft
{
    public string Name = "";
    public AbilityTargeting Targeting = AbilityTargeting.Touch;
    public List<RecordId> Effects = new();
    public float Magnitude = 1f;      // how strong, scaling both the effects and the price
    public float Range = 12f;
    public float Radius;
    public float Damage;
    public RecordId DamageType;
    public RecordId Projectile;
    public float ProjectileSpeed = 16f;
}

// A world's book of composed spells: the drafts, and nothing else. It is a saved resource (09 §3.1)
// rather than a component on the player because it is the *world's* magic — in a party game it is not
// one character's, and the same book is what a spellmaker shop would list.
[SavedResource("spellbook")]
public sealed class Spellbook : ISavedResource
{
    public List<SpellDraft> Drafts { get; set; } = new();

    // Loading installs this book and then asks it to make its records again (09 §3.1). Nothing else
    // has to know that custom spells exist: a save with none clears the last game's, because the save
    // system replaces every registered resource whether the file mentioned it or not.
    public void AfterLoad(World world) => Spellmaker.Restore(world);
}

public readonly record struct SpellResult(bool Ok, RecordId Id, float Cost, string Problem)
{
    public static SpellResult Failed(string problem) => new(false, default, 0f, problem);
}

public static class Spellmaker
{
    // Where composed spells live. A namespace of their own, so nothing a player made can shadow or be
    // mistaken for content — and so `rec_list` shows at a glance which is which.
    public const string Namespace = "custom";

    // Prices a draft without making it, for a spellmaker screen that has to show a number before the
    // player commits. The formula is deliberately in one place and deliberately simple: each effect
    // costs what its record says, the whole thing scales with magnitude, and delivery adds to it
    // because reaching further and hitting more is worth more.
    public static float Price(RecordStore records, SpellDraft draft)
    {
        float effects = 0f;
        foreach (var id in draft.Effects)
            effects += records.TryGet(id, out EffectRecord effect) ? MathF.Max(effect.Cost, 0f) : 0f;

        float delivery = draft.Targeting switch
        {
            AbilityTargeting.Self => 0.5f,
            AbilityTargeting.Touch => 1f,
            AbilityTargeting.Area => 1.6f,
            AbilityTargeting.TouchArea => 1.8f,
            AbilityTargeting.Projectile => 1.4f,
            _ => 1f,
        };

        // Only what the delivery actually uses: a spell cast on yourself pays for no range, and one
        // with no burst pays for no radius, or every price carries a default nobody chose.
        bool reaches = draft.Targeting is AbilityTargeting.Touch or AbilityTargeting.TouchArea or AbilityTargeting.Projectile;
        bool bursts = draft.Targeting is AbilityTargeting.Area or AbilityTargeting.TouchArea;
        float reach = 1f
                    + (reaches ? MathF.Max(draft.Range - 6f, 0f) * 0.02f : 0f)
                    + (bursts ? draft.Radius * 0.12f : 0f);

        float damage = draft.Damage * 0.35f;
        // At least one: rounding a cheap spell down to free would make it castable for ever.
        return MathF.Max(MathF.Round((effects + damage) * MathF.Max(draft.Magnitude, 0.1f) * delivery * reach), 1f);
    }

    // Whether this draft could be composed, and why not (R17). Asked by `Compose` itself and by a
    // spellmaker screen to grey out its "make it" row — one set of rules, so the button and the
    // attempt cannot disagree, and the words are the ones a player reads either way.
    public static bool CanCompose(World world, SpellDraft draft, out string problem)
    {
        problem = "";
        var engine = world.Engine;
        if (engine is null) { problem = "this world has no engine"; return false; }

        string name = draft.Name.Trim();
        if (name.Length == 0) { problem = "a spell needs a name"; return false; }
        if (draft.Effects.Count == 0 && draft.Damage <= 0f)
        {
            problem = "a spell needs at least one effect, or some damage";
            return false;
        }

        foreach (var id in draft.Effects)
        {
            if (!engine.Records.TryGet(id, out EffectRecord effect)) { problem = $"there is no effect called {id}"; return false; }
            // Priced at zero means the game applies it itself — a cooldown, a mana spend — and is not
            // a thing to build with. Saying so is the difference between a spellmaker and a cheat.
            if (effect.Cost <= 0f) { problem = $"{id} is not an effect you can put in a spell"; return false; }
        }

        var recordId = new RecordId(Namespace, Slug(name));
        if (Book(world).Drafts.Any(d => Slug(d.Name) == recordId.Name))
        {
            problem = $"you already know a spell called \"{name}\"";
            return false;
        }
        // And nothing else may be standing on the id: another world's composed spell, or a mount that
        // happens to be called `custom`. Overwriting a record that exists is the one thing the record
        // pipeline refuses to do quietly (05 §3.5), and composing is not an exception to that.
        if (engine.Records.Exists(recordId)) { problem = $"the name \"{name}\" is taken"; return false; }
        return true;
    }

    // Composes a draft into a real `ability` and puts it in the world's book. Refusals say why, in
    // words a spellmaker screen can show: this is the one place a player's composition meets the rules.
    public static SpellResult Compose(World world, SpellDraft draft)
    {
        if (!CanCompose(world, draft, out string problem)) return SpellResult.Failed(problem);

        var engine = world.Engine!;
        string name = draft.Name.Trim();
        var book = Book(world);
        var recordId = new RecordId(Namespace, Slug(name));
        float cost = Price(engine.Records, draft);
        Register(engine, recordId, draft, cost);
        book.Drafts.Add(draft);
        Log.Info(LogCat.Gameplay, $"Composed {recordId} \"{name}\": {cost:F0} mana, {draft.Effects.Count} effect(s)");
        return new SpellResult(true, recordId, cost, "");
    }

    // Rebuilds every spell in the book as a record. Called after a load: the drafts came back with the
    // world, and the records are derived from them — which is why a rebalanced effect changes a
    // player's old spell instead of being frozen into it.
    public static int Restore(World world)
    {
        var engine = world.Engine;
        if (engine is null) return 0;

        // Out with the last game's, so a load cannot leave behind spells nothing composed. The record
        // cache is the *engine's* and a book is a *world's*, so what is kept is what any loaded world
        // still accounts for — clearing the whole namespace would wipe the other world's magic.
        //
        // Two worlds that both composed "firebolt" would share the one `custom:firebolt` record, which
        // is inherent to one engine-wide namespace and waits for maps and sectors (09 §3.4) to matter.
        var wanted = new HashSet<RecordId>();
        foreach (var other in engine.Worlds)
            if (other.Resources.TryGet<Spellbook>(out var book) && book is not null)
                foreach (var draft in book.Drafts)
                    if (!string.IsNullOrWhiteSpace(draft.Name)) wanted.Add(new RecordId(Namespace, Slug(draft.Name)));

        foreach (var (_, id, record) in engine.Records.RuntimeRecords.ToList())
            if (record is AbilityRecord && id.Namespace == Namespace && !wanted.Contains(id))
                engine.Records.RemoveRuntime<AbilityRecord>(id);

        int made = 0;
        foreach (var draft in Book(world).Drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Name)) continue;
            Register(engine, new RecordId(Namespace, Slug(draft.Name)), draft, Price(engine.Records, draft));
            made++;
        }
        if (made > 0) Log.Info(LogCat.Gameplay, $"Restored {made} composed spell(s)");
        return made;
    }

    // Unmakes one. The entities that knew it keep the id in their spellbook and the cast is refused as
    // `NotKnown`, which is what happens to any ability whose record went away — a mod being removed
    // does the same thing, so there is one behaviour rather than a special case for forgetting.
    public static bool Forget(World world, string name)
    {
        var engine = world.Engine;
        if (engine is null) return false;

        string slug = Slug(name);
        if (Book(world).Drafts.RemoveAll(d => Slug(d.Name) == slug) == 0) return false;
        engine.Records.RemoveRuntime<AbilityRecord>(new RecordId(Namespace, slug));
        return true;
    }

    public static Spellbook Book(World world)
    {
        if (!world.Resources.TryGet<Spellbook>(out var book) || book is null)
        {
            book = new Spellbook();
            world.Resources.Set(book);
        }
        return book;
    }

    private static void Register(Engine engine, RecordId id, SpellDraft draft, float cost)
    {
        engine.Records.AddRuntime(id, new AbilityRecord
        {
            Name = draft.Name.Trim(),
            // v1: spells cost mana, the engine's own pool (`engine_content/data/gameplay.json`). A game
            // whose magic runs on something else needs this to become a choice — a field on the draft,
            // or a rule on `GameRules` — and nothing else here changes.
            CostAttribute = Mana,
            Cost = cost,
            // `Cooldown` is deliberately left unset: a composed spell is gated by its cost, which is
            // the bargain Daggerfall's spellmaker struck. When a cooldown becomes something the player
            // chooses, it is another field on the draft and another term in the price.
            Targeting = draft.Targeting,
            Range = draft.Range,
            Radius = draft.Radius,
            Damage = draft.Damage,
            DamageType = draft.DamageType,
            Effects = new List<RecordId>(draft.Effects),
            Magnitude = draft.Magnitude,
            Projectile = draft.Projectile,
            ProjectileSpeed = draft.ProjectileSpeed,
        });
    }

    private static readonly RecordId Mana = new("sage", "mana");

    // ---- the console spellmaker ---------------------------------------------------------------------

    // The spellmaker screen does not exist yet (13), and a console is a perfectly good spellmaker: the
    // composition rules are what needed building, and they are the same ones a screen will call.
    //
    //   spell_make "cold snap" sage:burning target=projectile damage=20 type=sage:fire mag=2
    //
    internal static void RegisterCommands(Engine engine)
    {
        engine.CVars.RegisterCommand("spell_effects", CVarFlags.None, "What effects you can build a spell out of.", _ =>
        {
            int n = 0;
            foreach (var id in engine.Records.Ids("effect"))
            {
                if (!engine.Records.TryGet(id, out EffectRecord effect) || effect.Cost <= 0f) continue;
                Log.Info(LogCat.Console, $"  {id,-28} {effect.Cost,5:F0}   {Describe(effect)}");
                n++;
            }
            if (n == 0) Log.Info(LogCat.Console, "no effect has a cost, so none can be built with (16 §3.3)");
        });

        engine.CVars.RegisterCommand("spell_list", CVarFlags.None, "The spells you have composed.", _ =>
            GameplayModules.ForEachPlayer(engine, (world, _) =>
            {
                var book = Book(world);
                foreach (var draft in book.Drafts)
                {
                    var id = new RecordId(Namespace, Slug(draft.Name));
                    float cost = Price(engine.Records, draft);
                    Log.Info(LogCat.Console, $"  {draft.Name,-24} {id}  {cost:F0} mana  {draft.Targeting}" +
                                             (draft.Effects.Count > 0 ? $"  [{string.Join(", ", draft.Effects)}]" : ""));
                }
                if (book.Drafts.Count == 0) Log.Info(LogCat.Console, "you have composed nothing (try spell_effects)");
            }));

        engine.CVars.RegisterCommand("spell_forget", CVarFlags.Cheat, "spell_forget <name>: unmake a composed spell.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "spell_forget <name>"); return; }
            GameplayModules.ForEachPlayer(engine, (world, _) =>
                Log.Info(LogCat.Console, Forget(world, a[0]) ? $"\"{a[0]}\" is gone" : $"no spell called \"{a[0]}\""));
        });

        engine.CVars.RegisterCommand("spell_make", CVarFlags.Cheat,
            "spell_make <name> <effect|key=value>...: compose a spell and learn it. " +
            "Keys: target, damage, type, range, radius, mag, projectile, speed.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "spell_make <name> <effect|key=value>..."); return; }

            var draft = new SpellDraft { Name = a[0] };
            for (int i = 1; i < a.Count; i++)
            {
                string token = a[i];
                int equals = token.IndexOf('=');
                if (equals < 0)
                {
                    draft.Effects.Add(engine.Records.Resolve("effect", token));
                    continue;
                }

                string key = token.Substring(0, equals).ToLowerInvariant();
                string value = token.Substring(equals + 1);
                if (!Set(engine, draft, key, value)) Log.Warn(LogCat.Console, $"spell_make: don't know '{key}'");
            }

            GameplayModules.ForEachPlayer(engine, (world, entity) =>
            {
                // Composed per player world, and each gets its own copy: two players' books must not
                // share a draft object, or editing one would edit the other's spell.
                // Two players in one world share its book, so the second one is *taught* the spell the
                // first composed rather than told it already exists.
                var known = new RecordId(Namespace, Slug(draft.Name));
                if (Book(world).Drafts.Any(d => Slug(d.Name) == known.Name))
                {
                    world.Teach(entity, known);
                    Log.Info(LogCat.Console, $"{World.Describe(entity)} learns \"{draft.Name}\" ({known}), already composed here");
                    return;
                }

                var result = Compose(world, Copy(draft));
                if (!result.Ok) { Log.Warn(LogCat.Console, $"spell_make: {result.Problem}"); return; }
                world.Teach(entity, result.Id);
                Log.Info(LogCat.Console, $"{World.Describe(entity)} learns \"{draft.Name}\" ({result.Id}), {result.Cost:F0} mana");
            });
        });
    }

    private static bool Set(Engine engine, SpellDraft draft, string key, string value)
    {
        switch (key)
        {
            case "target" or "targeting":
                if (!Enum.TryParse<AbilityTargeting>(value, ignoreCase: true, out var targeting))
                {
                    Log.Warn(LogCat.Console, $"spell_make: target must be one of {string.Join(", ", Enum.GetNames<AbilityTargeting>())}");
                    return true;
                }
                draft.Targeting = targeting;
                return true;
            case "damage": draft.Damage = Number(value); return true;
            case "type" or "damagetype": draft.DamageType = engine.Records.Resolve("damage_type", value); return true;
            case "range": draft.Range = Number(value); return true;
            case "radius": draft.Radius = Number(value); return true;
            case "mag" or "magnitude": draft.Magnitude = Number(value); return true;
            case "projectile": draft.Projectile = engine.Records.Resolve("prefab", value); return true;
            case "speed": draft.ProjectileSpeed = Number(value); return true;
            default: return false;
        }
    }

    private static float Number(string value) =>
        float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n) ? n : 0f;

    private static SpellDraft Copy(SpellDraft draft) => new()
    {
        Name = draft.Name,
        Targeting = draft.Targeting,
        Effects = new List<RecordId>(draft.Effects),
        Magnitude = draft.Magnitude,
        Range = draft.Range,
        Radius = draft.Radius,
        Damage = draft.Damage,
        DamageType = draft.DamageType,
        Projectile = draft.Projectile,
        ProjectileSpeed = draft.ProjectileSpeed,
    };

    private static string Describe(EffectRecord effect)
    {
        var parts = new List<string>();
        foreach (var modifier in effect.Modifiers)
            parts.Add($"{modifier.Attribute.Name} {(modifier.Value >= 0 ? "+" : "")}{modifier.Value:0.##}");
        if (effect.Duration != EffectDuration.Instant) parts.Add(effect.Duration == EffectDuration.Timed ? $"{effect.Time:0.#}s" : "lasting");
        return string.Join(", ", parts);
    }

    // A record id has to be a record id (05 §3.5): lower case, and only letters, digits, `_` and `.`.
    // "Bob's Big Fire" becomes "bob_s_big_fire", which is ugly and stable, and the *name* is what any
    // screen shows.
    internal static string Slug(string name)
    {
        var slug = new StringBuilder(name.Length);
        foreach (char c in name.Trim().ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(c) || c is '.' ? c : '_');
        string text = slug.ToString().Trim('_');
        return text.Length == 0 ? "spell" : text;
    }
}
