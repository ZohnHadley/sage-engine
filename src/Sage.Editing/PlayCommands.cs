#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Sage.Editing;

// Play-in-editor's console commands (issue #226): `ed_play` builds a play world from the document and
// plays it, `ed_stop` throws it away. The toolbar's button and the host's key press these.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class PlayCommands
{
    public static void Register(CVarRegistry cvars, Func<PlaySession?> session)
    {
        cvars.RegisterCommand("ed_play", CVarFlags.DevOnly,
            "ed_play [x y z [yaw]]: play the document in a world of its own, the player at the free camera (or at x y z, or the scene's start headless).", a =>
        {
            if (!Current(session, out var play)) return;
            if (play.IsPlaying) { Log.Info(LogCat.Console, "ed_play: already playing (ed_stop)"); return; }

            PlayStart? from = play.Camera?.Invoke();
            if (a.Count >= 3)
            {
                if (!Number(a[0], out float x) || !Number(a[1], out float y) || !Number(a[2], out float z))
                {
                    Log.Warn(LogCat.Console, "ed_play [x y z [yaw]]");
                    return;
                }
                from = new PlayStart(new Vector3(x, y, z), a.Count >= 4 && Number(a[3], out float yaw) ? yaw : 0f);
            }
            play.Play(from);
        });

        cvars.RegisterCommand("ed_stop", CVarFlags.DevOnly, "ed_stop: stop playing and return to the editor; nothing the play did is kept.", _ =>
        {
            if (!Current(session, out var play)) return;
            if (!play.Stop()) Log.Info(LogCat.Console, "ed_stop: not playing");
        });
    }

    private static bool Current(Func<PlaySession?> session, [NotNullWhen(true)] out PlaySession? play)
    {
        play = session();
        if (play == null)
        {
            Log.Warn(LogCat.Console, "no world yet: play-in-editor works once a world exists");
            return false;
        }
        if (!play.EditWorld.Editing)
        {
            Log.Warn(LogCat.Console, "play-in-editor is the editor's: start the host with -edit");
            play = null;
            return false;
        }
        return true;
    }

    private static bool Number(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
