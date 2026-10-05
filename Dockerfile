# --platform=$BUILDPLATFORM keeps the SDK native; TARGETARCH picks the output.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

COPY Directory.Build.props ./
COPY src/FeedEater/*.csproj src/FeedEater/
RUN dotnet restore src/FeedEater/FeedEater.csproj -a "${TARGETARCH}"

COPY src/ src/
# Migrations are embedded into FeedEater from here.
COPY db/ db/
RUN dotnet publish src/FeedEater/FeedEater.csproj -c Release -a "${TARGETARCH}" -p:Version="${VERSION}" -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
# curl for the compose healthcheck; tzdata for Europe/Kyiv.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/*
USER app
COPY --from=build --chown=app:app /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "FeedEater.dll"]
