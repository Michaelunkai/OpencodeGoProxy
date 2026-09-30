' OpenCode Go Proxy - invisible launcher (PORTABLE).
' Resolves the exe + config next to THIS script, so the folder works on any
' machine at any path. setup.cmd registers this VBS as the ONLOGON task;
' the exe's EnsureSelfSetup keeps it registered automatically afterwards.
Dim shell, here, exe, cfg
Set shell = CreateObject("WScript.Shell")
here = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
exe = here & "OpencodeGoProxy.exe"
cfg = here & "config.json"
shell.Run """" & exe & """ -Mode Serve -ConfigPath """ & cfg & """", 0, False
