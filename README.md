# OllamaToLmStudioProxy

A .NET 10 web proxy that translates between Ollama API format and LM Studio API format, enabling Ollama clients to work with locally-hosted LLMs via LM Studio.

> **Note:** This proxy is designed to run **locally** on a developer machine. It binds to `0.0.0.0:11434` by default for convenience with local tooling (Ollama CLI, VS Code, Copilot), but has no authentication, rate limiting, or production hardening. Do not expose it to untrusted networks.

## Features

- **Ollama API compatibility**: Supports `/api/tags`, `/api/show`, `/api/ps`, `/api/chat`, `/api/generate`, `/api/embeddings`
- **OpenAI-compatible endpoints**: `/v1/chat/completions`, `/v1/completions`, `/v1/models`, `/v1/embeddings`
- **Model mapping**: Automatic model name matching and conversion between LM Studio metadata → Ollama format
- **Streaming support**: Both NDJSON and text/event-streaming modes
- **CORS enabled for public access**
- **Model fuzzy matching**: Fuzzy model name matching with `:latest` suffix handling

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10)
- LM Studio running locally (or remote server)
- Ollama client

## Installation

### 1. Clone the repository

```bash
git clone https://github.com/your-username/OllamaToLmStudioProxy.git
cd OllamaToLmStudioProxy
```

### 2. Build and run

```bash
dotnet build
dotnet run
```

Or using Visual Studio/FVS.

### 3. Default configuration

The proxy reads configuration from `appsettings.json` (and `appsettings.Development.json` for development):

| Setting | Default | Description |
|---------|---------|-------------|
| `ProxySettings:LmStudioUrl` | `http://localhost:1234` | LM Studio API base URL |
| `ProxySettings:ProxyHost` | `0.0.0.0` | Proxy listen host |
| `ProxySettings:ProxyPort` | `11434` | Proxy listen port |
| `ProxySettings:TimeoutSeconds` | `600` | Request timeout in seconds |
| `ProxySettings:EnableLogging` | `true` | Enable console logging |

### 4. Environment variables (optional)

Override config via environment variables:

```bash
set ASPNETCORE_ENVIRONMENT=Production
set ProxySettings:LmStudioUrl=http://lm-studio-host:1234
set ProxySettings:ProxyPort=11434
```

Or using the `launchSettings.json` profile.

## Running

The proxy will be available at `http://<ProxyHost>:<ProxyPort>` by default (`http://0.0.0.0:11434`).

### Endpoints

#### Ollama-format endpoints

- `GET /api/tags` / `POST /api/tags` - List available models
- `GET /api/show?name=MODEL` / `POST /api/show` - Model metadata
- `GET /api/ps` / `POST /api/ps` - Running models status
- `POST /api/pull` - Fake pull progress (for compatibility)
- `POST /api/chat` - Chat completions (streaming supported)
- `POST /api/generate` - Text generation (streaming supported)
- `POST /api/embeddings` / `POST /api/embed` - Text embeddings

#### OpenAI-compatible endpoints

- `POST /v1/chat/completions` / `POST /chat/completions`
- `POST /v1/completions` / `POST /completions`
- `GET /v1/models` / `GET /models`
- `GET /v1/models/{id}` - Get specific model
- `POST /v1/embeddings`

## Docker Deployment

### Build and run with Docker

```bash
# Build the image
docker build -t ollama-lmstudio-proxy .

# Run the proxy (default port 11434)
docker run -d -p 11434:11434 --name ollama-lmstudio-proxy ollama-lmstudio-proxy
```

Or using docker compose:

```yaml
version: '3.8'
services:
  ollama-lmstudio-proxy:
    image: ollama-lmstudio-proxy
    ports:
      - "11434:11434"
    environment:
      - ProxySettings__LmStudioUrl=http://host.docker.internal:1234
      - ProxySettings__ProxyHost=0.0.0.0
      - ProxySettings__ProxyPort=11434
```

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/foo`
3. Commit your changes: `git commit -m "Add foo feature"`
4. Push to branch: `git push origin feature/foo`
5. Submit a Pull Request

#### Development notes

- Follow the existing code style
- Add/Update unit tests for new functionality
- Ensure all endpoints are documented in README
- Run `dotnet build` and verify no warnings

### License

This project is licensed under the **MIT License** - see the [LICENSE](LICENSE) file for details.