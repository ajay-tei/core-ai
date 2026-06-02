# ── Build stage ───────────────────────────────────────────────────────────────
FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# Branding build args — passed from docker-compose via build.args
ARG VITE_APP_NAME="Diva AI"
ARG VITE_APP_SLUG="diva"
# Subpath prefix for reverse-proxy deployments (e.g. /beta/tei-ai — no trailing slash).
# Leave empty (default) for root deployment. When set, all asset and API URLs include this prefix.
ARG VITE_BASE_PATH=""

# Skip tsc (type-check is a dev/CI concern); vite uses esbuild and does not require it
RUN VITE_BASE_PATH="${VITE_BASE_PATH}" \
    VITE_APP_NAME="${VITE_APP_NAME}" \
    VITE_APP_SLUG="${VITE_APP_SLUG}" \
    npx vite build

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM nginx:alpine AS runtime

COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 6010
