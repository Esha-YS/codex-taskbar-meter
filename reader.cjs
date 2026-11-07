// Quota formatting and app-server client adapted from Codex Usage Card v0.9.5. See LICENSE.
"use strict";
// Formatting is independent of platform and account storage.
function remainingPercent(used) { return Math.max(0, Math.min(100, Math.round(100 - used))); }
function validWindow(value) { return value != null && Number.isFinite(value.usedPercent); }
function isCodexLimit(value) { return value?.limitId == null || value.limitId === 'codex'; }
function formatResetTime(seconds) {
  if (!Number.isFinite(seconds) || seconds <= 0) return null;
  const date = new Date(seconds * 1000);
  if (!Number.isFinite(date.getTime())) return null;
  return new Intl.DateTimeFormat('zh-CN', {month:'numeric', day:'numeric', hour:'2-digit', minute:'2-digit', hour12:false}).format(date);
}
function durationLabel(minutes) {
  for (const [unit, label] of [[10080,'周'],[1440,'天'],[60,'小时']]) {
    if (Number.isFinite(minutes) && minutes > 0 && minutes % unit === 0) return `${minutes / unit}${label}`;
  }
  return Number.isFinite(minutes) && minutes > 0 ? `${minutes}分钟` : '额度';
}
function formatResetCredits(response) {
  const credits = response?.rateLimitResetCredits;
  if (!credits || typeof credits !== 'object') return [];
  const available = Array.isArray(credits.credits) ? credits.credits.filter(c => c?.status === 'available' && c?.resetType === 'codexRateLimits') : [];
  const count = Number.isFinite(Number(credits.availableCount)) ? Math.max(0, Math.floor(Number(credits.availableCount))) : available.length;
  const expiry = formatResetTime(available.map(c=>c.expiresAt).filter(v=>Number.isFinite(v)&&v>0).sort((a,b)=>a-b)[0]);
  return [`重置卡：${count} 张可用`, ...(count && expiry ? [`最近到期：${expiry}`] : [])];
}
function formatPrimaryRateLimits(response) {
  const snapshot = pickCodexSnapshot(response);
  const windows = [snapshot?.primary, snapshot?.secondary].filter(validWindow);
  const weekly = w => Number.isFinite(w.windowDurationMins) && w.windowDurationMins >= 10080 && w.windowDurationMins % 10080 === 0;
  const plan = String(snapshot?.planType ?? response?.planType ?? '').toLowerCase();
  const window = plan.startsWith('pro') ? windows.find(weekly) : windows.find(w=>w.windowDurationMins === 300) ?? (!plan ? windows.find(weekly) : null);
  const details = windows.map(w=>`${durationLabel(w.windowDurationMins)}：剩余 ${remainingPercent(w.usedPercent)}%${formatResetTime(w.resetsAt) ? `，${formatResetTime(w.resetsAt)} 重置` : ''}`);
  const percent = window ? remainingPercent(window.usedPercent) : null;
  return {percent, windowLabel: window && weekly(window) ? '周' : '', tone:quotaTone(percent),
    title:[window ? `当前显示：${durationLabel(window.windowDurationMins)}额度，剩余${percent}%` : '暂时无法读取 Codex 剩余用量',...details,...formatResetCredits(response)].join('\n')};
}

