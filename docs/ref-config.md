# Reference: Configuration, Docker, Kubernetes & CI/CD

> **Type:** Reference (not a phase)
> **Project:** All projects

---

## Full appsettings.json Template

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },

  "OAuth": {
    "Authority": "https://your-auth-provider.com",
    "Audience": "diva-api",
    "Issuer": "https://your-auth-provider.com",
    "JwksUri": "https://your-auth-provider.com/.well-known/jwks.json",
    "RequireHttpsMetadata": true,
    "ClaimsMapping": {
      "TenantId": "tenant_id",
      "SiteIds": "site_ids",
      "UserId": "sub",
      "Role": "role",
      "TeamApiKey": "litellm_team_key"
    }
  },

  "Database": {
    "Provider": "SQLite",
    "SQLite": {
      "ConnectionString": "Data Source=diva.db"
    },
    "SqlServer": {
      "ConnectionString": "Server=localhost;Database=Diva;Trusted_Connection=True;",
      "UseRls": false,
      "UseConnectionPerTenant": false
    }
  },

  "LLM": {
    "UseLiteLLM": false,
    "DirectProvider": {
      "Provider": "Anthropic",
      "ApiKey": "${ANTHROPIC_API_KEY}",
      "Model": "claude-sonnet-4-20250514"
    },
    "LiteLLM": {
      "BaseUrl": "http://litellm:4000",
      "MasterKey": "${LITELLM_MASTER_KEY}",
      "DefaultModel": "claude-sonnet"
    }
  },

  "Agent": {
    "MaxIterations": 10,
    "DefaultTimeout": "00:05:00",
    "EnableParallelDispatch": true,
    "RuleLearning": {
      "ApprovalMode": "RequireAdmin",
      "ConfidenceThreshold": 0.8
    }
  },

  "Seq": {
    "ServerUrl": "http://seq:5341"
  },

  "OTel": {
    "Endpoint": "http://otel-collector:4317"
  },

  "Temporal": {
    "Address": "temporal:7233",
    "Namespace": "default"
  },

  "Kafka": {
    "BootstrapServers": "kafka:9092",
    "GroupId": "diva-agent-consumer",
    "Topics": ["booking.confirmed", "order.completed", "noshow.detected"]
  },

  "AdminPortal": {
    "CorsOrigin": "http://localhost:3000"
  },

  "Credentials": {
    "MasterKey": "${CREDENTIALS_MASTER_KEY}"
  },

  "AllowedHosts": "*"
}
```

**`Credentials` section:**

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `MasterKey` | `string` | `""` | Base64-encoded 32-byte AES-256 key for encrypting MCP credential secrets. **Required for production.** If empty, an ephemeral random key is generated (dev only — encrypted values lost on restart). Generate with: `[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes(32))` |
```

### Environment-Specific Overrides

**Development (SQLite — Default)**
```json
{
  "Database": {
    "Provider": "SQLite",
    "SQLite": { "ConnectionString": "Data Source=diva.db" }
  }
}
```

**Production (SQL Server)**
```json
{
  "Database": {
    "Provider": "SqlServer",
    "SqlServer": {
      "ConnectionString": "Server=prod-sql.database.windows.net;Database=Diva;...",
      "UseRls": false
    }
  },
  "ConnectionStrings": {
    "SessionTrace": "Server=prod-sql.database.windows.net;Database=DivaTrace;..."
  }
}
```

> **Notes**
> - Native SQL Server RLS is **not implemented**. `UseRls` is a no-op placeholder — tenant
>   isolation uses EF global query filters for both providers. Leave it `false`.
> - The session-trace store **must be a separate database** (`DivaTrace`). `SessionTraceDbContext`
>   uses `EnsureCreated` (not migrations), which only provisions tables when the database is empty;
>   pointing it at the main `Diva` DB would leave trace tables uncreated. If `ConnectionStrings:SessionTrace`
>   is omitted in SQL Server mode, the host derives a sibling `<Database>Trace` catalog automatically.
> - SQL Server migrations live in the **`Diva.Infrastructure.SqlServer`** assembly (a squashed
>   `InitialCreate`); SQLite migrations remain in `Diva.Infrastructure`. Both are applied automatically
>   on host startup via `MigrateAsync()`.

