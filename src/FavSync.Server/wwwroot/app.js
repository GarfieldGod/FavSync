'use strict';

const $ = (id) => document.getElementById(id);

const state = {
  // 同步页
  filter: 'all', q: '', offset: 0, limit: 100, total: 0, rows: [], selected: new Set(), busy: false,
  onPhone: 0,   // 库里扫过多少手机条目（区分"没扫描过"和"这个分类没条目"两种空态）
  // 活动作业（SSE 推送）
  jobs: [],
  // 当前页面
  page: 'sync',
  ready: { library: false, bili: false },
};

// ==================== 工具 ====================

const fmtBytes = (n) => {
  if (n == null) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v >= 100 || i < 2 ? Math.round(v) : v.toFixed(1)} ${u[i]}`;
};

const fmtSpeed = (bps, unit) => bps > 0
  ? (unit === 'items' ? `${bps.toFixed(2)} 条/s` : `${(bps / 1048576).toFixed(2)} MB/s`)
  : '—';

const fmtTime = (s) => {
  if (!isFinite(s) || s <= 0) return '—';
  if (s < 90) return `${Math.round(s)}s`;
  if (s < 5400) return `${Math.round(s / 60)}min`;
  return `${(s / 3600).toFixed(1)}h`;
};

const fmtDur = (ms) => {
  if (!ms || ms <= 0) return '';
  const t = Math.round(ms / 1000);
  const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
               : `${m}:${String(s).padStart(2, '0')}`;
};

const fmtDate = (iso) => {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d)) return '';
  const now = new Date();
  if (d.getFullYear() === now.getFullYear()) return `${d.getMonth() + 1}月${d.getDate()}日`;
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

function escapeHtml(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

async function api(path, options) {
  const o = options || {};
  if (o.body && !o.headers) o.headers = { 'Content-Type': 'application/json' };   // 否则 POST JSON 会被 415
  const res = await fetch('/api/v1' + path, o);
  if (!res.ok) {
    let msg = res.statusText;
    try { const j = await res.json(); msg = j.error || msg; } catch { }
    throw new Error(msg);
  }
  return res.status === 204 ? null : res.json();
}

const STATE_LABEL = {
  phone_only: ['待同步', 'b-todo'],
  outdated: ['有变化', 'b-drift'],
  synced: ['已同步', 'b-synced'],
  incomplete: ['残片', 'b-incomplete'],
  mismatch: ['校验失败', 'b-mismatch'],
};

const DRIFT_LABEL = {
  content: ['内容已变', '同档位但不是同一份字节（换源 / 重新转码），可新增备份'],
  tag_up: ['有新档位', '手机档位高于总库已备份，可新增备份'],
  tag_down: ['已升级', '库里那份画质比手机缓存高（手机存的是较低档），已同步、不会重复下载'],
  structure: ['文件集变化', '内容相同，但文件数量与备份不同'],
  codec_diff: ['编码不同', '同画质但编码不同（手机 HEVC / 库里 AVC），字节本该不同，不是内容被改'],
  webmux: ['封装不同', '库里是收藏夹直下的网页版、手机是原版缓存：同画质同编码、内容一致，只是封装不同。同步时只补元数据，不重搬视频'],
};

const driftsOf = (s) => (s && s !== 'none' ? s.split(',').map(x => x.trim()).filter(Boolean) : []);

const PROBE_LABEL = {
  exact: ['可下载替代', 'pv-exact'],
  lower: ['仅拷贝', 'pv-lower'],
  gone: ['已失效', 'pv-gone'],
  charge: ['充电视频', 'pv-charge'],
  unknown: ['探测失败', 'pv-unknown'],
};

// 原视频地址：有 bvid 用 BV 号（最稳），没有就退回 av 号；两个都没有返回 null
function biliUrl(avid, bvid) {
  if (bvid) return 'https://www.bilibili.com/video/' + bvid;
  const a = Number(avid);
  return a > 0 ? 'https://www.bilibili.com/video/av' + a : null;
}

// 新标签页打开原视频的小链接（stopPropagation：别让点击冒泡到卡片/行的其它处理）
function biliLinkHtml(url, text) {
  if (!url) return '';
  return `<a class="blink" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer"
    onclick="event.stopPropagation()" title="在 B 站打开原视频（新标签页）">${text || '原视频'} ↗</a>`;
}

// 档位标注：q 值 + 编码。手机侧与 B 站侧并排看，才知道差在"画质"还是"编码"
// （CODEC_NAME 在下方 406 行定义：7=AVC / 12=HEVC / 13=AV1）
const tierText = (quality, codecid) => {
  if (quality == null) return '—';
  const c = CODEC_NAME[codecid];
  return 'q' + quality + (c ? '/' + c : '');
};

// ==================== 路由 ====================

const PAGES = ['sync', 'library', 'bili', 'settings'];

function pageFromHash() {
  const h = (location.hash || '').replace(/^#\/?/, '').trim();
  return PAGES.includes(h) ? h : 'sync';
}

function showPage(p) {
  if (!PAGES.includes(p)) p = 'sync';
  state.page = p;
  document.body.dataset.page = p;          // CSS 需要按页面切布局（bili 页左栏通栏）
  PAGES.forEach(name => {
    const el = $('page-' + name);
    if (el) el.classList.toggle('hidden', name !== p);
  });
  document.querySelectorAll('.nav-item').forEach(a => a.classList.toggle('active', a.dataset.page === p));

  if (p === 'library' && !state.ready.library) { state.ready.library = true; loadLibrary(true); }
  if (p === 'bili' && !state.ready.bili) { state.ready.bili = true; loadFolders(); }
  if (p === 'settings') { loadSettings(); loadColdStatus(); }
}

// 顶栏高度实测写进 CSS 变量：Bilibili 页左栏高度 = 视口 - 顶栏，不靠硬编码猜
function syncHeadHeight() {
  const h = document.querySelector('.top');
  if (h) document.documentElement.style.setProperty('--head-h', h.offsetHeight + 'px');
}
window.addEventListener('resize', syncHeadHeight);

window.addEventListener('hashchange', () => showPage(pageFromHash()));

// ==================== 状态卡片 ====================

async function loadStatus() {
  try {
    const s = await api('/status');
    $('archivePath').textContent = s.archiveRoot;
    renderBootBanner(s.archiveNotice);
    const chip = $('statusChip');
    const dev = (s.devices || []).find(d => d.online);
    if (!s.adbPath) { chip.textContent = '未找到 adb'; chip.className = 'status bad'; }
    else if (!dev) { chip.textContent = '设备离线'; chip.className = 'status bad'; }
    else { chip.textContent = dev.serial; chip.className = 'status'; }
    state.onPhone = (s.stats && s.stats.onPhone) || 0;
    renderCards(s.stats, s.actions);
    renderBiliChip(null, s.biliLoggedIn);
  } catch (e) {
    $('statusChip').textContent = '服务未就绪：' + e.message;
    $('statusChip').className = 'status bad';
  }
  try {
    const b = await api('/bili/state');
    renderBiliChip(b, b.loggedIn);
  } catch { }
}

function renderBiliChip(acc, loggedIn) {
  const chip = $('biliChip'), btn = $('btnBili');
  if (loggedIn) {
    chip.textContent = acc && acc.uname ? `B站：${acc.uname}` : 'B站：已登录';
    chip.className = 'status bili';
    btn.textContent = '退出登录';
  } else {
    chip.textContent = 'B站：未登录';
    chip.className = 'status bili out';
    btn.textContent = '登录 B 站';
  }
}

function renderCards(st, a) {
  if (!st) return;
  // 第一行：手机侧现状 + 三类"异常"（它们讲的是"手机/搬运这一侧"，与能否从B站下载是两条线）
  // 口径铁律：**分母永远是「本次扫描到的手机条目」**（on_phone=1），
  // 所以 手机缓存条目 == 已同步 + 已升级 + 待同步，三张卡能直接对账。
  // 只在库里、手机侧已删除的条目（libraryOnly）不属于手机侧，单列在脚注，不混进「已同步」。
  // 「已升级」是「已同步」的子集（库里那份画质更高），拆开显示但两者相加 = 已同步总量
  const top = [
    { k: '手机缓存条目', v: a ? a.onPhone : st.onPhone },
    { k: '已同步', v: a ? a.synced : st.synced, c: 'var(--ok)',
      tip: '本次扫描到的手机条目里，已在总库中有对应备份（同档位同内容）的条数。不含「已升级」；手机侧已删除、只剩库里那份的也不算（见脚注「仅存库中」）' },
    { k: '已升级', v: a ? a.upgraded : 0, c: a && a.upgraded ? 'var(--ok)' : '',
      tip: '库里那份比手机缓存更好（档位更高，或同档位编码更优）。属于已同步，不会重复下载；点进去能看手机 vs 库里各是什么档位和编码' },
    { k: '待同步', v: a ? a.remaining : st.phoneOnly, c: 'var(--accent)',
      tip: '手机上有、库里一份都没有 = 第二行四类之和。对账：手机缓存条目 = 已同步 + 已升级 + 待同步 + 冲突' },
    { k: '冲突', v: a ? a.conflict : 0, c: a && a.conflict ? 'var(--warn)' : '',
      tip: '库里有东西、但和手机那份对不上：有变化（可升档 / 编码不同 / 内容变了）+ 校验失败 + 残片。这些要你决定留哪份，不是「还没搬」——点进去按类型分开看' },
  ];
  $('cardsTop').innerHTML = top.map(cardHtml).join('');
  if (!a) return;
  // 第二行：可下载性四类，**互斥**（有结论且未过期才算，过期/无结论一律进「待探测」）
  // 分母是「待同步」（phone_only，库里一份都没有）。库里有东西的条目不在这里，它们在「冲突」。
  const sub = [
    { k: '可替代下载', v: a.exact, c: a.exact ? 'var(--ok)' : '',
      tip: '能放心从 B 站下的两种：① 同档同编码且字节数一致（下载完用手机 md5 逐字节校验，拿到的就是原件）；② B 站能给到比手机更高的画质（升档下载，内容只会更好）。同画质不同编码、或同档但字节数不同，都不在此列' },
    { k: '已失效', v: a.gone, c: a.gone ? 'var(--bad)' : '', tip: '稿件在B站已不可见（删除/私密/不可见），只能趁手机还在赶紧拷贝' },
    { k: '仅拷贝', v: a.lower, c: a.lower ? 'var(--warn)' : '',
      tip: 'B站那份不能替代手机缓存，只能从手机拷贝。四种原因：① 画质更低；② 同画质但编码不同（手机 HEVC / B站 AVC·AV1）；③ 同档同编码但字节数不同（B站 web 转码 mux 与手机缓存不是同一份）；④ 充电视频（playurl 87008，账号没充电不给流）' + (a.charge ? `。其中 ${a.charge} 条是充电视频，点「充电」标签可单独列出` : '') },
    { k: '待探测', v: a.unprobed, c: '',
      tip: '没有可用结论：从没探过 / 结论过期 / 探测失败' + (a.pendingUnknown ? `（其中 ${a.pendingUnknown} 条探测失败）` : '') },
  ];
  $('cardsSub').innerHTML = sub.map(cardHtml).join('');
  // 脚注：总库备份数与手机侧口径的差额要讲清楚，否则「已同步 3531」配「总库备份 4312」看着像 bug
  const extra = [];
  if (a.libraryOnly) extra.push(`<a class="lonly" data-filter="libraryonly" title="手机侧已删除、只剩总库那份。点开查看（不计入「已同步」）">仅存库中 ${a.libraryOnly}</a>`);
  if (a.lost) extra.push(`<span class="lv-lost" title="手机侧文件已删除、而且从来没入库 —— 数据已永久丢失">⚠ 已丢失 ${a.lost}</span>`);
  $('cardsFoot').innerHTML =
    `总库备份 ${st.backups} 条${extra.length ? '（' + extra.join(' · ') + '）' : ''}` +
    ` · 手机侧 ${fmtBytes(st.bytesOnPhone)} · 总库 ${fmtBytes(st.bytesBackedUp)}`;
}

function cardHtml(i) {
  const tip = i.tip ? ` title="${escapeHtml(i.tip)}"` : '';
  return `<div class="card"${tip}><div class="k">${i.k}</div><div class="v" style="${i.c ? 'color:' + i.c : ''}">${i.v}</div></div>`;
}

// ---- 统计卡实时刷新：同步页每 3s 拉轻量 /stats（/status 带 adb 检查，不适合轮询）----
let statsTimer = null;
function startStatsPoll() {
  if (statsTimer) return;
  statsTimer = setInterval(async () => {
    if (state.page !== 'sync') return;
    try {
      const d = await api('/stats');
      state.onPhone = (d.stats && d.stats.onPhone) || 0;
      renderCards(d.stats, d.actions);
    } catch { }
  }, 3000);
}

// ==================== 同步列表 ====================

async function loadItems() {
  const params = new URLSearchParams({ filter: state.filter, offset: state.offset, limit: state.limit });
  if (state.q) params.set('q', state.q);
  const data = await api('/items?' + params);
  state.total = data.total;
  state.rows = data.rows;
  renderRows();
  renderPager();
}

function renderRows() {
  const tbody = $('rows');
  if (!state.rows.length) {
    // 库里有数据但当前分类没条目 ≠ 没扫描过：文案要能区分这两种空
    const emptyText = state.onPhone
      ? '这个分类下暂无条目。'
      : '还没有数据。点右上角「扫描手机」。';
    tbody.innerHTML = `<tr><td colspan="8" class="empty">${emptyText}</td></tr>`;
    updateSyncButton();
    return;
  }

  tbody.innerHTML = state.rows.map(r => {
    const [label, cls] = STATE_LABEL[r.state] || [r.state, 'b-todo'];
    const q = r.quality ? (r.height ? `${r.height}P` : `q${r.quality}`) : '—';
    const codec = r.codecid === 12 ? 'HEVC' : r.codecid === 7 ? 'AVC' : '';
    const offPhone = r.onPhone ? '' : ' · 手机已删';

    const marks = driftsOf(r.drift).map(d => {
      const [t, tip] = DRIFT_LABEL[d] || [d, ''];
      return `<span class="dchip" title="${escapeHtml(tip)}">${t}</span>`;
    }).join('');

    // 具体原因（用户要能看懂"到底哪一步/哪一部分出了问题"）：
    // 残片 → 缺哪个流、实际字节 vs index.json 声明；有变化 → 哪个流变了（md5 前后）；
    // 校验失败 → 文件集/大小/md5 哪一道闸门没过。行内截两行，title 给全文。
    let why = null;
    // 后端 issueNote 自带"搬运校验未通过 / 下载字节校验未通过"前缀，前端别再叠一层
    if (r.state === 'mismatch' && r.issueNote) why = { t: r.issueNote, cls: 'w-bad' };
    else if (r.state === 'incomplete') why = r.driftNote ? { t: '残片：' + r.driftNote, cls: 'w-warn' } : null;
    else if (r.driftNote) why = { t: r.driftNote, cls: 'w-warn' };

    const srcTag = (() => {
      if (!r.backupSource) return '';
      const SRC = { backup: '手机备份', download: '库内下载', unknown: '来源不明' };
      const VIA = { copy: '拷贝', download: '下载替代' };
      const s = SRC[r.backupSource] || r.backupSource;
      const v = r.backupSource === 'backup' && r.backupVia ? '·' + (VIA[r.backupVia] || r.backupVia) : '';
      return ` · ${s}${v}`;
    })();
    const lib = r.backupCount > 0
      ? `${r.backupCount} 份<span class="key">${r.bestQuality ? ' · 最高 q' + r.bestQuality : ''}${srcTag}</span>`
      : '<span class="key">未备份</span>';

    let probeCell;
    if (r.state === 'synced') {
      // 已升级：手机那份是较低档，库里已有更高画质 → 并排标出两端，说明"不会再下"。
      // bestTagNorm 形如 q120_c7，编解码从后缀解析（老数据没有 _c 后缀就只显示 q）
      const upTier = driftsOf(r.drift).includes('tag_down') && r.bestTagNorm;
      if (upTier) {
        const m = /q(\d+)(?:_c(\d+))?/.exec(r.bestTagNorm);
        const libTier = m ? tierText(+m[1], m[2] ? +m[2] : null) : r.bestTagNorm;
        probeCell = `<div class="tier-cmp" title="手机缓存是较低档（${tierText(r.quality, r.codecid)}），库里已经有 ${libTier} 那份 —— 已同步，不会重复下载">
           手机 ${tierText(r.quality, r.codecid)} <span class="arrow">→</span> 库里 ${libTier}</div>`;
      } else probeCell = '<span class="probe unknown">—</span>';
    }
    else if (r.probeVerdict) {
      const [t, c] = PROBE_LABEL[r.probeVerdict] || [r.probeVerdict, 'pv-unknown'];
      // 「仅拷贝」必须并排标出两端，并写清是哪一种原因：
      //   同画质不同编码（手机 HEVC / B站 AVC·AV1）—— B站那份不是原件，不能替代
      //   画质更低（B站最高 q80 < 手机 q120）—— B站救不回来，只能趁手机还在赶紧拷
      let cmp = '';
      if (r.probeVerdict === 'lower') {
        const phoneTier = tierText(r.quality, r.codecid);
        const biliTier = r.probeMaxQuality ? tierText(r.probeMaxQuality, r.probeMaxCodecid) : (r.probeNote || '—');
        const sameQ = r.probeMaxQuality != null && r.quality === r.probeMaxQuality;
        const sameCodec = r.probeMaxCodecid == null || r.probeMaxCodecid === r.codecid;
        // 第三种原因：档位和编码都一样，但 web 那份字节数不同（web 转码 mux ≠ 手机缓存）
        // 这种"不可下载相同编码"最容易被误当成"可替代下载"，白下几百 MB 后 md5 必然不符
        const byteDiff = sameQ && sameCodec;
        const why = r.probeMaxQuality == null
          ? 'B站侧给的档位/编码还没探到（需重探确认）'
          : !sameQ
            ? `B站最高画质更低（B站 ${biliTier} < 手机 ${phoneTier}）`
            : byteDiff
              ? '同档同编码但字节不同（B站 web 那份不是手机缓存的原件）'
              : `同画质不同编码（手机 ${CODEC_NAME[r.codecid] || '?'} / B站 ${CODEC_NAME[r.probeMaxCodecid] || '?'}）—— B站那份不是手机的原件`;
        const biliText = byteDiff ? `${biliTier}<span class="muted">（字节不同）</span>` : biliTier;
        cmp = `<div class="tier-cmp" title="${escapeHtml(why)}；只能从手机拷贝">
           手机 ${phoneTier} <span class="arrow">→</span> B站 ${biliText}</div>`;
      }
      // 充电视频：稿件活着，是账号没充电/充电过期，playurl 直接回 87008。
      // 不画档位对比（B站根本不给流，没有"能给到哪一档"这回事），直接写清原因
      if (r.probeVerdict === 'charge') {
        cmp = `<div class="tier-cmp" title="充电专属视频：稿件还在，但账号没充电（或已过期），B站 playurl 直接回 87008 不给流。只能从手机拷贝 —— 注意手机缓存若带加密，过期后同样解不开">B站 <span class="arrow">✕</span> 未充电</div>`;
      }
      const tip = r.probeNote ? ` title="${escapeHtml(r.probeNote)}"` : '';
      probeCell = `<span class="badge ${c}"${tip}>${t}</span>${cmp}`;
    } else probeCell = '<span class="probe unknown">— 待探测</span>';

    // 能勾选重跑的状态见 CAN_SYNC_STATE（mismatch 也要能选：上一次搬运被中途取消/校验没过，
    // 它不是终态，重跑一次就能修好；以前漏了它，用户点了「校验失败」标签进去发现选不动）
    const canSelect = CAN_SYNC_STATE.has(r.state);

    let dlCell = '';
    if (r.dlStatus === 'downloading') {
      const pct = r.dlTotal > 0 ? Math.min(100, Math.round((r.dlDone || 0) / r.dlTotal * 100)) : 0;
      dlCell = `<div class="dlprog">
        <div class="dltrack"><div class="dlfill" style="width:${pct}%"></div></div>
        <span>下载中 ${pct}% · ${fmtBytes(r.dlDone)} / ${fmtBytes(r.dlTotal)}</span>
      </div>`;
    } else if (r.dlStatus === 'queued') {
      dlCell = '<div class="dlprog">排队中</div>';
    }

    return `<tr class="${r.drift !== 'none' ? 'has-drift' : ''}">
      <td class="col-check">
        <input type="checkbox" data-key="${r.key}" ${state.selected.has(r.key) ? 'checked' : ''} ${canSelect ? '' : 'disabled'}>
      </td>
      <td>
        <span class="title" title="${escapeHtml(r.title || '')}">${escapeHtml(r.title || '(无标题)')}</span>
        ${biliLinkHtml(biliUrl(r.avid))}
        ${marks}
        ${why ? `<div class="why ${why.cls}" title="${escapeHtml(why.t)}">${escapeHtml(why.t)}</div>` : ''}
        <span class="key">${r.key}</span>
      </td>
      <td class="col-owner" title="${escapeHtml(r.owner || '')}">${escapeHtml(r.owner || '—')}</td>
      <td class="col-size">${fmtBytes(r.size)}</td>
      <td class="col-q">${q}${codec ? ' <span class="key">' + codec + '</span>' : ''}</td>
      <td class="col-lib">${lib}</td>
      <td class="col-state"><span class="badge ${cls}">${label}${offPhone}</span>${dlCell}</td>
      <td class="col-probe">${probeCell}</td>
    </tr>`;
  }).join('');

  tbody.querySelectorAll('input[type=checkbox][data-key]').forEach(cb => {
    cb.addEventListener('change', () => {
      if (cb.checked) state.selected.add(cb.dataset.key);
      else state.selected.delete(cb.dataset.key);
      updateSyncButton();
    });
  });
  updateSyncButton();
}

function renderPager() {
  const pages = Math.max(1, Math.ceil(state.total / state.limit));
  const page = Math.floor(state.offset / state.limit) + 1;
  $('pager').innerHTML = `
    <button id="prev" ${page <= 1 ? 'disabled' : ''}>上一页</button>
    <span class="mono" style="align-self:center;color:var(--muted)">第 ${page} / ${pages} 页 · 共 ${state.total} 条</span>
    <button id="next" ${page >= pages ? 'disabled' : ''}>下一页</button>
    <button id="pageSelAll" class="page-sel">全选本页</button>`;
  const prev = $('prev'), next = $('next');
  if (prev) prev.onclick = () => { state.offset = Math.max(0, state.offset - state.limit); loadItems(); };
  if (next) next.onclick = () => { state.offset = state.offset + state.limit; loadItems(); };
  // 翻页按钮旁边就有全选本页 —— 不用再滚回表格顶部
  const selAll = $('pageSelAll');
  if (selAll) selAll.onclick = () => setPageSelection(!isPageFullySelected());
  syncCheckAll();
}

/// 可以再次同步的状态：待同步 / 有变化 / 校验失败。
/// **mismatch 不是终态** —— 上一次搬运被取消、adb 断开、md5 没过都会落成它，
/// 文件根本没入库，重跑一次就该修好，所以必须允许勾选。
/// 残片（incomplete）是手机侧缓存本身没下完整，重搬也没用，不在此列。
const CAN_SYNC_STATE = new Set(['phone_only', 'outdated', 'mismatch']);

/// 当前页"可以勾选"的条目（勾选框的 disabled 判定也用它，两处别各写一套）
function selectableRows() {
  return state.rows.filter(r => CAN_SYNC_STATE.has(r.state));
}

function isPageFullySelected() {
  const avail = selectableRows();
  return avail.length > 0 && avail.every(r => state.selected.has(r.key));
}

/// 本页全选 / 取消（不动其它页已经勾好的）
function setPageSelection(on) {
  selectableRows().forEach(r => { if (on) state.selected.add(r.key); else state.selected.delete(r.key); });
  renderRows();
}

/// 表头那个复选框必须跟着当前页走：它以前从不清状态，勾过一次之后翻到哪一页都还是勾着的
function syncCheckAll() {
  const avail = selectableRows();
  const picked = avail.filter(r => state.selected.has(r.key)).length;
  const box = $('checkAll');
  if (box) {
    box.disabled = avail.length === 0;
    box.checked = avail.length > 0 && picked === avail.length;
    box.indeterminate = picked > 0 && picked < avail.length;
    box.title = avail.length === 0
      ? '本页没有可勾选的条目（只有「待同步」「有变化」「校验失败」能重跑）'
      : `全选本页 ${avail.length} 条可同步条目 · 跨页已选 ${state.selected.size} 条`;
  }
  const btn = $('pageSelAll');
  if (btn) {
    btn.disabled = avail.length === 0;
    btn.textContent = isPageFullySelected() ? `取消本页（${avail.length}）` : `全选本页（${avail.length}）`;
  }
}

function updateSyncButton() {
  const n = state.selected.size;
  $('selCount').textContent = n;
  $('btnSync').disabled = n === 0 || state.busy;
  syncCheckAll();
}

// ==================== 库页 ====================

// source：全部 / 备份(backup) / 下载(download) / 其他(other)
const lib = { rows: [], total: 0, offset: 0, limit: 60, q: '', source: 'all', counts: null, loading: false, done: false };

async function loadLibrary(reset) {
  if (lib.loading) return;
  if (reset) { lib.rows = []; lib.offset = 0; lib.done = false; $('libGrid').innerHTML = ''; }
  if (lib.done) return;

  lib.loading = true;
  try {
    const params = new URLSearchParams({ offset: lib.offset, limit: lib.limit, source: lib.source });
    if (lib.q) params.set('q', lib.q);
    const data = await api('/library?' + params);
    lib.total = data.total;
    lib.counts = data.counts || lib.counts;
    lib.rows.push(...data.rows);
    lib.offset += data.rows.length;
    if (lib.rows.length >= data.total || data.rows.length === 0) lib.done = true;
    renderLibrary();
    renderLibTabs();
  } catch (e) {
    $('libMore').innerHTML = `<span>加载失败：${escapeHtml(e.message)}</span>`;
  } finally {
    lib.loading = false;
  }
}

// 选项卡上的数字 = 该来源的条目数（口径与服务端"最高档备份"一致）
function renderLibTabs() {
  const c = lib.counts;
  if (!c) return;
  const N = { all: c.all, backup: c.backup, download: c.download, other: c.other };
  $('libTabs').querySelectorAll('button[data-source]').forEach(b => {
    const key = b.dataset.source;
    const label = { all: '全部', backup: '备份', download: '下载', other: '其他' }[key];
    b.innerHTML = `${label}<span class="tabn">${N[key] == null ? '—' : N[key]}</span>`;
    b.classList.toggle('active', key === lib.source);
  });
}

function libCardHtml(r) {
  const p = r.page && r.page > 1 ? `<span class="pbadge">P${r.page}</span>` : '';
  const vers = r.backupCount > 1 ? `<span class="sbadge">${r.backupCount} 份</span>` : '';
  const dur = fmtDur(r.durationMs);
  return `<div class="vcard" data-key="${escapeHtml(r.key)}" title="${escapeHtml(r.title || '')}（双击播放）">
    <div class="thumb">
      <img src="${r.coverUrl}" loading="lazy" alt="" onerror="this.style.visibility='hidden'">
      <span class="shade"></span>
      ${p}${vers}
      <span class="ov left">${fmtBytes(r.size)}</span>
      ${dur ? `<span class="ov right">${dur}</span>` : ''}
    </div>
    <div class="vtitle">${escapeHtml(r.title || '(无标题)')}</div>
    <div class="vmeta"><span>${escapeHtml(r.owner || '—')}${r.syncedAt ? ' · ' + fmtDate(r.syncedAt) : ''}</span>${
      r.key ? `<button class="folderbtn" data-folder="${escapeHtml(r.key)}" title="在资源管理器里打开这个视频所在的文件夹">📁 文件夹</button>` +
              `<button class="delbtn" data-del="${escapeHtml(r.key)}" data-title="${escapeHtml(r.title || '')}" title="删除这个条目（先搬进回收站，库里不再显示）">删除</button>` : ''
    }</div>
  </div>`;
}

function renderLibrary() {
  $('libCount').textContent = lib.total ? `共 ${lib.total} 条 · 已加载 ${lib.rows.length}` : '';
  if (!lib.rows.length) {
    $('libGrid').innerHTML = '<div class="empty">总库还是空的。先去「同步」页把手机缓存搬进来。</div>';
    $('libMore').innerHTML = '';
    return;
  }
  $('libGrid').innerHTML = lib.rows.map(libCardHtml).join('');
  $('libMore').innerHTML = lib.done
    ? (lib.total > lib.limit ? '<span>已到底</span>' : '')
    : '<button class="linkbtn">加载更多…</button>';   // 可点：滚动兜底
}

// ==================== 页内播放器 ====================

const CODEC_NAME = { 7: 'AVC', 12: 'HEVC', 13: 'AV1' };
const QUALITY_NAME = {
  127: '8K', 126: '杜比视界', 125: 'HDR', 120: '4K', 116: '1080P60', 112: '1080P+',
  80: '1080P', 74: '720P60', 64: '720P', 32: '480P', 16: '360P', 6: '240P',
};

const player = { key: null, info: null, siblings: [], abort: null, widthTimer: null };

// HEVC 是唯一有风险的编码：Chrome 只有硬解、没有软解兜底，硬解不可用就是黑屏有声。
// 探一次缓存住（同一会话内浏览器能力不会变）。
let hevcProbe = null;
function probeHevc() {
  if (hevcProbe) return hevcProbe;
  hevcProbe = (async () => {
    const mc = navigator.mediaCapabilities;
    if (!mc || !mc.decodingInfo) return null;          // 老浏览器：探测不了就按"未知"放过
    try {
      const r = await mc.decodingInfo({
        type: 'file',
        video: {
          contentType: 'video/mp4; codecs="hvc1.1.6.L153.B0"',
          width: 3840, height: 2160, bitrate: 40e6, framerate: 60,
        },
      });
      return !!r.supported;
    } catch { return null; }
  })();
  return hevcProbe;
}

function showPlayerOver(text, sub, bad) {
  const ov = $('pOver');
  ov.classList.remove('hidden');
  ov.classList.toggle('bad', !!bad);
  $('pSpin').classList.toggle('hidden', !!bad);
  $('pPlay').classList.add('hidden');
  $('pOverText').textContent = text;
  $('pOverSub').textContent = sub || '';
}

function showPlayerReady(text) {
  const ov = $('pOver');
  ov.classList.remove('hidden', 'bad');
  $('pSpin').classList.add('hidden');
  $('pOverText').textContent = text || '已就绪';
  $('pPlay').classList.remove('hidden');
}

function hidePlayerOver() {
  $('pOver').classList.add('hidden');
}

function playerFail(text, sub) {
  showPlayerOver(text, sub || '点上方的「外部播放器」可以用本机 PotPlayer 打开同一个视频。', true);
}

function resetVideo() {
  const v = $('pVideo');
  clearTimeout(player.widthTimer);
  v.onplaying = null;
  v.onerror = null;
  v.onended = null;
  try { v.pause(); } catch { }
  v.removeAttribute('src');
  try { v.load(); } catch { }        // 彻底释放解码器与缓冲
}

function nextSibling() {
  const sib = player.siblings;
  if (!sib.length) return null;
  const page = (player.info && player.info.page) || 0;
  return sib.find(s => (s.page || 0) > page) || sib[0];
}

async function openPlayer(key) {
  const v = $('pVideo');
  $('playerModal').classList.remove('hidden');

  // 上一轮的准备请求作废（能连 ffmpeg 一起取消，别让它白跑一次整片读盘）
  if (player.abort) { try { player.abort.abort(); } catch { } }
  const ac = new AbortController();
  player.abort = ac;
  player.key = key;
  player.info = null;
  player.siblings = [];
  resetVideo();

  $('pTitle').textContent = '正在读取条目…';
  $('pMeta').textContent = '';
  $('pFoot').textContent = '';
  $('btnPNext').disabled = true;
  showPlayerOver('正在读取条目…', '');

  let info;
  try {
    info = await api('/play/' + encodeURIComponent(key) + '/info', { signal: ac.signal });
  } catch (e) {
    if (ac.signal.aborted) return;
    return playerFail('读取条目失败：' + e.message);
  }
  if (ac.signal.aborted) return;

  player.info = info;
  player.siblings = info.siblings || [];

  $('pTitle').textContent = info.title || key;
  $('pMeta').textContent = [
    info.owner || '—',
    info.page ? 'P' + info.page : '',
    CODEC_NAME[info.codecid] || (info.codecid ? 'codec ' + info.codecid : ''),
    QUALITY_NAME[info.quality] || (info.quality ? String(info.quality) : ''),
    fmtBytes(info.size),
    info.backupCount > 1 ? info.backupCount + ' 份备份' : '',
  ].filter(Boolean).join(' · ');
  $('btnPNext').disabled = !nextSibling();
  $('pFoot').textContent = '空格 播放/暂停 · ←/→ 5 秒 · ↑/↓ 音量 · F 全屏 · Esc 关闭　|　remux 只在内存，磁盘不留缓存';

  // HEVC 先问浏览器能不能解，明确不行就别硬试（黑屏有声比直接报错更难受）
  if (info.codecid === 12) {
    const ok = await probeHevc();
    if (ac.signal.aborted) return;
    if (ok === false) {
      return playerFail('这个浏览器解不了 HEVC',
        'Chrome 对 HEVC 只有硬件解码、没有软件兜底（HEVCVideoExtension 也帮不上 Chrome）。用「外部播放器」交给 PotPlayer，或换 Edge 试试。');
    }
  }

  showPlayerOver(info.cached ? '正在从内存读取…' : '正在合成播放文件…',
    info.cached ? '' : '把 video.m4s + audio.m4s 合成一个 MP4（-c copy，不转码）。首次要把整片读一遍，冷盘几十秒；合完立刻删临时文件，只留内存。');

  try {
    const r = await api('/play/' + encodeURIComponent(key) + '/prepare', { method: 'POST', signal: ac.signal });
    if (ac.signal.aborted) return;
    if (!r.cached) $('pOverSub').textContent = `已就绪：${fmtBytes(r.bytes)} · 用时 ${(r.ms / 1000).toFixed(1)}s（只在内存）`;
  } catch (e) {
    if (ac.signal.aborted) return;
    return playerFail('准备失败：' + e.message);
  }

  v.src = '/api/v1/play/' + encodeURIComponent(key) + '/stream';

  v.onended = () => {
    const nx = nextSibling();
    if (nx) { toast('接着播下一 P'); openPlayer(nx.key); }
    else hidePlayerOver();
  };

  v.onerror = () => {
    if (ac.signal.aborted) return;
    playerFail('播放失败', '取流或解码没成功。点上方的「外部播放器」用 PotPlayer 打开同一个视频。');
  };

  v.onplaying = () => {
    hidePlayerOver();
    clearTimeout(player.widthTimer);
    // 硬解"支持"但实际没出帧的情况确实存在（虚拟机/远程桌面/驱动 workaround）
    player.widthTimer = setTimeout(() => {
      if (v.videoWidth === 0) {
        $('pFoot').textContent = '⚠ 有声音没画面 —— 这台机器没解出 ' + (CODEC_NAME[info.codecid] || '这个编码') + '，点「外部播放器」交给 PotPlayer';
      }
    }, 3500);
  };

  // 双击是用户手势，但 remux 几十秒后手势已过期，autoplay 大概率被拦 —— 被拦就停在"点一下播放"
  const started = await v.play().then(() => true).catch(() => false);
  if (!started && !ac.signal.aborted) showPlayerReady('准备好了，点一下开始');
}

function closePlayer() {
  if (player.abort) { try { player.abort.abort(); } catch { } player.abort = null; }
  resetVideo();
  $('playerModal').classList.add('hidden');
  player.key = null;
  player.info = null;
  player.siblings = [];
}

// 外部播放器（PotPlayer 等）：走同一个内存流，页内失败时的兜底出口
async function openExternal(key) {
  try {
    const r = await api('/play/' + encodeURIComponent(key), { method: 'POST' });
    toast(`已用 ${r.player} 打开（${fmtBytes(r.bytes)}，内存）`);
  } catch (e) {
    alert('打开外部播放器失败：' + e.message);
  }
}

let toastTimer = null;
function toast(msg) {
  let el = $('toast');
  if (!el) {
    el = document.createElement('div');
    el.id = 'toast';
    el.style.cssText = 'position:fixed;left:50%;bottom:26px;transform:translateX(-50%);background:rgba(20,26,36,.92);' +
      'color:#fff;padding:9px 16px;border-radius:9px;font-size:12.5px;z-index:60;box-shadow:0 8px 22px rgba(0,0,0,.25)';
    document.body.appendChild(el);
  }
  el.textContent = msg;
  el.style.display = 'block';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.style.display = 'none'; }, 3000);
}

// ==================== Bilibili 页 ====================

// idx：mediaId → 整夹扫描快照（state/total/scanned/synced/unsynced/…），
// 服务端持有，这里只镜像；换页面回来不用重扫（服务端 10 分钟内直接复用）。
// snapItems：快照明细（含封面/UP主/时长），扫完一次拿全 → 网格直接渲染它，不再逐页打列表接口。
// items/total/pn：兜底路径（还没扫过就想先浏览时，逐页 20 条拉 B 站列表）。
const fav = {
  folders: [], folder: null, items: [], total: 0, pn: 0, selected: new Set(), loading: false,
  idx: {}, scanTimer: null, filter: 'all', snapItems: [], shown: 60,
};

// 网格数据源：有快照明细就用快照（一次全量），否则用列表接口逐页拉的
function favSrc() { return fav.snapItems.length ? fav.snapItems : fav.items; }

async function loadFolders() {
  $('favList').innerHTML = '<div class="empty">加载中…</div>';
  try {
    const r = await api('/bili/fav/folders');
    fav.folders = r.folders || [];
    $('favCount').textContent = `(${fav.folders.length})`;
    await loadFavSummary();
    if (!fav.folders.length) { $('favList').innerHTML = '<div class="empty">没有自建收藏夹</div>'; return; }
    renderFolders();
  } catch (e) {
    $('favList').innerHTML = `<div class="empty">${escapeHtml(e.message)}<br>请先登录 B 站</div>`;
  }
}

// 左栏每行的"已同步/总数"角标：只含扫过的夹
async function loadFavSummary() {
  try {
    const r = await api('/bili/fav/index/summary');
    (r.folders || []).forEach(s => { fav.idx[s.mediaId] = s; });
  } catch { }
}

// 整夹算不算"同步完成"：待同步 0 就算（失效、充电视频不计入待同步）。
// 与服务端 Unsynced() 同口径：unsynced 已经排除了 invalid/charge，所以这里只看它是否为 0；
// 再用 scanned>0 挡一下"还没扫过的空夹"（扫到 0 条的夹不能算完成）。
function favFolderDone(idx) {
  return !!idx && idx.state === 'done' && (idx.scanned || 0) > 0 && (idx.unsynced || 0) === 0;
}

function renderFolders() {
  $('favList').innerHTML = fav.folders.map(f => {
    const idx = fav.idx[f.id];
    let stamp = `<span class="fv-n">${f.mediaCount}</span>`;
    let done = '';
    if (idx && (idx.state === 'done' || idx.state === 'running')) {
      const scanning = idx.state !== 'done';
      const synced = idx.synced || 0;
      const total = idx.total || idx.scanned || f.mediaCount;
      if (!scanning && favFolderDone(idx)) {
        // 待同步 0：角标让位给"已同步"，比例和失效/充电数收进 tooltip（否则 N/N + 标签太挤）
        done = `<span class="fv-done" title="整夹同步完成：已同步 ${synced} / 共 ${total}` +
          (idx.invalid ? ` · 失效 ${idx.invalid}` : '') +
          (idx.charge ? ` · 充电 ${idx.charge}` : '') + `">已同步</span>`;
        stamp = '';
      } else {
        stamp = `<span class="fv-n ${scanning ? 'scanning' : ''}" title="已同步 ${synced} / 共 ${total}${scanning ? '（扫描中）' : ''}` +
          (idx.invalid ? ` · 失效 ${idx.invalid}` : '') +
          (idx.charge ? ` · 充电 ${idx.charge}` : '') + `">` +
          `<b>${synced}</b>/${total}</span>`;
      }
    }
    return `<div class="fav-item ${fav.folder && fav.folder.id === f.id ? 'active' : ''}" data-fid="${f.id}">
      <span class="fv-title" title="${escapeHtml(f.title)}">${escapeHtml(f.title)}</span>
      ${done}${stamp}
    </div>`;
  }).join('');
  $('favList').querySelectorAll('.fav-item').forEach(el => {
    el.onclick = () => openFolder(Number(el.dataset.fid));
  });
}

