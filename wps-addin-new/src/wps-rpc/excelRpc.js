/**
 * WPS 表格 CurrentDocument RPC，返回 JSON 字符串形状对齐 office-addin/taskpane.js（Excel.run 分支）。
 */

function unwrap(v) {
  if (v == null) return v
  if (typeof v === 'object' && v !== null && 'value' in v && v.value !== undefined) return unwrap(v.value)
  return v
}

function sheetCount(sheets) {
  if (!sheets) return 0
  let c = sheets.Count
  c = unwrap(c)
  return typeof c === 'number' && !Number.isNaN(c) ? c : 0
}

function pickSheetName(params) {
  return params.sheetName ?? params.SheetName
}

function pickAddress(params) {
  return params.address ?? params.Address ?? params.range ?? params.Range
}

function getWorksheet(wps, sheetName) {
  const wb = unwrap(wps.ActiveWorkbook)
  if (!wb) throw new Error('没有活动工作簿，请在 WPS 表格中打开工作簿后再试。')
  const sheets = wb.Worksheets || wb.Sheets
  if (!sheets) throw new Error('无法访问工作表集合。')
  const name = sheetName != null ? String(sheetName).trim() : ''
  if (!name) {
    const sh = unwrap(wps.ActiveSheet) || unwrap(wb.ActiveSheet)
    if (!sh) throw new Error('无法获取当前活动工作表。')
    return sh
  }
  try {
    return sheets.Item(name)
  } catch (e) {
    throw new Error('未找到工作表「' + name + '」：' + (e && e.message ? e.message : String(e)))
  }
}

function getRange(ws, address) {
  const addr = (address != null && String(address).trim()) || 'A1'
  if (!ws) throw new Error('工作表无效。')
  try {
    return ws.Range(addr)
  } catch (e) {
    throw new Error('区域地址无效（' + addr + '）：' + (e && e.message ? e.message : String(e)))
  }
}

function rangeToValuesAndText(rng) {
  const vals = unwrap(rng.Value2)
  let text = ''
  try {
    text = unwrap(rng.Text) != null ? String(unwrap(rng.Text)) : ''
  } catch {
    text = ''
  }
  if (!text && vals != null) {
    if (Array.isArray(vals)) {
      text = vals.map((row) => (Array.isArray(row) ? row.join('\t') : String(row))).join('\n')
    } else {
      text = String(vals)
    }
  }
  return { values: vals, text }
}

export function runWpsExcelRpc(method, params, wps) {
  switch (method) {
    case 'excel_read_range': {
      const ws = getWorksheet(wps, pickSheetName(params))
      const rng = getRange(ws, pickAddress(params))
      const { values, text } = rangeToValuesAndText(rng)
      return JSON.stringify({ values, text })
    }
    case 'excel_write_range': {
      const vals = params.values ?? params.Values
      if (!vals || !Array.isArray(vals)) throw new Error('缺少 values 二维数组。')
      const ws = getWorksheet(wps, pickSheetName(params))
      const rng = getRange(ws, pickAddress(params))
      rng.Value2 = vals
      return '成功：已写入当前 WPS 表格区域。'
    }
    case 'excel_list_sheets': {
      const wb = unwrap(wps.ActiveWorkbook)
      if (!wb) throw new Error('没有活动工作簿。')
      const sheets = wb.Worksheets || wb.Sheets
      if (!sheets) throw new Error('无法访问工作表集合。')
      const n = sheetCount(sheets)
      const names = []
      for (let i = 1; i <= n; i++) {
        const sh = sheets.Item(i)
        const nm = unwrap(sh && sh.Name)
        names.push(nm != null ? String(nm) : '')
      }
      return JSON.stringify({ names })
    }
    case 'excel_get_used_range': {
      const ws = getWorksheet(wps, pickSheetName(params))
      let used
      try {
        used = ws.UsedRange
      } catch (e) {
        throw new Error('无法读取已使用区域（可能为空表）：' + (e && e.message ? e.message : String(e)))
      }
      if (!used) throw new Error('当前工作表没有已使用区域。')
      let addr = ''
      try {
        addr = unwrap(used.Address) != null ? String(unwrap(used.Address)) : ''
      } catch {
        addr = ''
      }
      const vals = unwrap(used.Value2)
      return JSON.stringify({ address: addr, values: vals })
    }
    case 'excel_read_formulas': {
      const ws = getWorksheet(wps, pickSheetName(params))
      const rng = getRange(ws, pickAddress(params))
      let formulas
      try {
        formulas = unwrap(rng.Formula)
      } catch (e) {
        try {
          formulas = unwrap(rng.FormulaR1C1)
        } catch (e2) {
          throw new Error('无法读取公式：' + (e && e.message ? e.message : String(e)))
        }
      }
      return JSON.stringify({ formulas })
    }
    case 'excel_write_formulas': {
      const formulas = params.formulas ?? params.Formulas
      if (!formulas || !Array.isArray(formulas)) throw new Error('缺少 formulas 二维数组。')
      const ws = getWorksheet(wps, pickSheetName(params))
      const rng = getRange(ws, pickAddress(params))
      try {
        rng.Formula = formulas
      } catch (e) {
        try {
          rng.FormulaR1C1 = formulas
        } catch (e2) {
          throw new Error('无法写入公式：' + (e && e.message ? e.message : String(e)))
        }
      }
      return '成功：已写入公式。'
    }
    default:
      throw new Error('未知 Excel RPC：' + method)
  }
}
