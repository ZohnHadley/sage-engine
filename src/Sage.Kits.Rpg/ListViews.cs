#nullable enable
using System;
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// The screens that were panels (docs/design/13 "As built (the panel model)", issue #350): the spellbook,
// the bag, a conversation and the spellmaker, as `screen` records over view-models like every other kit
// screen. Until #350 they were `Screen`s on the client's second screen system, drawn by hand; now the
// widget path is the only one and a game re-lays out or restyles them with a patch.
//
// **The rows are still the panel's.** Each view-model builds a `Panel` with the same builders the console
// prints (`spells`, `inv`: GameplayPanels) or with the same rules (DialogueRules, Spellmaker), so a greyed
// row, its reason and a refused action cannot disagree (R17), and the console and the screen cannot drift
// apart. A row says whether it is selected (readied, equipped, chosen), enabled, and why not.
//
// Refresh reads the world every frame into a signature — numbers folded together without allocating — and
// rebuilds the panel and its rows only when that changed or after the player did something, reusing the
// rows, so an open list allocates nothing while nothing changes.

// One line of a list screen: what a layout's row template binds.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]   // the RPG screens: SAGE0125, as Sage.UI
public sealed class ListRow
{
    public const string TextStyle = "rpg:list_text", OffStyle = "rpg:list_text_off",
                        DetailOnStyle = "rpg:list_detail", DetailOffStyle = "rpg:list_detail_off";

    // The panel row it shows.
    public PanelRow Row { get; internal set; }

    // Its place in the list.
    public int Index { get; internal set; }

    public RecordId Id => Row.Id;

    // The name, with "×N" after a stack of more than one.
    public string Text { get; internal set; } = "";

    // The second column: "12 mana", "3 kg", a price; or why not.
    public string Detail => Row.Detail;

    // Readied, equipped, chosen: what the screen ticks.
    public bool Selected => Row.Selected;
    public string Tick => Row.Selected ? "•" : "";

    // Usable right now; when not, the reason in words the rules gave.
    public bool Enabled => Row.Enabled;
    public string Reason => Row.Reason;

    public string Style => Row.Enabled ? TextStyle : OffStyle;
    public string DetailStyle => Row.Enabled ? DetailOnStyle : DetailOffStyle;
}

// A screen that is a titled list of panel rows (the pattern above). A subclass says what the rows depend on
// (Signature), builds them (Build) and acts on one (Activate, and Alternate: Delete, the pad's X).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
public abstract class PanelListView : IViewModel
{
    private readonly Panel _panel = new();
    private readonly List<ListRow> _pool = new();
    private int _signature;
    private bool _built;
    private ListRow? _focused;

    // The panel's title: "Spells", "Carrying — 3 / 50 kg", what somebody just said.
    public string Title { get; private set; } = "";

    // Why there is no list at all ("knows no magic at all"), or empty.
    public string Problem => _panel.Problem;
    public bool HasProblem => _panel.Problem.Length > 0;

    // Nothing to list, and no problem to say instead.
    public bool Empty => Rows.Count == 0 && !HasProblem;

    public List<ListRow> Rows { get; } = new();

    // Why the focused row cannot be used, under the list: the reason the rules gave, not a guess (R17).
    public string Reason { get; private set; } = "";

    // What the last action said, when it said something.
    public string Message { get; protected set; } = "";

    // The line along the bottom, saying what the buttons do on this screen (a `@key`).
    public abstract string Hint { get; }

    public virtual void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        int signature = Signature(world, context.Subject);
        if (_built && signature == _signature) return;
        _built = true;
        _signature = signature;

