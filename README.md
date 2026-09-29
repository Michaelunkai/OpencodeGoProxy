# OpenCode Go Proxy

![Hero Banner](images/hero-banner.png)

Smart API key rotation proxy for OpenCode Go/Zen models with weighted scoring, immediate Zen fallback, and a single system tray icon for full management.

## Quick Start (Fresh Windows)

1. Copy this folder anywhere
2. Add your API keys to `api.txt` (one per line)
3. Double-click `OpencodeGoProxy.exe`

That's it. Zero configuration needed. The proxy auto-starts at Windows logon via a scheduled task.

## Features

![Key Rotation](images/feature-rotation.png)

### Smart Key Rotation
Weighted scoring prioritizes long-lived limits:
- **Monthly (50%)** — hardest to recover
- **Weekly (35%)** — second priority
- **Rolling 5h (15%)** — resets fastest

![Zen Fallback](images/feature-fallback.png)

### Immediate Zen Fallback
When Go upstream fails, retry on Zen instantly:
- Insufficient funds → Zen
- Global regions policy → Zen
- Training consent required → Zen

![Tray Icon](images/feature-tray.png)

### Single System Tray Icon
- Show Status, Reorder Keys, Open Config/Keys
- Auto health check every 2 minutes
- Key file watcher

## Architecture

![Architecture](images/architecture.png)

## Files

| File | Purpose |
|------|---------|
| `OpencodeGoProxy.exe` | The proxy (self-contained, no install needed) |
| `config.json` | Configuration (defaults work out of the box) |
| `api.txt` | Your API keys (create this file, one per line) |
| `build.ps1` | Rebuild from source |

## Configuration

Edit `config.json` only if you need custom settings. Defaults:

```json
{
  "listen_prefix": "http://127.0.0.1:4001/",
  "upstream_base_url": "https://opencode.ai/zen/go/v1",
  "zen_upstream_base_url": "https://opencode.ai/zen/v1",
  "credential_source": "api.txt",
  "local_api_key": "7rPWb0YtL2miBuu2dep2Qv0kc7LNj-QpRhDIOOxLgqk",
  "public_model": "opencode-go",
  "upstream_model": "kimi-k3"
}
```

## Supported Models

30+ models via Go and Zen upstreams (deepseek-v4-flash, mimo-v2.5, kimi-k3, gpt-6-luna, grok-4.6, etc.)

## Building from Source

```powershell
.\build.ps1 -OutputPath OpencodeGoProxy.exe
```

Requires .NET Framework 4.0+ (built into Windows 10/11).

## License

MIT