async function openFolder(fid) {
  const folder = fav.folders.find(f => f.id === fid) || null;
  fav.folder = folder;
  fav.items = [];
  fav.total = 0;
  fav.pn = 0;
  fav.snapItems = [];
  fav.shown = 60;
  fav.selected.clear();
  renderFolders();
  $('favTitle').textContent = folder ? folder.title : '—';
  $('favMeta').textContent = folder ? `${folder.mediaCount} 个内容` : '';
  $('favGrid').innerHTML = '<div class="empty">加载中…</div>';
  fav.selected.clear();
  renderFavTabs();
  applyFavIndex(fav.idx[fid]);          // 先亮出上次的扫描结果，避免等接口时空白
  // 扫完的夹：快照里有全量明细（含封面），直接渲染，不必再翻 47 页列表
  // 没扫过的：走列表接口逐页拉（此时筛选禁用，等扫完再启用）
  if (!fav.snapItems.length) await loadFavItems();
  else renderFavGrid();
  scanFav(false);                        // 点开即扫（服务端 10 分钟内已扫过的直接复用）
  ensureFavIndexItems(fid);              // 复用旧扫描时，明细（封面/多P）可能没跟着回来，补一次
}

// 扫过的夹直接复用了快照，但前端缓存里可能只有状态没有 items 明细 → 多P 筛不出，补拉一次
async function ensureFavIndexItems(fid) {
  const s = fav.idx[fid];
  if (!s || s.state !== 'done' || (s.items && s.items.length)) return;
  try {
    const full = await api('/bili/fav/index?mediaId=' + fid + '&items=1');
    if (!fav.folder || fav.folder.id !== fid) return;
    applyFavIndex(full);
    patchFavInLib(full.items || []);
    renderFavGrid();
  } catch { }
}

