(function () {
  'use strict';

  var LS = 'rtg.studio.v1';
  var COLORS = ['#e9edf7', '#98a2bd', '#ff6b81', '#ffa94d', '#f0b429', '#3ddc97',
                '#4dd0e1', '#6ea8ff', '#a855f7', '#ff7ab6', '#ffd166', '#7bd88f'];

  var editor = document.getElementById('editor');
  var source = document.getElementById('source');
  var tabsBox = document.getElementById('tabs');
  var swatches = document.getElementById('swatches');
  var stFile = document.getElementById('stFile');
  var stMode = document.getElementById('stMode');
  var stCount = document.getElementById('stCount');
  var stSaved = document.getElementById('stSaved');

  var state = { tabs: [], active: 0, mode: 'rich' };
  var savedRange = null;
  var saveTimer = null;

  function uid() {
    return 't' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
  }

  function blankTab(name) {
    return { id: uid(), name: name || '未命名', html: '', dirty: false };
  }

  /* ---------- 持久化 ---------- */

  function load() {
    try {
      var raw = localStorage.getItem(LS);
      if (raw) {
        var j = JSON.parse(raw);
        if (j && j.tabs && j.tabs.length) {
          state.tabs = j.tabs;
          state.active = Math.min(j.active || 0, state.tabs.length - 1);
          return;
        }
      }
    } catch (e) {}
    state.tabs = [blankTab()];
    state.active = 0;
  }

  function save(now) {
    if (saveTimer) clearTimeout(saveTimer);
    var doSave = function () {
      try { localStorage.setItem(LS, JSON.stringify({ tabs: state.tabs, active: state.active })); } catch (e) {}
      stSaved.textContent = '已保存';
    };
    if (now) return doSave();
    stSaved.textContent = '保存中…';
    saveTimer = setTimeout(doSave, 400);
  }

  function current() { return state.tabs[state.active]; }

  /** 把编辑区内容写回当前标签 */
  function storeCurrent() {
    var t = current();
    if (!t) return;
    if (state.mode === 'rich') {
      t.html = editor.innerHTML;
      t.dirty = false;
    } else {
      t.html = source.value;
      t.dirty = false;
    }
  }

  /* ---------- 标签 ---------- */

  function renderTabs() {
    tabsBox.innerHTML = '';
    state.tabs.forEach(function (t, i) {
      var tab = document.createElement('div');
      tab.className = 'tab' + (i === state.active ? ' on' : '') + (t.dirty ? ' dirty' : '');
      tab.setAttribute('role', 'tab');

      var dot = document.createElement('i');
      dot.className = 'dot-unsaved';
      tab.appendChild(dot);

      var nm = document.createElement('span');
      nm.className = 'name';
      nm.textContent = t.name;
      tab.appendChild(nm);

      var x = document.createElement('button');
      x.className = 'x';
      x.type = 'button';
      x.textContent = '×';
      x.title = '关闭标签';
      x.addEventListener('click', function (ev) {
        ev.stopPropagation();
        closeTab(i);
      });
      tab.appendChild(x);

      tab.addEventListener('click', function () { activate(i); });
      tab.addEventListener('dblclick', function () { rename(i); });
      tabsBox.appendChild(tab);
    });

    var add = document.createElement('button');
    add.className = 'tab-add';
    add.type = 'button';
    add.textContent = '+';
    add.title = '新建标签';
    add.addEventListener('click', function () {
      storeCurrent();
      state.tabs.push(blankTab('未命名 ' + (state.tabs.length + 1)));
      state.active = state.tabs.length - 1;
      save(true);
      syncView();
    });
    tabsBox.appendChild(add);
  }

  function activate(i) {
    if (i === state.active) return;
    storeCurrent();
    save(true);
    state.active = i;
    syncView();
  }

  function closeTab(i) {
    if (state.tabs.length === 1) {
      state.tabs[0] = blankTab('未命名');
      state.active = 0;
      save(true);
      syncView();
      return;
    }
    storeCurrent();
    state.tabs.splice(i, 1);
    if (state.active >= state.tabs.length) state.active = state.tabs.length - 1;
    else if (state.active > i) state.active -= 1;
    save(true);
    syncView();
  }

  function rename(i) {
    var t = state.tabs[i];
    var name = window.prompt('标签名称', t.name);
    if (name === null) return;
    name = name.trim();
    if (!name) return;
    t.name = name.slice(0, 40);
    save(true);
    renderTabs();
    updateStatus();
  }

  /* ---------- 视图 ---------- */

  function syncView() {
    renderTabs();
    var t = current();
    if (!t) return;
    if (state.mode === 'rich') {
      editor.innerHTML = t.html;
      editor.hidden = false;
      source.hidden = true;
      document.getElementById('toggleSource').classList.remove('on');
      stMode.textContent = '富文本';
    } else {
      source.value = t.html;
      editor.hidden = true;
      source.hidden = false;
      document.getElementById('toggleSource').classList.add('on');
      stMode.textContent = '源码';
    }
    updateStatus();
  }

  function updateStatus() {
    var t = current();
    if (!t) return;
    stFile.textContent = t.name;
    var text = state.mode === 'rich' ? editor.innerText : source.value;
    var n = (text || '').replace(/\s/g, '').length;
    stCount.textContent = n + ' 字 · ' + ((text || '').split('\n').length) + ' 行';
  }

  function markDirty() {
    var t = current();
    if (!t) return;
    t.dirty = true;
    var tab = tabsBox.children[state.active];
    if (tab) tab.classList.add('dirty');
    stSaved.textContent = '未保存';
  }

  /* ---------- 富文本命令 ---------- */

  function restoreRange() {
    if (!savedRange) return;
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(savedRange);
  }

  function exec(cmd, val) {
    editor.focus();
    restoreRange();
    try { document.execCommand('styleWithCSS', false, true); } catch (e) {}
    document.execCommand(cmd, false, val);
    onEdit();
  }

  function onEdit() {
    var t = current();
    if (!t) return;
    if (state.mode === 'rich') t.html = editor.innerHTML;
    else t.html = source.value;
    autoName(t);
    markDirty();
    updateStatus();
    save();
  }

  /** 名字还是默认的「未命名」时，用首行内容自动命名，方便区分多个标签 */
  function autoName(t) {
    if (!/^未命名/.test(t.name)) return;
    var txt = (state.mode === 'rich' ? editor.innerText : source.value).trim();
    if (!txt) return;
    var first = txt.split('\n')[0].replace(/\s+/g, ' ').trim();
    if (!first) return;
    t.name = first.length > 14 ? first.slice(0, 14) + '…' : first;
    var tab = tabsBox.children[state.active];
    if (tab) {
      var nm = tab.querySelector('.name');
      if (nm) nm.textContent = t.name;
    }
    stFile.textContent = t.name;
  }

  document.addEventListener('selectionchange', function () {
    var sel = window.getSelection();
    if (sel && sel.rangeCount && editor.contains(sel.anchorNode)) {
      savedRange = sel.getRangeAt(0).cloneRange();
    }
  });

  editor.addEventListener('input', onEdit);
  source.addEventListener('input', onEdit);

  // 工具栏：按下时不要抢焦点，否则选区会丢
  document.querySelectorAll('.tb[data-cmd]').forEach(function (b) {
    b.addEventListener('mousedown', function (e) { e.preventDefault(); });
    b.addEventListener('click', function () {
      exec(b.dataset.cmd);
    });
  });

  /* ---------- 色板 ---------- */

  COLORS.forEach(function (c) {
    var b = document.createElement('button');
    b.className = 'sw';
    b.type = 'button';
    b.style.background = c;
    b.title = c;
    b.addEventListener('mousedown', function (e) { e.preventDefault(); });
    b.addEventListener('click', function () { exec('foreColor', c); });
    swatches.appendChild(b);
  });

  var pick = document.getElementById('colorPick');
  pick.addEventListener('input', function () { exec('foreColor', pick.value); });

  document.getElementById('fontSize').addEventListener('change', function (e) {
    exec('fontSize', e.target.value);
    e.target.value = '3';
  });

  /* ---------- 源码模式 ---------- */

  document.getElementById('toggleSource').addEventListener('click', function () {
    var t = current();
    if (!t) return;
    if (state.mode === 'rich') {
      t.html = editor.innerHTML;
      state.mode = 'source';
    } else {
      t.html = source.value;
      state.mode = 'rich';
    }
    save(true);
    syncView();
  });

  /* ---------- 复制 / 导出 ---------- */

  function plainText() {
    return state.mode === 'rich' ? editor.innerText : source.value;
  }

  function htmlText() {
    return state.mode === 'rich' ? editor.innerHTML : source.value;
  }

  document.getElementById('btnCopy').addEventListener('click', function () {
    var btn = this;
    var done = function () {
      var old = btn.textContent;
      btn.textContent = '已复制';
      setTimeout(function () { btn.textContent = old; }, 1000);
    };
    var html = htmlText();
    var text = plainText();
    if (navigator.clipboard && window.ClipboardItem) {
      try {
        var item = new ClipboardItem({
          'text/html': new Blob([html], { type: 'text/html' }),
          'text/plain': new Blob([text], { type: 'text/plain' })
        });
        navigator.clipboard.write([item]).then(done, function () { fallbackCopy(text, done); });
        return;
      } catch (e) {}
    }
    fallbackCopy(text, done);
  });

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

  document.getElementById('btnDownload').addEventListener('click', function () {
    var t = current();
    if (!t) return;
    var name = (t.name || '未命名').replace(/[\\/:*?"<>|]/g, '_');
    var body = htmlText();
    var isHtml = /<\w+[\s>]/.test(body);
    var content, ext;

    if (isHtml) {
      content = '<!DOCTYPE html>\n<html lang="zh-CN">\n<head>\n<meta charset="utf-8" />\n<title>' +
        name + '</title>\n</head>\n<body style="font:15px/1.75 \'Microsoft YaHei UI\',sans-serif">\n' +
        body + '\n</body>\n</html>\n';
      ext = '.html';
    } else {
      content = body;
      ext = '.txt';
    }

    var blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name + ext;
    document.body.appendChild(a);
    a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  });

  /* ---------- 快捷键 ---------- */

  document.addEventListener('keydown', function (e) {
    if (!(e.ctrlKey || e.metaKey)) return;
    if (e.key === 's') {
      e.preventDefault();
      save(true);
      stSaved.textContent = '已保存';
    }
  });

  /* ---------- 启动 ---------- */

  load();
  syncView();
  save(true);
  window.addEventListener('beforeunload', function () {
    storeCurrent();
    try { localStorage.setItem(LS, JSON.stringify({ tabs: state.tabs, active: state.active })); } catch (e) {}
  });
})();
