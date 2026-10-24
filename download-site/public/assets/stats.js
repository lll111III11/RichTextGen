(function () {
  'use strict';

  var API = (location.origin || '') + '/api/count';
  var ids = [];
  var labels = {};
  var current = '';

  function fmt(n) {
    return (n || 0).toLocaleString('zh-CN');
  }

  function loadMeta() {
    return fetch('releases.json', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        ids = j.files.map(function (f) { return f.id; });
        j.files.forEach(function (f) { labels[f.id] = f.label; });
        current = j.version || '';
        var pill = document.getElementById('verPill');
        if (pill) pill.textContent = 'v' + current;
        var cv = document.getElementById('curVer');
        if (cv) cv.textContent = 'v' + current;
      });
  }

  function verNum(v) {
    return String(v).split('.').map(function (x) { return parseInt(x, 10) || 0; });
  }

  /* 版本号从旧到新排序 */
  function cmpVer(a, b) {
    var x = verNum(a), y = verNum(b);
    var n = Math.max(x.length, y.length);
    for (var i = 0; i < n; i++) {
      var d = (x[i] || 0) - (y[i] || 0);
      if (d) return d;
    }
    return 0;
  }

  function row(name, n, max, suffix) {
    var r = document.createElement('div');
    r.className = 'stat-row';

    var head = document.createElement('div');
    head.className = 'stat-row-head';
    var label = document.createElement('span');
    label.textContent = name + (suffix || '');
    var num = document.createElement('b');
    num.textContent = fmt(n);
    head.appendChild(label);
    head.appendChild(num);

    var bar = document.createElement('div');
    bar.className = 'bar';
    var fill = document.createElement('i');
    fill.style.width = (max > 0 ? Math.round((n / max) * 100) : 0) + '%';
    bar.appendChild(fill);

    r.appendChild(head);
    r.appendChild(bar);
    return r;
  }

  function note(text) {
    var p = document.createElement('p');
    p.className = 'total-updated';
    p.textContent = text;
    return p;
  }

  function render(data) {
    var versions = data.versions || {};
    document.getElementById('total').textContent = fmt(versions[current] || 0);

    // 每个版本单独一行，互不累加
    var vlist = Object.keys(versions).sort(cmpVer).reverse();
    var vbox = document.getElementById('verList');
    vbox.innerHTML = '';
    if (!vlist.length) {
      vbox.appendChild(note('暂无按版本的数据：旧版安装器未上报版本号。'));
    } else {
      var vmax = 1;
      vlist.forEach(function (v) { if (versions[v] > vmax) vmax = versions[v]; });
      vlist.forEach(function (v) {
        vbox.appendChild(row('v' + v, versions[v], vmax, v === current ? ' · 当前版本' : ''));
      });
    }

    // 按安装包
    var files = data.files || {};
    var max = 1;
    ids.forEach(function (id) { if ((files[id] || 0) > max) max = files[id] || 0; });
    var list = document.getElementById('list');
    list.innerHTML = '';
    ids.forEach(function (id) {
      list.appendChild(row(labels[id] || id, files[id] || 0, max, ''));
    });

    var d = new Date();
    var p = function (x) { return x < 10 ? '0' + x : '' + x; };
    document.getElementById('updated').textContent = '更新于 ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds()) + ' · 每 30 秒刷新';
  }

  function refresh() {
    var url = API + '?files=' + encodeURIComponent(ids.join(','));
    fetch(url, { cache: 'no-store' })
      .then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      })
      .then(function (j) {
        if (!j || !j.ok) throw new Error('counter unavailable');
        render(j);
      })
      .catch(function (e) {
        document.getElementById('updated').textContent = '统计不可用：' + e.message;
      });
  }

  loadMeta().then(function () {
    var link = document.getElementById('apiLink');
    link.href = API + '?files=' + encodeURIComponent(ids.join(','));
    refresh();
    setInterval(refresh, 30000);
  }).catch(function (e) {
    document.getElementById('updated').textContent = '初始化失败：' + e.message;
  });
})();
