#nullable enable

namespace Sage.Gameplay;

// What a conversation looks like (docs/design/13 §3, 16 §3.5, F24).
//
// In the engine, like the spellmaker's screen and for the same reason: what a conversation *is* belongs
// to this feature, and only the drawing belongs to the client. The node's line is the panel's title and
// the things you may say are its rows — which means a conversation is the same list-with-reasons every
// other screen is, and the client learnt nothing new to show it.
public sealed class DialogueScreen : Screen
{
    public override float Width => 620f;      // wider than a spell list: these rows are sentences

    public override string Hint => "↑↓ choose    Enter say    Esc leave";

    public override void Build(World world, Entity subject)
    {
        var node = DialogueRules.Current(world);
        if (node == null)
        {
            Panel.Begin("", subject, "there is nobody to talk to");
            return;
        }

        var conversation = world.Resources.Get<Conversation>();
        string who = world.IsAlive(conversation.Speaker) ? World.Describe(conversation.Speaker) : "";
        Panel.Begin(node.Text.Length > 0 ? node.Text : who, subject);

        for (int i = 0; i < node.Options.Count; i++)
        {
            var option = node.Options[i];
            bool can = DialogueRules.CanPick(world, conversation.Listener, option, out string why);
            // The row's id is its place in the node, because an option is not a record and two of them
            // may say the same words.
            Panel.Add(PanelRow.Of(new RecordId("dialogue", i.ToString()), option.Text,
                                  detail: can ? "" : why, enabled: can, reason: why));
        }

        if (node.Options.Count == 0)
            Panel.Add(PanelRow.Of(new RecordId("dialogue", "0"), "(say nothing)", detail: ""));
    }

    // Saying it. The screen closes itself when the conversation ends, which is what makes "leave" and
    // "that is all" the same thing as far as a player is concerned.
    public override bool Activate(World world, Entity subject, in PanelRow row)
    {
        var node = DialogueRules.Current(world);
        if (node == null) return false;

        int index = Index;
        if (index < 0 || index >= node.Options.Count)
        {
            world.Resources.Get<Conversation>().Stop();
            return true;
        }

        DialogueRules.Pick(world, node.Options[index]);
        return true;
    }
}
