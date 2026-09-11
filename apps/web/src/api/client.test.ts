import { describe, it, expect, vi, beforeEach } from 'vitest'
import {
  listSubscriptions,
  subscribe,
  unsubscribe,
  listCookies,
  addCookie,
  listBackstageSubscriptions,
  listBackstageCookies,
  listWeiboSubscriptions,
  subscribeWeibo,
  unsubscribeWeibo,
  getWeiboTimeline,
  listNotifyChannels,
  listNotificationBindings,
  createNotificationBinding,
  deleteNotificationBinding,
} from './client'

const mockFetch = vi.fn()
vi.stubGlobal('fetch', mockFetch)

describe('API Client', () => {
  beforeEach(() => {
    mockFetch.mockReset()
  })

  describe('Weibo', () => {
    it('listWeiboSubscriptions should fetch weibo subscription list', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ items: [{ uid: 123, subscribedAt: '2024-01-01T00:00:00Z' }] }),
      })

      const result = await listWeiboSubscriptions()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/weibo/subscription'),
        expect.anything(),
      )
      expect(result).toEqual([{ uid: 123, subscribedAt: '2024-01-01T00:00:00Z' }])
    })

    it('subscribeWeibo should POST with uid body', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ uid: 123, subscribedAt: '2024-01-01T00:00:00Z' }),
      })

      const result = await subscribeWeibo({ uid: 123 })

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/weibo/subscription'),
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ uid: 123 }),
        }),
      )
      expect(result.uid).toBe(123)
    })

    it('unsubscribeWeibo should DELETE by uid path', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 204 })

      await unsubscribeWeibo(123)

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/weibo/subscription/123'),
        expect.objectContaining({ method: 'DELETE' }),
      )
    })

    it('getWeiboTimeline should append query params', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ total: 0, page: 1, pageSize: 20, items: [] }),
      })

      await getWeiboTimeline(123, { page: 2, pageSize: 50 })

      const urlArg = mockFetch.mock.calls[0][0] as string
      expect(urlArg).toContain('/weibo/user/123/timeline')
      expect(urlArg).toContain('page=2')
      expect(urlArg).toContain('pageSize=50')
    })

    it('should throw error on non-2xx response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        text: () => Promise.resolve('uid must be positive'),
      })

      await expect(subscribeWeibo({ uid: 0 })).rejects.toThrow('API request failed')
    })
  })

  describe('listSubscriptions', () => {
    it('should fetch subscription list', async () => {
      const mockData = {
        items: [
          { mid: 123, type: 'danmaku', subscribedAt: '2024-01-01T00:00:00Z' },
          { mid: 456, type: 'dynamic', subscribedAt: '2024-01-02T00:00:00Z' },
        ],
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockData),
      })

      const result = await listSubscriptions()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/subscription'),
        expect.objectContaining({
          headers: expect.objectContaining({
            'Content-Type': 'application/json',
          }),
        }),
      )
      expect(result).toEqual(mockData.items)
    })

    it('should return empty array when items is undefined', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({}),
      })

      const result = await listSubscriptions()
      expect(result).toEqual([])
    })

    it('should throw error on non-2xx response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 500,
        statusText: 'Internal Server Error',
        text: () => Promise.resolve('Server error message'),
      })

      await expect(listSubscriptions()).rejects.toThrow('API request failed')
    })
  })

  describe('subscribe', () => {
    it('should send POST request with correct body', async () => {
      const mockResponse = {
        mid: 123,
        type: 'danmaku',
        subscribedAt: '2024-01-01T00:00:00Z',
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockResponse),
      })

      const result = await subscribe({ mid: 123, type: 'danmaku' })

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/subscription'),
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ mid: 123, type: 'danmaku' }),
        }),
      )
      expect(result).toEqual(mockResponse)
    })

    it('should throw error on 400 response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        text: () => Promise.resolve('Invalid mid or type'),
      })

      await expect(subscribe({ mid: -1, type: 'danmaku' })).rejects.toThrow(
        'API request failed',
      )
    })
  })

  describe('unsubscribe', () => {
    it('should send DELETE request with correct path and query', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        status: 204,
      })

      await unsubscribe(123, 'danmaku')

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/subscription/123?type=danmaku'),
        expect.objectContaining({
          method: 'DELETE',
        }),
      )
    })

    it('should throw error on 404 response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 404,
        statusText: 'Not Found',
        text: () => Promise.resolve('Subscription not found'),
      })

      await expect(unsubscribe(123, 'danmaku')).rejects.toThrow(
        'API request failed',
      )
    })
  })

  describe('listCookies', () => {
    it('should fetch cookie list', async () => {
      const mockData = {
        items: [
          {
            id: 'cookie-1',
            status: 'active',
            lastCheckedAt: '2024-01-01T00:00:00Z',
            lastUsedAt: '2024-01-01T00:00:00Z',
          },
          {
            id: 'cookie-2',
            status: 'offline',
            lastCheckedAt: null,
            lastUsedAt: null,
          },
        ],
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockData),
      })

      const result = await listCookies()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/cookie'),
        expect.objectContaining({}),
      )
      expect(result).toEqual(mockData.items)
    })

    it('should return empty array when items is undefined', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({}),
      })

      const result = await listCookies()
      expect(result).toEqual([])
    })
  })

  describe('addCookie', () => {
    it('should send POST request with cookie body', async () => {
      const mockResponse = {
        id: 'cookie-1',
        status: 'active',
        lastCheckedAt: null,
        lastUsedAt: null,
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockResponse),
      })

      const result = await addCookie({ cookie: 'test-cookie-value' })

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/cookie'),
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ cookie: 'test-cookie-value' }),
        }),
      )
      expect(result).toEqual(mockResponse)
    })

    it('should throw error on 400 response for empty cookie', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        text: () => Promise.resolve('Cookie is empty'),
      })

      await expect(addCookie({ cookie: '' })).rejects.toThrow(
        'API request failed',
      )
    })
  })

  describe('listBackstageSubscriptions', () => {
    it('should fetch backstage subscription list', async () => {
      const mockData = {
        items: [
          { mid: 123, type: 'danmaku', streamer: { name: '某主播' } },
        ],
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockData),
      })

      const result = await listBackstageSubscriptions()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/backstage/subscription'),
        expect.objectContaining({}),
      )
      expect(result).toEqual(mockData.items)
    })

    it('should return empty array when items is undefined', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({}),
      })

      const result = await listBackstageSubscriptions()
      expect(result).toEqual([])
    })

    it('should throw error on non-2xx response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 500,
        statusText: 'Internal Server Error',
        text: () => Promise.resolve('Server error message'),
      })

      await expect(listBackstageSubscriptions()).rejects.toThrow(
        'API request failed',
      )
    })
  })

  describe('listBackstageCookies', () => {
    it('should fetch backstage cookie list', async () => {
      const mockData = {
        items: [
          { id: 'cookie-1', status: 'active', account: { name: '某帐号' } },
        ],
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockData),
      })

      const result = await listBackstageCookies()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/backstage/cookie'),
        expect.objectContaining({}),
      )
      expect(result).toEqual(mockData.items)
    })

    it('should throw error on non-2xx response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 500,
        statusText: 'Internal Server Error',
        text: () => Promise.resolve('Server error message'),
      })

      await expect(listBackstageCookies()).rejects.toThrow(
        'API request failed',
      )
    })
  })

  describe('listNotifyGroups', () => {
    it('should fetch notify groups', async () => {
      const mockData = {
        items: [
          { groupId: '123456', groupName: '运维群' },
        ],
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockData),
      })

      const result = await listNotifyGroups()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/notify/groups'),
        expect.objectContaining({}),
      )
      expect(result).toEqual(mockData.items)
    })

    it('should throw error on non-2xx response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 503,
        statusText: 'Service Unavailable',
        text: () => Promise.resolve('Milky 不可达'),
      })

      await expect(listNotifyGroups()).rejects.toThrow('API request failed')
    })
  })

  describe('listNotificationBindings', () => {
    it('should fetch notification bindings', async () => {
      const mockData = {
        items: [
          { id: '1', eventType: 'cookie_invalid', groupId: '123456', groupName: '运维群', createdAt: '2024-01-01T00:00:00Z' },
        ],
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockData),
      })

      const result = await listNotificationBindings()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/notify/subscriptions'),
        expect.objectContaining({}),
      )
      expect(result).toEqual(mockData.items)
    })
  })

  describe('createNotificationBinding', () => {
    it('should send POST request with correct body', async () => {
      const mockResponse = {
        id: '1',
        eventType: 'cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
        createdAt: '2024-01-01T00:00:00Z',
      }
      mockFetch.mockResolvedValue({
        ok: true,
        status: 201,
        json: () => Promise.resolve(mockResponse),
      })

      const result = await createNotificationBinding({
        eventType: 'cookie_invalid',
        groupId: '123456',
        groupName: '运维群',
      })

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/notify/subscriptions'),
        expect.objectContaining({
          method: 'POST',
        }),
      )
      expect(result).toEqual(mockResponse)
    })

    it('should throw error on 400 response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        text: () => Promise.resolve('eventType 非法'),
      })

      await expect(createNotificationBinding({
        eventType: 'invalid' as 'cookie_invalid',
        groupId: '123456',
      })).rejects.toThrow('API request failed')
    })
  })

  describe('deleteNotificationBinding', () => {
    it('should send DELETE request with correct path', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        status: 204,
      })

      await deleteNotificationBinding('1')

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/notify/subscriptions/1'),
        expect.objectContaining({
          method: 'DELETE',
        }),
      )
    })

    it('should throw error on 404 response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 404,
        statusText: 'Not Found',
        text: () => Promise.resolve('订阅不存在'),
      })

      await expect(deleteNotificationBinding('999')).rejects.toThrow(
        'API request failed',
      )
    })
  })
})
