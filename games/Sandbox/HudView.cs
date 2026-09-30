using Sage.Kits.Rpg;
using Sage.UI;

namespace Sandbox;

// The Sandbox's HUD as a view-model (docs/design/13 "As built (the HUD, journal, map and menus)", issue
// #99): what the hand-placed SandboxHud used to draw — the health bar and its numbers, what is in your
// hands, the message log fading out, what Use would do and what V does — as plain fields the
// `sandbox:hud` layout (content/data/ui.json) binds. Where each goes, its colours and sizes are that
// layout's; SandboxClientModule opens it as a HUD layer (UiScreenStack.OpenHud), drawn under every
// window and never taking a key.
//
// It runs every frame, so it keeps its strings until what they say changes and reuses its message rows:
// a HUD frame allocates nothing (test: TheHudIsAScreenRecordAndAFrameOfItAllocatesNothing).
#pragma warning disable SAGE0123   // cameras as entities are experimental; the HUD follows which rig has the screen
#pragma warning disable SAGE0127   // the ammunition readout reads the 4e magazine (engine issue #138)
[ViewModel("sandbox_hud")]
public sealed class HudView : IViewModel
{
    // How many messages show, newest at the bottom (13 §3).
    public const int MessageLines = 6;

    // The message styles, by kind, and the one a message fades into for its last second.
    public const string MessageStyle = "sandbox:hud_message", GoodStyle = "sandbox:hud_good",
                        BadStyle = "sandbox:hud_bad", FadingStyle = "sandbox:hud_fading";

    public sealed class Line
    {
        public string Text { get; internal set; } = "";
        public string Style { get; internal set; } = MessageStyle;
    }

    public float Health;
    public float MaxHealth = 100f;
    public string HealthText = "";

    // The bar's style: it reddens when you run low (the hand-drawn bar lerped; a style is one of two).
    public const string BarStyle = "sandbox:hud_bar", LowBarStyle = "sandbox:hud_bar_low";
    public string HealthStyle = BarStyle;
    public string Hands = "";
    public bool HasPlayer;

    // The rounds of the weapon in hand, loaded / carried (the kit's AmmoReadout; engine issue #138).
    public string Ammo = "";
    public bool HasAmmo;

    // Looking out of the player's own eyes with no screen up: what the prompt and the view hint need.
    public bool Aiming;

    // What Use would do (a key: @sandbox.hud.use, @sandbox.hud.pick_up) and what it would pick up.
    public string Prompt = "";
    public string PromptItem = "";
    public bool HasPrompt;

    // What V does from here (a key), or empty.
    public string ViewHint = "";
    public bool HasViewHint;

    public List<Line> Messages { get; } = new();

    private readonly Line[] _lines = new Line[MessageLines];
    private World? _world;
    private Query<Transform> _players;
    private int _shownHealth = -1, _shownMax = -1;
    private RecordId _lastMain, _lastOff;
    private bool _handsShown;
    private Entity _lastHovered;
    private readonly AmmoReadout _ammo = new();

