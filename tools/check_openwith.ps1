# Lists the apps Windows offers in "Open with" for a file type (read-only): the same list the picker shows.
param([string[]]$Ext = @('.stl'))
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
[ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAssocHandler {
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
    [PreserveSig] int IsRecommended();
}
[ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumAssocHandlers {
    [PreserveSig] int Next(int count, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler handler, out int fetched);
}
public static class OpenWith {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHAssocEnumHandlers(string ext, int filter, out IEnumAssocHandlers handlers);
    public static List<string> For(string ext) {
        var list = new List<string>();
        IEnumAssocHandlers e;
        if (SHAssocEnumHandlers(ext, 0, out e) != 0) return list;
        IAssocHandler h; int n;
        while (e.Next(1, out h, out n) == 0 && n == 1) {
            string name, ui; h.GetName(out name); h.GetUIName(out ui);
            list.Add((h.IsRecommended() == 0 ? "recommended  " : "other        ") + ui + "   [" + name + "]");
        }
        return list;
    }
}
'@
foreach ($e in $Ext) { "== $e"; [OpenWith]::For($e) | Sort-Object | ForEach-Object { "  $_" } }
