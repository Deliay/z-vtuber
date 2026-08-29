import type { components } from './schema'

const BASE_URL = '/api'

type CookieItem = components['schemas']['CookieItem']
type SubscriptionItem = components['schemas']['SubscriptionItem']
type BackstageSubscriptionItem = components['schemas']['BackstageSubscriptionItem']
type BackstageCookieItem = components['schemas']['BackstageCookieItem']
type SubscribeRequest = components['schemas']['SubscribeRequest']
type CookieAddRequest = components['schemas']['CookieAddRequest']

interface ApiError extends Error {
  status?: number
  serverMessage?: string
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
  type: 'danmaku' | 'dynamic',
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
