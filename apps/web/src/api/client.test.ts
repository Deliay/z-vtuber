import { describe, it, expect, vi, beforeEach } from 'vitest'
import {
  listSubscriptions,
  subscribe,
  unsubscribe,
  listCookies,
  addCookie,
  listBackstageSubscriptions,
  listBackstageCookies,
} from './client'

const mockFetch = vi.fn()
vi.stubGlobal('fetch', mockFetch)

describe('API Client', () => {
  beforeEach(() => {
    mockFetch.mockReset()
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
})
