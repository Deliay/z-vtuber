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
  apiErrorMessage,
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
  updateTranscriptTaskOffset,
  transcriptSseUrl,
  transcriptSubtitleUrl,
  openTranscriptStream,
  getTranscriptSettings,
  setRoomAutoEnqueue,
  getRoomTranscripts,
  listTranscriptModels,
  createTranscriptModel,
  updateTranscriptModel,
  listTrainingDatasets,
  createTrainingDataset,
  renameTrainingDataset,
  deleteTrainingDataset,
  listTrainingDatasetItems,
  importTrainingDatasetItems,
  updateTrainingDatasetItem,
  batchDeleteTrainingDatasetItems,
  trainingAudioClipUrl,
  listTrainingProviders,
  createTrainingProvider,
  updateTrainingProvider,
  deleteTrainingProvider,
  discoverTrainingModels,
  importTrainingModels,
  listTrainingModels,
  createTrainingModel,
  updateTrainingModel,
  deleteTrainingModel,
  recognizeTrainingDatasetItem,
  createEmotionTasks,
  listEmotionTasks,
  retryEmotionTask,
  listTrainingFeaturePoints,
  resolveFeaturePointModel,
  setFeaturePointModel,
  clearFeaturePointModel,
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

    it('updateTranscriptTaskOffset should PUT /transcripts/task/{taskId}/offset with offsetMs', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ id: 't1', offsetMs: 2500 }),
      })

      const result = await updateTranscriptTaskOffset('t1', 2500)

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/transcripts/task/t1/offset'),
        expect.objectContaining({
          method: 'PUT',
          body: JSON.stringify({ offsetMs: 2500 }),
        }),
      )
      expect(result).toEqual({ id: 't1', offsetMs: 2500 })
    })

    it('updateTranscriptTaskOffset should throw ApiError with the server message on 400', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        text: () => Promise.resolve('{"error":"offsetMs out of range"}'),
      })

      const error = (await updateTranscriptTaskOffset('t1', 99999999).catch((e) => e)) as {
        status?: number
        serverMessage?: string
      }

      expect(error.status).toBe(400)
      expect(error.serverMessage).toContain('offsetMs out of range')
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

  describe('Training', () => {
    const dataset = {
      id: 'd1',
      name: '歌回语料',
      type: 'audio',
      createdAt: '2026-09-27T00:00:00Z',
      updatedAt: '2026-09-27T00:00:00Z',
    }

    const item = {
      id: 'i1',
      datasetId: 'd1',
      audioType: 'transcript',
      source: 't1',
      audioUri: 'b/live/101/recordings/a.flv',
      audioKey: 'transcript:t1:0',
      startMs: 0,
      endMs: 60000,
      text: '第一句',
      extensions: null,
      createdAt: '2026-09-27T00:00:00Z',
      updatedAt: '2026-09-27T00:00:00Z',
    }

    it('listTrainingDatasets should GET /training/datasets with the audio type filter', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ items: [dataset] }),
      })

      const result = await listTrainingDatasets()

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets?type=audio',
        expect.objectContaining({
          headers: expect.objectContaining({ 'Content-Type': 'application/json' }),
        }),
      )
      expect(result).toEqual([dataset])
    })

    it('listTrainingDatasets should return an empty array when items is undefined', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({}) })

      expect(await listTrainingDatasets()).toEqual([])
    })

    it('createTrainingDataset should POST /training/datasets with name and audio type', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        status: 201,
        json: () => Promise.resolve(dataset),
      })

      const result = await createTrainingDataset({ name: '歌回语料', type: 'audio' })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ name: '歌回语料', type: 'audio' }),
        }),
      )
      expect(result).toEqual(dataset)
    })

    it('renameTrainingDataset should PUT /training/datasets/{datasetId}', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ ...dataset, name: '新名字' }),
      })

      await renameTrainingDataset('d1', { name: '新名字' })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1',
        expect.objectContaining({
          method: 'PUT',
          body: JSON.stringify({ name: '新名字' }),
        }),
      )
    })

    it('deleteTrainingDataset should DELETE /training/datasets/{datasetId}', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 204 })

      await deleteTrainingDataset('d1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1',
        expect.objectContaining({ method: 'DELETE' }),
      )
    })

    it('listTrainingDatasetItems should GET items with optional filters', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ items: [item] }),
      })

      const result = await listTrainingDatasetItems('d1', {
        audioType: 'transcript',
        source: 't1',
      })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1/items?audioType=transcript&source=t1',
        expect.anything(),
      )
      expect(result).toEqual([item])

      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items: [] }) })
      await listTrainingDatasetItems('d1')
      expect(mockFetch).toHaveBeenLastCalledWith(
        '/api/training/datasets/d1/items',
        expect.anything(),
      )
    })

    it('importTrainingDatasetItems should POST .../items/import with the taskId body', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ imported: 12, skipped: 3 }),
      })

      const result = await importTrainingDatasetItems('d1', { taskId: 't1' })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1/items/import',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ taskId: 't1' }),
        }),
      )
      expect(result).toEqual({ imported: 12, skipped: 3 })
    })

    it('updateTrainingDatasetItem should serialize an explicit endMs null instead of dropping it', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ ...item, endMs: null }),
      })

      const result = await updateTrainingDatasetItem('d1', 'i1', { endMs: null })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1/items/i1',
        expect.objectContaining({ method: 'PUT', body: '{"endMs":null}' }),
      )
      expect(result.endMs).toBeNull()
    })

    it('updateTrainingDatasetItem should only send the provided fields', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(item) })

      await updateTrainingDatasetItem('d1', 'i1', { text: '修订后', startMs: 90000 })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1/items/i1',
        expect.objectContaining({
          method: 'PUT',
          body: JSON.stringify({ text: '修订后', startMs: 90000 }),
        }),
      )
    })

    it('updateTrainingDatasetItem should throw ApiError carrying the server message on 400', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        text: () => Promise.resolve('{"error":"invalid time range"}'),
      })

      const error = (await updateTrainingDatasetItem('d1', 'i1', {
        startMs: 0,
        endMs: -1,
      }).catch((e) => e)) as { status?: number; serverMessage?: string }

      expect(error.status).toBe(400)
      expect(error.serverMessage).toContain('invalid time range')
    })

    it('batchDeleteTrainingDatasetItems should POST .../batch-delete with the itemIds body', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ deleted: 2 }),
      })

      const result = await batchDeleteTrainingDatasetItems('d1', ['i1', 'i2'])

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1/items/batch-delete',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ itemIds: ['i1', 'i2'] }),
        }),
      )
      expect(result).toEqual({ deleted: 2 })
    })

    it('trainingAudioClipUrl should build the clip url from a recording uri', () => {
      expect(trainingAudioClipUrl('b/live/101/recordings/a.flv', 1000, 2500)).toBe(
        '/api/b/live/101/recordings/a.flv/audio?startMs=1000&endMs=2500',
      )
    })

    it('trainingAudioClipUrl should fall back to a 5 minute preview window when endMs is empty', () => {
      expect(trainingAudioClipUrl('b/live/101/recordings/a b.flv', 1000, null)).toBe(
        '/api/b/live/101/recordings/a%20b.flv/audio?startMs=1000&endMs=301000',
      )
      expect(trainingAudioClipUrl('b/live/101/recordings/a.flv', 500, undefined)).toBe(
        '/api/b/live/101/recordings/a.flv/audio?startMs=500&endMs=300500',
      )
      expect(trainingAudioClipUrl('b/live/101/recordings/a.flv', 500, 0)).toBe(
        '/api/b/live/101/recordings/a.flv/audio?startMs=500&endMs=300500',
      )
    })

    it('trainingAudioClipUrl should return null for an unparseable uri', () => {
      expect(trainingAudioClipUrl('', 0, null)).toBeNull()
      expect(trainingAudioClipUrl('b/live/101/recordings/', 0, null)).toBeNull()
      expect(trainingAudioClipUrl('b/live/abc/recordings/a.flv', 0, null)).toBeNull()
      expect(trainingAudioClipUrl('https://example.com/a.flv', 0, null)).toBeNull()
    })
  })

  describe('Training Providers & Models', () => {
    const provider = {
      id: 'p1',
      name: 'openai-prod',
      type: 'openai',
      endpoint: 'https://api.openai.com/v1',
      apiKeySet: true,
      createdAt: '2026-09-27T00:00:00Z',
      updatedAt: '2026-09-27T00:00:00Z',
    }

    const model = {
      id: 'm1',
      providerId: 'p1',
      providerName: 'openai-prod',
      model: 'gpt-4o',
      slug: 'openai-prod/gpt-4o',
      enabled: true,
      inputCapabilities: ['audio', 'text'],
      outputCapabilities: ['text'],
      createdAt: '2026-09-27T00:00:00Z',
      updatedAt: '2026-09-27T00:00:00Z',
    }

    it('listTrainingProviders should GET /training/providers', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items: [provider] }) })

      const result = await listTrainingProviders()

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/providers',
        expect.anything(),
      )
      expect(result).toEqual([provider])
    })

    it('createTrainingProvider should POST /training/providers with the body', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 201, json: () => Promise.resolve(provider) })

      const result = await createTrainingProvider({
        name: 'openai-prod',
        type: 'openai',
        endpoint: 'https://api.openai.com/v1',
        apiKey: 'sk-secret',
      })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/providers',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({
            name: 'openai-prod',
            type: 'openai',
            endpoint: 'https://api.openai.com/v1',
            apiKey: 'sk-secret',
          }),
        }),
      )
      expect(result).toEqual(provider)
    })

    it('updateTrainingProvider should PUT /training/providers/{id} with the partial body', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(provider) })

      await updateTrainingProvider('p1', { name: 'renamed' })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/providers/p1',
        expect.objectContaining({ method: 'PUT', body: JSON.stringify({ name: 'renamed' }) }),
      )
    })

    it('deleteTrainingProvider should DELETE /training/providers/{id}', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 204 })

      await deleteTrainingProvider('p1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/providers/p1',
        expect.objectContaining({ method: 'DELETE' }),
      )
    })

    it('discoverTrainingModels should GET .../models/discover and return the model list', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ models: [{ model: 'gpt-4o' }, { model: 'gpt-4o-mini' }] }),
      })

      const result = await discoverTrainingModels('p1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/providers/p1/models/discover',
        expect.anything(),
      )
      expect(result).toEqual([{ model: 'gpt-4o' }, { model: 'gpt-4o-mini' }])
    })

    it('importTrainingModels should POST .../models/import with the models body', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ imported: 1, skipped: 1 }),
      })

      const result = await importTrainingModels('p1', ['gpt-4o', 'gpt-4o-mini'])

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/providers/p1/models/import',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ models: ['gpt-4o', 'gpt-4o-mini'] }),
        }),
      )
      expect(result).toEqual({ imported: 1, skipped: 1 })
    })

    it('listTrainingModels should append enabled only when defined', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items: [model] }) })

      const result = await listTrainingModels(true)

      expect(mockFetch).toHaveBeenLastCalledWith(
        '/api/training/models?enabled=true',
        expect.anything(),
      )
      expect(result).toEqual([model])

      await listTrainingModels()
      expect(mockFetch).toHaveBeenLastCalledWith('/api/training/models', expect.anything())

      await listTrainingModels(false)
      expect(mockFetch).toHaveBeenLastCalledWith(
        '/api/training/models?enabled=false',
        expect.anything(),
      )
    })

    it('createTrainingModel should POST /training/models with providerId and model', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 201, json: () => Promise.resolve(model) })

      await createTrainingModel({ providerId: 'p1', model: 'gpt-4o' })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/models',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ providerId: 'p1', model: 'gpt-4o' }),
        }),
      )
    })

    it('updateTrainingModel should PUT /training/models/{id}', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(model) })

      await updateTrainingModel('m1', { enabled: false })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/models/m1',
        expect.objectContaining({ method: 'PUT', body: JSON.stringify({ enabled: false }) }),
      )
    })

    it('deleteTrainingModel should DELETE /training/models/{id}', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 204 })

      await deleteTrainingModel('m1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/models/m1',
        expect.objectContaining({ method: 'DELETE' }),
      )
    })

    it('recognizeTrainingDatasetItem should POST .../recognize with modelId and return text', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ text: '识别文本' }) })

      const result = await recognizeTrainingDatasetItem('d1', 'i1', 'm1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1/items/i1/recognize',
        expect.objectContaining({ method: 'POST', body: JSON.stringify({ modelId: 'm1' }) }),
      )
      expect(result).toEqual({ text: '识别文本' })
    })

    it('createEmotionTasks should POST .../items/emotion-tasks with itemIds and modelId', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ created: 2, skipped: 1 }),
      })

      const result = await createEmotionTasks('d1', ['i1', 'i2', 'i3'], 'm1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/datasets/d1/items/emotion-tasks',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ itemIds: ['i1', 'i2', 'i3'], modelId: 'm1' }),
        }),
      )
      expect(result).toEqual({ created: 2, skipped: 1 })
    })

    it('listEmotionTasks should build the query string and omit empty params', async () => {
      const counts = { pending: 1, running: 0, done: 2, failed: 3 }
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ items: [], counts }),
      })

      const result = await listEmotionTasks({ datasetId: 'd1', status: 'failed', limit: 50 })

      const url = mockFetch.mock.calls[0][0] as string
      expect(url).toContain('/api/training/emotion-tasks?')
      expect(url).toContain('datasetId=d1')
      expect(url).toContain('status=failed')
      expect(url).toContain('limit=50')
      expect(result).toEqual({ items: [], counts })

      await listEmotionTasks()
      expect(mockFetch).toHaveBeenLastCalledWith(
        '/api/training/emotion-tasks',
        expect.anything(),
      )
    })

    it('retryEmotionTask should POST /training/emotion-tasks/{id}/retry', async () => {
      const task = {
        id: 't1',
        datasetId: 'd1',
        itemId: 'i1',
        audioKey: 'transcript:t1:0',
        modelId: 'm1',
        modelSlug: 'openai-prod/gpt-4o',
        status: 'pending',
        attempts: 2,
        error: null,
        emotion: null,
        createdAt: '2026-09-27T00:00:00Z',
        startedAt: null,
        finishedAt: null,
      }
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(task) })

      const result = await retryEmotionTask('t1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/emotion-tasks/t1/retry',
        expect.objectContaining({ method: 'POST' }),
      )
      expect(result).toEqual(task)
    })
  })

  describe('Training Feature Points & Capabilities', () => {
    const model = {
      id: 'm1',
      providerId: 'p1',
      providerName: 'openai-prod',
      model: 'gpt-4o',
      slug: 'openai-prod/gpt-4o',
      enabled: true,
      inputCapabilities: ['audio', 'text'],
      outputCapabilities: ['text'],
      createdAt: '2026-09-27T00:00:00Z',
      updatedAt: '2026-09-27T00:00:00Z',
    }

    const featurePoint = {
      slug: 'dataset-clean',
      name: '数据清洗',
      inputCapabilities: ['audio', 'text'],
      outputCapabilities: ['text'],
      specifiedModelId: null,
      specifiedModelSlug: null,
      specifiedModelAvailable: false,
      defaultModelId: 'm1',
      defaultModelSlug: 'openai-prod/gpt-4o',
      registeredAt: '2026-10-01T00:00:00Z',
      updatedAt: '2026-10-01T00:00:00Z',
    }

    it('listTrainingModels should append the input/output capability queries when non-empty', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({ items: [model] }) })

      await listTrainingModels(true, 'audio,text', 'text')

      expect(mockFetch).toHaveBeenLastCalledWith(
        '/api/training/models?enabled=true&input=audio%2Ctext&output=text',
        expect.anything(),
      )

      // 空字符串能力视为缺省，不拼 query，保持既有 listTrainingModels(true) 兼容
      await listTrainingModels(true, '', '')
      expect(mockFetch).toHaveBeenLastCalledWith(
        '/api/training/models?enabled=true',
        expect.anything(),
      )
    })

    it('listTrainingFeaturePoints should GET /training/feature-points and return data.items', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ items: [featurePoint] }),
      })

      const result = await listTrainingFeaturePoints()

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/feature-points',
        expect.anything(),
      )
      expect(result).toEqual([featurePoint])
    })

    it('listTrainingFeaturePoints should return an empty array when items is undefined', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve({}) })

      expect(await listTrainingFeaturePoints()).toEqual([])
    })

    it('resolveFeaturePointModel should POST the resolve body and return the resolved model', async () => {
      const resolved = {
        slug: 'dataset-clean',
        modelId: 'm1',
        modelSlug: 'openai-prod/gpt-4o',
        source: 'default',
      }
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(resolved) })

      const result = await resolveFeaturePointModel('dataset-clean', {
        name: '数据清洗',
        inputCapabilities: ['audio', 'text'],
        outputCapabilities: ['text'],
      })

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/feature-points/dataset-clean/model/resolve',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({
            name: '数据清洗',
            inputCapabilities: ['audio', 'text'],
            outputCapabilities: ['text'],
          }),
        }),
      )
      expect(result).toEqual(resolved)
    })

    it('setFeaturePointModel should PUT the modelId and encode the slug', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ ...featurePoint, specifiedModelId: 'm1' }),
      })

      const result = await setFeaturePointModel('data clean', 'm1')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/feature-points/data%20clean/model',
        expect.objectContaining({
          method: 'PUT',
          body: JSON.stringify({ modelId: 'm1' }),
        }),
      )
      expect(result.specifiedModelId).toBe('m1')
    })

    it('clearFeaturePointModel should DELETE the feature point model assignment', async () => {
      mockFetch.mockResolvedValue({ ok: true, json: () => Promise.resolve(featurePoint) })

      await clearFeaturePointModel('dataset-clean')

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/training/feature-points/dataset-clean/model',
        expect.objectContaining({ method: 'DELETE' }),
      )
    })
  })

  describe('apiErrorMessage', () => {
    it('should extract the error field from a JSON error body and map it to a readable message', () => {
      expect(apiErrorMessage({ serverMessage: '{"error":"task not done"}' })).toBe(
        '该识别任务尚未完成，无法导入',
      )
      expect(apiErrorMessage({ serverMessage: '{"error":"dataset name exists"}' })).toBe(
        '数据集名字已存在',
      )
      expect(apiErrorMessage({ serverMessage: '{"error":"invalid time range"}' })).toBe(
        '时间区间非法（开始不小于 0，结束须晚于开始）',
      )
      expect(
        apiErrorMessage({ serverMessage: '{"error":"transcript service unavailable"}' }),
      ).toBe('识别服务暂不可用，请稍后重试')
    })

    it('should map the new training provider/model/task error codes', () => {
      const cases: [string, string][] = [
        ['provider name exists', 'Provider 名称已存在'],
        ['provider not found', 'Provider 不存在'],
        ['unsupported provider type', '不支持的模型类型'],
        ['invalid endpoint', '服务地址非法'],
        ['api key is required', 'API key 必填'],
        ['model exists', '模型已存在'],
        ['model not found', '模型不存在'],
        ['model is required', '模型名必填'],
        ['model too long', '模型名过长'],
        ['model disabled', '模型已停用'],
        ['model does not support audio input', '该模型不支持音频输入'],
        ['audio uri is empty', '音频地址为空'],
        ['audio too large', '音频过大'],
        ['audio unavailable', '音频暂不可用'],
        ['model service unavailable', '模型服务不可用'],
        ['invalid model output', '模型输出无法解析'],
        ['itemIds is required', '请选择要识别的条目'],
        ['provider service unavailable', '模型服务不可用'],
        ['invalid provider response', '模型服务响应非法'],
        ['task not failed', '任务未处于失败状态'],
        ['task not found', '任务不存在'],
        ['unsupported capability', '不支持的能力'],
        ['model not qualified', '该模型不满足功能点能力要求'],
        ['feature point not found', '功能点不存在'],
        ['feature point slug is required', '功能点标识不能为空'],
        ['feature point slug too long', '功能点标识过长'],
      ]
      for (const [code, message] of cases) {
        expect(apiErrorMessage({ serverMessage: JSON.stringify({ error: code }) })).toBe(message)
      }
    })

    it('should keep an unmapped error code or a non-JSON body as-is', () => {
      expect(apiErrorMessage({ serverMessage: '{"error":"some future error"}' })).toBe(
        'some future error',
      )
      expect(apiErrorMessage({ serverMessage: 'plain text from gateway' })).toBe(
        'plain text from gateway',
      )
    })

    it('should fall back when there is no server message', () => {
      expect(apiErrorMessage(undefined)).toBe('操作失败')
      expect(apiErrorMessage({}, '导入失败')).toBe('导入失败')
      expect(apiErrorMessage({ serverMessage: '' }, '导入失败')).toBe('导入失败')
    })
  })
})
