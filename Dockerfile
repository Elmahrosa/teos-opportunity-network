# TEOS Opportunity Network — runner image (Railway / any Docker host).
# Builds only the Runner (the single orchestration entry point); the test project is
# never part of the image. Migrations are copied in so `--migrate` also works in-container.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Teos.OpportunityNetwork.Runner/Teos.OpportunityNetwork.Runner.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app .
COPY migrations ./migrations
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Teos.OpportunityNetwork.Runner.dll"]
