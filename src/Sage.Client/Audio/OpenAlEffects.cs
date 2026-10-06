#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Audio;

namespace Sage.Client;

// The low-pass filter and the reverb, on the device (issue #329).
//
// MonoGame's DesktopGL plays through OpenAL Soft, which has the EFX extension: per-source filters and
// auxiliary effect slots. MonoGame uses them for XACT only and keeps them internal, so this reaches them
// the narrowest way there is: the EFX entry points come from OpenAL itself (`alGetProcAddress`, in the
// library MonoGame already loaded), and the one thing taken from MonoGame is the OpenAL source id of a
// playing `SoundEffectInstance` (its internal `SourceId`, by reflection). No new native library.
//
// **It degrades, it does not fail.** No EFX, no export, a MonoGame without that field: one warning, and
// the backend goes on with occlusion as volume only and no reverb — which is what the mixer's numbers
// already give it. MonoGame itself unbinds a source's filter and send when it recycles the source.
//
// One filter object serves every voice: EFX copies a filter's values into the source when it is bound,
// so it is set and bound per voice, only when that voice's value moved. One effect slot holds the reverb
// (EFX's standard reverb), fed by every positional voice; the zone blend changes its values and level.
internal sealed class OpenAlEffects : IDisposable
{
    // ---- EFX, as OpenAL defines it (efx.h) -------------------------------------------------------------
    private const int AL_DIRECT_FILTER = 0x20005;
    private const int AL_AUXILIARY_SEND_FILTER = 0x20006;
    private const int AL_FILTER_TYPE = 0x8001;
    private const int AL_FILTER_LOWPASS = 0x0001;
    private const int AL_LOWPASS_GAIN = 0x0001;
    private const int AL_LOWPASS_GAINHF = 0x0002;
    private const int AL_EFFECT_TYPE = 0x8001;
    private const int AL_EFFECT_REVERB = 0x0001;
    private const int AL_REVERB_DENSITY = 0x0001;
    private const int AL_REVERB_DIFFUSION = 0x0002;
    private const int AL_REVERB_GAIN = 0x0003;
    private const int AL_REVERB_GAINHF = 0x0004;
    private const int AL_REVERB_DECAY_TIME = 0x0005;
    private const int AL_REVERB_DECAY_HFRATIO = 0x0006;
    private const int AL_REVERB_REFLECTIONS_GAIN = 0x0007;
    private const int AL_REVERB_REFLECTIONS_DELAY = 0x0008;
    private const int AL_REVERB_LATE_REVERB_GAIN = 0x0009;
    private const int AL_REVERB_LATE_REVERB_DELAY = 0x000A;
    private const int AL_EFFECTSLOT_EFFECT = 0x0001;
    private const int AL_EFFECTSLOT_GAIN = 0x0002;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Gen(int n, out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Delete(int n, ref uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetI(uint id, int param, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetF(uint id, int param, float value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Set3I(uint id, int param, int a, int b, int c);

    private sealed class Api
    {
        public required Gen GenFilters, GenEffects, GenSlots;
        public required Delete DeleteFilters, DeleteEffects, DeleteSlots;
        public required SetI Filteri, Effecti, Sloti, Sourcei;
        public required SetF Filterf, Effectf, Slotf;
        public required Set3I Source3i;
    }

    private static readonly FieldInfo? SourceIdField =
        typeof(SoundEffectInstance).GetField("SourceId", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? HasSourceField =
        typeof(SoundEffectInstance).GetField("HasSourceId", BindingFlags.Instance | BindingFlags.NonPublic);

    // The same two fields without boxing, once TryCreate has checked they are there (per voice per frame).
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "SourceId")]
    private static extern ref int SourceIdOf(SoundEffectInstance instance);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "HasSourceId")]
    private static extern ref bool HasSourceOf(SoundEffectInstance instance);

    private readonly Api _al;
    private readonly uint _filter;
    private readonly uint _effect;
    private readonly uint _slot;

    // Per voice: the source it was last bound on, the low-pass it has, whether it feeds the reverb.
    private readonly Dictionary<int, Bound> _bound = new();
    private ReverbMix _reverb = ReverbMix.Dry;
    private bool _reverbSet;

    private struct Bound
    {
        public int Source;
        public float LowPass;
        public bool Send;
    }

    private OpenAlEffects(Api al, uint filter, uint effect, uint slot)
    {
        _al = al;
        _filter = filter;
        _effect = effect;
        _slot = slot;
    }

    // The effects, or null (and one warning) when this OpenAL or this MonoGame cannot give them.
    public static OpenAlEffects? TryCreate()
    {
        string? why = null;
        OpenAlEffects? made = null;
        try
        {
            if (SourceIdField?.FieldType != typeof(int) || HasSourceField?.FieldType != typeof(bool))
                why = "this MonoGame does not expose a sound's OpenAL source";
            else if (!NativeLibrary.TryLoad("openal", typeof(SoundEffect).Assembly, null, out var library))
                why = "the OpenAL library could not be found";
            else if (!NativeLibrary.TryGetExport(library, "alGetProcAddress", out var getProc) ||
                     !NativeLibrary.TryGetExport(library, "alSourcei", out var sourcei) ||
                     !NativeLibrary.TryGetExport(library, "alSource3i", out var source3i))
                why = "the OpenAL library has no alGetProcAddress";
            else
            {
                var proc = Marshal.GetDelegateForFunctionPointer<GetProcAddress>(getProc);
                T? Get<T>(string name) where T : Delegate
                {
                    var p = proc(name);
                    return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
                }
                var genFilters = Get<Gen>("alGenFilters");
                var genEffects = Get<Gen>("alGenEffects");
                var genSlots = Get<Gen>("alGenAuxiliaryEffectSlots");
                var deleteFilters = Get<Delete>("alDeleteFilters");
                var deleteEffects = Get<Delete>("alDeleteEffects");
                var deleteSlots = Get<Delete>("alDeleteAuxiliaryEffectSlots");
                var filteri = Get<SetI>("alFilteri");
                var effecti = Get<SetI>("alEffecti");
                var sloti = Get<SetI>("alAuxiliaryEffectSloti");
                var filterf = Get<SetF>("alFilterf");
                var effectf = Get<SetF>("alEffectf");
                var slotf = Get<SetF>("alAuxiliaryEffectSlotf");
                if (genFilters == null || genEffects == null || genSlots == null || deleteFilters == null ||
                    deleteEffects == null || deleteSlots == null || filteri == null || effecti == null ||
                    sloti == null || filterf == null || effectf == null || slotf == null)
                    why = "this OpenAL has no EFX";
                else
                {
                    var al = new Api
                    {
                        GenFilters = genFilters, GenEffects = genEffects, GenSlots = genSlots,
                        DeleteFilters = deleteFilters, DeleteEffects = deleteEffects, DeleteSlots = deleteSlots,
                        Filteri = filteri, Effecti = effecti, Sloti = sloti,
                        Filterf = filterf, Effectf = effectf, Slotf = slotf,
                        Sourcei = Marshal.GetDelegateForFunctionPointer<SetI>(sourcei),
                        Source3i = Marshal.GetDelegateForFunctionPointer<Set3I>(source3i),
                    };
                    al.GenFilters(1, out uint filter);
                    al.GenEffects(1, out uint effect);
                    al.GenSlots(1, out uint slot);
                    if (filter == 0 || effect == 0 || slot == 0)
                        why = "OpenAL would not make a filter or an effect slot";
                    else
                    {
                        al.Filteri(filter, AL_FILTER_TYPE, AL_FILTER_LOWPASS);
                        al.Effecti(effect, AL_EFFECT_TYPE, AL_EFFECT_REVERB);
                        al.Slotf(slot, AL_EFFECTSLOT_GAIN, 0f);
                        al.Sloti(slot, AL_EFFECTSLOT_EFFECT, (int)effect);
                        made = new OpenAlEffects(al, filter, effect, slot);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException
                                       or BadImageFormatException or FieldAccessException)
        {
            why = ex.Message;
        }

        if (made != null) Log.Info(LogCat.Audio, "Audio effects: OpenAL EFX low-pass and reverb are on");
        else Log.Once(LogCat.Audio, LogLevel.Warn, "openal-efx",
                      $"Audio effects are off ({why}): occlusion is volume only and there is no reverb");
        return made;
    }

    // Once a frame: the reverb heard now. Only what moved is sent.
    public void SetReverb(in ReverbMix mix)
    {
        if (_reverbSet && mix == _reverb) return;
        _reverb = mix;
        _reverbSet = true;
        _al.Effectf(_effect, AL_REVERB_DENSITY, Math.Clamp(mix.Density, 0f, 1f));
        _al.Effectf(_effect, AL_REVERB_DIFFUSION, Math.Clamp(mix.Diffusion, 0f, 1f));
        _al.Effectf(_effect, AL_REVERB_GAIN, Math.Clamp(mix.Gain, 0f, 1f));
        _al.Effectf(_effect, AL_REVERB_GAINHF, Math.Clamp(mix.GainHF, 0f, 1f));
        _al.Effectf(_effect, AL_REVERB_DECAY_TIME, Math.Clamp(mix.DecayTime, 0.1f, 20f));
        _al.Effectf(_effect, AL_REVERB_DECAY_HFRATIO, Math.Clamp(mix.DecayHFRatio, 0.1f, 2f));
        _al.Effectf(_effect, AL_REVERB_REFLECTIONS_GAIN, Math.Clamp(mix.ReflectionsGain, 0f, 3.16f));
        _al.Effectf(_effect, AL_REVERB_REFLECTIONS_DELAY, Math.Clamp(mix.ReflectionsDelay, 0f, 0.3f));
        _al.Effectf(_effect, AL_REVERB_LATE_REVERB_GAIN, Math.Clamp(mix.LateGain, 0f, 10f));
        _al.Effectf(_effect, AL_REVERB_LATE_REVERB_DELAY, Math.Clamp(mix.LateDelay, 0f, 0.1f));
        // A slot copies its effect when the effect is attached, so it is attached again after a change.
        _al.Sloti(_slot, AL_EFFECTSLOT_EFFECT, (int)_effect);
        _al.Slotf(_slot, AL_EFFECTSLOT_GAIN, Math.Clamp(mix.Mix, 0f, 1f));
    }

    // After the voice's instance has been pushed (and, the first time, played): its low-pass and its send.
    public void Apply(SoundEffectInstance instance, Voice voice)
    {
        if (!HasSourceOf(instance)) return;
        int source = SourceIdOf(instance);
        int id = voice.Handle.Id;
        bool fresh = !_bound.TryGetValue(id, out var bound) || bound.Source != source;
        if (fresh) bound = new Bound { Source = source, LowPass = 1f };

        float lowPass = Math.Clamp(voice.LowPass, 0f, 1f);
        if (fresh ? lowPass < 0.999f : MathF.Abs(lowPass - bound.LowPass) > 0.005f)
        {
            if (lowPass >= 0.999f) _al.Sourcei((uint)source, AL_DIRECT_FILTER, 0);
            else
            {
                _al.Filterf(_filter, AL_LOWPASS_GAIN, 1f);
                _al.Filterf(_filter, AL_LOWPASS_GAINHF, lowPass);
                _al.Sourcei((uint)source, AL_DIRECT_FILTER, (int)_filter);
            }
            bound.LowPass = lowPass;
        }

        // Where a sound is matters to how a room answers it; a 2D one (a menu, the music) is not in the room.
        if (voice.Positional && !bound.Send)
        {
            _al.Source3i((uint)source, AL_AUXILIARY_SEND_FILTER, (int)_slot, 0, 0);
            bound.Send = true;
        }

        _bound[id] = bound;
    }

    public void Forget(int voice) => _bound.Remove(voice);

    public void Dispose()
    {
        _bound.Clear();
        uint slot = _slot, effect = _effect, filter = _filter;
        _al.Sloti(slot, AL_EFFECTSLOT_EFFECT, 0);
        _al.DeleteSlots(1, ref slot);
        _al.DeleteEffects(1, ref effect);
        _al.DeleteFilters(1, ref filter);
    }
}
