/* Taskly video / stream URL harvester — paste entire file as run_custom_javascript_in_page scriptCode. */
var notes = [];
var videos = [];
var seen = {};
var MAX = 300;
var MAX_VID = 400;

function abs(u) {
  if (!u || typeof u !== "string") return null;
  u = u.trim();
  if (!u) return null;
  if (u.indexOf("data:") === 0 || u.indexOf("blob:") === 0) {
    notes.push("跳过 blob:/data: 媒体占位。");
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
  if (videos.length >= MAX) return;
  var a = abs(u);
  if (!a || seen[a]) return;
  seen[a] = true;
  videos.push(a);
}

function looksLikeMediaUrl(h) {
  if (!h) return false;
  var l = h.toLowerCase();
  return (
    /\.(mp4|webm|ogv|m3u8|mpd|mov|mkv|ts)(\?|$)/i.test(l) ||
    l.indexOf("/video/") >= 0 ||
    l.indexOf("format=m3u8") >= 0 ||
    l.indexOf("format=mpd") >= 0
  );
}

var vels = document.querySelectorAll("video");
for (var vi = 0; vi < vels.length && vi < MAX_VID; vi++) {
  var v = vels[vi];
  try {
    if (v.currentSrc) add(v.currentSrc);
    else if (v.getAttribute("src")) add(v.getAttribute("src"));
  } catch (e0) {}
  var ch = v.querySelectorAll("source[src], source[srcset]");
  for (var ci = 0; ci < ch.length; ci++) {
    var sc = ch[ci];
    var ssrc = sc.getAttribute("src");
    if (ssrc) add(ssrc);
    var sst = sc.getAttribute("srcset");
    if (sst) {
      var parts = sst.split(",");
      for (var k = 0; k < parts.length; k++) {
        var uu = parts[k].trim().split(/\s+/)[0];
        if (uu) add(uu);
      }
    }
  }
}

var auds = document.querySelectorAll("audio[src], audio source[src], audio source[srcset]");
for (var ai = 0; ai < auds.length; ai++) {
  var ael = auds[ai];
  var asrc = ael.getAttribute("src");
  if (asrc) add(asrc);
  var asst = ael.getAttribute("srcset");
  if (asst) {
    var ap = asst.split(",");
    for (var j = 0; j < ap.length; j++) {
      var au = ap[j].trim().split(/\s+/)[0];
      if (au) add(au);
    }
  }
}

var anchors = document.querySelectorAll("a[href]");
for (var ai2 = 0; ai2 < anchors.length && ai2 < 800; ai2++) {
  var h = anchors[ai2].getAttribute("href");
  if (h && looksLikeMediaUrl(h)) add(h);
}

try {
  var objs = document.querySelectorAll("object[data], embed[src]");
  for (var oi = 0; oi < objs.length; oi++) {
    var o = objs[oi];
    var du = o.getAttribute("data") || o.getAttribute("src");
    if (du && looksLikeMediaUrl(du)) add(du);
  }
} catch (e1) {}

try {
  var scripts = document.querySelectorAll('script[type="application/ld+json"]');
  for (var si = 0; si < scripts.length && si < 15; si++) {
    var txt = (scripts[si].textContent || "").trim();
    if (txt.length < 20 || txt.length > 200000) continue;
    var re = /"(contentUrl|embedUrl|url)"\s*:\s*"([^"]+)"/g;
    var m;
    while ((m = re.exec(txt)) !== null) {
      var u = m[2];
      if (looksLikeMediaUrl(u)) add(u);
    }
  }
  if (scripts.length) notes.push("已尝试从 JSON-LD 中粗匹配 contentUrl/embedUrl（启发式，可能误报）。");
} catch (e2) {}

if (videos.length >= MAX) notes.push("视频相关 URL 已达上限 " + MAX + "。");
notes.push("脚本类型：仅视频/流媒体相关枚举；m3u8/mpd 多为清单，DRM 与分片密钥不在此脚本范围。");

return JSON.stringify({
  kind: "videos",
  pageUrl: location.href,
  title: document.title || "",
  videos: videos,
  notes: notes,
  counts: { videoElementsScanned: Math.min(vels.length, MAX_VID), videos: videos.length }
});
