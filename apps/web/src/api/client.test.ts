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
  listNotifyGroups,
  listNotificationBindings,
  createNotificationBinding,
  deleteNotificationBinding,
  listCookieAccounts,
  createCookieAccount,
  getCookieAccount,
  deleteCookieAccount,
  listAvailableCookies,
  listRecordingStatus,
  listRecordings,
  recordingAudioUrl,
  recordingDownloadUrl,
  recordingAudioClipUrl,
  listWeiboCookieAccounts,
  createWeiboCookieAccount,
  getWeiboCookieAccount,
  deleteWeiboCookieAccount,
  listAvailableWeiboCookies,
  checkoutWeiboCookie,
  reportInvalidWeiboCookie,
  cooldownWeiboCookie,
  listTranscriptRecordings,
  getTranscriptCandidateRooms,
  deleteTranscriptSettings,
  enqueueTranscript,
  listTranscriptTasks,
  getTranscriptTask,
  retryTranscriptTask,
  transcriptSseUrl,
  transcriptSubtitleUrl,
  openTranscriptStream,
  getTranscriptSettings,
  setRoomAutoEnqueue,
  getRoomTranscripts,
  listTranscriptModels,
  createTranscriptModel,
  updateTranscriptModel,
} from './client'

const mockFetch = vi.fn()
vi.stubGlobal('fetch', mockFetch)

