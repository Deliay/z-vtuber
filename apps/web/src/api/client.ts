import type { components } from './schema'
import type { components as weiboComponents } from './schema-weibo'
import type { components as notifyComponents } from './schema-notify'
import type { components as cookieComponents } from './schema-cookie'
import type { components as transcriptComponents } from './schema-recording-transcript'
import type { components as trainingComponents } from './schema-training'

const BASE_URL = '/api'

type CookieItem = components['schemas']['CookieItem']
type SubscriptionItem = components['schemas']['SubscriptionItem']
type BackstageSubscriptionItem = components['schemas']['BackstageSubscriptionItem']
type BackstageCookieItem = components['schemas']['BackstageCookieItem']
type RecordingStatusItem = components['schemas']['RecordingStatusItem']
type RecordingFile = components['schemas']['RecordingFile']
type SubscribeRequest = components['schemas']['SubscribeRequest']
type CookieAddRequest = components['schemas']['CookieAddRequest']
type WeiboSubscriptionItem = weiboComponents['schemas']['WeiboSubscriptionItem']
type WeiboSubscribeRequest = weiboComponents['schemas']['WeiboSubscribeRequest']

type NotifyGroup = notifyComponents['schemas']['NotifyGroup']
type NotificationSubscription = notifyComponents['schemas']['NotificationSubscription']
type CreateNotificationSubscriptionRequest = notifyComponents['schemas']['CreateNotificationSubscriptionRequest']

type TranscriptTask = transcriptComponents['schemas']['TranscriptTask']
type TranscriptTaskDetail = transcriptComponents['schemas']['TranscriptTaskDetail']
type TranscriptSettingsItem = transcriptComponents['schemas']['TranscriptSettingsItem']
type TranscriptModel = transcriptComponents['schemas']['TranscriptModel']
type TranscriptModelCreateRequest = transcriptComponents['schemas']['TranscriptModelCreateRequest']
type TranscriptModelUpdateRequest = transcriptComponents['schemas']['TranscriptModelUpdateRequest']
type TranscriptRecordingItem = transcriptComponents['schemas']['RecordingItem']
type TranscriptCandidateRoom = transcriptComponents['schemas']['CandidateRoom']
type RoomTranscriptView = transcriptComponents['schemas']['RoomTranscriptView']

type TrainingDataset = trainingComponents['schemas']['TrainingDataset']
type TrainingDatasetItem = trainingComponents['schemas']['TrainingDatasetItem']
type TrainingProvider = trainingComponents['schemas']['TrainingProvider']
type TrainingModel = trainingComponents['schemas']['TrainingModel']
type TrainingProviderCreateRequest = trainingComponents['schemas']['TrainingProviderCreateRequest']
type TrainingProviderUpdateRequest = trainingComponents['schemas']['TrainingProviderUpdateRequest']
type TrainingModelCreateRequest = trainingComponents['schemas']['TrainingModelCreateRequest']
type TrainingModelUpdateRequest = trainingComponents['schemas']['TrainingModelUpdateRequest']
type TrainingDiscoveredModel = trainingComponents['schemas']['TrainingDiscoveredModel']
type TrainingEmotionTask = trainingComponents['schemas']['TrainingEmotionTask']
type TrainingEmotionTaskList = trainingComponents['schemas']['TrainingEmotionTaskList']

interface ApiError extends Error {
  status?: number
  serverMessage?: string
}

