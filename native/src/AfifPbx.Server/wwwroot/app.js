"use strict";
/* AFIF-PBX web panel */
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fmtTime = t => t ? new Date(t).toLocaleString() : "";
const fmtDur = s => { s = Math.max(0, s | 0); const h = s / 3600 | 0, m = (s % 3600) / 60 | 0, x = s % 60; return (h ? h + ":" + String(m).padStart(2, "0") : m) + ":" + String(x).padStart(2, "0"); };
const fmtUptime = s => { const d = s / 86400 | 0, h = (s % 86400) / 3600 | 0, m = (s % 3600) / 60 | 0; return (d ? d + "d " : "") + h + "h " + m + "m"; };

let state = { status: null, timer: null };

// ------------------------------------------------------------ API
async function api(path, opts = {}) {
  const o = { method: opts.method || "GET", headers: {} };
  if (opts.body !== undefined && !(opts.body instanceof FormData)) { o.headers["Content-Type"] = "application/json"; o.body = JSON.stringify(opts.body); }
  else if (opts.body) o.body = opts.body;
  const r = await fetch("/api" + path, o);
  if (r.status === 401 && path !== "/login") { showLogin(); throw new Error("Please sign in"); }
  const type = r.headers.get("content-type") || "";
  const data = type.includes("json") ? await r.json() : opts.raw ? await r.blob() : await r.text();
  if (!r.ok) throw new Error((data && data.error) || r.statusText || "Request failed");
  return data;
}

function toast(msg, err = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.className = "toast" + (err ? " err" : "");
  clearTimeout(t._h);
  t._h = setTimeout(() => t.classList.add("hidden"), 3500);
}
const run = fn => async (...a) => { try { await fn(...a); } catch (e) { toast(e.message, true); } };

// ------------------------------------------------------------ login + intro
function showLogin() {
  $("#app").classList.add("hidden");
  $("#login").classList.remove("hidden");
  startField();
}

$("#loginForm").addEventListener("submit", async e => {
  e.preventDefault();
  const f = new FormData(e.target);
  $("#loginError").textContent = "";
  try {
    const me = await api("/login", { method: "POST", body: { user: f.get("user"), password: f.get("password") } });
    enterApp(me);
  } catch (err) { $("#loginError").textContent = err.message; }
});

$("#logout").addEventListener("click", async () => { await api("/logout", { method: "POST" }).catch(() => {}); showLogin(); });
$("#menuBtn").addEventListener("click", () => $(".side").classList.toggle("open"));

function startField() {
  const c = $("#field"); if (!c || c._running) return; c._running = true;
  const x = c.getContext("2d"); let w, h, dpr, pts = [], t = 0;
  const size = () => { dpr = devicePixelRatio || 1; w = c.width = innerWidth * dpr; h = c.height = innerHeight * dpr; c.style.width = innerWidth + "px"; c.style.height = innerHeight + "px";
    pts = Array.from({ length: Math.round(innerWidth * innerHeight / 15000) }, () => ({ x: Math.random() * w, y: Math.random() * h, vx: (Math.random() - .5) * .35 * dpr, vy: (Math.random() - .5) * .35 * dpr })); };
  size(); addEventListener("resize", size);
  const frame = () => {
    if ($("#login").classList.contains("hidden")) { c._running = false; return; }
    x.clearRect(0, 0, w, h); t += .008;
    for (let k = 0; k < 3; k++) { x.beginPath(); x.lineWidth = 1.2 * dpr; x.strokeStyle = `rgba(56,225,255,${.1 - k * .025})`;
      for (let X = 0; X <= w; X += 8 * dpr) { const Y = h * .74 + Math.sin(X / (180 * dpr) + t * (2 + k) + k) * 28 * dpr * (1 + k * .6); X ? x.lineTo(X, Y) : x.moveTo(X, Y); } x.stroke(); }
    for (let i = 0; i < pts.length; i++) { const p = pts[i]; p.x += p.vx; p.y += p.vy; if (p.x < 0 || p.x > w) p.vx *= -1; if (p.y < 0 || p.y > h) p.vy *= -1;
      x.fillStyle = "rgba(127,166,217,.55)"; x.fillRect(p.x, p.y, 1.6 * dpr, 1.6 * dpr);
      for (let j = i + 1; j < pts.length; j++) { const q = pts[j], dx = p.x - q.x, dy = p.y - q.y, d = dx * dx + dy * dy, m = (120 * dpr) ** 2;
        if (d < m) { x.strokeStyle = `rgba(56,225,255,${.12 * (1 - d / m)})`; x.lineWidth = dpr; x.beginPath(); x.moveTo(p.x, p.y); x.lineTo(q.x, q.y); x.stroke(); } } }
    requestAnimationFrame(frame);
  };
  frame();
}

async function enterApp(me) {
  $("#login").classList.add("hidden");
  $("#app").classList.remove("hidden");
  $("#version").textContent = "AFIF-PBX " + (me.version || "");
  route();
  if (me.mustChangePassword) setTimeout(changePasswordDialog, 400, true);
}

// ------------------------------------------------------------ router
const pages = {};
function route() {
  const name = (location.hash.replace("#/", "") || "dashboard").split("?")[0];
  const page = pages[name] || pages.dashboard;
  $$("#nav a").forEach(a => a.classList.toggle("active", a.getAttribute("href") === "#/" + name));
  $(".side").classList.remove("open");
  clearInterval(state.timer);
  $("#title").textContent = page.title;
  $("#view").innerHTML = '<div class="empty">Loading…</div>';
  run(page.render)();
  if (page.refresh) state.timer = setInterval(run(page.render), page.refresh);
}
addEventListener("hashchange", route);

// ------------------------------------------------------------ helpers
let cfgCache = null;
async function loadCfg() {
  const [extensions, ringgroups, ivrs] = await Promise.all([api("/extensions"), api("/ringgroups"), api("/ivrs")]);
  cfgCache = { extensions, ringgroups, ivrs };
  return cfgCache;
}
function destOptions(cfg, selected, { includeGsm = false } = {}) {
  const o = [];
  const add = (v, l) => o.push(`<option value="${esc(v)}" ${v === selected ? "selected" : ""}>${esc(l)}</option>`);
  add("hangup:", "Hang up");
  cfg.extensions.forEach(e => add("ext:" + e.number, `Extension ${e.number} – ${e.name}`));
  cfg.ringgroups.forEach(r => add("rg:" + r.number, `Ring group ${r.number} – ${r.name}`));
  cfg.ivrs.forEach(i => add("ivr:" + i.number, `IVR ${i.number} – ${i.name}`));
  cfg.extensions.filter(e => e.voicemailEnabled).forEach(e => add("vm:" + e.number, `Voicemail of ${e.number} – ${e.name}`));
  if (selected && !o.some(x => x.includes(`value="${esc(selected)}"`))) add(selected, selected);
  return o.join("");
}
function describeDest(d) {
  if (!d) return "—";
  const [t, a] = d.split(":");
  return ({ ext: "Extension ", rg: "Ring group ", ivr: "IVR ", vm: "Voicemail ", hangup: "Hang up", gsm: "GSM " }[t] || t) + (a || "");
}
function signalBars(pct) {
  const n = Math.round(pct / 20);
  return `<span class="signal" title="${pct}%">${[1, 2, 3, 4, 5].map(i => `<i class="${i <= n ? "on" : ""}" style="height:${4 + i * 3}px"></i>`).join("")}</span>`;
}
function modemPill(m) {
  if (!m) return '<span class="pill">Off</span>';
  const map = { ready: ["good", "Ready"], "no-voice": ["warn", "SMS only"], starting: ["warn", "Starting"], "no-sim": ["bad", "SIM problem"],
    "no-device": ["bad", "Not plugged in"], "no-driver": ["bad", "Driver missing"], "port-busy": ["bad", "Port busy"], error: ["bad", "Error"] };
  const [cls, label] = map[m.state] || ["", m.state];
  return `<span class="pill ${cls}">${label}</span>`;
}
function openModal(title, html, onMount) {
  $("#modalTitle").textContent = title;
  $("#modalBody").innerHTML = html;
  $("#modal").classList.remove("hidden");
  onMount && onMount($("#modalBody"));
}
function closeModal() { $("#modal").classList.add("hidden"); $("#modalBody").innerHTML = ""; }
$("#modalClose").addEventListener("click", closeModal);
$("#modal").addEventListener("click", e => { if (e.target.id === "modal") closeModal(); });
function formData(form) {
  const o = {};
  $$("[name]", form).forEach(el => { o[el.name] = el.type === "checkbox" ? el.checked : el.type === "number" ? Number(el.value) : el.value; });
  return o;
}
function copy(text) { navigator.clipboard?.writeText(text).then(() => toast("Copied")); }
window.copyText = copy;

