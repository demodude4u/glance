# Shows which program Windows will launch for each model file type (read-only).
Add-Type -Namespace Win -Name Assoc -MemberDefinition @'
[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
public static extern int AssocQueryStringW(int flags, int str, string assoc, string extra, System.Text.StringBuilder buf, ref int size);
'@
foreach ($ext in '.stl', '.obj', '.3mf', '.ply', '.glb', '.gltf', '.step', '.stp', '.igs', '.iges') {
    $sb = New-Object System.Text.StringBuilder 1024; $n = 1024
    $hr = [Win.Assoc]::AssocQueryStringW(0, 2, $ext, 'open', $sb, [ref]$n)      # ASSOCSTR_EXECUTABLE
    $exe = if ($hr -eq 0) { $sb.ToString() } else { '(none)' }
    $choice = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$ext\UserChoice" -ErrorAction SilentlyContinue).ProgId
    $class = (Get-Item "HKCU:\Software\Classes\$ext" -ErrorAction SilentlyContinue)
    $default = if ($class) { $class.GetValue('') } else { '' }
    '{0,-6} opens with: {1,-60} class default: {2,-20} your saved choice: {3}' -f $ext, $exe, $default, $choice
}
