' OpenCode Go Proxy — hidden launcher (no window, no terminal pop-up)
' Launches the proxy from this script's folder, completely invisible.
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.BuildPath(root, "OpencodeGoProxy.exe")
cfg = fso.BuildPath(root, "config.json")
logDir = fso.BuildPath(root, "Logs")

If Not fso.FileExists(exe) Then WScript.Quit 2
If Not fso.FolderExists(logDir) Then fso.CreateFolder(logDir)

' Prevent duplicates: if already running, exit quietly
Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Set procs = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='OpencodeGoProxy.exe'")
If procs.Count > 0 Then WScript.Quit 0

' 0 = hidden window, False = don't wait
sh.Run Chr(34) & exe & Chr(34) & " -Mode Serve -ConfigPath " & Chr(34) & cfg & Chr(34), 0, False
WScript.Quit 0
