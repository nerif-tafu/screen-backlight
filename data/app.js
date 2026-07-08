'use strict';

const PATTERNS = [
  { id: 'off', label: 'Off' },
  { id: 'solid_white', label: 'Solid White' },
  { id: 'solid_red', label: 'Solid Red' },
  { id: 'solid_green', label: 'Solid Green' },
  { id: 'solid_blue', label: 'Solid Blue' },
  { id: 'rainbow', label: 'Rainbow' },
  { id: 'color_wipe', label: 'Color Wipe' },
  { id: 'theater_chase', label: 'Theater Chase' },
  { id: 'breathing_white', label: 'Breathing White' },
  { id: 'rotating_hue', label: 'Rotating Hue' },
  { id: 'edge_identification', label: 'Edge ID' },
];

const EDGE_FIELDS = [
  ['left', 'left-start', 'left-end'],
  ['right', 'right-start', 'right-end'],
  ['top', 'top-start', 'top-end'],
  ['bottom', 'bottom-start', 'bottom-end'],
];

let config = {};
let ws = null;
let wsReconnectTimer = null;
let statusTimer = null;
let otaPollTimer = null;

const $ = (sel) => document.querySelector(sel);

function showToast(msg, ms = 2500) {
  const el = $('#toast');
  el.textContent = msg;
  el.classList.remove('hidden');
  el.classList.add('show');
  clearTimeout(showToast._t);
  showToast._t = setTimeout(() => {
    el.classList.add('hidden');
    el.classList.remove('show');
  }, ms);
}

async function api(method, path, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    opts.headers['Content-Type'] = 'application/json';
    opts.body = JSON.stringify(body);
  }
  const res = await fetch(path, opts);
  const text = await res.text();
  let data;
  try { data = JSON.parse(text); } catch { data = { raw: text }; }
  if (!res.ok) throw new Error(data.error || res.statusText);
  return data;
}

function spanLength(start, end) {
  return Math.abs(end - start) + 1;
}

function edgeIndices(start, end) {
  const indices = [];
  const step = start <= end ? 1 : -1;
  for (let i = start; ; i += step) {
    indices.push(i);
    if (i === end) break;
  }
  return indices;
}

function readEdge(name) {
  return {
    start: parseInt($(`#${name}-start`).value, 10) || 0,
    end: parseInt($(`#${name}-end`).value, 10) || 0,
  };
}

function computedStripLength() {
  let max = 0;
  for (const [name] of EDGE_FIELDS) {
    const edge = readEdge(name);
    max = Math.max(max, edge.start, edge.end);
  }
  return max + 1;
}

function readLayoutFromForm() {
  const body = {
    totalLedCount: computedStripLength(),
  };
  for (const [name] of EDGE_FIELDS) {
    const edge = readEdge(name);
    body[`${name}Start`] = edge.start;
    body[`${name}End`] = edge.end;
  }
  return body;
}

function updateLayoutSummary() {
  const stripLen = computedStripLength();
  let total = 0;

  for (const [name] of EDGE_FIELDS) {
    const edge = readEdge(name);
    total += spanLength(edge.start, edge.end);
  }

  $('#strip-length').textContent = stripLen;
  $('#layout-total').textContent = total;
  $('#layout-warning').classList.add('hidden');
}

function applyConfigToForm(c) {
  config = c;
  $('#left-start').value = c.leftStart ?? 0;
  $('#left-end').value = c.leftEnd ?? 29;
  $('#right-start').value = c.rightStart ?? 60;
  $('#right-end').value = c.rightEnd ?? 89;
  $('#top-start').value = c.topStart ?? 30;
  $('#top-end').value = c.topEnd ?? 59;
  $('#bottom-start').value = c.bottomStart ?? 90;
  $('#bottom-end').value = c.bottomEnd ?? 119;
  $('#brightness').value = c.brightness ?? 128;
  $('#brightness-value').textContent = c.brightness ?? 128;
  $('#gamma-correction').checked = c.gammaCorrection !== false;
  $('#max-fps').value = c.maxFps ?? 60;
  $('#color-order').value = c.colorOrder ?? 'GRB';
  $('#rev-left').checked = !!c.reverseLeft;
  $('#rev-top').checked = !!c.reverseTop;
  $('#rev-right').checked = !!c.reverseRight;
  $('#rev-bottom').checked = !!c.reverseBottom;
  if (c.wifiSsid) $('#wifi-ssid').value = c.wifiSsid;
  updateLayoutSummary();
  drawPreview();
}

function getAdvancedPayload() {
  return {
    colorOrder: $('#color-order').value,
    gammaCorrection: $('#gamma-correction').checked,
    maxFps: parseInt($('#max-fps').value, 10) || 0,
    reverseLeft: $('#rev-left').checked,
    reverseTop: $('#rev-top').checked,
    reverseRight: $('#rev-right').checked,
    reverseBottom: $('#rev-bottom').checked,
  };
}

