# syntax=docker/dockerfile:1.7
# Multi-stage build for the BidBuilder ASP.NET Core 8 API.

# ─── Stage 1: build ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Restore first (layer-cached until the csproj changes).
COPY BidBuilder.Api.csproj ./
RUN dotnet restore BidBuilder.Api.csproj
# Then the rest of the source.
COPY . ./
RUN dotnet publish BidBuilder.Api.csproj -c Release -o /app --no-restore

# ─── Stage 2: runtime ───────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# QuestPDF (SkiaSharp) needs fontconfig + a base font to render PDF text on Linux.
# curl is included so the container healthcheck can probe /healthz.
# 29.A.2 — `upgrade -y` pulls Debian security patches published after the
# aspnet base image was cut (the Trivy gate fails on fixable HIGH/CRITICAL
# CVEs in OS packages, e.g. libgnutls30/openssl).
RUN apt-get update \
 && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends libfontconfig1 fontconfig fonts-dejavu-core curl \
 && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app ./
# Run as the built-in non-root user shipped in the aspnet image.
USER $APP_UID
ENTRYPOINT ["dotnet", "BidBuilder.Api.dll"]
