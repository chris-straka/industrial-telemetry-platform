FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY ["src/Industrial.Ingestion.Api/Industrial.Ingestion.Api.csproj", "Industrial.Ingestion.Api/"]
RUN dotnet restore "Industrial.Ingestion.Api/Industrial.Ingestion.Api.csproj"
COPY . .
WORKDIR "/app/src/Industrial.Ingestion.Api"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Industrial.Ingestion.Api.dll"]
