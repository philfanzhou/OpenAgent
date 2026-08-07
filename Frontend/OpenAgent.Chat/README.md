# OpenAgent.Chat

Single-page Vue 3 + TypeScript + Vite workspace for connecting directly to an OpenAgent Engine.

## Local development

```bash
pnpm install --ignore-scripts
pnpm dev
```

Enter the Engine address in the settings drawer, for example `http://localhost:5208`.
The development Engine uses PassThrough authentication and accepts `X-Tenant-Id` through the frontend tenant field.

## Production authentication

The page accepts an access token issued by the configured external identity provider. Password, Microsoft Entra ID, and enterprise OIDC login flows belong to that identity provider; Engine validates the resulting Bearer token according to its `Authentication:Mode` configuration.
