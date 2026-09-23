using System.Numerics;
using Microsoft.Xna.Framework;

namespace Sandbox;

// The Sandbox's HUD (docs/design/13 §3). The engine hands a game `UiDraw`, a `MessageLog` and a
// crosshair; what a health bar looks like is the game's business, which is why this lives here and
// not in Sage.Client.
//
// FrameUpdate, so it runs at display rate and is queued before the Overlay phase draws it.
public sealed class SandboxHud : ISystem
{
    private static readonly RecordId Health = new("sage", "health");

    private readonly ArchetypeQuery<Transform> _players;
    private readonly UiDraw _ui;
    private readonly MessageLog _messages;
    private readonly RecordStore _records;
    private readonly InteractionEvents _interactions;

    // Cached text: a HUD that rebuilds its strings every frame allocates in the steady state, which
    // the frame budget does not allow (02 §4.6). These change when what they say changes.
    private string _healthText = "", _handsText = "", _promptText = "";
    private int _lastHealth = -1, _lastMax = -1;
    private RecordId _lastMain, _lastOff;
    private Entity _lastHovered;

    public SandboxHud(World world, RecordStore records)
    {
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
        _ui = world.Resources.Get<UiDraw>();
        _messages = world.Messages();
        _records = records;
        _interactions = world.Resources.Get<InteractionEvents>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float width = _ui.Size.X, height = _ui.Size.Y;
        if (width < 1f) return;                       // before the first frame sized the viewport

        float x = 24f, bottom = height - 28f;
        DrawMessages(x, bottom - 86f);
        DrawPrompt(world, width * 0.5f, height * 0.5f + 28f);

        foreach (var player in _players.Entities)
        {
            DrawHealth(world, player, x, bottom - 44f);
            DrawHands(world, player, x, bottom - 20f);
            return;                                   // one local player
        }
    }

    // A bar that empties and reddens, with the numbers beside it: readable at a glance and it says
    // exactly what the simulation says.
    private void DrawHealth(World world, Entity player, float x, float y)
    {
        float health = world.Attribute(player, Health);
        float max = _records.TryGet(Health, out AttributeRecord record) ? record.Max : 100f;
        float fraction = max > 0f ? System.Math.Clamp(health / max, 0f, 1f) : 0f;

        int shownHealth = (int)MathF.Round(health), shownMax = (int)MathF.Round(max);
        if (shownHealth != _lastHealth || shownMax != _lastMax)
        {
            _healthText = $"{shownHealth} / {shownMax}";
            _lastHealth = shownHealth;
            _lastMax = shownMax;
        }

        const float Width = 220f, Height = 16f;
        _ui.Rect(x, y, Width, Height, new Color(0, 0, 0, 140));
        _ui.Rect(x + 1f, y + 1f, (Width - 2f) * fraction, Height - 2f, Lerp(fraction));
        _ui.Frame(x, y, Width, Height, new Color(0, 0, 0, 200));

        _ui.Text(x + Width + 10f, y - 2f, _healthText, Color.White);
    }

    // What is in your hands, because equipping something is invisible otherwise (16 §3.2, F19).
    private void DrawHands(World world, Entity player, float x, float y)
    {
        if (!world.TryGet<Equipment>(player, out var equipment)) return;
        if (equipment.MainHand != _lastMain || equipment.OffHand != _lastOff)
        {
            string main = Describe(equipment.MainHand, "bare hands");
            _handsText = equipment.OffHand.IsEmpty ? main : $"{main} / {Describe(equipment.OffHand, "")}";
            _lastMain = equipment.MainHand;
            _lastOff = equipment.OffHand;
        }
        _ui.Text(x, y, _handsText, new Color(215, 215, 215, 230));
    }

    private string Describe(RecordId item, string ifEmpty) =>
        item.IsEmpty ? ifEmpty
        : _records.TryGet(item, out ItemRecord record) ? record.Describe(item)
        : item.Name;

    // What pressing Use would do, under the crosshair. Without this, an item on the ground is a
    // decoration: nothing on screen says it can be taken (16 §3.2).
    private void DrawPrompt(World world, float centreX, float y)
    {
        var target = _interactions.Hovered;
        if (target.IsNull || !world.IsAlive(target)) { _lastHovered = default; return; }

        if (target != _lastHovered)
        {
            _promptText = world.TryGet<Pickup>(target, out var pickup)
                ? $"E   Pick up {Describe(pickup.Item, pickup.Item.Name)}"
                : "E   Use";
            _lastHovered = target;
        }
        var size = _ui.Measure(_promptText);
        _ui.Text(centreX - size.X * 0.5f, y, _promptText, new Color(245, 240, 200, 235));
    }

    // Newest at the bottom, fading out as they age (13 §3).
    private void DrawMessages(float x, float bottom)
    {
        var messages = _messages.Messages;
        float line = _ui.LineHeight;
        for (int i = messages.Length - 1, row = 0; i >= 0 && row < 6; i--, row++)
        {
            ref readonly var message = ref messages[i];
            var colour = message.Kind switch
            {
                MessageKind.Good => new Color(120, 230, 140),
                MessageKind.Bad => new Color(240, 120, 110),
                _ => Color.White,
            };
            // The last second is the fade, so a message leaves rather than vanishing.
            byte alpha = (byte)(255 * System.Math.Clamp(message.Remaining, 0f, 1f));
            _ui.Text(x, bottom - row * line, message.Text, new Color(colour.R, colour.G, colour.B, alpha));
        }
    }

    // Green while you are well, red as you run out.
    private static Color Lerp(float fraction)
    {
        byte r = (byte)(220 - 80 * fraction), g = (byte)(60 + 150 * fraction);
        return new Color(r, g, (byte)70, (byte)235);
    }
}
