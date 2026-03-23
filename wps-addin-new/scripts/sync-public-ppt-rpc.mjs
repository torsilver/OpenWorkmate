/**
 * 从 useCopilot.js 抽取 ppt_document_create … ppt_slide_duplicate 分支，转为 public/taskpane.js 风格（var + 双引号 method）。
 * 一次性同步脚本；运行：node scripts/sync-public-ppt-rpc.mjs
 */
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const root = path.join(__dirname, '..')
const srcPath = path.join(root, 'src/composables/useCopilot.js')
const pubPath = path.join(root, 'public/taskpane.js')

const srcRaw = fs.readFileSync(srcPath, 'utf8')
const src = srcRaw.replace(/\r\n/g, '\n')
const startMark = "      } else if (method === 'ppt_document_create') {"
const endMark =
  "\n      } else {\n        sendRes(null, 'Method not supported in this client: ' + method)\n      }"
const si = src.indexOf(startMark)
const ei = src.indexOf(endMark)
if (si < 0 || ei < 0) throw new Error('Markers not found in useCopilot.js si=' + si + ' ei=' + ei)
let frag = src.slice(si, ei)

frag = frag.replace(/\bconst\b/g, 'var').replace(/\blet\b/g, 'var')
frag = frag.replace(/method === '([^']+)'/g, 'method === "$1"')
frag = frag.replace(/new ActiveXObject\('([^']+)'\)/g, 'new ActiveXObject("$1")')
frag = frag.replace(/catch \(_\) \{\s*\/\* ignore \*\/\s*\}/g, 'catch (eIgn) {}')
frag = frag.replace(/catch \(_\) \{\s*\/\* 越界则跳过 \*\/\s*\}/g, 'catch (eCell) {}')

let pub = fs.readFileSync(pubPath, 'utf8').replace(/\r\n/g, '\n')
const pubOpenImplemented = '      } else if (method === "ppt_document_create") {'
const stubOpenLegacy =
  '      } else if (\n        method === "ppt_document_create" ||\n        method === "ppt_slide_image_add" ||'
let pubStartReal = pub.indexOf(pubOpenImplemented)
if (pubStartReal < 0) pubStartReal = pub.indexOf(stubOpenLegacy)
if (pubStartReal < 0) throw new Error('PPT RPC block start not found in public/taskpane.js')
const pubEnd = pub.indexOf('\n      } else {\n        sendRes(null, "Method not supported in this client: " + method);\n      }')
if (pubEnd < 0) throw new Error('PPT RPC block end not found in public/taskpane.js end=' + pubEnd)

const newPub = pub.slice(0, pubStartReal) + frag + pub.slice(pubEnd)
fs.writeFileSync(pubPath, newPub, 'utf8')
console.log('Updated', pubPath)