        Build(world, context.Subject, _panel);
        Title = _panel.Title;
        Rows.Clear();
        for (int i = 0; i < _panel.Count; i++)
        {
            if (_pool.Count == i) _pool.Add(new ListRow());
            var row = _pool[i];
            var panelRow = _panel[i];
            row.Row = panelRow;
            row.Index = i;
            row.Text = panelRow.Count > 1 ? $"{panelRow.Name} ×{panelRow.Count}" : panelRow.Name;
            Rows.Add(row);
        }
        ShowReason();
    }

    // Build again at the next Refresh, whatever the signature says (after an action).
    protected void Invalidate() => _built = false;

    // What the rows depend on, folded into one number without allocating.
    protected abstract int Signature(World world, Entity subject);

    // The panel: Begin, then a row each (GameplayPanels' builders, or the screen's own).
    protected abstract void Build(World world, Entity subject, Panel panel);

    // Confirm on a row. True when it did something.
    protected virtual bool Activate(World world, Entity subject, ListRow row) => false;

    // The other button on a row (Delete, the pad's X): drop it, take it off.
    protected virtual bool Alternate(World world, Entity subject, ListRow row) => false;

    // Back: true when the view-model used it, false to let the screen close (IViewModel.Back).
    public virtual bool Back(in UiBindContext context) => false;

    // The player changed a form widget of the screen — a text field (IViewModel.Changed): build again.
    public virtual void Changed(Widget widget, in UiBindContext context) => Invalidate();

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world || UiScreen.RowOf(widget) is not ListRow row) return false;
        Message = "";
        bool done = Activate(world, context.Subject, row);
        Invalidate();
        Refresh(in context);
        return done;
    }

    public bool Command(UiCommand command, Widget? target, in UiBindContext context)
    {
        if (command != UiCommand.Alternate || context.World is not { } world || UiScreen.RowOf(target) is not ListRow row) return false;
        Message = "";
        bool done = Alternate(world, context.Subject, row);
        Invalidate();
        Refresh(in context);
        return done;
    }

    public void Focused(Widget? widget, in UiBindContext context)
    {
        _focused = UiScreen.RowOf(widget) as ListRow;
        ShowReason();
    }

    private void ShowReason()
    {
        var focused = _focused != null && _focused.Index < Rows.Count && ReferenceEquals(Rows[_focused.Index], _focused) ? _focused : null;
        Reason = focused is { Enabled: false } ? focused.Reason : "";
    }

    protected static int Fold(int hash, int value) => unchecked(hash * 31 + value);
}

// What the player can cast, what each costs, which is readied, and why one cannot be cast right now
// (GameplayPanels.Spellbook, the `spells` command's rows). Confirm readies it — even one you cannot afford
// right now, which is the point of readying: you pick what to throw, then find the mana.
//   screen rpg:spellbook — layout rpg:list
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
[ViewModel("rpg_spellbook")]
public sealed class SpellbookView : PanelListView
{
    public override string Hint => "@rpg.spellbook.hint";

    protected override int Signature(World world, Entity subject)
    {
        if (!world.TryGet<Abilities>(subject, out var abilities)) return world.Has<Abilities>(subject) ? 1 : 0;
        int hash = 7;
        if (abilities.Known == null) return hash;
        var records = world.Records();
        hash = Fold(hash, ReadiedSpell.Of(in abilities).GetHashCode());
        foreach (var id in abilities.Known)
        {
            hash = Fold(hash, id.GetHashCode());
            if (!records.TryGet(id, out AbilityRecord _)) { hash = Fold(hash, 99); continue; }
            bool can = AbilityRules.CanCast(world, records, subject, id, in abilities, out _, out var why);
            hash = Fold(hash, can ? 1 : 2 + (int)why);
        }
        return hash;
    }

    protected override void Build(World world, Entity subject, Panel panel) => GameplayPanels.Spellbook(world, subject, panel);

    protected override bool Activate(World world, Entity subject, ListRow row) => world.Ready(subject, row.Id);
}

// What the player carries — stacks with their weight, a tick on what is in a hand — and what could go in
// one (GameplayPanels.Inventory, the `inv` command's rows). Confirm equips, or takes off what is already in
// a hand; Alternate drops one, which lands in front of the player as a pickup.
//   screen rpg:bag — layout rpg:list
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
[ViewModel("rpg_bag")]
public sealed class BagView : PanelListView
{
    public override string Hint => "@rpg.bag.hint";

    protected override int Signature(World world, Entity subject)
    {
        if (!world.TryGet<Inventory>(subject, out var inventory) || inventory.Items == null) return 0;
        int hash = Fold(11, (int)(inventory.Capacity * 10f));
        foreach (var stack in inventory.Items)
        {
            hash = Fold(hash, stack.Item.GetHashCode());
            hash = Fold(hash, stack.Count);
        }
        if (world.TryGet<Equipment>(subject, out var equipment) && equipment.Worn != null)
            foreach (var worn in equipment.Worn)
                hash = Fold(Fold(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(worn.Slot ?? "")), worn.Item.GetHashCode());
        return hash;
    }

    protected override void Build(World world, Entity subject, Panel panel) => GameplayPanels.Inventory(world, subject, panel);

    // One button for a two-state thing: equip, or take off again what is in a hand already.
    protected override bool Activate(World world, Entity subject, ListRow row)
    {
        if (row.Selected)
        {
            if (!world.Records().TryGet(row.Id, out ItemRecord record)) return false;
            world.Unequip(subject, record.Slot);
            return true;
        }
        return world.Equip(subject, row.Id);
    }

