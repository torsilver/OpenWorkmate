/**
 * 对齐 Chrome：chrome-extension/libs/chat-markdown-preparse.js
 * 在 marked.parse + innerHTML 之前调用，避免 `<` 被当成 HTML；围栏 ``` 内不改写。
 */
function escapeAnglesInProse(s) {
  return s.replace(/<(?!(?:\/[A-Za-z]|[A-Za-z]|[!?]))/g, '&lt;')
}

export function preparseChatMarkdownForMarkedHtml(markdown) {
  const md = markdown != null ? String(markdown) : ''
  if (!md) return md
  const lines = md.split(/\r\n|\n|\r/)
  const sep = md.indexOf('\r\n') >= 0 ? '\r\n' : md.indexOf('\r') >= 0 ? '\r' : '\n'
  const out = []
  let i = 0
  while (i < lines.length) {
    if (/^( {0,3})```/.test(lines[i])) {
      const fenceStart = i
      let j = i + 1
      let found = false
      while (j < lines.length) {
        if (/^( {0,3})```\s*$/.test(lines[j])) {
          found = true
          break
        }
        j++
      }
      const fenceEnd = found ? j + 1 : lines.length
      out.push(lines.slice(fenceStart, fenceEnd).join(sep))
      i = fenceEnd
    } else {
      let k = i
      while (k < lines.length && !/^( {0,3})```/.test(lines[k])) k++
      out.push(escapeAnglesInProse(lines.slice(i, k).join(sep)))
      i = k
    }
  }
  return out.join('')
}
