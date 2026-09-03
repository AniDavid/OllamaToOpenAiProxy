# OllamaToLmStudioProxy

[GitHub](https://github.com/AniDavid/OllamaToLmStudioProxy) · [Releases](https://github.com/AniDavid/OllamaToLmStudioProxy/releases)

### Use your existing LM Studio models with Visual Studio — without installing Ollama

**OllamaToLmStudioProxy** is a lightweight .NET proxy that translates **Ollama API requests into LM Studio API requests**.

It was created to solve a simple problem:

> **I wanted to use local LLMs in Visual Studio, but I already had my models downloaded and configured in LM Studio. I didn't want to install Ollama and maintain a second model environment.**

Instead of downloading the models again or maintaining both Ollama and LM Studio, this proxy allows Visual Studio's Ollama integration to communicate with LM Studio.

```text
┌─────────────────────┐
│    Visual Studio    │
│                     │
│   Ollama Provider   │
└──────────┬──────────┘
           │
           │ Ollama API
           ▼
┌────────────────────────────┐
│   OllamaToLmStudioProxy    │
│                            │
│   Ollama API → LM Studio   │
└─────────────┬──────────────┘
              │
              │ LM Studio API
              ▼
┌─────────────────────┐
│      LM Studio     │
│                     │
│  Your existing     │
│      models         │
└─────────────────────┘
```

## Why use this?

If you already use **LM Studio**, you may already have several gigabytes of LLMs downloaded and configured.

Installing Ollama just to satisfy an application that expects an Ollama-compatible API can mean:

* Installing another LLM runtime
* Downloading models again
* Maintaining two model environments
* Using additional disk space
* Managing models in two different applications

This proxy provides an alternative:

```text
Without the proxy:

Visual Studio
      │
      ▼
   Ollama
      │
      ▼
Download / maintain models separately


With OllamaToLmStudioProxy:

Visual Studio
      │
      ▼
OllamaToLmStudioProxy
      │
      ▼
  LM Studio
      │
      ▼
Existing models
```

**Ollama is not required.**

The proxy does not run an LLM itself. LM Studio remains responsible for loading and running the model.

---

## Main Use Case: Visual Studio + LM Studio

The original purpose of this project is to allow **Visual Studio** to use LM Studio through its Ollama-compatible local model integration.

Visual Studio communicates with the proxy using the Ollama API:

```text
Visual Studio
      │
      │ http://localhost:11434
      ▼
OllamaToLmStudioProxy
      │
      │ http://localhost:1234
      ▼
LM Studio
```

From Visual Studio's perspective, it is communicating with an Ollama-compatible server.

From LM Studio's perspective, it is receiving requests through its local API.

No Ollama installation is required.

---

## Features

* **Ollama API compatibility** — `/api/tags`, `/api/show`, `/api/chat`, `/api/generate`, `/api/embeddings`
* **OpenAI API compatibility** — `/v1/chat/completions`, `/v1/models`, `/v1/embeddings`
* **Model management** — Model discovery, name matching, `:latest` suffix handling
* **Streaming** — NDJSON and SSE support
* **Local-only** — No auth, no cloud, runs on your machine
* **.NET 10, Docker support**

---

## Supported APIs

### Ollama API

The proxy implements Ollama-compatible endpoints including:

| Method | Endpoint          | Description            |
| ------ | ----------------- | ---------------------- |
| GET    | `/api/tags`       | List available models  |
| POST   | `/api/show`       | Get model information  |
| GET    | `/api/ps`         | List running models    |
| POST   | `/api/chat`       | Chat completion        |
| POST   | `/api/generate`   | Text generation        |
| POST   | `/api/embeddings` | Generate embeddings    |
| POST   | `/api/pull`       | Compatibility endpoint |

The exact behavior of individual endpoints depends on the corresponding functionality available from LM Studio.

### OpenAI-compatible API

The proxy also exposes OpenAI-compatible endpoints:

| Method | Endpoint               | Description           |
| ------ | ---------------------- | --------------------- |
| GET    | `/v1/models`           | List models           |
| GET    | `/v1/models/{id}`      | Get model information |
| POST   | `/v1/chat/completions` | Chat completion       |
| POST   | `/v1/completions`      | Text completion       |
| POST   | `/v1/embeddings`       | Generate embeddings   |

This makes the proxy useful for applications that support either Ollama or OpenAI-compatible local APIs.

---

## Quick Start

### Requirements

* Windows, Linux, or another platform supported by .NET 10
* [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)
* [LM Studio](https://lmstudio.ai/)
* At least one model downloaded in LM Studio

**Ollama is NOT required.**

---

### 1. Start LM Studio

Open LM Studio and load the model you want to use.

Start the LM Studio local server.

The default LM Studio API endpoint is:

```text
http://localhost:1234
```

If you are using a different port, change the proxy configuration accordingly.

---

### 2. Start OllamaToLmStudioProxy

Download the latest release from the [GitHub Releases page](https://github.com/AniDavid/OllamaToLmStudioProxy/releases).

Run:

```text
OllamaToLmStudioProxy.exe
```

The proxy listens on:

```text
http://localhost:11434
```

This is the default port normally used by Ollama.

---

### 3. Configure Visual Studio

Configure Visual Studio to use its Ollama local model provider.

Point it to:

```text
http://localhost:11434/v1
```

The proxy will receive the requests and forward them to LM Studio.

You can then use the models already available in LM Studio.

---

## Example

Suppose LM Studio has the following model loaded:

```text
qwen/qwen3-coder
```

Visual Studio sends an Ollama/OpenAI-compatible request to:

```text
http://localhost:11434/v1/chat/completions
```

The proxy translates the request and forwards it to LM Studio:

```text
http://localhost:1234/v1/chat/completions
```

The response is then returned to Visual Studio.

The application flow is therefore:

```text
┌──────────────┐
│ Visual Studio│
└──────┬───────┘
       │
       │ Ollama/OpenAI API
       ▼
┌──────────────────────┐
│ OllamaToLmStudioProxy│
└──────────┬───────────┘
           │
           │ LM Studio API
           ▼
┌──────────────────────┐
│      LM Studio       │
└──────────┬───────────┘
           │
           ▼
      Local LLM
```

---

## Model Matching

Different applications can use slightly different model-name formats.

For example, a client may request:

```text
qwen3-coder:latest
```

while LM Studio reports:

```text
qwen/qwen3-coder
```

The proxy includes model-name matching logic to make these differences easier to handle.

This is particularly useful when an application expects Ollama-style model names while LM Studio uses its own model identifiers.

---

## Configuration

Configuration can be controlled through `appsettings.json`.

Example:

```json
{
  "ProxySettings": {
    "LmStudioUrl": "http://localhost:1234",
    "ProxyHost": "0.0.0.0",
    "ProxyPort": 11434,
    "TimeoutSeconds": 600,
    "EnableLogging": true
  }
}
```

### Configuration options

| Setting          | Default                 | Description                        |
| ---------------- | ----------------------- | ---------------------------------- |
| `LmStudioUrl`    | `http://localhost:1234` | LM Studio API URL                  |
| `ProxyHost`      | `0.0.0.0`               | Address on which the proxy listens |
| `ProxyPort`      | `11434`                 | Proxy listening port               |
| `TimeoutSeconds` | `600`                   | HTTP request timeout               |
| `EnableLogging`  | `true`                  | Enable application logging         |

---

## Local-only Configuration

If the proxy is only required by applications running on the same computer, it is recommended to bind it to localhost:

```json
{
  "ProxySettings": {
    "ProxyHost": "127.0.0.1"
  }
}
```

The proxy will then only accept connections from the local machine.

---

## Docker

The proxy can also be run in Docker.

Build the image:

```bash
docker build -t ollama-lmstudio-proxy .
```

Run it:

```bash
docker run -d \
  -p 11434:11434 \
  --name ollama-lmstudio-proxy \
  ollama-lmstudio-proxy
```

When LM Studio is running on the host machine, Docker Desktop can normally reach it using:

```text
http://host.docker.internal:1234
```

Configure the proxy accordingly.

For example:

```json
{
  "ProxySettings": {
    "LmStudioUrl": "http://host.docker.internal:1234"
  }
}
```

---

## Building from Source

Clone the repository:

```bash
git clone https://github.com/AniDavid/OllamaToLmStudioProxy.git
```

Enter the directory:

```bash
cd OllamaToLmStudioProxy
```

Build:

```bash
dotnet build
```

Run:

```bash
dotnet run
```

The project targets **.NET 10**.

---

## Use Cases

Although the project was originally created for Visual Studio, it can also be useful whenever an application expects an Ollama-compatible API but you prefer to run your models through LM Studio.

For example:

```text
Ollama-compatible application
             │
             ▼
OllamaToLmStudioProxy
             │
             ▼
          LM Studio
```

This allows you to keep LM Studio as your model runtime while providing an Ollama-compatible interface to applications that expect it.

---

## Why not just install Ollama?

You can.

This project is not intended to replace Ollama.

The purpose is to provide an alternative for people who already use LM Studio and don't want to maintain another model runtime.

If you already have your preferred models downloaded in LM Studio, the proxy lets you reuse them instead of setting up a separate Ollama environment.

---

## Security

This application is primarily intended for local development.

The default configuration may listen on:

```text
0.0.0.0:11434
```

The proxy does **not** provide:

* Authentication
* Authorization
* Rate limiting
* TLS termination
* Production-grade network security

Do not expose the proxy directly to an untrusted network or the Internet.

If you only need local access, use:

```text
127.0.0.1
```

as the proxy host.

---

## Limitations

The proxy translates API requests; it does not attempt to reproduce the complete Ollama runtime.

LM Studio remains responsible for:

* Loading models
* Running inference
* GPU/CPU utilization
* Model configuration
* Context size
* Sampling parameters
* Model lifecycle

Some Ollama-specific functionality may therefore not have a direct equivalent in LM Studio.

---

## Troubleshooting

### Visual Studio cannot connect

Check that:

1. LM Studio is running.
2. A model is loaded in LM Studio.
3. The LM Studio server is running.
4. OllamaToLmStudioProxy is running.
5. Port `11434` is not already being used by another application.
6. Visual Studio is configured to use:

```text
http://localhost:11434/v1
```

---

### The model is not found

Check the model list exposed by the proxy:

```text
http://localhost:11434/v1/models
```

or:

```text
http://localhost:11434/api/tags
```

Compare the model identifier returned by the proxy with the model name expected by the client.

---

### LM Studio cannot be reached

Check that LM Studio's local server is running and verify the configured URL.

Default:

```text
http://localhost:1234
```

If the proxy is running inside Docker, remember that `localhost` refers to the Docker container, not the host machine.

Use:

```text
http://host.docker.internal:1234
```

when appropriate.

---

## Contributing

Contributions, bug reports, and suggestions are welcome.

If you find an API compatibility problem with a particular Ollama client, please open an issue and include:

* The client/application
* The endpoint being called
* The request, if possible
* The response/error
* Relevant proxy log output
* The LM Studio version

---

## Project Status

This project was created to solve a specific practical problem:

> **Use LM Studio's existing local models from Visual Studio without installing Ollama.**

It is intentionally lightweight and focuses on API compatibility rather than implementing another LLM runtime.

If you find it useful, consider giving the project a ⭐ on GitHub. It helps other developers discover the project.

---

## License

MIT License

Copyright (c) 2026 AniDavid
