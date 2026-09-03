FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["src/Industrial.Diagnostics.Worker/Industrial.Diagnostics.Worker.csproj", "src/Industrial.Diagnostics.Worker/"]
COPY ["src/Industrial.Shared/Industrial.Shared.csproj", "src/Industrial.Shared/"]
RUN dotnet restore "src/Industrial.Diagnostics.Worker/Industrial.Diagnostics.Worker.csproj"

COPY . .
RUN dotnet publish "src/Industrial.Diagnostics.Worker/Industrial.Diagnostics.Worker.csproj" -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out .

ENTRYPOINT ["dotnet", "Industrial.Diagnostics.Worker.dll"]
