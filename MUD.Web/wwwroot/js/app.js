// Reconnect banner (WEB_BLAZOR.md 6.2): Blazor sets components-reconnect-show / -failed / -rejected on #components-reconnect-modal.
(function () {
  var retry = document.getElementById('mud-retry');
  var reload = document.getElementById('mud-reload');
  if (retry) retry.addEventListener('click', function () {
    if (window.Blazor && Blazor.reconnect) { Blazor.reconnect().then(function (ok) { if (!ok) location.reload(); }); }
    else location.reload();
  });
  if (reload) reload.addEventListener('click', function () { location.reload(); });
})();

// While the circuit is not connected (WEB_BLAZOR.md 6.2) every mutating button is disabled so a click cannot queue: buttons with class
// mud-save (Save, Delete, Regen, Move, Update, confirm) or attribute data-mutating. One helper for all pages; it re-enables only the
// buttons it disabled itself when the connection returns.
window.mudReconnectGuard = (function () {
  var SELECTOR = '.mud-save, [data-mutating]';
  var MARK = 'data-reconnect-disabled';
  function apply(root, reconnecting) {
    var buttons = root.querySelectorAll(SELECTOR);
    for (var i = 0; i < buttons.length; i++) {
      var b = buttons[i];
      if (reconnecting) {
        if (!b.disabled) { b.disabled = true; b.setAttribute(MARK, '1'); }
      } else if (b.hasAttribute(MARK)) {
        b.disabled = false; b.removeAttribute(MARK);
      }
    }
  }
  function isReconnecting(modal) {
    var c = modal.classList;
    return c.contains('components-reconnect-show') || c.contains('components-reconnect-failed') || c.contains('components-reconnect-rejected');
  }
  function start() {
    var modal = document.getElementById('components-reconnect-modal');
    if (!modal || !window.MutationObserver) return;
    new MutationObserver(function () { apply(document, isReconnecting(modal)); }).observe(modal, { attributes: true, attributeFilter: ['class'] });
  }
  return { apply: apply, start: start };
})();
window.mudReconnectGuard.start();

// Local drafts of the editor forms (WEB_BLAZOR.md 6.4): localStorage, 24 hours, key draft:<user>:<route>. Removed on logout.
window.mudDraft = (function () {
  var TTL = 24 * 60 * 60 * 1000;
  function ok() { try { return !!window.localStorage; } catch (e) { return false; } }
  return {
    set: function (key, json) { if (ok()) { try { localStorage.setItem(key, JSON.stringify({ t: Date.now(), v: json })); } catch (e) { } } },
    get: function (key) {
      if (!ok()) return null;
      try {
        var raw = localStorage.getItem(key);
        if (!raw) return null;
        var o = JSON.parse(raw);
        if (!o || Date.now() - o.t > TTL) { localStorage.removeItem(key); return null; }
        return o.v;
      } catch (e) { return null; }
    },
    remove: function (key) { if (ok()) { try { localStorage.removeItem(key); } catch (e) { } } },
    clearAll: function () {
      if (!ok()) return;
      try { Object.keys(localStorage).filter(function (k) { return k.indexOf('draft:') === 0; }).forEach(function (k) { localStorage.removeItem(k); }); } catch (e) { }
    }
  };
})();
// Logging out removes this device's drafts.
document.addEventListener('submit', function (e) {
  if (e.target && e.target.action && /\/logout$/.test(e.target.action)) window.mudDraft.clearAll();
}, true);

// Pull-out menu (WEB_BLAZOR.md 5.2): focus moves into the dialog when it opens and back to the menu button when it closes, and Tab stays
// inside the dialog while it is open. Open/close state itself lives in the NavShell component.
window.mudMenu = (function () {
  var FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select, textarea, [tabindex]:not([tabindex="-1"])';
  var active = null;
  function items(menu) { return Array.prototype.slice.call(menu.querySelectorAll(FOCUSABLE)); }
  function trap(e) {
    if (e.key !== 'Tab' || !active) return;
    var list = items(active);
    if (!list.length) { e.preventDefault(); return; }
    var first = list[0], last = list[list.length - 1];
    if (!active.contains(document.activeElement)) { e.preventDefault(); first.focus(); }
    else if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }
  return {
    opened: function (menuId) {
      active = document.getElementById(menuId);
      if (!active) return;
      document.addEventListener('keydown', trap, true);
      var list = items(active);
      if (list.length) list[0].focus();
    },
    closed: function (buttonId) {
      document.removeEventListener('keydown', trap, true);
      active = null;
      var b = document.getElementById(buttonId);
      if (b) b.focus();
    }
  };
})();

// Live filter boxes: set the text (and optionally focus) without recreating the input, so the phone keyboard stays up.
window.mudLive = {
  set: function (el, text, focus) {
    if (!el) return;
    if (el.value !== text) el.value = text;
    if (focus) el.focus();
  }
};