/** 服务端 `{"error": "..."}` 错误码 → 用户可读文案（未收录的错误码原样展示）。 */
const ERROR_MESSAGES: Record<string, string> = {
  'name is required': '数据集名字不能为空',
  'name too long': '数据集名字过长（最多 128 字符）',
  'unsupported dataset type': '不支持的数据集类型',
  'dataset name exists': '数据集名字已存在',
  'dataset not found': '数据集不存在',
  'task not found': '任务不存在',
  'task not done': '该识别任务尚未完成，无法导入',
  'invalid time range': '时间区间非法（开始不小于 0，结束须晚于开始）',
  'field cannot be null': '该字段不能为空',
  'item not found': '条目不存在',
  'transcript service unavailable': '识别服务暂不可用，请稍后重试',
  'training service unavailable': '训练服务暂不可用，请稍后重试',
  'provider name exists': 'Provider 名称已存在',
  'provider not found': 'Provider 不存在',
  'unsupported provider type': '不支持的模型类型',
  'endpoint is required': '服务地址必填',
  'invalid endpoint': '服务地址非法',
  'api key is required': 'API key 必填',
  'model exists': '模型已存在',
  'model not found': '模型不存在',
  'model is required': '模型名必填',
  'model too long': '模型名过长',
  'model disabled': '模型已停用',
  'model does not support audio input': '该模型不支持音频输入',
  'audio uri is empty': '音频地址为空',
  'audio too large': '音频过大',
  'audio unavailable': '音频暂不可用',
  'model service unavailable': '模型服务不可用',
  'invalid model output': '模型输出无法解析',
  'provider service unavailable': '模型服务不可用',
  'invalid provider response': '模型服务响应非法',
  'models is required': '请选择要导入的模型',
  'itemIds is required': '请选择要识别的条目',
  'task not failed': '任务未处于失败状态',
  'active task exists': '该条目已有进行中的识别任务',
  'emotion task failed': '情绪识别失败',
  'item text is empty': '条目文本为空，无法识别',
  'max attempts exceeded': '已超过最大重试次数，无法自动重试',
}

/**
 * 把 `ApiError.serverMessage`（原始 JSON 错误体或纯文本）转成可读提示：
 * 1. 解析 `{"error": "..."}` 取出错误码；2. 映射已知错误码为中文文案；3. 未收录则原样展示。
 * `serverMessage` 本身保持原始报文不变（既有调用方按原文解析），展示统一走本函数。
 */
export function apiErrorMessage(error: unknown, fallback = '操作失败'): string {
  const raw = (error as { serverMessage?: string })?.serverMessage ?? ''
  if (!raw) {
    return fallback
  }

  let code = raw
  try {
    const parsed = JSON.parse(raw) as { error?: unknown }
    if (typeof parsed?.error === 'string' && parsed.error) {
      code = parsed.error
    }
  } catch {
    // 非 JSON 错误体（代理/网关返回的纯文本），按原文处理
  }

  return ERROR_MESSAGES[code] ?? (code || fallback)
}

/**
 * 仅有服务端错误码字符串时（如任务记录 `error` 字段）映射为可读文案；未收录原样返回。
 */
export function apiErrorCodeText(code: string): string {
  return ERROR_MESSAGES[code] ?? code
}

async function request<T>(
  path: string,
  options?: RequestInit,
): Promise<T> {
  const url = `${BASE_URL}${path}`
  const response = await fetch(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  })

  if (!response.ok) {
    const error: ApiError = new Error(`API request failed: ${response.statusText}`)
    error.status = response.status

    try {
      const errorBody = await response.text()
      if (errorBody) {
        error.serverMessage = errorBody
      }
    } catch {
      // Ignore error reading body
    }

    throw error
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json()
}

export async function listSubscriptions(): Promise<SubscriptionItem[]> {
  const data = await request<components['schemas']['SubscriptionList']>(
    '/b/subscription',
  )
  return data.items ?? []
}

export async function subscribe(
  params: SubscribeRequest,
): Promise<SubscriptionItem> {
  return request<SubscriptionItem>('/b/subscription', {
    method: 'POST',
    body: JSON.stringify(params),
  })
}

export async function unsubscribe(
  mid: number,
  type: NonNullable<SubscriptionItem['type']>,
): Promise<void> {
  return request<void>(
    `/b/subscription/${mid}?type=${type}`,
    { method: 'DELETE' },
  )
}

export async function listCookies(): Promise<CookieItem[]> {
  const data = await request<components['schemas']['CookieList']>('/b/cookie')
  return data.items ?? []
}

export async function addCookie(
  params: CookieAddRequest,
): Promise<CookieItem> {
  return request<CookieItem>('/b/cookie', {
    method: 'POST',
    body: JSON.stringify(params),
  })
}

export async function listBackstageSubscriptions(): Promise<
  BackstageSubscriptionItem[]
> {
  const data = await request<
    components['schemas']['BackstageSubscriptionList']
  >('/b/backstage/subscription')
  return data.items ?? []
}

