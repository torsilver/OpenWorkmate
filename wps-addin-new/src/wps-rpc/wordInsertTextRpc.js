/**
 * WPS 文字：文末插入文本（对齐 Chrome/Office CurrentDocument：word_insert_text 的 text、style）。
 * 对齐 office-addin/taskpane.js word_insert_text 的样式语义（Heading1/Normal/Title 等）。
 */

function unwrap(v) {
  if (v == null) return v
  if (typeof v === 'object' && v !== null && 'value' in v && v.value !== undefined) return unwrap(v.value)
  return v
}

/** @param {string} s */
function normalizeStyleKey(s) {
  return String(s || '')
    .trim()
    .toLowerCase()
    .replace(/[\s_\-]+/g, '')
}

/**
 * 每次 RPC 对应独立段落（对齐 Office.js insertParagraph）。
 * 若不在末尾加段落结束符，多次 InsertAfter 会挤在同一段内，段落样式以后一次为准，前面的 Title/Heading 会「全变成正文」。
 */
function ensureTrailingParagraphMarkForInsert(s) {
  let t = String(s).replace(/\n+$/, '')
  if (!t) return String(s)
  if (/\r$/.test(t)) return t
  return t + '\r'
}

/** Word 内置样式常量（与 VBA wdStyle* 一致），Enum 缺失时备用 */
const FALLBACK_WD_STYLE = {
  heading1: -2,
  heading2: -3,
  heading3: -4,
  normal: -1,
  title: -63,
  subtitle: -75
}

/**
 * @param {object} wps
 * @param {object} doc
 * @param {object} range WPS Range
 * @param {string} styleKeyNorm normalizeStyleKey 结果
 * @returns {boolean}
 */
function tryApplyParagraphStyle(wps, doc, range, styleKeyNorm) {
  if (!range || !styleKeyNorm) return false

  const E = wps.Enum || {}
  const builtinIds = [
    ['heading1', E.wdStyleHeading1, FALLBACK_WD_STYLE.heading1],
    ['heading2', E.wdStyleHeading2, FALLBACK_WD_STYLE.heading2],
    ['heading3', E.wdStyleHeading3, FALLBACK_WD_STYLE.heading3],
    ['normal', E.wdStyleNormal, FALLBACK_WD_STYLE.normal],
    ['title', E.wdStyleTitle, FALLBACK_WD_STYLE.title],
    ['subtitle', E.wdStyleSubtitle, FALLBACK_WD_STYLE.subtitle]
  ]

  for (const [k, enumVal, fallbackNum] of builtinIds) {
    if (styleKeyNorm !== k) continue
    const id = enumVal != null && enumVal !== undefined ? unwrap(enumVal) : fallbackNum
    if (id == null) continue
    try {
      const coll = doc.Styles
      const st = coll && typeof coll.Item === 'function' ? coll.Item(id) : coll(id)
      if (st) {
        range.Style = st
        return true
      }
    } catch {
      /* 继续 */
    }
    try {
      range.Style = id
      return true
    } catch {
      /* 继续 */
    }
  }

  const nameMap = {
    heading1: ['标题 1', 'Heading 1', '标题1'],
    heading2: ['标题 2', 'Heading 2', '标题2'],
    heading3: ['标题 3', 'Heading 3', '标题3'],
    normal: ['正文', 'Normal', '普通'],
    title: ['标题', 'Title'],
    subtitle: ['副标题', 'Subtitle']
  }
  const names = nameMap[styleKeyNorm]
  if (!names) return false
  for (const nm of names) {
    try {
      range.Style = nm
      return true
    } catch {
      /* 继续 */
    }
  }
  return false
}

/**
 * @param {object} wps window.wps
 * @param {object} params text、style（可选，如 Heading1、Normal、Title）
 * @returns {string} 成功文案（若样式未应用会附带说明）
 */
export function wordInsertTextWps(wps, params) {
  const text = params.text != null ? String(params.text) : ''
  const styleRaw = params.style != null ? String(params.style).trim() : ''
  const styleKeyNorm = styleRaw ? normalizeStyleKey(styleRaw) : ''

  if (!(wps.Enum && wps.Enum.wdStory != null)) {
    throw new Error('WPS 文字 API 需在 WPS 文字加载项中调用，请参考 WPS 开放平台文档。')
  }

  const doc = unwrap(wps.ActiveDocument)
  if (!doc || !doc.Content) {
    throw new Error('无法获取当前文档内容对象。')
  }

  if (!text) {
    return '成功：text 为空，未做插入。'
  }

  const textToInsert = ensureTrailingParagraphMarkForInsert(text)

  const wdCollapseEnd = wps.Enum.wdCollapseEnd != null ? wps.Enum.wdCollapseEnd : 0

  let applied = false
  let usedDuplicatePath = false

  try {
    const dup = doc.Content.Duplicate
    if (dup && typeof dup.Collapse === 'function' && typeof dup.InsertAfter === 'function') {
      dup.Collapse(wdCollapseEnd)
      dup.InsertAfter(textToInsert)
      usedDuplicatePath = true
      if (styleKeyNorm) {
        applied = tryApplyParagraphStyle(wps, doc, dup, styleKeyNorm)
      }
    }
  } catch {
    usedDuplicatePath = false
  }

  if (!usedDuplicatePath) {
    const beforeEnd = unwrap(doc.Content.End)
    doc.Content.InsertAfter(textToInsert)
    const afterEnd = unwrap(doc.Content.End)
    if (styleKeyNorm && typeof beforeEnd === 'number' && typeof afterEnd === 'number' && afterEnd >= beforeEnd) {
      const tryMk = (start, end) => {
        try {
          return doc.Range(start, end)
        } catch {
          return null
        }
      }
      const candidates = [tryMk(beforeEnd + 1, afterEnd), tryMk(beforeEnd, afterEnd)]
      for (const r of candidates) {
        if (r && tryApplyParagraphStyle(wps, doc, r, styleKeyNorm)) {
          applied = true
          break
        }
      }
      if (!applied) {
        try {
          const paras = doc.Paragraphs
          const n = unwrap(paras.Count)
          if (n > 0) {
            const lastPara = paras.Item(n)
            const pr = lastPara && lastPara.Range
            applied = tryApplyParagraphStyle(wps, doc, pr, styleKeyNorm)
          }
        } catch {
          applied = false
        }
      }
    }
  }

  let msg = '成功：已在当前 WPS 文档末尾插入内容。'
  if (styleRaw && !applied) {
    msg += '（未能应用段落样式「' + styleRaw + '」，请确认文档内置样式是否存在。）'
  }
  return msg
}