// ------------------------------------------------------------ dashboard
pages.dashboard = {
  title: "Dashboard", refresh: 3000,
  async render() {
    const s = await api("/status");
    state.status = s;
    $("#vmBadge").textContent = s.newVoicemails || "";
    $("#topStatus").innerHTML = `<span><span class="dot ${s.modem?.state === "ready" ? "on" : "warn"}"></span>GSM ${esc(s.modem?.operator || s.modem?.state || "off")}</span>
      <span><span class="dot on"></span>${s.extensions.filter(e => e.registered).length}/${s.extensions.length} phones</span>`;
    const m = s.modem;
    const regd = s.extensions.filter(e => e.registered).length;
    $("#view").innerHTML = `
      <div class="grid">
        <div class="card"><h3>System</h3>
          <div class="stat">Running</div>
          <div class="sub">Up ${fmtUptime(s.uptimeSeconds)} · version ${esc(s.version)}</div>
          <div class="sub" style="margin-top:10px">Phones register to <b class="mono">${esc((s.addresses[0] || "this PC") + ":" + s.sipPort)}</b></div>
        </div>
        <div class="card"><h3>GSM line</h3>
          ${m ? `<div class="row">${modemPill(m)} ${signalBars(m.signalPercent)} <span class="sub">${m.signalPercent}%</span></div>
            <div class="stat" style="font-size:20px;margin-top:8px">${esc(m.operator || "—")}</div>
            <div class="sub">${esc(m.registration || m.message || "")}${m.callState !== "idle" ? " · on a call" : ""}</div>` : `<div class="sub">The GSM line is switched off in Settings.</div>`}
        </div>
        <div class="card"><h3>Phones</h3><div class="stat">${regd} <span class="sub">of ${s.extensions.length} online</span></div>
          <div class="row" style="margin-top:8px">${s.extensions.map(e => `<span class="pill ${e.registered ? "good" : ""}" title="${esc(e.name)}">${esc(e.number)}</span>`).join("")}</div></div>
        <div class="card"><h3>Messages</h3><div class="stat">${s.newVoicemails}</div><div class="sub">new voicemail${s.newVoicemails === 1 ? "" : "s"}</div>
          <div class="sub" style="margin-top:6px">${s.recentSms.length ? "Latest SMS: " + esc(s.recentSms[0].number) : "No SMS yet"}</div></div>
        <div class="card wide"><h3>Calls in progress</h3>
          ${s.activeCalls.length ? `<div class="table-wrap"><table><tr><th>From</th><th>To</th><th>Status</th><th>Since</th><th></th></tr>
            ${s.activeCalls.map(c => `<tr><td>${esc(c.fromName && c.fromName !== c.from ? c.fromName + " · " : "")}${esc(c.from)}</td><td>${esc(c.to)}${c.connectedTo ? " → " + esc(c.connectedTo) : ""}</td>
              <td><span class="pill ${c.state === "connected" ? "good" : "warn"}">${esc(c.state)}</span>${c.recording ? ' <span class="pill bad">● REC</span>' : ""}</td>
              <td>${fmtDur((Date.now() - new Date(c.answer || c.start)) / 1000)}</td>
              <td><button class="btn small danger" data-hangup="${esc(c.id)}">Hang up</button></td></tr>`).join("")}</table></div>`
            : '<div class="empty">No calls right now</div>'}
        </div>
        <div class="card wide"><h3>Recent calls</h3>${callTable(s.recentCalls)}</div>
      </div>`;
    $$("[data-hangup]").forEach(b => b.addEventListener("click", run(async () => { await api(`/calls/${b.dataset.hangup}/hangup`, { method: "POST" }); toast("Call ended"); })));
  }
};

function callTable(calls) {
  if (!calls.length) return '<div class="empty">No calls yet</div>';
  return `<div class="table-wrap"><table><tr><th>When</th><th>From</th><th>To</th><th>Type</th><th>Result</th><th>Duration</th><th></th></tr>
    ${calls.map(c => `<tr><td>${fmtTime(c.start)}</td><td>${esc(c.fromName && c.fromName !== c.fromNumber ? c.fromName + " · " : "")}${esc(c.fromNumber)}</td><td>${esc(c.toNumber)}</td>
      <td>${esc(c.direction)}</td><td><span class="pill ${c.disposition === "ANSWERED" ? "good" : c.disposition === "VOICEMAIL" ? "" : "warn"}">${esc(c.disposition.toLowerCase())}</span></td>
      <td>${fmtDur(c.durationSeconds)}</td><td>${c.recording ? `<audio controls preload="none" src="/api/recordings/file?path=${encodeURIComponent(c.recording)}"></audio>` : ""}</td></tr>`).join("")}</table></div>`;
}

// ------------------------------------------------------------ extensions
pages.extensions = {
  title: "Extensions",
  async render() {
    const [cfg, status] = await Promise.all([loadCfg(), api("/status")]);
    const reg = Object.fromEntries(status.extensions.map(e => [e.number, e]));
    const server = (status.addresses[0] || "PC-IP") + (status.sipPort === 5060 ? "" : ":" + status.sipPort);
    $("#view").innerHTML = `
      <div class="toolbar"><button class="btn primary" id="add">+ Add extension</button><span class="spacer"></span>
        <span class="sub">Phone settings: server <b class="mono">${esc(server)}</b>, user name = extension number, password = secret</span></div>
      <div class="table-wrap"><table><tr><th>Number</th><th>Name</th><th>Phone</th><th>Voicemail</th><th>Recording</th><th>If unanswered</th><th></th></tr>
      ${cfg.extensions.map(e => `<tr><td><b>${esc(e.number)}</b></td><td>${esc(e.name)}</td>
        <td><span class="dot ${reg[e.number]?.registered ? "on" : "off"}"></span>${reg[e.number]?.registered ? esc(reg[e.number].devices[0]?.userAgent || "online") : "offline"}</td>
        <td>${e.voicemailEnabled ? "On" : "Off"}</td><td>${e.recordCalls ? "Always" : "Off"}</td>
        <td>${esc(e.noAnswerDestination ? describeDest(e.noAnswerDestination) : e.voicemailEnabled ? "Voicemail" : "Hang up")}</td>
        <td><button class="btn small" data-edit="${esc(e.number)}">Edit</button></td></tr>`).join("") || '<tr><td colspan="7" class="empty">No extensions</td></tr>'}
      </table></div>`;
    $("#add").addEventListener("click", () => extensionDialog(cfg, null, server));
    $$("[data-edit]").forEach(b => b.addEventListener("click", () => extensionDialog(cfg, cfg.extensions.find(e => e.number === b.dataset.edit), server)));
  }
};

