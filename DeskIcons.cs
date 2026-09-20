using System; using System.Reflection; using System.Runtime.InteropServices;
public static class DeskIcons {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; }
  [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IServiceProvider { [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppv); }
  [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellBrowser {
    void GetWindow(out IntPtr h); void ContextSensitiveHelp(bool f);
    void InsertMenusSB(IntPtr a, IntPtr b); void SetMenuSB(IntPtr a, IntPtr b, IntPtr c); void RemoveMenusSB(IntPtr a);
    void SetStatusTextSB(IntPtr a); void EnableModelessSB(bool f); void TranslateAcceleratorSB(IntPtr a, ushort b);
    void BrowseObject(IntPtr pidl, uint f); void GetViewStateStream(uint m, out IntPtr s); void GetControlWindow(uint id, out IntPtr h);
    void SendControlMsg(uint id, uint msg, IntPtr w, IntPtr l, out IntPtr r);
    void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
    void OnViewWindowActive(IntPtr v); void SetToolbarItems(IntPtr a, uint b, uint c);
  }
  [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IFolderView {
    void GetCurrentViewMode(out uint m); void SetCurrentViewMode(uint m);
    void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void Item(int i, out IntPtr pidl); void ItemCount(uint f, out int c);
    void Items(uint f, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void GetSelectionMarkedItem(out int i); void GetFocusedItem(out int i);
    void GetItemPosition(IntPtr pidl, out POINT pt); void GetSpacing(ref POINT pt); void GetDefaultSpacing(out POINT pt);
    [PreserveSig] int GetAutoArrange(); void SelectItem(int i, uint f);
    void SelectAndPositionItems(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [MarshalAs(UnmanagedType.LPArray)] POINT[] apt, uint f);
  }
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgao, out uint psfgao);
  [DllImport("shell32.dll")] static extern IntPtr ILFindLastID(IntPtr pidl);
  static IFolderView View() {
    object sw = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
    object[] args = new object[] { (object)0, null, 8, 0, 1 };
    ParameterModifier pm = new ParameterModifier(5); pm[0] = true; pm[1] = true; pm[3] = true;
    object disp = sw.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, sw, args, new ParameterModifier[] { pm }, null, null);
    if (disp == null) throw new Exception("desktop window not found");
    Guid sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); Guid iid = typeof(IShellBrowser).GUID; IntPtr p;
    int hr = ((IServiceProvider)disp).QueryService(ref sid, ref iid, out p); if (hr != 0) throw new Exception("QueryService " + hr);
    object v; ((IShellBrowser)Marshal.GetObjectForIUnknown(p)).QueryActiveShellView(out v);
    return (IFolderView)v;
  }
  static IntPtr Child(string path) { IntPtr abs; uint a; int hr = SHParseDisplayName(path, IntPtr.Zero, out abs, 0, out a); if (hr != 0) throw new Exception("parse " + path); return ILFindLastID(abs); }
  public static void Position(string[] paths, int[] xs, int[] ys) {
    IFolderView fv = View(); IntPtr[] pidls = new IntPtr[paths.Length]; POINT[] pts = new POINT[paths.Length];
    for (int i = 0; i < paths.Length; i++) { pidls[i] = Child(paths[i]); pts[i].x = xs[i]; pts[i].y = ys[i]; }
    fv.SelectAndPositionItems((uint)paths.Length, pidls, pts, 0x8);
  }
  public static int[] Get(string path) { POINT pt; View().GetItemPosition(Child(path), out pt); return new int[] { pt.x, pt.y }; }
  public static int[] Spacing() { POINT pt = new POINT(); View().GetSpacing(ref pt); return new int[] { pt.x, pt.y }; }
}