/* ---------- 整夹扫描（判断"已在库"） ---------- */

function favIdxOf() { return fav.folder ? fav.idx[fav.folder.id] : null; }

function applyFavIndex(s) {
  if (!s || s.mediaId == null) { renderFavStat(); return; }
  fav.idx[s.mediaId] = s;
  // 快照明细就是网格数据源：扫完一次拿全，之后"已同步/未同步/多P"都以它为准（与左栏同一口径）
  if (s.items && s.items.length && fav.folder && fav.folder.id === s.mediaId) {
    fav.snapItems = s.items;
    fav.items = [];                      // 快照优先，旧的逐页数据作废
    renderFavGrid();                     // 快照是网格数据源，换它就得重渲
  }
  renderFavStat();
  renderFolders();
  renderFavTabs();
}

function renderFavStat() {
  const s = favIdxOf();
  const head = $('favHeadStat');
  const meta = $('favMeta');
  const btn = $('btnFavScan');
  const busy = s && (s.state === 'running' || s.state === 'queued');

  if (!s || s.state === 'none' || !fav.folder) {
    head.textContent = fav.folder ? '未扫描' : '';
    head.className = 'fsh-stat';
    if (fav.folder) meta.textContent = `${fav.folder.mediaCount} 个内容`;
    btn.disabled = !fav.folder;
    btn.textContent = '扫描';
  } else if (busy) {
    head.className = 'fsh-stat scanning';
    head.textContent = s.state === 'queued' ? '排队扫描…' : `扫描中 ${s.scanned || 0}/${s.total || '…'}`;
    meta.textContent = `${fav.folder.mediaCount} 个内容 · 扫描中…`;
    btn.disabled = true;
    btn.textContent = '扫描中…';
  } else if (s.state === 'failed') {
    head.className = 'fsh-stat bad';
    head.textContent = '扫描失败：' + (s.message || '');
    btn.disabled = false;
    btn.textContent = '重试扫描';
    btn.title = '再扫一次（扫描失败不会留下可用结果）';
  } else {
    head.className = 'fsh-stat';
    head.innerHTML = `已同步 <b class="ok">${s.synced}</b> · 未同步 <b class="todo">${s.unsynced}</b> · 共 <b>${s.scanned}</b>`;
    // 显示扫描时刻：点开收藏夹只复用 10 分钟内的旧快照，不写时间的话"怎么没刷新"没法自证
    meta.textContent = `共 ${s.scanned} 个内容` + (s.invalid ? ` · 失效 ${s.invalid}` : '') + scanAgo(s.updatedAt);
    btn.disabled = false;
    btn.textContent = '重新扫描';
    btn.title = '强制重扫一遍。点开这个收藏夹时，服务端 10 分钟内的旧结果会直接复用，这个按钮就是绕开复用的入口';
  }

  // 整夹下载按"未同步"个数给按钮上量；整夹都已同步就直接禁用
  const whole = $('btnFavWhole');
  if (whole) {
    const done = s && s.state === 'done';
    whole.disabled = !fav.folder || state.busy || (done && (s.unsynced || 0) === 0);
    whole.textContent = done
      ? (s.unsynced === 0 ? '整夹已同步' : `下载整夹（未同步 ${s.unsynced}）`)
      : '下载整夹';
  }
}

// 「N 分钟前扫描」——配合服务端的 10 分钟快照复用，让人一眼看出这次是不是复用了旧结果
function scanAgo(iso) {
  if (!iso) return '';
  const ms = Date.now() - new Date(iso).getTime();
  if (!isFinite(ms) || ms < 0) return '';
  const m = Math.round(ms / 60000);
  if (m < 1) return ' · 刚刚扫描';
  if (m < 60) return ` · ${m} 分钟前扫描`;
  return ` · ${Math.round(m / 60)} 小时前扫描`;
}

async function scanFav(force) {
  if (!fav.folder) return;
  const fid = fav.folder.id;
  try {
    const s = await api('/bili/fav/scan', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mediaId: fid, folderTitle: fav.folder.title, force: !!force }),
    });
    if (!fav.folder || fav.folder.id !== fid) return;
    applyFavIndex(s);
    if (s && (s.state === 'running' || s.state === 'queued')) pollFavScan(fid);
  } catch (e) {
    if (!fav.folder || fav.folder.id !== fid) return;
    $('favHeadStat').className = 'fsh-stat bad';
    $('favHeadStat').textContent = '扫描失败：' + e.message;
  }
}

let scanMiss = 0;
function pollFavScan(fid) {
  clearTimeout(fav.scanTimer);
  fav.scanTimer = setTimeout(async () => {
    if (!fav.folder || fav.folder.id !== fid) return;
    try {
      const s = await api('/bili/fav/index?mediaId=' + fid);
      if (!fav.folder || fav.folder.id !== fid) return;
      if (s.state === 'running' || s.state === 'queued') {
        scanMiss = 0;
        applyFavIndex(s);
        pollFavScan(fid);
        return;
      }
      // 结束：拉一次带明细的快照，把已加载的卡牌打上"已在库"
      applyFavIndex(s);                       // 先落状态，明细失败也不至于卡在"扫描中"
      const full = await api('/bili/fav/index?mediaId=' + fid + '&items=1');
      applyFavIndex(full);
      patchFavInLib(full.items || []);
    } catch (e) {
      if (++scanMiss > 5) { scanMiss = 0; toast('扫描结果读取失败：' + e.message); return; }
      pollFavScan(fid);
    }
  }, 700);
}

// 扫描结果回填到已渲染的卡牌上（不重排网格，滚动位置不丢）
function patchFavInLib(items) {
  if (!items.length) return;
  const map = new Map(items.map(i => [String(i.avid), i]));
  let changed = false;
  favSrc().forEach(it => {
    const m = map.get(String(it.avid));
    if (!m) return;
    if (m.inLib && !it.inLib) { it.inLib = true; changed = true; }
    if (m.multiP && it.multiP !== m.multiP) { it.multiP = m.multiP; changed = true; }
  });
  if (changed) renderFavGrid(); else renderFavTabs();   // 计数/多P 变了也要刷新筛选条
}

async function loadFavItems() {
  if (!fav.folder || fav.loading) return;
  fav.loading = true;
  $('favMore').innerHTML = '<span>加载中…</span>';
  try {
    for (let i = 0; i < 3; i++) {
      if (fav.total && fav.items.length >= fav.total) break;
      fav.pn++;
      const r = await api(`/bili/fav/items?mediaId=${fav.folder.id}&pn=${fav.pn}&ps=20`);
      fav.total = r.total;
      fav.items.push(...r.items);
      if (!r.items.length) break;
    }
    renderFavGrid();
  } catch (e) {
    $('favGrid').innerHTML = `<div class="empty">${escapeHtml(e.message)}</div>`;
    $('favMore').innerHTML = '';
  } finally {
    fav.loading = false;
  }
}


// multiP 页数：扫描索引里有（下载作业回报后标记）；B 站列表接口本身不带
function favMultiP(it) {
  const idxItem = (favIdxOf()?.items || []).find(x => String(x.avid) === String(it.avid));
  return (idxItem && idxItem.multiP) || it.multiP || 0;
}

/* ---------- 筛选：全部 / 已同步 / 未同步 / 已失效 / 多P / 充电 ---------- */

const FAV_FILTER_LABEL = { all: '全部', synced: '已同步', unsynced: '未同步', invalid: '已失效', multi: '多P', charge: '充电' };

// 归桶优先级与卡片徽标一致：失效 > 充电 > 多P > 在库/未在库
//（充电视频多P时先被多P分支拦下，不会重复计数；两者互斥是下载路径决定的）
function favBucket(it) {
  if (it.invalid) return 'invalid';
  if (it.charge) return 'charge';
  if (favMultiP(it) > 1) return 'multi';
  return it.inLib ? 'synced' : 'unsynced';
}

// 筛选只在快照明细（整夹扫描结果）到位后才可用 —— 没扫过就给半截计数，数字会和左栏对不上
function favFilterReady() { return fav.snapItems.length > 0; }

// 当前筛选下的全量条目（计数用，不受本地分页影响）
function favFilteredAll() {
  const src = favSrc();
  return fav.filter === 'all' ? src : src.filter(i => favBucket(i) === fav.filter);
}

// 实际渲染的卡片：快照路径再套一层本地分页（数据已全在本地，翻页不再打接口）
function favVisible() {
  const f = favFilteredAll();
  return fav.snapItems.length ? f.slice(0, fav.shown) : f;
}

// 可勾选的：未失效、不在库、非多P、非充电（充电的下必失败，勾了也白搭）
function favSelectable(list) {
  return (list || favSrc()).filter(i => !i.invalid && !i.inLib && !i.charge && favMultiP(i) <= 1);
}

function renderFavTabs() {
  const box = $('favTabs');
  if (!box) return;
  const src = favSrc();
  const c = { synced: 0, unsynced: 0, invalid: 0, multi: 0, charge: 0 };
  src.forEach(it => { c[favBucket(it)]++; });
  const ready = favFilterReady();
  box.querySelectorAll('button').forEach(b => {
    const k = b.dataset.ff;
    b.classList.toggle('active', fav.filter === k);
    b.disabled = k !== 'all' && !ready;               // 未扫描：只留"全部"
    const n = b.querySelector('.tabn');
    if (!n) return;
    n.textContent = k === 'all'
      ? (fav.snapItems.length ? String(src.length)
        : (fav.total ? `${fav.items.length}/${fav.total}` : (fav.items.length || '')))
      : (ready ? String(c[k]) : '');                  // 未就绪不显示数字，避免误读
  });
  const hint = $('favFilterHint');
  if (hint) {
    const s = favIdxOf();
    hint.textContent = !fav.folder || ready ? ''
      : (!s || s.state === 'none') ? '扫描完成后才能按状态筛选（打开收藏夹会自动扫）'
        : (s.state === 'running' || s.state === 'queued') ? '扫描中…，扫完即可按状态筛选'
          : s.state === 'failed' ? '扫描失败，筛选暂不可用（可点扫描重试）'
            : '正在读取扫描结果…';
  }
}

function favCardHtml(it) {
  const key = String(it.avid);
  const isMulti = favMultiP(it) > 1;
  // 充电视频与失效一样：点了也下不动，直接置灰不给勾（避免整批下载里混进去白跑）
  const disabled = it.invalid || !!it.charge;
  if (it.invalid || it.inLib || isMulti || it.charge) fav.selected.delete(key);
  const checked = fav.selected.has(key) ? 'checked' : '';
  const cover = it.cover ? '/api/v1/bili/cover?u=' + encodeURIComponent(it.cover) : '';
  const dur = fmtDur((it.duration || 0) * 1000);
  const badge = it.invalid ? '<span class="cbadge">已失效</span>'
    : it.charge ? '<span class="cbadge charge" title="充电专属视频：playurl 返回 87008，账号没充电（或已过期）B站不给流。需先给 UP 充电，或从手机拷贝">⚡ 充电</span>'
    : isMulti ? `<span class="cbadge multi">多P · ${favMultiP(it)} 个${it.inLib ? ' · 部分已在库' : ''}</span>`
    : it.inLib ? '<span class="sbadge">已在库</span>' : '';
  return `<div class="vcard ${disabled ? 'dim' : ''}" data-avid="${key}" data-disabled="${disabled}" title="${escapeHtml(it.title)}（点卡片按分P下载）">
    <div class="thumb">
      ${cover ? `<img src="${escapeHtml(cover)}" loading="lazy" alt="" onerror="this.style.visibility='hidden'">` : ''}
      <span class="shade"></span>
      ${badge}
      <span class="pick"><input type="checkbox" data-avid="${key}" ${checked} ${(it.inLib || isMulti || it.invalid || it.charge) ? 'disabled' : ''}></span>
      ${dur ? `<span class="ov right">${dur}</span>` : ''}
    </div>
    <div class="vtitle">${escapeHtml(it.title || '(无标题)')}</div>
    <div class="vmeta"><span>${escapeHtml(it.owner || '—')}</span>${biliLinkHtml(biliUrl(it.avid, it.bvid))}</div>
  </div>`;
}

function renderFavGrid() {
  const src = favSrc();
  const all = favFilteredAll();
  const vis = favVisible();
  renderFavTabs();

  if (!src.length) {
    $('favGrid').innerHTML = '<div class="empty">这个收藏夹是空的</div>';
    $('favMore').innerHTML = '';
    renderFavToolbar();
    return;
  }
  if (!vis.length) {
    const scope = fav.snapItems.length ? `全夹 ${src.length} 条里` : `已加载的 ${src.length} 条里`;
    $('favGrid').innerHTML = `<div class="empty">${scope}没有「${FAV_FILTER_LABEL[fav.filter]}」的条目</div>`;
  } else {
    $('favGrid').innerHTML = vis.map(favCardHtml).join('');
  }

  if (fav.snapItems.length) {
    // 快照路径：本地分页，翻页零请求
    const tail = fav.filter === 'all' ? '' : ` · 筛出 ${all.length}`;
    $('favMore').innerHTML = vis.length < all.length
      ? `<span>显示更多…（已显示 ${vis.length}/${all.length}${tail}）</span>`
      : `<span>已到底 · 共 ${all.length} 条${tail}</span>`;
  } else {
    const tail = fav.filter === 'all' ? '' : ` · 筛出 ${all.length}`;
    $('favMore').innerHTML = (!fav.total || fav.items.length < fav.total)
      ? `<span>加载更多…（已加载 ${fav.items.length}/${fav.total}${tail}）</span>`
      : `<span>已到底${tail}</span>`;
  }
  renderFavToolbar();
}

function renderFavToolbar() {
  // 多P视频不进批量勾选（只下选中的P才有意义），只能点卡片单独确认；
  // "全选"作用于当前筛选下的全部可勾选项（快照路径下不受分页限制）
  const avail = favSelectable(favFilteredAll());
  $('favSel').textContent = fav.selected.size;
  $('btnFavGo').disabled = fav.selected.size === 0 || !fav.folder;
  $('btnFavAll').disabled = avail.length === 0;
  $('btnFavAll').textContent = (avail.length > 0 && avail.every(i => fav.selected.has(String(i.avid))))
    ? '取消全选' : `全选（${avail.length}）`;
  renderFavStat();
}

/**
 * keys = null → 整夹下载：服务端分页拉全，已在库的自动跳过（去重口径与扫描一致）。
 * keys = [...] → 只下当前勾选的这些 avid。
 */