async function extensionDialog(cfg, ext, server) {
  const prompts = await api("/prompts");
  const isNew = !ext;
  ext = ext || { number: suggestNumber(cfg, 100), name: "", secret: "", enabled: true, voicemailEnabled: true, voicemailPin: "1234", recordCalls: false, noAnswerDestination: "", greetingFile: "" };
  openModal(isNew ? "New extension" : `Extension ${ext.number}`, `
    <form id="f"><div class="form-grid">
      <label>Number<input name="number" value="${esc(ext.number)}" required pattern="\\d{2,6}"></label>
      <label>Name<input name="name" value="${esc(ext.name)}"></label>
    </div>
    <label>SIP password (secret)<div class="secret"><input name="secret" value="${esc(ext.secret)}" placeholder="leave empty to generate">
      <button type="button" class="btn small" id="gen">Generate</button><button type="button" class="btn small" id="cp">Copy</button></div></label>
    <div class="form-grid">
      <label>If not answered<select name="noAnswerDestination"><option value="">${ext.voicemailEnabled ? "Voicemail (default)" : "Hang up (default)"}</option>${destOptions(cfg, ext.noAnswerDestination)}</select></label>
      <label>Voicemail greeting<select name="greetingFile"><option value="">Spoken greeting</option>${prompts.map(p => `<option ${p === ext.greetingFile ? "selected" : ""}>${esc(p)}</option>`).join("")}</select></label>
    </div>
    <label class="check"><input type="checkbox" name="enabled" ${ext.enabled ? "checked" : ""}> Enabled</label>
    <label class="check"><input type="checkbox" name="voicemailEnabled" ${ext.voicemailEnabled ? "checked" : ""}> Voicemail box</label>
    <label class="check"><input type="checkbox" name="recordCalls" ${ext.recordCalls ? "checked" : ""}> Record all calls of this extension</label>
    <input type="hidden" name="voicemailPin" value="${esc(ext.voicemailPin || "1234")}">
    <div class="notice info">Phone setup: server <b class="mono">${esc(server)}</b> · user <b class="mono">${esc(ext.number)}</b> · password = the secret above. Dial <b>*97</b> for voicemail, <b>*43</b> for an echo test.</div>
    <div class="modal-actions">${isNew ? "" : '<button type="button" class="btn danger" id="del">Delete</button><span class="spacer"></span>'}
      <button type="button" class="btn" onclick="document.getElementById('modalClose').click()">Cancel</button><button class="btn primary">Save</button></div></form>`, body => {
    $("#gen", body).addEventListener("click", () => { $("[name=secret]", body).value = randomSecret(); });
    $("#cp", body).addEventListener("click", () => copy($("[name=secret]", body).value));
    $("#del", body)?.addEventListener("click", run(async () => {
      if (!confirm(`Delete extension ${ext.number}?`)) return;
      await api(`/extensions/${ext.number}`, { method: "DELETE" }); closeModal(); toast("Deleted"); route();
    }));
    $("#f", body).addEventListener("submit", run(async e => {
      e.preventDefault();
      const d = formData(e.target);
      await api(isNew ? "/extensions" : `/extensions/${ext.number}`, { method: isNew ? "POST" : "PUT", body: d });
      closeModal(); toast("Saved"); route();
    }));
  });
}
function randomSecret() { const c = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789"; return Array.from(crypto.getRandomValues(new Uint32Array(12)), x => c[x % c.length]).join(""); }
function suggestNumber(cfg, start) {
  const used = new Set([...cfg.extensions, ...cfg.ringgroups, ...cfg.ivrs].map(x => x.number));
  let n = start; while (used.has(String(n))) n++; return String(n);
}

// ------------------------------------------------------------ ring groups
pages.ringgroups = {
  title: "Ring groups",
  async render() {
    const cfg = await loadCfg();
    $("#view").innerHTML = `<div class="toolbar"><button class="btn primary" id="add">+ Add ring group</button></div>
      <div class="table-wrap"><table><tr><th>Number</th><th>Name</th><th>Members</th><th>Strategy</th><th>Rings for</th><th>If unanswered</th><th></th></tr>
      ${cfg.ringgroups.map(r => `<tr><td><b>${esc(r.number)}</b></td><td>${esc(r.name)}</td><td>${r.members.map(esc).join(", ")}</td>
        <td>${r.strategy === "hunt" ? "One after another" : "All at once"}</td><td>${r.ringSeconds}s</td><td>${esc(describeDest(r.failoverDestination))}</td>
        <td><button class="btn small" data-edit="${esc(r.number)}">Edit</button></td></tr>`).join("") || '<tr><td colspan="7" class="empty">No ring groups</td></tr>'}</table></div>`;
    $("#add").addEventListener("click", () => ringGroupDialog(cfg, null));
    $$("[data-edit]").forEach(b => b.addEventListener("click", () => ringGroupDialog(cfg, cfg.ringgroups.find(r => r.number === b.dataset.edit))));
  }
};
function ringGroupDialog(cfg, rg) {
  const isNew = !rg;
  rg = rg || { number: suggestNumber(cfg, 600), name: "", members: [], strategy: "ringall", ringSeconds: 20, failoverDestination: "hangup:" };
  openModal(isNew ? "New ring group" : `Ring group ${rg.number}`, `<form id="f">
    <div class="form-grid"><label>Number<input name="number" value="${esc(rg.number)}" required></label><label>Name<input name="name" value="${esc(rg.name)}"></label>
    <label>Strategy<select name="strategy"><option value="ringall" ${rg.strategy !== "hunt" ? "selected" : ""}>Ring all at once</option><option value="hunt" ${rg.strategy === "hunt" ? "selected" : ""}>One after another</option></select></label>
    <label>Ring time (seconds)<input name="ringSeconds" type="number" min="5" max="120" value="${rg.ringSeconds}"></label></div>
    <label>Members</label><div class="form-grid" style="margin-bottom:12px">${cfg.extensions.map(e => `<label class="check"><input type="checkbox" data-member="${esc(e.number)}" ${rg.members.includes(e.number) ? "checked" : ""}> ${esc(e.number)} – ${esc(e.name)}</label>`).join("")}</div>
    <label>If nobody answers<select name="failoverDestination">${destOptions(cfg, rg.failoverDestination)}</select></label>
    <div class="modal-actions">${isNew ? "" : '<button type="button" class="btn danger" id="del">Delete</button><span class="spacer"></span>'}<button class="btn primary">Save</button></div></form>`, body => {
    $("#del", body)?.addEventListener("click", run(async () => { if (!confirm("Delete this ring group?")) return; await api(`/ringgroups/${rg.number}`, { method: "DELETE" }); closeModal(); route(); }));
    $("#f", body).addEventListener("submit", run(async e => {
      e.preventDefault();
      const d = formData(e.target);
      d.members = $$("[data-member]", body).filter(c => c.checked).map(c => c.dataset.member);
      await api(isNew ? "/ringgroups" : `/ringgroups/${rg.number}`, { method: isNew ? "POST" : "PUT", body: d });
      closeModal(); toast("Saved"); route();
    }));
  });
}

// ------------------------------------------------------------ IVR
pages.ivrs = {
  title: "IVR menus",
  async render() {
    const cfg = await loadCfg();
    $("#view").innerHTML = `<div class="toolbar"><button class="btn primary" id="add">+ Add IVR menu</button></div>
      <div class="table-wrap"><table><tr><th>Number</th><th>Name</th><th>Options</th><th>No choice</th><th></th></tr>
      ${cfg.ivrs.map(i => `<tr><td><b>${esc(i.number)}</b></td><td>${esc(i.name)}</td>
        <td>${Object.entries(i.options).map(([k, v]) => `<span class="pill">${esc(k)} → ${esc(describeDest(v))}</span>`).join(" ")}</td>
        <td>${esc(describeDest(i.timeoutDestination))}</td><td><button class="btn small" data-edit="${esc(i.number)}">Edit</button></td></tr>`).join("") || '<tr><td colspan="5" class="empty">No IVR menus</td></tr>'}</table></div>`;
    $("#add").addEventListener("click", () => ivrDialog(cfg, null));
    $$("[data-edit]").forEach(b => b.addEventListener("click", () => ivrDialog(cfg, cfg.ivrs.find(i => i.number === b.dataset.edit))));
  }
};
async function ivrDialog(cfg, ivr) {
  const prompts = await api("/prompts");
  const isNew = !ivr;
  ivr = ivr || { number: suggestNumber(cfg, 700), name: "", promptText: "Welcome. For sales press 1. For support press 2.", promptFile: "", options: { "1": "hangup:" }, timeoutDestination: "hangup:", timeoutSeconds: 6, directDial: true };
  const optRow = (k, v) => `<div class="opt-row"><input data-key value="${esc(k)}" maxlength="1" placeholder="1"><select data-dest>${destOptions(cfg, v)}</select><button type="button" class="btn small" data-rm>✕</button></div>`;
  openModal(isNew ? "New IVR menu" : `IVR ${ivr.number}`, `<form id="f">
    <div class="form-grid"><label>Number<input name="number" value="${esc(ivr.number)}" required></label><label>Name<input name="name" value="${esc(ivr.name)}"></label></div>
    <label>Greeting (spoken by Windows text-to-speech)<textarea name="promptText" rows="3" dir="auto">${esc(ivr.promptText)}</textarea></label>
    <div class="row" style="margin:-4px 0 12px"><button type="button" class="btn small" id="preview">▶ Preview speech</button><audio id="pv" controls class="hidden"></audio></div>
    <label>…or play an uploaded recording<select name="promptFile"><option value="">Use the spoken greeting</option>${prompts.map(p => `<option ${p === ivr.promptFile ? "selected" : ""}>${esc(p)}</option>`).join("")}</select></label>
    <label>Key options</label><div class="opts" id="opts">${Object.entries(ivr.options).map(([k, v]) => optRow(k, v)).join("")}</div>
    <button type="button" class="btn small" id="addOpt" style="margin-bottom:14px">+ Add key</button>
    <div class="form-grid"><label>If no key is pressed<select name="timeoutDestination">${destOptions(cfg, ivr.timeoutDestination)}</select></label>
    <label>Wait for a key (seconds)<input type="number" name="timeoutSeconds" min="2" max="30" value="${ivr.timeoutSeconds}"></label></div>
    <label class="check"><input type="checkbox" name="directDial" ${ivr.directDial ? "checked" : ""}> Callers may dial an extension number directly</label>
    <div class="modal-actions">${isNew ? "" : '<button type="button" class="btn danger" id="del">Delete</button><span class="spacer"></span>'}<button class="btn primary">Save</button></div></form>`, body => {
    const opts = $("#opts", body);
    const wire = () => $$("[data-rm]", opts).forEach(b => b.onclick = () => b.parentElement.remove());
    wire();
    $("#addOpt", body).addEventListener("click", () => { opts.insertAdjacentHTML("beforeend", optRow("", "hangup:")); wire(); });
    $("#preview", body).addEventListener("click", run(async () => {
      const blob = await api("/speech/preview", { method: "POST", body: { text: $("[name=promptText]", body).value }, raw: true });
      const a = $("#pv", body); a.src = URL.createObjectURL(blob); a.classList.remove("hidden"); a.play();
    }));
    $("#del", body)?.addEventListener("click", run(async () => { if (!confirm("Delete this IVR?")) return; await api(`/ivrs/${ivr.number}`, { method: "DELETE" }); closeModal(); route(); }));
    $("#f", body).addEventListener("submit", run(async e => {
      e.preventDefault();
      const d = formData(e.target);
      d.options = {};
      $$(".opt-row", opts).forEach(r => { const k = $("[data-key]", r).value.trim(); if (k) d.options[k] = $("[data-dest]", r).value; });
      delete d[""];
      await api(isNew ? "/ivrs" : `/ivrs/${ivr.number}`, { method: isNew ? "POST" : "PUT", body: d });
      closeModal(); toast("Saved"); route();
    }));
  });
}

// ------------------------------------------------------------ GSM line
pages.gsm = {
  title: "GSM line", refresh: 5000,
  async render() {
    if ($("#ussdOut")?.dataset.busy) return;
    const [g, settings, cfg] = await Promise.all([api("/gsm"), api("/settings"), cfgCache ? Promise.resolve(cfgCache) : loadCfg()]);
    const m = g.status;
    const help = {
      "no-driver": "Windows sees the dongle but it has no driver. Open the Start menu → AFIF-PBX → \"Install dongle driver\" (answer Yes to the administrator prompt), then unplug and re-plug the dongle. No Huawei software is needed.",
      "port-busy": "Another program holds the dongle. Exit HUAWEI Mobile Partner from the system tray and stop it starting with Windows.",
      "no-sim": "Check the SIM is inserted and its PIN is turned off.",
      "no-voice": "This dongle's firmware does not allow voice calls. SMS still works.",
      "no-device": "Plug in the Huawei dongle (E303, E1550, E173…).",
    };
    $("#view").innerHTML = `
      ${!g.enabled ? '<div class="notice">The GSM line is switched off. Turn it on in Settings.</div>' : ""}
      ${m && help[m.state] ? `<div class="notice">${esc(help[m.state])}</div>` : ""}
      <div class="grid">
        <div class="card"><h3>Status</h3>${m ? `<div class="row">${modemPill(m)} ${signalBars(m.signalPercent)} <span class="sub">${m.signalPercent}%</span></div>
          <div class="stat" style="font-size:20px;margin-top:8px">${esc(m.operator || "—")}</div><div class="sub">${esc(m.message || "")}</div>` : "Off"}</div>
        <div class="card"><h3>Dongle</h3>${m ? `<dl class="kv">
          <dt>Model</dt><dd>${esc((m.manufacturer + " " + m.model).trim() || "—")}</dd><dt>Firmware</dt><dd>${esc(m.firmware || "—")}</dd>
          <dt>IMEI</dt><dd class="mono">${esc(m.imei || "—")}</dd><dt>SIM number</dt><dd class="mono">${esc(m.ownNumber || "—")}</dd>
          <dt>Voice</dt><dd>${m.voiceEnabled === false ? "Disabled in firmware" : m.voiceEnabled ? "Enabled" : "—"}</dd>
          <dt>Ports</dt><dd class="mono">${esc(m.atPort || "?")} / ${esc(m.audioPort || "?")}</dd><dt>Call</dt><dd>${esc(m.callState)}</dd></dl>` : ""}
          <div class="row" style="margin-top:12px"><button class="btn small" id="reconnect">Reconnect</button></div></div>
        <div class="card"><h3>Balance / USSD</h3><form id="ussd" class="row"><input name="code" placeholder="*100#" style="max-width:150px"><button class="btn small">Send</button></form>
          <p class="sub" id="ussdOut" style="white-space:pre-wrap" dir="auto"></p></div>
        <div class="card wide"><h3>Routing</h3><form id="route"><div class="form-grid">
          <label>Calls to the SIM go to<select name="gsmInboundDestination">${destOptions(cfg, settings.gsmInboundDestination)}</select></label>
          <label>Numbers that go out on the SIM (regular expression)<input name="outboundPattern" value="${esc(settings.outboundPattern)}"></label>
          <label>Remove this prefix before dialling out<input name="outboundStripPrefix" value="${esc(settings.outboundStripPrefix)}" placeholder="e.g. 9"></label>
          <label>Receive volume (dB)<input type="number" name="gsmRxGain" min="-12" max="12" value="${settings.gsmRxGain}"></label>
          <label>Send volume (dB)<input type="number" name="gsmTxGain" min="-12" max="12" value="${settings.gsmTxGain}"></label>
          <label>AT port (empty = automatic)<select name="gsmAtPort"><option value="">Automatic</option>${g.ports.map(p => `<option value="${esc(p.port)}" ${p.port === settings.gsmAtPort ? "selected" : ""}>${esc(p.name)}</option>`).join("")}</select></label>
          <label>Voice port (empty = automatic)<select name="gsmAudioPort"><option value="">Automatic</option>${g.ports.map(p => `<option value="${esc(p.port)}" ${p.port === settings.gsmAudioPort ? "selected" : ""}>${esc(p.name)}</option>`).join("")}</select></label>
        </div><label class="check"><input type="checkbox" name="recordGsmCalls" ${settings.recordGsmCalls ? "checked" : ""}> Record every call on the GSM line</label>
        <div class="modal-actions"><button class="btn primary">Save</button></div></form></div>
        <div class="card wide"><h3>Devices Windows sees</h3><div class="mono sub">${g.huaweiDevices.map(esc).join("<br>") || "No Huawei device"}</div>
          <h3 style="margin-top:14px">COM ports</h3><div class="mono sub">${g.ports.map(p => esc(p.name)).join("<br>") || "None"}</div></div>
      </div>`;
    $("#reconnect").addEventListener("click", run(async () => { await api("/gsm/reconnect", { method: "POST" }); toast("Reconnecting…"); }));
    $("#ussd").addEventListener("submit", run(async e => {
      e.preventDefault(); const out = $("#ussdOut"); out.dataset.busy = "1"; out.textContent = "Waiting for the network…";
      try { const r = await api("/gsm/ussd", { method: "POST", body: { code: new FormData(e.target).get("code") } }); out.textContent = r.reply; }
      catch (err) { out.textContent = err.message; } finally { delete out.dataset.busy; }
    }));
    $("#route").addEventListener("submit", run(async e => {
      e.preventDefault();
      const s = await api("/settings");
      Object.assign(s, formData(e.target));
      await api("/settings", { method: "PUT", body: s }); toast("Saved");
    }));
    $("#route").addEventListener("focusin", () => clearInterval(state.timer), { once: true });
  }
};

// ------------------------------------------------------------ SMS
pages.sms = {
  title: "SMS", refresh: 6000,
  async render() {
    if (document.activeElement && $("#smsForm")?.contains(document.activeElement)) return;
    const list = await api("/sms");
    $("#view").innerHTML = `<div class="grid"><div class="card wide"><h3>New message</h3>
        <form id="smsForm"><div class="form-grid"><label>To<input name="number" placeholder="07XXXXXXXX or +9627XXXXXXXX" required></label></div>
        <label>Message<textarea name="text" rows="3" dir="auto" required></textarea></label>
        <div class="row"><span class="sub" id="cnt"></span><span class="spacer"></span><button class="btn primary">Send</button></div></form></div>
      <div class="card wide"><h3>Messages</h3><div class="sms-list">${list.map(m => `<div class="sms ${m.direction === "out" ? "out" : ""}">
          <div class="meta">${m.direction === "out" ? "To" : "From"} <b>${esc(m.number)}</b> · ${fmtTime(m.time)} · ${esc(m.status)}
          <button class="link" data-reply="${esc(m.number)}">Reply</button> · <button class="link" data-del="${m.id}">Delete</button></div>
          <div class="text" dir="auto">${esc(m.text)}</div></div>`).join("") || '<div class="empty">No messages yet</div>'}</div></div></div>`;
    const f = $("#smsForm");
    const count = () => { const t = f.text.value; const uni = /[^\x00-\x7F£¥èéùìòÇØøÅåÆæßÉÄÖÑÜäöñüà§¿¡€]/.test(t); const per = uni ? 70 : 160; $("#cnt").textContent = `${t.length} characters · ${Math.max(1, Math.ceil(t.length / (t.length > per ? (uni ? 67 : 153) : per)))} SMS${uni ? " (Unicode)" : ""}`; };
    f.text.addEventListener("input", count); count();
    f.addEventListener("submit", run(async e => { e.preventDefault(); await api("/sms", { method: "POST", body: formData(f) }); toast("Sent"); f.reset(); pages.sms.render(); }));
    $$("[data-reply]").forEach(b => b.addEventListener("click", () => { f.number.value = b.dataset.reply; f.text.focus(); }));
    $$("[data-del]").forEach(b => b.addEventListener("click", run(async () => { await api(`/sms/${b.dataset.del}`, { method: "DELETE" }); pages.sms.render(); })));
    $("#smsBadge").textContent = "";
  }
};

// ------------------------------------------------------------ voicemail
pages.voicemail = {
  title: "Voicemail",
  async render() {
    const box = new URLSearchParams(location.hash.split("?")[1] || "").get("box") || "";
    const [list, cfg] = await Promise.all([api("/voicemail" + (box ? "?mailbox=" + box : "")), loadCfg()]);
    $("#view").innerHTML = `<div class="toolbar"><select id="box" style="max-width:260px"><option value="">All mailboxes</option>
        ${cfg.extensions.filter(e => e.voicemailEnabled).map(e => `<option value="${esc(e.number)}" ${e.number === box ? "selected" : ""}>${esc(e.number)} – ${esc(e.name)}</option>`).join("")}</select>
        <span class="sub">From a phone, dial <b>*97</b> to listen.</span></div>
      <div class="table-wrap"><table><tr><th></th><th>Mailbox</th><th>From</th><th>When</th><th>Length</th><th>Listen</th><th></th></tr>
      ${list.map(v => `<tr><td>${v.heard ? "" : '<span class="dot on" title="new"></span>'}</td><td>${esc(v.mailbox)}</td><td>${esc(v.caller)}</td><td>${fmtTime(v.time)}</td><td>${fmtDur(v.durationSeconds)}</td>
        <td><audio controls preload="none" data-id="${v.id}" src="/api/voicemail/${v.id}/audio"></audio></td>
        <td><a class="btn small" href="/api/voicemail/${v.id}/audio" download>Download</a> <button class="btn small danger" data-del="${v.id}">Delete</button></td></tr>`).join("") || '<tr><td colspan="7" class="empty">No voicemail</td></tr>'}</table></div>`;
    $("#box").addEventListener("change", e => location.hash = "#/voicemail" + (e.target.value ? "?box=" + e.target.value : ""));
    $$("audio[data-id]").forEach(a => a.addEventListener("play", () => api(`/voicemail/${a.dataset.id}/heard`, { method: "POST" }).catch(() => {}), { once: true }));
    $$("[data-del]").forEach(b => b.addEventListener("click", run(async () => { if (!confirm("Delete this message?")) return; await api(`/voicemail/${b.dataset.del}`, { method: "DELETE" }); route(); })));
  }
};

// ------------------------------------------------------------ recordings
pages.recordings = {
  title: "Recordings",
  async render() {
    const list = await api("/recordings");
    $("#view").innerHTML = `<div class="notice info">Turn recording on per extension (Extensions → Edit) or for the whole GSM line (GSM line → Routing). Recordings are stereo: caller on the left, the other party on the right.</div>
      <div class="table-wrap"><table><tr><th>When</th><th>From</th><th>To</th><th>Length</th><th>Listen</th><th></th></tr>
      ${list.map(r => `<tr><td>${fmtTime(r.time)}</td><td>${esc(r.from)}</td><td>${esc(r.to)}</td><td>${fmtDur(r.seconds)}</td>
        <td><audio controls preload="none" src="/api/recordings/file?path=${encodeURIComponent(r.path)}"></audio></td>
        <td><a class="btn small" href="/api/recordings/file?path=${encodeURIComponent(r.path)}" download>Download</a> <button class="btn small danger" data-del="${esc(r.path)}">Delete</button></td></tr>`).join("") || '<tr><td colspan="6" class="empty">No recordings</td></tr>'}</table></div>`;
    $$("[data-del]").forEach(b => b.addEventListener("click", run(async () => { if (!confirm("Delete this recording?")) return; await api("/recordings?path=" + encodeURIComponent(b.dataset.del), { method: "DELETE" }); route(); })));
  }
};

// ------------------------------------------------------------ call log
pages.calls = {
  title: "Call log",
  async render() {
    const q = $("#q")?.value || "";
    const list = await api("/calls?limit=500" + (q ? "&search=" + encodeURIComponent(q) : ""));
    $("#view").innerHTML = `<div class="toolbar"><input id="q" placeholder="Search number or name" value="${esc(q)}"><button class="btn" id="go">Search</button></div>${callTable(list)}`;
    $("#go").addEventListener("click", run(pages.calls.render));
    $("#q").addEventListener("keydown", e => { if (e.key === "Enter") run(pages.calls.render)(); });
  }
};

// ------------------------------------------------------------ prompts
pages.prompts = {
  title: "Prompts",
  async render() {
    const [files, voices, settings] = await Promise.all([api("/prompts"), api("/voices"), api("/settings")]);
    $("#view").innerHTML = `<div class="grid">
      <div class="card"><h3>Upload a recording</h3><form id="up"><input type="file" name="file" accept=".wav,audio/wav" required>
        <p class="sub">WAV files of any quality are converted to telephone format automatically.</p><button class="btn primary">Upload</button></form></div>
      <div class="card"><h3>Text-to-speech</h3><form id="tts"><label>Voice<select name="ttsVoice"><option value="">Windows default (Arabic voice for Arabic text)</option>
        ${voices.map(v => `<option ${v === settings.ttsVoice ? "selected" : ""}>${esc(v)}</option>`).join("")}</select></label>
        <label>Try it<textarea id="say" rows="2" dir="auto">Welcome to AFIF PBX.</textarea></label>
        <div class="row"><button type="button" class="btn small" id="play">▶ Listen</button><button class="btn small primary">Save voice</button><audio id="pa" class="hidden" controls></audio></div></form>
        <p class="sub">Add Arabic voices in Windows Settings → Time &amp; language → Speech.</p></div>
      <div class="card wide"><h3>Uploaded prompts</h3><div class="table-wrap"><table>${files.map(f => `<tr><td class="mono">${esc(f)}</td>
        <td><audio controls preload="none" src="/api/prompts/${encodeURIComponent(f)}/audio"></audio></td>
        <td><button class="btn small danger" data-del="${esc(f)}">Delete</button></td></tr>`).join("") || '<tr><td class="empty">No uploaded prompts</td></tr>'}</table></div></div></div>`;
    $("#up").addEventListener("submit", run(async e => { e.preventDefault(); await api("/prompts", { method: "POST", body: new FormData(e.target) }); toast("Uploaded"); route(); }));
    $("#play").addEventListener("click", run(async () => { const b = await api("/speech/preview", { method: "POST", body: { text: $("#say").value }, raw: true }); const a = $("#pa"); a.src = URL.createObjectURL(b); a.classList.remove("hidden"); a.play(); }));
    $("#tts").addEventListener("submit", run(async e => { e.preventDefault(); const s = await api("/settings"); s.ttsVoice = e.target.ttsVoice.value; await api("/settings", { method: "PUT", body: s }); toast("Saved"); }));
    $$("[data-del]").forEach(b => b.addEventListener("click", run(async () => { await api("/prompts/" + encodeURIComponent(b.dataset.del), { method: "DELETE" }); route(); })));
  }
};


// ------------------------------------------------------------ remote access (AFIF Bridge)
pages.bridge = {
  title: "Remote access", refresh: 5000,
  async render() {
    if (document.activeElement && $("#bridgeForm")?.contains(document.activeElement)) return;
    if ($("#bridgeBusy") || $("#bridgeForm [name=apiToken]")?.value) return;
    const [b, st] = await Promise.all([api("/bridge"), api("/status")]);
    const lan = st.addresses[0] || "";
    const pill = b.connected ? '<span class="pill good">Connected</span>' : b.configured && b.enabled ? '<span class="pill warn">' + esc(b.message) + "</span>" : '<span class="pill">' + esc(b.message) + "</span>";
    const team = b.teamDomain ? b.teamDomain.split(".")[0] : "";
    const exts = st.extensions.map(e => e.number);
    $("#view").innerHTML = `
      <div class="notice info">The AFIF Bridge puts AFIF-PBX on your own domain through <b>Cloudflare</b>, with <b>no router ports opened</b>.
        It uses its own tunnel named <b>afif-pbx</b>, so your other tunnels and hostnames (for example Node-RED) are not changed.</div>
      <div class="grid">
        <div class="card"><h3>Bridge</h3><div class="row">${pill}</div>
          ${b.panelUrl ? `<div class="stat" style="font-size:18px;margin-top:10px"><a href="${esc(b.panelUrl)}" target="_blank">${esc(b.panelUrl.replace("https://", ""))}</a></div><div class="sub">Web panel from anywhere</div>` : '<div class="sub" style="margin-top:8px">Not set up yet</div>'}
          ${b.configured ? `<div class="row" style="margin-top:12px"><button class="btn small" id="toggle">${b.enabled ? "Switch off" : "Switch on"}</button>
            <button class="btn small danger" id="remove">Remove</button></div>` : ""}</div>
        <div class="card"><h3>Phones outside your network</h3>
          ${b.sipHostname ? `<dl class="kv"><dt>SIP server</dt><dd class="mono">${esc(b.sipHostname)}</dd><dt>Transport</dt><dd>UDP</dd>
            <dt>PBX address</dt><dd class="mono">${esc(b.lanIp)}</dd><dt>Cloudflare One team</dt><dd class="mono">${esc(team || "—")}</dd>
            <dt>Split tunnel</dt><dd>${b.splitTunnelAdjusted ? "Ready" : "Check Zero Trust settings"}</dd></dl>` : '<div class="sub">Set up the bridge first.</div>'}</div>
        <div class="card wide"><h3>${b.configured ? "Run setup again" : "Set up"}</h3>
          <form id="bridgeForm"><div class="form-grid">
            <label>Your domain<input name="zone" value="${esc(b.zone || "powerchain.uk")}" required></label>
            <label>Web panel name<input name="panelHostname" value="${esc((b.panelUrl || "").replace("https://", "").split(".")[0] || "pbx")}"></label>
            <label>SIP name (for Linphone)<input name="sipHostname" value="${esc((b.sipHostname || "").split(".")[0] || "sip")}"></label>
            <label>This PC's address<input name="lanIp" value="${esc(b.lanIp || lan)}"></label>
          </div>
          <label>Cloudflare API token<input name="apiToken" type="password" autocomplete="off" placeholder="paste the token here" required></label>
          <label class="check"><input type="checkbox" name="adjustSplitTunnel" checked> Let the Cloudflare One app send this PC's address through the bridge (adjusts the split tunnel)</label>
          <details style="margin:6px 0 14px"><summary class="sub" style="cursor:pointer">How to create the API token</summary>
            <ol class="sub">
              <li>Cloudflare dashboard → <b>My Profile → API Tokens → Create Token → Create Custom Token</b>.</li>
              <li>Permissions: <b>Account · Cloudflare Tunnel · Edit</b>, <b>Account · Zero Trust · Edit</b>, <b>Account · Access: Organizations, Identity Providers, and Groups · Read</b>, <b>Zone · DNS · Edit</b>, <b>Zone · Zone · Read</b>.</li>
              <li>Account resources: your account. Zone resources: <b>${esc(b.zone || "powerchain.uk")}</b>. Create, then copy the token here.</li>
              <li>The token is stored encrypted on this PC only.</li>
            </ol></details>
          <div class="row"><button class="btn primary">Set up the bridge</button><span class="spacer"></span>
            <button type="button" class="link" id="manual">I already have a tunnel token</button></div></form>
          ${b.lastSetupLog?.length ? `<h3 style="margin-top:18px">Last setup</h3><div class="mono sub" style="white-space:pre-wrap">${b.lastSetupLog.map(esc).join("\n")}</div>` : ""}
        </div>
        <div class="card wide"><h3>Linphone on your mobile — step by step</h3>
          <ol>
            <li>Install <b>Cloudflare One</b> (by Cloudflare) from the App Store or Google Play.</li>
            <li>Open it → <b>Log in with Zero Trust</b> → team name <b class="mono">${esc(team || "your team name")}</b> → sign in with your email (Cloudflare sends a code). Then switch it <b>on</b>.
              <div class="sub">First time only: in one.dash.cloudflare.com → Settings → WARP Client → <b>Device enrollment permissions</b>, add a rule that allows your email address.</div></li>
            <li>Install <b>Linphone</b> → <b>Use SIP account</b>:
              <dl class="kv" style="margin:8px 0"><dt>Username</dt><dd>your extension (${exts.map(esc).join(", ")})</dd><dt>Password</dt><dd>the extension's secret (Extensions → Edit)</dd>
              <dt>Domain</dt><dd class="mono">${esc(b.sipHostname || "sip." + (b.zone || "powerchain.uk"))}</dd><dt>Transport</dt><dd>UDP</dd></dl></li>
            <li>Make a test call to <b>*43</b> (echo test): you should hear yourself.</li>
          </ol>
          <p class="sub">Calls and voice go phone → Cloudflare → this PC through the encrypted Cloudflare One connection. Nothing is opened on your router.</p></div>
        ${b.recentConnectorLog?.length ? `<div class="card wide"><h3>Connector log</h3><div class="mono sub" style="white-space:pre-wrap;max-height:240px;overflow:auto">${b.recentConnectorLog.map(esc).join("\n")}</div></div>` : ""}
      </div>`;
    $("#bridgeForm").addEventListener("submit", run(async e => {
      e.preventDefault();
      const d = formData(e.target);
      const btn = e.target.querySelector(".btn.primary");
      btn.disabled = true; btn.textContent = "Setting up…"; btn.id = "bridgeBusy";
      try {
        const r = await fetch("/api/bridge/setup", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(d) });
        const j = await r.json();
        toast(j.ok ? "Bridge is set up" : "Setup stopped — see the log", !j.ok);
      } finally {
        btn.removeAttribute("id");
        const t = $("#bridgeForm [name=apiToken]"); if (t) t.value = "";
        document.activeElement?.blur();
        pages.bridge.render();
      }
    }));
    $("#manual").addEventListener("click", () => openModal("Use an existing tunnel token", `<form id="f">
      <p class="sub">Create a tunnel in Cloudflare Zero Trust → Networks → Tunnels, copy its token, and add a public hostname pointing to <b class="mono">http://localhost:${st.httpPort}</b>.</p>
      <label>Tunnel token<input name="token" required></label><label>Public hostname of the panel<input name="panelHostname" placeholder="pbx.powerchain.uk"></label>
      <div class="modal-actions"><button class="btn primary">Save and connect</button></div></form>`, body => {
        $("#f", body).addEventListener("submit", run(async ev => { ev.preventDefault(); await api("/bridge/token", { method: "POST", body: formData(ev.target) }); closeModal(); toast("Saved"); pages.bridge.render(); }));
      }));
    $("#toggle")?.addEventListener("click", run(async () => { await api("/bridge/enabled", { method: "POST", body: { on: !b.enabled } }); pages.bridge.render(); }));
    $("#remove")?.addEventListener("click", run(async () => {
      if (!confirm("Remove the bridge from this PC (and AFIF-PBX's DNS records)?")) return;
      const r = await api("/bridge", { method: "DELETE" }); toast(r.log.join(" ")); pages.bridge.render();
    }));
  }
};

