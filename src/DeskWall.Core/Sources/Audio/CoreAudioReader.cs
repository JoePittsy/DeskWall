using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace DeskWall.Core.Sources.Audio;

/// <summary>CoreAudio behind <see cref="IAudioReader"/>: the default render endpoint's master
/// volume, pushed by <c>IAudioEndpointVolumeCallback</c> rather than polled.
///
/// <para><b>Why the vtable is hand-built.</b> Implementing a COM interface from managed code needs
/// a callee-side object, and CsWin32's <c>PopulateVTable</c>/<c>ComHelpers.UnwrapCCW</c> path is
/// built on <c>ComWrappers.ComInterfaceDispatch</c> - which means writing a <c>ComWrappers</c>
/// subclass with its own <c>ComputeVtables</c>, and keeping a process-wide instance of it alive,
/// for one interface with one method. Four <c>[UnmanagedCallersOnly]</c> statics over a struct
/// whose first field is the vtable pointer is less code, has no ambient state, and is exactly the
/// shape native AOT wants. CLAUDE.md's callback rule applies: the statics are stdcall, and nothing
/// may escape them.</para>
///
/// <para><b>Two locks, deliberately.</b> <c>_com</c> guards the COM pointers and every call into
/// them; <c>_state</c> guards the four fields the notification writes and <see cref="Current"/>
/// reads. They are never nested COM-first, because <c>UnregisterControlChangeNotify</c> can block
/// until a notification already in flight returns: one lock would then have the unregistering
/// thread holding what the callback is waiting for, which is a deadlock in the audio service.</para>
///
/// <para>Nothing here throws. A failure of any kind reports "no endpoint", the same contract
/// <c>IHardwareReader</c> keeps.</para></summary>
public sealed unsafe class CoreAudioReader : IAudioReader
{
    private readonly object _com = new();
    private readonly object _state = new();

    private IMMDeviceEnumerator* _enumerator;
    private IAudioEndpointVolume* _volume;
    private Shim* _shim;
    private bool _disposed;

    private string? _registeredId;
    private string _registeredName = "";
    private double _scalar;
    private bool _muted;

    public event Action? Changed;

    /// <summary>eMultimedia, not eConsole: it is the role the Windows volume flyout and the
    /// keyboard volume keys move, so this is the number the owner sees change on screen.</summary>
    private const ERole Role = ERole.eMultimedia;

    public string? DefaultDeviceId
    {
        get
        {
            lock (_com)
            {
                if (_disposed) return null;
                IMMDevice* device = null;
                try
                {
                    device = DefaultEndpoint();
                    return device is null ? null : IdOf(device);
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (device is not null) device->Release();
                }
            }
        }
    }

    public string? RegisteredDeviceId
    {
        get { lock (_state) return _registeredId; }
    }

    public AudioReading? Current
    {
        get
        {
            lock (_state)
                return _registeredId is null ? null : new AudioReading(_registeredId, _registeredName, _scalar, _muted);
        }
    }

    public void Register()
    {
        lock (_com)
        {
            if (_disposed) return;
            Unregister();

            IMMDevice* device = null;
            IAudioEndpointVolume* volume = null;
            try
            {
                device = DefaultEndpoint();
                if (device is null) return;              // no playback device: not an error
                var id = IdOf(device);
                var name = NameOf(device);

                var iid = IAudioEndpointVolume.IID_Guid;
                device->Activate(iid, CLSCTX.CLSCTX_INPROC_SERVER, null, out var raw);
                volume = (IAudioEndpointVolume*)raw;
                volume->GetMasterVolumeLevelScalar(out var level);
                volume->GetMute(out var muted);

                // State first, callback second: a notification that arrives the instant the
                // registration lands must not find _registeredId still pointing at the old
                // endpoint and publish the new level under the old device's name.
                lock (_state)
                {
                    _registeredId = id;
                    _registeredName = name;
                    _scalar = Math.Clamp(level, 0f, 1f);
                    _muted = muted;
                }
                volume->RegisterControlChangeNotify((IAudioEndpointVolumeCallback*)EnsureShim());
                _volume = volume;
                volume = null;                            // owned by the field now
            }
            catch (Exception)
            {
                lock (_state) _registeredId = null;
            }
            finally
            {
                if (volume is not null) volume->Release();
                if (device is not null) device->Release();
            }
        }
    }

    /// <summary>Caller holds <c>_com</c>.</summary>
    private void Unregister()
    {
        if (_volume is null) return;
        var volume = _volume;
        _volume = null;
        lock (_state) _registeredId = null;
        try
        {
            if (_shim is not null) volume->UnregisterControlChangeNotify((IAudioEndpointVolumeCallback*)_shim);
        }
        catch (Exception)
        {
        }
        volume->Release();
    }

