FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Industrial.Sensor.Emulator/Industrial.Sensor.Emulator.csproj", "Industrial.Sensor.Emulator/"]
RUN dotnet restore "Industrial.Sensor.Emulator/Industrial.Sensor.Emulator.csproj"
COPY . .
WORKDIR "/src/src/Industrial.Sensor.Emulator"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Industrial.Sensor.Emulator.dll"]
