# AI and the assistant

Set up AI, ask for help and connect an external assistant securely.

## Enable only what you want

AI is off by default. An administrator needs an account with an AI provider, or a running local model service, before enabling it. A provider is the service that runs the model and receives your AI requests.

1. Open Configuration → AI and enable AI.
2. Choose your provider. Enter its API key if required. For a custom service, enter its base URL and check that it supports the OpenAI-compatible API, the request format Arrk uses. A local service must be reachable from Arrk, not just from your browser.
3. Enter a Medium model supported by your provider. The assistant always uses Medium; Low and High are optional for background and explicit work.
4. Review the individual feature switches. Enable the assistant for chat; review automatic features before allowing background usage.
5. Open AI Usage at `/ai` and select Test AI. Read the result before trying the assistant.

Test AI makes a real request with your Medium model and saved settings. A successful test does not guarantee every model or feature works; try your intended task too. If it fails, check the provider, exact model name, endpoint, key and account limits.

For manual setup, the main keys are `ai.enabled`, `ai.provider`, `ai.apiKey`, `ai.baseUrl` and `ai.models.medium`.

## Which model the assistant uses

The assistant always uses the Medium model. There is no per-question tier selector.

- Medium answers assistant questions.
- Low is used for background ranking, matching and summaries, and as a rate-limit fallback.
- High is optional for explicitly demanding requests and falls back to Medium when blank.

Configure them with `ai.models.low`, `ai.models.medium` and `ai.models.high`. The Medium model is required when AI is enabled; blank Low or High settings use Medium.

## Ask for facts or instructions

Open the assistant from the app bar. Ask a specific question, such as "How do I upload episodes?", "What anime do I have?" or "Why is this download paused?". The assistant can search these guides for instructions and check available Arrk services for current information.

Follow-up questions can refer to recent results. Closing the popup clears the conversation. A persona changes the assistant's tone, not your permissions or the information available to it.

These guides are also readable without AI. Your administrator may have different features enabled from those described here.

## Search before acting

Give the title and year, artist or season when names could be confused. Requests, watchlists and playback depend on the services available to your account. Adding a bookmark does not download a title, and an approved request does not mean it is ready to play.

Review any proposed action and its result. Deleting media and changing quality require an administrator and an explicit confirmation in Arrk. The assistant cannot bypass request approval or your account's limits.

## Ranking, matching and privacy

AI can help choose between suitable music downloads or possible matches for uploaded videos. Normal matching and file checks still apply. If AI is unavailable or uncertain, Arrk keeps its usual non-AI choice. You still review upload matches before import.

For release ranking or import matching, `shadow` records what AI would choose without changing the result. `ambiguous` uses AI only for close choices; `always` considers it for eligible choices; `off` disables it. These settings are under `ai.features.releaseRanking` and `ai.features.importMatching`.

AI providers may charge for tokens, the pieces of text sent and received. Automatic features can incur costs without a chat message. Review calls, tokens and limits on AI Usage. Cost estimates require prices configured under `ai.pricing`; check your provider's billing for actual charges.

Prompt and response storage is off by default with `ai.privacy.storePromptText: false`; usage counts are still recorded. Turning local prompt storage off does not stop the provider receiving data needed for AI requests. Avoid sending passwords, keys or unnecessary personal details. Check the provider's own privacy policy.

## Connect an external assistant with MCP

MCP, the Model Context Protocol, lets a compatible assistant app ask Arrk for information and permitted actions. The external app supplies its own AI service. Arrk's MCP connection does not require `ai.enabled` or an Arrk AI provider.

An administrator needs an approved, active Arrk account for the connection and an HTTPS address for Arrk. Use a separate non-administrator account unless administrative access is necessary. Open Users at `/users`, choose Edit user for that account and note the User ID shown beneath its username. MCP setup uses this number, not the username.

1. Choose a client app that supports a remote MCP server using Streamable HTTP and a manually configured Authorization header.
2. Generate a unique random secret of at least 32 characters. This is the connection's token: anyone holding it can act with the assigned account's allowed access. Do not reuse an AI provider key or an account password.
3. In Configuration → AI, set `ai.mcp.enabled` to true. Add an entry to `ai.mcp.clients` with `name`, the account's `userId`, the secret as `token`, and `allowWrites: false`.
4. In the client app, add Arrk's public HTTPS address followed by `/mcp`. Select Streamable HTTP if a transport choice is offered.
5. Configure the header name `Authorization` and value `Bearer <token>`, replacing `<token>` with your secret. Keep it in the client's secure credential settings, not in the server URL.
6. Connect and try a read-only task, such as searching the product guides. Verify the result before allowing changes.

Read-only is the default. Set that client's `allowWrites` to true only if you want actions such as requests or watchlist changes. The account's normal permissions still apply. Deletion and quality changes are not available through MCP; use Arrk for those.

Use a different token for each client. Replace or remove its configured token to revoke access. Disabled, deleted or pending accounts cannot connect. Signing into Arrk in a browser does not authenticate an MCP client, and clients that require automatic OAuth sign-in instead of a bearer header are not supported by this setup.

If connection fails, check that MCP is enabled, HTTPS reaches Arrk, the account is active and approved, and the client sends the exact token in the header. An unsupported client authentication method will not be fixed by entering the token as a URL parameter. Do not send tokens in screenshots or support messages.

## When the assistant cannot help

AI can be unavailable because it is disabled, a provider failed or a usage limit was reached. Administrators can inspect AI Usage and the relevant error. A provider rate limit may trigger a retry using a different configured Low model; it is not a guarantee that the question will complete.

Normal browsing, playback, requests and these guides remain available without a model. Features such as ranking and upload matching keep their non-AI fallback. The assistant itself needs a working provider to answer chat questions.
