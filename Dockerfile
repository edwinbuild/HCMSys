# Stage 1: ASP.NET Core 9.0 Base Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false

# Stage 2: SDK Image for compilation
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["HCMSys.csproj", "./"]
RUN dotnet restore "./HCMSys.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "HCMSys.csproj" -c Release -o /app/build

# Stage 3: Publish Production Binaries
FROM build AS publish
RUN dotnet publish "HCMSys.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 4: Final Lightweight Runtime Image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HCMSys.dll"]
