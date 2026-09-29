#nullable enable

namespace Sage.Gameplay;

// The narrative half of the gameplay set (16 §3.5, F24; issue #26): quests and dialogue, each its own
// plugin. They used to ride in FactionsModule, so a game could not have one without the others, and
// the effect system called quests directly when something died.
//
// Now **nothing calls into them**. A death reaches quests as a `Died` event; dialogue reaches quests
// and factions only through their static rules, which answer "not on it" and "standing 0" when the
// plugin is missing; and the client opens a conversation only when this world has one. So any of
// factions, quests and dialogue can be switched off (`modules.disable`) and the rest still work
// (test: EachNarrativePluginCanBeSwitchedOffAlone). `sage.gameplay.*` still brings them all.

// Something to do, and something that notices you did it (16 §3.5): quest records, the saved Journal,
// the kills it counts, and the console's view of it.
[Plugin("sage.gameplay.quests", "0.1.0")]
public sealed class QuestsModule : IModule
{
    public void Init(ModuleContext ctx)
    {
        // The quest record and the saved Journal are this plugin's by their attributes (Plugin =
        // "sage.gameplay.quests"); generated code registers them (issues #16, #17).

        ctx.Engine.CVars.RegisterCommand("quests", CVarFlags.None,
            "What you are on, and how far.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (Quests.JournalOf(world) is not { } journal) continue;
                if (journal.Entries.Count == 0) Log.Info(LogCat.Console, "  (nothing in the journal)");
                foreach (var entry in journal.Entries)
                    Log.Info(LogCat.Console, $"  {entry.Quest,-28} {(entry.Finished ? "done" : entry.Stage)}");
            }
        });

        ctx.Engine.CVars.RegisterCommand("quest_start", CVarFlags.Cheat, "quest_start <quest>: put it in the journal.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "quest_start <quest>"); return; }
            var id = ctx.Engine.Records.Resolve("quest", a[0]);
            if (id.IsEmpty) return;
            foreach (var world in ctx.Engine.Worlds)
                if (Quests.JournalOf(world) != null)
                    Log.Info(LogCat.Console, Quests.Start(world, id) ? $"started {id}" : $"already on {id}");
        });

        ctx.Engine.CVars.RegisterCommand("quest_stage", CVarFlags.Cheat,
            "quest_stage <quest> <stage>: move a quest along by hand.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "quest_stage <quest> <stage>"); return; }
            var id = ctx.Engine.Records.Resolve("quest", a[0]);
            if (id.IsEmpty) return;
            foreach (var world in ctx.Engine.Worlds)
                if (Quests.JournalOf(world) != null)
                    Log.Info(LogCat.Console, Quests.SetStage(world, id, a[1]) ? $"{id} -> {a[1]}" : $"{id} did not move");
        });
    }

    // What the player is on is saved with the world (09 §3.1).
    public void OnWorldCreated(World world)
    {
        world.Resources.Add(new Journal());
        world.AddSystem(new QuestDeathSystem(world));
        world.AddSystem(new QuestWatchSystem(world));   // conversations and places (issue #28)
    }
}

// Talking to somebody (16 §3.5): dialogue records, the `dialogue` prefab part and the world's one
// Conversation. The conversation is not saved: a save taken mid-sentence resumes with the window
// closed, which is the honest behaviour — what the conversation *did* is already in the world.
[Plugin("sage.gameplay.dialogue", "0.1.0")]
public sealed class DialogueModule : IModule
{
    // The dialogue record and the `dialogue` part are this plugin's by their attributes (Plugin =
    // "sage.gameplay.dialogue"); generated code registers them (issues #16, #17).
    public void Init(ModuleContext ctx) { }

    public void OnWorldCreated(World world) => world.Resources.Add(new Conversation());
}
