# z-aspire-orchestrator

Service orchestration using Aspire for local development environments.

## Overview

z-aspire-orchestrator is a skill module that provides service orchestration capabilities using Aspire. It manages distributed applications locally, handling service discovery, health checks, and inter-service communication.

## Project Structure

```
z-aspire-orchestrator/
├── SKILL.md    # Main orchestration workflow
└── aspire.md   # Aspire CLI reference
```

## Key Features

- **Distributed Service Orchestration** — Manage multiple services and their dependencies
- **Middleware Integration** — Redis, PostgreSQL, MongoDB, and other middleware support
- **Health Check Management** — Wait for services to be healthy before starting dependents
- **Environment Variable Injection** — Configure services via `.withEnvironment()`
- **External Port Exposure** — Use `.withHttpEndpoint()` for frontend/backend access

## Quick Start

### Prerequisites

Install Aspire CLI:

```bash
aspire --version
```

### Initialize Orchestration Environment

```bash
# Create local-dev Aspire project if needed
aspire init --language typescript
```

### Configure Services

```typescript
// Add middleware
const redis = await builder.addRedis();
const postgres = await builder.addPostgres();

// Add JavaScript/Node.js service
const api = await builder.addJavaScriptApp('api', './services/api', { runScriptName: 'dev' })
  .withReference(redis)
  .waitFor(redis)
  .withHttpEndpoint({ env: 'PORT' });

// Inject environment variables
const frontend = builder.addJavaScriptApp('frontend', './services/frontend', { runScriptName: 'dev' })
  .withReference(api)
  .withEnvironment("API_URI", await api.getEndpoint('http'));
```

### Build & Verify

```bash
npm run aspire:build
npm run aspire:lint
```

### Start Services

```bash
aspire stop
aspire restore
aspire start
aspire describe
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `aspire start` | Start the app |
| `aspire start --isolated` | Start in isolated mode |
| `aspire stop` | Stop the app |
| `aspire wait <resource>` | Wait for resource to be healthy |
| `aspire describe` | List resources |
| `aspire logs [resource]` | View console logs |
| `aspire otel logs [resource]` | View structured logs |
| `aspire otel traces [resource]` | View distributed traces |
| `aspire docs search <query>` | Search documentation |
| `aspire doctor` | Environment diagnostics |

## Integration Examples

Search for middleware integration docs:

```bash
# Redis
aspire docs search get-started-with-the-redis-integration

# PostgreSQL
aspire docs search get-started-with-the-postgresql-integration
```

## Important Rules

- **Always start the app first** (`aspire start`) before making changes
- **To restart, just run `aspire start` again** — it automatically stops the previous instance
- **Only restart the AppHost when AppHost code changes** — use `aspire resource <name> rebuild` for .NET project resources
- **Never install the Aspire workload** — it is obsolete

## Documentation

- [SKILL.md](./SKILL.md) — Detailed orchestration workflow
- [aspire.md](./aspire.md) — Complete Aspire CLI reference
