using Sage.UI;

namespace Sandbox;

// The Sandbox's widget screen (docs/design/13 "As built (drawing)", issue #97): `sandbox:status`, a
// `screen` record over `sandbox:status` (a ui_layout) and this view-model, all in content/data/ui.json
// and content/strings/. The first screen drawn by the widget renderer rather than the panel view, and
// what the screenshot shows: a nine-sliced window, a health bar, a scrolling list of what you carry
// (each row a button with its weight as a tooltip) and a close button.
//
// A view-model is simulation (it is in Sandbox, not Sandbox.Client): it reads the world into plain
// fields a layout binds by name, and a headless test could open it. It keeps its strings until what
// they say changes, so an open screen allocates nothing frame to frame (02 §4.6).
[ViewModel("sandbox_status")]
public sealed class StatusView : IViewModel
{
    public sealed class Row
    {
        public string Label = "";
        public string Detail = "";
    }

    public string Name = "";
    public float Health;
    public float MaxHealth = 100f;
    public string HealthText = "";
    public int ItemCount;
    public List<Row> Items { get; } = new();

    private int _shownHealth = -1, _shownMax = -1;
    private int _bagVersion = -1;

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world || context.Subject.IsNull) return;
        var player = context.Subject;
        if (Name.Length == 0) Name = World.Describe(player);

        var records = world.Records();
        var healthId = world.Conventions().Health;
        Health = world.Attribute(player, healthId);
        MaxHealth = !healthId.IsEmpty && records.TryGet(healthId, out AttributeRecord record) ? record.Max : 100f;
        int shown = (int)MathF.Round(Health), max = (int)MathF.Round(MaxHealth);
        if (shown != _shownHealth || max != _shownMax)
        {
            _shownHealth = shown;
            _shownMax = max;
            HealthText = $"{shown} / {max}";
        }

        if (!world.TryGet<Inventory>(player, out var bag) || bag.Items == null) { Items.Clear(); ItemCount = 0; return; }
        // What the bag holds changes rarely; its rows are rebuilt only then. The signature is cheap and
        // allocation-free: how many stacks, and a sum over their ids and counts.
        int version = bag.Items.Count;
        foreach (var stack in bag.Items) version = version * 31 + stack.Item.GetHashCode() * 7 + stack.Count;
        if (version == _bagVersion) return;
        _bagVersion = version;

        int n = 0;
        foreach (var stack in bag.Items)
        {
            if (stack.Count <= 0) continue;
            records.TryGet(stack.Item, out ItemRecord item);
            if (n == Items.Count) Items.Add(new Row());
            var row = Items[n++];
            string name = item?.Describe(stack.Item) ?? stack.Item.Name;
            row.Label = stack.Count > 1 ? $"{name} x{stack.Count}" : name;
            row.Detail = item != null ? $"{item.Weight * stack.Count:0.#} kg" : "";
        }
        if (Items.Count > n) Items.RemoveRange(n, Items.Count - n);
        ItemCount = n;
    }
}