const mockEventSource = vi.fn()
vi.stubGlobal('EventSource', mockEventSource)

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
        eventType: 'bilibili_cookie_invalid',
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
        eventType: 'bilibili_cookie_invalid',
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
        eventType: 'invalid' as 'bilibili_cookie_invalid',
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

  describe('Recording', () => {
    it('listRecordingStatus should fetch /b/live/recording-status', async () => {
      const mockData = {
        items: [
          {
            mid: 123,
            roomId: 456,
            state: 'recording',
            hostName: 'host-1',
            sessionTitle: '晚间歌回',
            files: ['[RAW][456][某主播] 2026-09-13-晚间歌回.flv'],
          },
        ],
      }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve(mockData),
      })

      const result = await listRecordingStatus()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/live/recording-status'),
        expect.anything(),
      )
      expect(result).toEqual(mockData.items)
    })

    it('listRecordingStatus should return empty array when items undefined', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({}) })

      expect(await listRecordingStatus()).toEqual([])
    })

    it('listRecordings should fetch room recordings', async () => {
      const files = [
        {
          name: 'a.flv',
          size: 1024,
          createdAt: '2026-09-01T00:00:00Z',
          info: {
            recordingId: 'rec-1',
            title: '歌回',
            mid: 456,
            startedAt: '2026-09-01T00:00:00Z',
            endedAt: '2026-09-01T02:00:00Z',
          },
        },
        {
          name: 'b.flv',
          size: 2048,
          createdAt: '2026-09-02T00:00:00Z',
          info: {
            recordingId: 'rec-2',
            title: '杂谈',
            mid: 456,
            startedAt: '2026-09-02T00:00:00Z',
            endedAt: null,
          },
        },
      ]
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ files }),
      })

      const result = await listRecordings(456)

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/b/live/456/recordings'),
        expect.anything(),
      )
      expect(result).toEqual(files)
    })

    it('recordingAudioUrl should build proxy url', () => {
      expect(recordingAudioUrl(456)).toContain('/b/live/456/live-stream/audio')
    })

    it('recordingDownloadUrl should encode file name', () => {
      expect(recordingDownloadUrl(456, '[RAW] a.flv')).toContain(
        '/b/live/456/recordings/%5BRAW%5D%20a.flv',
      )
    })

    it('recordingAudioClipUrl should build the audio clip url with encoded file name and ms query', () => {
      expect(recordingAudioClipUrl(456, '[RAW] a.flv', 1000, 2500)).toBe(
        '/api/b/live/456/recordings/%5BRAW%5D%20a.flv/audio?startMs=1000&endMs=2500',
      )
      expect(recordingAudioClipUrl(101, 'a.flv', 0, 1000)).toBe(
        '/api/b/live/101/recordings/a.flv/audio?startMs=0&endMs=1000',
      )
    })
  })

  describe('Cookie Service', () => {
    describe('listCookieAccounts', () => {
      it('should fetch cookie account list via /cookie/accounts', async () => {
        const mockData = {
          items: [
            { accountId: 'acc-1', status: 'active', mid: 123, uname: 'user1' },
          ],
        }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockData),
        })

        const result = await listCookieAccounts()

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/accounts'),
          expect.objectContaining({}),
        )
        expect(result).toEqual(mockData.items)
      })

      it('should return empty array when items is undefined', async () => {
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve({}),
        })

        const result = await listCookieAccounts()
        expect(result).toEqual([])
      })
    })

    describe('createCookieAccount', () => {
      it('should POST to /cookie/accounts', async () => {
        const mockResponse = {
          accountId: 'acc-1',
          status: 'pending_login',
          qrCodeImage: 'base64data',
          expiresAt: '2024-01-01T00:05:00Z',
        }
        mockFetch.mockResolvedValue({
          ok: true,
          status: 202,
          json: () => Promise.resolve(mockResponse),
        })

        const result = await createCookieAccount()

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/accounts'),
          expect.objectContaining({ method: 'POST' }),
        )
        expect(result.accountId).toBe('acc-1')
        expect(result.status).toBe('pending_login')
      })

      it('should throw error on 429 response', async () => {
        mockFetch.mockResolvedValue({
          ok: false,
          status: 429,
          statusText: 'Too Many Requests',
          text: () => Promise.resolve('Too many pending login sessions'),
        })

        const err = await createCookieAccount().catch(e => e)
        expect(err.status).toBe(429)
      })
    })

    describe('getCookieAccount', () => {
      it('should GET /cookie/accounts/{accountId}', async () => {
        const mockResponse = {
          accountId: 'acc-1',
          status: 'active',
          mid: 123,
          uname: 'user1',
        }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockResponse),
        })

        const result = await getCookieAccount('acc-1')

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/accounts/acc-1'),
          expect.objectContaining({}),
        )
        expect(result.status).toBe('active')
      })

      it('should throw error on 404 response', async () => {
        mockFetch.mockResolvedValue({
          ok: false,
          status: 404,
          statusText: 'Not Found',
          text: () => Promise.resolve('Account not found'),
        })

        await expect(getCookieAccount('nonexistent')).rejects.toThrow(
          'API request failed',
        )
      })
    })

    describe('deleteCookieAccount', () => {
      it('should DELETE /cookie/accounts/{accountId}', async () => {
        mockFetch.mockResolvedValue({
          ok: true,
          status: 204,
        })

        await deleteCookieAccount('acc-1')

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/accounts/acc-1'),
          expect.objectContaining({ method: 'DELETE' }),
        )
      })
    })

    describe('listAvailableCookies', () => {
      it('should fetch available cookies via /cookie/available', async () => {
        const mockData = {
          items: [
            { cookieId: 'c1', status: 'active' },
          ],
        }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockData),
        })

        const result = await listAvailableCookies()

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/available'),
          expect.objectContaining({}),
        )
        expect(result).toEqual(mockData.items)
      })
    })

    describe('listWeiboCookieAccounts', () => {
      it('should fetch weibo account list via /cookie/weibo/accounts', async () => {
        const mockData = {
          items: [
            { accountId: 'w-1', status: 'active', platform: 'weibo', mid: 456, uname: '微博用户' },
          ],
        }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockData),
        })

        const result = await listWeiboCookieAccounts()

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/accounts'),
          expect.objectContaining({}),
        )
        expect(result).toEqual(mockData.items)
      })

      it('should return empty array when items is undefined', async () => {
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve({}),
        })

        const result = await listWeiboCookieAccounts()
        expect(result).toEqual([])
      })
    })

    describe('createWeiboCookieAccount', () => {
      it('should POST to /cookie/weibo/accounts', async () => {
        const mockResponse = {
          accountId: 'w-1',
          status: 'pending_login',
          qrCodeImage: 'base64data',
          expiresAt: '2024-01-01T00:05:00Z',
        }
        mockFetch.mockResolvedValue({
          ok: true,
          status: 202,
          json: () => Promise.resolve(mockResponse),
        })

        const result = await createWeiboCookieAccount()

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/accounts'),
          expect.objectContaining({ method: 'POST' }),
        )
        expect(result.accountId).toBe('w-1')
        expect(result.status).toBe('pending_login')
      })
    })

    describe('getWeiboCookieAccount', () => {
      it('should GET /cookie/weibo/accounts/{accountId}', async () => {
        const mockResponse = {
          accountId: 'w-1',
          status: 'active',
          mid: 456,
          uname: '微博用户',
        }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockResponse),
        })

        const result = await getWeiboCookieAccount('w-1')

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/accounts/w-1'),
          expect.objectContaining({}),
        )
        expect(result.status).toBe('active')
      })

      it('should throw error on 404 response', async () => {
        mockFetch.mockResolvedValue({
          ok: false,
          status: 404,
          statusText: 'Not Found',
          text: () => Promise.resolve('Account not found'),
        })

        await expect(getWeiboCookieAccount('nonexistent')).rejects.toThrow(
          'API request failed',
        )
      })
    })

    describe('deleteWeiboCookieAccount', () => {
      it('should DELETE /cookie/weibo/accounts/{accountId}', async () => {
        mockFetch.mockResolvedValue({
          ok: true,
          status: 204,
        })

        await deleteWeiboCookieAccount('w-1')

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/accounts/w-1'),
          expect.objectContaining({ method: 'DELETE' }),
        )
      })
    })

    describe('listAvailableWeiboCookies', () => {
      it('should fetch available weibo cookies via /cookie/weibo/available', async () => {
        const mockData = {
          items: [
            { cookieId: 'c1', status: 'active' },
          ],
        }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockData),
        })

        const result = await listAvailableWeiboCookies()

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/available'),
          expect.objectContaining({}),
        )
        expect(result).toEqual(mockData.items)
      })
    })

    describe('checkoutWeiboCookie', () => {
      it('should POST /cookie/weibo/checkout', async () => {
        const mockData = { cookieId: 'c1', accountId: 'a1', cookie: 'SUB=x' }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockData),
        })

        const result = await checkoutWeiboCookie()

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/checkout'),
          expect.objectContaining({ method: 'POST' }),
        )
        expect(result).toEqual(mockData)
      })
    })

    describe('reportInvalidWeiboCookie', () => {
      it('should POST /cookie/weibo/report-invalid with cookieId', async () => {
        const mockData = { cookieId: 'c1', cookie: 'SUB=new', refreshed: true }
        mockFetch.mockResolvedValue({
          ok: true,
          json: () => Promise.resolve(mockData),
        })

        const result = await reportInvalidWeiboCookie('c1')

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/report-invalid'),
          expect.objectContaining({
            method: 'POST',
            body: JSON.stringify({ cookieId: 'c1' }),
          }),
        )
        expect(result).toEqual(mockData)
      })
    })

    describe('cooldownWeiboCookie', () => {
      it('should POST /cookie/weibo/cooldown with cookieId and duration', async () => {
        mockFetch.mockResolvedValue({ ok: true, status: 204 })

        await cooldownWeiboCookie('c1', 300)

        expect(mockFetch).toHaveBeenCalledWith(
          expect.stringContaining('/cookie/weibo/cooldown'),
          expect.objectContaining({
            method: 'POST',
            body: JSON.stringify({ cookieId: 'c1', durationSeconds: 300 }),
          }),
        )
      })
    })
  })

  describe('Transcript', () => {
    it('listTranscriptRecordings should GET /recordings with filters', async () => {
      const items = [
        { id: 'r1', roomId: 101, mid: 1, title: '晚间歌回', files: [], startedAt: '2026-09-16T12:00:00Z' },
      ]
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items }) })

      const result = await listTranscriptRecordings({ roomId: 101, limit: 20 })

      const urlArg = mockFetch.mock.calls[0][0] as string
      expect(urlArg).toContain('/recordings')
      expect(urlArg).toContain('roomId=101')
      expect(urlArg).toContain('limit=20')
      expect(result).toEqual(items)
    })

    it('getTranscriptCandidateRooms should GET /transcripts/candidate-rooms', async () => {
      const items = [
        { roomId: 101, mid: 1, anchorName: '主播A' },
        { roomId: 202, mid: 2, anchorName: null },
      ]
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items }) })

      const result = await getTranscriptCandidateRooms()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/candidate-rooms'),
        expect.anything(),
      )
      expect(result).toEqual(items)
    })

    it('getTranscriptCandidateRooms should return empty array when items undefined', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({}) })

      expect(await getTranscriptCandidateRooms()).toEqual([])
    })

    it('deleteTranscriptSettings should DELETE /recordings/{roomId}/transcripts/settings', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 204 })

      await deleteTranscriptSettings(101)

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/recordings/101/transcripts/settings'),
        expect.objectContaining({ method: 'DELETE' }),
      )
    })

    it('enqueueTranscript should POST /transcripts/enqueue with recordingId and modelId', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ taskId: 't1', status: 'PENDING' }),
      })

      const result = await enqueueTranscript('r1', 'm1')

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/enqueue'),
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ recordingId: 'r1', modelId: 'm1' }),
        }),
      )
      expect(result).toEqual({ taskId: 't1', status: 'PENDING' })
    })

    it('listTranscriptTasks should GET /transcripts/task with filters', async () => {
      const items = [
        { id: 't1', status: 'RUNNING', roomId: 101, position: 1000 },
      ]
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items }) })

      const result = await listTranscriptTasks({ roomId: 101, status: 'RUNNING', limit: 50 })

      const urlArg = mockFetch.mock.calls[0][0] as string
      expect(urlArg).toContain('/transcripts/task')
      expect(urlArg).toContain('roomId=101')
      expect(urlArg).toContain('status=RUNNING')
      expect(urlArg).toContain('limit=50')
      expect(result).toEqual(items)
    })

    it('listTranscriptTasks should return empty array when items undefined', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({}) })

      expect(await listTranscriptTasks()).toEqual([])
    })

    it('getTranscriptTask should GET /transcripts/task/{taskId}', async () => {
      const detail = {
        task: { id: 't1', status: 'DONE' },
        segments: [{ start: 0, end: 1000, text: '你好' }],
      }
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(detail) })

      const result = await getTranscriptTask('t1')

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/task/t1'),
        expect.anything(),
      )
      expect(result).toEqual(detail)
    })

    it('retryTranscriptTask should POST /transcripts/task/{taskId}/retry', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ id: 't1', status: 'RUNNING' }),
      })

      await retryTranscriptTask('t1')

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/task/t1/retry'),
        expect.objectContaining({ method: 'POST' }),
      )
    })

    it('transcriptSseUrl should build the SSE endpoint url', () => {
      expect(transcriptSseUrl('t1')).toBe('/api/transcripts/task/t1/sse')
    })

    it('transcriptSubtitleUrl should build the subtitle download url with encoded taskId and format', () => {
      expect(transcriptSubtitleUrl('t 1', 'srt')).toBe(
        '/api/transcripts/task/t%201/subtitle?format=srt',
      )
      expect(transcriptSubtitleUrl('t1', 'ass')).toBe(
        '/api/transcripts/task/t1/subtitle?format=ass',
      )
    })

    it('openTranscriptStream should create an EventSource at the SSE url', () => {
      mockEventSource.mockClear()

      openTranscriptStream('t1')

      expect(mockEventSource).toHaveBeenCalledWith('/api/transcripts/task/t1/sse')
    })

    it('getTranscriptSettings should GET /transcripts/settings', async () => {
      const items = [{ roomId: 101, mid: 1, anchorName: '主播A', enabled: true }]
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items }) })

      const result = await getTranscriptSettings()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/settings'),
        expect.anything(),
      )
      expect(result).toEqual(items)
    })

    it('setRoomAutoEnqueue should POST /recordings/{roomId}/transcripts/settings with modelId', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ roomId: 101, enabled: true }),
      })

      await setRoomAutoEnqueue(101, true, 'm1')

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/recordings/101/transcripts/settings'),
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ enable: true, modelId: 'm1' }),
        }),
      )
    })

    it('setRoomAutoEnqueue should omit modelId when disabling', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ roomId: 101, enabled: false }),
      })

      await setRoomAutoEnqueue(101, false)

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/recordings/101/transcripts/settings'),
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ enable: false }),
        }),
      )
    })

    it('getRoomTranscripts should GET /recordings/{roomId}/transcripts', async () => {
      const view = { roomId: 101, autoEnqueueEnabled: true, items: [] }
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(view) })

      const result = await getRoomTranscripts(101)

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/recordings/101/transcripts'),
        expect.anything(),
      )
      expect(result).toEqual(view)
    })

    it('listTranscriptModels should GET /transcripts/models', async () => {
      const items = [{ id: 'm1', name: '默认模型', provider: 'ali-fun-asr', type: 'streaming' }]
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items }) })

      const result = await listTranscriptModels()

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/models'),
        expect.anything(),
      )
      expect(result).toEqual(items)
    })

    it('createTranscriptModel should POST /transcripts/models', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ id: 'm1', name: '默认模型' }),
      })

      await createTranscriptModel({
        name: '默认模型',
        provider: 'ali-fun-asr',
        type: 'streaming',
        apiKey: 'secret',
        model: 'fun-asr-realtime',
        enabled: true,
        maxConcurrency: 1,
      })

      const body = JSON.parse(mockFetch.mock.calls[0][1].body as string)
      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/models'),
        expect.objectContaining({ method: 'POST' }),
      )
      expect(body.apiKey).toBe('secret')
      expect(body.model).toBe('fun-asr-realtime')
    })

    it('updateTranscriptModel should PUT /transcripts/models/{modelId}', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ id: 'm1', enabled: false }),
      })

      await updateTranscriptModel('m1', { enabled: false, maxConcurrency: 2 })

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/models/m1'),
        expect.objectContaining({
          method: 'PUT',
          body: JSON.stringify({ enabled: false, maxConcurrency: 2 }),
        }),
      )
    })
  })
})