async function startFavDownload(keys) {
  if (!fav.folder) return;
  const whole = keys === null;
  if (!whole && !keys.length) return;

  const idx = favIdxOf();
  const scanned = idx && idx.state === 'done';
  const msg = whole
    ? (scanned
      ? `下载「${fav.folder.title}」整个收藏夹？\n\n共 ${idx.scanned} 条：已同步 ${idx.synced}，未同步 ${idx.unsynced}` +
        `${idx.invalid ? `，失效 ${idx.invalid}` : ''}\n本次将下载 ${idx.unsynced} 条（取最高档位，已在库的自动跳过）\n\n注意：多P视频会跳过，点卡片可单独选P下载`
      : `下载「${fav.folder.title}」整个收藏夹？\n\n还没扫描过，服务端会自己分页去重（已在库的跳过），耗时略长。\n\n注意：多P视频会跳过，点卡片可单独选P下载`)
    : `从「${fav.folder.title}」下载 ${keys.length} 个视频到总库？\n（取最高档位；总库已有的会自动跳过；多P视频会跳过）`;

  if (!confirm(msg)) return;
  try {
    const body = { mediaId: fav.folder.id, folderTitle: fav.folder.title };
    if (!whole) body.keys = keys;
    const r = await api('/fav/download', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    // 作业结束后刷新扫描索引：multiP 标记由服务端在作业回报时并入，这里拉新快照
    fav.pendingRefresh = { jobId: r.jobId, mediaId: fav.folder.id };
    if (!whole) { fav.selected.clear(); renderFavGrid(); }
    toast(whole ? `整夹已加入队列（${scanned ? idx.unsynced : '待服务端统计'} 条，多P会跳过）` : '已加入任务队列（多P会跳过）');
  } catch (e) {
    alert('启动失败：' + e.message);
  }
}

/* ---------- 分P选择框（多P视频单独确认下哪几个P） ---------- */

// infos：cid → /bili/preview/info 的结果（打开面板就串行预取，点某P直接用，不用再等一次 playurl）
// pvState：cid → 'loading' | 'ready' | 'err'，行尾显示，让用户知道哪几P已经能秒开
const pm = { avid: 0, pages: [], selected: new Set(), open: false, openToken: 0,
             infos: {}, pvState: {}, playing: 0,
             prebuf: {},       // cid → {qual, bytes, total}：每P预取的"3秒起步油"（点哪P哪P秒开）
             preAbort: null }; // 正在跑的预取请求，点P开播时掐掉，把带宽让给播放

// 预览音频开关：默认关。省流量、躲开浏览器给音频 SourceBuffer 的 12MB 小配额、首帧更快；
// 用户开了就记住（localStorage），中途开从当前位置接上声音
const PM_AUDIO_KEY = 'pmAudioOn';
const pmAudioOn = () => localStorage.getItem(PM_AUDIO_KEY) === '1';
function pmAudioBtn() { $('btnPmAudio').textContent = pmAudioOn() ? '声音：开' : '声音：关'; }

async function openPagesModal(avid) {
  pm.avid = avid; pm.pages = []; pm.selected = new Set(); pm.open = true;
  pm.infos = {}; pm.pvState = {}; pm.playing = 0;
  pm.prebuf = {};
  pm.openToken++;
  pv.token++;                             // 换稿件：掐掉上一个还在填充的预览
  pvStopBuffers();
  $('pmTitle').textContent = '读取分P…';
  $('pmMeta').textContent = '';
  $('pmList').innerHTML = '<div class="empty" style="padding:18px">加载中…</div>';
  $('pmPvStat').textContent = '';
  $('pmQuality').innerHTML = '';
  $('pmSel').textContent = '';
  $('btnPmGo').disabled = true;
  pmAudioBtn();
  $('pagesModal').classList.remove('hidden');
  try {
    const r = await api('/bili/fav/pages?avid=' + avid);
    if (!pm.open) return;                 // 用户已关掉
    pm.pages = r.pages || [];
    $('pmTitle').textContent = r.title || (' avid ' + avid);
    $('pmMeta').textContent = (r.owner || '') + (pm.pages.length > 1 ? ` · 共 ${pm.pages.length} 个分P` : ' · 单P');
    renderPmList();
    pmPreloadAll();                       // 每一P的流信息后台串行预取，第一个就绪就开播
  } catch (e) {
    $('pmList').innerHTML = `<div class="empty" style="padding:18px">${escapeHtml(e.message)}</div>`;
  }
}

function closePagesModal() {
  pm.open = false; pm.playing = 0;
  pv.token++;
  pvStopBuffers();
  const el = $('pmVideo');
  try { el.removeAttribute('src'); el.load(); } catch { }
  if (pv.url) { URL.revokeObjectURL(pv.url); pv.url = ''; }
  $('pagesModal').classList.add('hidden');
}

// 行不再是 <label>（label 会把点行变成切换勾选）；点整行 = 预览，只有行首勾选框才选"要下载"
function renderPmList() {
  $('pmList').innerHTML = pm.pages.map(p => {
    const key = String(p.cid);
    if (p.inLib) pm.selected.delete(key);
    const st = pm.pvState[p.cid];
    const stTxt = st === 'loading' ? '读取中…' : st === 'err' ? '预览不可用' : '';
    return `<div class="pm-row ${p.inLib ? 'inlib' : ''} ${pm.playing === p.cid ? 'playing' : ''}" data-cid="${p.cid}" data-page="${p.page}" title="点左侧「方框+P号」区域：勾选下载　点右侧标题：预览这一P">
      <span class="pmsel">
        <input type="checkbox" data-cid="${p.cid}" ${pm.selected.has(key) ? 'checked' : ''} ${p.inLib ? 'disabled' : ''}>
        <span class="pp">P${p.page}</span>
      </span>
      <span class="pt" title="${escapeHtml(p.part || '')}">${escapeHtml(p.part || '(无标题)')}</span>
      ${p.duration ? `<span class="pd">${fmtDur(p.duration * 1000)}</span>` : ''}
      ${p.inLib ? '<span class="sbadge">已在库</span>' : ''}
      <span class="pvst${st === 'err' ? ' err' : ''}" data-st="${p.cid}">${stTxt}</span>
    </div>`;
  }).join('');
  renderPmToolbar();
}

// 只改一行的状态文字/高亮，不整表重渲染 —— 保住滚动位置和勾选焦点
function pmUpdateRowState(cid) {
  const row = $('pmList').querySelector(`.pm-row[data-cid="${cid}"]`);
  if (!row) return;
  const st = pm.pvState[cid];
  const span = row.querySelector('.pvst');
  if (span) {
    span.textContent = st === 'loading' ? '读取中…' : st === 'err' ? '预览不可用' : '';
    span.className = 'pvst' + (st === 'err' ? ' err' : '');
  }
}

function pmMarkPlaying() {
  $('pmList').querySelectorAll('.pm-row').forEach(r => {
    r.classList.toggle('playing', Number(r.dataset.cid) === pm.playing);
  });
}

// 下载类作业允许排队入队（后端 JobManager 的 worker 是串行的，后来的自动排队），
// 所以有作业在跑时不禁用按钮，只是文案改成"加入队列"
function queueHint() {
  const q = (state.jobs || []).filter(j => j.state === 'queued' || j.state === 'running').length;
  return q ? ` · 将排队第 ${q + 1} 位执行` : '';
}

function renderPmToolbar() {
  const todo = pm.pages.filter(p => !p.inLib).length;
  $('pmSel').textContent = `已选 ${pm.selected.size} · 未入库 ${todo}${state.busy ? queueHint() : ''}`;
  $('btnPmGo').disabled = pm.selected.size === 0;
  $('btnPmGo').textContent = state.busy ? '加入队列' : '下载所选 P';
}

/* ---------- 在线预览：服务端 Range 代理 + MSE 双轨 ---------- */

// 为什么必须走服务端代理：B 站 CDN 校验 Referer，浏览器直连 403。
// 为什么敢直接 append：B 站 dash 的 m4s **自包含 init segment**（头两个 box 就是 ftyp+moov），
// 不用自己切 init，分块 append 即可（已实测）。
const pv = { avid: 0, cid: 0, info: null, ms: null, vSb: null, aSb: null, token: 0, url: '',
             vUrl: '', vSize: 0, seeking: false };

// 打开面板就串行把每一P处理到位：先拿流清单（playurl，服务端 10 分钟缓存 + pacer，串行带间隔防风控），
// 再顺手预取 3 秒"起步油"。第一个拿到结论的P立刻开播（用户在等画面），其余后台继续。
async function pmPreloadAll() {
  const token = pm.openToken;
  for (const p of pm.pages) {
    if (!pm.open || token !== pm.openToken) return;
    if (!pm.infos[p.cid]) {
      pm.pvState[p.cid] = 'loading';
      pmUpdateRowState(p.cid);
      try {
        pm.infos[p.cid] = await api(`/bili/preview/info?avid=${pm.avid}&cid=${p.cid}`);
        pm.pvState[p.cid] = 'ready';
      } catch { pm.pvState[p.cid] = 'err'; }
      pmUpdateRowState(p.cid);
      // 第一个拿到结论的P立刻开播（用户在等画面），其余后台继续
      if (!pm.playing && pm.pvState[p.cid] === 'ready') pmPlay(p.cid, p.page);
      await new Promise(r => setTimeout(r, 180));
    }
    await pmPrebuf(p.cid, token);
  }
}

// 预取一P的起步油：video 前 3 秒（bandwidth 是 bps → 3s = ×3/8，clamp 在 256KB~1MB）。
// Range 响应的 Content-Range 白送真实总长，点开时连"探长度"那一步都省了。
async function pmPrebuf(cid, token) {
  const info = pm.infos[cid];
  if (!info) return;
  const v = (info.videos || []).find(x => x.id === info.pickVideoId && x.codecid === info.pickVideoCodecid)
         || (info.videos || [])[0];
  if (!v) return;
  const qual = `${v.id}:${v.codecid}`;
  const old = pm.prebuf[cid];
  if (old && old.qual !== qual) delete pm.prebuf[cid];   // 用户换过画质，旧油作废
  else if (old) return;
  const want = Math.min(Math.max(Math.round((v.bandwidth || 1200000) * 3 / 8), 256 * 1024), 1024 * 1024);
  const ac = new AbortController();
  pm.preAbort = ac;
  try {
    const r = await fetch(`/api/v1/bili/preview/stream?avid=${pm.avid}&cid=${cid}&track=video&id=${v.id}&codecid=${v.codecid}`,
      { headers: { Range: `bytes=0-${want - 1}` }, signal: ac.signal });
    if (!pm.open || token !== pm.openToken) return;
    const buf = await r.arrayBuffer();
    if (!buf.byteLength) return;
    const total = Number((/\/(\d+)\s*$/.exec(r.headers.get('Content-Range') || '') || [])[1] || 0);
    pm.prebuf[cid] = { qual, bytes: buf, total };
  } catch { /* 预取失败不致命：点开时现拉 */ }
  finally { if (pm.preAbort === ac) pm.preAbort = null; }
}

// 预览某一P：行点击、或预加载就绪后自动开播都走这里
async function pmPlay(cid, page) {
  if (!pm.open) return;
  // 点正在播的那一行：什么都不做。重新加载会白白再缓冲一次（几 MB 起步）
  if (pm.playing === cid) return;
  pm.playing = cid;
  pmMarkPlaying();
  const my = ++pv.token;
  pvStopBuffers();

  let info = pm.infos[cid];
  if (!info) {
    $('pmPvStat').textContent = `P${page} · 读取流信息…`;
    pm.pvState[cid] = 'loading';
    pmUpdateRowState(cid);
    try {
      info = pm.infos[cid] = await api(`/bili/preview/info?avid=${pm.avid}&cid=${cid}`);
      pm.pvState[cid] = 'ready';
    } catch (e) {
      pm.pvState[cid] = 'err';
      $('pmPvStat').textContent = `P${page} 无法预览：${e.message || e}`;
    }
    pmUpdateRowState(cid);
    if (!info) return;
  }
  if (my !== pv.token) return;
  pv.avid = pm.avid; pv.cid = cid; pv.info = info;
  pmFillQuality(info);
  await pvStart(my, `P${page}`);
}

function pmFillQuality(info) {
  const vs = (info.videos || []).slice().sort((a, b) => a.id - b.id);
  if (!vs.length) { $('pmPvStat').textContent = 'B站没给视频流（充电视频或被风控）'; return; }
  $('pmQuality').innerHTML = vs.map(v => {
    const nm = QUALITY_NAME[v.id] || ('q' + v.id);
    const cc = CODEC_NAME[v.codecid] || ('c' + v.codecid);
    const sel = (v.id === info.pickVideoId && v.codecid === info.pickVideoCodecid) ? 'selected' : '';
    // 注意：dash 的 size 恒为 0（B站不填），真实长度只能靠 Range 探测，这里只显示分辨率
    const wh = v.width && v.height ? ` · ${v.width}×${v.height}` : '';
    return `<option value="${v.id}:${v.codecid}" ${sel}>${nm} · ${cc}${wh}</option>`;
  }).join('');
  const a = (info.audios || []).slice().sort((x, y) => x.size - y.size)[0];
  $('pmPvStat').textContent = `${vs.length} 个档位 · 默认最低档${a ? ' · 带音频' : ' · 无音频流'}`;
}

// 预览某一P的播放器启动。节奏：首块（预取的3秒或现拉1MB）落地就 play，
// 之后后台全速续拉到整段结束；数据默认全留在 SourceBuffer 里（进度条完整、任意拖拽零等待），
// 只有逼近浏览器配额才清已播区间（见 pvFreeBehind）。
async function pvStart(my, label = '') {
  const info = pv.info;
  if (!info) return;
  pm.preAbort?.abort();                 // 掐掉正在跑的预取，带宽让给这一P
  pvStopBuffers();
  const say = (t) => { if (my === pv.token) $('pmPvStat').textContent = label ? `${label} · ${t}` : t; };

  const [idStr, ccStr] = ($('pmQuality').value || '').split(':');
  const id = Number(idStr), codecid = Number(ccStr);
  const v = (info.videos || []).find(x => x.id === id && x.codecid === codecid)
         || (info.videos || [])[0];
  if (!v) { say('这一档不可用'); return; }

  const el = $('pmVideo');
  if (!window.MediaSource) { say('当前浏览器不支持 MSE，无法在线预览'); return; }

  const base = `/api/v1/bili/preview/stream?avid=${pv.avid}&cid=${pv.cid}`;
  const vUrl = `${base}&track=video&id=${v.id}&codecid=${v.codecid}`;
  const CH0 = 1024 * 1024;              // 现拉首块 1MB：越小首帧越快，续拉仍是 3MB 块

  say('缓冲中…');
  const pb = pm.prebuf[pv.cid];
  let first, vOff = 0, vTotal = 0;
  if (pb && pb.qual === `${v.id}:${v.codecid}` && pb.bytes && pb.bytes.byteLength) {
    first = pb.bytes; vOff = first.byteLength; vTotal = pb.total || 0;
    delete pm.prebuf[pv.cid];           // 起步油用掉就释放内存
  } else {
    const r = await pvFetch(vUrl, 0, CH0 - 1);
    if (my !== pv.token) return;
    first = await r.arrayBuffer(); vOff = first.byteLength;
    vTotal = Number((/\/(\d+)\s*$/.exec(r.headers.get('Content-Range') || '') || [])[1] || 0);
  }
  // dash 的 size 恒 0，真实总长从 Content-Range 白拿；真没有再退回 Range 0-0 探
  if (!vTotal) vTotal = await pvLength(vUrl);
  if (my !== pv.token) return;
  if (!vTotal) { say('拿不到流长度，无法预览'); return; }
  pv.vUrl = vUrl; pv.vSize = vTotal;

  const ms = new MediaSource();
  pv.ms = ms;
  if (pv.url) URL.revokeObjectURL(pv.url);
  pv.url = URL.createObjectURL(ms);
  el.src = pv.url;

  await new Promise(res => ms.addEventListener('sourceopen', res, { once: true }));
  if (my !== pv.token) return;
  try {
    pv.vSb = ms.addSourceBuffer(`video/mp4; codecs="${v.codecs || 'avc1.64001F'}"`);
  } catch {
    say('浏览器解不了这个编码：' + (v.codecs || '?') + '（换一档试试）');
    return;
  }

  // 显式 duration：边下边播没 endOfStream 也有进度条；拉完 endOfStream 会校准到实际值
  const page = pm.pages.find(x => x.cid === pv.cid);
  if (page?.duration) { try { ms.duration = page.duration; } catch { } }

  try { await pvAppend(pv.vSb, first); }
  catch (e) { say('缓冲失败：' + (e.message || e)); return; }

  say(`已缓冲 ${fmtBytes(vOff)} / ${fmtBytes(vTotal)}`);
  try { await el.play(); } catch { /* 自动播放被拦，用户点一下即可 */ }

  // 音频开关开着才灌（默认关）：与视频并行，音频只有几 MB；失败不致命，画面照播
  let aPump = Promise.resolve();
  const a = pmAudioOn() ? (info.audios || []).slice().sort((x, y) => x.size - y.size)[0] : null;
  if (a) {
    const aUrl = `${base}&track=audio&id=${a.id}`;
    aPump = (async () => {
      try {
        pv.aSb = ms.addSourceBuffer(`audio/mp4; codecs="${a.codecs}"`);
        a.size ||= await pvLength(aUrl);
        if (my !== pv.token || !a.size || !pv.aSb) return;
        await pvPump(pv.aSb, aUrl, a.size, 0, my);
      } catch { }
    })();
  }

  try {
    await pvPump(pv.vSb, vUrl, vTotal, vOff, my, (off) => {
      say(`已缓冲 ${fmtBytes(off)} / ${fmtBytes(vTotal)}`);
    });
    await aPump;
    if (my !== pv.token) return;
    if (ms.readyState === 'open') ms.endOfStream();
    say(`整段就绪 · ${fmtBytes(vTotal)}`);
  } catch (e) {
    if (my === pv.token) say('缓冲失败：' + (e.message || e));
  }
}

// dash 不给 size，用一次 Range 0-0 从 Content-Range 里读总长
async function pvLength(url) {
  try {
    const r = await fetch(url, { headers: { Range: 'bytes=0-0' } });
    const cr = r.headers.get('Content-Range') || '';
    const m = /\/(\d+)\s*$/.exec(cr);
    if (m) return Number(m[1]);
    const cl = Number(r.headers.get('Content-Length') || 0);
    return r.status === 206 ? 0 : cl;   // 206 却没有总长 → 认不出来，交给调用方
  } catch { return 0; }
}

async function pvFetch(url, from, to) {
  return fetch(url, { headers: { Range: `bytes=${from}-${to}` } });
}

// 从 from 字节顺序拉到 size 并 append。默认全量灌到底：预览默认最低档一般几十 MB，
// 离浏览器给视频 SourceBuffer 的 150MB 配额很远；真快满了由 pvFreeBehind 清已播部分兜底。
async function pvPump(sb, url, size, from, token, onTick) {
  const CH = 3 * 1024 * 1024;
  let off = from;
  while (off < size) {
    if (token !== pv.token) return false;
    const end = Math.min(off + CH, size) - 1;
    const res = await pvFetch(url, off, end);
    if (!res.ok && res.status !== 206) throw new Error('代理返回 ' + res.status);
    const buf = await res.arrayBuffer();
    if (token !== pv.token) return false;
    try {
      await pvAppend(sb, buf);
    } catch (e) {
      // appendBuffer 抛错八成是配额满了：清掉播放位置之前的数据再试一次
      await pvFreeBehind(sb);
      await pvAppend(sb, buf);
    }
    off = end + 1;
    onTick?.(off);
  }
  return true;
}

function pvAppend(sb, buf) {
  return new Promise((resolve, reject) => {
    const ok = () => { cleanup(); resolve(); };
    const bad = () => { cleanup(); reject(new Error('SourceBuffer 出错')); };
    const cleanup = () => {
      sb.removeEventListener('updateend', ok);
      sb.removeEventListener('error', bad);
    };
    sb.addEventListener('updateend', ok, { once: true });
    sb.addEventListener('error', bad, { once: true });
    try { sb.appendBuffer(buf); } catch (e) { cleanup(); reject(e); }   // QuotaExceededError 在这里同步抛
  });
}

// 配额兜底：把播放位置之前的数据清掉（浏览器对 SourceBuffer 有硬上限，与系统内存无关）
async function pvFreeBehind(sb) {
  const el = $('pmVideo');
  const cut = Math.max(0, el.currentTime - 10);
  if (cut <= 0.5 || sb.updating) return;
  try { sb.abort(); } catch { }
  try {
    sb.remove(0, cut);
    await new Promise(r => sb.addEventListener('updateend', r, { once: true }));
  } catch { }
}

// ---------- 拖拽兜底 ----------
// 默认整段都在缓冲里，seek 浏览器自己搞定；只有被配额清理过的区间才缺数据。
// 缺了就从"目标时间按比例折算的字节位置"重新拉：视频 VBR 只能估个大概，
// 靠找下一个 moof 分片边界来对齐（偏差最多一两个分片）。

function pvCovered(sb, t) {
  const b = sb.buffered;
  for (let i = 0; i < b.length; i++) {
    if (t >= b.start(i) - 0.1 && t <= b.end(i) + 0.1) return true;
  }
  return false;
}

// 在字节流里找 'moof'（分片边界；box 从标记前 4 字节的长度字段开始）
function pvFindMoof(u8) {
  for (let i = 4; i + 4 <= u8.length; i++) {
    if (u8[i] === 0x6D && u8[i + 1] === 0x6F && u8[i + 2] === 0x6F && u8[i + 3] === 0x66) return i;
  }
  return -1;
}

async function pvSeekFill() {
  const el = $('pmVideo');
  const ms = pv.ms, sb = pv.vSb;
  if (!ms || ms.readyState === 'closed' || !sb || sb.updating || pv.seeking) return;
  const t = el.currentTime;
  if (pvCovered(sb, t)) return;
  const page = pm.pages.find(x => x.cid === pv.cid);
  const dur = (isFinite(ms.duration) && ms.duration) || page?.duration || 0;
  if (!pv.vSize || !dur || !pv.vUrl) return;
  pv.seeking = true;
  try {
    const CH = 3 * 1024 * 1024;
    let off = Math.floor(Math.min(Math.max(t / dur, 0), 1) * pv.vSize);
    off = Math.max(0, off - 512 * 1024);   // 往前退一点：宁可多拿一段也别落在目标之后
    for (let i = 0; i < 10 && pv.ms === ms; i++) {
      if (pvCovered(sb, el.currentTime)) break;
      const res = await pvFetch(pv.vUrl, off, Math.min(off + CH, pv.vSize) - 1);
      if (!res.ok && res.status !== 206) break;
      const u8 = new Uint8Array(await res.arrayBuffer());
      if (pv.ms !== ms) return;
      const m = pvFindMoof(u8);
      if (m < 0) break;
      try {
        await pvAppend(sb, u8.slice(m - 4).buffer);
      } catch {
        pmPlay(pv.cid, page?.page || 1);   // ended 状态下追加失败 → 干脆重开这一P（有预取，秒开）
        return;
      }
      off = Math.min(off + CH, pv.vSize);
      if (off >= pv.vSize) break;
    }
  } finally { pv.seeking = false; }
}

// 拖到没有数据的位置（只可能是配额清理过的区间）就现拉补上
$('pmVideo').addEventListener('seeking', () => { pvSeekFill(); });

// 只掐断填充、关掉 MediaSource，**不动 token**（token 由调用方自己作废，
// 否则 pmPlay 刚取的 my 会被自己这一步作废掉，等于什么都没干）
function pvStopBuffers() {
  const el = $('pmVideo');
  try { el.pause(); } catch { }
  try { if (pv.ms && pv.ms.readyState === 'open') pv.ms.endOfStream(); } catch { }
  pv.ms = null; pv.vSb = null; pv.aSb = null;
  pv.vUrl = ''; pv.vSize = 0; pv.seeking = false;
}

async function submitPages() {
  const cids = [...pm.selected].map(Number);
  if (!cids.length) return;
  const ahead = (state.jobs || []).filter(j => j.state === 'queued' || j.state === 'running').length;
  try {
    const r = await api('/fav/download', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        mediaId: fav.folder ? fav.folder.id : 0,
        folderTitle: fav.folder ? fav.folder.title : null,
        pages: [{ avid: pm.avid, cids }],
      }),
    });
    if (fav.folder) fav.pendingRefresh = { jobId: r.jobId, mediaId: fav.folder.id };
    closePagesModal();
    toast(ahead ? `已加入队列（第 ${ahead + 1} 位）：${cids.length} 个分P` : `已开始下载：${cids.length} 个分P`);
  } catch (e) {
    alert('启动失败：' + e.message);
  }
}

