// Touch and mouse navigation of the room map SVG (WEB_BLAZOR.md 5.4). Everything runs in the browser by editing the viewBox:
// no event goes to the server during a gesture. Room squares are real links, so a plain tap navigates even without this script;
// when a room square is smaller than 44px at the current zoom, a tap selects the nearest room and offers "Open room".
const states = new WeakMap();
const HIT = 44;
const MAX_ZOOM = 8;   // times the fitted size
const MIN_ZOOM = 0.25;

export function attach(host) {
  if (!host) return;
  const svg = host.querySelector('svg');
  if (!svg) return;
  const w = parseFloat(svg.dataset.w), h = parseFloat(svg.dataset.h);
  let st = states.get(host);
  if (st && st.w === w && st.h === h && st.svg === svg) return; // same map, same element: keep the user's view
  if (st) st.dispose();
  st = create(host, svg, w, h);
  states.set(host, st);
}

export function detach(host) {
  const st = states.get(host);
  if (st) { st.dispose(); states.delete(host); }
}

function create(host, svg, w, h) {
  let view = { x: 0, y: 0, w, h };
  const pointers = new Map();
  let pinch = null, dragMoved = false, lastTap = 0;
  const sheet = host.querySelector('.mud-map-sheet');

  const apply = () => svg.setAttribute('viewBox', `${view.x} ${view.y} ${view.w} ${view.h}`);
  const rect = () => svg.getBoundingClientRect();
  const clampZoom = (nw) => Math.min(Math.max(nw, w / MAX_ZOOM), w / MIN_ZOOM);
  const toSvg = (cx, cy) => { const r = rect(); return { x: view.x + (cx - r.left) / r.width * view.w, y: view.y + (cy - r.top) / r.height * view.h }; };

  function zoomAt(factor, cx, cy) {
    const nw = clampZoom(view.w / factor);
    const f = nw / view.w;
    const p = toSvg(cx, cy);
    view = { x: p.x - (p.x - view.x) * f, y: p.y - (p.y - view.y) * f, w: nw, h: view.h * f };
    apply();
  }
  function zoomCentre(factor) { const r = rect(); zoomAt(factor, r.left + r.width / 2, r.top + r.height / 2); }
  function fit() { view = { x: 0, y: 0, w, h }; apply(); hideSheet(); }
  function pan(dxPx, dyPx) { const r = rect(); view.x -= dxPx / r.width * view.w; view.y -= dyPx / r.height * view.h; apply(); }

  function hideSheet() { if (sheet) sheet.hidden = true; }
  function showSheet(a) {
    if (!sheet) return;
    sheet.querySelector('.mud-map-sheet-name').textContent = a.dataset.room;
    sheet.querySelector('a').setAttribute('href', a.getAttribute('href'));
    sheet.hidden = false;
  }
  // Apparent size of a room square on screen, in CSS px.
  function squarePx() { const r = rect(); return 120 * (r.width / view.w); }
  function nearestRoom(cx, cy) {
    const p = toSvg(cx, cy);
    const scale = rect().width / view.w;
    let best = null, bestD = Infinity;
    for (const a of svg.querySelectorAll('a[data-room]')) {
      const d = Math.hypot(parseFloat(a.dataset.x) - p.x, parseFloat(a.dataset.y) - p.y) * scale;
      if (d < bestD) { bestD = d; best = a; }
    }
    return bestD <= 22 + squarePx() / 2 ? best : null;
  }

  function onDown(e) {
    if (e.target.closest('.mud-map-tools, .mud-map-sheet')) return;
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    dragMoved = false;
    if (pointers.size === 2) { const [a, b] = [...pointers.values()]; pinch = { d: Math.hypot(a.x - b.x, a.y - b.y) }; }
  }
  function onMove(e) {
    const prev = pointers.get(e.pointerId);
    if (!prev) return;
    const cur = { x: e.clientX, y: e.clientY };
    if (pointers.size === 1) {
      const dx = cur.x - prev.x, dy = cur.y - prev.y;
      if (!dragMoved && Math.hypot(dx, dy) < 6) return; // a tap, not a drag
      if (!dragMoved) { dragMoved = true; try { host.setPointerCapture(e.pointerId); } catch (_) { /* ignore */ } }
      pan(dx, dy);
    } else if (pointers.size === 2 && pinch) {
      pointers.set(e.pointerId, cur);
      const [a, b] = [...pointers.values()];
      const d = Math.hypot(a.x - b.x, a.y - b.y);
      if (pinch.d > 0) zoomAt(d / pinch.d, (a.x + b.x) / 2, (a.y + b.y) / 2);
      pinch.d = d;
      dragMoved = true;
      return;
    }
    pointers.set(e.pointerId, cur);
  }
  function onUp(e) {
    const had = pointers.delete(e.pointerId);
    if (pointers.size < 2) pinch = null;
    if (!had) return;
    if (dragMoved) { dragMoved = false; return; }
    // A tap. Double tap zooms in. A tap on a room that is too small to hit selects the nearest room instead of following the link.
    const now = Date.now();
    if (now - lastTap < 300) { zoomAt(2, e.clientX, e.clientY); lastTap = 0; return; }
    lastTap = now;
    if (squarePx() < HIT) {
      const room = nearestRoom(e.clientX, e.clientY);
      if (room) { showSheet(room); suppressClickUntil = Date.now() + 400; } else hideSheet();
    } else hideSheet();
  }
  let suppressClickUntil = 0;
  function onClick(e) {
    if (Date.now() < suppressClickUntil || dragMoved) { e.preventDefault(); e.stopPropagation(); }
  }
  function onCancel(e) { pointers.delete(e.pointerId); if (pointers.size < 2) pinch = null; }
  function onWheel(e) { e.preventDefault(); zoomAt(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.clientX, e.clientY); }
  function onKey(e) {
    const step = 40;
    switch (e.key) {
      case 'ArrowLeft': pan(step, 0); break;
      case 'ArrowRight': pan(-step, 0); break;
      case 'ArrowUp': pan(0, step); break;
      case 'ArrowDown': pan(0, -step); break;
      case '+': case '=': zoomCentre(1.25); break;
      case '-': case '_': zoomCentre(0.8); break;
      default: return;
    }
    e.preventDefault();
  }
  function onTool(e) {
    const b = e.target.closest('[data-map]');
    if (!b) return;
    const a = b.dataset.map;
    if (a === 'in') zoomCentre(1.4); else if (a === 'out') zoomCentre(1 / 1.4); else fit();
  }

  host.style.touchAction = 'none';
  host.addEventListener('pointerdown', onDown);
  host.addEventListener('pointermove', onMove);
  host.addEventListener('pointerup', onUp);
  host.addEventListener('pointercancel', onCancel);
  host.addEventListener('click', onClick, true);
  host.addEventListener('wheel', onWheel, { passive: false });
  host.addEventListener('keydown', onKey);
  host.querySelector('.mud-map-tools')?.addEventListener('click', onTool);

  return {
    w, h, svg,
    dispose() {
      host.removeEventListener('pointerdown', onDown);
      host.removeEventListener('pointermove', onMove);
      host.removeEventListener('pointerup', onUp);
      host.removeEventListener('pointercancel', onCancel);
      host.removeEventListener('click', onClick, true);
      host.removeEventListener('wheel', onWheel);
      host.removeEventListener('keydown', onKey);
      host.querySelector('.mud-map-tools')?.removeEventListener('click', onTool);
    },
  };
}
