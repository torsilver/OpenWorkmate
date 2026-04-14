/* Taskly image URL harvester — paste entire file as run_custom_javascript_in_page scriptCode. */
var notes = [];
var images = [];
var seen = {};
var MAX = 600;
var MAX_IMG = 2500;
var MAX_STYLE_NODES = 3000;

function abs(u) {
  if (!u || typeof u !== "string") return null;
  u = u.trim();
  if (!u) return null;
  if (u.indexOf("data:") === 0) {
    notes.push("跳过 data: 图片内联。");
    return null;
  }
  if (u.indexOf("blob:") === 0) {
    notes.push("存在 blob: 图片引用（内存 URL，无法当稳定外链）。");
    return null;
  }
  try {
    var x = new URL(u, location.href);
    if (x.protocol !== "http:" && x.protocol !== "https:") return null;
    return x.href;
  } catch (e) {
    return null;
  }
}

function add(u) {
  if (images.length >= MAX) return;
  var a = abs(u);
  if (!a || seen[a]) return;
  seen[a] = true;
  images.push(a);
}

function parseSrcset(ss) {
  if (!ss || typeof ss !== "string") return;
  var parts = ss.split(",");
  for (var i = 0; i < parts.length; i++) {
    var p = parts[i].trim().split(/\s+/)[0];
    if (p) add(p);
  }
}

try {
  var metas = document.querySelectorAll(
    'meta[property="og:image"],meta[name="twitter:image"],meta[itemprop="image"],link[rel="image_src"]'
  );
  for (var mi = 0; mi < metas.length; mi++) {
    var m = metas[mi];
    var c = m.getAttribute("content");
    if (c) add(c);
    if (m.tagName === "LINK" && m.getAttribute("href")) add(m.getAttribute("href"));
  }
} catch (e0) {}

try {
  var icons = document.querySelectorAll(
    'link[rel="icon"],link[rel="shortcut icon"],link[rel="apple-touch-icon"],link[rel="apple-touch-icon-precomposed"]'
  );
  for (var ii = 0; ii < icons.length; ii++) {
    var h = icons[ii].getAttribute("href");
    if (h) add(h);
  }
} catch (e1) {}

var imgs = document.querySelectorAll("img");
for (var i = 0; i < imgs.length && i < MAX_IMG; i++) {
  var el = imgs[i];
  var cs =
    el.currentSrc ||
    el.getAttribute("src") ||
    el.getAttribute("data-src") ||
    el.getAttribute("data-lazy-src") ||
    el.getAttribute("data-original") ||
    el.getAttribute("data-lazy") ||
    "";
  if (cs) add(cs);
  var ds = el.getAttribute("srcset");
  if (ds) parseSrcset(ds);
  var dss = el.getAttribute("data-srcset");
  if (dss) parseSrcset(dss);
}

try {
  var ps = document.querySelectorAll("picture source[srcset], picture source[src], image[src]");
  for (var p = 0; p < ps.length; p++) {
    var s = ps[p];
    var st = s.getAttribute("srcset");
    if (st) parseSrcset(st);
    var su = s.getAttribute("src") || s.getAttribute("href");
    if (su) add(su);
  }
} catch (e2) {}

var styleHits = 0;
try {
  var nodes = document.querySelectorAll("[style]");
  for (var si = 0; si < nodes.length && si < MAX_STYLE_NODES; si++) {
    var stl = nodes[si].getAttribute("style");
    if (!stl || stl.indexOf("url") < 0) continue;
    var re = /url\(\s*['"]?([^'")]+)['"]?\s*\)/gi;
    var m;
    while ((m = re.exec(stl)) !== null) {
      styleHits++;
      if (styleHits > 100) break;
      var raw = m[1];
      if (raw && /\.(png|jpe?g|gif|webp|avif|svg|bmp|ico)(\?|$)/i.test(raw)) add(raw);
    }
    if (styleHits > 100) break;
  }
  if (styleHits > 0) notes.push("已从部分内联 style 的 url() 中提取疑似图片路径。");
} catch (e3) {}

if (images.length >= MAX) notes.push("图片 URL 已达上限 " + MAX + "。");
if (imgs.length > MAX_IMG) notes.push("仅扫描前 " + MAX_IMG + " 个 img。");
notes.push("脚本类型：仅图片枚举；不含 video 专项逻辑。");

return JSON.stringify({
  kind: "images",
  pageUrl: location.href,
  title: document.title || "",
  images: images,
  notes: notes,
  counts: { imgElementsScanned: Math.min(imgs.length, MAX_IMG), images: images.length }
});
