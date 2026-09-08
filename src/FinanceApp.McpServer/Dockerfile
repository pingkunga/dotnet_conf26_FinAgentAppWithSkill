FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/FinanceApp.Core/FinanceApp.Core.csproj src/FinanceApp.Core/
COPY src/FinanceApp.McpServer/FinanceApp.McpServer.csproj src/FinanceApp.McpServer/
RUN dotnet restore src/FinanceApp.McpServer/FinanceApp.McpServer.csproj

# FinanceApp.McpServer.csproj's own <Content Include="skills\**"> item picks up its 4 hosted skills
# (monthly-summary, goals-progress, emergency-fund, debt-payoff-strategies) into the publish output.
COPY src/FinanceApp.Core/ src/FinanceApp.Core/
COPY src/FinanceApp.McpServer/ src/FinanceApp.McpServer/

RUN dotnet publish src/FinanceApp.McpServer/FinanceApp.McpServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# curl is only for docker-compose's healthcheck against the already-existing GET /health endpoint
# (Program.cs, AllowAnonymous) — this image has no file-skill scripts of its own, so unlike Web's image,
# no Python is needed here.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "FinanceApp.McpServer.dll"]