    /// <summary>Caller holds <c>_com</c>. Null when the machine has no active render endpoint;
    /// GetDefaultAudioEndpoint throws E_NOTFOUND for that, which is a fact, not a failure.</summary>
    private IMMDevice* DefaultEndpoint()
    {
        if (_enumerator is null)
        {
            Com.EnsureInitialized();
            MMDeviceEnumerator.CreateInstance(out IMMDeviceEnumerator* e).ThrowOnFailure();
            _enumerator = e;
        }
        IMMDevice* device = null;
        try
        {
            _enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, Role, &device);
        }
        catch (COMException)
        {
            return null;
        }
        return device;
    }

    private static string IdOf(IMMDevice* device)
    {
        device->GetId(out var id);
        if (id.Value is null) return "";
        var s = id.ToString();
        PInvoke.CoTaskMemFree(id.Value);
        return s;
    }

    /// <summary>The endpoint's friendly name. An endpoint with no readable property store is
    /// unusual but not fatal: the volume still reads, so the name falls back to empty.</summary>
    private static string NameOf(IMMDevice* device)
    {
        IPropertyStore* store = null;
        try
        {
            device->OpenPropertyStore(STGM.STGM_READ, &store);
            store->GetValue(PInvoke.PKEY_Device_FriendlyName, out var pv);
            try
            {
                return pv.pwszVal.Value is null ? "" : pv.pwszVal.ToString();
            }
            finally
            {
                PInvoke.PropVariantClear(ref pv);
            }
        }
        catch (COMException)
        {
            return "";
        }
        finally
        {
            if (store is not null) store->Release();
        }
    }

    /// <summary>Called from the COM callback, on an audio service thread. Touches no COM.</summary>
    private void Publish(float volume, bool muted)
    {
        Action? handler;
        lock (_state)
        {
            // A notification can outrace an unregister; with no registered endpoint there is
            // nothing for it to be about.
            if (_registeredId is null) return;
            _scalar = Math.Clamp(volume, 0f, 1f);
            _muted = muted;
            handler = Changed;
        }
        handler?.Invoke();
    }

    public void Dispose()
    {
        lock (_com)
        {
            if (_disposed) return;
            _disposed = true;
            Unregister();
            if (_enumerator is not null) { _enumerator->Release(); _enumerator = null; }
            ReleaseShim();
        }
    }

    // ---- the hand-built IAudioEndpointVolumeCallback ----------------------------------------

    /// <summary>A COM object whose first field is its vtable pointer, so a <c>Shim*</c> is a valid
    /// <c>IAudioEndpointVolumeCallback*</c>. <c>Owner</c> is a GCHandle back to the reader; it is
    /// what keeps the reader alive while CoreAudio can still call in, and what makes disposing the
    /// reader necessary rather than optional.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Shim
    {
        public void** Vtbl;
        public nint Owner;
        public int RefCount;
    }

    private static void** s_vtbl;
    private static readonly object s_vtblLock = new();

    private static readonly Guid IidIUnknown = new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    private const int S_OK = 0;
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    /// <summary>One vtable for the whole process: it is four function pointers and it never
    /// changes, so allocating one per reader would be waste with an extra free to get wrong.</summary>
    private static void** Vtable()
    {
        lock (s_vtblLock)
        {
            if (s_vtbl is not null) return s_vtbl;
            var v = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
            v[0] = (void*)(delegate* unmanaged[Stdcall]<Shim*, Guid*, void**, int>)&CbQueryInterface;
            v[1] = (void*)(delegate* unmanaged[Stdcall]<Shim*, uint>)&CbAddRef;
            v[2] = (void*)(delegate* unmanaged[Stdcall]<Shim*, uint>)&CbRelease;
            v[3] = (void*)(delegate* unmanaged[Stdcall]<Shim*, AUDIO_VOLUME_NOTIFICATION_DATA*, int>)&CbOnNotify;
            s_vtbl = v;
            return v;
        }
    }

    /// <summary>Caller holds <c>_com</c>. One shim per reader, made on first registration and
    /// reused across every re-registration, so plugging a headset in and out does not churn
    /// native allocations.</summary>
    private Shim* EnsureShim()
    {
        if (_shim is not null) return _shim;
        var shim = (Shim*)NativeMemory.AllocZeroed(1, (nuint)sizeof(Shim));
        shim->Vtbl = Vtable();
        shim->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(this, GCHandleType.Normal));
        shim->RefCount = 1;                              // the reference this reader itself holds
        _shim = shim;
        return shim;
    }

    /// <summary>Caller holds <c>_com</c>, and has already unregistered.</summary>
    private void ReleaseShim()
    {
        if (_shim is null) return;
        var shim = _shim;
        _shim = null;
        if (Interlocked.Decrement(ref shim->RefCount) == 0) FreeShim(shim);
    }

    private static void FreeShim(Shim* shim)
    {
        if (shim->Owner != 0) GCHandle.FromIntPtr(shim->Owner).Free();
        NativeMemory.Free(shim);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbQueryInterface(Shim* self, Guid* iid, void** ppv)
    {
        if (ppv is null) return E_POINTER;
        if (self is not null && iid is not null && (*iid == IidIUnknown || *iid == IAudioEndpointVolumeCallback.IID_Guid))
        {
            Interlocked.Increment(ref self->RefCount);
            *ppv = self;
            return S_OK;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CbAddRef(Shim* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CbRelease(Shim* self)
    {
        var n = Interlocked.Decrement(ref self->RefCount);
        if (n == 0) FreeShim(self);
        return (uint)n;
    }

    /// <summary>The one that matters. It runs on an audio service thread inside native code's own
    /// call frame: an exception crossing back into it takes the whole process down, so everything
    /// here is inside the catch, including the subscriber's handler. It also does not call back
    /// into COM - the notification already carries the new scalar and mute flag, which is the
    /// whole reason this source needs no poll.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbOnNotify(Shim* self, AUDIO_VOLUME_NOTIFICATION_DATA* data)
    {
        try
        {
            if (self is null || data is null || self->Owner == 0) return S_OK;
            if (GCHandle.FromIntPtr(self->Owner).Target is CoreAudioReader reader)
                reader.Publish(data->fMasterVolume, data->bMuted);
        }
        catch (Exception)
        {
        }
        return S_OK;
    }
}
