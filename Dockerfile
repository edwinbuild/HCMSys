# Stage 1: ASP.NET Core 9.0 Base Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false

# Install the *patched-Qt* static wkhtmltopdf build (used by TimeAttendanceController's PDF exports via
# Rotativa.AspNetCore). This is NOT the same as `apt-get install wkhtmltopdf` — Debian/Ubuntu/Fedora's
# repository package links against the system Qt5WebKit, which is missing the patches wkhtmltopdf needs
# for correct multi-page pagination (verified directly: it silently renders arbitrarily long content
# onto a single squashed, illegible page instead of paginating). The official static release bundles its
# own patched Qt and has none of that problem — always get it from wkhtmltopdf's own releases, matching
# this base image's OS (bookworm), never from the distro's package manager.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        wget \
        xfonts-75dpi \
        xfonts-base \
        fontconfig \
        libjpeg62-turbo \
        libxrender1 \
        libxext6 \
        libx11-6 \
        libssl3 \
    && wget -q https://github.com/wkhtmltopdf/packaging/releases/download/0.12.6.1-3/wkhtmltox_0.12.6.1-3.bookworm_amd64.deb -O /tmp/wkhtmltox.deb \
    && (dpkg -i /tmp/wkhtmltox.deb || apt-get install -f -y) \
    && rm -f /tmp/wkhtmltox.deb \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

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
# The wkhtmltox .deb installs to /usr/local/bin/wkhtmltopdf; Rotativa.AspNetCore (configured in Program.cs
# via RotativaConfiguration.Setup) looks for it at wwwroot/Rotativa/wkhtmltopdf specifically.
RUN ln -s /usr/local/bin/wkhtmltopdf /app/wwwroot/Rotativa/wkhtmltopdf
ENTRYPOINT ["dotnet", "HCMSys.dll"]
