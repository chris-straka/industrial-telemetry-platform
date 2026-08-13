FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Industrial.Web.Api/Industrial.Web.Api.csproj", "Industrial.Web.Api/"]
RUN dotnet restore "Industrial.Web.Api/Industrial.Web.Api.csproj"
COPY . .
WORKDIR "/src/src/Industrial.Web.Api"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Industrial.Web.Api.dll"]
