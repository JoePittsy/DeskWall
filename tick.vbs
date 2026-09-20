' Launches render.ps1 -Apply with no console window. Task Scheduler runs this every minute.
' (powershell -WindowStyle Hidden still flashes a console; wscript with window style 0 does not.)
Dim sh, dir
Set sh = CreateObject("WScript.Shell")
dir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\") - 1)
sh.Run "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & dir & "\render.ps1"" -Apply", 0, False
