(function () {
  'use strict';

  var API = (location.origin || '') + '/api/count';
  var ids = [];
  var labels = {};

  function fmt(n) {
    return (n || 0).toLocaleString('zh-CN');
  }

  function loadMeta() {
    return fetch('releases.json', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        ids = j.files.map(function (f) { return f.id; });
        j.files.forEach(function (f) { labels[f.id] = f.label; });
        var pill = document.getElementById('verPill');
        if (pill) pill.textContent = 'v' + j.version;
      });
  }

  function render(data) {
    var total = data.total || 0;
    document.getElementById('total').textContent = fmt(total);

    var files = data.files || {};
    var max = 1;
    ids.forEach(function (id) { if ((files[id] || 0) > max) max = files[id] || 0; });

    var list = document.getElementById('list');
    list.innerHTML = '';
    ids.forEach(function (id) {
      var n = files[id] || 0;
      var pct = Math.round((n / max) * 100);

      var row = document.createElement('div');
      row.className = 'stat-row';

      var head = document.createElement('div');
      head.className = 'stat-row-head';
      var name = document.createElement('span');
      name.textContent = labels[id] || id;
      var num = document.createElement('b');
      num.textContent = fmt(n);
      head.appendChild(name);
      head.appendChild(num);

      var bar = document.createElement('div');
      bar.className = 'bar';
      var fill = document.createElement('i');
      fill.style.width = pct + '%';
      bar.appendChild(fill);

      row.appendChild(head);
      row.appendChild(bar);
      list.appendChild(row);
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
