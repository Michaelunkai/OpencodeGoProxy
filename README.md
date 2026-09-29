# OpenCode Go Proxy

![Hero Banner](images/hero-banner.png)

![tests](https://img.shields.io/badge/tests-5%2F5%20passing-brightgreen) ![platform](https://img.shields.io/badge/platform-Windows-blue) ![runtime](https://img.shields.io/badge/runtime-%2EFX%204.0%20portable-informational) ![license](https://img.shields.io/badge/license-MIT-green)

Smart API key rotation proxy for OpenCode Go/Zen models with weighted scoring, immediate Zen fallback, and a single system tray icon for full management.

**Highlights (current hard build):**
- Wire ladder always starts on the client's own wire — no untranslated cross-wire attempts, no more `invalid_union` / empty `event:error` frames
- In-band `event: error` SSE frames and `FreeTierError` 403s are treated as upstream failures: the proxy keeps rotating keys and, worst case, answers with a valid retryable error instead of a broken stream
- Failover order = lowest **monthly → weekly → daily → rolling 5-hour** spend first, across any number of keys (a 4th key added to `api.txt` joins rotation immediately)
- System-tray rotator mark (three key slots around a routing chevron) with cached GDI icons
- `tests/verify.ps1` — 5-check live verification harness

## Quick Start (Fresh Windows)

1. Copy this folder anywhere
2. Add your API keys to `api.txt` (one per line)
3. Double-click `OpencodeGoProxy.exe`

That's it. Zero configuration needed. The proxy auto-starts at Windows logon via a scheduled task.

## Features

![Key Rotation](images/feature-rotation.png)

### Smart Key Rotation
Weighted scoring prioritizes long-lived limits:
- **Monthly (0.45)** — hardest to recover
- **Weekly (0.30)** — second priority
- **Daily / 7-day (0.15)** — mid-term window
- **Rolling 5h (0.10)** — resets fastest

Weights are normalized across the windows the upstream reports, and any
window that hits 100% immediately benches the key.


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

![Features](images/features.png)

![Tray Mark](images/tray-icon-preview.png)

## Testing

Live harness against the running proxy (health, unique model catalog, cheapest-keys
first round trip, responses wire, secret-redaction scan):

```powershell
powershell -ExecutionPolicy Bypass -File tests\verify.ps1
```

Latest run: `RESULT passes=5 failures=0`

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
