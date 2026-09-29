Option Explicit
Dim fso, sh, root, exe, cfg, outFile, errFile, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.BuildPath(root, "OpencodeGoProxy.exe")
cfg = fso.BuildPath(root, "config.json")
outFile = fso.BuildPath(root, "proxy.stdout.log")
errFile = fso.BuildPath(root, "proxy.stderr.log")

If Not fso.FileExists(exe) Then WScript.Quit 2
If Not fso.FileExists(cfg) Then WScript.Quit 3

Dim wmi, procs, alreadyRunning
alreadyRunning = False
Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Set procs = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='OpencodeGoProxy.exe'")
If procs.Count > 0 Then alreadyRunning = True
If alreadyRunning Then WScript.Quit 0

Dim q
q = Chr(34)
cmd = "cmd /c " & q & q & exe & q & " -Mode Serve -ConfigPath " & q & cfg & q & _
      " 1> " & q & outFile & q & " 2> " & q & errFile & q & q

' Window style 0 = hidden; bWaitOnReturn False = do not block.
sh.Run cmd, 0, False
WScript.Quit 0
