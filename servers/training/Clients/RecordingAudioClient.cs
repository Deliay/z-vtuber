using Microsoft.Extensions.Options;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Clients;

/// <summary>
/// 录音片段取数（Plan 2.5 / 偏差 1）：解析 <c>b/live/{roomId}/recordings/{file}</c> →
/// <c>GET api/{roomId}/recordings/{file}/audio?startMs=&amp;endMs=</c>。
/// recording 片段端点要求 <c>endMs</c> 必填，<paramref name="endMs"/> 为空时按
/// <c>Ai:AudioClipDefaultDurationMs</c> 补齐（与前端 <c>trainingAudioClipUrl</c> 同口径，默认 300000）。
/// 取数失败/超时/非 2xx → <see cref="AudioUnavailableException"/>；超上限 → <see cref="AudioTooLargeException"/>。
/// 绝不记录音频内容。
/// </summary>
public sealed class RecordingAudioClient : IRecordingAudioClient
{
    private readonly HttpClient _httpClient;
    private readonly AiOptions _aiOptions;
    private readonly ILogger<RecordingAudioClient> _logger;

    public RecordingAudioClient(
        HttpClient httpClient,
        IOptions<RecordingServiceOptions> recordingOptions,
        IOptions<AiOptions> aiOptions,
        ILogger<RecordingAudioClient> logger)
    {
        _httpClient = httpClient;
        _aiOptions = aiOptions.Value;
        _logger = logger;

        // BaseAddress 为 recording 服务地址；归一化为以 "/" 结尾，保证相对路径拼接正确。
        var baseUrl = httpClient.BaseAddress?.ToString() ?? recordingOptions.Value.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!baseUrl.EndsWith('/'))
            {
                baseUrl += "/";
            }

            _httpClient.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        }
    }

    public async Task<AudioClip> GetClipAsync(
        string audioUri, long startMs, long? endMs, CancellationToken ct = default)
    {
        var parsed = TrainingMapping.ParseAudioUri(audioUri);
        if (parsed is null)
        {
            throw new ArgumentException("audio uri is empty");
        }

        // recording 片段端点要求 endMs 必填：空则按兜底时长补齐（Plan 偏差 1）。
        var effectiveEndMs = endMs ?? (startMs + _aiOptions.AudioClipDefaultDurationMs);
        var file = Uri.EscapeDataString(parsed.File);
        var relative = $"api/{parsed.RoomId}/recordings/{file}/audio?startMs={startMs}&endMs={effectiveEndMs}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _aiOptions.RequestTimeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(relative, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AudioUnavailableException("recording audio request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new AudioUnavailableException($"recording audio request failed: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AudioUnavailableException($"recording audio returned {(int)response.StatusCode}");
            }

            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AudioUnavailableException("recording audio read timed out");
            }
            catch (HttpRequestException ex)
            {
                throw new AudioUnavailableException($"recording audio read failed: {ex.Message}");
            }

            if (bytes.LongLength > _aiOptions.MaxAudioBytes)
            {
                throw new AudioTooLargeException(
                    $"recording audio exceeds max size {_aiOptions.MaxAudioBytes} bytes");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            _logger.LogDebug(
                "Fetched recording audio clip: roomId={RoomId} bytes={Bytes}", parsed.RoomId, bytes.Length);
            return new AudioClip(bytes, contentType);
        }
    }
}
