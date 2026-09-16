FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/Z.Vtuber.Common.csproj servers/common/
COPY servers/recording-transcript-consumer/Z.Vtuber.RecordingTranscript.Consumer.csproj servers/recording-transcript-consumer/
RUN dotnet restore servers/recording-transcript-consumer/Z.Vtuber.RecordingTranscript.Consumer.csproj

COPY servers/common/ servers/common/
COPY servers/recording-transcript-consumer/ servers/recording-transcript-consumer/
RUN dotnet publish servers/recording-transcript-consumer/Z.Vtuber.RecordingTranscript.Consumer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# ffmpeg：实时音频流/录播文件转码为 16kHz mono s16le pipe，供 FunASR 流式识别
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.RecordingTranscript.Consumer.dll"]
