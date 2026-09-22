export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) {
    return '未知大小'
  }

  if (bytes < 1024) {
    return `${bytes} B`
  }

  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 'B'
  for (const next of units) {
    if (value < 1024) {
      break
    }
    value /= 1024
    unit = next
  }

  return `${value.toFixed(1)} ${unit}`
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) {
    return '-'
  }

  const pad = (value: number) => String(value).padStart(2, '0')
  return [
    date.getFullYear(),
    pad(date.getMonth() + 1),
    pad(date.getDate()),
    pad(date.getHours()),
    pad(date.getMinutes()),
  ].join('-')
}

/** 毫秒 → `m:ss`（识别结果片段/进度时间展示）。 */
export function formatPosition(position?: number | null): string {
  if (position === undefined || position === null) {
    return '-'
  }
  const totalSeconds = Math.floor(position / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${String(seconds).padStart(2, '0')}`
}
