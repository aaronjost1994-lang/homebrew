# WinBrew repository builder: compiles the demo packages into real Windows x64
# executables, then runs winbrew-repo to publish a SIGNED repository into
# /srv/repo (served by Caddy, see compose.yaml). Build context = repo root.

# 1) Demo executables (Windows x64 PE, statically linked, cross-compiled with MinGW-w64).
FROM debian:trixie-slim AS demo-exes
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc-mingw-w64-x86-64 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /demo
COPY deploy/server/demo/ ./
RUN set -e; cc="x86_64-w64-mingw32-gcc -O2 -s -static"; \
    for d in packages/*/*; do mkdir -p "$d/files/bin"; done; \
    $cc -DHELLO_VERSION='"1.0.0"' -o packages/hello/1.0.0/files/bin/hello.exe src/hello.c; \
    $cc -DHELLO_VERSION='"2.0.0"' -o packages/hello/2.0.0/files/bin/hello.exe src/hello.c; \
    $cc -o packages/echoargs/1.0.0/files/bin/echoargs.exe src/echoargs.c; \
    $cc -o packages/exitcode/1.0.0/files/bin/exitcode.exe src/exitcode.c; \
    $cc -o packages/toolbox/1.0.0/files/bin/toolbox.exe src/toolbox.c

# 2) The repository tool (same SDK as global.json, locked restore).
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS tool
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY tools/WinBrew.RepoTool/ tools/WinBrew.RepoTool/
RUN dotnet restore tools/WinBrew.RepoTool --locked-mode \
 && dotnet publish tools/WinBrew.RepoTool -c Release --no-restore -o /out

# 3) Runtime: re-signs the repository every WINBREW_REPO_EVERY_HOURS hours
#    (timestamp.json expires after 6 days; clients refuse expired metadata).
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS repo-builder
WORKDIR /app
COPY --from=tool /out/ ./
COPY --from=demo-exes /demo/packages/ /packages/
ENV WINBREW_REPO_PACKAGES=/packages \
    WINBREW_REPO_KEYS=/keys \
    WINBREW_REPO_OUT=/srv/repo \
    WINBREW_REPO_EVERY_HOURS=24
VOLUME ["/keys", "/srv/repo"]
ENTRYPOINT ["dotnet", "/app/winbrew-repo.dll", "build"]
