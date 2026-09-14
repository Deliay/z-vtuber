import type { components } from './schema'
import type { components as weiboComponents } from './schema-weibo'
import type { components as notifyComponents } from './schema-notify'
import type { components as cookieComponents } from './schema-cookie'

const BASE_URL = '/api'

type CookieItem = components['schemas']['CookieItem']
type SubscriptionItem = components['schemas']['SubscriptionItem']
type BackstageSubscriptionItem = components['schemas']['BackstageSubscriptionItem']
type BackstageCookieItem = components['schemas']['BackstageCookieItem']
type SubscribeRequest = components['schemas']['SubscribeRequest']
type CookieAddRequest = components['schemas']['CookieAddRequest']
type WeiboSubscriptionItem = weiboComponents['schemas']['WeiboSubscriptionItem']
type WeiboSubscribeRequest = weiboComponents['schemas']['WeiboSubscribeRequest']

type NotifyGroup = notifyComponents['schemas']['NotifyGroup']
type NotificationSubscription = notifyComponents['schemas']['NotificationSubscription']
type CreateNotificationSubscriptionRequest = notifyComponents['schemas']['CreateNotificationSubscriptionRequest']

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