const hasField = (value, key) => value != null && Object.hasOwn(value, key);
const isRecord = value => value !== null && typeof value === 'object' && !Array.isArray(value);
function mergeWindow(current, patch) {
  if (patch === undefined) return current ?? null;
  if (!isRecord(patch)) return null;
  const sameWindow = current && (!Number.isFinite(patch.windowDurationMins) || current.windowDurationMins === patch.windowDurationMins);
  return sameWindow ? { ...current, ...patch } : { ...patch };
}
function mergeRateLimitSnapshot(current, patch) {
  if (patch === undefined) return current ?? null;
  if (!isRecord(patch)) return null;
  if (current?.planType && patch.planType && current.planType !== patch.planType) current = null;
  return { ...current, ...patch,
    primary: mergeWindow(current?.primary, patch.primary),
    secondary: mergeWindow(current?.secondary, patch.secondary) };
}
function mergeRateLimitsResponse(current, patch) {
  if (patch === undefined) return current ?? null;
  if (!isRecord(patch)) return null;
  // A refresh is a complete snapshot (caller passes null); notifications are deltas.
  // Never carry windows or credits across account changes.
  const changedAccount = hasField(patch, 'accountId') && hasField(current, 'accountId') && patch.accountId !== current.accountId;
  const base = changedAccount ? {} : current ?? {};
  const merged = { ...base, ...patch };
  merged.rateLimits = base.rateLimits ?? null;
  if (hasField(patch, 'rateLimits') && isCodexLimit(patch.rateLimits)) {
    merged.rateLimits = mergeRateLimitSnapshot(base.rateLimits, patch.rateLimits);
    if (isRecord(base.rateLimitsByLimitId)) merged.rateLimitsByLimitId = { ...base.rateLimitsByLimitId, codex: merged.rateLimits };
  }
  if (hasField(patch, 'rateLimitsByLimitId')) {
    if (!isRecord(patch.rateLimitsByLimitId)) {
      merged.rateLimitsByLimitId = null;
      if (!hasField(patch, 'rateLimits')) merged.rateLimits = null;
    }
    else {
      merged.rateLimitsByLimitId = { ...base.rateLimitsByLimitId, ...merged.rateLimitsByLimitId };
      for (const [id, snapshot] of Object.entries(patch.rateLimitsByLimitId)) {
        // The keyed Codex bucket is authoritative when both formats are returned.
        const previous = base.rateLimitsByLimitId?.[id] ?? (id === 'codex' ? base.rateLimits : null);
        Object.defineProperty(merged.rateLimitsByLimitId, id, {value:mergeRateLimitSnapshot(previous, snapshot),enumerable:true,configurable:true,writable:true});
      }
      if (hasField(patch.rateLimitsByLimitId, 'codex')) merged.rateLimits = merged.rateLimitsByLimitId.codex;
    }
  }
  return merged;
}
function pickCodexSnapshot(response) {
  if (!isRecord(response)) return null;
  if (hasField(response.rateLimitsByLimitId, 'codex')) {
    const keyed = response.rateLimitsByLimitId.codex;
    return isRecord(keyed) && isCodexLimit(keyed) ? keyed : null;
  }
  if (hasField(response, 'rateLimits')) return isRecord(response.rateLimits) && isCodexLimit(response.rateLimits) ? response.rateLimits : null;
  return isCodexLimit(response) ? response : null;
}

function quotaTone(percent) {
  return !Number.isFinite(percent) ? 'muted' : percent < 10 ? 'danger' : percent <= 50 ? 'warning' : 'normal';
}
function unavailableValue(current, title = '暂时无法读取 Codex 用量，正在自动重试') {
  return { ...current, percent: null, title, tone: 'muted', stale: true,
    rings: current?.rings?.map(ring => ({ ...ring, percent: null, tone: 'muted', title: `${ring.label}：暂不可用` })) ?? null };
}
function formatRateLimits(response) {
  const single = formatPrimaryRateLimits(response);
  single.tone = quotaTone(single.percent);
  const snapshot = pickCodexSnapshot(response);
  const planType = String(snapshot?.planType ?? response?.planType ?? '').toLowerCase();
  if (planType !== 'plus') return { ...single, mode: 'single', rings: null };
  const windows = [snapshot?.primary, snapshot?.secondary].filter(Boolean);
  function ring(key, label, match) {
    const window = windows.find(match);
    const percent = validWindow(window) ? remainingPercent(window.usedPercent) : null;
    const reset = formatResetTime(window?.resetsAt);
    const tone = quotaTone(percent);
    return { key, label, percent, tone,
      title: `${label}：${percent === null ? '暂不可用' : `剩余 ${percent}%`}${reset ? `，${reset} 重置` : ''}` };
  }
  const rings = [
    ring('five-hour', '5h额度', w => w.windowDurationMins === 300),
    ring('weekly', '周额度', w => Number.isFinite(w.windowDurationMins) && w.windowDurationMins >= 10080 && w.windowDurationMins % 10080 === 0)
  ];
  return { ...single, mode: 'dual', rings,
    title: ['Plus 剩余额度', ...rings.map(r => r.title), ...formatResetCredits(response)].join('\n') };
}