export async function listBackstageCookies(): Promise<BackstageCookieItem[]> {
  const data = await request<components['schemas']['BackstageCookieList']>(
    '/b/backstage/cookie',
  )
  return data.items ?? []
}

export async function listRecordingStatus(): Promise<RecordingStatusItem[]> {
  const data = await request<components['schemas']['RecordingStatusList']>(
    '/b/live/recording-status',
  )
  return data.items ?? []
}

export async function listRecordings(roomId: number): Promise<RecordingFile[]> {
  const data = await request<components['schemas']['RecordingFileList']>(
    `/b/live/${roomId}/recordings`,
  )
  return data.files ?? []
}

export function recordingAudioUrl(roomId: number): string {
  return `${BASE_URL}/b/live/${roomId}/live-stream/audio`
}

export function recordingDownloadUrl(roomId: number, fileName: string): string {
  return `${BASE_URL}/b/live/${roomId}/recordings/${encodeURIComponent(fileName)}`
}

export function recordingAudioClipUrl(
  roomId: number,
  fileName: string,
  startMs: number,
  endMs: number,
): string {
  const query = new URLSearchParams({ startMs: String(startMs), endMs: String(endMs) })
  return `${BASE_URL}/b/live/${roomId}/recordings/${encodeURIComponent(fileName)}/audio?${query}`
}

export function transcriptSubtitleUrl(taskId: string, format: 'srt' | 'ass'): string {
  return `${BASE_URL}/transcripts/task/${encodeURIComponent(taskId)}/subtitle?format=${format}`
}

export async function listWeiboSubscriptions(): Promise<WeiboSubscriptionItem[]> {
  const data = await request<weiboComponents['schemas']['WeiboSubscriptionList']>(
    '/weibo/subscription',
  )
  return data.items ?? []
}

export async function subscribeWeibo(
  params: WeiboSubscribeRequest,
): Promise<WeiboSubscriptionItem> {
  return request<WeiboSubscriptionItem>('/weibo/subscription', {
    method: 'POST',
    body: JSON.stringify(params),
  })
}

export async function listNotifyGroups(): Promise<NotifyGroup[]> {
  const data = await request<{ items: NotifyGroup[] }>('/notify/groups')
  return data.items ?? []
}

export async function listNotificationBindings(): Promise<NotificationSubscription[]> {
  const data = await request<{ items: NotificationSubscription[] }>('/notify/subscriptions')
  return data.items ?? []
}

export async function createNotificationBinding(
  params: CreateNotificationSubscriptionRequest,
): Promise<NotificationSubscription> {
  return request<NotificationSubscription>('/notify/subscriptions', {
    method: 'POST',
    body: JSON.stringify(params),
  })
}

export async function unsubscribeWeibo(uid: number): Promise<void> {
  return request<void>(`/weibo/subscription/${uid}`, { method: 'DELETE' })
}

export async function getWeiboTimeline(
  uid: number,
  params: {
    page?: number
    pageSize?: number
    startedAt?: string
    endedAt?: string
  } = {},
): Promise<weiboComponents['schemas']['WeiboTimelinePage']> {
  const query = new URLSearchParams()
  if (params.page) query.set('page', String(params.page))
  if (params.pageSize) query.set('pageSize', String(params.pageSize))
  if (params.startedAt) query.set('startedAt', params.startedAt)
  if (params.endedAt) query.set('endedAt', params.endedAt)
  const qs = query.toString()
  return request(`/weibo/user/${uid}/timeline${qs ? `?${qs}` : ''}`)
}

export async function deleteNotificationBinding(id: string): Promise<void> {
  return request<void>(`/notify/subscriptions/${id}`, { method: 'DELETE' })
}

export async function listCookieAccounts(): Promise<cookieComponents['schemas']['AccountItem'][]> {
  const data = await request<cookieComponents['schemas']['AccountList']>(
    '/cookie/accounts',
  )
  return data.items ?? []
}

export async function createCookieAccount(): Promise<cookieComponents['schemas']['PendingAccount']> {
  return request<cookieComponents['schemas']['PendingAccount']>('/cookie/accounts', {
    method: 'POST',
  })
}