$('btnPmCancel').addEventListener('click', closePagesModal);
$('btnPmGo').addEventListener('click', submitPages);
$('btnPmAll').addEventListener('click', () => {
  pm.pages.filter(p => !p.inLib).forEach(p => pm.selected.add(String(p.cid)));
  renderPmList();
});
$('pagesModal').addEventListener('click', (e) => { if (e.target.id === 'pagesModal') closePagesModal(); });
$('pmList').addEventListener('change', (e) => {
  const cb = e.target.closest('input[type=checkbox][data-cid]');
  if (!cb) return;
  if (cb.checked) pm.selected.add(cb.dataset.cid); else pm.selected.delete(cb.dataset.cid);
  renderPmToolbar();
});

// 行首「勾选框 + P号」整块是勾选热区（小方框太容易误触，扩大命中范围）；其余区域才是预览
$('pmList').addEventListener('click', (e) => {
  const row = e.target.closest('.pm-row');
  if (!row) return;
  const cb = row.querySelector('input[type=checkbox][data-cid]');
  if (e.target.closest('.pmsel')) {
    if (!cb || cb.disabled) return;                 // 已在库：整块都不响应
    if (e.target === cb) return;                    // 点在方框本身：交给 change 处理，别切两次
    cb.checked = !cb.checked;
    cb.dispatchEvent(new Event('change', { bubbles: true }));
    return;
  }
  pmPlay(Number(row.dataset.cid), Number(row.dataset.page));
});

// 换画质：用当前这一P重开一次（info 已缓存，不再打 playurl）
$('pmQuality').addEventListener('change', () => {
  if (!pm.playing) return;
  const p = pm.pages.find(x => x.cid === pm.playing);
  pmPlay(pm.playing, p ? p.page : 1);
});

// 预览声音开关：默认关。开着播放就带音频；关着播放中途开 → 从当前位置接上声音（不打断画面）
$('btnPmAudio').addEventListener('click', async () => {
  localStorage.setItem(PM_AUDIO_KEY, pmAudioOn() ? '0' : '1');
  pmAudioBtn();
  if (pmAudioOn()) await pmEnableAudio();
  else pmDisableAudio();
});

// 中途开声音：不打断画面，从当前播放位置接上。
// 音频是固定码率，"时间比例 → 字节偏移"对音频是准的；起点再对齐到分片边界，误差最多一两秒。
async function pmEnableAudio() {
  const el = $('pmVideo'), ms = pv.ms;
  if (!ms || ms.readyState === 'closed' || pv.aSb) return;   // 没在播：下次播放自动生效
  const info = pv.info;
  const a = info ? (info.audios || []).slice().sort((x, y) => x.size - y.size)[0] : null;
  if (!a) { $('pmPvStat').textContent = '这一P没有音频流'; return; }
  const my = pv.token;
  const aUrl = `/api/v1/bili/preview/stream?avid=${pv.avid}&cid=${pv.cid}&track=audio&id=${a.id}`;
  try {
    a.size ||= await pvLength(aUrl);
    if (my !== pv.token) return;
    const sb = ms.addSourceBuffer(`audio/mp4; codecs="${a.codecs}"`);
    pv.aSb = sb;
    const dur = (isFinite(ms.duration) && ms.duration) || 0;
    let off = dur ? Math.max(0, Math.floor(Math.min(el.currentTime / dur, 1) * a.size) - 256 * 1024) : 0;
    const CH = 1024 * 1024;
    for (let i = 0; i < 8; i++) {
      if (my !== pv.token || pv.aSb !== sb) return;
      if (pvCovered(sb, el.currentTime)) break;
      const res = await pvFetch(aUrl, off, Math.min(off + CH, a.size) - 1);
      if (!res.ok && res.status !== 206) break;
      const u8 = new Uint8Array(await res.arrayBuffer());
      if (my !== pv.token || pv.aSb !== sb) return;
      const m = pvFindMoof(u8);
      if (m < 0) break;
      await pvAppend(sb, u8.slice(m - 4).buffer);
      off = Math.min(off + CH, a.size);
      if (off >= a.size) break;
    }
    // 接上了就把这一P的音频在后台整段续完（下次拖回去也有声）
    if (my === pv.token && pv.aSb === sb && a.size) {
      pvPump(sb, aUrl, a.size, off, my).catch(() => { });
    }
    if (my === pv.token && pv.aSb === sb) $('pmPvStat').textContent = '声音已接上';
  } catch (e) {
    if (pv.aSb) {
      try { pv.aSb.abort(); } catch { }
      try { ms.removeSourceBuffer(pv.aSb); } catch { }
      pv.aSb = null;
    }
    if (my === pv.token) $('pmPvStat').textContent = '声音接不上（这一P重开一次就有了）';
  }
}

function pmDisableAudio() {
  const ms = pv.ms, sb = pv.aSb;
  if (!ms || !sb || ms.readyState === 'closed') { pv.aSb = null; return; }
  try { sb.abort(); } catch { }
  try { ms.removeSourceBuffer(sb); } catch { }
  pv.aSb = null;
}

// ==================== 任务浮窗（跨页面多任务） ====================

const KIND_LABEL = { scan: '扫描手机', probe: '探测可下载性', sync: '同步', fav: '收藏夹下载',
  'library-scan': '扫描总库', cold: '冷备份', coldrestore: '从冷备份恢复' };
const PHASE_LABEL = { copy: '手机拷贝', download: '替代下载', upgrade: '升档下载',
  plan: '账实核对', pack: '打包', read: '读清单', restore: '恢复' };
const JOB_STATE = {
  running: ['进行中', 'running'],
  queued: ['排队中', 'queued'],
  done: ['已完成', 'done'],
  failed: ['失败', 'failed'],
  cancelled: ['已取消', 'cancelled'],
};

let finishedTimer = null;
// 最后一次结束的作业：队列清空后仍留着，供 pill / 「查看日志」回看
let finishedJob = null;

// 日志的展开态和滚动位置必须跨「整表重建」保留 —— SSE 每推一次进度就重铺 taskList，
// 不记住的话用户一展开就被收起（每下完一个条目都会发生），长日志还会跳回顶部。
const logOpen = new Set();          // job id：用户手动展开着
const logScroll = new Map();        // job id → { top, atBottom }

function bindLogs(scope) {
  scope.querySelectorAll('details.task-log').forEach(d => {
    const id = d.dataset.log;
    const pre = d.querySelector('pre');
    if (d.open && pre) {
      // 放到下一帧：浮窗刚从 hidden 显示出来时元素还没布局，scrollHeight 会是 0，scrollTop 设不上
      requestAnimationFrame(() => {
        // 原来贴着底就继续贴着底（日志在长），用户上翻过就停在他看的位置
        const keep = logScroll.get(id);
        if (!keep || keep.atBottom) pre.scrollTop = pre.scrollHeight;
        else pre.scrollTop = keep.top;
      });
    }
    d.addEventListener('toggle', () => {
      if (d.open) logOpen.add(id); else logOpen.delete(id);
      syncDockHeight();
    });
    if (pre) pre.addEventListener('scroll', () => {
      logScroll.set(id, {
        top: pre.scrollTop,
        atBottom: pre.scrollHeight - pre.scrollTop - pre.clientHeight < 8,
      });
    });
  });
}

function jobLabel(j) {
  return j.label || KIND_LABEL[j.kind] || j.kind || '任务';
}

function taskCardHtml(j, isFinished) {
  const label = jobLabel(j);
  const [stTxt, stCls] = JOB_STATE[j.state] || [j.state, ''];
  // 主条：有字节分母就用字节口径。只选 1 个P时条目口径是 0/1 → 全程 0%、下完才跳 100%，看着像卡住。
  const byPct = j.bytesTotal > 0 ? (j.bytesDone || 0) / j.bytesTotal * 100 : null;
  const pct = Math.max(0, Math.min(100, Math.round(byPct ?? (j.progress || 0) * 100)));
  const eta = (j.speedBps > 0 && j.bytesTotal > j.bytesDone)
    ? fmtTime((j.bytesTotal - j.bytesDone) / j.speedBps) : '—';

  const head = `<div class="task-top">
      <span class="tlabel">${escapeHtml(label)}</span>
      <span class="tstate ${stCls}">${stTxt}${j.state === 'queued' && j.queuePos ? ' 第 ' + j.queuePos + ' 位' : ''}</span>
      <span class="mono muted">${j.total ? j.done + ' / ' + j.total : ''}</span>
      <div class="spacer"></div>
      ${isFinished ? '' : `<button class="btn tiny" data-cancel="${j.id}">取消</button>`}
    </div>`;

  const bar = `<div class="bar"><div class="fill" style="width:${pct}%"></div></div>`;

  // 拿不到字节分母（CDN 不给 Content-Length / 扫描作业）时也要给信息：
  // 已下多少 + 速度都报，只是不编分母、不给 ETA——"不知道占多少"不等于"什么都不显示"
  const totalLine = j.bytesTotal > 0
    ? `总进度：${fmtBytes(j.bytesDone)} / ${fmtBytes(j.bytesTotal)} · ${fmtSpeed(j.speedBps, j.speedUnit)} · 预估剩余时间：${eta}`
    : [j.message || '', j.bytesDone > 0 ? `已传输 ${fmtBytes(j.bytesDone)}` : '',
       j.speedBps > 0 ? fmtSpeed(j.speedBps, j.speedUnit) : ''].filter(Boolean).join(' · ');

  // 当前条目：标签行 = 「作业名：动作」，用于区分 同步 / 同步：替代下载
  const tag = j.phase ? `${label}：${PHASE_LABEL[j.phase] || j.phase}` : label;
  // 下载模式 3 条并发：下面这条进度只属于「最近开始的那一条」，不是三条之和（分母不同会算出超量）
  const multi = (j.activeItems || 0) > 1 ? ` <span class="muted">（${j.activeItems} 条并发中，显示最近一条）</span>` : '';
  let cur = '';
  if (j.currentKey && (j.currentBytesTotal > 0 || j.phase)) {
    const cpct = j.currentBytesTotal > 0
      ? Math.min(100, Math.round((j.currentBytesDone || 0) / j.currentBytesTotal * 100)) : 0;
    cur = `<div class="task-cur"><b>${escapeHtml(tag)}</b> · ${escapeHtml(j.currentTitle || j.currentKey)}${multi}</div>`;
    if (j.currentBytesTotal > 0) {
      cur += `<div class="bar tight"><div class="fill soft" style="width:${cpct}%"></div></div>
        <div class="task-line">当前：${fmtBytes(j.currentBytesDone)} / ${fmtBytes(j.currentBytesTotal)} · ${cpct}%</div>`;
    } else if (j.currentBytesDone > 0) {
      // 拿不到真实流长（进度分母未知）时只报已下字节，不要编一个分母出来
      cur += `<div class="task-line">当前：已下 ${fmtBytes(j.currentBytesDone)}</div>`;
    }
  }

  const log = (j.log && j.log.length)
    ? `<details class="task-log"${logOpen.has(j.id) ? ' open' : ''} data-log="${escapeHtml(j.id)}"><summary>日志（${j.log.length}）</summary><pre>${escapeHtml(j.log.join('\n'))}</pre></details>`
    : '';

  return `<div class="task ${j.state === 'running' ? 'running' : ''}">
    ${head}${bar}
    <div class="task-line">${escapeHtml(totalLine)}</div>
    ${cur}${log}
  </div>`;
}

function renderTasks(jobs) {
  const prevCount = state.jobs.length;
  state.jobs = (jobs || []).slice();

  // fav 下载作业结束后刷新收藏夹索引（multiP 标记 / 已同步数由服务端并入快照）
  if (fav.pendingRefresh) {
    const j = state.jobs.find(x => x.id === fav.pendingRefresh.jobId);
    if (!j || (j.state !== 'running' && j.state !== 'queued')) {
      const mid = fav.pendingRefresh.mediaId;
      fav.pendingRefresh = null;
      const wasDone = favFolderDone(fav.idx[mid]);          // 作业前是不是已经算完成，用来决定要不要报喜
      api('/bili/fav/index?mediaId=' + mid + '&items=1').then(s => {
        if (!s || s.mediaId == null) return;
        // 必须走 applyFavIndex：它才会把 items 灌进 fav.snapItems（网格与筛选的数据源）。
        // 只写 fav.idx 的话，作业结束后"充电/已同步"标记会停在旧快照上不刷新。
        applyFavIndex(s);
        if (fav.folder && fav.folder.id === mid) { renderFavStat(); renderFolders(); renderFavGrid(); }
        if (!wasDone && favFolderDone(s)) {
          const name = (fav.folders.find(f => f.id === mid) || {}).title || '收藏夹';
          toast(`「${name}」已全部同步`);                       // 待同步 0（失效/充电不计）= 整夹同步完成
        }
      }).catch(() => { });
    }
  }

  if (!state.jobs.length) {
    if (prevCount > 0) {
      // 刚清空队列：接住最后一次作业的完成态，别让浮窗连同日志一起消失
      api('/jobs/latest').then(j => {
        if (!state.jobs.length && j && j.state !== 'running' && j.state !== 'queued') showFinished(j);
      }).catch(() => { });
    }
    // 其余情况什么都别做：收起交给 showFinished 的定时器。
    // 这里若自动最小化，用户点开 pill 回看时会被下一次空列表推送立刻收走。
    return;
  }

  finishedJob = null;                // 有新作业了，旧的完成态让位
  clearTimeout(finishedTimer);
  finishedTimer = null;

  const running = state.jobs.filter(j => j.state === 'running');
  const queued = state.jobs.filter(j => j.state === 'queued');
  $('taskDockCount').textContent = running.length + queued.length > 0
    ? `运行 ${running.length} · 排队 ${queued.length}` : '';

  $('taskList').innerHTML = state.jobs.map(j => taskCardHtml(j, false)).join('');
  bindLogs($('taskList'));
  $('taskList').querySelectorAll('button[data-cancel]').forEach(b => {
    b.onclick = async () => {
      try { await api('/cancel?id=' + b.dataset.cancel, { method: 'POST' }); }
      catch (e) { alert('取消失败：' + e.message); }
    };
  });

  showDock();
  updateDockSummary(state.jobs);

  // 同步作业运行期间轮询列表，让"排队中 / 下载中"徽标跟着走
  setListPoll(running.some(j => j.kind === 'sync'));
}

function showFinished(j) {
  finishedJob = j;
  $('taskDockCount').textContent = '已结束';
  $('taskList').innerHTML = taskCardHtml(j, true);
  bindLogs($('taskList'));
  showDock();
  updateDockSummary([j]);
  clearTimeout(finishedTimer);
  // 先亮 8 秒看结果，然后收成一行 pill —— 不是消失，点一下就能回来翻日志
  finishedTimer = setTimeout(() => {
    finishedTimer = null;
    if (!state.jobs.length) minimizeDock(true);
  }, 8000);
}

function showDock() {
  $('taskdock').classList.remove('hidden');
  $('taskPill').classList.add('hidden');
  document.body.classList.add('dock-open');
  applyDockCollapse();     // 连同高度一起重算（收起态只有一行）
}

// ---- 收起态：一行「任务：+ 总进度条 + 展开」----
let dockCollapsed = false;
try { dockCollapsed = localStorage.getItem('favsync.dockCollapsed') === '1'; } catch { }

// 收起态总览条统一用 **「已处理条目 ÷ 总条目」** 这把尺子，和展开后的作业条同源。
// 旧版按字节加权，分母用 job.bytesTotal —— 但扫描作业的字节总量要扫完才知道，
// 服务端只能让 BytesTotal 保持 0（=未知），前端拿已传字节自己当分母，分子分母同数 → 恒 100%。
// 字节从此只负责算 ETA（"还要多久"），不再参与百分比。
function overallProgress(jobs) {
  const list = (jobs || []).filter(j => j && j.state !== 'cancelled');
  if (!list.length) return { pct: 0, eta: null };

  // 只统计还没收工的作业：已完成的老作业若参与累加，会把刚开始的新作业稀释成 98%。
  const live = list.filter(j => j.state === 'running' || j.state === 'queued');
  const use = live.length ? live : list;

  let done = 0, total = 0;
  for (const j of use) {
    const t = j.total || 0;
    if (t <= 0) continue;                       // 条目总数还没算出来（刚入队的作业）→ 不参与
    total += t;
    done += Math.max(0, Math.min(j.done || 0, t));
  }
  const pct = total > 0
    ? Math.max(0, Math.min(100, Math.round(done / total * 100)))
    : Math.max(0, Math.min(100, Math.round(
        use.reduce((a, j) => a + (j.progress || 0), 0) / use.length * 100)));

  // ETA 仍用字节：剩余字节 ÷ 当前速度（只有报字节总量的作业算得出来；扫描不报 → 不显示）
  let rest = 0, speed = 0;
  for (const j of use) {
    if (j.state === 'done' || j.bytesTotal <= 0 || j.speedBps <= 0) continue;
    rest += Math.max(0, j.bytesTotal - (j.bytesDone || 0));
    speed += j.speedBps;
  }
  const eta = (rest > 0 && speed > 0) ? fmtTime(rest / speed) : null;
  return { pct, eta };
}

function updateDockSummary(jobs) {
  const { pct, eta } = overallProgress(jobs || state.jobs);
  $('dockcFill').style.width = pct + '%';
  $('dockcPct').textContent = eta ? `${pct}% · 剩 ${eta}` : `${pct}%`;
}

function syncDockHeight() {
  if ($('taskdock').classList.contains('hidden')) {
    document.documentElement.style.setProperty('--dock-h', '0px');
    return;
  }
  document.documentElement.style.setProperty('--dock-h', $('taskdock').offsetHeight + 'px');
}

function applyDockCollapse() {
  $('taskdock').classList.toggle('collapsed', dockCollapsed);
  requestAnimationFrame(syncDockHeight);
}

function setDockCollapsed(on) {
  dockCollapsed = !!on;
  try { localStorage.setItem('favsync.dockCollapsed', dockCollapsed ? '1' : '0'); } catch { }
  applyDockCollapse();
}

function hideDock() {
  $('taskdock').classList.add('hidden');
  $('taskPill').classList.add('hidden');
  document.body.classList.remove('dock-open');
  document.documentElement.style.setProperty('--dock-h', '0px');
  state.busy = false;
  updateSyncButton();
  renderFavToolbar();
}

function minimizeDock(auto) {
  const running = state.jobs.filter(j => j.state === 'running');
  // 队列空了也要留住最后一次作业：pill 就是事后回看日志的入口
  const j = running[0] || state.jobs[0] || finishedJob;
  if (!j) { hideDock(); return; }
  const queued = state.jobs.filter(x => x.state === 'queued').length;
  const pct = Math.round((j.progress || 0) * 100);
  const [stTxt] = JOB_STATE[j.state] || [''];
  $('taskPill').textContent = j.state === 'running'
    ? `${jobLabel(j)} ${pct}%${queued ? ' · 排队 ' + queued : ''}`
    : `${jobLabel(j)} ${stTxt || '已结束'}${j.log && j.log.length ? ' · 查看日志' : ''}`;
  $('taskdock').classList.add('hidden');
  $('taskPill').classList.remove('hidden');
  document.body.classList.remove('dock-open');
  document.documentElement.style.setProperty('--dock-h', '0px');
}

// 同步作业运行期间每 2s 刷新列表（服务端会给运行中作业的条目附 dlStatus）
let listPollTimer = null;
function setListPoll(on) {
  if (on && !listPollTimer) {
    listPollTimer = setInterval(() => {
      if (state.page === 'sync') loadItems().catch(() => { });
    }, 2000);
  } else if (!on && listPollTimer) {
    clearInterval(listPollTimer);
    listPollTimer = null;
  }
}

// 任务状态影响按钮可用性
function applyBusy() {
  const running = state.jobs.some(j => j.state === 'running');
  state.busy = running;
  $('btnScan').disabled = running;
  $('btnProbe').disabled = running;
  updateSyncButton();
  renderFavToolbar();
  if (pm.open) renderPmToolbar();   // 按钮文案要在"下载所选 P / 加入队列"之间跟着作业状态切
  if (!running && state.page === 'sync') { /* 结束后刷新由 SSE 空列表触发 */ }
}

function connectEvents() {
  const es = new EventSource('/api/v1/events');
  es.onmessage = (ev) => {
    try {
      const payload = JSON.parse(ev.data);
      renderTasks(payload.jobs || []);
      applyBusy();
      if (!(payload.jobs || []).length) { loadStatus(); if (state.page === 'sync') loadItems(); }
    } catch { }
  };
}

