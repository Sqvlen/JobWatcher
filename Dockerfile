# JobWatcher: a background .NET worker (no HTTP server), so the runtime image is enough to run it.
#
# Build:  docker build -t jobwatcher .
# Run:    docker run -d --name jobwatcher --restart unless-stopped \
#           -e JobWatcher__Telegram__BotToken=... -e JobWatcher__Telegram__ChatId=... \
#           -v jobwatcher-data:/app/data jobwatcher
#
# No secret is baked into the image: the bot token and the other keys are read from environment variables
# at start (see README.md). The only state, the database of already-sent vacancies, lives in the /app/data volume.

# ---- build ----
# The build stage always runs on the machine that builds the image (--platform=$BUILDPLATFORM), even when the image is
# built for another CPU (docker buildx build --platform linux/amd64 on an Apple Silicon Mac): it needs no emulation
# and no second SDK download. That is safe because the published output is portable (UseAppHost=false, no runtime
# identifier); the native SQLite libraries of every platform sit in runtimes/, so the final stage works on any CPU.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restore is its own layer and sees only the project file, so it is reused until a package reference changes.
COPY src/JobWatcher/JobWatcher.csproj src/JobWatcher/
RUN dotnet restore src/JobWatcher/JobWatcher.csproj

COPY src/JobWatcher/ src/JobWatcher/
RUN dotnet publish src/JobWatcher/JobWatcher.csproj \
    --configuration Release \
    --no-restore \
    -p:UseAppHost=false \
    --output /app/publish

# ---- run ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# The folder for seen.db. It is created and handed to the unprivileged user before VOLUME, so that a new
# named volume starts out writable by that user (Docker copies the folder's owner into an empty volume).
RUN mkdir -p /app/data && chown "$APP_UID" /app/data

ENV JobWatcher__DataDir=/app/data
VOLUME /app/data

# The base image ships the unprivileged user "app" and its id in APP_UID. The service never runs as root.
USER $APP_UID

ENTRYPOINT ["dotnet", "JobWatcher.dll"]
