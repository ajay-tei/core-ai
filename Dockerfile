# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies (layer-cached until csproj files change)
COPY src/Diva.Core/Diva.Core.csproj              src/Diva.Core/
COPY src/Diva.Sso/Diva.Sso.csproj                src/Diva.Sso/
COPY src/Diva.Infrastructure/Diva.Infrastructure.csproj src/Diva.Infrastructure/
COPY src/Diva.Rag/Diva.Rag.csproj                 src/Diva.Rag/
COPY src/Diva.Tools/Diva.Tools.csproj             src/Diva.Tools/
COPY src/Diva.TenantAdmin/Diva.TenantAdmin.csproj src/Diva.TenantAdmin/
COPY src/Diva.Agents/Diva.Agents.csproj           src/Diva.Agents/
COPY src/Diva.Host/Diva.Host.csproj               src/Diva.Host/
RUN dotnet restore src/Diva.Host/Diva.Host.csproj

# Copy source and publish
COPY src/ src/
COPY prompts/ prompts/
RUN dotnet publish src/Diva.Host/Diva.Host.csproj \
    -c Release -o /app/publish \
    --no-restore

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
