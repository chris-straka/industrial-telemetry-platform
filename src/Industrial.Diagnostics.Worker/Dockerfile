FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY ["src/Industrial.Diagnostics.Worker/Industrial.Diagnostics.Worker.csproj", "src/Industrial.Diagnostics.Worker/"]
RUN dotnet restore "src/Industrial.Diagnostics.Worker/Industrial.Diagnostics.Worker.csproj"

COPY . .
WORKDIR "/app/src/Industrial.Diagnostics.Worker"
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .

ENTRYPOINT ["dotnet", "Industrial.Diagnostics.Worker.dll"]