    protected override bool Alternate(World world, Entity subject, ListRow row) => !world.Drop(subject, row.Id).IsNull;
}

// A conversation (DialogueRules, 16 §3.5): the node's line as the title and the things the player may say as
// rows, greyed with the refusal when a requirement is not met. Confirm says it; the screen closes itself when
// the conversation ends, so "that is all" and Back are the same thing to a player — and Back ends it.
// The kit opens it when the player uses somebody with a `dialogue` (UseScreenSystem).
//   screen rpg:dialogue — layout rpg:dialogue
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
[ViewModel("rpg_dialogue")]
public sealed class DialogueView : PanelListView
{
    // The rows' ids: an option is not a record, and two may say the same words, so a row is its place.
    private static readonly RecordId[] Places = new RecordId[16];

    public override string Hint => "@rpg.dialogue.hint";

    // Who is talking, by name.
    public string Speaker { get; private set; } = "";

    public override void Refresh(in UiBindContext context)
    {
        base.Refresh(in context);
        if (context.World is { } world && world.Resources.TryGet<Conversation>(out var conversation) && conversation != null)
        {
            var speaker = conversation.Speaker;
            if (speaker != _named) { _named = speaker; Speaker = world.IsAlive(speaker) ? speaker.Name ?? "" : ""; }
        }
    }

    private Entity _named;

    protected override int Signature(World world, Entity subject)
    {
        if (!world.Resources.TryGet<Conversation>(out var conversation) || conversation is not { Running: true }) return 0;
        int hash = Fold(Fold(13, conversation.Record.GetHashCode()), StringComparer.Ordinal.GetHashCode(conversation.Node));
        if (DialogueRules.Current(world) is not { } node) return hash;
        foreach (var option in node.Options)
            hash = Fold(hash, DialogueRules.CanPick(world, conversation.Listener, option, out _) ? 1 : 2);
        return hash;
    }

    protected override void Build(World world, Entity subject, Panel panel)
    {
        var node = DialogueRules.Current(world);
        if (node == null)
        {
            panel.Begin("", subject, "there is nobody to talk to");
            return;
        }

        var conversation = world.Resources.Get<Conversation>();
        string who = world.IsAlive(conversation.Speaker) ? World.Describe(conversation.Speaker) : "";
        panel.Begin(node.Text.Length > 0 ? node.Text : who, subject);
        for (int i = 0; i < node.Options.Count; i++)
        {
            var option = node.Options[i];
            bool can = DialogueRules.CanPick(world, conversation.Listener, option, out string why);
            panel.Add(PanelRow.Of(Place(i), option.Text, detail: can ? "" : why, enabled: can, reason: why));
        }
        if (node.Options.Count == 0) panel.Add(PanelRow.Of(Place(0), "(say nothing)"));
    }