// ------------------------------------------------------------ settings
pages.tailscale = {
  title: "Tailscale", refresh: 5000,
  async render() {
    if (document.activeElement && $("#view form")?.contains(document.activeElement)) return;
    if ($("#tsBusy") || $("#tsKey")?.value || $("#tsToken")?.value) return;
    const [r, st] = await Promise.all([api("/tailscale"), api("/status")]);
    const t = r.status;
    const altPort = r.sipPorts.find(p => p !== 5060) || r.sipPorts[0];
    const server = t.domain || t.dnsName || t.ipv4 || "";
    const pill = !t.installed ? '<span class="pill bad">Not installed</span>'
      : t.connected ? '<span class="pill good">Connected</span>'
      : `<span class="pill warn">${esc(t.message)}</span>`;
    const expires = t.keyExpiry ? new Date(t.keyExpiry) : null;
    const expSoon = expires && (expires - Date.now()) < 30 * 86400e3;
    const phones = t.peers.filter(p => ["android", "iOS", "ios"].includes(p.os));
    $("#view").innerHTML = `
      <div class="notice info">With <b>Tailscale</b>, your mobile and this PC join your private tailnet. Linphone then registers to AFIF-PBX
        from anywhere — Wi-Fi or 4G — with voice going straight to this PC, encrypted, and <b>no router ports opened</b>.</div>
      <div class="grid">
        <div class="card"><h3>This PC on Tailscale</h3><div class="row">${pill}</div>
          ${t.connected ? `<dl class="kv" style="margin-top:12px"><dt>Name</dt><dd class="mono">${esc(t.dnsName)}</dd>
            <dt>Address</dt><dd class="mono">${esc(t.ipv4)}</dd><dt>Tailnet</dt><dd>${esc(t.tailnet)}</dd>
            <dt>Key expiry</dt><dd>${expires ? `<span class="${expSoon ? "pill warn" : ""}">${expires.toLocaleDateString()}</span>` : "never"}</dd></dl>
            <div class="row" style="margin-top:12px"><button class="btn small danger" id="tsLogout">Disconnect</button></div>`
          : `<p class="sub">${esc(t.message || "")}</p>`}
          ${t.health?.length ? `<div class="notice" style="margin-top:10px">${t.health.map(esc).join("<br>")}</div>` : ""}
          ${t.version ? `<div class="sub" style="margin-top:8px">Tailscale ${esc(t.version)}</div>` : ""}</div>
        <div class="card"><h3>Linphone settings</h3>
          ${server ? `<dl class="kv"><dt>Username</dt><dd>your extension (${st.extensions.map(e => esc(e.number)).join(", ")})</dd>
            <dt>Password</dt><dd>the extension's secret</dd><dt>Domain</dt><dd class="mono">${esc(server)}:${altPort}</dd>
            <dt>Transport</dt><dd>UDP</dd></dl>
            <div class="sub" style="margin-top:8px">AFIF-PBX listens on ports ${r.sipPorts.map(p => `<b>${p}</b>`).join(" and ")} — use either.</div>`
          : '<div class="sub">Connect this PC to Tailscale first.</div>'}</div>
        ${!t.installed ? `<div class="card wide"><h3>Step 1 — install Tailscale on this PC</h3>
          <p class="sub">Tailscale runs as a Windows service and stays connected even when nobody is signed in to Windows.</p>
          ${t.installerAvailable ? '<button class="btn primary" id="tsInstall">Install Tailscale</button>'
            : '<p>Download it from <a href="https://tailscale.com/download/windows" target="_blank">tailscale.com/download/windows</a>, install, then come back here.</p>'}</div>` : ""}
        ${t.installed && !t.connected ? `<div class="card wide"><h3>Step 2 — connect this PC to your tailnet</h3>
          <form id="tsConnect"><div class="form-grid">
            <label>Name on the tailnet<input name="hostname" value="${esc(t.wantedName || "afif-pbx")}"></label>
            <label>Auth key (optional)<input id="tsKey" name="authKey" type="password" autocomplete="off" placeholder="tskey-auth-…"></label></div>
            <p class="sub">Leave the key empty to sign in with your Tailscale account in the browser instead.
              Keys: Tailscale admin console → <b>Settings → Keys → Generate auth key</b>.</p>
            <button class="btn primary">Connect</button></form>
          ${t.authUrl ? `<div class="notice info" style="margin-top:12px">Open this link and sign in with your Tailscale account:<br>
            <a href="${esc(t.authUrl)}" target="_blank" class="mono">${esc(t.authUrl)}</a></div>` : ""}</div>` : ""}
        ${t.connected ? `<div class="card wide"><h3>Your domain (optional)</h3>
          <p class="sub">Give the PBX a name on your own domain, e.g. <b class="mono">voip.powerchain.uk</b>. AFIF-PBX creates a DNS-only record
            pointing to <b class="mono">${esc(t.ipv4)}</b> in Cloudflare; it only answers for devices on your tailnet.
            ${t.dnsName ? `Without a domain, Linphone can use <b class="mono">${esc(t.dnsName)}</b>.` : ""}</p>
          <form id="tsDomain"><div class="form-grid">
            <label>Name<input name="domain" value="${esc(t.domain || "voip.powerchain.uk")}" required></label>
            <label>Cloudflare API token ${t.hasApiToken || t.bridgeTokenAvailable ? "(saved — leave empty)" : ""}<input id="tsToken" name="apiToken" type="password" autocomplete="off"
              placeholder="${t.hasApiToken || t.bridgeTokenAvailable ? "using the saved token" : "Zone · DNS · Edit"}"></label></div>
            <div class="row"><button class="btn primary">Point the name to this PBX</button>
            ${t.domain ? `<span class="pill ${t.domainOk ? "good" : "warn"}">${esc(t.domain)} → ${esc(t.domainResolvesTo || "?")}</span>
              <button type="button" class="link" id="tsDomainClear">Forget</button>` : ""}</div></form>
          ${t.lastDnsLog?.length ? `<div class="mono sub" style="white-space:pre-wrap;margin-top:10px">${t.lastDnsLog.map(esc).join("\n")}</div>` : ""}</div>` : ""}
        ${t.connected ? `<div class="card wide"><h3>Devices on your tailnet</h3>
          ${t.peers.length ? `<div class="table-wrap"><table><tr><th>Device</th><th>System</th><th>Address</th><th>Status</th></tr>
            ${t.peers.map(p => `<tr><td>${esc(p.hostName)}</td><td>${esc(p.os)}</td><td class="mono">${esc(p.address)}</td>
              <td><span class="dot ${p.online ? "on" : "off"}"></span>${p.online ? esc(p.connection || "online") : "offline"}</td></tr>`).join("")}</table></div>`
          : '<div class="empty">No other devices yet — install Tailscale on your mobile.</div>'}</div>` : ""}
        <div class="card wide"><h3>Your mobile — step by step</h3>
          <ol>
            <li>Install <b>Tailscale</b> from Google Play or the App Store and sign in with the <b>same account</b> as this PC.
              ${phones.length ? `<span class="pill good">${phones.length} phone${phones.length > 1 ? "s" : ""} on the tailnet</span>` : ""}</li>
            <li>Android: Settings → Network → VPN → Tailscale → <b>Always-on VPN</b>, and allow Tailscale and Linphone to run without battery restrictions.</li>
            <li>Install <b>Linphone</b> → <b>Use SIP account</b>: username = extension, password = its secret (Extensions → Edit),
              domain = <b class="mono">${esc(server || "voip.powerchain.uk")}:${altPort}</b>, transport <b>UDP</b>.</li>
            <li>Linphone → Settings → Advanced: turn on <b>Keep alive service</b> so calls ring when the app is in the background.</li>
            <li>Call <b>*43</b> (echo test) — you should hear yourself. Then try mobile data with Wi-Fi off.</li>
          </ol>
          <p class="sub">In the Tailscale admin console, open this PC (${esc(t.hostName || t.wantedName || "afif-pbx")}) → <b>Disable key expiry</b>, so the PBX never drops off the tailnet.</p></div>
      </div>`;
    const busy = async (btn, fn) => { btn.disabled = true; const label = btn.textContent; btn.textContent = "Working…"; btn.id = "tsBusy";
      try { await fn(); } finally { btn.removeAttribute("id"); btn.disabled = false; btn.textContent = label; } };
    $("#tsInstall")?.addEventListener("click", run(async e => busy(e.target, async () => {
      const x = await api("/tailscale/install", { method: "POST" }); toast(x.message); pages.tailscale.render(); })));
    $("#tsConnect")?.addEventListener("submit", run(async e => { e.preventDefault(); const f = e.target;
      await busy(f.querySelector(".btn.primary"), async () => {
        const x = await api("/tailscale/connect", { method: "POST", body: formData(f) });
        if ($("#tsKey")) $("#tsKey").value = "";
        if (x.authUrl) window.open(x.authUrl, "_blank");
        toast(x.message); });
      document.activeElement?.blur(); pages.tailscale.render(); }));
    $("#tsLogout")?.addEventListener("click", run(async () => {
      if (!confirm("Disconnect this PC from Tailscale? Phones outside your network will stop reaching AFIF-PBX.")) return;
      const x = await api("/tailscale/logout", { method: "POST" }); toast(x.message); pages.tailscale.render(); }));
    $("#tsDomain")?.addEventListener("submit", run(async e => { e.preventDefault(); const f = e.target;
      await busy(f.querySelector(".btn.primary"), async () => {
        const res = await fetch("/api/tailscale/domain", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(formData(f)) });
        const j = await res.json(); toast(j.ok ? "Done" : (j.log || []).slice(-1)[0] || "Failed", !j.ok);
        if ($("#tsToken")) $("#tsToken").value = ""; });
      document.activeElement?.blur(); pages.tailscale.render(); }));
    $("#tsDomainClear")?.addEventListener("click", run(async () => { await api("/tailscale/domain", { method: "DELETE" }); pages.tailscale.render(); }));
  }
};

