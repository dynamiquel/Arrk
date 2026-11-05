# syntax=docker/dockerfile:1

# Build metadata. CI passes these; local builds fall back to development values.
ARG VERSION=0.0.0-dev
ARG VCS_REF=unknown
ARG BUILD_DATE=unknown
ARG SOURCE_URL=https://github.com/dynamiquel/Stackarr

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION
WORKDIR /src

COPY src/ ./src/
COPY Directory.Build.props ./
COPY global.json ./
RUN dotnet restore src/Stackarr.App/Stackarr.App.csproj

RUN dotnet publish src/Stackarr.App/Stackarr.App.csproj -c Release -o /app/publish \
    --no-restore -p:Version="${VERSION}"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION
ARG VCS_REF
ARG BUILD_DATE
ARG SOURCE_URL
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl ffmpeg && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

LABEL org.opencontainers.image.title="Stackarr" \
      org.opencontainers.image.description="One web UI and one YAML file for a self-hosted media stack." \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.licenses="MIT"

ENV ASPNETCORE_URLS=http://+:5186
ENV ASPNETCORE_ENVIRONMENT=Production
# Anchor Stackarr's own files and default storage roots at the filesystem root so the
# container uses /config, /downloads and /media (matching the documented mounts).
ENV STACKARR_DATA_ROUTE=/
EXPOSE 5186

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:5186/health || exit 1

ENTRYPOINT ["dotnet", "Stackarr.App.dll"]
