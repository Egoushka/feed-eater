# --platform=$BUILDPLATFORM keeps the SDK native; TARGETARCH picks the output.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

COPY Directory.Build.props profile.example.json ./
COPY src/FeedEater/*.csproj src/FeedEater/
RUN dotnet restore src/FeedEater/FeedEater.csproj -a "${TARGETARCH}"

COPY src/ src/
# watch.example.json and watch-map.json ship beside the app (see FeedEater.csproj).
COPY config/ config/
# Migrations are embedded into FeedEater from here.
COPY db/ db/
RUN dotnet publish src/FeedEater/FeedEater.csproj -c Release -a "${TARGETARCH}" -p:Version="${VERSION}" -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
# curl for the healthcheck; tzdata for FeedEater__TimeZone names other than UTC.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/*
USER app
COPY --from=build --chown=app:app /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
# /healthz answers 503 while Postgres is unreachable; the start period covers the migrations on a first start.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "FeedEater.dll"]
