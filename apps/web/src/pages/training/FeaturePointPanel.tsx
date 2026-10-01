import { useCallback, useEffect, useState, type CSSProperties } from 'react'
import {
  apiErrorMessage,
  clearFeaturePointModel,
  listTrainingFeaturePoints,
  listTrainingModels,
  setFeaturePointModel,
} from '../../api/client'
import type { components } from '../../api/schema-training'

type TrainingFeaturePoint = components['schemas']['TrainingFeaturePoint']
type TrainingModel = components['schemas']['TrainingModel']

const CAPABILITY_LABELS: Record<string, string> = {
  audio: '语音',
  video: '视频',
  text: '文本',
  image: '图片',
}

const cellStyle: CSSProperties = { textAlign: 'left', padding: '0.5rem' }

/** 能力集合 → 中文标签（空集合显示「未设置」）。 */
function capabilityLabels(capabilities: string[]): string {
  if (capabilities.length === 0) return '未设置'
  return capabilities.map((cap) => CAPABILITY_LABELS[cap] ?? cap).join('、')
}

/**
 * 「功能点」区块：枚举已登记功能点，展示其所需能力与当前模型三态，
 * 并支持为该功能点指定模型 / 恢复默认。候选模型按功能点能力过滤且仅取启用者。
 * 本区块自行承载加载与操作错误，不向模型设置主流程冒泡。
 */
export function FeaturePointPanel() {
  const [featurePoints, setFeaturePoints] = useState<TrainingFeaturePoint[]>([])
  const [candidates, setCandidates] = useState<Record<string, TrainingModel[]>>({})
  const [selection, setSelection] = useState<Record<string, string>>({})
  const [busySlug, setBusySlug] = useState('')
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    try {
      const list = await listTrainingFeaturePoints()
      setFeaturePoints(list)
      const entries = await Promise.all(
        list.map(async (fp) => {
          const models = await listTrainingModels(
            true,
            fp.inputCapabilities.join(','),
            fp.outputCapabilities.join(','),
          )
          return [fp.slug, models] as const
        }),
      )
      setCandidates(Object.fromEntries(entries))
      setError('')
    } catch (err) {
      setError(apiErrorMessage(err, '加载功能点失败'))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const handleSet = async (fp: TrainingFeaturePoint) => {
    const modelId = selection[fp.slug]
    if (!modelId || busySlug) return
    setBusySlug(fp.slug)
    setError('')
    try {
      await setFeaturePointModel(fp.slug, modelId)
      await load()
    } catch (err) {
      setError(apiErrorMessage(err, '指定模型失败'))
    } finally {
      setBusySlug('')
    }
  }

  const handleClear = async (fp: TrainingFeaturePoint) => {
    if (busySlug) return
    setBusySlug(fp.slug)
    setError('')
    try {
      await clearFeaturePointModel(fp.slug)
      await load()
    } catch (err) {
      setError(apiErrorMessage(err, '恢复默认失败'))
    } finally {
      setBusySlug('')
    }
  }

  return (
    <section className="feature-point-panel" style={{ marginTop: '2rem' }}>
      <h3>功能点</h3>

      {error && <div role="alert">{error}</div>}

      {!error && featurePoints.length === 0 && <p>暂无功能点</p>}

      {featurePoints.length > 0 && (
        <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
          <thead>
            <tr>
              <th style={cellStyle}>名称</th>
              <th style={cellStyle}>标识</th>
              <th style={cellStyle}>所需输入能力</th>
              <th style={cellStyle}>所需输出能力</th>
              <th style={cellStyle}>当前模型</th>
              <th style={cellStyle}>操作</th>
            </tr>
          </thead>
          <tbody>
            {featurePoints.map((fp) => {
              const rowCandidates = candidates[fp.slug] ?? []
              const unspecified = fp.specifiedModelId === null
              return (
                <tr key={fp.slug}>
                  <td style={cellStyle}>{fp.name}</td>
                  <td style={cellStyle}>{fp.slug}</td>
                  <td style={cellStyle}>{capabilityLabels(fp.inputCapabilities)}</td>
                  <td style={cellStyle}>{capabilityLabels(fp.outputCapabilities)}</td>
                  <td style={cellStyle}>
                    {unspecified ? (
                      <>
                        <span>未设置</span>
                        {fp.defaultModelSlug ? (
                          <span style={{ color: '#94a3b8', marginLeft: '0.25rem' }}>
                            （默认 {fp.defaultModelSlug}）
                          </span>
                        ) : (
                          <span style={{ color: '#94a3b8', marginLeft: '0.25rem' }}>无可用模型</span>
                        )}
                      </>
                    ) : fp.specifiedModelAvailable ? (
                      fp.specifiedModelSlug
                    ) : (
                      <>
                        <span>{fp.specifiedModelSlug}</span>
                        <span style={{ color: '#fca5a5', marginLeft: '0.5rem' }}>
                          指定模型不可用
                        </span>
                        {fp.defaultModelSlug && (
                          <span style={{ color: '#94a3b8', marginLeft: '0.5rem' }}>
                            回退默认 {fp.defaultModelSlug}
                          </span>
                        )}
                      </>
                    )}
                  </td>
                  <td style={cellStyle}>
                    {rowCandidates.length === 0 ? (
                      <span style={{ color: '#94a3b8' }}>
                        该功能点暂无可用模型，请先在模型设置中配置模型与能力
                      </span>
                    ) : (
                      <>
                        <select
                          aria-label={`指定模型 ${fp.slug}`}
                          style={{
                            padding: '0.25rem',
                            backgroundColor: '#334155',
                            color: '#e2e8f0',
                            border: '1px solid #475569',
                            borderRadius: '0.25rem',
                          }}
                          value={selection[fp.slug] ?? ''}
                          onChange={(event) =>
                            setSelection((prev) => ({ ...prev, [fp.slug]: event.target.value }))
                          }
                        >
                          <option value="">请选择模型</option>
                          {rowCandidates.map((model) => (
                            <option key={model.id} value={model.id}>
                              {model.slug}
                            </option>
                          ))}
                        </select>
                        <button
                          aria-label={`指定 ${fp.slug}`}
                          onClick={() => void handleSet(fp)}
                          disabled={!selection[fp.slug] || busySlug === fp.slug}
                          style={{ marginLeft: '0.5rem' }}
                        >
                          指定
                        </button>
                      </>
                    )}
                    {!unspecified && (
                      <button
                        aria-label={`恢复默认 ${fp.slug}`}
                        onClick={() => void handleClear(fp)}
                        disabled={busySlug === fp.slug}
                        style={{ marginLeft: '0.5rem' }}
                      >
                        恢复默认
                      </button>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </section>
  )
}
