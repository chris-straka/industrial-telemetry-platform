FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY ["src/Industrial.Sensor.Emulator/Industrial.Sensor.Emulator.csproj", "Industrial.Sensor.Emulator/"]
RUN dotnet restore "Industrial.Sensor.Emulator/Industrial.Sensor.Emulator.csproj"
COPY . .
WORKDIR "/app/src/Industrial.Sensor.Emulator"
# -c is for C# compiler build optimizations and not the environment
RUN dotnet publish -c Release -o /app/publish

# runtime, not aspnet: this image needs the .NET runtime but never the ASP.NET Core
# shared framework, because nothing here listens on a port. Smaller image, less surface.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Industrial.Sensor.Emulator.dll"]
