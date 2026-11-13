# Opens the Windows "Open with" picker for a file for a couple of seconds, prints what it lists, and closes
# that one window again. The picker only runs on the real desktop, so this is visible while it runs.
param([Parameter(Mandatory)][string]$File, [int]$Max = 14)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]; $C = [System.Windows.Automation.Condition]
function Tops { $A::RootElement.FindAll($T::Children, $C::TrueCondition) | ForEach-Object { $_ } }
$known = @(Tops | ForEach-Object { $_.Current.NativeWindowHandle })
Start-Process -FilePath "$env:WINDIR\System32\OpenWith.exe" -ArgumentList "`"$File`""
$win = $null; $deadline = (Get-Date).AddSeconds(12)
while ((Get-Date) -lt $deadline -and -not $win) {
    Start-Sleep -Milliseconds 300
    $win = Tops | Where-Object { $known -notcontains $_.Current.NativeWindowHandle -and $_.Current.NativeWindowHandle -ne 0 -and $_.Current.Name -match 'open|app' } | Select-Object -First 1
}
if (-not (Test-Path -LiteralPath $File)) { "file not found: $File"; return }
if (-not $win) {
    'picker window not found. New windows that did appear:'
    Tops | Where-Object { $known -notcontains $_.Current.NativeWindowHandle } | ForEach-Object { "   '$($_.Current.Name)' class=$($_.Current.ClassName)" }
    return
}
Start-Sleep -Milliseconds 1800
"window: '$($win.Current.Name)' class=$($win.Current.ClassName)"
$names = foreach ($el in $win.FindAll($T::Descendants, $C::TrueCondition)) {
    $c = $el.Current
    if ($c.Name) { '{0,-9} {1}' -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name }
}
try { ($win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close() } catch { 'could not close the picker window; press Esc on it' }
$names | Select-Object -Unique | Select-Object -First $Max
'glance listed: ' + [bool]($names -match 'Glance')
