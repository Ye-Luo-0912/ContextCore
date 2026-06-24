# ContextCore private configuration

ContextCore.Service automatically reads machine-private configuration from:

```text
%USERPROFILE%\.contextcore\.env
%USERPROFILE%\.contextcore\secrets.json
```

`secrets.json` is the preferred format. `.env` is kept as a compatibility fallback.

These files stay outside the repository and should not be committed.

Minimal `.env` example:

```dotenv
DEEPSEEK_API_KEY=replace-with-your-local-key
PINAI_OPENAI_API_KEY=replace-with-your-local-key
```

Recommended `secrets.json` example:

```json
{
  "PrivateApiKeys": {
    "DEEPSEEK_API_KEY": "replace-with-your-local-key",
    "PINAI_OPENAI_API_KEY": "replace-with-your-local-key"
  },
  "PostgresStore": {
    "Enabled": true,
    "ConnectionString": "Host=localhost;Port=55432;Database=contextcore;Username=contextcore;Password=replace-with-your-local-password",
    "SchemaName": "contextcore_smoke",
    "AutoMigrate": false,
    "CommandTimeoutSeconds": 30,
    "ProviderId": "postgres-local-smoke"
  }
}
```

Command-line arguments still take precedence over user-private configuration.
If both `.json` and `.env` exist, the JSON values win.

Sensitive data should use this user-private file instead of repository `appsettings*.json`. Repository config should keep only safe defaults, placeholders, provider names, and non-secret policy values.

Model routing is configured in three layers:

```text
ApiProviders   -> API platforms and keys
ModelProfiles  -> concrete models exposed by each API plus capabilities
Routes         -> role/task/thinking-mode rules that select a model name or model category
```

Example route-level override in `secrets.json` (arrays are overrides, so include the complete route set you want to use):

```json
{
  "PrivateApiKeys": {
    "DEEPSEEK_API_KEY": "replace-with-your-local-key",
    "PINAI_OPENAI_API_KEY": "replace-with-your-local-key"
  },
  "ModelGateway": {
    "Routes": [
      {
        "Role": "GeneralCompression",
        "TaskKind": "ExtractKeyPoints",
        "ThinkingMode": "balanced",
        "PrimaryModelCategory": "balanced",
        "FallbackModelCategory": "deep",
        "RequiredCapabilities": [ "compression" ],
        "Priority": 90,
        "EnableFallback": true,
        "FallbackOnTimeout": true,
        "FallbackOnServerError": true
      }
    ]
  }
}
```

Default model routes:

```text
fast      -> deepseek-v4-flash
balanced  -> deepseek-v4-pro
deep      -> gpt-5.4
audit     -> gpt-5.5
validator -> gpt-5.5
fallback  -> deepseek-v4-flash
```

`deepseek-v4-pro` is configured with `supportsJsonResponseFormat=false`; ContextCore still asks for JSON in the prompt and validates the returned JSON, but it does not send OpenAI's `response_format` field to that endpoint.
