# Asks Windows which registered handler is the effective default for each model type (read-only).
# This is the same lookup Explorer uses on double-click, saved "open with" choices included.
Add-Type @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("4e530b0a-e611-4c77-a3ac-9031d022281b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAppAssocReg {
    [PreserveSig] int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string query, int type, int level, [MarshalAs(UnmanagedType.LPWStr)] out string progId);
}
public static class DefaultApp {
    public static string For(string ext) {
        var reg = (IAppAssocReg)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("591209c7-767b-42b2-9fba-44ee4615f2c7")));
        string id; int hr = reg.QueryCurrentDefault(ext, 0, 1, out id);
        return hr == 0 ? id : "(none, 0x" + hr.ToString("x8") + ")";
    }
}
'@
$ok = $true
foreach ($ext in '.stl', '.obj', '.3mf', '.ply', '.glb', '.gltf', '.step', '.stp', '.igs', '.iges') {
    $id = [DefaultApp]::For($ext)
    $cmd = (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\$id\shell\open\command" -ErrorAction SilentlyContinue).'(default)'
    if ($id -notlike 'Glance.*') { $ok = $false }
    '{0,-6} default handler: {1,-22} {2}' -f $ext, $id, $cmd
}
if ($ok) { 'All six types open with Glance.' } else { 'At least one type does not open with Glance.' }
