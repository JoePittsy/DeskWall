using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Media.Audio;
using Windows.Win32.Foundation;

namespace DeskWall.Core.Sources.Audio;

/// <summary><see cref="IAudioDeviceNotifier"/> on CoreAudio's <c>IMMNotificationClient</c>,
/// registered on an enumerator of its own. The client is hand-built on
/// <see cref="ComCallback"/>, like the volume callback in <see cref="CoreAudioReader"/>.
/// <para>Of the five notifications only two are forwarded. <c>OnDeviceAdded</c> and
/// <c>OnDeviceRemoved</c> are about an endpoint being installed or uninstalled, which is not the
/// same as becoming usable (plugging a headset in is a <em>state</em> change); every one of them
/// that matters is followed by a state or default change anyway. <c>OnPropertyValueChanged</c>
/// fires for any property on any endpoint, many times a second while some drivers settle, and
/// the source publishes nothing it would change.</para>
/// <para>Nothing here throws, and nothing escapes a callback.</para></summary>
public sealed unsafe class CoreAudioDeviceNotifier : IAudioDeviceNotifier
{
    private readonly object _com = new();
    private IMMDeviceEnumerator* _enumerator;
    private ComCallbackObject* _client;
    private bool _registered;
    private bool _disposed;

    public event Action<string?>? DefaultDeviceChanged;
    public event Action<string>? DeviceStateChanged;

    /// <summary>True once the client is registered. For tests and measurements; the source does
    /// not need to know.</summary>
    public bool IsListening
    {
        get { lock (_com) return _registered; }
    }

    public bool TryStart(out string? error)
    {
        lock (_com)
        {
            error = null;
            if (_registered) return true;
            if (_disposed) { error = "disposed"; return false; }
            try
            {
                if (_enumerator is null)
                {
                    Com.EnsureInitialized();
                    MMDeviceEnumerator.CreateInstance(out IMMDeviceEnumerator* e).ThrowOnFailure();
                    _enumerator = e;
                }
                _client = ComCallback.Create(Vtable(), IMMNotificationClient.IID_Guid, this);
                _enumerator->RegisterEndpointNotificationCallback((IMMNotificationClient*)_client);
                _registered = true;
                return true;
            }
            catch (Exception ex)
            {
                if (_client is not null) { ComCallback.ReleaseOwned(_client); _client = null; }
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>Unregisters, then lets go. <c>UnregisterEndpointNotificationCallback</c> waits for a
    /// notification already in flight to return, which is safe here because the callbacks take
    /// no lock of ours. Idempotent.</summary>
    public void Dispose()
    {
        lock (_com)
        {
            if (_disposed) return;
            _disposed = true;
            if (_registered)
            {
                try { _enumerator->UnregisterEndpointNotificationCallback((IMMNotificationClient*)_client); }
                catch (Exception) { }
                _registered = false;
            }
            if (_client is not null) { ComCallback.ReleaseOwned(_client); _client = null; }
            if (_enumerator is not null) { _enumerator->Release(); _enumerator = null; }
        }
    }

    // ---- the hand-built IMMNotificationClient -----------------------------------------------

    private static void** s_vtbl;
    private static readonly object s_vtblLock = new();

    /// <summary>IUnknown, then OnDeviceStateChanged, OnDeviceAdded, OnDeviceRemoved,
    /// OnDefaultDeviceChanged, OnPropertyValueChanged, in the interface's own order.</summary>
    private static void** Vtable()
    {
        lock (s_vtblLock)
        {
            if (s_vtbl is not null) return s_vtbl;
            var v = ComCallback.NewVtable(8);
            v[3] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, char*, uint, int>)&CbOnDeviceStateChanged;
            v[4] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, char*, int>)&CbIgnoreDevice;
            v[5] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, char*, int>)&CbIgnoreDevice;
            v[6] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, EDataFlow, ERole, char*, int>)&CbOnDefaultDeviceChanged;
            v[7] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, char*, PROPERTYKEY, int>)&CbOnPropertyValueChanged;
            s_vtbl = v;
            return v;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbOnDeviceStateChanged(ComCallbackObject* self, char* id, uint state)
    {
        try
        {
            if (id is not null && ComCallback.OwnerOf<CoreAudioDeviceNotifier>(self) is { } n)
                n.DeviceStateChanged?.Invoke(new string(id));
        }
        catch (Exception)
        {
        }
        return ComCallback.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbIgnoreDevice(ComCallbackObject* self, char* id) => ComCallback.S_OK;

    /// <summary>Fires once per role and per flow, so a headset arriving is up to six calls; only
    /// the render endpoint for <see cref="CoreAudioReader.Role"/> is the one the source reads.
    /// A null id means that role has no endpoint left.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbOnDefaultDeviceChanged(ComCallbackObject* self, EDataFlow flow, ERole role, char* id)
    {
        try
        {
            if (flow == EDataFlow.eRender && role == CoreAudioReader.Role
                && ComCallback.OwnerOf<CoreAudioDeviceNotifier>(self) is { } n)
                n.DefaultDeviceChanged?.Invoke(id is null ? null : new string(id));
        }
        catch (Exception)
        {
        }
        return ComCallback.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CbOnPropertyValueChanged(ComCallbackObject* self, char* id, PROPERTYKEY key) => ComCallback.S_OK;
}
