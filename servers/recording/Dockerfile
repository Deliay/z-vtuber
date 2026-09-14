FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/Z.Vtuber.Common.csproj servers/common/
COPY servers/recording/Z.Vtuber.Recording.csproj servers/recording/
RUN dotnet restore servers/recording/Z.Vtuber.Recording.csproj

COPY servers/common/ servers/common/
COPY servers/recording/ servers/recording/
RUN dotnet publish servers/recording/Z.Vtuber.Recording.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# ffmpeg：RAW 录制与 AV1(NVENC) 二压依赖；无 GPU 环境可用 Recording__Av1__Enabled=false 降级
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV Recording__StorageDir=/data/recordings
VOLUME ["/data/recordings"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.Recording.dll"]