async function loadConfig() {
  const c = await api('GET', '/api/config');
  applyConfigToForm(c);
}

async function saveLayout() {
  const c = await api('POST', '/api/config', readLayoutFromForm());
  applyConfigToForm(c);
  showToast('Layout saved');
}

async function saveBrightnessSettings() {
  const val = parseInt($('#brightness').value, 10);
  const c = await api('POST', '/api/config', {
    brightness: val,
    gammaCorrection: $('#gamma-correction').checked,
    maxFps: parseInt($('#max-fps').value, 10) || 0,
  });
  applyConfigToForm(c);
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ cmd: 'brightness', value: val }));
  }
  showToast('Brightness settings saved');
}

async function saveAdvanced() {
  const c = await api('POST', '/api/config', getAdvancedPayload());
  applyConfigToForm(c);
  showToast('Advanced settings saved');
}

async function saveWifi() {
  const ssid = $('#wifi-ssid').value.trim();
  const password = $('#wifi-password').value;
  if (!ssid) {
    showToast('SSID required');
    return;
  }
  await api('POST', '/api/config', { wifiSsid: ssid, wifiPassword: password });
  showToast('Wi-Fi saved — reboot to connect');
}

async function runPattern(pattern) {
  await api('POST', '/api/testpattern', { pattern });
  document.querySelectorAll('.pattern-grid .btn').forEach((b) => {
    b.classList.toggle('active', b.dataset.pattern === pattern);
  });
  showToast(`Pattern: ${pattern}`);
}

async function rebootDevice() {
  if (!confirm('Reboot the device?')) return;
  await api('POST', '/api/reboot');
  showToast('Rebooting…');
}

function setOtaProgress(pct) {
  $('#ota-progress-wrap').classList.remove('hidden');
  $('#ota-bar-fill').style.width = `${pct}%`;
  $('#ota-progress-text').textContent = `${pct}%`;
}

async function pollOtaStatus() {
  try {
    const s = await api('GET', '/api/ota/status');
    setOtaProgress(s.progress ?? 0);
    if (!s.active && s.progress >= 100) {
      clearInterval(otaPollTimer);
      showToast('Update complete — rebooting');
    }
  } catch {
    clearInterval(otaPollTimer);
  }
}

async function uploadFirmware() {
  const file = $('#ota-file').files[0];
  if (!file) {
    showToast('Choose a firmware.bin file');
    return;
  }
  if (!confirm(`Upload ${file.name} (${(file.size / 1024).toFixed(0)} KB)?`)) return;

  setOtaProgress(0);
  otaPollTimer = setInterval(pollOtaStatus, 500);

  try {
    const res = await fetch('/api/ota/update', {
      method: 'POST',
      headers: { 'Content-Type': 'application/octet-stream' },
      body: file,
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.error || res.statusText);
    }
    showToast('Firmware uploaded — device rebooting');
  } catch (err) {
    clearInterval(otaPollTimer);
    showToast(err.message || 'OTA failed');
  }
}

function formatUptime(sec) {
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  if (h > 0) return `${h}h ${m}m ${s}s`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

async function refreshStatus() {
  try {
    const s = await api('GET', '/api/status');
    $('#info-ip').textContent = s.ip ?? '—';
    $('#info-hostname').textContent = (s.hostname ?? '—') + '.local';
    $('#info-rssi').textContent = s.apMode ? 'AP mode' : `${s.rssi} dBm`;
    $('#info-heap').textContent = `${(s.freeHeap / 1024).toFixed(1)} KB`;
    $('#info-uptime').textContent = formatUptime(s.uptime ?? 0);
    $('#info-fps').textContent = (s.wsFps ?? 0).toFixed(1);
    $('#info-clients').textContent = s.wsClients ?? 0;
    $('#info-firmware').textContent = s.firmwareVersion ?? '—';
    $('#info-pattern').textContent = s.testPattern ?? 'none';
    if (s.otaActive) setOtaProgress(s.otaProgress ?? 0);
  } catch {
    /* device may be rebooting */
  }
}

function connectWebSocket() {
  if (ws) {
    ws.onclose = null;
    ws.close();
  }

  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  ws = new WebSocket(`${proto}//${location.host}/ws`);
  ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    clearTimeout(wsReconnectTimer);
  };

  ws.onclose = () => {
    wsReconnectTimer = setTimeout(connectWebSocket, 3000);
  };

  ws.onerror = () => ws.close();
}