    private static RecordId Place(int index)
    {
        if (index >= Places.Length) return new RecordId("dialogue", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Places[index].IsEmpty) Places[index] = new RecordId("dialogue", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Places[index];
    }

    protected override bool Activate(World world, Entity subject, ListRow row)
    {
        var node = DialogueRules.Current(world);
        if (node == null) return false;
        if (row.Index >= node.Options.Count) world.Resources.Get<Conversation>().Stop();
        else DialogueRules.Pick(world, node.Options[row.Index]);
        if (!world.Resources.Get<Conversation>().Running) CloseOwnLayer(world);
        return true;
    }

    // Back leaves the conversation, and lets the screen close.
    public override bool Back(in UiBindContext context)
    {
        if (context.World is { } world && world.Resources.TryGet<Conversation>(out var conversation) && conversation != null) conversation.Stop();
        return false;
    }

    // The conversation ended (an option said so, or opened another screen — the shop — and ended): this
    // screen goes, wherever it is in the stack.
    private void CloseOwnLayer(World world)
    {
        if (!world.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return;
        foreach (var layer in stack.Layers)
            if (ReferenceEquals(layer.Screen?.ViewModel, this) && !layer.IsClosing) { stack.Close(layer); return; }
    }
}

// The spellmaker (16 §3.3, F21): name it, choose what it does and how it is delivered, and it costs what
// those add up to. The name is a `text_field` bound to Name; Delivery and Power are rows that cycle on
// Confirm; each effect for sale is a row that goes in or out; and **"make it" is a row**, greyed with the
// reason Spellmaker.CanCompose gives — the rule Compose itself applies — so the button and the attempt
// cannot disagree. The spell it makes is the one the console's `spell_make` would.
//   screen rpg:spellmaker — layout rpg:spellmaker
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
[ViewModel("rpg_spellmaker")]
public sealed class SpellmakerView : PanelListView
{
    private static readonly AbilityTargeting[] Deliveries =
    {
        AbilityTargeting.Self, AbilityTargeting.Touch, AbilityTargeting.Projectile,
        AbilityTargeting.Area, AbilityTargeting.TouchArea,
    };

    private static readonly float[] Powers = { 1f, 1.5f, 2f, 3f, 5f };

    // Ids for the rows that are not effects. A namespace of their own so they can never collide with an
    // effect's id.
    public static readonly RecordId DeliveryRow = new("screen", "delivery");
    public static readonly RecordId PowerRow = new("screen", "power");
    public static readonly RecordId MakeRow = new("screen", "make");

    // Re-priced on every keystroke (PanelListView.Changed builds again): the title carries the price, and a
    // spell with no name cannot be made.
    private string _name = "";
    private int _changes;

    public override string Hint => "@rpg.spellmaker.hint";

    // What the player typed: the text field writes it here.
    public string Name
    {
        get => _name;
        set { value ??= ""; if (string.Equals(_name, value, StringComparison.Ordinal)) return; _name = value; _changes++; }
    }

    public SpellDraft Draft { get; private set; } = New();

    private static SpellDraft New() => new() { Targeting = AbilityTargeting.Touch, Magnitude = 1f, Range = 12f };

    protected override int Signature(World world, Entity subject) =>
        Fold(Fold(17, _changes), Spellmaker.Book(world).Drafts.Count);

    protected override void Build(World world, Entity subject, Panel panel)
    {
        var records = world.Records();
        Draft.Name = _name;
        float price = Spellmaker.Price(records, Draft);
        string cost = Spellmaker.CostName(records);
        panel.Begin(_name.Length == 0
            ? $"Make a spell — {price:F0} {cost}"
            : $"Make a spell — \"{_name}\", {price:F0} {cost}", subject);

        // The two things that are not effects: how it reaches, and how hard. Confirm cycles them.
        panel.Add(PanelRow.Of(DeliveryRow, "Delivery", Draft.Targeting.ToString()));
        panel.Add(PanelRow.Of(PowerRow, "Power", $"×{Draft.Magnitude:0.#}"));

        foreach (var id in records.Ids("effect"))
        {
            if (!records.TryGet(id, out EffectRecord effect) || effect.Cost <= 0f) continue;
            panel.Add(PanelRow.Of(id, id.Name, $"{effect.Cost:F0}", 1, Draft.Effects.Contains(id)));
        }

        bool can = Spellmaker.CanCompose(world, Draft, out string problem);
        panel.Add(PanelRow.Of(MakeRow, "— make it —", can ? $"{price:F0} {cost}" : "", 1, false, can, problem));
    }

    protected override bool Activate(World world, Entity subject, ListRow row)
    {
        _changes++;
        if (row.Id == DeliveryRow)
        {
            int i = Array.IndexOf(Deliveries, Draft.Targeting);
            Draft.Targeting = Deliveries[(i + 1) % Deliveries.Length];
            // A burst needs a radius to burst in, and only a burst should pay for one (16 §3.3).
            Draft.Radius = Draft.Targeting is AbilityTargeting.Area or AbilityTargeting.TouchArea ? 3f : 0f;
            return true;
        }

        if (row.Id == PowerRow)
        {
            int i = Array.IndexOf(Powers, Draft.Magnitude);
            Draft.Magnitude = Powers[(i + 1) % Powers.Length];
            return true;
        }

        if (row.Id == MakeRow)
        {
            Draft.Name = _name;
            if (!row.Enabled) { world.Say(row.Reason, MessageKind.Bad); return false; }
            var made = Spellmaker.Compose(world, Draft);
            if (!made.Ok) { world.Say(made.Problem, MessageKind.Bad); return false; }

            world.Teach(subject, made.Id);
            world.Say($"You invent {Draft.Name} ({made.Cost:F0} {Spellmaker.CostName(world.Records())})", MessageKind.Good);

            // A fresh draft and the name box empty: the spell that was being made exists now.
            Draft = New();
            Name = "";
            return true;
        }

        // An effect: in or out.
        if (!Draft.Effects.Remove(row.Id)) Draft.Effects.Add(row.Id);
        return true;
    }
}