    public HudView()
    {
        for (int i = 0; i < _lines.Length; i++) _lines[i] = new Line();
    }

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
        }

        // Its own player, found each frame: a HUD opened with the world outlives the player a load replaces.
        var player = context.Subject;
        if (player.IsNull || !world.IsAlive(player))
        {
            player = default;
            foreach (var entity in _players.Entities) { player = entity; break; }
        }
        HasPlayer = !player.IsNull;

        // With a window open the bars and the log stay — you want to read them while deciding what to
        // equip — but what belongs to aiming does not (13 §3, F38); nor during a scripted cut or with the
        // editor's free camera (engine issue #81).
        bool screenOpen = world.Resources.TryGet<ScreenStack>(out var panels) && panels!.IsOpen
                       || world.Resources.TryGet<UiScreenStack>(out var widgets) && widgets!.IsOpen;
        var rig = world.MainViewRig();
        Aiming = !screenOpen && rig != CameraRigKind.None;

        Messages.Clear();
        ReadMessages(world);
        if (!HasPlayer) { HasPrompt = HasViewHint = HasAmmo = false; return; }

        ReadHealth(world, player);
        ReadHands(world, player);
        if (_ammo.Read(world, player)) Ammo = _ammo.Text;
        HasAmmo = _ammo.Has;
        ReadPrompt(world);
        ViewHint = rig switch
        {
            CameraRigKind.FirstPerson => "@sandbox.hud.third_person",
            CameraRigKind.ThirdPerson => "@sandbox.hud.first_person",
            _ => "",
        };
        HasViewHint = Aiming && ViewHint.Length > 0;
    }

    private void ReadHealth(World world, Entity player)
    {
        var healthId = world.Conventions().Health;   // whatever this game calls it (issue #26)
        Health = world.Attribute(player, healthId);
        MaxHealth = !healthId.IsEmpty && world.Records().TryGet(healthId, out AttributeRecord record) ? record.Max : 100f;
        HealthStyle = MaxHealth > 0f && Health / MaxHealth < 0.35f ? LowBarStyle : BarStyle;
        int shown = (int)MathF.Round(Health), max = (int)MathF.Round(MaxHealth);
        if (shown == _shownHealth && max == _shownMax) return;
        _shownHealth = shown;
        _shownMax = max;
        HealthText = $"{shown} / {max}";
    }

    // What is in your hands, because equipping something is invisible otherwise (16 §3.2, F19).
    private void ReadHands(World world, Entity player)
    {
        if (!world.TryGet<Equipment>(player, out var equipment)) { Hands = ""; _handsShown = false; return; }
        var mainHand = equipment.In(RpgKitModule.MainHand);
        var offHand = equipment.In(RpgKitModule.OffHand);
        if (_handsShown && mainHand == _lastMain && offHand == _lastOff) return;
        _handsShown = true;
        _lastMain = mainHand;
        _lastOff = offHand;
        string main = mainHand.IsEmpty ? world.Resources.Get<Localisation>().Text("@sandbox.hud.bare_hands") : Describe(world, mainHand);
        Hands = offHand.IsEmpty ? main : $"{main} / {Describe(world, offHand)}";
    }

    // What pressing Use would do, under the crosshair. Without this, an item on the ground is a
    // decoration: nothing on screen says it can be taken (16 §3.2).
    private void ReadPrompt(World world)
    {
        var target = world.Resources.TryGet<InteractionState>(out var interactions) ? interactions!.Hovered : default;
        if (target.IsNull || !world.IsAlive(target)) { _lastHovered = default; HasPrompt = false; return; }
        HasPrompt = Aiming;
        if (target == _lastHovered) return;
        _lastHovered = target;
        if (world.TryGet<Pickup>(target, out var pickup))
        {
            Prompt = "@sandbox.hud.pick_up";
            PromptItem = Describe(world, pickup.Item);
        }
        else
        {
            Prompt = "@sandbox.hud.use";
            PromptItem = "";
        }
    }

    // The last few things said, oldest first so the newest is at the bottom of the column; each in its
    // kind's colour, and faded for its last second so it leaves rather than vanishing (13 §3).
    private void ReadMessages(World world)
    {
        var messages = world.Messages().Messages;
        int first = Math.Max(0, messages.Length - MessageLines);
        for (int i = first, row = 0; i < messages.Length; i++, row++)
        {
            ref readonly var message = ref messages[i];
            var line = _lines[row];
            line.Text = message.Text;
            line.Style = message.Remaining < 1f ? FadingStyle
                       : message.Kind switch { MessageKind.Good => GoodStyle, MessageKind.Bad => BadStyle, _ => MessageStyle };
            Messages.Add(line);
        }
    }

    private static string Describe(World world, RecordId item) =>
        world.Records().TryGet(item, out ItemRecord record) ? record.Describe(item) : item.Name;
}
#pragma warning restore SAGE0127
#pragma warning restore SAGE0123
