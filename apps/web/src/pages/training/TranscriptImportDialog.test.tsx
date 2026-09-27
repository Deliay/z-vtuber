import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { TranscriptImportDialog } from './TranscriptImportDialog'
import * as client from '../../api/client'

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>()
  return {
    ...actual,
    listTranscriptTasks: vi.fn(),
    importTrainingDatasetItems: vi.fn(),
  }
})

const mockListTasks = vi.mocked(client.listTranscriptTasks)
const mockImport = vi.mocked(client.importTrainingDatasetItems)

type TaskItem = Awaited<ReturnType<typeof client.listTranscriptTasks>>[number]

function apiError(status: number, serverMessage: string): Error {
  return Object.assign(new Error('API request failed'), { status, serverMessage })
}

const doneTask: TaskItem = {
  id: 't1',
  status: 'DONE',
  roomId: 101,
  title: '晚间歌回',
  offsetMs: 0,
}

const runningTask: TaskItem = {
  id: 't2',
  status: 'RUNNING',
  roomId: 102,
  title: '进行中场次',
  offsetMs: 0,
}

function renderDialog(overrides: Partial<Parameters<typeof TranscriptImportDialog>[0]> = {}) {
  const props = {
    datasetId: 'd1',
    onClose: vi.fn(),
    onImported: vi.fn(),
    ...overrides,
  }
  render(<TranscriptImportDialog {...props} />)
  return props
}

describe('TranscriptImportDialog', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mockListTasks.mockResolvedValue([])
    mockImport.mockResolvedValue({ imported: 0, skipped: 0 })
  })

  it('should only offer transcripts whose status is DONE', async () => {
    mockListTasks.mockResolvedValue([doneTask, runningTask])

    renderDialog()

    await waitFor(() => {
      expect(mockListTasks).toHaveBeenCalledWith({ status: 'DONE' })
    })
    const select = await screen.findByLabelText('识别任务')
    expect(within(select).getByRole('option', { name: '晚间歌回（t1）' })).toBeInTheDocument()
    expect(
      within(select).queryByRole('option', { name: '进行中场次（t2）' }),
    ).not.toBeInTheDocument()
  })

  it('should import the selected transcript and report imported and skipped counts', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    mockImport.mockResolvedValue({ imported: 12, skipped: 3 })
    const props = renderDialog()

    fireEvent.change(await screen.findByLabelText('识别任务'), { target: { value: 't1' } })
    fireEvent.click(screen.getByRole('button', { name: '确认导入' }))

    await waitFor(() => {
      expect(mockImport).toHaveBeenCalledWith('d1', { taskId: 't1' })
    })
    expect(await screen.findByRole('status')).toHaveTextContent('已导入 12 条，跳过 3 条')
    expect(props.onImported).toHaveBeenCalledWith({ imported: 12, skipped: 3 })
    expect(props.onClose).not.toHaveBeenCalled()
  })

  it('should not send any import request when cancelled', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    const props = renderDialog()

    await screen.findByLabelText('识别任务')
    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    expect(props.onClose).toHaveBeenCalledTimes(1)
    expect(mockImport).not.toHaveBeenCalled()
  })

  it('should keep the import button disabled until a task is selected', async () => {
    mockListTasks.mockResolvedValue([doneTask])

    renderDialog()

    await screen.findByLabelText('识别任务')
    expect(screen.getByRole('button', { name: '确认导入' })).toBeDisabled()
  })

  it('should surface a readable message when the import fails with 400', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    mockImport.mockRejectedValue(apiError(400, '{"error":"task not done"}'))

    renderDialog()

    fireEvent.change(await screen.findByLabelText('识别任务'), { target: { value: 't1' } })
    fireEvent.click(screen.getByRole('button', { name: '确认导入' }))

    // 原始报文 {"error":"task not done"} 不能直接给用户看，须映射为可读文案
    expect(await screen.findByRole('alert')).toHaveTextContent('该识别任务尚未完成，无法导入')
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it('should surface a readable message when the transcript service is unavailable (502)', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    mockImport.mockRejectedValue(
      apiError(502, '{"error":"transcript service unavailable"}'),
    )

    renderDialog()

    fireEvent.change(await screen.findByLabelText('识别任务'), { target: { value: 't1' } })
    fireEvent.click(screen.getByRole('button', { name: '确认导入' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('识别服务暂不可用，请稍后重试')
  })
})