// ==================== 设置 ====================
// 设计前提：**应用先能打开，再谈配置**。库地址不可用时服务会降级启动并给出 archiveNotice，
// 页面顶部弹黄条把用户引到这里来改；而不是让程序直接起不来、只剩一句"无法访问"。

let cfgCurrent = null;   // 上次读到的配置（判断 canRestart / 降级说明用）

function renderBootBanner(notice) {
  const el = $('bootBanner');
  if (!notice) { el.classList.add('hidden'); el.innerHTML = ''; return; }
  el.classList.remove('hidden');
  el.innerHTML = `<span class="bb-ico">!</span><span class="bb-text">${escapeHtml(notice)}</span>
                  <a href="#/settings" class="bb-link">去设置</a>`;
}

async function loadSettings() {
  try {
    const c = await api('/config');
    cfgCurrent = c;
    $('cfgArchive').value = c.archiveRoot || '';
    $('cfgPhoneRoot').value = c.phoneRoot || '';
    $('cfgAdb').value = c.adbPath || '';
    $('cfgPort').value = c.port || 8720;
    fillDeviceSelect(c.device || '');

    // 高级项：留空即自动探测，所以把"实际用上了哪个"显示出来，否则空框等于没信息
    $('cfgFfmpeg').value = c.ffmpegPath || '';
    $('cfgPlayer').value = c.playerPath || '';
    $('cfgBiliProxy').checked = !!c.biliUseProxy;
    $('cfgFfmpegNote').textContent = c.ffmpegResolved
      ? `ffmpeg：${c.ffmpegResolved}`
      : '没找到 ffmpeg：面板内双流 remux 会失败（装好后加入 PATH，或在上面的框里指定，也可以设 FAVSYNC_FFMPEG）';
    $('cfgPlayerNote').textContent = c.playerResolved
      ? `播放器：${c.playerResolved}`
      : '没找到常见播放器（PotPlayer / mpv / VLC）：「用外部播放器打开」会交给系统默认程序';

    // 冷备份配置（与总库分开：它允许移动盘/网络盘，所以不共用 drives）
    $('cfgColdRoot').value = c.coldRoot || '';
    $('cfgColdLabel').value = c.coldLabel || 'cold';
    $('cfgColdBucket').value = c.coldBucketMiB || 2048;
    $('cfgColdOrigin').value = c.coldOrigin || 'M';
    $('cfgSevenZip').value = c.sevenZipPath || '';
    $('cfg7zNote').textContent = c.sevenZipResolved
      ? `7z：${c.sevenZipResolved}`
      : '没找到 7z：发布包里的 tools\\7z\\7za.exe 缺失时请重新完整解压，或在上面指定路径';

    $('coldDrives').innerHTML = (c.allDrives || []).map(d => {
      const name = (d.root || '').replace(/\\$/, '');
      const label = d.label ? `${name} · ${d.label}` : name;
      return `<button type="button" class="drive-chip" data-root="${escapeHtml(d.root)}">
                <b>${escapeHtml(label)}${d.removable ? ' · 移动/网络' : ''}</b>
                <span class="mono">剩 ${d.freeGb} / 共 ${d.totalGb} GB</span>
              </button>`;
    }).join('');

    $('cfgArchiveNote').textContent = c.archiveFallback
      ? `当前实际使用 ${c.effectiveArchiveRoot}（配置里的 ${c.archiveRoot} 这次不可用，已临时降级）`
      : `当前实际使用 ${c.effectiveArchiveRoot}`;

    $('cfgAdbNote').textContent = c.adbResolved
      ? `实际使用 ${c.adbResolved}`
      : '没找到 adb：扫描手机与同步都会不可用（发布包里的 tools\\adb\\adb.exe 缺失时请重新完整解压）';

    // 磁盘列表：点一下把「盘符\FavSyncLibrary」填进输入框，省得手打
    $('cfgDrives').innerHTML = (c.drives || []).map(d => {
      const name = (d.root || '').replace(/\\$/, '');
      const label = d.label ? `${name} · ${d.label}` : name;
      return `<button type="button" class="drive-chip" data-root="${escapeHtml(d.root)}">
                <b>${escapeHtml(label)}</b>
                <span class="mono">剩 ${d.freeGb} / 共 ${d.totalGb} GB</span>
              </button>`;
    }).join('');

    $('cfgChecks').innerHTML = (c.checks || []).map(k =>
      `<div class="set-check ${k.ok ? 'ok' : 'bad'}">
         <span class="sc-dot"></span><b>${escapeHtml(k.name)}</b>
         <span class="mono muted">${escapeHtml(k.detail)}</span>
       </div>`).join('');

    $('cfgPath').textContent = `配置文件：${c.configPath}`;

    $('btnCfgRestart').disabled = !c.canRestart;
    $('btnCfgRestart').title = c.canRestart
      ? '改过总库目录或端口后需要重启服务'
      : '当前以命令行方式运行（mfav.exe），无法自重启：关掉窗口重新启动即可';
  } catch (e) {
    setCfgMsg('读取配置失败：' + e.message, 'bad');
  }
}

// 设备下拉：留空 = 自动取第一台在线设备；只有接了两台（USB + 无线）时才需要指定。
// 配置里写的那台离线时也要保留成选项，否则一打开设置页就被"自动"顶掉，用户会以为配置丢了。
async function fillDeviceSelect(current) {
  const sel = $('cfgDevice');
  const auto = '<option value="">自动（第一台在线设备）</option>';
  sel.innerHTML = auto;
  try {
    const d = await api('/devices');
    const list = d.devices || [];
    const opts = [auto];
    for (const x of list) {
      const label = x.serial + (x.model ? ` · ${x.model}` : '') + (x.online ? '' : '（离线）');
      opts.push(`<option value="${escapeHtml(x.serial)}">${escapeHtml(label)}</option>`);
    }
    if (current && !list.some(x => x.serial === current))
      opts.splice(1, 0,
        `<option value="${escapeHtml(current)}">${escapeHtml(current)}（配置里指定 · 当前不在线）</option>`);
    sel.innerHTML = opts.join('');
    sel.value = current;
    const online = list.filter(x => x.online).length;
    $('cfgDeviceNote').textContent = list.length
      ? `在线 ${online} 台 / 共 ${list.length} 台`
      : '当前没有设备：USB 连好后刷新，或到「手机连接」用无线 adb 连';
  } catch (e) {
    sel.innerHTML = auto;
    sel.value = current;
    $('cfgDeviceNote').textContent = '读取设备列表失败：' + e.message;
  }
}

function setCfgMsg(text, kind) {
  const el = $('cfgMsg');
  el.textContent = text || '';
  el.className = 'set-msg' + (kind ? ' ' + kind : '');
}

$('cfgDrives').addEventListener('click', (e) => {
  const chip = e.target.closest('.drive-chip');
  if (!chip) return;
  const root = chip.dataset.root || '';
  const sep = root.endsWith('\\') ? '' : '\\';
  $('cfgArchive').value = root + sep + 'FavSyncLibrary';
  $('cfgArchive').focus();
});

// 高级：默认折叠。低频选项堆在主流程里会让人以为"这些都得填"
function toggleAdv(force) {
  const body = $('advBody');
  const open = force ?? body.classList.contains('hidden');
  body.classList.toggle('hidden', !open);
  $('advToggle').classList.toggle('open', open);
  $('advToggle').setAttribute('aria-expanded', String(open));
  $('advMark').textContent = open ? '收起' : '展开';
}
$('advToggle').addEventListener('click', () => toggleAdv());
$('advToggle').addEventListener('keydown', e => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleAdv(); }
});

$('btnCfgSave').addEventListener('click', async () => {
  const port = Number($('cfgPort').value);
  const body = {
    archiveRoot: $('cfgArchive').value.trim(),
    phoneRoot: $('cfgPhoneRoot').value.trim(),
    adbPath: $('cfgAdb').value.trim(),
    device: $('cfgDevice').value.trim(),
    port,
    // 冷备份：不触发重启，下一次打包/恢复即时生效
    coldRoot: $('cfgColdRoot').value.trim(),
    coldLabel: $('cfgColdLabel').value.trim(),
    coldBucketMiB: Number($('cfgColdBucket').value) || 2048,
    coldOrigin: $('cfgColdOrigin').value.trim(),
    sevenZipPath: $('cfgSevenZip').value.trim(),
    ffmpegPath: $('cfgFfmpeg').value.trim(),
    playerPath: $('cfgPlayer').value.trim(),
    biliUseProxy: $('cfgBiliProxy').checked,
  };
  if (!body.archiveRoot) { setCfgMsg('总库目录不能为空', 'bad'); return; }
  if (!port || port < 1024 || port > 65535) { setCfgMsg('端口要在 1024–65535 之间', 'bad'); return; }

  const btn = $('btnCfgSave');
  btn.disabled = true;
  try {
    const r = await api('/config', { method: 'POST', body: JSON.stringify(body) });
    if (r.restartRequired) {
      $('btnCfgRestart').disabled = !(cfgCurrent && cfgCurrent.canRestart);
      setCfgMsg('已保存。总库目录/端口要重启服务才生效 —— 点右边「重启服务」。', 'warn');
    } else {
      // BiliClient 是单例，HttpClientHandler 在构造时就绑定了 => 代理开关只能重启生效
      const proxyChanged = !!cfgCurrent && !!cfgCurrent.biliUseProxy !== $('cfgBiliProxy').checked;
      setCfgMsg(proxyChanged
        ? '已保存。B 站代理开关要重启服务才生效，其余项即时生效。'
        : '已保存（设备、ffmpeg/播放器路径、缓存目录下一次作业即时生效）。', proxyChanged ? 'warn' : 'ok');
      await loadSettings();      // 重读一次，让「实际使用」的探测结果跟上
      loadStatus();
    }
  } catch (e) {
    setCfgMsg('保存失败：' + e.message, 'bad');
  } finally {
    btn.disabled = false;
  }
});

$('btnCfgRestart').addEventListener('click', async () => {
  const btn = $('btnCfgRestart');
  btn.disabled = true;
  setCfgMsg('正在重启服务…', 'warn');

  try {
    const r = await api('/config/restart', { method: 'POST' });
    if (!r.restarting) {
      setCfgMsg(r.error || '无法自重启', 'bad');
      btn.disabled = false;
      return;
    }

    const port = r.port || location.port;
    // 重启期间旧连接会断，fetch 报错是正常的：轮询到新端口起来为止
    for (let i = 0; i < 30; i++) {
      await new Promise(res => setTimeout(res, 700));
      try {
        const res = await fetch(`http://127.0.0.1:${port}/api/v1/status`, { cache: 'no-store' });
        if (res.ok) { location.href = `http://127.0.0.1:${port}/#/settings`; return; }
      } catch { }
    }

    setCfgMsg('重启后没等到服务（多半是端口被占用）。看托盘图标的提示，或打开托盘菜单里的「打开日志」。', 'bad');
    btn.disabled = false;
  } catch (e) {
    setCfgMsg('重启请求失败：' + e.message, 'bad');
    btn.disabled = false;
  }
});

// 顶栏那个库地址：点一下进设置页（以前是 prompt 弹框，太简陋也没法提示磁盘空间）
$('archivePath').addEventListener('click', () => { location.hash = '#/settings'; });

// ==================== 事件绑定 ====================

// 冲突的子筛选：这些值都由「冲突」这一个主标签承载，点开后才出现第二排
const CONFLICT_FILTERS = ['conflict', 'conflict-upgrade', 'conflict-codec', 'conflict-content', 'mismatch', 'incomplete'];
const CONFLICT_MAIN = { mismatch: 'conflict', incomplete: 'conflict' };

function syncTabs() {
  const mainKey = state.filter.startsWith('conflict') ? 'conflict' : (CONFLICT_MAIN[state.filter] || state.filter);
  $('tabs').querySelectorAll('button').forEach(b => b.classList.toggle('active', b.dataset.filter === mainKey));
  const showSub = CONFLICT_FILTERS.includes(state.filter);
  $('conflictTabs').classList.toggle('hidden', !showSub);
  if (showSub)
    $('conflictTabs').querySelectorAll('button').forEach(b => b.classList.toggle('active', b.dataset.filter === state.filter));
}

$('tabs').addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-filter]');
  if (!btn) return;
  state.filter = btn.dataset.filter;
  state.offset = 0;
  syncTabs();
  loadItems();
});

$('conflictTabs').addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-filter]');
  if (!btn) return;
  state.filter = btn.dataset.filter;
  state.offset = 0;
  syncTabs();
  loadItems();
});

// 脚注里的「仅存库中 N」「已丢失 N」也能点：等价于点对应的筛选标签
$('cardsFoot').addEventListener('click', (e) => {
  const a = e.target.closest('a[data-filter]');
  if (!a) return;
  e.preventDefault();
  $('tabs').querySelector(`button[data-filter="${a.dataset.filter}"]`)?.click();
});

let searchTimer = null;
$('search').addEventListener('input', (e) => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => {
    state.q = e.target.value.trim();
    state.offset = 0;
    loadItems();
  }, 250);
});

// 表头复选框 = 本页全选（点之前先看它当前是不是"本页都勾了"，别信浏览器翻页后残留的 checked）
$('checkAll').addEventListener('change', () => setPageSelection(!isPageFullySelected()));

$('btnScan').addEventListener('click', async () => {
  try { await api('/scan', { method: 'POST' }); }
  catch (err) { alert('扫描失败：' + err.message); }
});

$('btnProbe').addEventListener('click', async () => {
  try { await api('/probe', { method: 'POST' }); }
  catch (err) {
    alert('探测失败：' + err.message);
    if (err.message.includes('登录')) openLogin();
  }
});

$('btnSync').addEventListener('click', async () => {
  const keys = [...state.selected];
  if (!keys.length) return;
  if (!confirm(`确认同步 ${keys.length} 个条目到总库？`)) return;
  try {
    await api('/sync', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ keys, mode: $('syncMode').value }),
    });
    state.selected.clear();
    renderRows();
  } catch (err) {
    // 没有在线设备 → 直接打开连接弹窗，别让用户自己猜命令行
    if (err.message.includes('没有在线设备')) { toast('没有在线设备，先连手机'); openPhoneModal(); return; }
    alert('同步失败：' + err.message);
  }
});

// ---- 库页交互 ----
$('libTabs').addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-source]');
  if (!btn) return;
  lib.source = btn.dataset.source;
  renderLibTabs();
  loadLibrary(true);
});

let libSearchTimer = null;
$('libSearch').addEventListener('input', (e) => {
  clearTimeout(libSearchTimer);
  libSearchTimer = setTimeout(() => {
    lib.q = e.target.value.trim();
    loadLibrary(true);
  }, 280);
});

// 双击卡片 → 页内播放器
$('libGrid').addEventListener('dblclick', (e) => {
  const card = e.target.closest('.vcard[data-key]');
  if (card) openPlayer(card.dataset.key);
});

// 卡片上的「📁 文件夹」：在资源管理器里打开该条目在总库里的目录，方便本地查看文件
$('libGrid').addEventListener('click', async (e) => {
  const btn = e.target.closest('button[data-folder]');
  if (!btn) return;
  e.stopPropagation();
  const key = btn.dataset.folder;
  btn.disabled = true;
  const old = btn.textContent;
  btn.textContent = '打开中…';
  try {
    const r = await api('/open-folder', { method: 'POST', body: JSON.stringify({ key }) });
    toast('已打开：' + r.path);
  } catch (err) {
    toast('打不开：' + (err.message || err));
  } finally {
    btn.textContent = old;
    btn.disabled = false;
  }
});

// 卡片上的「删除」：整条目（所有档位）搬进 .trash 并从库里移除。
// 不真删字节——误删了还能从回收站目录里捞回来，确认无误再点「清空回收站」。
$('libGrid').addEventListener('click', async (e) => {
  const btn = e.target.closest('button[data-del]');
  if (!btn) return;
  e.stopPropagation();
  const key = btn.dataset.del;
  const name = btn.dataset.title || key;
  if (!confirm(`删除《${name}》？\n\n这个条目的所有档位都会搬进回收站，库中不再显示。\n想彻底释放空间，去「回收站」清空。`)) return;

  btn.disabled = true;
  btn.textContent = '删除中…';
  try {
    await api('/trash', { method: 'POST', body: JSON.stringify({ key }) });
    toast('已删除：《' + name + '》（已移入回收站）');
    await loadLibrary(true);      // 列表与计数一起刷新
    refreshTrashBadge();
  } catch (err) {
    toast('删除失败：' + (err.message || err));
    btn.textContent = '删除';
    btn.disabled = false;
  }
});

/* ---------- 回收站 ---------- */

const trash = { rows: [], total: 0, bytes: 0, root: '' };

async function refreshTrashBadge() {
  try {
    const r = await api('/trash');
    trash.rows = r.rows || [];
    trash.total = r.total || 0;
    trash.bytes = r.bytes || 0;
    trash.root = r.root || '';
    const b = $('btnTrash');
    if (b) b.textContent = trash.total ? `回收站（${trash.total}）` : '回收站';
  } catch { /* 徽标失败不影响主流程 */ }
}

async function openTrashModal() {
  $('trashModal').classList.remove('hidden');
  $('trList').innerHTML = '<div class="empty" style="padding:18px">加载中…</div>';
  $('btnTrEmpty').disabled = true;
  try {
    const r = await api('/trash');
    trash.rows = r.rows || [];
    trash.total = r.total || 0;
    trash.bytes = r.bytes || 0;
    trash.root = r.root || '';
    renderTrashList();
  } catch (err) {
    $('trList').innerHTML = `<div class="empty" style="padding:18px">${escapeHtml(err.message || String(err))}</div>`;
  }
}

function closeTrashModal() { $('trashModal').classList.add('hidden'); }

function renderTrashList() {
  $('trMeta').textContent = trash.total
    ? `${trash.total} 条 · ${fmtBytes(trash.bytes)} · ${trash.root}`
    : '回收站是空的';
  $('btnTrEmpty').disabled = trash.total === 0;
  if (!trash.rows.length) {
    $('trList').innerHTML = '<div class="empty" style="padding:18px">还没有删除过条目</div>';
    return;
  }
  $('trList').innerHTML = trash.rows.map(r => `
    <div class="tr-row">
      <span class="pt" title="${escapeHtml(r.title || r.key)}">${escapeHtml(r.title || '(无标题)')}</span>
      <span class="pd">${r.owner ? escapeHtml(r.owner) + ' · ' : ''}${fmtBytes(r.size)}</span>
      <span class="pd muted">${escapeHtml(r.deletedAt || '')}${r.backupCount > 1 ? ` · ${r.backupCount} 份` : ''}</span>
      <span class="mono muted tr-key">${escapeHtml(r.key)}</span>
    </div>`).join('');
}

// ==================== 版本重复（附加版本副本） ====================
// 换主后旧主副本会落到 .versions/，同 key 就多一份字节。这里能看也能单独删掉——
// 删的是"这一份附加版本"，主副本一个字节都不动。
const vs = { rows: [], total: 0, bytes: 0 };

async function refreshVersionBadge() {
  try {
    const r = await api('/versions');
    vs.rows = r.rows || [];
    vs.total = r.total || 0;
    vs.bytes = r.bytes || 0;
    const b = $('btnVersions');
    if (b) b.textContent = vs.total ? `版本重复（${vs.total}）` : '版本重复';
  } catch { /* 徽标失败不影响主流程 */ }
}

async function openVersionsModal() {
  $('versionsModal').classList.remove('hidden');
  $('vsList').innerHTML = '<div class="empty" style="padding:18px">加载中…</div>';
  try {
    const r = await api('/versions');
    vs.rows = r.rows || [];
    vs.total = r.total || 0;
    vs.bytes = r.bytes || 0;
    renderVersionList();
  } catch (err) {
    $('vsList').innerHTML = `<div class="empty" style="padding:18px">${escapeHtml(err.message || String(err))}</div>`;
  }
}

function closeVersionsModal() { $('versionsModal').classList.add('hidden'); }

