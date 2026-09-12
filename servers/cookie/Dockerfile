FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/Z.Vtuber.Common.csproj servers/common/
COPY servers/cookie/Z.Vtuber.Cookie.csproj servers/cookie/
RUN dotnet restore servers/cookie/Z.Vtuber.Cookie.csproj

COPY servers/common/ servers/common/
COPY servers/cookie/ servers/cookie/
RUN dotnet publish servers/cookie/Z.Vtuber.Cookie.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install Playwright system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libdbus-1-3 \
    libxkbcommon0 \
    libatspi2.0-0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libpango-1.0-0 \
    libcairo2 \
    libasound2 \
    libwayland-client0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Install Playwright browsers (Firefox)
RUN dotnet tool install --global Microsoft.Playwright.CLI || true
RUN dotnet playwright install firefox --with-deps

EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.Cookie.dll"]