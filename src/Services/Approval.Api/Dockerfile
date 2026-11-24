FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Services/Approval.Api/Approval.Api.csproj
RUN dotnet publish src/Services/Approval.Api/Approval.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=4 CMD curl -fsS http://localhost:8080/health/ready || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "Approval.Api.dll"]
