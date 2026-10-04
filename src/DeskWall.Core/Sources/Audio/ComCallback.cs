using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskWall.Core.Sources.Audio;

/// <summary>A COM object implemented by hand: a native struct whose first field is its vtable
/// pointer, so a <c>ComCallbackObject*</c> is a valid pointer to whichever interface the vtable
/// describes. <c>Owner</c> is a GCHandle back to the managed object the callbacks forward to; it
/// is what keeps that object alive while native code can still call in, and what makes disposing
/// the owner necessary rather than optional. <c>Iid</c> is the one interface besides IUnknown that
/// QueryInterface answers for.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ComCallbackObject
{
    public unsafe void** Vtbl;
    public nint Owner;
    public int RefCount;
    public Guid Iid;
}

/// <summary>The IUnknown half every hand-built callback shares, and the allocation rules.
/// <para><b>Why not ComWrappers.</b> CsWin32's <c>PopulateVTable</c>/<c>ComHelpers.UnwrapCCW</c>
/// path is built on <c>ComWrappers.ComInterfaceDispatch</c>: a <c>ComWrappers</c> subclass with
/// its own <c>ComputeVtables</c>, and a process-wide instance of it kept alive, for an interface
/// with one or two methods that matter. Static <c>[UnmanagedCallersOnly]</c> functions over a
/// struct whose first field is the vtable pointer are less code, carry no ambient state, and are
/// exactly the shape native AOT wants. CLAUDE.md's callback rule applies: every slot is stdcall,
/// and nothing may escape one.</para></summary>
internal static unsafe class ComCallback
{
    private static readonly Guid IidIUnknown = new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    public const int S_OK = 0;
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    /// <summary>A vtable of <paramref name="slots"/> entries with IUnknown's three already filled
    /// in. Allocated once per interface per process and never freed: it is a few function
    /// pointers that never change.</summary>
    public static void** NewVtable(int slots)
    {
        var v = (void**)NativeMemory.Alloc((nuint)slots, (nuint)sizeof(void*));
        v[0] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, Guid*, void**, int>)&QueryInterface;
        v[1] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, uint>)&AddRef;
        v[2] = (void*)(delegate* unmanaged[Stdcall]<ComCallbackObject*, uint>)&Release;
        return v;
    }

    /// <summary>A new object holding one reference, the owner's own.</summary>
    public static ComCallbackObject* Create(void** vtbl, Guid iid, object owner)
    {
        var o = (ComCallbackObject*)NativeMemory.AllocZeroed(1, (nuint)sizeof(ComCallbackObject));
        o->Vtbl = vtbl;
        o->Iid = iid;
        o->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner, GCHandleType.Normal));
        o->RefCount = 1;
        return o;
    }

    /// <summary>Drops the owner's reference. The object is freed when native code has let go of
    /// its own references too, which it may do later, from its own thread.</summary>
    public static void ReleaseOwned(ComCallbackObject* o)
    {
        if (Interlocked.Decrement(ref o->RefCount) == 0) Free(o);
    }

    /// <summary>The owner, for a callback to forward to; null when the handle is gone.</summary>
    public static T? OwnerOf<T>(ComCallbackObject* self) where T : class
        => self is null || self->Owner == 0 ? null : GCHandle.FromIntPtr(self->Owner).Target as T;

    private static void Free(ComCallbackObject* o)
    {
        if (o->Owner != 0) GCHandle.FromIntPtr(o->Owner).Free();
        NativeMemory.Free(o);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(ComCallbackObject* self, Guid* iid, void** ppv)
    {
        if (ppv is null) return E_POINTER;
        if (self is not null && iid is not null && (*iid == IidIUnknown || *iid == self->Iid))
        {
            Interlocked.Increment(ref self->RefCount);
            *ppv = self;
            return S_OK;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(ComCallbackObject* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(ComCallbackObject* self)
    {
        var n = Interlocked.Decrement(ref self->RefCount);
        if (n == 0) Free(self);
        return (uint)n;
    }
}
