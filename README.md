# OpenCode Go Proxy

![Hero Banner](images/hero-banner.png)

Smart API key rotation proxy for OpenCode Go/Zen models with weighted scoring, immediate Zen fallback, and a single system tray icon for full management.

## Features

![Key Rotation](images/feature-rotation.png)

### Smart Key Rotation
Weighted scoring prioritizes long-lived limits to maximize usage across all keys:
- **Monthly limits (50%)** — hardest to recover, highest priority
- **Weekly limits (35%)** — second priority
- **Rolling 5-hour limits (15%)** — resets fastest, lowest priority

The proxy always picks the key with the most remaining headroom across every window.

![Zen Fallback](images/feature-fallback.png)

### Immediate Zen Fallback
When the Go upstream fails, the proxy retries on Zen instantly — no wasted retries on other keys:
- **Insufficient funds (402)** → Zen retry
- **Global regions policy (400)** → Zen retry
- **Training consent required (400)** → Zen retry

![Tray Icon](images/feature-tray.png)

### Single System Tray Icon
One binary, one icon, full management:
- **Show Status** — keys, model, health at a glance
- **Reorder Keys by Quota** — probe usage and sort best-first
- **Open Config / Open Keys** — quick access to settings
- **Auto health check** — monitors proxy every 2 minutes
- **Key file watcher** — detects changes automatically

## Architecture

![Architecture](images/architecture.png)

## Quick Start

### 1. Add your API keys

Create `api.txt` with one key per line:

```
oc_sk_your_first_key_here
oc_sk_your_second_key_here
```

### 2. Run the proxy

```bash
OpencodeGoProxy.exe -Mode Serve -ConfigPath config.json
```

Or use the included `start-proxy.cmd` supervisor for auto-restart.

### 3. Configure your client

Point your OpenCode client to `http://127.0.0.1:4001/v1` with the local proxy bearer key.

## Configuration

The proxy reads `config.json` on startup:

```json
{
  "listen_prefix": "http://127.0.0.1:4001/",
  "upstream_base_url": "https://opencode.ai/zen/go/v1",
  "zen_upstream_base_url": "https://opencode.ai/zen/v1",
  "credential_source": "path/to/api.txt",
  "local_api_key": "your-local-bearer-key",
  "public_model": "opencode-go",
  "upstream_model": "kimi-k3"
}
```

## Supported Models

30+ models available through the Go and Zen upstreams:

| Model | Wire | Notes |
|-------|------|-------|
| deepseek-v4-flash | chat | Requires Global regions |
| deepseek-v4-pro | chat | |
| mimo-v2.5 | chat | |
| mimo-v2.5-pro | chat | |
| kimi-k3 | chat | Default upstream model |
| gpt-5.6-luna | responses | |
| gpt-6-luna | responses | |
| grok-4.6 | responses | |
| muse-spark-1.3-contributor | responses | Requires training consent |
| minimax-m3 | messages | Anthropic wire |
| qwen3.8-max | messages | Anthropic wire |

## How Key Rotation Works

1. **Background polling** — Every 20 seconds, the proxy queries `/usage` for each key
2. **Weighted scoring** — Monthly (50%) + Weekly (35%) + Rolling (15%)
3. **Automatic selection** — Highest-scored key tried first
4. **Instant failover** — On error, next key tried immediately
5. **Auto-reload** — New keys added to `api.txt` are picked up on the next request

## Building from Source

Requires .NET Framework 4.0+:

```powershell
.\build.ps1 -OutputPath OpencodeGoProxy.exe
```

## License

MIT
