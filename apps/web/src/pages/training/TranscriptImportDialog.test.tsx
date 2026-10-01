import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
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

async function pickTask(optionText: string) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: '识别任务' }))
  await waitFor(() => {
    expect(document.querySelectorAll('.ant-select-item-option').length).toBeGreaterThan(0)
  })
  const option = Array.from(document.querySelectorAll('.ant-select-item-option')).find((el) =>
    (el.getAttribute('title') ?? '').includes(optionText),
  )
  if (!option) throw new Error(`option not found: ${optionText}`)
  fireEvent.click(option)
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

  it('should render as a centred modal overlay above the page', async () => {
    mockListTasks.mockResolvedValue([doneTask])

    renderDialog()

    const dialog = await screen.findByRole('dialog', { name: '从 Transcript 导入' })
    expect(dialog).toBeInTheDocument()
    // antd Modal：遮罩层 fixed + 半透明
    const wrap = document.querySelector('.ant-modal-wrap') as HTMLElement
    expect(wrap).not.toBeNull()
    expect(wrap.style.position === 'fixed' || getComputedStyle(wrap).position === 'fixed').toBe(true)
  })

  it('should close on cancel but not import inside the panel', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    const props = renderDialog()

    await screen.findByRole('dialog', { name: '从 Transcript 导入' })
    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    expect(props.onClose).toHaveBeenCalledTimes(1)
    expect(mockImport).not.toHaveBeenCalled()
  })

  it('should ignore closing while the import is in flight so the result stays visible', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    let resolveImport: (value: { imported: number; skipped: number }) => void = () => {}
    mockImport.mockReturnValue(
      new Promise((resolve) => {
        resolveImport = resolve
      }),
    )
    renderDialog()

    await pickTask('晚间歌回')
    fireEvent.click(screen.getByRole('button', { name: '确认导入' }))

    // 导入中：关闭按钮禁用（保住结果反馈）
    expect(screen.getByRole('button', { name: '取消' })).toBeDisabled()

    resolveImport({ imported: 5, skipped: 1 })
    expect(await screen.findByText('已导入 5 条，跳过 1 条')).toBeInTheDocument()
  })

  it('should only offer transcripts whose status is DONE', async () => {
    mockListTasks.mockResolvedValue([doneTask, runningTask])

    renderDialog()

    await waitFor(() => {
      expect(mockListTasks).toHaveBeenCalledWith({ status: 'DONE' })
    })
    fireEvent.mouseDown(await screen.findByRole('combobox', { name: '识别任务' }))
    expect(await screen.findByTitle('晚间歌回（t1）')).toBeInTheDocument()
    expect(screen.queryByTitle('进行中场次（t2）')).not.toBeInTheDocument()
  })

  it('should import the selected transcript and report imported and skipped counts', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    mockImport.mockResolvedValue({ imported: 12, skipped: 3 })
    const props = renderDialog()

    await pickTask('晚间歌回')
    fireEvent.click(screen.getByRole('button', { name: '确认导入' }))

    await waitFor(() => {
      expect(mockImport).toHaveBeenCalledWith('d1', { taskId: 't1' })
    })
    expect(await screen.findByText('已导入 12 条，跳过 3 条')).toBeInTheDocument()
    expect(props.onImported).toHaveBeenCalledWith({ imported: 12, skipped: 3 })
    expect(props.onClose).not.toHaveBeenCalled()
  })

  it('should not send any import request when cancelled', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    const props = renderDialog()

    await screen.findByRole('combobox', { name: '识别任务' })
    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    expect(props.onClose).toHaveBeenCalledTimes(1)
    expect(mockImport).not.toHaveBeenCalled()
  })

  it('should keep the import button disabled until a task is selected', async () => {
    mockListTasks.mockResolvedValue([doneTask])

    renderDialog()

    await screen.findByRole('combobox', { name: '识别任务' })
    expect(screen.getByRole('button', { name: '确认导入' })).toBeDisabled()
  })

  it('should surface a readable message when the import fails with 400', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    mockImport.mockRejectedValue(apiError(400, '{"error":"task not done"}'))

    renderDialog()

    await pickTask('晚间歌回')
    fireEvent.click(screen.getByRole('button', { name: '确认导入' }))

    // 原始报文 {"error":"task not done"} 不能直接给用户看，须映射为可读文案
    expect(await screen.findByText('该识别任务尚未完成，无法导入')).toBeInTheDocument()
    expect(screen.queryByText(/已导入/)).not.toBeInTheDocument()
  })

  it('should surface a readable message when the transcript service is unavailable (502)', async () => {
    mockListTasks.mockResolvedValue([doneTask])
    mockImport.mockRejectedValue(
      apiError(502, '{"error":"transcript service unavailable"}'),
    )

    renderDialog()

    await pickTask('晚间歌回')
    fireEvent.click(screen.getByRole('button', { name: '确认导入' }))

    expect(await screen.findByText('识别服务暂不可用，请稍后重试')).toBeInTheDocument()
  })
})
