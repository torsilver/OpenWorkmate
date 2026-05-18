/**
 * 在 marked.parse + innerHTML 之前调用：避免正文中的 `<`（颜文字如 (๑>ᴗ<๑)、<3 等）被当成 HTML 解析而截断。
 * 围栏代码块（行首 ``` … 行首 ```）内不改写，以免破坏示例代码。
 * streamRaw / 存盘 Markdown 仍保留原文；仅在渲染路径预处理。
 * 未闭合 ``` 时流式中途可能误判，收尾后再 parse 会收敛。
 */
(function (global) {
  "use strict";

  function escapeAnglesInProse(s) {
    return s.replace(/<(?!(?:\/[A-Za-z]|[A-Za-z]|[!?]))/g, "&lt;");
  }

  function preparseChatMarkdownForMarkedHtml(markdown) {
    var md = markdown != null ? String(markdown) : "";
    if (!md) return md;
    var lines = md.split(/\r\n|\n|\r/);
    var sep = md.indexOf("\r\n") >= 0 ? "\r\n" : md.indexOf("\r") >= 0 ? "\r" : "\n";
    var out = [];
    var i = 0;
    while (i < lines.length) {
      if (/^( {0,3})```/.test(lines[i])) {
        var fenceStart = i;
        var j = i + 1;
        var found = false;
        while (j < lines.length) {
          if (/^( {0,3})```\s*$/.test(lines[j])) {
            found = true;
            break;
          }
          j++;
        }
        var fenceEnd = found ? j + 1 : lines.length;
        out.push(lines.slice(fenceStart, fenceEnd).join(sep));
        i = fenceEnd;
      } else {
        var k = i;
        while (k < lines.length && !/^( {0,3})```/.test(lines[k])) k++;
        out.push(escapeAnglesInProse(lines.slice(i, k).join(sep)));
        i = k;
      }
    }
    return out.join("");
  }

  global.preparseChatMarkdownForMarkedHtml = preparseChatMarkdownForMarkedHtml;
})(typeof self !== "undefined" ? self : this);
