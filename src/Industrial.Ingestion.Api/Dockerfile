FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["src/Industrial.Ingestion.Api/Industrial.Ingestion.Api.csproj", "src/Industrial.Ingestion.Api/"]
COPY ["src/Industrial.Shared/Industrial.Shared.csproj", "src/Industrial.Shared/"]
COPY ["src/Protos/telemetry.proto", "src/Protos/"]
RUN dotnet restore "src/Industrial.Ingestion.Api/Industrial.Ingestion.Api.csproj"
COPY . .
RUN dotnet publish "src/Industrial.Ingestion.Api/Industrial.Ingestion.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Industrial.Ingestion.Api.dll"]