class AppServerClient extends require('node:events').EventEmitter {
  constructor({command='codex', args=['app-server'], requestTimeoutMs=10000}={}) {
    super(); Object.assign(this,{command,args,requestTimeoutMs});
    this.child=null; this.lines=null; this.pending=new Map(); this.serial=0; this.rateLimits=null;
  }
  async start() {
    if (this.child) return;
    const child=require('node:child_process').spawn(this.command,this.args,{stdio:['pipe','pipe','pipe'],windowsHide: true,env:process.env});
    this.child=child;
    const fail=error=>{
      if (this.child!==child) return;
      this.stop(); this.emit('server-exit',error);
    };
    child.once('error',fail);
    child.once('exit',(code)=>fail(new Error(`App Server exited (${code})`)));
    child.stdin.on('error',fail);
    child.stderr.resume(); // Drain without recording account/server details.
    this.lines=require('node:readline').createInterface({input:child.stdout});
    this.lines.on('line',line=>{
      let message; try { message=JSON.parse(line); } catch { return; }
      if (!message || typeof message!=='object') return;
      const request=this.pending.get(message.id);
      if (request) {
        this.pending.delete(message.id); clearTimeout(request.timer);
        message.error ? request.reject(new Error(message.error.message || 'App Server request failed')) : request.resolve(message.result);
      } else if(message.method==='account/rateLimits/updated') {
        this.rateLimits=mergeRateLimitsResponse(this.rateLimits,message.params);
        this.emit('rate-limits',this.rateLimits);
      }
    });
    await this.request('initialize',{clientInfo:{name:'codex_taskbar_meter',title:'Codex Taskbar Meter',version:AGENT_VERSION}});
    this.child?.stdin.write(JSON.stringify({method:'initialized',params:{}})+'\n');
    await this.refresh();
  }
  request(method,params) {
    if (!this.child?.stdin.writable) return Promise.reject(new Error('App Server is not running'));
    const id=++this.serial;
    return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{this.pending.delete(id);reject(new Error(`${method} timed out`));},this.requestTimeoutMs);
      this.pending.set(id,{resolve,reject,timer});
      this.child.stdin.write(JSON.stringify({id,method,...(params===undefined?{}:{params})})+'\n',error=>{
        if(error && this.pending.delete(id)){clearTimeout(timer);reject(error);}
      });
    });
  }
  async refresh() {
    this.rateLimits=mergeRateLimitsResponse(null,await this.request('account/rateLimits/read'));
    this.emit('rate-limits',this.rateLimits); return this.rateLimits;
  }
  stop() {
    const child=this.child; this.child=null;
    this.lines?.close(); this.lines=null;
    if(child && !child.killed) child.kill();
    for(const item of this.pending.values()){clearTimeout(item.timer);item.reject(new Error('App Server stopped'));}
    this.pending.clear();
  }
}

const resolverFs = require('node:fs');
function isExecutableFile(file) {
  if (!file) return false;
  try {
    resolverFs.accessSync(file, resolverFs.constants.X_OK);
    return resolverFs.statSync(file).isFile();
  } catch { return false; }
}
function resolveCodexBin(explicit, appPath = '/Applications/ChatGPT.app', platform = process.platform) {
  if (explicit && explicit !== 'codex' && isExecutableFile(explicit)) return explicit;
  // New desktop releases bundle the CLI as a nested app and expose a shell entrypoint.
  // Prefer this client's CLI to unrelated installations in the user's shell PATH.
  const candidates = platform === 'win32' ? windowsCodexCandidates(appPath) : [
    `${appPath}/Contents/Resources/codex-cli/bin/codex`,
    `${appPath}/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex`,
    `${appPath}/Contents/Resources/codex`
  ];
  const found = candidates.find(isExecutableFile);
  if (!found) throw new Error('找不到客户端内置 Codex 可执行程序，请检查客户端安装');
  return found;
}
function windowsCodexCandidates(appPath) {
  const path = require('node:path');
  const root = /\.exe$/i.test(appPath) ? path.dirname(appPath) : appPath;
  const local = process.env.LOCALAPPDATA || path.join(require('node:os').homedir(), 'AppData', 'Local');
  const managed = path.join(local, 'OpenAI', 'Codex', 'bin');
  let versions = [];
  try {
    versions = resolverFs.readdirSync(managed, { withFileTypes: true }).filter(e => e.isDirectory())
      .map(e => path.join(managed, e.name, 'codex.exe')).filter(isExecutableFile)
      .sort((a, b) => resolverFs.statSync(b).mtimeMs - resolverFs.statSync(a).mtimeMs);
  } catch {}
  return [path.join(managed, 'codex.exe'), ...versions,
    path.join(root, 'resources', 'codex.exe'), path.join(root, 'resources', 'codex-cli', 'bin', 'codex.exe'),
    path.join(root, 'resources', 'codex-cli', 'codex.exe')];
}

const AGENT_VERSION = '1.0.0';

const once = process.argv.includes('--once');
let client;
let timer;
let stopped = false;

function stop() {
  stopped = true;
  clearTimeout(timer);
  client?.stop();
  process.stdin.pause();
}

function publish(value) {
  if (!stopped) process.stdout.write(JSON.stringify(value) + '\n');
}

async function refresh() {
  let interval = 60000;
  try {
    if (client) await client.refresh();
    else {
      client = new AppServerClient({command: resolveCodexBin(undefined, process.env.CODEX_METER_APP_PATH || ''), requestTimeoutMs: 15000});
      await client.start();
    }
    publish(formatRateLimits(client.rateLimits));
  } catch {
    client?.stop();
    client = null;
    publish(unavailableValue());
    interval = 15000;
    if (once) process.exitCode = 1;
  }
  if (once) stop();
  if (!stopped) timer = setTimeout(refresh, interval);
}

process.stdout.on('error', stop);
if (!once) {
  process.stdin.on('end', stop);
  process.stdin.resume();
}
refresh();
