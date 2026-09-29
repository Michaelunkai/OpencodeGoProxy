# OpenCode Go Proxy

Smart proxy for OpenCode Go API with:
- Multi-key rotation with real-time usage tracking
- Weighted scoring across rolling/weekly/monthly windows
- Policy failover (training-data, Global regions)
- Zen fallback when all keys exhausted
- System tray icon with status display

## Usage
1. Add keys to F:\backup\windowsapps\credentials\opencodego\api.txt
2. Run uild.ps1 to compile
3. Run start_proxy.ps1 to start

## Configuration
Edit config.json to customize ports, upstreams, and retry behavior.
