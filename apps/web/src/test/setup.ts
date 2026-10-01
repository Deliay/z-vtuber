import '@testing-library/jest-dom'
import { vi } from 'vitest'

// antd 依赖 matchMedia（响应式栅格/useBreakpoint），jsdom 未实现，统一在测试环境补齐。
if (typeof window !== 'undefined' && !window.matchMedia) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }),
  })
}

// antd Table 的响应式列/滚动测量依赖 ResizeObserver，jsdom 未实现。
if (typeof window !== 'undefined' && !window.ResizeObserver) {
  class ResizeObserverStub {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  window.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver
  globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver
}

// antd 弹窗/表格可能出现：jsdom 未实现的 scrollTo。
if (typeof window !== 'undefined' && !window.HTMLElement.prototype.scrollTo) {
  window.HTMLElement.prototype.scrollTo = vi.fn()
}