function renderVersionList() {
  $('vsMeta').textContent = vs.total
    ? `${vs.total} 份附加版本 · 共 ${fmtBytes(vs.bytes)}`
    : '没有附加版本';
  if (!vs.rows.length) {
    $('vsList').innerHTML = '<div class="empty" style="padding:18px">每个条目只有一份主副本，没有重复</div>';
    return;
  }
  $('vsList').innerHTML = vs.rows.map(r => {
    // 与主副本比档位：低/高/同档，帮用户判断这份还值不值得留
    let cmp = '';
    if (r.bestQuality && r.quality) {
      cmp = r.quality < r.bestQuality ? ` · 低于主副本 q${r.bestQuality}`
          : r.quality > r.bestQuality ? ` · 高于主副本 q${r.bestQuality}`
          : ` · 与主副本同档 q${r.quality}`;
    }
    const miss = r.exists === false ? ' · 目录已不在（只有账目）' : '';
    return `<div class="tr-row">
      <span class="pt" title="${escapeHtml(r.title || r.key)}">${escapeHtml(r.title || '(无标题)')}</span>
      <span class="pd">${QUALITY_NAME[r.quality] || ('q' + (r.quality ?? '?'))}${r.codecid ? ' · ' + (CODEC_NAME[r.codecid] || ('c' + r.codecid)) : ''}${cmp}</span>
      <span class="pd muted">${fmtBytes(r.size)} · ${escapeHtml(r.path || '')}${miss}</span>
      <button class="btn danger vs-del" data-key="${escapeHtml(r.key)}"
              data-tn="${escapeHtml(r.tagNorm || r.tag || '')}"
              data-cid="${escapeHtml(r.contentId || '')}">删除这份</button>
    </div>`;
  }).join('');
}

$('vsList').addEventListener('click', async (e) => {
  const btn = e.target.closest('button.vs-del');
  if (!btn) return;
  const key = btn.dataset.key;
  if (!confirm(`删除这份附加版本？\n\n${key}\n\n只删这一份字节，主副本不受影响；文件先搬进库内 .trash。`)) return;
  btn.disabled = true;
  try {
    const r = await api('/versions/delete', {
      method: 'POST',
      body: JSON.stringify({ key, tagNorm: btn.dataset.tn, contentId: btn.dataset.cid }),
    });
    toast(r.message || '已删除');
    await openVersionsModal();
    refreshVersionBadge();
  } catch (err) {
    toast('删除失败：' + (err.message || err));
    btn.disabled = false;
  }
});

// ==================== 扫描总库（把磁盘上已有的条目认领入库） ====================
// 和「扫描手机」完全是两件事：扫手机写的是 phone_*（手机现状），扫库只写 backup 账目、
// 把 state 翻成 synced。用于中继回传、手工搬条目进库、换机迁移，也可当库体检反复跑。
$('btnLibScan')?.addEventListener('click', async () => {
  if (!confirm('扫描总库磁盘？\n\n' +
    '做什么：把已经存在于库目录、但账目还不认识的条目认领为「已同步」\n' +
    '不会做：不联网、不碰手机字段、不删任何文件\n' +
    '可重跑：已入账的条目会自动跳过（幂等）\n\n' +
    '库大的话要一两分钟。继续？')) return;
  const btn = $('btnLibScan');
  const old = btn.textContent;
  btn.disabled = true; btn.textContent = '扫库中…';
  try {
    const r = await api('/library/scan', { method: 'POST' });
    toast('已开始扫描总库');
    pollLibScan(r && r.jobId);
  } catch (err) {
    toast('启动失败：' + (err.message || err));
  } finally {
    btn.disabled = false; btn.textContent = old;
  }
});

async function pollLibScan(jobId) {
  if (!jobId) return;
  for (let i = 0; i < 900; i++) {                 // 最长等 30 分钟
    await new Promise(r => setTimeout(r, 2000));
    let j;
    try { j = await api('/jobs/' + encodeURIComponent(jobId)); } catch { return; }
    if (!j || j.state === 'running' || j.state === 'queued') continue;
    if (j.state === 'done') {
      toast(j.message || '扫库完成');
      loadLibrary(true);
    } else if (j.state === 'failed') {
      toast('扫库失败：' + (j.message || j.state));
    }
    return;
  }
}

// ==================== 补齐元数据（存量迁移） ====================
// 早年「收藏夹直下」入库的条目，entry.json 是极简版：缺顶层 title / owner_id / 宽高。
// 外部播放器扫到会退化（标题变 key、分辨率 0×0、UP 主合集塌进「未识别 UP 主」）。
// 补齐所需字段绝大多数 db 里就有，只有 owner_id 要逐个问 B 站（681 条约 40 分钟）。
$('btnBackfill')?.addEventListener('click', async () => {
  if (!confirm('补齐早年下载条目的元数据？\n\n' +
    '会补：标题、UP 主 ID、分辨率（写进每个条目的 entry.json）\n' +
    '安全：改之前原文会先备份到 .favsync/entry-backup/\n\n' +
    '继续？')) return;
  const withMid = confirm('是否同时补齐 UP 主 ID？\n\n' +
    '· 确定 = 补齐（需向 B 站发约 680 次请求，约 40 分钟，可随时取消）\n' +
    '· 取消 = 只补标题与分辨率（几秒完成，不发网络请求）');
  const btn = $('btnBackfill');
  const old = btn.textContent;
  btn.disabled = true; btn.textContent = '补齐中…';
  try {
    const r = await api('/maintenance/backfill-entry', {
      method: 'POST',
      body: JSON.stringify({ withOwnerMid: withMid }),
    });
    toast(r.message || '已开始');
  } catch (err) {
    toast('启动失败：' + (err.message || err));
  } finally {
    btn.disabled = false; btn.textContent = old;
  }
});

$('btnVersions').addEventListener('click', openVersionsModal);
$('btnVsClose').addEventListener('click', closeVersionsModal);
$('versionsModal').addEventListener('click', (e) => { if (e.target === $('versionsModal')) closeVersionsModal(); });

$('btnTrash').addEventListener('click', openTrashModal);
$('btnTrClose').addEventListener('click', closeTrashModal);
$('btnTrEmpty').addEventListener('click', async () => {
  if (!trash.total) return;
  if (!confirm(`清空回收站？\n\n${trash.total} 条 · ${fmtBytes(trash.bytes)} 将被永久删除，无法恢复。`)) return;
  const btn = $('btnTrEmpty');
  btn.disabled = true;
  const old = btn.textContent;
  btn.textContent = '清空中…';
  try {
    const r = await api('/trash/empty', { method: 'POST', body: '{}' });
    toast(`回收站已清空：${r.removed} 条 · ${fmtBytes(r.bytes || 0)}`);
    await openTrashModal();
    refreshTrashBadge();
  } catch (err) {
    toast('清空失败：' + (err.message || err));
    btn.textContent = old;
    btn.disabled = trash.total > 0;
  }
});

// ---- 页内播放器交互 ----
$('btnPClose').addEventListener('click', closePlayer);
$('btnPExt').addEventListener('click', () => { if (player.key) openExternal(player.key); });
$('btnPNext').addEventListener('click', () => {
  const nx = nextSibling();
  if (nx) openPlayer(nx.key);
});
$('pPlay').addEventListener('click', () => {
  $('pVideo').play().catch(() => { });
});
$('playerModal').addEventListener('click', (e) => {
  if (e.target === $('playerModal')) closePlayer();     // 点遮罩外层关掉
});
document.addEventListener('keydown', (e) => {
  if ($('playerModal').classList.contains('hidden')) return;
  const tag = (e.target.tagName || '').toLowerCase();
  if (tag === 'input' || tag === 'textarea' || tag === 'select') return;
  const v = $('pVideo');
  switch (e.key) {
    case 'Escape': e.preventDefault(); closePlayer(); break;
    case ' ':
      e.preventDefault();
      if (v.paused) v.play().catch(() => { }); else v.pause();
      break;
    case 'ArrowLeft':
      e.preventDefault();
      v.currentTime = Math.max(0, v.currentTime - 5);
      break;
    case 'ArrowRight':
      e.preventDefault();
      if (isFinite(v.duration)) v.currentTime = Math.min(v.duration, v.currentTime + 5);
      break;
    case 'ArrowUp': e.preventDefault(); v.volume = Math.min(1, v.volume + 0.05); break;
    case 'ArrowDown': e.preventDefault(); v.volume = Math.max(0, v.volume - 0.05); break;
    case 'f': case 'F':
      e.preventDefault();
      if (document.fullscreenElement) document.exitFullscreen().catch(() => { });
      else $('pVideo').requestFullscreen().catch(() => { });
      break;
  }
});

// 滚动加载：真正的滚动容器是 <main>（CSS: main{overflow:auto}），window 自己根本不滚 ——
// 事件挂在 window 上永远不触发，这就是"滚动加载更多"看起来失效的原因。scroll 不冒泡，必须直接挂元素。
const libScroller = document.querySelector('main');
(libScroller || window).addEventListener('scroll', () => {
  if (state.page !== 'library' || lib.done || lib.loading) return;
  const el = libScroller || document.scrollingElement || document.documentElement;
  if (el.scrollTop + el.clientHeight > el.scrollHeight - 600) loadLibrary(false);
});

// 兜底：滚动到底万一没触发（容器被换 / 触屏惯性），底部提示自己可点
$('libMore').addEventListener('click', () => {
  if (!lib.done && !lib.loading) loadLibrary(false);
});

// ---- Bilibili 页交互 ----
$('btnFavAll').addEventListener('click', () => {
  // 作用于当前筛选下的全部可勾选项（快照路径下不受分页限制）
  const avail = favSelectable(favFilteredAll());
  const allOn = avail.length > 0 && avail.every(i => fav.selected.has(String(i.avid)));
  fav.selected.clear();
  if (!allOn) avail.forEach(i => fav.selected.add(String(i.avid)));
  renderFavGrid();
});

$('favTabs').addEventListener('click', (e) => {
  const b = e.target.closest('button[data-ff]');
  if (!b || b.disabled) return;
  fav.filter = b.dataset.ff;
  fav.shown = 60;                        // 换筛选回到第一页
  renderFavGrid();
});

$('btnFavGo').addEventListener('click', () => startFavDownload([...fav.selected]));

$('btnFavWhole').addEventListener('click', () => startFavDownload(null));

$('btnFavScan').addEventListener('click', () => {
  scanFav(true);       // 一律强制重扫：点开收藏夹那条路径会复用 10 分钟内的旧快照，这里就是要绕过它
});

$('favGrid').addEventListener('click', (e) => {
  const cb = e.target.closest('input[type=checkbox][data-avid]');
  if (cb) {
    if (cb.checked) fav.selected.add(cb.dataset.avid); else fav.selected.delete(cb.dataset.avid);
    renderFavToolbar();
    return;
  }
  const card = e.target.closest('.vcard[data-avid]');
  if (!card || card.dataset.disabled === 'true') return;
  // 点卡片一律弹 P 选择框（单P一行可直接下；多P逐个勾选；已在库的P显示徽标可补漏）
  openPagesModal(Number(card.dataset.avid));
});

$('favMore').addEventListener('click', () => {
  if (fav.loading) return;
  if (fav.snapItems.length) { fav.shown += 60; renderFavGrid(); }   // 快照路径：本地翻页
  else loadFavItems();
});

// ---- 任务浮窗 ----
$('btnDockMin').addEventListener('click', () => minimizeDock());
$('btnDockCollapse').addEventListener('click', () => setDockCollapsed(true));
$('btnDockExpand').addEventListener('click', () => setDockCollapsed(false));
$('btnDockClose').addEventListener('click', () => {
  if (state.jobs.length) { minimizeDock(); return; }   // 还有活在干：只能收走，不能丢
  finishedJob = null;
  hideDock();
});
$('taskPill').addEventListener('click', () => {
  $('taskPill').classList.add('hidden');
  // 点回来时队列可能已空：把最后一次作业的卡片（含完整日志）重新铺上
  if (!state.jobs.length && finishedJob) {
    $('taskList').innerHTML = taskCardHtml(finishedJob, true);
    bindLogs($('taskList'));
    updateDockSummary([finishedJob]);
    $('taskDockCount').textContent = '已结束';
  }
  showDock();
  if (dockCollapsed) setDockCollapsed(false);   // 收起态点回来是想看内容，不是看一行进度
});

// ==================== B 站登录 ====================

let qrTimer = null, qrKey = null, biliLoggedIn = false;

$('btnBili').addEventListener('click', async () => {
  if (biliLoggedIn) {
    if (!confirm('退出 B 站登录？探测将不可用（缓存仍有效）。')) return;
    try {
      await api('/bili/logout', { method: 'POST' });
      biliLoggedIn = false;
      renderBiliChip(null, false);
      state.ready.bili = false;
    } catch (err) { alert('退出失败：' + err.message); }
  } else {
    openLogin();
  }
});

async function openLogin() {
  $('loginModal').classList.remove('hidden');
  $('qrHint').textContent = '生成中…';
  $('qrImg').removeAttribute('src');
  try {
    const r = await api('/bili/qr/start', { method: 'POST' });
    qrKey = r.qrKey;
    $('qrImg').src = 'data:image/png;base64,' + r.qrImage;
    $('qrHint').textContent = '请用 B 站 App 扫码登录';
    clearInterval(qrTimer);
    qrTimer = setInterval(pollQr, 2000);
  } catch (err) {
    $('qrHint').textContent = err.message;
  }
}

async function pollQr() {
  try {
    const r = await api('/bili/qr/poll?qrcodeKey=' + encodeURIComponent(qrKey));
    if (r.state === 'scanned') {
      $('qrHint').textContent = '已扫码，请在手机上确认';
    } else if (r.state === 'expired') {
      clearInterval(qrTimer);
      $('qrHint').textContent = '二维码已过期，正在重新生成…';
      setTimeout(openLogin, 800);
    } else if (r.state === 'confirmed') {
      clearInterval(qrTimer);
      biliLoggedIn = true;
      $('qrHint').textContent = '登录成功' + (r.uname ? '：' + r.uname : '');
      renderBiliChip({ uname: r.uname }, true);
      state.ready.bili = false;
      setTimeout(() => {
        $('loginModal').classList.add('hidden');
        loadStatus();
        if (state.page === 'bili') { state.ready.bili = true; loadFolders(); }
      }, 900);
    }
  } catch (err) {
    clearInterval(qrTimer);
    $('qrHint').textContent = '轮询失败：' + err.message;
  }
}

$('btnQrClose').addEventListener('click', () => {
  clearInterval(qrTimer);
  $('loginModal').classList.add('hidden');
});

// ==================== 连接手机 ====================

const PHONE_STATE_LABEL = {
  device: '在线',
  offline: '离线',
  unauthorized: '未授权（在手机上确认调试授权）',
  recovery: '恢复模式',
};

async function openPhoneModal() {
  $('phoneModal').classList.remove('hidden');
  if (!$('phoneAddr').value.trim()) {
    $('phoneAddr').value = localStorage.getItem('favsync.lastDeviceAddr') || '';
  }
  await refreshPhoneDevs();
}

async function refreshPhoneDevs() {
  try {
    const d = await api('/devices');
    $('phoneDevs').innerHTML = (d.devices || []).length
      ? d.devices.map(x => `<div class="ph-dev">
          <span class="ph-ser mono">${escapeHtml(x.serial)}</span>
          <span class="ph-state ${x.online ? 'ok' : 'bad'}">${PHONE_STATE_LABEL[x.state] || x.state}</span>
        </div>`).join('')
      : '<div class="empty">没有设备。填上面的地址点连接，或插 USB。</div>';
  } catch (e) {
    $('phoneDevs').innerHTML = `<div class="empty">${escapeHtml(e.message)}</div>`;
  }
}

$('btnPhone').addEventListener('click', openPhoneModal);
$('statusChip').addEventListener('click', openPhoneModal);

$('btnPhoneConnect').addEventListener('click', async () => {
  const addr = $('phoneAddr').value.trim();
  if (!addr) { toast('先填手机 IP:端口'); return; }
  const btn = $('btnPhoneConnect');
  btn.disabled = true;
  btn.textContent = '连接中…';
  try {
    const r = await api('/devices/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ address: addr }),
    });
    localStorage.setItem('favsync.lastDeviceAddr', addr);
    await refreshPhoneDevs();
    await loadStatus();
    toast(r.ok ? '已连接，可以同步了' : ('未连上：' + (r.output || '未知错误')));
  } catch (e) {
    toast('连接失败：' + e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = '连接';
  }
});

$('phoneAddr').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') $('btnPhoneConnect').click();
});

$('btnPhoneDisconnect').addEventListener('click', async () => {
  try {
    await api('/devices/disconnect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ address: '' }),
    });
    await refreshPhoneDevs();
    await loadStatus();
    toast('已断开');
  } catch (e) { toast('断开失败：' + e.message); }
});

$('btnPhoneClose').addEventListener('click', () => $('phoneModal').classList.add('hidden'));
$('phoneModal').addEventListener('click', (e) => { if (e.target === $('phoneModal')) $('phoneModal').classList.add('hidden'); });

// ==================== 冷备份与恢复 ====================
// 密码只活在前端这一次批次会话里（sessionStorage，关页面即清），
// 除了转给 7z 进程，服务端不落库、不写 config、不写日志。

const COLD_PASS_KEY = 'favsyncColdPass';
const coldPass = () => sessionStorage.getItem(COLD_PASS_KEY) || '';
const setColdPass = (p) => p ? sessionStorage.setItem(COLD_PASS_KEY, p) : sessionStorage.removeItem(COLD_PASS_KEY);
const COLD_DIR_KEY = 'favsyncColdDir';
const coldDir = () => sessionStorage.getItem(COLD_DIR_KEY) || '';
const setColdDir = (p) => p ? sessionStorage.setItem(COLD_DIR_KEY, p) : sessionStorage.removeItem(COLD_DIR_KEY);

const cold = {
  status: null, buckets: [], offset: 0, limit: 20, state: 'all', total: 0,
  open: false, preview: null, mode: 'restore', bucketId: null, loading: false,
};

function driveChipsHtml(drives) {
  return (drives || []).map(d => {
    const name = (d.root || '').replace(/\\$/, '');
    const label = d.label ? `${name} · ${d.label}` : name;
    return `<button type="button" class="drive-chip" data-root="${escapeHtml(d.root)}">
              <b>${escapeHtml(label)}${d.removable ? ' · 移动/网络' : ''}</b>
              <span class="mono">剩 ${d.freeGb} / 共 ${d.totalGb} GB</span>
            </button>`;
  }).join('');
}

function coldStat(cls, n, label, sub, click) {
  return `<div class="cold-stat ${cls}${click ? ' click' : ''}" data-cold="${click || ''}">
      <div class="cs-n">${escapeHtml(String(n))}</div>
      <div class="cs-l">${escapeHtml(label)}</div>
      <div class="cs-s">${escapeHtml(sub || '')}</div>
    </div>`;
}

async function loadColdStatus() {
  try {
    const s = await api('/cold/status');
    cold.status = s;
    renderColdStatus(s);
    if (cold.open) loadColdBuckets();
  } catch (e) {
    $('coldStats').innerHTML = `<div class="cold-stat bad"><div class="cs-n">读取失败</div>
      <div class="cs-l">${escapeHtml(e.message)}</div></div>`;
  }
}

function renderColdStatus(s) {
  const parts = [];
  parts.push(coldStat('', s.uncoldItems, '未冷备', fmtBytes(s.uncoldBytes), 'pack'));
  parts.push(coldStat(s.coldItems > 0 ? 'ok' : '', s.coldItems, '已冷备', fmtBytes(s.coldBytes)));
  parts.push(coldStat(s.onlyInBucketItems > 0 ? 'warn' : '', s.onlyInBucketItems, '仅在桶里（需恢复）',
    fmtBytes(s.onlyInBucketBytes), s.onlyInBucketItems > 0 ? 'restore' : ''));
  parts.push(coldStat(s.blockedItems > 0 ? 'bad' : '', s.blockedItems, '无法冷备', '账实不符', 'blocked'));
  parts.push(coldStat(s.bucketPendingUpload > 0 ? 'warn' : '', s.bucketPendingUpload, '待上传桶',
    fmtBytes(s.pendingUploadBytes), 'buckets'));
  // 外来桶 = 文件在冷备份目录之外（导入 / 恢复进来的）。它们本来就在别处，不算"等上传"，
  // 否则从网盘下回来的一批会显示成"待上传 84 GB"，纯吓人
  if (s.bucketExternal > 0)
    parts.push(coldStat('', s.bucketExternal, '外来桶',
      fmtBytes(s.bucketExternalBytes) + ' · 文件不在冷备目录', 'buckets'));
  if (s.bucketLocalMissing > 0)
    parts.push(coldStat('bad', s.bucketLocalMissing, '本地文件缺失', '标过已上传但文件不在了'));
  $('coldStats').innerHTML = parts.join('');

  const note = [];
  note.push(s.coldRoot ? `冷备份目录：${s.coldRoot}` : '冷备份目录：未设置（点上面「导出未冷备」会提示去填）');
  if (s.coldFreeBytes >= 0) note.push(`剩余 ${fmtBytes(s.coldFreeBytes)} / 共 ${fmtBytes(s.coldTotalBytes)}`);
  if (s.coldRootError && s.coldRoot) note.push('⚠ ' + s.coldRootError);
  if (s.lastCheckAt) note.push(`上次账实核对 ${fmtDate(s.lastCheckAt)}`);
  if (s.lastVerifyAt) note.push(`上次抽验 ${fmtDate(s.lastVerifyAt)}`);
  $('cfgColdNote').textContent = note.join('　·　');
}

