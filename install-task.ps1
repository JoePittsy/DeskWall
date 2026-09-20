# Registers (or replaces) the per-minute tick as a Task Scheduler job for the current user.
# Runs only while logged on (needed: SystemParametersInfo must hit the interactive desktop).
# No admin required. Remove with:  Unregister-ScheduledTask -TaskName 'DeskWall Tick' -Confirm:$false
param([switch]$Uninstall)
$name = 'DeskWall Tick'
if ($Uninstall) { Unregister-ScheduledTask -TaskName $name -Confirm:$false -EA SilentlyContinue; "removed $name"; return }

$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "`"$PSScriptRoot\tick.vbs`"" -WorkingDirectory $PSScriptRoot
$nextMinute = (Get-Date).AddMinutes(1); $nextMinute = $nextMinute.AddSeconds(-$nextMinute.Second).AddMilliseconds(-$nextMinute.Millisecond)
$every = New-ScheduledTaskTrigger -Once -At $nextMinute -RepetitionInterval (New-TimeSpan -Minutes 1)
$logon = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$logon.Repetition = $every.Repetition
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Hidden
Register-ScheduledTask -TaskName $name -Action $action -Trigger $every, $logon -Settings $settings -Force | Out-Null
$t = Get-ScheduledTask -TaskName $name
"registered '$name' state=$($t.State); first run $($nextMinute.ToString('HH:mm:ss')), then every minute, and on logon"
