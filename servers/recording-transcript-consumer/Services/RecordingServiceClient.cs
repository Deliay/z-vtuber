using Z.Vtuber.RecordingTranscript.Consumer.Configuration;

namespace Z.Vtuber.RecordingTranscript.Consumer.Services;

/// <summary>实时音频探测结果：Live=200 在播；NotLive=404 已结束（可转 file 补尾）；Unknown=传输异常/其他状态（不得当作已结束）。</summary>
public enum LiveAudioProbe
{
    Live,
    NotLive,
    Unknown,
}

public interface IRecordingServiceClient
{
    string BuildLiveAudioUrl(long roomId);
    string BuildFileUrl(long roomId, string fileName);

    /// <summary>探测实时音频端点。仅 404 视为直播已结束，其余异常/状态返回 Unknown 交调用方重试。</summary>
    Task<LiveAudioProbe> ProbeLiveAudioAsync(long roomId, CancellationToken ct = default);
}

public sealed class RecordingServiceClient(
    HttpClient httpClient,
    ILogger<RecordingServiceClient> logger) : IRecordingServiceClient
{
    public string BuildLiveAudioUrl(long roomId) => BuildUrl($"api/{roomId}/stream/audio");

    public string BuildFileUrl(long roomId, string fileName) =>
        BuildUrl($"api/{roomId}/recordings/{Uri.EscapeDataString(fileName)}");

    public async Task<LiveAudioProbe> ProbeLiveAudioAsync(long roomId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"api/{roomId}/stream/audio");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.IsSuccessStatusCode)
            {
                return LiveAudioProbe.Live;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return LiveAudioProbe.NotLive;
            }

            logger.LogWarning(
                "Probe live audio for room {RoomId} returned unexpected status {Status}; cannot conclude live has ended",
                roomId,
                (int)response.StatusCode);
            return LiveAudioProbe.Unknown;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 传输异常 ≠ 直播结束：返回 Unknown，交调用方 FAILED/重试，避免误读仍在写入的录播文件
            logger.LogWarning(ex, "Probe live audio for room {RoomId} failed (transport); treating as unknown", roomId);
            return LiveAudioProbe.Unknown;
        }
    }

    private string BuildUrl(string relative)
    {
        var baseAddress = httpClient.BaseAddress ?? new Uri(RecordingServiceOptions.DefaultBaseUrl);
        return new Uri(baseAddress, relative).ToString();
    }
}