export async function getCookieAccount(
  accountId: string,
): Promise<cookieComponents['schemas']['AccountStatus']> {
  return request<cookieComponents['schemas']['AccountStatus']>(
    `/cookie/accounts/${accountId}`,
  )
}

export async function deleteCookieAccount(accountId: string): Promise<void> {
  return request<void>(`/cookie/accounts/${accountId}`, { method: 'DELETE' })
}

export async function listAvailableCookies(): Promise<cookieComponents['schemas']['CookieItem'][]> {
  const data = await request<cookieComponents['schemas']['CookieList']>(
    '/cookie/available',
  )
  return data.items ?? []
}

export async function listWeiboCookieAccounts(): Promise<cookieComponents['schemas']['AccountItem'][]> {
  const data = await request<cookieComponents['schemas']['AccountList']>(
    '/cookie/weibo/accounts',
  )
  return data.items ?? []
}

export async function createWeiboCookieAccount(): Promise<cookieComponents['schemas']['PendingAccount']> {
  return request<cookieComponents['schemas']['PendingAccount']>('/cookie/weibo/accounts', {
    method: 'POST',
  })
}

export async function getWeiboCookieAccount(
  accountId: string,
): Promise<cookieComponents['schemas']['AccountStatus']> {
  return request<cookieComponents['schemas']['AccountStatus']>(
    `/cookie/weibo/accounts/${accountId}`,
  )
}

export async function deleteWeiboCookieAccount(accountId: string): Promise<void> {
  return request<void>(`/cookie/weibo/accounts/${accountId}`, { method: 'DELETE' })
}

export async function listAvailableWeiboCookies(): Promise<cookieComponents['schemas']['CookieItem'][]> {
  const data = await request<cookieComponents['schemas']['CookieList']>(
    '/cookie/weibo/available',
  )
  return data.items ?? []
}

export async function checkoutWeiboCookie(): Promise<cookieComponents['schemas']['CookieCheckout']> {
  return request<cookieComponents['schemas']['CookieCheckout']>('/cookie/weibo/checkout', {
    method: 'POST',
  })
}

export async function reportInvalidWeiboCookie(
  cookieId: string,
): Promise<cookieComponents['schemas']['CookieCheckout']> {
  return request<cookieComponents['schemas']['CookieCheckout']>('/cookie/weibo/report-invalid', {
    method: 'POST',
    body: JSON.stringify({ cookieId }),
  })
}

export async function cooldownWeiboCookie(
  cookieId: string,
  durationSeconds: number,
): Promise<void> {
  return request<void>('/cookie/weibo/cooldown', {
    method: 'POST',
    body: JSON.stringify({ cookieId, durationSeconds }),
  })
}

export async function checkoutCookie(): Promise<cookieComponents['schemas']['CookieCheckout']> {
  return request<cookieComponents['schemas']['CookieCheckout']>('/cookie/checkout', {
    method: 'POST',
  })
}

export async function reportInvalidCookie(
  cookieId: string,
): Promise<cookieComponents['schemas']['CookieCheckout']> {
  return request<cookieComponents['schemas']['CookieCheckout']>('/cookie/report-invalid', {
    method: 'POST',
    body: JSON.stringify({ cookieId }),
  })
}

export async function listTranscriptRecordings(params: {
  roomId?: number
  limit?: number
  before?: string
} = {}): Promise<TranscriptRecordingItem[]> {
  const query = new URLSearchParams()
  if (params.roomId) query.set('roomId', String(params.roomId))
  if (params.limit) query.set('limit', String(params.limit))
  if (params.before) query.set('before', params.before)
  const qs = query.toString()
  const data = await request<transcriptComponents['schemas']['RecordingList']>(
    `/recordings${qs ? `?${qs}` : ''}`,
  )
  return data.items ?? []
}

export async function getTranscriptCandidateRooms(): Promise<TranscriptCandidateRoom[]> {
  const data = await request<transcriptComponents['schemas']['CandidateRoomList']>(
    '/transcripts/candidate-rooms',
  )
  return data.items ?? []
}

export async function deleteTranscriptSettings(roomId: number): Promise<void> {
  return request<void>(`/recordings/${roomId}/transcripts/settings`, {
    method: 'DELETE',
  })
}

