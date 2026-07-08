# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies (layer-cached until csproj files change)
COPY src/Diva.Core/Diva.Core.csproj              src/Diva.Core/
COPY src/Diva.Sso/Diva.Sso.csproj                src/Diva.Sso/
COPY src/Diva.Infrastructure/Diva.Infrastructure.csproj src/Diva.Infrastructure/
COPY src/Diva.Infrastructure.SqlServer/Diva.Infrastructure.SqlServer.csproj src/Diva.Infrastructure.SqlServer/
COPY src/Diva.Tools/Diva.Tools.csproj             src/Diva.Tools/
COPY src/Diva.TenantAdmin/Diva.TenantAdmin.csproj src/Diva.TenantAdmin/
COPY src/Diva.Agents/Diva.Agents.csproj           src/Diva.Agents/
COPY src/Diva.Host/Diva.Host.csproj               src/Diva.Host/
COPY tools/DbMigrate/DbMigrate.csproj             tools/DbMigrate/
RUN dotnet restore src/Diva.Host/Diva.Host.csproj
RUN dotnet restore tools/DbMigrate/DbMigrate.csproj

# Copy source and publish
COPY src/ src/
COPY tools/ tools/
COPY prompts/ prompts/
RUN dotnet publish src/Diva.Host/Diva.Host.csproj \
    -c Release -o /app/publish \
    --no-restore
RUN dotnet publish tools/DbMigrate/DbMigrate.csproj \
    -c Release -o /app/migrate \
    --no-restore

# ── Migration stage (one-shot SQLite → SQL Server data copy) ──────────────────
# Build/run with: docker compose --profile migrate run --rm dbmigrate
# Not the final stage on purpose, so `docker build .` (no --target) still yields the API image.
# Uses the aspnet base image (not runtime) because DbMigrate transitively references the
# Microsoft.AspNetCore.App shared framework via Diva.Infrastructure.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrate
WORKDIR /migrate
COPY --from=build /app/migrate .
# Source SQLite DB + target SQL Server connection are supplied via env / args:
#   Database__SQLite__ConnectionString, Database__SqlServer__ConnectionString
ENTRYPOINT ["dotnet", "DbMigrate.dll"]

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    rm -rf /var/lib/apt/lists/*

# prompts/ must be beside the dll so PromptTemplateStore can find them
COPY --from=build /app/publish .
COPY --from=build /src/prompts ./prompts

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Diva.Host.dll"]
