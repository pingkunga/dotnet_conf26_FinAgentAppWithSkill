FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files (+ central package management props) first so this restore layer only invalidates
# when a project reference or package version actually changes, not on every source edit.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/FinanceApp.Core/FinanceApp.Core.csproj src/FinanceApp.Core/
COPY src/FinanceApp.AI/FinanceApp.AI.csproj src/FinanceApp.AI/
COPY src/FinanceApp.Skills/FinanceApp.Skills.csproj src/FinanceApp.Skills/
COPY src/FinanceApp.Web/FinanceApp.Web.csproj src/FinanceApp.Web/
RUN dotnet restore src/FinanceApp.Web/FinanceApp.Web.csproj

# Full source, including repo-root skills/ — FinanceApp.Web.csproj's own
# <Content Include="..\..\skills\**"> item picks this up into the publish output automatically.
COPY src/FinanceApp.Core/ src/FinanceApp.Core/
COPY src/FinanceApp.AI/ src/FinanceApp.AI/
COPY src/FinanceApp.Skills/ src/FinanceApp.Skills/
COPY src/FinanceApp.Web/ src/FinanceApp.Web/
COPY skills/ skills/

RUN dotnet publish src/FinanceApp.Web/FinanceApp.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# savings-calculator (docs/spec.md §4.3) shells out to "python3" on PATH via SubprocessScriptRunner — the
# only file-based skill in this app with a scripts/ folder, and it runs inside Web's process. McpServer's
# own image does not need this (it has no file-skill scripts of its own).
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "FinanceApp.Web.dll"]
