#nullable enable
using System;
using System.Linq;

namespace Sage.Gameplay;

// The spellmaker, as a screen (docs/design/16 §3.3, 13 §3; TODO F21, F38).
//
// In the engine rather than in a game, unlike the spellbook and the bag: what a spellmaker *is* — you
// name it, you choose what it does and how it is delivered, it costs what those add up to — is the
// feature F21 built, not a decision each game makes. A game that wants a different one subclasses
// `Screen` as the Sandbox does for its bag.
//
// Every row is the same shape as any other panel row, including the last one. **"Make it" is a row**,
// greyed with the reason when the draft is not yet a spell — and the reason comes from
// `Spellmaker.CanCompose`, the rule `Compose` itself applies (R17). So the button and the attempt
// cannot disagree, and "a spell needs a name" is written once.
public sealed class SpellmakerScreen : Screen
{
    private static readonly AbilityTargeting[] Deliveries =
    {
        AbilityTargeting.Self, AbilityTargeting.Touch, AbilityTargeting.Projectile,
        AbilityTargeting.Area, AbilityTargeting.TouchArea,
    };

    private static readonly float[] Powers = { 1f, 1.5f, 2f, 3f, 5f };

    private readonly TextField _name = new(28, "a name for it");

    public SpellDraft Draft { get; private set; } = New();

    public override TextField? Field => _name;

    public override float Width => 620f;

    public override string Hint => "type a name   ↑↓ choose   Enter add/change   Esc close";

    private static SpellDraft New() => new() { Targeting = AbilityTargeting.Touch, Magnitude = 1f, Range = 12f };

    // Re-priced on every keystroke, because the title carries the price and a spell with no name
    // cannot be made — both change as it is typed.
    public override void Typed(World world, Entity subject) => Build(world, subject);

    public override void Build(World world, Entity subject)
    {
        var records = world.Records();
        Draft.Name = _name.Text;

        float price = Spellmaker.Price(records, Draft);
        Panel.Begin(_name.IsEmpty
            ? $"Make a spell — {price:F0} mana"
            : $"Make a spell — \"{_name.Text}\", {price:F0} mana", subject);

        // The two things that are not effects: how it reaches, and how hard. Enter cycles them, which
        // is why they are rows rather than a second control a list cannot hold.
        Panel.Add(PanelRow.Of(Delivery, "Delivery", Draft.Targeting.ToString()));
        Panel.Add(PanelRow.Of(Power, "Power", $"×{Draft.Magnitude:0.#}"));

        foreach (var id in records.Ids("effect"))
        {
            if (!records.TryGet(id, out EffectRecord effect) || effect.Cost <= 0f) continue;
            bool chosen = Draft.Effects.Contains(id);
            Panel.Add(PanelRow.Of(id, id.Name, $"{effect.Cost:F0}", 1, chosen));
        }

        bool can = Spellmaker.CanCompose(world, Draft, out string problem);
        Panel.Add(PanelRow.Of(Make, "— make it —", can ? $"{price:F0} mana" : "", 1, false, can, problem));
    }

    public override bool Activate(World world, Entity subject, in PanelRow row)
    {
        if (row.Id == Delivery)
        {
            int i = Array.IndexOf(Deliveries, Draft.Targeting);
            Draft.Targeting = Deliveries[(i + 1) % Deliveries.Length];
            // A burst needs a radius to burst in, and only a burst should pay for one (16 §3.3).
            Draft.Radius = Draft.Targeting is AbilityTargeting.Area or AbilityTargeting.TouchArea ? 3f : 0f;
            return true;
        }

        if (row.Id == Power)
        {
            int i = Array.IndexOf(Powers, Draft.Magnitude);
            Draft.Magnitude = Powers[(i + 1) % Powers.Length];
            return true;
        }

        if (row.Id == Make)
        {
            if (!row.Enabled) { world.Say(row.Reason, MessageKind.Bad); return false; }
            var made = Spellmaker.Compose(world, Draft);
            if (!made.Ok) { world.Say(made.Problem, MessageKind.Bad); return false; }

            world.Teach(subject, made.Id);
            world.Say($"You invent {Draft.Name} ({made.Cost:F0} mana)", MessageKind.Good);

            // A fresh draft, and the name box empty: the spell that was being made now exists, and
            // composing the same one twice is refused anyway.
            Draft = New();
            _name.Clear();
            return true;
        }

        // An effect: in or out.
        if (!Draft.Effects.Remove(row.Id)) Draft.Effects.Add(row.Id);
        return true;
    }

    // Ids for the rows that are not effects. A namespace of their own so they can never collide with
    // a record id, which is what `TryFind` and `Activate` compare against.
    private static readonly RecordId Delivery = new("screen", "delivery");
    private static readonly RecordId Power = new("screen", "power");
    private static readonly RecordId Make = new("screen", "make");
}
