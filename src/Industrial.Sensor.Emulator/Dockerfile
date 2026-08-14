FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY ["src/Industrial.Sensor.Emulator/Industrial.Sensor.Emulator.csproj", "Industrial.Sensor.Emulator/"]
RUN dotnet restore "Industrial.Sensor.Emulator/Industrial.Sensor.Emulator.csproj"
COPY . .
WORKDIR "/app/src/Industrial.Sensor.Emulator"
# -c is for C# compiler build optimizations and not the environment
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Industrial.Sensor.Emulator.dll"]
