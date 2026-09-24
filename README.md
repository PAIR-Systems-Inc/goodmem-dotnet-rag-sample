# GoodMem RAG sample for .NET 10

A small ASP.NET Core Minimal API that follows GoodMem's Basic RAG flow:

- reuses providers configured in the GoodMem console or creates them from API keys;
- creates a space and ingests three Markdown documents;
- returns generated answers with source citations;
- stores chat turns in GoodMem and isolates retrieval by session;
- compares baseline and reranked retrieval against identical history.

Only GoodMem resource IDs are stored locally in `App_Data/goodmem-state.json`. Documents and chat turns live in GoodMem, so they survive API restarts.

## Prerequisites

- .NET 10 SDK
- a local GoodMem instance or a GoodMem Cloud instance
- the GoodMem API key attached to the instance
- an embedder and LLM, configured either through this sample or the GoodMem console

Reranking is optional for basic chat, but required for `/api/compare` and chat requests with `useReranker: true`.

> [!NOTE]
> The REST base URL does not include `/v1`. The default Docker install serves REST and the bundled console at `https://localhost:8080` and `https://localhost:8080/console/`.

## Configure

The app reads environment variables from its process. It does not automatically load `.env` files; `.env.example` is a configuration template. Export the values in the shell where you start the app.

For the default local self-signed certificate:

```bash
export GOODMEM_BASE_URL="https://localhost:8080"
export GOODMEM_API_KEY="gm_..."
export GOODMEM_VERIFY_SSL="false" # loopback development only
```

If GoodMem was installed with `--tls-disabled`, use `http://localhost:8080` and omit `GOODMEM_VERIFY_SSL`. For GoodMem Cloud or trusted TLS, use the HTTPS instance URL and leave verification enabled.

Choose one provider setup.

### Reuse console-configured providers

This is the E2E-validated setup path for both local Docker and GoodMem Cloud.

Open the bundled console, configure the providers, and export their resource IDs:

```bash
export GOODMEM_EMBEDDER_ID="..."
export GOODMEM_LLM_ID="..."
export GOODMEM_RERANKER_ID="..." # optional
```

No provider credentials are needed by this app when the corresponding IDs are supplied. To use other supported providers, configure them in the GoodMem console and supply their IDs here.

### Let the sample create providers

The sample creates OpenAI providers for embeddings and the LLM. Add Voyage if you want reranking.

```bash
export OPENAI_API_KEY="sk-..." # creates `text-embedding-3-large` and `gpt-5.1`
export VOYAGE_API_KEY="pa-..." # optional with OpenAI; enables `rerank-2.5`
```

Explicit `GOODMEM_*_ID` values take precedence over automatic creation. Existing providers recorded in `App_Data/goodmem-state.json` are reused; changing keys does not replace them. Setup can attach a missing reranker later.

Provider credentials are sent to GoodMem during registration and are never written to this repository or the local state file.

## Run

Use two terminal tabs for the demo. In the first, from the repository root, start the API using the same shell where you exported the environment variables above:

```bash
dotnet run --project src/GoodMem.Rag.Api
```

Leave it running; this tab also shows the API logs. If you change environment variables, stop the API with Ctrl+C and restart it with the updated environment.

`http://localhost:5077` is this sample's ASP.NET API, configured in `Properties/launchSettings.json`. It calls the separate GoodMem server at `GOODMEM_BASE_URL` (normally `https://localhost:8080` for the default local Docker install).

In the second tab, run the following requests. This tab does not need the provider credentials or GoodMem API key. Provision the sample once:

```bash
curl -sS -X POST http://localhost:5077/api/setup | jq
```

The setup response's `rerankerId` identifies the sample's configured reranker; `null` means none is configured. With `created: false`, these IDs come from saved local state, not a live provider-health check. You can inspect the corresponding provider in the GoodMem console.

Start with basic chat (no reranker required):

```bash
curl -sS http://localhost:5077/api/chat \
  -H 'content-type: application/json' \
  -d '{"sessionId":"demo-user","message":"What is the refund policy for annual plans?","useReranker":false}' | jq
```

If a reranker is configured, compare baseline and reranked retrieval in one request:

```bash
curl -sS http://localhost:5077/api/compare \
  -H 'content-type: application/json' \
  -d '{"sessionId":"demo-user","message":"What is the refund policy for annual plans?"}' | jq
```

A successful comparison returns `baseline.reranked: false` and `reranked.reranked: true`. The sample verifies the rerank stage in GoodMem's response; it fails rather than silently falling back if reranking fails. Without a configured reranker, `/api/compare` fails with "Reranking is unavailable."

Or select reranking for a normal chat turn (also requires a reranker):

```bash
curl -sS http://localhost:5077/api/chat \
  -H 'content-type: application/json' \
  -d '{"sessionId":"demo-user","message":"What did I ask you previously?","useReranker":true}' | jq
```

Restart the API and reuse `demo-user` to demonstrate persistence. Different session IDs cannot retrieve one another's chat memories.

Session IDs are caller-supplied retrieval filters, not authentication or authorization. Anyone who can call the API can submit another session's ID. Before deploying beyond localhost, authenticate callers and derive the session scope from their verified identity.

## Notes

- `POST /api/setup` is idempotent after the state file is written. It can attach a reranker later if one was not initially configured.
- `POST /api/compare` runs both retrievals before storing one turn, so both variants see the same history.
- The sample uses `RetrieveRawAsync` because per-space metadata filters are advanced request fields; the simpler `RetrieveAsync` convenience method does not expose them.
- Citations retain GoodMem's order and belong to the answer's result set. Baseline distance scores and reranker scores use different scales; do not compare their numeric values directly. Provider failures fail the request instead of returning a fallback as a successful reranked answer.
- `GOODMEM_VERIFY_SSL=false` is rejected for non-loopback URLs. Trust a real CA for remote deployments.
- The sample endpoints are unauthenticated and intended for local development. Before exposing the API, add authentication and authorization; provision resources out of band instead of exposing `/api/setup`.
- The package is pinned to `PairSystems.Goodmem.Client` 2.0.2 for reproducible builds.

## Development checks

```bash
dotnet restore GoodMem.Rag.Sample.slnx --locked-mode
dotnet format GoodMem.Rag.Sample.slnx --no-restore --verify-no-changes
dotnet build GoodMem.Rag.Sample.slnx --configuration Release --no-restore
dotnet test GoodMem.Rag.Sample.slnx --configuration Release --no-build
```

## License

This sample is licensed under the [MIT License](LICENSE).