export async function enqueueTranscript(
  recordingId: string,
  modelId: string,
): Promise<transcriptComponents['schemas']['EnqueueResponse']> {
  return request<transcriptComponents['schemas']['EnqueueResponse']>(
    '/transcripts/enqueue',
    {
      method: 'POST',
      body: JSON.stringify({ recordingId, modelId }),
    },
  )
}

export async function listTranscriptTasks(params: {
  recordingId?: string
  roomId?: number
  status?: TranscriptTask['status']
  limit?: number
} = {}): Promise<TranscriptTask[]> {
  const query = new URLSearchParams()
  if (params.recordingId) query.set('recordingId', params.recordingId)
  if (params.roomId) query.set('roomId', String(params.roomId))
  if (params.status) query.set('status', params.status)
  if (params.limit) query.set('limit', String(params.limit))
  const qs = query.toString()
  const data = await request<transcriptComponents['schemas']['TranscriptTaskList']>(
    `/transcripts/task${qs ? `?${qs}` : ''}`,
  )
  return data.items ?? []
}

export async function getTranscriptTask(
  taskId: string,
): Promise<TranscriptTaskDetail> {
  return request<TranscriptTaskDetail>(
    `/transcripts/task/${encodeURIComponent(taskId)}`,
  )
}

export async function retryTranscriptTask(taskId: string): Promise<TranscriptTask> {
  return request<TranscriptTask>(
    `/transcripts/task/${encodeURIComponent(taskId)}/retry`,
    { method: 'POST' },
  )
}

export async function updateTranscriptTaskOffset(
  taskId: string,
  offsetMs: number,
): Promise<TranscriptTask> {
  return request<TranscriptTask>(
    `/transcripts/task/${encodeURIComponent(taskId)}/offset`,
    {
      method: 'PUT',
      body: JSON.stringify({ offsetMs }),
    },
  )
}

export function transcriptSseUrl(taskId: string): string {
  return `${BASE_URL}/transcripts/task/${encodeURIComponent(taskId)}/sse`
}

export function openTranscriptStream(taskId: string): EventSource {
  return new EventSource(transcriptSseUrl(taskId))
}

export async function getTranscriptSettings(): Promise<TranscriptSettingsItem[]> {
  const data = await request<transcriptComponents['schemas']['TranscriptSettingsList']>(
    '/transcripts/settings',
  )
  return data.items ?? []
}

export async function setRoomAutoEnqueue(
  roomId: number,
  enable: boolean,
  modelId?: string,
): Promise<transcriptComponents['schemas']['TranscriptSettingsUpdateResponse']> {
  return request<transcriptComponents['schemas']['TranscriptSettingsUpdateResponse']>(
    `/recordings/${roomId}/transcripts/settings`,
    {
      method: 'POST',
      body: JSON.stringify(modelId ? { enable, modelId } : { enable }),
    },
  )
}

export async function getRoomTranscripts(
  roomId: number,
): Promise<RoomTranscriptView> {
  return request<RoomTranscriptView>(`/recordings/${roomId}/transcripts`)
}

export async function listTranscriptModels(): Promise<TranscriptModel[]> {
  const data = await request<transcriptComponents['schemas']['TranscriptModelList']>(
    '/transcripts/models',
  )
  return data.items ?? []
}

export async function createTranscriptModel(
  params: TranscriptModelCreateRequest,
): Promise<TranscriptModel> {
  return request<TranscriptModel>('/transcripts/models', {
    method: 'POST',
    body: JSON.stringify(params),
  })
}

export async function updateTranscriptModel(
  modelId: string,
  params: TranscriptModelUpdateRequest,
): Promise<TranscriptModel> {
  return request<TranscriptModel>(
    `/transcripts/models/${encodeURIComponent(modelId)}`,
    {
      method: 'PUT',
      body: JSON.stringify(params),
    },
  )
}

export async function listTrainingDatasets(
  type: 'audio' = 'audio',
): Promise<TrainingDataset[]> {
  const data = await request<trainingComponents['schemas']['TrainingDatasetList']>(
    `/training/datasets?type=${encodeURIComponent(type)}`,
  )
  return data.items ?? []
}