**Multi-Tenant Dedicated Databases**
```json
{
  "Database": {
    "Provider": "SqlServer",
    "SqlServer": {
      "ConnectionString": "Server=prod-sql;Database=Diva;...",
      "UseConnectionPerTenant": true
    }
  }
}
```

---

## Dockerfile (API Host)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY *.sln .
COPY src/Diva.Core/*.csproj src/Diva.Core/
COPY src/Diva.Agents/*.csproj src/Diva.Agents/
COPY src/Diva.Tools/*.csproj src/Diva.Tools/
COPY src/Diva.Infrastructure/*.csproj src/Diva.Infrastructure/
COPY src/Diva.TenantAdmin/*.csproj src/Diva.TenantAdmin/
COPY src/Diva.Host/*.csproj src/Diva.Host/

# Restore
RUN dotnet restore

# Copy source and build
COPY src/ src/
WORKDIR /src/src/Diva.Host
RUN dotnet publish -c Release -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

EXPOSE 8080
ENTRYPOINT ["dotnet", "Diva.Host.dll"]
```

---

## Docker Compose — Default (SQLite, minimal)

```yaml
# docker-compose.yml
version: '3.8'

services:
  diva-host:
    build:
      context: .
      dockerfile: Dockerfile
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Database__Provider=SQLite
      - Database__SQLite__ConnectionString=Data Source=/app/data/diva.db
      - LLM__UseLiteLLM=false
      - LLM__DirectProvider__Provider=Anthropic
      - LLM__DirectProvider__ApiKey=${ANTHROPIC_API_KEY}
    volumes:
      - diva-data:/app/data    # Persist SQLite database
    networks:
      - diva-network
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 2G

  admin-portal:
    build:
      context: ./admin-portal
      dockerfile: Dockerfile
    ports:
      - "3000:3000"
    environment:
      - VITE_API_URL=http://diva-host:8080
    depends_on:
      - diva-host
    networks:
      - diva-network

networks:
  diva-network:
    driver: bridge

volumes:
  diva-data:
```

---

## Docker Compose — Enterprise (SQL Server + LiteLLM + Seq + Temporal)

```yaml
# docker-compose.enterprise.yml
version: '3.8'

services:
  diva-host:
    build:
      context: .
      dockerfile: Dockerfile
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Database__Provider=SqlServer
      - Database__SqlServer__ConnectionString=${DB_CONNECTION_STRING}
      - Database__SqlServer__UseRls=true
      - LLM__UseLiteLLM=true
      - LLM__LiteLLM__BaseUrl=http://litellm:4000
      - LLM__LiteLLM__MasterKey=${LITELLM_MASTER_KEY}
      - Seq__ServerUrl=http://seq:5341
      - OTel__Endpoint=http://otel-collector:4317
    depends_on:
      - litellm
      - sqlserver
      - seq
    networks:
      - diva-network
    deploy:
      replicas: 2
      resources:
        limits:
          cpus: '2'
          memory: 4G

  admin-portal:
    build:
      context: ./admin-portal
      dockerfile: Dockerfile
    ports:
      - "3000:3000"
    environment:
      - VITE_API_URL=http://diva-host:8080
    depends_on:
      - diva-host
    networks:
      - diva-network

  # LiteLLM Proxy — multi-provider routing + cost tracking
  litellm:
    image: ghcr.io/berriai/litellm:main-latest
    ports:
      - "4000:4000"
    environment:
      - LITELLM_MASTER_KEY=${LITELLM_MASTER_KEY}
      - DATABASE_URL=postgresql://postgres:postgres@litellm-db:5432/litellm
      - ANTHROPIC_API_KEY=${ANTHROPIC_API_KEY}
      - OPENAI_API_KEY=${OPENAI_API_KEY}
    volumes:
      - ./litellm_config.yaml:/app/config.yaml
    command: ["--config", "/app/config.yaml", "--port", "4000"]
    depends_on:
      - litellm-db
    networks:
      - diva-network

  litellm-db:
    image: postgres:16
    environment:
      - POSTGRES_USER=postgres
      - POSTGRES_PASSWORD=postgres
      - POSTGRES_DB=litellm
    volumes:
      - litellm-db-data:/var/lib/postgresql/data
    networks:
      - diva-network

  # SQL Server (enterprise)
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      - ACCEPT_EULA=Y
      - SA_PASSWORD=${SQL_SA_PASSWORD}
    ports:
      - "1433:1433"
    volumes:
      - sqlserver-data:/var/opt/mssql
    networks:
      - diva-network

  # Seq — structured log viewer
  seq:
    image: datalust/seq:latest
    ports:
      - "5341:5341"
      - "8081:80"      # Web UI
    environment:
      - ACCEPT_EULA=Y
    volumes:
      - seq-data:/data
    networks:
      - diva-network

  # Temporal — scheduled workflow engine
  temporal:
    image: temporalio/auto-setup:latest
    ports:
      - "7233:7233"
    environment:
      - DB=postgresql
      - DB_PORT=5432
      - POSTGRES_USER=postgres
      - POSTGRES_PWD=postgres
      - POSTGRES_SEEDS=temporal-db
    depends_on:
      - temporal-db
    networks:
      - diva-network

  temporal-db:
    image: postgres:16
    environment:
      - POSTGRES_USER=postgres
      - POSTGRES_PASSWORD=postgres
    volumes:
      - temporal-db-data:/var/lib/postgresql/data
    networks:
      - diva-network

  temporal-ui:
    image: temporalio/ui:latest
    ports:
      - "8082:8080"
    environment:
      - TEMPORAL_ADDRESS=temporal:7233
    networks:
      - diva-network

networks:
  diva-network:
    driver: bridge

volumes:
  sqlserver-data:
  litellm-db-data:
  seq-data:
  temporal-db-data:
```

### Run Enterprise Stack

```bash
# Start all enterprise services
docker compose -f docker-compose.yml -f docker-compose.enterprise.yml up -d

# SQLite-only (development)
docker compose up -d
```

---

## Kubernetes Deployment

```yaml
# k8s/deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: diva-host
  labels:
    app: diva-host
spec:
  replicas: 3
  selector:
    matchLabels:
      app: diva-host
  template:
    metadata:
      labels:
        app: diva-host
    spec:
      containers:
      - name: diva-host
        image: your-registry/diva-host:latest
        ports:
        - containerPort: 8080
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        - name: Database__SqlServer__ConnectionString
          valueFrom:
            secretKeyRef:
              name: diva-secrets
              key: db-connection-string
        - name: LLM__LiteLLM__MasterKey
          valueFrom:
            secretKeyRef:
              name: diva-secrets
              key: litellm-master-key
        resources:
          requests:
            memory: "2Gi"
            cpu: "1000m"
          limits:
            memory: "4Gi"
            cpu: "2000m"
        livenessProbe:
          httpGet:
            path: /health/live
            port: 8080
          initialDelaySeconds: 10
          periodSeconds: 30
        readinessProbe:
          httpGet:
            path: /health/ready
            port: 8080
          initialDelaySeconds: 5
          periodSeconds: 10
---
apiVersion: v1
kind: Service
metadata:
  name: diva-host
spec:
  selector:
    app: diva-host
  ports:
  - port: 80
    targetPort: 8080
  type: ClusterIP
```

```yaml
# k8s/secrets.yaml (never commit real values — use sealed-secrets or external-secrets)
apiVersion: v1
kind: Secret
metadata:
  name: diva-secrets
type: Opaque
stringData:
  db-connection-string: "Server=prod-sql;Database=Diva;..."
  litellm-master-key: "sk-litellm-master-key"
  anthropic-api-key: "sk-ant-..."
```

---

## GitHub Actions CI/CD

```yaml
# .github/workflows/ci-cd.yml
name: CI/CD Pipeline

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main]

env:
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository }}

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0.x'

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --no-restore --configuration Release

    - name: Run unit tests
      run: dotnet test --no-build --configuration Release --verbosity normal --logger trx --results-directory TestResults

    - name: Publish test results
      uses: dorny/test-reporter@v1
      if: always()
      with:
        name: .NET Tests
        path: TestResults/*.trx
        reporter: dotnet-trx

  security-scan:
    runs-on: ubuntu-latest
    needs: build-and-test
    steps:
    - uses: actions/checkout@v4

    - name: Run Trivy vulnerability scanner
      uses: aquasecurity/trivy-action@master
      with:
        scan-type: 'fs'
        scan-ref: '.'
        format: 'sarif'
        output: 'trivy-results.sarif'

    - name: Upload Trivy scan results
      uses: github/codeql-action/upload-sarif@v3
      with:
        sarif_file: 'trivy-results.sarif'

  build-and-push-image:
    runs-on: ubuntu-latest
    needs: [build-and-test, security-scan]
    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
    permissions:
      contents: read
      packages: write
    steps:
    - uses: actions/checkout@v4

    - name: Log in to Container Registry
      uses: docker/login-action@v3
      with:
        registry: ${{ env.REGISTRY }}
        username: ${{ github.actor }}
        password: ${{ secrets.GITHUB_TOKEN }}

    - name: Extract metadata
      id: meta
      uses: docker/metadata-action@v5
      with:
        images: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}
        tags: |
          type=sha,prefix=
          type=raw,value=latest

    - name: Build and push Docker image
      uses: docker/build-push-action@v5
      with:
        context: .
        push: true
        tags: ${{ steps.meta.outputs.tags }}
        labels: ${{ steps.meta.outputs.labels }}
        cache-from: type=gha
        cache-to: type=gha,mode=max

  deploy-staging:
    runs-on: ubuntu-latest
    needs: build-and-push-image
    environment: staging
    steps:
    - name: Deploy to Staging
      uses: azure/k8s-deploy@v4
      with:
        namespace: diva-staging
        manifests: |
          k8s/staging/
        images: |
          ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:${{ github.sha }}

  deploy-production:
    runs-on: ubuntu-latest
    needs: deploy-staging
    environment: production
    steps:
    - name: Deploy to Production
      uses: azure/k8s-deploy@v4
      with:
        namespace: diva-production
        manifests: |
          k8s/production/
        images: |
          ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:${{ github.sha }}
```

---

## LiteLLM Config (litellm_config.yaml)

```yaml
model_list:
  - model_name: claude-sonnet
    litellm_params:
      model: anthropic/claude-sonnet-4-20250514
      api_key: os.environ/ANTHROPIC_API_KEY
    model_info:
      max_tokens: 200000

  - model_name: gpt-4o
    litellm_params:
      model: openai/gpt-4o
      api_key: os.environ/OPENAI_API_KEY

litellm_settings:
  drop_params: true
  set_verbose: false

general_settings:
  master_key: sk-litellm-master-key
  database_url: postgresql://postgres:postgres@litellm-db:5432/litellm
  enable_team_based_access: true
  store_spend_logs: true
  enable_rate_limiting: true
```

### LiteLLM Team Setup (per tenant)

```json
{
  "teams": [
    {
      "team_id": "team_acme",
      "team_alias": "Acme Corporation",
      "metadata": { "tenant_id": 1 },
      "max_budget": 500.00,
      "budget_duration": "monthly",
      "rpm_limit": 60,
      "tpm_limit": 100000
    }
  ]
}
```

`tenant.TeamApiKey` is the team-specific key generated by LiteLLM and stored in `TenantEntity.LiteLLMTeamKey`.

---

## Prompt Templates Directory

```
prompts/
├── analytics/
│   ├── react-agent.txt         ← Base system prompt for AnalyticsAgent
│   └── text-to-sql.txt         ← SQL generation instructions
├── Reservation/
│   └── react-agent.txt
└── shared/
    ├── security-context.txt    ← Injected into all agents
    └── business-rules.txt      ← Template for rule injection
```

Prompt template format (version-aware):

```
---
version: 1
agent: Analytics
section: react-agent
---

You are the Analytics Agent for {{TenantName}}.

## Your Task
{{TaskDescription}}

## Business Rules
{{BusinessRules}}

## Available Tools
- run_query: Execute SQL queries against tenant database
- get_schema: Retrieve table schemas
- get_metrics: Get pre-computed metric summaries

Always think step-by-step before calling tools.
```

---

## Environment Variables Quick Reference

| Variable | Used By | Example |
|----------|---------|---------|
| `ANTHROPIC_API_KEY` | Direct LLM, LiteLLM | `sk-ant-...` |
| `OPENAI_API_KEY` | LiteLLM (optional) | `sk-...` |
| `LITELLM_MASTER_KEY` | LiteLLM proxy | `sk-litellm-...` |
| `DB_CONNECTION_STRING` | SQL Server | `Server=...;Database=Diva;...` |
| `SQL_SA_PASSWORD` | SQL Server docker | `StrongP@ss1` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET Core | `Development` / `Production` |
| `OAuth__Authority` | JWT validation | `https://auth.provider.com` |
| `Seq__ServerUrl` | Serilog | `http://seq:5341` |
| `OTel__Endpoint` | OpenTelemetry | `http://otel:4317` |