async function loadColdBuckets() {
  if (cold.loading) return;
  cold.loading = true;
  try {
    const q = new URLSearchParams({ state: cold.state, offset: cold.offset, limit: cold.limit });
    const r = await api('/cold/buckets?' + q.toString());
    cold.buckets = r.rows || [];
    cold.total = r.total || 0;
    renderColdBuckets();
  } catch (e) {
    $('coldList').innerHTML = `<div class="empty">读取失败：${escapeHtml(e.message)}</div>`;
  } finally {
    cold.loading = false;
  }
}

function renderColdBuckets() {
  const rows = cold.buckets.map(b => {
    const lost = !b.fileExists && (b.state === 'packed' || b.state === 'uploaded');
    // 外来桶：文件不在冷备份目录里（导入 / 恢复进来的），它的 "packed" 不等于"等我上传"
    const badge = lost ? '<span class="cold-badge lost">本地缺失</span>'
      : (b.external && b.state === 'packed')
        ? '<span class="cold-badge uploaded">外来桶</span>'
        : `<span class="cold-badge ${b.state}">${escapeHtml(b.stateLabel)}</span>`;
    const ops = [];
    if (b.state !== 'pruned' && b.state !== 'aborted' && b.fileExists) {
      ops.push(`<button class="btn tiny" data-cop="verify" data-id="${escapeHtml(b.bucketId)}">校验</button>`);
      ops.push(`<button class="btn tiny" data-cop="open" data-id="${escapeHtml(b.bucketId)}">打开目录</button>`);
    }
    if (b.state === 'packed') {
      ops.push(`<button class="btn tiny primary" data-cop="uploaded-prune" data-id="${escapeHtml(b.bucketId)}"
        title="上传完点这里：标记已上传并删掉本地文件，省空间">已上传并删本地</button>`);
      ops.push(`<button class="btn tiny" data-cop="uploaded" data-id="${escapeHtml(b.bucketId)}">只标记已上传</button>`);
    } else if (b.state === 'uploaded' && b.fileExists) {
      ops.push(`<button class="btn tiny" data-cop="prune" data-id="${escapeHtml(b.bucketId)}">删本地</button>`);
    }
    if (lost) {
      ops.push(`<button class="btn tiny" data-cop="release" data-id="${escapeHtml(b.bucketId)}"
        title="桶文件丢了又没上传：把这批字节退回「未冷备」，下次导出重新打包">释放</button>`);
    }
    return `<div class="cold-row">
      <span class="cr-id" title="${escapeHtml(b.bucketId)}">${escapeHtml(b.fileName)}</span>
      ${badge}
      <span class="cr-n">${b.itemCount} 条</span>
      <span class="cr-n">${fmtBytes(b.bytes)}</span>
      <span class="cr-sha">${escapeHtml(b.sha256 || '—')}</span>
      <span class="cr-ops">${ops.join('')}</span>
    </div>`;
  }).join('');

  $('coldList').innerHTML = cold.buckets.length ? rows : '<div class="empty">这一批还没有桶</div>';
  const from = cold.total ? cold.offset + 1 : 0;
  const to = Math.min(cold.offset + cold.limit, cold.total);
  $('coldListMsg').textContent = `共 ${cold.total} 桶${cold.total ? `（${from}–${to}）` : ''}`;
}

async function pollJob(jobId, label, onDone) {
  if (!jobId) return;
  for (let i = 0; i < 36000; i++) {                 // 最长等 20 小时：全量打包本来就要十几小时
    await new Promise(r => setTimeout(r, 2000));
    let j;
    try { j = await api('/jobs/' + encodeURIComponent(jobId)); } catch { return; }
    if (!j) return;
    if (j.state === 'running' || j.state === 'queued') continue;
    if (j.state === 'done') toast(j.message || (label + '完成'));
    else toast(label + (j.state === 'cancelled' ? '已取消' : '失败：' + (j.message || j.state)));
    onDone && onDone(j);
    return;
  }
}

// ---- 导出 ----

async function openColdPack() {
  if (!cold.status) await loadColdStatus();
  const s = cold.status || {};
  if (!s.coldRoot) {
    setCfgMsg('先设置「冷备份目录」（就在本卡片最上面那个输入框），保存后再导出。', 'warn');
    $('cfgColdRoot').focus();
    return;
  }
  $('coldPass').value = coldPass();
  $('coldPassSaved').checked = false;
  $('coldVerifyMd5').checked = false;
  $('coldMaxGiB').value = 0;
  $('coldMaxBuckets').value = 0;
  $('coldPackMsg').textContent = '';
  $('btnColdPackGo').disabled = true;
  $('coldEstimate').innerHTML = '正在清点未冷备条目…';
  $('coldPackModal').classList.remove('hidden');

  try {
    const e = await api('/cold/estimate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ targetMiB: Number($('cfgColdBucket').value) || 2048 }),
    });
    cold.estimate = e;
    $('coldEstimate').innerHTML =
      `本次可打 <b>${e.itemCount}</b> 条 / <b>${fmtBytes(e.bytes)}</b> → 预计 <b>${e.bucketCount}</b> 个桶`
      + `（桶大小 ${e.targetMiB} MiB，前缀 <b>${escapeHtml(e.label || 'cold')}</b>，导出机 <b>${escapeHtml(e.origin || 'M')}</b>）`
      + (e.blockedCount > 0
        ? `<br><span style="color:var(--warn)">另有 <b>${e.blockedCount}</b> 条账实不符（${fmtBytes(e.blockedBytes)}），会被踢出本次打包</span>`
        : '')
      + (e.blockedSample && e.blockedSample.length ? `<br><span class="mono muted">${escapeHtml(e.blockedSample[0])}</span>` : '');
    $('btnColdPackGo').disabled = e.itemCount === 0;
  } catch (err) {
    $('coldEstimate').innerHTML = `<span style="color:var(--bad)">清点失败：${escapeHtml(err.message)}</span>`;
  }
}

function syncPackGo() {
  const ok = $('coldPass').value.length > 0 && $('coldPassSaved').checked
    && cold.estimate && cold.estimate.itemCount > 0;
  $('btnColdPackGo').disabled = !ok;
}

$('btnColdPack').addEventListener('click', openColdPack);
$('coldPass').addEventListener('input', syncPackGo);
$('coldPassSaved').addEventListener('change', syncPackGo);
$('btnColdPackCancel').addEventListener('click', () => $('coldPackModal').classList.add('hidden'));
$('coldPackModal').addEventListener('click', (e) => {
  if (e.target === $('coldPackModal')) $('coldPackModal').classList.add('hidden');
});

$('btnColdPackGo').addEventListener('click', async () => {
  const pass = $('coldPass').value;
  if (!pass) { $('coldPackMsg').textContent = '必须设置密码'; return; }
  if (!$('coldPassSaved').checked) { $('coldPackMsg').textContent = '请先确认密码已单独保存'; return; }
  setColdPass(pass);
  const btn = $('btnColdPackGo');
  btn.disabled = true;
  $('coldPackMsg').textContent = '正在入队…';
  try {
    const r = await api('/cold/pack', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        pass,
        label: $('cfgColdLabel').value.trim() || 'cold',
        origin: $('cfgColdOrigin').value.trim() || 'M',
        targetMiB: Number($('cfgColdBucket').value) || 2048,
        maxGiB: Number($('coldMaxGiB').value) || 0,
        maxBuckets: Number($('coldMaxBuckets').value) || 0,
        verifyMd5: $('coldVerifyMd5').checked,
      }),
    });
    $('coldPackModal').classList.add('hidden');
    toast('已开始打包，进度见右下角任务浮窗');
    pollJob(r.jobId, '冷备份', () => { loadColdStatus(); if (cold.open) loadColdBuckets(); });
    showDock();
  } catch (e) {
    $('coldPackMsg').textContent = e.message;
    btn.disabled = false;
  }
});

// ---- 恢复 / 导入 / 校验（共用弹层）----

function openColdIo(mode, bucketId) {
  cold.mode = mode;
  cold.bucketId = bucketId || null;
  cold.preview = null;
  const isVerify = mode === 'verify';
  $('coldIoTitle').textContent = isVerify ? '校验桶' : mode === 'import' ? '导入外部桶' : '从冷备份恢复';
  $('coldIoDir').value = isVerify
    ? (cold.status && cold.status.coldRoot ? cold.status.coldRoot : '')
    : (coldDir() || (cold.status && cold.status.coldRoot ? cold.status.coldRoot : ''));
  $('coldIoDir').disabled = isVerify;
  $('coldIoPass').value = coldPass();
  $('coldIoSummary').textContent = isVerify
    ? '校验 = 读一遍归档算 sha256 + 7z 完整性测试（逐文件 CRC），能够发现"传坏了/没写完"'
    : mode === 'import'
      ? '导入只登记账目（哪些桶、覆盖了哪些条目、补回 probe 结论），不搬字节、不改总库'
      : '先「读取清单」看能恢复哪些，确认后再开始。恢复只走 RegisterBackup，绝不覆盖库里已有的字节。';
  $('coldIoList').innerHTML = '';
  $('coldIoMsg').textContent = '';
  $('coldIoDrives').innerHTML = driveChipsHtml(cold.status && cold.status.drives);
  $('btnColdIoRead').classList.toggle('hidden', mode !== 'restore');
  $('btnColdIoGo').disabled = true;
  $('btnColdIoGo').textContent = isVerify ? '开始校验' : mode === 'import' ? '开始导入' : '开始恢复';
  $('coldIoModal').classList.remove('hidden');
}

$('btnColdRestore').addEventListener('click', () => openColdIo('restore'));
$('btnColdImport').addEventListener('click', () => openColdIo('import'));
$('btnColdIoClose').addEventListener('click', () => $('coldIoModal').classList.add('hidden'));
$('coldIoModal').addEventListener('click', (e) => {
  if (e.target === $('coldIoModal')) $('coldIoModal').classList.add('hidden');
});

$('coldIoDrives').addEventListener('click', (e) => {
  const chip = e.target.closest('.drive-chip');
  if (!chip) return;
  const root = chip.dataset.root || '';
  $('coldIoDir').value = root + (root.endsWith('\\') ? '' : '\\') + 'FavSyncCold';
  $('coldIoDir').focus();
});

$('btnColdIoRead').addEventListener('click', async () => {
  const dir = $('coldIoDir').value.trim();
  const pass = $('coldIoPass').value;
  if (!dir) { $('coldIoMsg').textContent = '先填桶所在目录'; return; }
  if (!pass) { $('coldIoMsg').textContent = '先填密码'; return; }
  const btn = $('btnColdIoRead');
  btn.disabled = true;
  $('coldIoMsg').textContent = '正在读清单…';
  try {
    const p = await api('/cold/preview', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ dir, pass }),
    });
    cold.preview = p;
    setColdPass(pass);
    setColdDir(dir);
    renderColdPreview(p);
    $('btnColdIoGo').disabled = p.missing === 0;
    $('coldIoMsg').textContent = p.missing === 0
      ? '这批条目在本机都有了，没有要恢复的'
      : '';
  } catch (e) {
    $('coldIoMsg').textContent = e.message;
  } finally {
    btn.disabled = false;
  }
});

function renderColdPreview(p) {
  $('coldIoSummary').innerHTML =
    `清单 <b>${escapeHtml(p.batch || '—')}</b>：<b>${p.bucketCount}</b> 个桶 / <b>${p.itemCount}</b> 条 / <b>${fmtBytes(p.bytes)}</b>`
    + `<br>本机缺 <b>${p.missing}</b>　已有 <b>${p.exists}</b>　其它档位 <b>${p.other}</b>`;

  const byBucket = {};
  (p.items || []).forEach(it => {
    const b = byBucket[it.bucketId] || (byBucket[it.bucketId] = { id: it.bucketId, missing: 0, exists: 0, other: 0, sample: [] });
    b[it.reconcile] = (b[it.reconcile] || 0) + 1;
    if (b.sample.length < 2 && it.title) b.sample.push(it.title);
  });

  $('coldIoList').innerHTML = Object.values(byBucket).map(b => {
    const on = b.missing > 0;
    return `<div class="cold-io-row">
      <input type="checkbox" data-bucket="${escapeHtml(b.id)}" ${on ? 'checked' : 'disabled'}>
      <span class="pt">${escapeHtml(b.id)}<br><span class="mono muted">${escapeHtml(b.sample.join(' / '))}</span></span>
      <span class="pd ${b.missing ? 'warn' : 'ok'}">缺 ${b.missing}</span>
      <span class="pd">已有 ${b.exists}</span>
      ${b.other ? `<span class="pd">其它档位 ${b.other}</span>` : ''}
    </div>`;
  }).join('') || '<div class="empty">清单里没有条目</div>';
}

$('coldIoList').addEventListener('change', () => {
  const any = !![...$('coldIoList').querySelectorAll('input[data-bucket]')].find(c => c.checked);
  $('btnColdIoGo').disabled = !any;
});

$('btnColdIoGo').addEventListener('click', async () => {
  const dir = $('coldIoDir').value.trim();
  const pass = $('coldIoPass').value;
  if (!pass) { $('coldIoMsg').textContent = '先填密码'; return; }
  const btn = $('btnColdIoGo');
  btn.disabled = true;

  try {
    if (cold.mode === 'verify') {
      $('coldIoMsg').textContent = '正在校验（2 GiB 约 10~30 秒）…';
      const r = await api(`/cold/bucket/${encodeURIComponent(cold.bucketId)}/verify`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pass }),
      });
      $('coldIoMsg').textContent = (r.ok ? '✓ ' : '✗ ') + r.message;
      setColdPass(pass);
      btn.disabled = false;
      return;
    }

    if (cold.mode === 'import') {
      if (!dir) { $('coldIoMsg').textContent = '先填目录'; btn.disabled = false; return; }
      $('coldIoMsg').textContent = '正在导入…';
      const r = await api('/cold/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ dir, pass }),
      });
      setColdPass(pass);
      setColdDir(dir);
      $('coldIoMsg').textContent = '✓ ' + r.message;
      toast(r.message);
      loadColdStatus();
      if (cold.open) loadColdBuckets();
      btn.disabled = false;
      return;
    }

    // restore
    const ids = [...$('coldIoList').querySelectorAll('input[data-bucket]')]
      .filter(c => c.checked).map(c => c.dataset.bucket);
    if (!ids.length) { $('coldIoMsg').textContent = '没勾选任何桶'; btn.disabled = false; return; }
    $('coldIoMsg').textContent = '正在入队…';
    const r = await api('/cold/restore', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ dir, pass, bucketIds: ids }),
    });
    setColdPass(pass);
    setColdDir(dir);
    $('coldIoModal').classList.add('hidden');
    toast('开始恢复，进度见右下角任务浮窗');
    pollJob(r.jobId, '恢复', () => { loadColdStatus(); if (cold.open) loadColdBuckets(); });
    showDock();
  } catch (e) {
    $('coldIoMsg').textContent = e.message;
    btn.disabled = false;
  }
});

// ---- 无法冷备清单 ----

$('btnColdBlocked').addEventListener('click', async () => {
  $('coldBlockedMeta').textContent = '读取中…';
  $('coldBlockedList').innerHTML = '';
  $('coldBlockedModal').classList.remove('hidden');
  try {
    const r = await api('/cold/blocked?offset=0&limit=200');
    $('coldBlockedMeta').textContent = `共 ${r.total} 条账实不符（只显示前 200 条）`;
    $('coldBlockedList').innerHTML = (r.rows || []).map(x => `<div class="tr-row">
        <span class="tr-key">${escapeHtml(x.key)}</span>
        <span class="pt">${escapeHtml(x.title || '')}<br><span class="mono muted">${escapeHtml(x.reason)}</span></span>
      </div>`).join('') || '<div class="empty">没有无法冷备的条目</div>';
  } catch (e) {
    $('coldBlockedMeta').textContent = '读取失败：' + e.message;
  }
});
$('btnColdBlockedClose').addEventListener('click', () => $('coldBlockedModal').classList.add('hidden'));
$('coldBlockedModal').addEventListener('click', (e) => {
  if (e.target === $('coldBlockedModal')) $('coldBlockedModal').classList.add('hidden');
});

// ---- 桶列表 ----

$('btnColdListToggle').addEventListener('click', () => {
  cold.open = !cold.open;
  $('coldList').classList.toggle('hidden', !cold.open);
  $('btnColdListToggle').textContent = cold.open ? '收起桶列表' : '展开桶列表';
  if (cold.open) loadColdBuckets();
});

$('coldTabs').addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-cstate]');
  if (!btn) return;
  $('coldTabs').querySelectorAll('button').forEach(b => b.classList.toggle('active', b === btn));
  cold.state = btn.dataset.cstate;
  cold.offset = 0;
  loadColdBuckets();
});

$('coldPrev').addEventListener('click', () => {
  cold.offset = Math.max(0, cold.offset - cold.limit);
  loadColdBuckets();
});
$('coldNext').addEventListener('click', () => {
  if (cold.offset + cold.limit >= cold.total) return;
  cold.offset += cold.limit;
  loadColdBuckets();
});

$('coldStats').addEventListener('click', (e) => {
  const el = e.target.closest('[data-cold]');
  if (!el) return;
  const act = el.dataset.cold;
  if (act === 'pack') openColdPack();
  else if (act === 'restore') openColdIo('restore');
  else if (act === 'blocked') $('btnColdBlocked').click();
  else if (act === 'buckets' && !cold.open) $('btnColdListToggle').click();
});

$('coldList').addEventListener('click', async (e) => {
  const btn = e.target.closest('button[data-cop]');
  if (!btn) return;
  const id = btn.dataset.id;
  const op = btn.dataset.cop;

  if (op === 'verify') { openColdIo('verify', id); return; }
  if (op === 'open') {
    try { await api(`/cold/bucket/${encodeURIComponent(id)}/open`, { method: 'POST' }); }
    catch (err) { toast(err.message); }
    return;
  }
  if (op === 'uploaded-prune') {
    if (!confirm(`已上传并删本地？\n\n${id}\n\n只删本地这份 7z 文件；账目保留（它记的是云端事实）。\n确认网盘上已经传完了再点。`)) return;
  }
  if (op === 'release') {
    if (!confirm(`释放 ${id}？\n\n桶文件已经不在了，释放会把这批字节退回「未冷备」，下次导出重新打包。\n桶号保留、不再复用。`)) return;
  }
  btn.disabled = true;
  try {
    const r = await api(`/cold/bucket/${encodeURIComponent(id)}/${op}`, { method: 'POST' });
    toast(r.message || '完成');
    loadColdStatus();
    loadColdBuckets();
  } catch (err) {
    toast('操作失败：' + err.message);
  } finally {
    btn.disabled = false;
  }
});

// ==================== 启动 ====================

(async function boot() {
  syncHeadHeight();
  showPage(pageFromHash());
  syncTabs();
  await loadStatus();
  await loadItems();
  startStatsPoll();
  connectEvents();
  try {
    const r = await api('/jobs');
    if (r && r.jobs && r.jobs.length) { renderTasks(r.jobs); applyBusy(); }
    else {
      // 刷新页面后队列是空的，但上一次作业（含日志）还在服务端内存里。
      // 不自动弹窗挡视线，只在角落留个 pill 当回看入口。
      const last = await api('/jobs/latest');
      if (last && last.state !== 'running' && last.state !== 'queued') {
        finishedJob = last;
        const [stTxt] = JOB_STATE[last.state] || [''];
        $('taskPill').textContent = `${jobLabel(last)} ${stTxt || '已结束'}`
          + (last.log && last.log.length ? ' · 查看日志' : '');
        $('taskPill').classList.remove('hidden');
      }
    }
  } catch { }
  refreshTrashBadge();     // 只更新按钮上的条数，列表等点开时再拉
  refreshVersionBadge();
})();