pages.settings = {
  title: "Settings",
  async render() {
    const s = await api("/settings");
    $("#view").innerHTML = `<div class="grid">
      <div class="card wide"><h3>System</h3><form id="sys"><div class="form-grid">
        <label>SIP port<input type="number" name="sipPort" value="${s.sipPort}"></label>
        <label>Extra SIP ports<input name="extraSipPortsText" value="${esc((s.extraSipPorts || []).join(", "))}" placeholder="5160"></label>
        <label>Web panel port<input type="number" name="httpPort" value="${s.httpPort}"></label>
        <label>RTP ports from<input type="number" name="rtpPortStart" value="${s.rtpPortStart}"></label>
        <label>RTP ports to<input type="number" name="rtpPortEnd" value="${s.rtpPortEnd}"></label>
        <label>Ring time (seconds)<input type="number" name="ringSeconds" value="${s.ringSeconds}"></label>
        <label>Longest voicemail (seconds)<input type="number" name="voicemailMaxSeconds" value="${s.voicemailMaxSeconds}"></label>
        <label>SIP realm<input name="realm" value="${esc(s.realm)}"></label></div>
        <label class="check"><input type="checkbox" name="gsmEnabled" ${s.gsmEnabled ? "checked" : ""}> Use the GSM dongle</label>
        <div class="modal-actions"><button class="btn primary">Save</button></div></form></div>
      <div class="card"><h3>Administrator</h3><p class="sub">Signed in as <b>${esc(s.adminUser)}</b>.</p><button class="btn" id="pw">Change password</button></div>
      <div class="card"><h3>Backup</h3><p class="sub">Extensions, ring groups, IVRs and settings.</p>
        <div class="row"><a class="btn" href="/api/backup" download>Download backup</a><label class="btn" style="margin:0">Restore…<input type="file" id="restore" accept=".json" hidden></label></div></div>
    </div>`;
    $("#sys").addEventListener("submit", run(async e => {
      e.preventDefault(); const cur = await api("/settings"); Object.assign(cur, formData(e.target));
      cur.extraSipPorts = String(cur.extraSipPortsText || "").split(/[ ,;]+/).map(Number).filter(n => n > 0 && n < 65536); delete cur.extraSipPortsText;
      const r = await api("/settings", { method: "PUT", body: cur });
      toast(r.restartNeeded ? "Saved — restart AFIF-PBX (tray icon → Restart) to apply" : "Saved");
    }));
    $("#pw").addEventListener("click", () => changePasswordDialog(false));
    $("#restore").addEventListener("change", run(async e => {
      const file = e.target.files[0]; if (!file || !confirm("Replace the current configuration with this backup?")) return;
      const r = await fetch("/api/restore", { method: "POST", body: await file.text(), headers: { "Content-Type": "application/json" } });
      if (!r.ok) throw new Error((await r.json()).error || "Restore failed"); toast("Restored"); route();
    }));
  }
};
function changePasswordDialog(first) {
  openModal(first ? "Choose your administrator password" : "Change password", `<form id="f">
    ${first ? '<div class="notice info">Welcome to AFIF-PBX. The default password is <b>admin</b> — please choose your own.</div>' : ""}
    <label>Current password<input type="password" name="current" ${first ? 'value="admin"' : ""} required></label>
    <label>New password<input type="password" name="new" minlength="6" required></label>
    <div class="modal-actions"><button class="btn primary">Save</button></div></form>`, body => {
    $("#f", body).addEventListener("submit", run(async e => { e.preventDefault(); await api("/password", { method: "POST", body: formData(e.target) }); closeModal(); toast("Password changed"); }));
  });
}

// ------------------------------------------------------------ logs
pages.logs = {
  title: "Logs", refresh: 4000,
  async render() {
    const lines = await api("/logs");
    $("#view").innerHTML = `<div class="table-wrap"><table>${lines.map(l => `<tr><td class="mono" style="white-space:nowrap">${new Date(l.time).toLocaleTimeString()}</td>
      <td><span class="pill ${l.level === "Error" ? "bad" : l.level === "Warning" ? "warn" : ""}">${esc(l.level)}</span></td><td class="sub">${esc(l.category)}</td><td>${esc(l.message)}</td></tr>`).join("") || '<tr><td class="empty">Nothing yet</td></tr>'}</table></div>`;
  }
};

// ------------------------------------------------------------ boot
(async () => {
  try { enterApp(await api("/me")); }
  catch { showLogin(); }
})();
