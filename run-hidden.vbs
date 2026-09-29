' OpenCode Go Proxy - invisible launcher.
' Launches the GUI-subsystem exe with no window of any kind. The proxy's
' EnsureSelfSetup registers this VBS as the ONLOGON task automatically, so a
' fresh boot brings the tray icon up with zero terminal/popup frames.
CreateObject("WScript.Shell").Run """F:\study\Windows\Applications\PowerShell\Automation\OpenCode\Projects\OpencodeGoProxy\OpencodeGoProxy_v8.exe"" -Mode Serve -ConfigPath ""F:\study\Windows\Applications\PowerShell\Automation\OpenCode\Projects\OpencodeGoProxy\config.json""", 0, False
