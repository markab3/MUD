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
