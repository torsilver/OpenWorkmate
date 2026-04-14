function unwrap(v) {
  if (v == null) return v
  if (typeof v === 'object' && v !== null && 'value' in v && v.value !== undefined) return unwrap(v.value)
  return v
}

/**
 * 判断当前 WPS 加载项宿主类型，用于 RPC 宿主守卫（对齐 Office 分组件思路）。
 */
export function getWpsHostKind(w) {
  if (!w) return 'none'
  try {
    const p = unwrap(w.ActivePresentation)
    if (p) return 'wpp'
  } catch {
    /* ignore */
  }
  try {
    const d = unwrap(w.ActiveDocument)
    if (d && d.Content) return 'word'
  } catch {
    /* ignore */
  }
  try {
    const book = unwrap(w.ActiveWorkbook)
    if (book) return 'et'
  } catch {
    /* ignore */
  }
  return 'unknown'
}

export function assertWpsHost(expected, actual, label) {
  if (actual === expected) return null
  if (expected === 'word')
    return `${label} 仅能在 WPS 文字中执行。当前宿主不是文字（请在 WPS 文字中打开任务窗格后重试）。`
  if (expected === 'et')
    return `${label} 仅能在 WPS 表格中执行。当前宿主不是表格（请在 WPS 表格中打开任务窗格后重试）。`
  if (expected === 'wpp')
    return `${label} 仅能在 WPS 演示中执行。当前宿主不是演示（请在 WPS 演示中打开任务窗格后重试）。`
  return `${label} 当前 WPS 宿主类型无法执行此操作。`
}
