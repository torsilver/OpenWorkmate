/**
 * WPS 文字：插入表格（对齐 CurrentDocumentPlugin current_word_insert_table 参数）。
 */

function unwrap(v) {
  if (v == null) return v
  if (typeof v === 'object' && v !== null && 'value' in v && v.value !== undefined) return unwrap(v.value)
  return v
}

/**
 * @param {object} wps window.wps
 * @param {object} params rowCount, columnCount, values (二维数组，可选), insertLocation（可选）
 * @returns {string} 成功文案
 */
export function wordInsertTableWps(wps, params) {
  const rowCount = Math.max(1, parseInt(params.rowCount ?? params.RowCount, 10) || 1)
  const columnCount = Math.max(1, parseInt(params.columnCount ?? params.ColumnCount, 10) || 1)
  const rawLoc = params.insertLocation ?? params.InsertLocation
  const insertLocation = (rawLoc != null && String(rawLoc).trim() !== '' ? String(rawLoc) : 'End').trim() || 'End'
  const values = params.values ?? params.Values

  const doc = unwrap(wps.ActiveDocument)
  if (!doc || !doc.Content) throw new Error('无法获取当前文档，请在 WPS 文字中打开文档。')

  let range
  if (insertLocation.toLowerCase() === 'start') {
    range = doc.Range(0, 0)
  } else {
    const sel = wps.Selection
    const wdStory = wps.Enum && wps.Enum.wdStory != null ? wps.Enum.wdStory : 6
    if (sel && typeof sel.EndKey === 'function') {
      sel.EndKey(wdStory)
    }
    if (!sel || !sel.Range) {
      const end = unwrap(doc.Content.End)
      const pos = typeof end === 'number' && end > 0 ? end - 1 : 0
      range = doc.Range(pos, pos)
    } else {
      range = sel.Range
    }
  }

  const tbl = doc.Tables.Add(range, rowCount, columnCount)
  if (!tbl) throw new Error('Tables.Add 返回空，当前 WPS 版本可能不支持在目标位置插入表格。')

  if (values && Array.isArray(values)) {
    for (let r = 0; r < values.length && r < rowCount; r++) {
      const row = values[r]
      if (!Array.isArray(row)) continue
      for (let c = 0; c < row.length && c < columnCount; c++) {
        try {
          const cell = tbl.Cell(r + 1, c + 1)
          const cellRng = cell && cell.Range
          if (cellRng && cellRng.Text !== undefined) {
            cellRng.Text = row[c] != null ? String(row[c]) : ''
          }
        } catch {
          /* 单格失败则跳过 */
        }
      }
    }
  }

  return '成功：已在文档中插入 ' + rowCount + '×' + columnCount + ' 表格（insertLocation=' + insertLocation + '）。'
}
