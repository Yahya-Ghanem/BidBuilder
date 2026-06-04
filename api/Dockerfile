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
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
# QuestPDF (SkiaSharp) needs fontconfig + a base font to render PDF text on Linux.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 fontconfig fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app ./
# Run as the built-in non-root user shipped in the aspnet image.
USER $APP_UID
ENTRYPOINT ["dotnet", "BidBuilder.Api.dll"]