export async function createTrainingDataset(params: {
  name: string
  type: 'audio'
}): Promise<TrainingDataset> {
  return request<TrainingDataset>('/training/datasets', {
    method: 'POST',
    body: JSON.stringify(params),
  })
}

export async function renameTrainingDataset(
  datasetId: string,
  params: { name: string },
): Promise<TrainingDataset> {
  return request<TrainingDataset>(
    `/training/datasets/${encodeURIComponent(datasetId)}`,
    {
      method: 'PUT',
      body: JSON.stringify(params),
    },
  )
}

export async function deleteTrainingDataset(datasetId: string): Promise<void> {
  await request<void>(`/training/datasets/${encodeURIComponent(datasetId)}`, {
    method: 'DELETE',
  })
}

export async function listTrainingDatasetItems(
  datasetId: string,
  params: { audioType?: string; source?: string } = {},
): Promise<TrainingDatasetItem[]> {
  const query = new URLSearchParams()
  if (params.audioType) query.set('audioType', params.audioType)
  if (params.source) query.set('source', params.source)
  const qs = query.toString()
  const data = await request<trainingComponents['schemas']['TrainingDatasetItemList']>(
    `/training/datasets/${encodeURIComponent(datasetId)}/items${qs ? `?${qs}` : ''}`,
  )
  return data.items ?? []
}

export async function importTrainingDatasetItems(
  datasetId: string,
  params: { taskId: string },
): Promise<{ imported: number; skipped: number }> {
  return request<{ imported: number; skipped: number }>(
    `/training/datasets/${encodeURIComponent(datasetId)}/items/import`,
    {
      method: 'POST',
      body: JSON.stringify(params),
    },
  )
}

export async function updateTrainingDatasetItem(
  datasetId: string,
  itemId: string,
  params: {
    audioUri?: string
    text?: string
    startMs?: number
    /** `null` 表示置空（到音频末尾），必须以 JSON `null` 透传。 */
    endMs?: number | null
  },
): Promise<TrainingDatasetItem> {
  // 仅提交出现（哪怕是 null）的字段：缺失字段由服务端保持不变。
  const body: Record<string, unknown> = {}
  if (params.audioUri !== undefined) body.audioUri = params.audioUri
  if (params.text !== undefined) body.text = params.text
  if (params.startMs !== undefined) body.startMs = params.startMs
  if (params.endMs !== undefined) body.endMs = params.endMs

  return request<TrainingDatasetItem>(
    `/training/datasets/${encodeURIComponent(datasetId)}/items/${encodeURIComponent(itemId)}`,
    {
      method: 'PUT',
      body: JSON.stringify(body),
    },
  )
}

export async function batchDeleteTrainingDatasetItems(
  datasetId: string,
  itemIds: string[],
): Promise<{ deleted: number }> {
  return request<{ deleted: number }>(
    `/training/datasets/${encodeURIComponent(datasetId)}/items/batch-delete`,
    {
      method: 'POST',
      body: JSON.stringify({ itemIds }),
    },
  )
}

export async function listTrainingProviders(): Promise<TrainingProvider[]> {
  const data = await request<trainingComponents['schemas']['TrainingProviderList']>(
    '/training/providers',
  )
  return data.items ?? []
}

export async function createTrainingProvider(
  body: TrainingProviderCreateRequest,
): Promise<TrainingProvider> {
  return request<TrainingProvider>('/training/providers', {
    method: 'POST',
    body: JSON.stringify(body),
  })
}

export async function updateTrainingProvider(
  providerId: string,
  body: TrainingProviderUpdateRequest,
): Promise<TrainingProvider> {
  return request<TrainingProvider>(
    `/training/providers/${encodeURIComponent(providerId)}`,
    {
      method: 'PUT',
      body: JSON.stringify(body),
    },
  )
}

export async function deleteTrainingProvider(providerId: string): Promise<void> {
  await request<void>(`/training/providers/${encodeURIComponent(providerId)}`, {
    method: 'DELETE',
  })
}

export async function discoverTrainingModels(
  providerId: string,
): Promise<TrainingDiscoveredModel[]> {
  const data = await request<trainingComponents['schemas']['TrainingModelDiscoverResult']>(
    `/training/providers/${encodeURIComponent(providerId)}/models/discover`,
  )
  return data.models ?? []
}

