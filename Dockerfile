# syntax=docker/dockerfile:1.7
# Multi-stage build for the BidBuilder Next.js frontend (standalone output).

# ─── deps ───────────────────────────────────────────────────────────────────
FROM node:20-alpine AS deps
WORKDIR /app
RUN apk add --no-cache libc6-compat
COPY package.json package-lock.json* ./
# React 19 / Next 16 are bleeding-edge; some peer ranges still lag.
RUN npm ci --legacy-peer-deps

# ─── builder ────────────────────────────────────────────────────────────────
FROM node:20-alpine AS builder
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
ENV NEXT_TELEMETRY_DISABLED=1
# Baked into the client bundle — must be the URL the BROWSER reaches the API at.
ARG NEXT_PUBLIC_API_URL
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL
RUN npm run build

# ─── runner ─────────────────────────────────────────────────────────────────
FROM node:20-alpine AS runner
WORKDIR /app
ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1
ENV PORT=3000
ENV HOSTNAME=0.0.0.0
# 29.A.2 — pull Alpine security patches, then strip npm + corepack: the
# runtime is `node server.js` only, and npm's BUNDLED deps (tar, glob,
# minimatch, cross-spawn) are what the Trivy gate flags. Removing the tool
# removes the attack surface (and ~50 MB).
RUN apk upgrade --no-cache \
 && rm -rf /usr/local/lib/node_modules /usr/local/bin/npm /usr/local/bin/npx /usr/local/bin/corepack
RUN addgroup --system --gid 1001 nodejs && adduser --system --uid 1001 nextjs
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static    ./.next/static
COPY --from=builder --chown=nextjs:nodejs /app/public          ./public
USER nextjs
EXPOSE 3000
CMD ["node", "server.js"]
