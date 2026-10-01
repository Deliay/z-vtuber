import { describe, it, expect, vi } from 'vitest'
import { screen, fireEvent } from '@testing-library/react'
import { renderWithProviders as render } from '../../test/render'
import { TranscriptsPage } from './TranscriptsPage'

vi.mock('./TaskListTab', () => ({
  TaskListTab: () => <div data-testid="task-list-tab">任务列表内容</div>,
}))

vi.mock('./SettingsTab', () => ({
  SettingsTab: () => <div data-testid="settings-tab">设置内容</div>,
}))

describe('TranscriptsPage', () => {
  it('should render two tabs and default to the task list tab', () => {
    render(<TranscriptsPage />)

    expect(screen.getByText('语音转写')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '任务列表' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '设置' })).toBeInTheDocument()
    expect(screen.getByTestId('task-list-tab')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '任务列表' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: '设置' })).toHaveAttribute('aria-selected', 'false')
  })

  it('should switch to the settings tab when clicked', () => {
    render(<TranscriptsPage />)

    fireEvent.click(screen.getByRole('tab', { name: '设置' }))

    expect(screen.getByTestId('settings-tab')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '设置' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.queryByTestId('task-list-tab')).not.toBeVisible()
  })

  it('should switch back to the task list tab', () => {
    render(<TranscriptsPage />)

    fireEvent.click(screen.getByRole('tab', { name: '设置' }))
    fireEvent.click(screen.getByRole('tab', { name: '任务列表' }))

    expect(screen.getByTestId('task-list-tab')).toBeVisible()
    expect(screen.queryByTestId('settings-tab')).not.toBeVisible()
  })
})
