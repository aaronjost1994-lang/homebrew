# WinBrew — Linux test container (dev/CI convenience, A2 Linux host).
#
#   docker build -t winbrew-test .                 # restore + build + all tests
#   docker build -t winbrew-test --target smoke .  # + CLI smoke (--version/config/doctor)
#   docker run --rm winbrew-test                   # re-run the test suite
#
# The image pins the same SDK as global.json. Windows-only deliverables
# (published zip, Native AOT shim, registry checks) still need Windows CI.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS test
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src

# 1) Restore layer — only project files + lock files, so it caches until deps change.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig WinBrew.sln ./
COPY src/WinBrew.Core/*.csproj src/WinBrew.Core/packages.lock.json src/WinBrew.Core/
COPY src/WinBrew.Application/*.csproj src/WinBrew.Application/packages.lock.json src/WinBrew.Application/
COPY src/WinBrew.Infrastructure/*.csproj src/WinBrew.Infrastructure/packages.lock.json src/WinBrew.Infrastructure/
COPY src/WinBrew.Windows/*.csproj src/WinBrew.Windows/packages.lock.json src/WinBrew.Windows/
COPY src/WinBrew.Cli/*.csproj src/WinBrew.Cli/packages.lock.json src/WinBrew.Cli/
COPY src/WinBrew.Shim/*.csproj src/WinBrew.Shim/packages.lock.json src/WinBrew.Shim/
COPY tests/WinBrew.TestFixtures/*.csproj tests/WinBrew.TestFixtures/packages.lock.json tests/WinBrew.TestFixtures/
COPY tests/WinBrew.LockHolderHelper/*.csproj tests/WinBrew.LockHolderHelper/packages.lock.json tests/WinBrew.LockHolderHelper/
COPY tests/WinBrew.UnitTests/*.csproj tests/WinBrew.UnitTests/packages.lock.json tests/WinBrew.UnitTests/
COPY tests/WinBrew.IntegrationTests/*.csproj tests/WinBrew.IntegrationTests/packages.lock.json tests/WinBrew.IntegrationTests/
COPY tests/WinBrew.EndToEndTests/*.csproj tests/WinBrew.EndToEndTests/packages.lock.json tests/WinBrew.EndToEndTests/
RUN dotnet restore WinBrew.sln --locked-mode

# 2) Build (warnings are errors) and test. A failing test fails `docker build`.
COPY . .
RUN dotnet build WinBrew.sln -c Release --no-restore
RUN dotnet test WinBrew.sln -c Release --no-build \
      --filter "FullyQualifiedName!~EndToEnd"

CMD ["dotnet", "test", "WinBrew.sln", "-c", "Release", "--no-build", \
     "--filter", "FullyQualifiedName!~EndToEnd"]

# Optional: run the CLI itself on Linux (framework-dependent build, unlocked
# restore — container-only, never touches the committed lock files).
FROM test AS smoke
ENV WINBREW_ROOT=/tmp/wbroot
RUN dotnet build src/WinBrew.Cli -c Release -o /cli \
      -p:RuntimeIdentifier= -p:SelfContained=false -p:PublishSingleFile=false \
      -p:UseAppHost=false -p:RestoreLockedMode=false \
 && dotnet /cli/wbrew.dll --version \
 && dotnet /cli/wbrew.dll config set download.timeoutSeconds 60 \
 && dotnet /cli/wbrew.dll config get download.timeoutSeconds \
 && dotnet /cli/wbrew.dll doctor --json \
 && ! dotnet /cli/wbrew.dll install
ENTRYPOINT ["dotnet", "/cli/wbrew.dll"]
CMD ["--help"]