export async function importTrainingModels(
  providerId: string,
  models: string[],
): Promise<{ imported: number; skipped: number }> {
  return request<{ imported: number; skipped: number }>(
    `/training/providers/${encodeURIComponent(providerId)}/models/import`,
    {
      method: 'POST',
      body: JSON.stringify({ models }),
    },
  )
}

export async function listTrainingModels(enabled?: boolean): Promise<TrainingModel[]> {
  const query = new URLSearchParams()
  if (enabled !== undefined) query.set('enabled', String(enabled))
  const qs = query.toString()
  const data = await request<trainingComponents['schemas']['TrainingModelList']>(
    `/training/models${qs ? `?${qs}` : ''}`,
  )
  return data.items ?? []
}

export async function createTrainingModel(
  params: TrainingModelCreateRequest,
): Promise<TrainingModel> {
  return request<TrainingModel>('/training/models', {
    method: 'POST',
    body: JSON.stringify(params),
  })
}

export async function updateTrainingModel(
  modelId: string,
  body: TrainingModelUpdateRequest,
): Promise<TrainingModel> {
  return request<TrainingModel>(`/training/models/${encodeURIComponent(modelId)}`, {
    method: 'PUT',
    body: JSON.stringify(body),
  })
}

export async function deleteTrainingModel(modelId: string): Promise<void> {
  await request<void>(`/training/models/${encodeURIComponent(modelId)}`, {
    method: 'DELETE',
  })
}

export async function recognizeTrainingDatasetItem(
  datasetId: string,
  itemId: string,
  modelId: string,
): Promise<trainingComponents['schemas']['TrainingRecognizeResult']> {
  return request<trainingComponents['schemas']['TrainingRecognizeResult']>(
    `/training/datasets/${encodeURIComponent(datasetId)}/items/${encodeURIComponent(itemId)}/recognize`,
    {
      method: 'POST',
      body: JSON.stringify({ modelId }),
    },
  )
}

export async function createEmotionTasks(
  datasetId: string,
  itemIds: string[],
  modelId: string,
): Promise<trainingComponents['schemas']['TrainingEmotionTaskCreateResult']> {
  return request<trainingComponents['schemas']['TrainingEmotionTaskCreateResult']>(
    `/training/datasets/${encodeURIComponent(datasetId)}/items/emotion-tasks`,
    {
      method: 'POST',
      body: JSON.stringify({ itemIds, modelId }),
    },
  )
}

export async function listEmotionTasks(
  params: { datasetId?: string; status?: string; limit?: number } = {},
): Promise<TrainingEmotionTaskList> {
  const query = new URLSearchParams()
  if (params.datasetId) query.set('datasetId', params.datasetId)
  if (params.status) query.set('status', params.status)
  if (params.limit !== undefined) query.set('limit', String(params.limit))
  const qs = query.toString()
  return request<TrainingEmotionTaskList>(
    `/training/emotion-tasks${qs ? `?${qs}` : ''}`,
  )
}

export async function retryEmotionTask(taskId: string): Promise<TrainingEmotionTask> {
  return request<TrainingEmotionTask>(
    `/training/emotion-tasks/${encodeURIComponent(taskId)}/retry`,
    { method: 'POST' },
  )
}

/**
 * 训练条目试听地址：`audioUri` 形如 `b/live/{roomId}/recordings/{fileName}` 时
 * 复用 `recordingAudioClipUrl`；`endMs` 为空（null/undefined/<= 0）时以
 * `startMs + 300000`（试听预览上限 5 分钟）兜底；uri 不可解析返回 `null`。
 */
export function trainingAudioClipUrl(
  audioUri: string,
  startMs: number,
  endMs: number | null | undefined,
): string | null {
  const match = /^b\/live\/(\d+)\/recordings\/(.+)$/.exec(audioUri)
  if (!match) {
    return null
  }

  const roomId = Number(match[1])
  const fileName = match[2]
  const end = typeof endMs === 'number' && endMs > 0 ? endMs : startMs + 300000
  return recordingAudioClipUrl(roomId, fileName, startMs, end)
}
