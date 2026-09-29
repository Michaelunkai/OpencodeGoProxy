Option Explicit
Dim fso, sh, root, exe
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.BuildPath(root, "SystemTray.exe")

If Not fso.FileExists(exe) Then WScript.Quit 2

Dim wmi, procs, running
running = False
Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Set procs = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='SystemTray.exe'")
If procs.Count > 0 Then running = True
If running Then WScript.Quit 0

sh.Run Chr(34) & exe & Chr(34), 0, False
WScript.Quit 0
