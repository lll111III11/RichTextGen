(function () {
  'use strict';

  var $ = function (s) { return document.querySelector(s); };

  var state = {
    cfg: null,
    releases: null,
    latency: {},
    best: null,
    manual: null,
    auto: true,
    counts: { total: 0, files: {} },
    mode: 'remote',
    probing: false
  };

  var ICONS = {
    win: '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="#8fc0ff" stroke-width="1.7" stroke-linejoin="round"><path d="M4 6.2l7-1v6.3H4zM13 5l7-1v7.5h-7zM4 12.5h7v6.3l-7-1zM13 12.5h7V20l-7-1z"/></svg>',
    zip: '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="#8fc0ff" stroke-width="1.7" stroke-linejoin="round"><rect x="4" y="3.5" width="16" height="17" rx="2.5"/><path d="M12 3.5v4M12 9.5v2M12 13.5v2"/><rect x="10.4" y="16" width="3.2" height="3.2" rx=".8"/></svg>',
    android: '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="#8fc0ff" stroke-width="1.7" stroke-linecap="round"><path d="M6 10.5a6 6 0 0 1 12 0v6a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 6 16.5z"/><path d="M8.5 10.5h7M9 4.5l1.2 2M15 4.5l-1.2 2"/><circle cx="9.7" cy="14.2" r=".9" fill="#8fc0ff" stroke="none"/><circle cx="14.3" cy="14.2" r=".9" fill="#8fc0ff" stroke="none"/></svg>'
  };

  function fmtBytes(n) {
    if (n >= 1048576) return (n / 1048576).toFixed(2) + ' MB';
    if (n >= 1024) return (n / 1024).toFixed(0) + ' KB';
    return n + ' B';
  }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function fetchJson(url) {
    return fetch(url, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error(url + ' -> HTTP ' + r.status);
      return r.json();
    });
  }

  /* ---------- 测速 ---------- */

  function timeOnce(mirror) {
    var url = mirror.base + state.cfg.probePath + '?_=' + Date.now() + Math.random().toString(36).slice(2, 7);
    var ctl = new AbortController();
    var timer = setTimeout(function () { ctl.abort(); }, 4500);
    var t0 = performance.now();
    return fetch(url, { cache: 'no-store', signal: ctl.signal })
      .then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.arrayBuffer();
      })
      .then(function () { return performance.now() - t0; })
      .then(function (ms) { clearTimeout(timer); return ms; }, function (e) { clearTimeout(timer); throw e; });
  }

  function probeMirror(mirror) {
    var samples = [];
    return timeOnce(mirror).then(function (ms) {
      samples.push(ms);
      return timeOnce(mirror).catch(function () {});
    }).then(function (ms2) {
      if (typeof ms2 === 'number') samples.push(ms2);
      return timeOnce(mirror).catch(function () {});
    }).then(function (ms3) {
      if (typeof ms3 === 'number') samples.push(ms3);
      samples.sort(function (a, b) { return a - b; });
      var med = samples[Math.floor((samples.length - 1) / 2)];
      return { id: mirror.id, ok: true, ms: Math.round(med) };
    }).catch(function (e) {
      return { id: mirror.id, ok: false, err: (e && e.name === 'AbortError') ? '超时' : (e && e.message) || '失败' };
    });
  }

  function probeAll() {
    if (state.probing) return Promise.resolve();
    state.probing = true;
    var nets = $('#netState');
    nets.className = 'net';
    nets.querySelector('em').textContent = '测速中';
    $('#mirrorHint').className = 'hint';
    $('#mirrorHint').textContent = '正在对每条线路发送探测请求…';
    renderChips();

    var tasks = state.cfg.mirrors.map(probeMirror);
    return Promise.all(tasks).then(function (results) {
      state.latency = {};
      results.forEach(function (r) { state.latency[r.id] = r; });
      var alive = results.filter(function (r) { return r.ok; }).sort(function (a, b) { return a.ms - b.ms; });
      state.best = alive.length ? alive[0].id : null;
      state.probing = false;

      if (state.manual && state.latency[state.manual] && state.latency[state.manual].ok === false) {
        state.manual = null;
      }
      renderChips();
      if (!alive.length) {
        nets.className = 'net bad';
        nets.querySelector('em').textContent = '线路不可用';
        $('#mirrorHint').className = 'hint err';
        $('#mirrorHint').textContent = '所有线路探测失败，请检查网络后重新测速。';
      } else {
        nets.className = 'net ok';
        nets.querySelector('em').textContent = '线路正常';
        $('#mirrorHint').className = 'hint';
        $('#mirrorHint').textContent = alive.length === 1
          ? '当前只配置了 1 条线路（' + labelOf(alive[0].id) + '），延迟 ' + alive[0].ms + ' ms。'
          : '最快线路：' + labelOf(alive[0].id) + '（' + alive[0].ms + ' ms），下载将自动走这一条。';
      }
      paintStats();
      enableButtons(alive.length > 0);
    });
  }

  function labelOf(id) {
    for (var i = 0; i < state.cfg.mirrors.length; i++) {
      if (state.cfg.mirrors[i].id === id) return state.cfg.mirrors[i].label;
    }
    return id;
  }

  function activeMirror() {
    if (!state.auto && state.manual && state.latency[state.manual] && state.latency[state.manual].ok) {
      return state.manual;
    }
    return state.best;
  }

  /* ---------- 渲染 ---------- */

  function renderChips() {
    var box = $('#chips');
    box.innerHTML = '';
    state.cfg.mirrors.forEach(function (m) {
      var r = state.latency[m.id];
      var chip = el('div', 'chip');
      chip.dataset.id = m.id;
      chip.appendChild(el('i', 'dot'));
      chip.appendChild(el('span', 'name', m.label));
      if (m.tag) chip.appendChild(el('span', 'tag', m.tag));
      var ms = el('span', 'ms');
      ms.textContent = state.probing ? '…' : (r ? (r.ok ? r.ms + ' ms' : '不可用') : '未测');
      chip.appendChild(ms);
      if (r && !r.ok) chip.classList.add('dead');
      else if (r && r.ok && m.id === activeMirror()) chip.classList.add('best');
      if (r && r.ok) {
        chip.title = '点击手动使用这条线路';
        chip.addEventListener('click', function () {
          state.auto = false;
          $('#autoPick').checked = false;
          state.manual = m.id;
          renderChips();
        });
      }
      box.appendChild(chip);
    });
  }

  function renderCards() {
    var box = $('#cards');
    box.innerHTML = '';
    state.releases.files.forEach(function (f) {
      var card = el('article', 'dl');
      card.dataset.file = f.id;

      var top = el('div', 'dl-top');
      var ico = el('div', 'ico');
      ico.innerHTML = ICONS[f.icon] || ICONS.zip;
      var titleBox = el('div');
      titleBox.appendChild(el('h3', null, f.label));
      titleBox.appendChild(el('div', 'plat', f.platform || ''));
      top.appendChild(ico);
      top.appendChild(titleBox);
      card.appendChild(top);

      var meta = el('div', 'meta');
      meta.appendChild(el('span', null, '版本 ' + f.version));
      meta.appendChild(el('span', null, fmtBytes(f.size)));
      meta.appendChild(el('span', null, f.file.split('.').pop().toUpperCase()));
      card.appendChild(meta);

      card.appendChild(el('p', 'desc', f.desc || ''));

      var bottom = el('div', 'dl-bottom');
      var count = el('div', 'count');
      count.innerHTML = '已下载 <b data-count="' + f.id + '">—</b> 次';
      var btn = el('button', 'btn', '下载');
      btn.type = 'button';
      btn.dataset.dl = f.id;
      btn.disabled = true;
      btn.addEventListener('click', function () { startDownload(f); });
      bottom.appendChild(count);
      bottom.appendChild(btn);
      card.appendChild(bottom);

      var det = document.createElement('details');
      det.appendChild(el('summary', null, 'SHA-256 校验值'));
      var hash = el('p', 'hash', f.sha256);
      hash.title = '点击复制';
      hash.addEventListener('click', function () {
        copy(hash.textContent, hash);
      });
      det.appendChild(hash);
      card.appendChild(det);

      box.appendChild(card);
    });
  }

  function copy(text, node) {
    var done = function () {
      var old = node.textContent;
      node.textContent = '已复制 ✓';
      setTimeout(function () { node.textContent = old; }, 900);
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, function () { fallbackCopy(text, done); });
    } else fallbackCopy(text, done);
  }

  function fallbackCopy(text, done) {
    var ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); done(); } catch (e) {}
    ta.remove();
  }

  function enableButtons(on) {
    var btns = document.querySelectorAll('[data-dl]');
    for (var i = 0; i < btns.length; i++) btns[i].disabled = !on;
  }

  function paintCounts() {
    var nodes = document.querySelectorAll('[data-count]');
    for (var i = 0; i < nodes.length; i++) {
      var id = nodes[i].dataset.count;
      nodes[i].textContent = state.counts.files[id] != null ? state.counts.files[id] : 0;
    }
    $('#statTotal').textContent = state.counts.total || 0;
    $('#statMode').textContent = state.mode === 'remote' ? '线上统计' : '本地离线';
    $('#statMode').style.color = state.mode === 'remote' ? '' : 'var(--warn)';
  }

  function paintStats() {
    var best = state.best ? state.latency[state.best] : null;
    $('#statFast').textContent = best ? labelOf(best.id) + ' · ' + best.ms + ' ms' : '—';
    var n = Object.keys(state.latency).length;
    $('#statProbe').textContent = n ? formatTime(state.probeAt) + ' · ' + n + ' 条' : '—';
  }

  function formatTime(t) {
    if (!t) return '—';
    var d = new Date(t);
    var p = function (x) { return x < 10 ? '0' + x : '' + x; };
    return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }

  /* ---------- 计数 ---------- */

  function loadCounts() {
    var ids = state.releases.files.map(function (f) { return f.id; }).join(',');
    return fetch(state.cfg.counter + '?files=' + encodeURIComponent(ids), { cache: 'no-store' })
      .then(function (r) {
        if (!r.ok) throw new Error('counter ' + r.status);
        return r.json();
      })
      .then(function (j) {
        if (!j || !j.ok) throw new Error('counter off');
        state.counts = { total: j.total || 0, files: j.files || {} };
        state.mode = 'remote';
      })
      .catch(function () {
        state.counts = readLocal();
        state.mode = 'local';
      })
      .then(function () {
        paintCounts();
      });
  }

  function readLocal() {
    try {
      var raw = localStorage.getItem('rtg.downloads');
      if (!raw) return { total: 0, files: {} };
      var j = JSON.parse(raw);
      return { total: j.total || 0, files: j.files || {} };
    } catch (e) {
      return { total: 0, files: {} };
    }
  }

  function writeLocal() {
    try { localStorage.setItem('rtg.downloads', JSON.stringify(state.counts)); } catch (e) {}
  }

  function sendCount(file, mirror) {
    var payload = JSON.stringify({ file: file.id, mirror: mirror ? mirror.id : '' });
    var beacons = false;
    try {
      beacons = navigator.sendBeacon(state.cfg.counter, new Blob([payload], { type: 'text/plain;charset=UTF-8' }));
    } catch (e) {}
    if (!beacons) {
      fetch(state.cfg.counter, {
        method: 'POST',
        body: payload,
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
        keepalive: true
      }).catch(function () {});
    }
    state.counts.total = (state.counts.total || 0) + 1;
    state.counts.files[file.id] = (state.counts.files[file.id] || 0) + 1;
    if (state.mode === 'local') writeLocal();
    paintCounts();
    setTimeout(loadCounts, 1600);
  }

  /* ---------- 下载 ---------- */

  function startDownload(file) {
    var id = activeMirror();
    if (!id) return;
    var mirror = null;
    for (var i = 0; i < state.cfg.mirrors.length; i++) {
      if (state.cfg.mirrors[i].id === id) mirror = state.cfg.mirrors[i];
    }
    if (!mirror) return;

    sendCount(file, mirror);

    var url = mirror.base + 'files/' + encodeURIComponent(file.file);
    var a = document.createElement('a');
    a.href = url;
    a.download = file.file;
    a.rel = 'noopener';
    document.body.appendChild(a);
    a.click();
    a.remove();
  }

  /* ---------- 启动 ---------- */

  function init() {
    Promise.all([fetchJson('releases.json'), fetchJson('mirrors.json')])
      .then(function (res) {
        state.releases = res[0];
        state.cfg = res[1];
        if (!state.cfg.mirrors || !state.cfg.mirrors.length) throw new Error('mirrors.json 未配置线路');
        $('#verPill').textContent = 'v' + state.releases.version;
        document.title = '下载 · ' + state.releases.product + ' v' + state.releases.version;
        renderCards();
        renderChips();
        return loadCounts();
      })
      .then(function () {
        $('#autoPick').addEventListener('change', function (e) {
          state.auto = e.target.checked;
          if (state.auto) state.manual = null;
          renderChips();
        });
        $('#reprobe').addEventListener('click', function () {
          state.probeAt = Date.now();
          probeAll();
        });
        state.probeAt = Date.now();
        return probeAll();
      })
      .catch(function (e) {
        var hint = $('#mirrorHint');
        if (hint) {
          hint.className = 'hint err';
          hint.textContent = '初始化失败：' + (e && e.message ? e.message : e);
        }
        var nets = $('#netState');
        if (nets) { nets.className = 'net bad'; nets.querySelector('em').textContent = '异常'; }
      });
  }

  init();
})();