function drawEdgeOnPreview(ctx, indices, edgeKey, pad, innerW, innerH) {
  const edgeColors = { left: '#ef4444', top: '#22c55e', right: '#3b82f6', bottom: '#cbd5e1' };
  const color = edgeColors[edgeKey];
  const n = indices.length;

  for (let i = 0; i < n; i++) {
    const t = n <= 1 ? 0.5 : i / (n - 1);
    let x = 0;
    let y = 0;

    if (edgeKey === 'left') {
      x = pad - 14;
      y = pad + innerH - t * innerH;
    } else if (edgeKey === 'top') {
      x = pad + t * innerW;
      y = pad - 14;
    } else if (edgeKey === 'right') {
      x = pad + innerW + 14;
      y = pad + t * innerH;
    } else {
      x = pad + innerW - t * innerW;
      y = pad + innerH + 14;
    }

    drawLedDot(ctx, x, y, color, indices[i]);
  }
}

function drawPreview() {
  const canvas = $('#preview-canvas');
  const ctx = canvas.getContext('2d');
  const W = canvas.width;
  const H = canvas.height;
  const pad = 60;
  const innerW = W - pad * 2;
  const innerH = H - pad * 2;

  ctx.clearRect(0, 0, W, H);

  ctx.fillStyle = getComputedStyle(document.body).getPropertyValue('--surface2').trim() || '#243044';
  ctx.strokeStyle = getComputedStyle(document.body).getPropertyValue('--border').trim() || '#2d3a4f';
  ctx.lineWidth = 2;
  roundRect(ctx, pad, pad, innerW, innerH, 8);
  ctx.fill();
  ctx.stroke();

  // Preview order around monitor: left, top, right, bottom
  const previewOrder = ['left', 'top', 'right', 'bottom'];
  for (const name of previewOrder) {
    const edge = readEdge(name);
    drawEdgeOnPreview(ctx, edgeIndices(edge.start, edge.end), name, pad, innerW, innerH);
  }

  ctx.fillStyle = getComputedStyle(document.body).getPropertyValue('--text-muted').trim();
  ctx.font = '13px Segoe UI, system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText('Monitor (labels = strip index)', W / 2, H / 2);
}

function drawLedDot(ctx, x, y, color, stripIndex) {
  ctx.beginPath();
  ctx.arc(x, y, 5, 0, Math.PI * 2);
  ctx.fillStyle = color;
  ctx.fill();
  ctx.strokeStyle = 'rgba(0,0,0,0.3)';
  ctx.lineWidth = 1;
  ctx.stroke();

  ctx.fillStyle = '#fff';
  ctx.font = '8px monospace';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(String(stripIndex), x, y - 10);
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.lineTo(x + w - r, y);
  ctx.quadraticCurveTo(x + w, y, x + w, y + r);
  ctx.lineTo(x + w, y + h - r);
  ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
  ctx.lineTo(x + r, y + h);
  ctx.quadraticCurveTo(x, y + h, x, y + h - r);
  ctx.lineTo(x, y + r);
  ctx.quadraticCurveTo(x, y, x + r, y);
  ctx.closePath();
}

function buildPatternButtons() {
  const container = $('#pattern-buttons');
  PATTERNS.forEach((p) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn';
    btn.dataset.pattern = p.id;
    btn.textContent = p.label;
    btn.addEventListener('click', () => runPattern(p.id));
    container.appendChild(btn);
  });
}

function initEvents() {
  const layoutInputs = [];
  for (const [, startId, endId] of EDGE_FIELDS) {
    layoutInputs.push(startId, endId);
  }
  layoutInputs.forEach((id) => {
    $(`#${id}`).addEventListener('input', () => {
      updateLayoutSummary();
      drawPreview();
    });
  });

  $('#save-layout').addEventListener('click', saveLayout);
  $('#save-advanced').addEventListener('click', saveAdvanced);
  $('#save-wifi').addEventListener('click', saveWifi);
  $('#reboot-btn').addEventListener('click', rebootDevice);
  $('#ota-upload-btn').addEventListener('click', uploadFirmware);

  $('#save-brightness').addEventListener('click', saveBrightnessSettings);

  $('#brightness').addEventListener('input', (e) => {
    $('#brightness-value').textContent = parseInt(e.target.value, 10);
  });

  $('#export-config').addEventListener('click', async () => {
    const data = await api('GET', '/api/config/export');
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'backlight-config.json';
    a.click();
    URL.revokeObjectURL(a.href);
  });

  $('#import-config').addEventListener('click', () => $('#import-file').click());
  $('#import-file').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    const text = await file.text();
    await fetch('/api/config/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: text,
    });
    await loadConfig();
    showToast('Configuration imported');
    e.target.value = '';
  });
}

async function init() {
  buildPatternButtons();
  initEvents();
  await loadConfig();
  connectWebSocket();
  refreshStatus();
  statusTimer = setInterval(refreshStatus, 2000);
}

init().catch((err) => showToast(err.message || 'Init failed'));
