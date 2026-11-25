# OllamaToOpenAiProxy

[![CI](https://github.com/AniDavid/OllamaToOpenAiProxy/actions/workflows/ci.yml/badge.svg)](https://github.com/AniDavid/OllamaToOpenAiProxy/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)

[GitHub Repository](https://github.com/AniDavid/OllamaToOpenAiProxy) · [Releases](https://github.com/AniDavid/OllamaToOpenAiProxy/releases) · [Report Issue](https://github.com/AniDavid/OllamaToOpenAiProxy/issues)

### Bridge Ollama-compatible clients to any OpenAI-compatible API backend — without installing Ollama

**OllamaToOpenAiProxy** is a high-performance, lightweight .NET proxy that translates **Ollama API requests into OpenAI-compatible API requests**. It enables seamless integration with backends such as **LM Studio**, **FreeToken**, **vLLM**, **llama.cpp server**, **LocalAI**, **LiteLLM**, and any standard OpenAI API provider.

---

## Why Use This?

Many developer tools (like Visual Studio, Open WebUI, Continue.dev, AnythingLLM, and LangChain) have built-in support for Ollama. However, if you already manage your models in an OpenAI-compatible runtime like **LM Studio** or use an API provider like **FreeToken**, you shouldn't have to install and maintain a separate Ollama instance.

```text
Without OllamaToOpenAiProxy:

Ollama Client / Visual Studio
            │
            ▼
         Ollama ──▶ Downloads & duplicates models on disk
            │
            ▼
   Ollama Model Store


With OllamaToOpenAiProxy:

Ollama Client / Visual Studio
            │
            │ Ollama API (/api/chat, /api/tags, etc.)
            ▼
   OllamaToOpenAiProxy
            │
            │ OpenAI API (/v1/chat/completions, /v1/models, etc.)
            ▼
OpenAI-Compatible Backend (LM Studio, FreeToken, vLLM, llama.cpp, etc.)
            │
            ▼
      Existing Models
```

**Key Advantages:**
- **No Ollama installation needed**: Reuse existing models and save gigabytes of disk space.
- **Universal compatibility**: Works with any tool that expects an Ollama endpoint on port `11434`.
- **Flexible backends**: Connects to LM Studio, FreeToken, vLLM, llama.cpp server, LocalAI, or cloud OpenAI providers.
- **Ultra-fast & lightweight**: Built on .NET 10 minimal APIs with minimal overhead and streaming support.

---

## Architecture & Data Flow

```text
┌───────────────────────────────────────┐
│     Client (Ollama Integration)       │
│  Visual Studio / Open WebUI / etc.    │
└──────────────────┬────────────────────┘
                   │
                   │ Ollama Protocol (e.g. POST /api/chat, GET /api/tags)
                   ▼
┌───────────────────────────────────────┐
│          OllamaToOpenAiProxy          │
│       Listening on 0.0.0.0:11434      │
│                                       │
│  • Model name & tag normalization     │
│  • Streaming translation (NDJSON↔SSE) │
│  • Schema translation & error handler │
└──────────────────┬────────────────────┘
                   │
                   │ OpenAI Protocol (POST /v1/chat/completions, GET /v1/models)
                   ▼
┌───────────────────────────────────────┐
│      OpenAI-Compatible Backend        │
│   LM Studio / FreeToken / vLLM / etc. │
│        (e.g. localhost:1234)          │
└───────────────────────────────────────┘
```

---

## Supported APIs

### 1. Ollama Endpoints

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/` | Root health check ("Ollama is running") |
| `GET` | `/api/version` | Returns Ollama API version compatibility |
| `GET` / `POST` | `/api/tags` | Lists available models in Ollama JSON format |
| `GET` / `POST` | `/api/ps` | Lists active/running models |
| `GET` / `POST` | `/api/show` | Returns detailed model metadata (modelfile, template, params) |
| `POST` | `/api/chat` | Native Ollama chat completion (streaming NDJSON & non-streaming) |
| `POST` | `/api/generate` | Text completion (streaming NDJSON & non-streaming) |
| `POST` | `/api/embeddings` | Vector embeddings endpoint |
| `POST` | `/api/embed` | Batch vector embeddings endpoint |
| `POST` | `/api/pull` | Compatibility endpoint simulating model pull progress |

### 2. OpenAI-Compatible Endpoints

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/v1/models` | Lists models in OpenAI format (`/models` alias supported) |
| `GET` | `/v1/models/{id}` | Retrieves specific model information |
| `POST` | `/v1/chat/completions` | OpenAI chat completions (SSE streaming & JSON) |
| `POST` | `/v1/completions` | OpenAI text completions (SSE streaming & JSON) |
| `POST` | `/v1/embeddings` | OpenAI vector embeddings |

### 3. Dashboard

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/dashboard` | Web page with live status, stats and models ([details](#dashboard)) |
| `GET` | `/dashboard/api/stats` | The page's data as JSON |

---

## Quick Start

### Requirements
- [.NET 10 SDK or Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows, Linux, macOS)
- An OpenAI-compatible backend (e.g. [LM Studio](https://lmstudio.ai/), FreeToken, vLLM, llama.cpp server)

### 1. Start Your Target Backend
- **LM Studio**: Open LM Studio, load your desired model, and start the local server (default: `http://localhost:1234`).
- **FreeToken / Other**: Ensure your endpoint URL and API access are available.

### 2. Start OllamaToOpenAiProxy
On Windows, install with winget (then run `ollama-openai-proxy`):

```powershell
winget install AniDavid.OllamaToOpenAiProxy
```

Or download the release binary or run from source:

```bash
# Run executable
./OllamaToOpenAiProxy.exe
```

By default, the proxy listens on `http://0.0.0.0:11434` and forwards requests to `http://localhost:1234`.
To see what it's doing, open the [dashboard](#dashboard) at **http://localhost:11434/dashboard**.

### 3. Configure Your Application

#### Visual Studio (Local Models)
1. In Visual Studio, navigate to Copilot / Local Model settings.
2. Select **Ollama** as the local provider.
3. Set the endpoint to `http://localhost:11434`.

#### Open WebUI / Continue.dev / AnythingLLM
- Set the Ollama base URL to `http://localhost:11434`.

---

## Configuration

Configuration can be customized via `appsettings.json`, environment variables, or command-line arguments (e.g. `--ProxySettings:ProxyPort=11500`).

`appsettings.json` is read from these locations; later ones win:

1. The install folder, next to the executable (for winget: `%LOCALAPPDATA%\Microsoft\WinGet\Packages\AniDavid.OllamaToOpenAiProxy_*`)
2. `%APPDATA%\OllamaToOpenAiProxy\appsettings.json`: **use this for winget installs**, since `winget upgrade` replaces the install folder

Environment variables and command-line arguments override all of these.

### `appsettings.json` Example

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ProxySettings": {
    "OpenAiUrl": "http://localhost:1234",
    "ProxyHost": "0.0.0.0",
    "ProxyPort": 11434,
    "TimeoutSeconds": 600,
    "EnableLogging": true
  }
}
```

### Environment Variables

| Variable | Appsettings Key | Default | Description |
| :--- | :--- | :--- | :--- |
| `OPENAI_URL` | `ProxySettings:OpenAiUrl` | `http://localhost:1234` | Target OpenAI API URL (LM Studio, FreeToken, etc.) |
| `LMSTUDIO_URL` | `ProxySettings:LmStudioUrl` | `http://localhost:1234` | Alias for `OPENAI_URL` (backward compatibility) |
| `PROXY_HOST` | `ProxySettings:ProxyHost` | `0.0.0.0` | Host address the proxy binds to |
| `PROXY_PORT` | `ProxySettings:ProxyPort` | `11434` | Port the proxy listens on |
| `TIMEOUT_SECONDS`| `ProxySettings:TimeoutSeconds` | `600` | HTTP request timeout in seconds |
| `ENABLE_LOGGING` | `ProxySettings:EnableLogging` | `true` | Enables console request/response logging |
| `ENABLE_DASHBOARD` | `ProxySettings:EnableDashboard` | `true` | Serves the dashboard at `/dashboard` |
| `DASHBOARD_ALLOW_REMOTE` | `ProxySettings:DashboardAllowRemote` | `false` | Lets other machines open the dashboard (otherwise this machine only) |

These variables take priority over every `appsettings.json` and over command-line arguments. The standard .NET form (`ProxySettings__OpenAiUrl`, `ProxySettings__ProxyPort`, …) also works.

---

## Dashboard

Open **http://localhost:11434/dashboard** in a browser while the proxy runs. It refreshes every 0.5 seconds (GPU stats, backend status and the model list every second) and shows:

- **Status**: whether the backend is reachable (and how fast), and which models it has loaded in memory
- **Totals since start**: requests, errors, prompt and completion tokens, average and latest tokens/second
- **Configuration**: backend URL, port, timeout, versions
- **GPU**: load, VRAM and temperature
- **Recent requests** (last 200): endpoint, model, status, total time, time to first token, tokens in → out, tokens/second, and how much of the context window the prompt used
- **Endpoints**: requests and errors per endpoint
- **Models** (collapsed; click to open): what the backend reports, with capabilities (tools, vision, thinking, embedding) and context window size

By default only this machine can open it, at `localhost`, `127.0.0.1` or `[::1]`. Set `DASHBOARD_ALLOW_REMOTE=true` to allow other machines (and other host names, such as the PC's name), or `ENABLE_DASHBOARD=false` to turn it off.

Good to know:
- Stats live in memory and reset when the proxy restarts. No prompt or response text is stored.
- Token counts and tokens/second are measured for both the Ollama endpoints (`/api/*`) and the OpenAI endpoints (`/v1/*`, which Visual Studio uses). Tokens/second needs a streaming request.
- Errors a backend reports inside a stream (for example LM Studio's "request exceeds the available context size") are shown and counted as errors, even though the HTTP status was 200. They are also written to the console log.
- Context window size is shown when the backend reports it (LM Studio and vLLM do). Loaded models are shown for LM Studio; other backends don't report them.
- GPU stats need an NVIDIA GPU with `nvidia-smi` on the machine running the proxy; a backend on another machine can't be seen.
- The dashboard costs nothing while closed. While open, it makes two small backend calls (status and model list) and one `nvidia-smi` run per second.

---

## Model Matching & Normalization

Different clients and backends use different naming conventions for models. OllamaToOpenAiProxy handles these differences automatically:

1. **Exact match**: Direct match against model `id` or `key`.
2. **`:latest` tag stripping**: If a client requests `qwen3-coder:latest`, the proxy matches it to `qwen3-coder` or `qwen/qwen3-coder`.
3. **Substring matching**: Matches `qwen` with `qwen/qwen3-coder-30b`.
4. **Fallback handling**: Automatically falls back to the loaded model for general chat/completions requests when no exact match is found.

---

## Docker Support

Run with Docker:

```bash
# Build Docker image
docker build -t ollama-openai-proxy .

# Run container (connecting to host machine backend)
docker run -d \
  -p 11434:11434 \
  -e OPENAI_URL="http://host.docker.internal:1234" \
  --name ollama-openai-proxy \
  ollama-openai-proxy
```

---

## Building from Source

```bash
# 1. Clone repository
git clone https://github.com/AniDavid/OllamaToOpenAiProxy.git
cd OllamaToOpenAiProxy

# 2. Build solution
dotnet build OllamaToOpenAiProxy.slnx

# 3. Run unit & integration tests
dotnet test OllamaToOpenAiProxy.slnx

# 4. Run application
dotnet run --project OllamaToOpenAiProxy.csproj
```

---

## Troubleshooting

### Client cannot connect to port 11434
- Verify that a native Ollama daemon is not already running on port `11434`.
- Check if firewall rules permit local connections on port `11434`.

### Model Not Found / 400 Bad Request
- Check the model list returned by `http://localhost:11434/api/tags` or `http://localhost:11434/v1/models`.
- Verify that your backend (e.g. LM Studio) has the model loaded and the server active.

### Target Backend Connection Refused (502 Bad Gateway)
- Ensure the target server (LM Studio, FreeToken, etc.) is running at the configured `OpenAiUrl`.
- When running in Docker, ensure you use `http://host.docker.internal:1234` instead of `localhost:1234`.

---

## Security

This application is designed for local development and proxying:
- To restrict access strictly to the local machine, set `ProxyHost` to `127.0.0.1`.
- Do not expose the proxy directly to public untrusted networks without appropriate reverse-proxy authentication, rate limiting, and TLS termination.
- The dashboard answers only this machine by default and rejects other host names, so a website can't read it through DNS rebinding. It never stores prompt or response text.

---

## License

MIT License — Copyright (c) 2026 AniDavid. See [LICENSE](file:///d:/Projects/OllamaToLmStudioProxy/LICENSE) for details.
