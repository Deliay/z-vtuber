FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/core/Z.Vtuber.Common.Core.csproj servers/common/core/
COPY servers/common/live/Z.Vtuber.Common.Live.csproj servers/common/live/
COPY servers/common/recording/Z.Vtuber.Common.Recording.csproj servers/common/recording/
COPY servers/common/transcript/Z.Vtuber.Common.Transcript.csproj servers/common/transcript/
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
