/*
 * StartupManager UI.
 *
 * Data only ever enters through StartupBridge:
 *   native WebView2 host -> state / settings / command results
 *   browser demo host   -> same message contract, in-memory data
 *
 * Mutation rules:
 *   - never render a setEnabled change as final until the host sends fresh state
 *   - keep the row in a pending state until commandResult/error arrives
 *   - surface UAC cancellation and verification errors verbatim
 */
(function () {
  'use strict';

  var Bridge = window.StartupBridge;

  var SOURCE_META = {
    registry: { label: '注册表', icon: 'key', note: 'Windows 注册表 Run 启动项' },
    folder: { label: '启动文件夹', icon: 'folder', note: '启动文件夹快捷方式' },
    task: { label: '计划任务', icon: 'clock', note: 'Windows 任务计划程序' },
    service: { label: '服务', icon: 'server', note: 'Windows 服务管理器' }
  };

  var SCOPE_META = {
    currentUser: { label: '当前用户', rank: 0 },
    allUsers: { label: '所有用户', rank: 1 },
    system: { label: '系统', rank: 2 }
  };

  var MECHANISM = {
    startupApproved: {
      label: '启动批准状态',
      desc: '写入 Windows 的启动禁用状态（StartupApproved），与任务管理器一致；不删除原始启动项。对于机器级项，禁用仅对当前用户生效。'
    },
    scheduledTask: {
      label: '任务计划程序',
      desc: '在任务计划程序中将该任务设为禁用，系统会真实跳过它。'
    },
    serviceStartType: {
      label: '服务启动类型',
      desc: '将服务启动类型改为手动，下次开机不再自动启动；当前运行中的服务不会被强制停止。'
    }
  };

  var LOCATE_LABEL = {
    registry: '打开文件所在位置',
    folder: '打开文件所在位置',
    task: '打开任务计划程序',
    service: '打开服务管理器'
  };

  var CATEGORY_META = {
    startup: { label: '启动项', sources: ['registry', 'folder'] },
    service: { label: '服务项', sources: ['service'] },
    task: { label: '计划任务', sources: ['task'] }
  };
  var FONT_SCALE_OPTIONS = [
    { value: 0.9, label: '小（90%）' },
    { value: 1, label: '标准（100%）' },
    { value: 1.1, label: '较大（110%）' },
    { value: 1.25, label: '大（125%）' }
  ];

  var DEFAULT_SETTINGS = {
    includeRegistry: true,
    includeStartupFolders: true,
    includeScheduledTasks: true,
    includeServices: true,
    startWithWindows: false,
    minimizeToTray: true,
    backupRetentionDays: 30,
    uiFontScale: 1,
    userAllowedItemIds: []
  };
  var BOOLEAN_SETTINGS = [
    'includeRegistry', 'includeStartupFolders', 'includeScheduledTasks', 'includeServices',
    'startWithWindows', 'minimizeToTray'
  ];
  var RETENTION_OPTIONS = [7, 30, 90, 365];

  var ui = {
    loading: true,
    loadError: null,
    snapshot: null,
    items: [],
    backups: [],
    settings: null,
    system: null,
    category: 'startup',
    filter: 'all',
    query: '',
    sort: 'source',
    page: 'startup',
    selectedId: null,
    scanPending: false,
    userScan: false,
    warningsDismissed: null,
    ops: new Map(),
    dialog: null
  };

  var el = {};
  var toastSeq = 0;
  var toastTimers = new Map();
  var dialogReturnFocus = null;
  var detailTriggerId = null;

  /* ---------- Small helpers ---------- */
  function $(selector, root) { return (root || document).querySelector(selector); }
  function $$(selector, root) { return Array.prototype.slice.call((root || document).querySelectorAll(selector)); }

  function esc(value) {
    return String(value == null ? '' : value).replace(/[&<>"']/g, function (ch) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
    });
  }

  function icon(name, cls) {
    return '<svg class="' + (cls || '') + '" aria-hidden="true"><use href="#i-' + name + '"/></svg>';
  }

  function pad(value) { return String(value).padStart(2, '0'); }

  function formatDateTime(value) {
    if (!value) return '—';
    var date = new Date(value);
    if (isNaN(date.getTime())) return '—';
    return date.getFullYear() + '/' + pad(date.getMonth() + 1) + '/' + pad(date.getDate()) +
      ' ' + pad(date.getHours()) + ':' + pad(date.getMinutes());
  }

  function formatScanTime(value) {
    if (!value) return '尚未扫描';
    var date = new Date(value);
    if (isNaN(date.getTime())) return '尚未扫描';
    var now = new Date();
    var today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    var target = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    var time = pad(date.getHours()) + ':' + pad(date.getMinutes());
    var diffDays = Math.round((today - target) / 86400000);
    if (diffDays === 0) return '今天 ' + time;
    if (diffDays === 1) return '昨天 ' + time;
    return (date.getMonth() + 1) + '/' + date.getDate() + ' ' + time;
  }

  function hashName(name) {
    var hash = 0;
    var text = String(name || '');
    for (var i = 0; i < text.length; i++) {
      hash = (hash * 31 + text.charCodeAt(i)) >>> 0;
    }
    return hash;
  }

  function tileFor(name) {
    var hue = hashName(name) % 360;
    var hue2 = (hue + 28) % 360;
    return 'linear-gradient(135deg,hsl(' + hue + ' 52% 52%),hsl(' + hue2 + ' 58% 37%))';
  }

  function letterFor(name) {
    var chars = Array.from(String(name || '').trim());
    var ch = chars.length ? chars[0] : '?';
    return /^[a-z]$/i.test(ch) ? ch.toUpperCase() : ch;
  }

  function isDataImage(value) {
    return typeof value === 'string' && /^data:image\//i.test(value);
  }

  function appIconHtml(name, iconValue, size) {
    var cls = 'app-ico' + (size === 'lg' ? ' lg' : '');
    if (isDataImage(iconValue)) {
      return '<div class="' + cls + ' has-img"><img src="' + esc(iconValue) + '" alt="" draggable="false"></div>';
    }
    return '<div class="' + cls + '" style="--tile:' + tileFor(name) + '">' + esc(letterFor(name)) + '</div>';
  }

  function sourceMeta(source) {
    return SOURCE_META[source] || { label: '未知来源', icon: 'info', note: '未识别的启动来源' };
  }

  function sourceChip(source) {
    var meta = sourceMeta(source);
    var cls = SOURCE_META[source] ? source : 'registry';
    return '<span class="source-chip ' + cls + '">' + icon(meta.icon) + esc(meta.label) + '</span>';
  }

  function scopeMeta(scope) {
    return SCOPE_META[scope] || { label: '未知范围', rank: 9 };
  }

  function exeName(item) {
    var command = String(item.command || '');
    var match = command.match(/([^\\\/"]+\.exe)/i);
    if (match) return match[1];
    if (item.targetPath) {
      var parts = String(item.targetPath).split(/[\\\/]/);
      return parts[parts.length - 1] || command;
    }
    return command || '—';
  }

  function mechanismInfo(item) {
    if (MECHANISM[item.mechanism]) return MECHANISM[item.mechanism];
    if (item.source === 'task') return MECHANISM.scheduledTask;
    if (item.source === 'service') return MECHANISM.serviceStartType;
    return MECHANISM.startupApproved;
  }

  function findItem(id) {
    for (var i = 0; i < ui.items.length; i++) { if (ui.items[i].id === id) return ui.items[i]; }
    return null;
  }

  function findBackup(id) {
    for (var i = 0; i < ui.backups.length; i++) { if (ui.backups[i].id === id) return ui.backups[i]; }
    return null;
  }

  function activeBackups() {
    return ui.backups.filter(function (backup) { return backup.status === 'active'; });
  }

  function activeBackupForItem(itemId) {
    for (var i = 0; i < ui.backups.length; i++) {
      if (ui.backups[i].itemId === itemId && ui.backups[i].status === 'active') return ui.backups[i];
    }
    return null;
  }

  function normalizeSettings(input) {
    var result = {};
    BOOLEAN_SETTINGS.forEach(function (key) {
      result[key] = input && typeof input[key] === 'boolean' ? input[key] : DEFAULT_SETTINGS[key];
    });
    var retention = input ? Number(input.backupRetentionDays) : NaN;
    result.backupRetentionDays = isFinite(retention) && retention > 0 ? Math.round(retention) : DEFAULT_SETTINGS.backupRetentionDays;
    var fontScale = input ? Number(input.uiFontScale) : NaN;
    result.uiFontScale = isFinite(fontScale)
      ? Math.min(1.5, Math.max(0.8, fontScale))
      : DEFAULT_SETTINGS.uiFontScale;
    var allowedIds = input && Array.isArray(input.userAllowedItemIds)
      ? input.userAllowedItemIds.filter(function (id) { return typeof id === 'string' && id.length > 0; })
      : [];
    result.userAllowedItemIds = Array.from(new Set(allowedIds));
    return result;
  }

  function cloneSettings(settings) {
    return JSON.parse(JSON.stringify(settings || DEFAULT_SETTINGS));
  }

  function itemInCategory(item, category) {
    var meta = CATEGORY_META[category || ui.category] || CATEGORY_META.startup;
    return meta.sources.indexOf(item.source) !== -1;
  }

  function getCategoryCounts() {
    var counts = { startup: 0, service: 0, task: 0 };
    ui.items.forEach(function (item) {
      if (itemInCategory(item, 'startup')) counts.startup++;
      else if (itemInCategory(item, 'service')) counts.service++;
      else if (itemInCategory(item, 'task')) counts.task++;
    });
    return counts;
  }

  function getCounts() {
    var enabled = 0, disabled = 0, protectedCount = 0, adminComputed = 0, manageable = 0, total = 0;
    ui.items.forEach(function (item) {
      if (!itemInCategory(item)) return;
      total++;
      if (item.enabled) enabled++; else disabled++;
      if (item.protected) protectedCount++;
      if (!item.protected && item.requiresAdmin) adminComputed++;
      if (item.enabled && !item.protected) manageable++;
    });
    return {
      total: total,
      enabled: enabled,
      disabled: disabled,
      manageable: manageable,
      protectedCount: protectedCount,
      adminCount: adminComputed
    };
  }

  /* ---------- Pending operations ---------- */
  function mutationOps() {
    var result = [];
    ui.ops.forEach(function (op) {
      if (op.kind !== 'settings') result.push(op);
    });
    return result;
  }

  function isItemPending(id) {
    var pending = false;
    ui.ops.forEach(function (op) {
      if (!pending && op.itemIds && op.itemIds.indexOf(id) !== -1) pending = true;
    });
    return pending;
  }

  function isBackupPending(id) {
    var pending = false;
    ui.ops.forEach(function (op) {
      if (!pending && op.backupId === id) pending = true;
    });
    return pending;
  }

  function isSettingPending(key) {
    var pending = false;
    ui.ops.forEach(function (op) {
      if (!pending && op.kind === 'settings' && op.key === key) pending = true;
    });
    return pending;
  }

  function settingsBusy() {
    var busy = false;
    ui.ops.forEach(function (op) {
      if (op.kind === 'settings') busy = true;
    });
    return busy;
  }

  /* ---------- Rendering: shell / header ---------- */
  function renderSidebar() {
    var noData = ui.loading || (ui.loadError && !ui.items.length);
    var totalItems = ui.items.length;
    el.navCount.textContent = noData ? '–' : String(totalItems);
    el.backupNavCount.textContent = noData ? '–' : String(activeBackups().length);

    var sourceKinds = {};
    ui.items.forEach(function (item) { sourceKinds[item.source] = true; });
    var kindCount = Object.keys(sourceKinds).length;

    var title = '正在检查启动来源';
    if (!ui.loading) {
      if (ui.scanPending) title = '正在重新扫描';
      else if (ui.loadError && !ui.items.length) title = '扫描未完成';
      else if (kindCount) title = '共 ' + totalItems + ' 个启动项 · ' + kindCount + ' 类来源';
      else title = '未发现启动项';
    }
    el.scanTitle.textContent = title;

    if (ui.loading) {
      el.lastScan.textContent = '请稍候…';
    } else if (ui.scanPending) {
      el.lastScan.textContent = '正在刷新…';
    } else if (ui.snapshot && ui.snapshot.scannedAt) {
      el.lastScan.textContent = '上次扫描 · ' + formatScanTime(ui.snapshot.scannedAt);
    } else {
      el.lastScan.textContent = '尚未扫描';
    }

    el.scanDot.className = 'scan-dot' +
      (ui.scanPending ? ' busy' : ((ui.loadError && !ui.snapshot) ? ' error' : ''));

    renderScanState();
  }

  function renderScanState() {
    var disabled = ui.scanPending || ui.loading;
    $$('[data-action="rescan"]').forEach(function (button) {
      button.disabled = disabled;
      button.classList.toggle('spinning', ui.scanPending);
    });
    el.listCard.setAttribute('aria-busy', String(ui.loading || ui.scanPending));
    renderHeader();
  }

  function renderHeader() {
    var counts = getCounts();
    var noData = ui.loading || (ui.loadError && !ui.items.length);
    var categoryLabel = (CATEGORY_META[ui.category] || CATEGORY_META.startup).label;

    if (ui.loading) {
      el.summaryText.textContent = '正在扫描启动项…';
    } else if (noData) {
      el.summaryText.textContent = '扫描未完成';
    } else {
      el.summaryText.textContent = '共 ' + counts.total + ' 个' + categoryLabel + ' · ' + counts.enabled + ' 个已启用 · ' + counts.disabled + ' 个已禁用';
    }

    el.countAll.textContent = noData ? '–' : String(counts.total);
    el.countEnabled.textContent = noData ? '–' : String(counts.enabled);
    el.countDisabled.textContent = noData ? '–' : String(counts.disabled);

    if (noData) {
      el.metaChip.hidden = true;
    } else if (counts.adminCount > 0) {
      el.metaChip.className = 'meta-chip admin';
      el.metaChip.innerHTML = icon('shield') + '<span>' + counts.adminCount + ' 项需要管理员权限</span>';
      el.metaChip.hidden = false;
    } else if (counts.protectedCount > 0) {
      el.metaChip.className = 'meta-chip protected';
      el.metaChip.innerHTML = icon('lock') + '<span>' + counts.protectedCount + ' 项受系统保护</span>';
      el.metaChip.hidden = false;
    } else {
      el.metaChip.className = 'meta-chip';
      el.metaChip.innerHTML = icon('shield') + '<span>修改前自动备份</span>';
      el.metaChip.hidden = false;
    }

    el.bulkDisable.disabled = noData || ui.scanPending || counts.manageable === 0 || mutationOps().length > 0;
    renderCategoryTabs(noData);
  }

  function renderCategoryTabs(noData) {
    var counts = getCategoryCounts();
    if (el.categoryStartupCount) el.categoryStartupCount.textContent = noData ? '–' : String(counts.startup);
    if (el.categoryServiceCount) el.categoryServiceCount.textContent = noData ? '–' : String(counts.service);
    if (el.categoryTaskCount) el.categoryTaskCount.textContent = noData ? '–' : String(counts.task);
    updateCategoryUi();
  }

  function updateCategoryUi() {
    $$('.category-tab[data-category]').forEach(function (button) {
      var active = button.dataset.category === ui.category;
      button.classList.toggle('active', active);
      button.setAttribute('aria-selected', String(active));
    });
  }

  function renderWarnings() {
    var warnings = (ui.snapshot && Array.isArray(ui.snapshot.warnings)) ? ui.snapshot.warnings : [];
    if (!warnings.length) {
      el.warningsBanner.hidden = true;
      return;
    }
    var signature = warnings.join('\u0000');
    if (ui.warningsDismissed === signature) {
      el.warningsBanner.hidden = true;
      return;
    }
    el.warningsTitle.textContent = '扫描提示（' + warnings.length + '）';
    el.warningsList.innerHTML = warnings.map(function (warning) {
      return '<li>' + esc(warning) + '</li>';
    }).join('');
    el.warningsBanner.hidden = false;
  }

  /* ---------- Rendering: list ---------- */
  function updateFilterUi() {
    $$('.segmented button[data-filter]').forEach(function (button) {
      var active = button.dataset.filter === ui.filter;
      button.classList.toggle('active', active);
      button.setAttribute('aria-selected', String(active));
    });
  }

  function updateSearchUi() {
    el.searchBox.classList.toggle('has-text', el.searchInput.value.length > 0);
  }

  function skeletonHtml() {
    var row = '<div class="skeleton-row" aria-hidden="true">' +
      '<div class="skeleton-main"><span class="sk sk-ico"></span><span class="sk-lines"><span class="sk sk-name"></span><span class="sk sk-meta"></span></span></div>' +
      '<div class="sk-cell-source"><span class="sk sk-chip"></span></div>' +
      '<div class="sk-cell-scope"><span class="sk sk-chip"></span></div>' +
      '<span class="sk sk-switch"></span>' +
      '</div>';
    var html = '';
    for (var i = 0; i < 7; i++) html += row;
    return html;
  }

  function emptyHtml() {
    var query = ui.query.trim();
    var categoryLabel = (CATEGORY_META[ui.category] || CATEGORY_META.startup).label;
    var title = '没有匹配的启动项';
    var desc = '换个关键词试试，或清除当前的筛选条件。';
    var action = '<button class="btn" data-action="reset-filters">' + icon('refresh') + '清除筛选</button>';

    if (!ui.items.length) {
      title = '没有发现启动项';
      desc = '可以调整扫描范围，或重新扫描系统启动项。';
      action = '<button class="btn" data-action="rescan">' + icon('refresh') + '重新扫描</button>';
    } else if (!ui.items.some(function (item) { return itemInCategory(item); }) && ui.filter === 'all' && !query) {
      title = '当前分类没有' + categoryLabel;
      desc = '可以在设置中调整扫描范围，或切换到其他分类。';
      action = '<button class="btn" data-action="open-settings">' + icon('sliders') + '打开设置</button>';
    } else if (query) {
      title = '未找到「' + query + '」';
      desc = '试试应用名称、发布者或可执行文件名。';
    } else if (ui.filter === 'enabled') {
      title = '没有已启用的启动项';
      desc = '当前所有启动项都已禁用。';
    } else if (ui.filter === 'disabled') {
      title = '没有已禁用的启动项';
      desc = '被禁用的启动项会出现在这里，并可随时恢复。';
    }
    return '<div class="empty">' +
      '<div class="empty-icon">' + icon('search') + '</div>' +
      '<div class="empty-title">' + esc(title) + '</div>' +
      '<div class="empty-desc">' + esc(desc) + '</div>' +
      action +
      '</div>';
  }

  function errorHtml(message) {
    return '<div class="empty">' +
      '<div class="empty-icon error">' + icon('alert') + '</div>' +
      '<div class="empty-title">无法读取启动项</div>' +
      '<div class="empty-desc">' + esc(message || '请重新扫描后再试。') + '</div>' +
      '<button class="btn" data-action="rescan">' + icon('refresh') + '刷新重试</button>' +
      '</div>';
  }

  function sourceRank(source) {
    var order = { registry: 0, folder: 1, task: 2, service: 3 };
    return Object.prototype.hasOwnProperty.call(order, source) ? order[source] : 9;
  }

  function compareItems(a, b) {
    if (ui.sort === 'name') {
      return a.name.localeCompare(b.name, 'zh-Hans-CN');
    }
    if (ui.sort === 'scope') {
      return (scopeMeta(a.scope).rank - scopeMeta(b.scope).rank) || a.name.localeCompare(b.name, 'zh-Hans-CN');
    }
    return (sourceRank(a.source) - sourceRank(b.source)) || a.name.localeCompare(b.name, 'zh-Hans-CN');
  }

  function filteredItems() {
    var query = ui.query.trim().toLowerCase();
    return ui.items.filter(function (item) {
      if (!itemInCategory(item)) return false;
      if (ui.filter === 'enabled' && !item.enabled) return false;
      if (ui.filter === 'disabled' && item.enabled) return false;
      if (!query) return true;
      var haystack = [
        item.name, item.publisher, item.command, item.location, item.subLocation,
        sourceMeta(item.source).label, scopeMeta(item.scope).label, exeName(item)
      ].join(' ').toLowerCase();
      return haystack.indexOf(query) !== -1;
    }).sort(compareItems);
  }

  function switchHtml(item) {
    var pending = isItemPending(item.id);
    var disabled = item.protected || pending;
    var cls = 'switch' + (item.enabled ? ' on' : '') + (pending ? ' pending' : '');
    var title = item.protected ? '系统保护项，无法修改' : (pending ? '正在应用更改…' : '');
    var dataAttr = (!item.protected && !pending) ? ' data-toggle-item="' + esc(item.id) + '"' : '';
    return '<button type="button" class="' + cls + '" role="switch" aria-checked="' + String(!!item.enabled) + '"' +
      ' aria-label="' + (item.enabled ? '关闭 ' : '开启 ') + esc(item.name) + ' 的自启动"' +
      (pending ? ' aria-busy="true"' : '') +
      (disabled ? ' disabled' : '') +
      (title ? ' title="' + esc(title) + '"' : '') +
      dataAttr + '><span class="knob"></span></button>';
  }

  function rowHtml(item) {
    var pending = isItemPending(item.id);
    var selected = item.id === ui.selectedId;
    var source = sourceMeta(item.source);
    var scope = scopeMeta(item.scope);
    var publisher = item.publisher ? esc(item.publisher) + ' · ' : '';
    var managed = (!item.enabled && item.managedByApp) ? '<span class="managed-tag">· 由本工具禁用</span>' : '';
    var lockChip = item.protected
      ? '<span class="lock-chip' + (item.canOverride ? ' overridable' : '') + '" title="' +
          (item.canOverride ? '发布者或程序路径未验证，可在详情中允许管理' : '系统保护项，无法修改') + '">' +
          icon(item.canOverride ? 'alert' : 'lock') + (item.canOverride ? '未验证' : '受保护') + '</span>'
      : '';
    var userChip = item.userAllowed ? '<span class="user-chip">' + icon('check') + '已允许</span>' : '';
    var permChip = (item.requiresAdmin && !item.protected) ? '<span class="perm-chip">' + icon('shield') + '管理员</span>' : '';
    var meta = publisher + esc(exeName(item)) + managed;

    return '<div class="row' + (item.enabled ? '' : ' is-disabled') + (selected ? ' is-selected' : '') + (pending ? ' is-pending' : '') + '"' +
      ' data-id="' + esc(item.id) + '" tabindex="0" role="button"' +
      ' aria-label="查看 ' + esc(item.name) + ' 的详情，' + (item.enabled ? '已启用' : '已禁用') + '">' +
      '<div class="cell-main">' + appIconHtml(item.name, item.icon) +
        '<div class="cell-main-text">' +
          '<div class="name-row"><span class="app-name">' + esc(item.name) + '</span>' + lockChip + userChip + permChip + '</div>' +
          '<div class="app-meta">' + meta + '</div>' +
        '</div>' +
      '</div>' +
      '<div class="cell-source">' + sourceChip(item.source) + '</div>' +
      '<div class="cell-scope"><span class="scope-text' + (item.scope === 'system' ? ' scope-system' : '') + '">' + esc(scope.label) + '</span></div>' +
      '<div class="cell-actions">' +
        (pending ? '<span class="spinner" aria-hidden="true"></span>' : '') +
        switchHtml(item) + icon('chevron-right', 'chev') +
      '</div>' +
    '</div>';
  }

  function renderList() {
    var active = document.activeElement;
    var focusRowId = null;
    var focusSwitch = false;
    if (active && el.startupList.contains(active)) {
      var focusRow = active.closest ? active.closest('.row[data-id]') : null;
      if (focusRow) {
        focusRowId = focusRow.dataset.id;
        focusSwitch = active.classList.contains('switch');
      }
    }

    if (ui.loading) {
      el.startupList.innerHTML = skeletonHtml();
      return;
    }
    if (ui.loadError && !ui.items.length) {
      el.startupList.innerHTML = errorHtml(ui.loadError);
      return;
    }
    var visible = filteredItems();
    el.startupList.innerHTML = visible.length ? visible.map(rowHtml).join('') : emptyHtml();

    if (focusRowId) {
      var nextRow = $('.row[data-id="' + focusRowId + '"]', el.startupList);
      if (nextRow) {
        var target = focusSwitch ? nextRow.querySelector('.switch') : nextRow;
        if (target && !target.disabled) target.focus({ preventScroll: true });
        else nextRow.focus({ preventScroll: true });
      }
    }
  }

  /* ---------- Rendering: detail ---------- */
  function howCardHtml(item) {
    if (item.protected) {
      var overridable = !!item.canOverride;
      var reason = item.protectionReason || '该项目由 Windows 或硬件驱动管理，暂不支持修改。';
      if (overridable) {
        reason += ' 如确认信任该程序，可以在下方允许管理。';
      }
      return '<div class="how-card protected' + (overridable ? ' overridable' : '') + '">' +
        '<div class="how-head">' + icon(overridable ? 'alert' : 'lock') + (overridable ? '需要额外确认' : '系统保护项') + '</div>' +
        '<p class="how-desc">' + esc(reason) + '</p>' +
        '</div>';
    }
    var mech = mechanismInfo(item);
    var steps = ['捕获原始状态', '写入备份', '应用系统修改', '重新读取验证'];
    var stepsHtml = steps.map(function (step, index) {
      return '<span class="step"><b>' + (index + 1) + '</b>' + esc(step) + '</span>' +
        (index < steps.length - 1 ? '<span class="step-arrow">›</span>' : '');
    }).join('');
    var desc = item.isPackagedService
      ? '这是 MSIX 应用包服务，Windows 的服务接口拒绝修改。管理器会先备份，再通过管理员权限写入服务启动注册表值（手动），重启后生效；应用更新可能重置该值。'
      : mech.desc;
    return '<div class="how-card">' +
      '<div class="how-head">' + icon('shield') + '真实生效方式</div>' +
      '<p class="how-desc">' + esc(desc) + '</p>' +
      '<div class="steps">' + stepsHtml + '</div>' +
      '<div class="how-foot">修改前自动备份；应用后重新读取系统状态验证，验证失败会自动回滚。</div>' +
      '</div>';
  }

  function renderDetail() {
    if (!ui.selectedId) return;
    var item = findItem(ui.selectedId);
    if (!item) {
      ui.selectedId = null;
      el.detailPanel.classList.remove('open');
      el.detailPanel.setAttribute('aria-hidden', 'true');
      return;
    }

    var pending = isItemPending(item.id);
    var isElevated = !!(ui.snapshot && ui.snapshot.isElevated);
    var scope = scopeMeta(item.scope);
    var mech = mechanismInfo(item);
    var source = sourceMeta(item.source);
    var activeBackup = activeBackupForItem(item.id);
    var lockChip = item.protected
      ? '<span class="lock-chip' + (item.canOverride ? ' overridable' : '') + '">' +
          icon(item.canOverride ? 'alert' : 'lock') + (item.canOverride ? '未验证' : '受保护') + '</span>'
      : '';
    var userChip = item.userAllowed ? '<span class="user-chip">' + icon('check') + '已允许</span>' : '';
    var packageChip = item.isPackagedService ? '<span class="lock-chip packaged">应用包</span>' : '';
    var permChip = (item.requiresAdmin && !item.protected) ? '<span class="perm-chip">' + icon('shield') + '管理员</span>' : '';

    var toggleSub = item.source === 'service'
      ? 'Windows 启动时自动运行'
      : (item.source === 'task' ? '由任务计划程序按触发器运行' : '登录 Windows 时自动运行');
    if (pending) toggleSub = '正在应用更改，请稍候…';
    else if (item.protected) {
      toggleSub = item.canOverride
        ? '发布者或程序路径未验证，默认受保护'
        : '此项目受系统保护，无法修改';
    } else if (item.userAllowed) toggleSub = '你已允许管理此未验证项';
    else if (!item.enabled && item.managedByApp) toggleSub = '已由本工具禁用，原始配置保存在恢复中心';
    else if (!item.enabled) toggleSub = '已禁用，不会自动启动';

    var adminNote = '';
    if (item.requiresAdmin && !item.protected) {
      var noteText = isElevated
        ? '此项目需要管理员权限；当前进程已提升，修改时不会再次请求 UAC。'
        : '此项目需要管理员权限。修改时会通过一次 UAC 确认；取消不会做任何更改。';
      adminNote = '<div class="admin-note">' + icon('alert') + '<span>' + esc(noteText) + '</span></div>';
    }

    var userNote = item.userAllowed
      ? '<div class="admin-note user-note">' + icon('info') + '<span>你已允许管理此未验证项。修改前仍会自动备份并验证；可以随时恢复系统保护。</span></div>'
      : '';
    var packagedNote = item.isPackagedService
      ? '<div class="admin-note packaged-note">' + icon('info') +
          '<span>这是 MSIX 应用包服务。Windows 的服务接口不允许修改其启动类型；关闭时会通过管理员权限写入服务启动注册表值，重启后生效，应用更新可能重置该设置。</span></div>'
      : '';

    var footnote;
    if (item.protected) {
      footnote = icon(item.canOverride ? 'alert' : 'lock') + '<span>' +
        (item.canOverride
          ? '未验证项默认锁定；允许管理后，修改仍会先备份并验证。'
          : '系统保护项不可修改，仅展示启动配置。') + '</span>';
    } else if (item.userAllowed) {
      footnote = icon('check') + '<span>已允许管理未验证项；每次修改仍会备份并在验证成功后报告成功。</span>';
    } else {
      footnote = icon('shield') + '<span>修改前自动备份；仅在系统状态验证成功后才会报告成功。</span>';
    }

    var allowButton = item.protected && item.canOverride && !pending
      ? '<button class="btn" data-action="allow-unverified">' + icon('shield') + '允许管理…</button>'
      : '';
    var relockButton = item.userAllowed && !pending
      ? '<button class="btn ghost" data-action="relock-item">' + icon('lock') + '恢复系统保护</button>'
      : '';

    var restoreButton = '';
    if (!item.enabled && !item.protected) {
      restoreButton = '<button class="btn primary" data-action="restore-detail"' + (pending ? ' disabled' : '') + '>' +
        (pending
          ? '<span class="spinner" aria-hidden="true"></span>正在应用…'
          : icon('power') + (activeBackup ? '从备份恢复' : '恢复启动')) +
        '</button>';
    }

    el.detailContent.innerHTML =
      '<div class="d-head">' + appIconHtml(item.name, item.icon, 'lg') +
        '<div>' +
          '<h2 class="d-title">' + esc(item.name) + '</h2>' +
          '<div class="d-sub ' + (item.enabled ? '' : 'off') + '"><span class="status-dot"></span>' +
            '<span>' + (item.enabled ? '已启用' : '已禁用') + ' · ' + esc(item.publisher || '未知发布者') + '</span>' +
            ((lockChip || userChip || packageChip || permChip) ? '<span class="d-head-chips">' + lockChip + userChip + packageChip + permChip + '</span>' : '') +
          '</div>' +
        '</div>' +
      '</div>' +
      '<div class="d-toggle">' +
        '<div class="d-toggle-text">' +
          '<div class="d-toggle-title">开机自动启动</div>' +
          '<div class="d-toggle-sub">' + esc(toggleSub) + '</div>' +
        '</div>' + switchHtml(item) +
      '</div>' +
      '<div class="d-label">启动来源</div>' +
      '<div class="d-source-row">' + sourceChip(item.source) + '<span class="d-source-note">' + esc(source.note) + '</span></div>' +
      '<div class="d-label">系统位置</div>' +
      '<div class="code-box">' + esc(item.location || '—') +
        (item.subLocation ? '<div class="code-sub">' + esc(item.subLocation) + '</div>' : '') +
      '</div>' +
      '<div class="d-label">启动命令</div>' +
      '<div class="code-box">' + esc(item.command || '—') + '</div>' +
      '<div class="d-facts">' +
        '<div class="d-fact"><div class="d-fact-label">启动范围</div><div class="d-fact-value">' + esc(scope.label) + '</div></div>' +
        '<div class="d-fact"><div class="d-fact-label">所需权限</div><div class="d-fact-value">' + (item.requiresAdmin ? '管理员（UAC）' : '无需管理员') + '</div></div>' +
        '<div class="d-fact"><div class="d-fact-label">生效机制</div><div class="d-fact-value">' + esc(mech.label) + '</div></div>' +
        '<div class="d-fact"><div class="d-fact-label">上次修改</div><div class="d-fact-value">' + esc(formatDateTime(item.lastModified)) + '</div></div>' +
      '</div>' +
      '<div style="height:16px"></div>' +
      howCardHtml(item) + packagedNote + adminNote + userNote +
      '<div class="d-actions">' +
        '<button class="btn" data-action="locate">' + icon('open') + esc(LOCATE_LABEL[item.source] || '打开所在位置') + '</button>' +
        '<button class="btn ghost" data-action="copy-location">' + icon('copy') + '复制位置</button>' +
        allowButton +
        relockButton +
        restoreButton +
      '</div>' +
      '<div class="d-footnote">' + footnote + '</div>';
  }

  function openDetail(id) {
    var item = findItem(id);
    if (!item) return;
    ui.selectedId = id;
    detailTriggerId = id;
    renderList();
    renderDetail();
    el.detailPanel.classList.add('open');
    el.detailPanel.setAttribute('aria-hidden', 'false');
    el.detailContent.scrollTop = 0;
    requestAnimationFrame(function () {
      if (el.detailPanel.classList.contains('open')) el.detailPanel.focus({ preventScroll: true });
    });
  }

  function closeDetail(restoreFocus) {
    var wasOpen = el.detailPanel.classList.contains('open');
    if (!ui.selectedId && !wasOpen) return;
    ui.selectedId = null;
    el.detailPanel.classList.remove('open');
    el.detailPanel.setAttribute('aria-hidden', 'true');
    if (ui.page === 'startup') renderList();
    if (restoreFocus !== false) {
      var row = detailTriggerId ? $('.row[data-id="' + detailTriggerId + '"]', el.startupList) : null;
      detailTriggerId = null;
      if (row) row.focus();
      else if (ui.page === 'startup') el.searchInput.focus();
    } else if (el.detailPanel.contains(document.activeElement)) {
      document.activeElement.blur();
    }
  }

  /* ---------- Rendering: restore center ---------- */
  function backupStatusChip(status) {
    var labels = { active: '待恢复', restored: '已恢复', failed: '失败' };
    var label = labels[status] || String(status || '未知');
    return '<span class="backup-status ' + esc(status || '') + '">' + esc(label) + '</span>';
  }

  function backupRowHtml(backup, isHistory) {
    var pending = !isHistory && (isBackupPending(backup.id) || isItemPending(backup.itemId));
    var name = backup.displayName || '未命名启动项';
    var source = sourceMeta(backup.source);
    var scope = scopeMeta(backup.scope);
    var permChip = backup.requiresAdmin ? '<span class="perm-chip">' + icon('shield') + '管理员</span>' : '';
    var restoredText = backup.restoredAt ? ' · 恢复于 ' + formatDateTime(backup.restoredAt) : '';
    var error = backup.error
      ? '<div class="backup-error">' + icon('alert') + '<span>' + esc(backup.error) + '</span></div>'
      : '';
    var summary = backup.summary
      ? '<div class="backup-summary">' + esc(backup.summary) + '</div>'
      : '';
    var actions = '';
    if (!isHistory) {
      actions = '<div class="backup-actions">' +
        '<button class="btn small" data-action="restore-backup" data-backup-id="' + esc(backup.id) + '"' + (pending ? ' disabled' : '') + '>' +
          (pending ? '<span class="spinner" aria-hidden="true"></span>恢复中…' : icon('undo') + '恢复') +
        '</button></div>';
    }

    return '<div class="backup-row' + (isHistory ? ' is-history' : '') + '">' +
      appIconHtml(name, null) +
      '<div class="backup-main">' +
        '<div class="backup-name">' + esc(name) + backupStatusChip(backup.status) + permChip + '</div>' +
        '<div class="backup-meta">' + icon('history') + '备份于 ' + esc(formatDateTime(backup.createdAt)) +
          ' · ' + esc(source.label) + ' · ' + esc(scope.label) + restoredText + '</div>' +
        '<div class="backup-path" title="' + esc(backup.location) + '">' + esc(backup.location || '—') + '</div>' +
        summary + error +
      '</div>' +
      actions +
    '</div>';
  }

  function renderBackupPage() {
    var active = activeBackups().slice().sort(function (a, b) {
      return new Date(b.createdAt) - new Date(a.createdAt);
    });
    var history = ui.backups.filter(function (backup) {
      return backup.status !== 'active';
    }).sort(function (a, b) {
      return new Date(b.createdAt) - new Date(a.createdAt);
    });

    var retention = ui.settings ? ui.settings.backupRetentionDays : null;
    var path = ui.system && ui.system.backupDirectory ? ui.system.backupDirectory : '—';
    el.backupInfoText.innerHTML = '修改前会保存原始系统配置' +
      (retention ? '，默认保留 ' + esc(String(retention)) + ' 天' : '') +
      '。备份位置：<span class="code-inline">' + esc(path) + '</span>';

    el.backupPageCount.textContent = String(active.length);

    if (ui.loading) {
      el.backupList.innerHTML = '<div class="empty"><div class="empty-title">正在读取备份…</div></div>';
    } else if (ui.loadError && !ui.snapshot) {
      el.backupList.innerHTML = '<div class="empty">' +
        '<div class="empty-icon error">' + icon('alert') + '</div>' +
        '<div class="empty-title">无法读取备份</div>' +
        '<div class="empty-desc">' + esc(ui.loadError) + '</div>' +
        '</div>';
    } else if (!active.length) {
      el.backupList.innerHTML = '<div class="empty">' +
        '<div class="empty-icon">' + icon('shield') + '</div>' +
        '<div class="empty-title">没有待恢复的项目</div>' +
        '<div class="empty-desc">当你禁用某个启动项时，原始配置会自动备份到这里，可随时恢复。</div>' +
        '</div>';
    } else {
      el.backupList.innerHTML = active.map(function (backup) {
        return backupRowHtml(backup, false);
      }).join('');
    }

    el.backupHistorySection.hidden = history.length === 0;
    el.backupHistoryCount.textContent = String(history.length);
    el.backupHistoryList.innerHTML = history.map(function (backup) {
      return backupRowHtml(backup, true);
    }).join('');
  }

  /* ---------- Rendering: settings ---------- */
  function settingSwitchHtml(key, label, value, busy) {
    var pending = isSettingPending(key);
    return '<button type="button" class="switch ' + (value ? 'on ' : '') + (pending ? 'pending' : '') + '"' +
      ' role="switch" aria-checked="' + String(value) + '"' +
      (pending ? ' aria-busy="true"' : '') +
      ' aria-label="' + esc(label) + '"' +
      ' data-setting="' + esc(key) + '"' +
      (busy ? ' disabled title="正在保存设置"' : '') +
      '><span class="knob"></span></button>';
  }

  function settingRowHtml(key, title, desc, value, busy) {
    return '<div class="settings-row' + (isSettingPending(key) ? ' is-saving' : '') + '">' +
      '<div class="settings-text">' +
        '<div class="settings-title">' + esc(title) + '</div>' +
        '<div class="settings-desc">' + esc(desc) + '</div>' +
      '</div>' +
      settingSwitchHtml(key, title, value, busy) +
      '</div>';
  }

  function folderRowHtml(title, desc, pathValue, action) {
    var hasPath = !!pathValue && pathValue !== '—';
    return '<div class="settings-row">' +
      '<div class="settings-text">' +
        '<div class="settings-title">' + esc(title) + '</div>' +
        '<div class="settings-desc">' + esc(desc) + '</div>' +
        '<span class="settings-path">' + esc(pathValue || '—') + '</span>' +
      '</div>' +
      '<button class="btn small" data-action="' + action + '"' + (hasPath ? '' : ' disabled') + '>' +
        icon('open') + '打开' +
      '</button>' +
      '</div>';
  }

  function renderSettings() {
    if (!ui.settings) {
      el.settingsContent.innerHTML = (ui.loadError && !ui.snapshot)
        ? '<div class="settings-loading">设置不可用：' + esc(ui.loadError) + '</div>'
        : '<div class="settings-loading">正在读取设置…</div>';
      return;
    }
    var settings = ui.settings;
    var busy = settingsBusy();
    var isElevated = !!(ui.snapshot && ui.snapshot.isElevated);
    var backupPath = ui.system && ui.system.backupDirectory ? ui.system.backupDirectory : '—';
    var logPath = ui.system && ui.system.logDirectory ? ui.system.logDirectory : '—';

    var fontScale = settings.uiFontScale || 1;
    var scaleOptions = FONT_SCALE_OPTIONS.slice();
    if (!scaleOptions.some(function (option) { return Math.abs(option.value - fontScale) < 0.001; })) {
      scaleOptions.push({ value: fontScale, label: Math.round(fontScale * 100) + '%' });
    }
    var scaleOptionsHtml = scaleOptions.map(function (option) {
      return '<option value="' + option.value + '"' +
        (Math.abs(option.value - fontScale) < 0.001 ? ' selected' : '') + '>' +
        esc(option.label) + '</option>';
    }).join('');

    var html = '';
    if (busy) {
      html += '<div class="settings-save-state"><span class="spinner" aria-hidden="true"></span><span>正在保存设置…</span></div>';
    }

    html += '<div class="settings-group-title">常规</div><div class="settings-card">' +
      settingRowHtml('startWithWindows', '随 Windows 登录启动本工具', '登录 Windows 时自动启动本工具。', settings.startWithWindows, busy) +
      settingRowHtml('minimizeToTray', '关闭窗口时最小化到托盘', '关闭主窗口时隐藏到系统托盘，不退出后台。', settings.minimizeToTray, busy) +
      '<div class="settings-row">' +
        '<div class="settings-text">' +
          '<div class="settings-title">界面字体大小</div>' +
          '<div class="settings-desc">调整列表、详情和设置界面的显示大小，立即生效。</div>' +
        '</div>' +
        '<div class="select-wrap">' +
          '<select class="settings-select" id="fontScaleSelect" aria-label="界面字体大小"' + (busy ? ' disabled' : '') + '>' +
            scaleOptionsHtml +
          '</select>' +
          icon('chevron-down') +
        '</div>' +
      '</div>' +
      '</div>';

    html += '<div class="settings-group-title">扫描与权限</div><div class="settings-card">' +
      '<div class="settings-row">' +
        '<div class="settings-text">' +
          '<div class="settings-title">当前运行权限</div>' +
          '<div class="settings-desc">' + esc(isElevated
            ? '当前进程已提升，修改系统级项目不会再次提示。'
            : '当前以标准用户运行；修改系统级项目时会通过一次 UAC 确认，取消不会做任何更改。') + '</div>' +
        '</div>' +
        (isElevated
          ? '<span class="meta-chip">' + icon('check') + '已提升</span>'
          : '<span class="meta-chip admin">' + icon('shield') + '需要 UAC</span>') +
      '</div>' +
      settingRowHtml('includeRegistry', '扫描注册表 Run 项', '包含当前用户与所有用户的注册表启动项。', settings.includeRegistry, busy) +
      settingRowHtml('includeStartupFolders', '扫描启动文件夹', '包含当前用户与所有用户的启动文件夹快捷方式。', settings.includeStartupFolders, busy) +
      settingRowHtml('includeScheduledTasks', '扫描计划任务', '包含登录时启动的计划任务；Microsoft 系统任务始终受保护。', settings.includeScheduledTasks, busy) +
      settingRowHtml('includeServices', '扫描系统服务', '包含自动启动的 Windows 服务；禁用会把启动类型改为手动。', settings.includeServices, busy) +
      '</div>';

    var retention = settings.backupRetentionDays;
    var options = RETENTION_OPTIONS.slice();
    if (options.indexOf(retention) === -1) options.push(retention);
    options.sort(function (a, b) { return a - b; });
    var optionsHtml = options.map(function (days) {
      return '<option value="' + days + '"' + (days === retention ? ' selected' : '') + '>' + days + ' 天</option>';
    }).join('');

    html += '<div class="settings-group-title">安全与备份</div><div class="settings-card">' +
      '<div class="settings-row">' +
        '<div class="settings-text">' +
          '<div class="settings-title">修改前自动备份</div>' +
          '<div class="settings-desc">安全保护功能，始终开启，无法关闭。备份保存原始系统配置。</div>' +
        '</div>' +
        '<button type="button" class="switch on" role="switch" aria-checked="true" disabled title="安全保护始终开启"><span class="knob"></span></button>' +
      '</div>' +
      '<div class="settings-row">' +
        '<div class="settings-text">' +
          '<div class="settings-title">备份保留时间</div>' +
          '<div class="settings-desc">超过保留期的备份会被自动清理。</div>' +
        '</div>' +
        '<div class="select-wrap">' +
          '<select class="settings-select" id="retentionSelect" aria-label="备份保留时间"' + (busy ? ' disabled' : '') + '>' + optionsHtml + '</select>' +
          icon('chevron-down') +
        '</div>' +
      '</div>' +
      folderRowHtml('备份位置', '保存每次修改前的原始系统配置。', backupPath, 'open-backup-folder') +
      folderRowHtml('日志位置', '应用运行日志的保存位置。', logPath, 'open-log-folder') +
      '</div>';

    html += '<div class="settings-group-title">关于</div><div class="settings-card">' +
      '<div class="settings-row">' +
        '<div class="settings-text">' +
          '<div class="settings-title">启动项管理器</div>' +
          '<div class="version-text">版本 1.1.0</div>' +
          '<div class="settings-desc">修改前自动备份，应用后重新读取系统状态验证；验证失败会自动回滚。</div>' +
        '</div>' +
      '</div>' +
      '</div>';

    el.settingsContent.innerHTML = html;
  }

  /* ---------- Toasts ---------- */
  function clearToastTimer(id) {
    var timer = toastTimers.get(id);
    if (timer) {
      clearTimeout(timer);
      toastTimers.delete(id);
    }
  }

  function removeToastNode(node, immediate) {
    if (!node || !node.isConnected) return;
    clearToastTimer(node.dataset.toastId);
    if (immediate) {
      node.remove();
      return;
    }
    node.classList.add('leaving');
    setTimeout(function () { if (node.isConnected) node.remove(); }, 180);
  }

  function dismissToast(id) {
    if (!el.toastWrap) return;
    $$('[data-toast-id]', el.toastWrap).forEach(function (node) {
      if (node.dataset.toastId === id) removeToastNode(node, false);
    });
  }

  function showToast(options) {
    options = options || {};
    var type = options.type || 'success';
    var id = options.id || ('toast-' + (++toastSeq));
    var iconName = type === 'success' ? 'check'
      : (type === 'error' || type === 'warning') ? 'alert'
        : (options.busy ? 'refresh' : 'info');

    $$('[data-toast-id]', el.toastWrap).forEach(function (node) {
      if (node.dataset.toastId === id) removeToastNode(node, true);
    });

    var node = document.createElement('div');
    node.className = 'toast ' + type + (options.busy ? ' busy' : '');
    node.dataset.toastId = id;
    node.setAttribute('role', (type === 'error' || type === 'warning') ? 'alert' : 'status');
    node.innerHTML = '<svg class="toast-icon" aria-hidden="true"><use href="#i-' + iconName + '"/></svg>' +
      '<div class="toast-body">' +
        '<div class="toast-title">' + esc(options.title || '') + '</div>' +
        (options.msg ? '<div class="toast-msg">' + esc(options.msg) + '</div>' : '') +
      '</div>' +
      (options.actionLabel ? '<button type="button" class="toast-action">' + esc(options.actionLabel) + '</button>' : '') +
      '<button type="button" class="toast-close" aria-label="关闭通知">' + icon('close') + '</button>';
    el.toastWrap.appendChild(node);

    while (el.toastWrap.children.length > 3) {
      var first = null;
      for (var i = 0; i < el.toastWrap.children.length; i++) {
        var child = el.toastWrap.children[i];
        if (!child.classList.contains('busy')) { first = child; break; }
      }
      if (!first) break;
      removeToastNode(first, true);
    }

    var action = $('.toast-action', node);
    if (action) {
      action.addEventListener('click', function () {
        removeToastNode(node, false);
        if (typeof options.onAction === 'function') options.onAction();
      });
    }
    $('.toast-close', node).addEventListener('click', function () { removeToastNode(node, false); });

    var timeout = options.timeout;
    if (timeout !== 0) {
      if (typeof timeout !== 'number') {
        timeout = options.actionLabel ? 8000 : (type === 'error' || type === 'warning') ? 7000 : 4800;
      }
      toastTimers.set(id, setTimeout(function () {
        toastTimers.delete(id);
        removeToastNode(node, false);
      }, timeout));
    }
    return id;
  }

  function toastForError(error, fallbackTitle) {
    var message = (error && error.message) || '操作未完成，请稍后重试。';
    var canceled = !!(error && (error.elevationCanceled || /UAC|取消/.test(message)));
    return {
      type: canceled ? 'warning' : 'error',
      title: canceled ? '操作已取消' : fallbackTitle,
      msg: message
    };
  }

  function updateBusyToast() {
    var ops = mutationOps();
    if (!ops.length) {
      dismissToast('busy');
      return;
    }
    var ids = {};
    var bulk = false;
    ops.forEach(function (op) {
      if (op.kind === 'bulk' || (op.itemIds && op.itemIds.length > 1)) bulk = true;
      (op.itemIds || []).forEach(function (id) { ids[id] = true; });
    });
    var count = Object.keys(ids).length;
    var title = bulk && count ? '正在应用更改（' + count + ' 项）' : '正在应用更改';
    var msg = Bridge && Bridge.mode === 'demo'
      ? '演示模式：正在更新本地演示状态。'
      : '如出现 Windows 权限提示，请允许；取消不会做任何更改。';
    showToast({ id: 'busy', type: 'info', busy: true, title: title, msg: msg, timeout: 0 });
  }

  /* ---------- Command flows ---------- */
  function startOperation(name, payload, meta) {
    var promise = Bridge.command(name, payload);
    var op = {
      kind: meta.kind,
      itemIds: meta.itemIds || [],
      backupId: meta.backupId || null,
      key: meta.key || null,
      previousSettings: meta.previousSettings || null
    };
    ui.ops.set(promise.requestId, op);
    refreshPendingUi();
    return promise;
  }

  function finishOperation(promise) {
    ui.ops.delete(promise.requestId);
    refreshPendingUi();
  }

  function refreshPendingUi() {
    renderHeader();
    renderScanState();
    renderList();
    if (ui.selectedId) renderDetail();
    renderBackupPage();
    updateBusyToast();
  }

  function submitItemChange(ids, enabled, options) {
    options = options || {};
    var bulk = !!options.bulk || ids.length > 1;
    var promise = startOperation('setEnabled', { itemIds: ids.slice(), enabled: enabled }, {
      kind: bulk ? 'bulk' : 'toggle',
      itemIds: ids.slice()
    });

    promise.then(function (result) {
      finishOperation(promise);
      var title;
      if (bulk) {
        title = enabled ? '已启用 ' + ids.length + ' 个启动项' : '已禁用 ' + ids.length + ' 个启动项';
      } else {
        var itemName = options.singleName || '启动项';
        title = enabled ? '已恢复「' + itemName + '」的自启动' : '已关闭「' + itemName + '」的自启动';
      }
      var fallback = enabled
        ? '启动配置已写回原位置。'
        : '原始配置已备份，可在恢复中心还原。';
      showToast({
        type: 'success',
        title: title,
        msg: (result && result.message) || fallback,
        actionLabel: '撤销',
        onAction: function () {
          submitUndo(ids.slice(), !enabled, { bulk: bulk, singleName: options.singleName });
        }
      });
    }).catch(function (error) {
      finishOperation(promise);
      var toast = toastForError(error, bulk ? '批量修改未完成' : '未能修改启动项');
      showToast(toast);
    });
  }

  function submitUndo(ids, enabled, options) {
    options = options || {};
    var bulk = !!options.bulk || ids.length > 1;
    var promise = startOperation('setEnabled', { itemIds: ids.slice(), enabled: enabled }, {
      kind: bulk ? 'bulk' : 'toggle',
      itemIds: ids.slice()
    });

    promise.then(function (result) {
      finishOperation(promise);
      showToast({
        type: 'success',
        title: bulk ? '已撤销全部修改' : '已撤销修改',
        msg: (result && result.message) || (enabled ? '启动项已重新启用。' : '启动项已重新关闭。')
      });
    }).catch(function (error) {
      finishOperation(promise);
      showToast(toastForError(error, '撤销未完成'));
    });
  }

  function toggleItem(id) {
    var item = findItem(id);
    if (!item || item.protected || ui.loading || ui.scanPending) return;
    if (isItemPending(id)) return;
    submitItemChange([id], !item.enabled, { singleName: item.name });
  }

  function restoreBackup(id) {
    var backup = findBackup(id);
    if (!backup || backup.status !== 'active') return;
    if (isBackupPending(id) || isItemPending(backup.itemId)) return;

    var promise = startOperation('restoreBackup', { backupId: id }, {
      kind: 'restore',
      backupId: id,
      itemIds: backup.itemId ? [backup.itemId] : []
    });

    promise.then(function (result) {
      finishOperation(promise);
      var item = backup.itemId ? findItem(backup.itemId) : null;
      var canRedisable = item && !item.protected;
      showToast({
        type: 'success',
        title: '已恢复「' + (backup.displayName || '启动项') + '」',
        msg: (result && result.message) || '原始配置已写回系统。',
        actionLabel: canRedisable ? '重新禁用' : null,
        onAction: canRedisable ? function () {
          submitItemChange([item.id], false, { singleName: item.name });
        } : null
      });
    }).catch(function (error) {
      finishOperation(promise);
      showToast(toastForError(error, '恢复未完成'));
    });
  }

  function restoreFromDetail() {
    var item = findItem(ui.selectedId);
    if (!item || item.enabled || item.protected || isItemPending(item.id)) return;
    var backup = activeBackupForItem(item.id);
    if (backup) {
      restoreBackup(backup.id);
    } else {
      submitItemChange([item.id], true, { singleName: item.name });
    }
  }

  function requestRefresh() {
    if (ui.scanPending) return;
    ui.scanPending = true;
    ui.userScan = true;
    if (!ui.snapshot) {
      ui.loading = true;
      ui.loadError = null;
      renderAll();
    } else {
      renderScanState();
      renderSidebar();
    }
    var promise = Bridge.command('refresh');
    promise.then(function () {
      ui.scanPending = false;
      renderScanState();
      renderSidebar();
    }).catch(function (error) {
      ui.scanPending = false;
      ui.userScan = false;
      if (!ui.snapshot) {
        ui.loading = false;
        ui.loadError = error && error.message ? error.message : '扫描失败。';
      }
      renderAll();
      showToast(toastForError(error, '扫描失败'));
    });
  }

  function runHostAction(command, payload, title) {
    Bridge.command(command, payload).then(function (result) {
      showToast({
        type: 'info',
        title: title,
        msg: (result && result.message) || '请求已发送。'
      });
    }).catch(function (error) {
      showToast(toastForError(error, '操作未完成'));
    });
  }

  /* ---------- Dialog ---------- */
  function dialogOpen() {
    return el.dialogBackdrop.classList.contains('open');
  }

  function openDialog() {
    if (dialogOpen()) return;
    el.dialogBackdrop.classList.add('open');
    el.dialogBackdrop.setAttribute('aria-hidden', 'false');
    document.body.classList.add('dialog-open');
    dialogReturnFocus = document.activeElement;
    setTimeout(function () {
      if (dialogOpen()) el.dialogCancel.focus();
    }, 30);
  }

  function closeDialog(restoreFocus) {
    if (!dialogOpen()) return;
    el.dialogBackdrop.classList.remove('open');
    el.dialogBackdrop.setAttribute('aria-hidden', 'true');
    document.body.classList.remove('dialog-open');
    ui.dialog = null;
    if (restoreFocus !== false && dialogReturnFocus && dialogReturnFocus.isConnected) {
      dialogReturnFocus.focus();
    }
    dialogReturnFocus = null;
  }

  function openBulkDialog() {
    if (ui.loading || ui.scanPending) return;
    var categoryLabel = (CATEGORY_META[ui.category] || CATEGORY_META.startup).label;
    var targets = ui.items.filter(function (item) {
      return itemInCategory(item) && item.enabled && !item.protected;
    });
    if (!targets.length) return;
    var counts = getCounts();
    var isElevated = !!(ui.snapshot && ui.snapshot.isElevated);
    var adminTargets = targets.filter(function (item) { return item.requiresAdmin; }).length;

    var noteParts = [];
    if (counts.protectedCount > 0) {
      noteParts.push('系统保护项会自动跳过。');
    }
    if (adminTargets > 0) {
      noteParts.push(isElevated
        ? '其中 ' + adminTargets + ' 项需要管理员权限，当前进程已提升。'
        : '其中 ' + adminTargets + ' 项需要管理员权限，将通过一次 UAC 提示处理。');
    }
    noteParts.push('所有修改会先备份原始配置，应用后会重新读取系统状态验证。');

    ui.dialog = { type: 'bulk', ids: targets.map(function (item) { return item.id; }) };
    el.dialogTitle.textContent = '禁用所有可管理的' + categoryLabel + '？';
    el.dialogDesc.textContent = '将关闭当前分类中的 ' + targets.length + ' 个已启用项。';
    el.dialogNote.textContent = noteParts.join('');
    el.dialogConfirm.textContent = '禁用 ' + targets.length + ' 项';
    el.dialogConfirm.dataset.action = 'confirm-bulk';
    openDialog();
  }

  function confirmBulk() {
    if (!ui.dialog || ui.dialog.type !== 'bulk') {
      closeDialog();
      return;
    }
    var ids = ui.dialog.ids.slice();
    closeDialog(true);
    if (!ids.length) return;
    submitItemChange(ids, false, { bulk: true });
  }

  function openUnlockDialog(item) {
    if (!item || !item.protected || !item.canOverride) return;
    ui.dialog = { type: 'unlock', id: item.id };
    el.dialogTitle.textContent = '允许管理未验证项？';
    el.dialogDesc.textContent = '无法确认「' + item.name + '」的发布者或程序路径。';
    el.dialogNote.textContent =
      '只有在确认信任该程序时才允许管理。允许后，修改前仍会备份并重新读取系统状态验证；如果它是服务，修改时仍会通过 UAC。你可以随时恢复系统保护。' +
      (item.isPackagedService
        ? ' 该服务属于 MSIX 应用包，修改会使用管理员注册表启动值，重启后生效，应用更新可能重置。'
        : '');
    el.dialogConfirm.textContent = '允许管理';
    el.dialogConfirm.dataset.action = 'confirm-unlock';
    openDialog();
  }

  function confirmUnlock() {
    if (!ui.dialog || ui.dialog.type !== 'unlock') {
      closeDialog();
      return;
    }
    var id = ui.dialog.id;
    closeDialog(true);
    setUserOverride(id, true);
  }

  function setUserOverride(id, allowed) {
    var item = findItem(id);
    if (!item) return;
    var promise = startOperation('setUserOverride', { itemId: id, allowed: !!allowed }, {
      kind: 'policy',
      itemIds: [id]
    });

    promise.then(function (result) {
      finishOperation(promise);
      showToast({
        type: 'success',
        title: allowed ? '已允许管理「' + item.name + '」' : '已恢复「' + item.name + '」的系统保护',
        msg: (result && result.message) ||
          (allowed ? '修改前仍会备份并验证；服务修改仍需 UAC。' : '该项目重新处于系统保护锁定状态。')
      });
    }).catch(function (error) {
      finishOperation(promise);
      showToast(toastForError(error, '未能更新项目保护状态'));
    });
  }

  function trapDialogFocus(event) {
    var focusables = $$('button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])', el.dialogBackdrop)
      .filter(function (node) {
        return node instanceof HTMLElement && !node.disabled && node.offsetParent !== null;
      });
    if (!focusables.length) return;
    var first = focusables[0];
    var last = focusables[focusables.length - 1];
    var active = document.activeElement;
    var inside = el.dialogBackdrop.contains(active);
    if (event.shiftKey) {
      if (!inside || active === first) {
        event.preventDefault();
        last.focus();
      }
    } else if (!inside || active === last) {
      event.preventDefault();
      first.focus();
    }
  }

  /* ---------- Settings flows ---------- */
  function commitSettings(next, previous, key) {
    if (settingsBusy()) return;
    var promise = startOperation('saveSettings', { settings: next }, {
      kind: 'settings',
      key: key,
      previousSettings: previous
    });
    ui.settings = next;
    applyFontScalePreview(next.uiFontScale);
    renderSettings();
    updateBusyToast();

    promise.then(function () {
      ui.ops.delete(promise.requestId);
      renderSettings();
    }).catch(function (error) {
      ui.ops.delete(promise.requestId);
      ui.settings = previous;
      applyFontScalePreview(previous.uiFontScale);
      renderSettings();
      showToast(toastForError(error, '设置未保存'));
    });
  }

  function toggleSetting(key) {
    if (!ui.settings || settingsBusy()) return;
    if (BOOLEAN_SETTINGS.indexOf(key) === -1) return;
    var previous = cloneSettings(ui.settings);
    var next = cloneSettings(ui.settings);
    next[key] = !previous[key];
    commitSettings(next, previous, key);
  }

  function changeRetention(value) {
    if (!ui.settings || settingsBusy()) { renderSettings(); return; }
    var days = Number(value);
    if (!isFinite(days) || days < 1 || days > 3650) { renderSettings(); return; }
    var previous = cloneSettings(ui.settings);
    if (previous.backupRetentionDays === Math.round(days)) return;
    var next = cloneSettings(ui.settings);
    next.backupRetentionDays = Math.round(days);
    commitSettings(next, previous, 'backupRetentionDays');
  }

  function changeFontScale(value) {
    if (!ui.settings || settingsBusy()) { renderSettings(); return; }
    var scale = Number(value);
    if (!isFinite(scale) || scale < 0.8 || scale > 1.5) { renderSettings(); return; }
    var previous = cloneSettings(ui.settings);
    if (Math.abs(previous.uiFontScale - scale) < 0.001) return;
    var next = cloneSettings(ui.settings);
    next.uiFontScale = scale;
    commitSettings(next, previous, 'uiFontScale');
  }

  function applyFontScalePreview(scale) {
    if (window.StartupBridge && window.StartupBridge.isNative) return;
    var value = Number(scale);
    if (!isFinite(value)) value = 1;
    document.documentElement.style.zoom = String(Math.min(1.5, Math.max(0.8, value)));
  }

  /* ---------- Pages and actions ---------- */
  function switchPage(page) {
    if (page === ui.page) return;
    if (ui.selectedId) closeDetail(false);
    ui.page = page;
    $$('.nav-item[data-page]').forEach(function (button) {
      var active = button.dataset.page === page;
      button.classList.toggle('active', active);
      if (active) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    });
    $$('.page').forEach(function (section) {
      section.classList.toggle('hidden', section.id !== 'page-' + page);
    });
    el.content.scrollTop = 0;
    if (page === 'startup') renderList();
    if (page === 'backup') renderBackupPage();
    if (page === 'settings') renderSettings();
  }

  function resetFilters() {
    ui.filter = 'all';
    ui.query = '';
    el.searchInput.value = '';
    updateSearchUi();
    updateFilterUi();
    renderList();
    el.searchInput.focus();
  }

  function clearSearch() {
    ui.query = '';
    el.searchInput.value = '';
    updateSearchUi();
    renderList();
    el.searchInput.focus();
  }

  function handleAction(action, element) {
    switch (action) {
      case 'rescan':
        requestRefresh();
        break;
      case 'clear-search':
        clearSearch();
        break;
      case 'reset-filters':
        resetFilters();
        break;
      case 'dismiss-warnings':
        if (ui.snapshot && ui.snapshot.warnings) {
          ui.warningsDismissed = ui.snapshot.warnings.join('\u0000');
          renderWarnings();
        }
        break;
      case 'open-settings':
        switchPage('settings');
        break;
      case 'close-detail':
        closeDetail(true);
        break;
      case 'allow-unverified': {
        var allowItem = findItem(ui.selectedId);
        if (allowItem) openUnlockDialog(allowItem);
        break;
      }
      case 'relock-item':
        if (ui.selectedId) setUserOverride(ui.selectedId, false);
        break;
      case 'confirm-unlock':
        confirmUnlock();
        break;
      case 'locate': {
        var locateItem = findItem(ui.selectedId);
        if (locateItem) runHostAction('openItemLocation', { itemId: locateItem.id }, '打开系统位置');
        break;
      }
      case 'copy-location': {
        var copyItem = findItem(ui.selectedId);
        if (copyItem) runHostAction('copyLocation', { itemId: copyItem.id }, '复制位置');
        break;
      }
      case 'restore-detail':
        restoreFromDetail();
        break;
      case 'restore-backup': {
        var backupId = element ? element.dataset.backupId : null;
        if (backupId) restoreBackup(backupId);
        break;
      }
      case 'open-backup-folder':
        runHostAction('openBackupFolder', null, '备份文件夹');
        break;
      case 'open-log-folder':
        runHostAction('openLogFolder', null, '日志文件夹');
        break;
      case 'open-bulk-dialog':
        openBulkDialog();
        break;
      case 'close-dialog':
        closeDialog(true);
        break;
      case 'confirm-bulk':
        confirmBulk();
        break;
      default:
        break;
    }
  }

  /* ---------- Bridge handlers ---------- */
  function handleState(message) {
    var snapshot = message.snapshot || {};
    ui.snapshot = snapshot;
    ui.items = Array.isArray(snapshot.items) ? snapshot.items : [];
    ui.backups = Array.isArray(message.backups) ? message.backups : [];
    if (message.system) ui.system = message.system;
    if (message.settings) {
      if (!settingsBusy()) {
        ui.settings = normalizeSettings(message.settings);
      }
    } else if (!ui.settings) {
      ui.settings = normalizeSettings(null);
    }
    applyFontScalePreview(ui.settings ? ui.settings.uiFontScale : 1);
    ui.loading = false;
    ui.loadError = null;
    if (ui.selectedId && !findItem(ui.selectedId)) {
      ui.selectedId = null;
      el.detailPanel.classList.remove('open');
      el.detailPanel.setAttribute('aria-hidden', 'true');
    }
    renderAll();
  }

  function handleScanStarted() {
    ui.scanPending = true;
    renderSidebar();
    renderScanState();
  }

  function handleScanFinished(message) {
    ui.scanPending = false;
    var wasUserScan = ui.userScan;
    ui.userScan = false;
    if (message && message.ok === false) {
      showToast({ type: 'error', title: '扫描失败', msg: message.message || '请稍后重试。' });
    } else if (wasUserScan) {
      showToast({
        type: 'success',
        title: '扫描完成',
        msg: (message && message.message) || '启动项与备份状态已刷新。'
      });
    }
    renderScanState();
    renderSidebar();
  }

  function handleSettings(settings) {
    if (!settings) return;
    ui.settings = normalizeSettings(settings);
    applyFontScalePreview(ui.settings.uiFontScale);
    renderSettings();
  }

  function handleGlobalError(message) {
    var text = (message && message.message) || '发生未知错误。';
    if (!ui.snapshot) {
      ui.loading = false;
      ui.loadError = text;
      ui.scanPending = false;
      ui.userScan = false;
      renderAll();
    }
    showToast({ type: 'error', title: '操作未完成', msg: text });
  }

  function renderAll() {
    renderSidebar();
    renderHeader();
    renderWarnings();
    renderList();
    renderDetail();
    renderBackupPage();
    renderSettings();
    renderScanState();
    updateBusyToast();
  }

  /* ---------- Events ---------- */
  function onDocumentClick(event) {
    var target = event.target;
    if (!target || !target.closest) return;

    var navButton = target.closest('.nav-item[data-page]');
    if (navButton) {
      switchPage(navButton.dataset.page);
      return;
    }

    var categoryButton = target.closest('.category-tab[data-category]');
    if (categoryButton) {
      var category = categoryButton.dataset.category;
      if (!CATEGORY_META[category] || category === ui.category) return;
      if (ui.selectedId) closeDetail(false);
      ui.category = category;
      updateCategoryUi();
      renderHeader();
      renderList();
      if (el.content) el.content.scrollTop = 0;
      return;
    }

    var filterButton = target.closest('.segmented button[data-filter]');
    if (filterButton) {
      ui.filter = filterButton.dataset.filter;
      updateFilterUi();
      renderList();
      return;
    }

    var settingSwitch = target.closest('.switch[data-setting]');
    if (settingSwitch) {
      if (settingSwitch.disabled) return;
      toggleSetting(settingSwitch.dataset.setting);
      return;
    }

    var itemSwitch = target.closest('[data-toggle-item]');
    if (itemSwitch) {
      if (itemSwitch.disabled) return;
      toggleItem(itemSwitch.dataset.toggleItem);
      return;
    }

    var actionElement = target.closest('[data-action]');
    if (actionElement) {
      handleAction(actionElement.dataset.action, actionElement);
      return;
    }

    var row = target.closest('.row[data-id]');
    if (row) {
      openDetail(row.dataset.id);
      return;
    }

    if (target === el.dialogBackdrop) closeDialog(true);
  }

  function onDocumentKeyDown(event) {
    if (dialogOpen()) {
      if (event.key === 'Escape') {
        event.preventDefault();
        closeDialog(true);
        return;
      }
      if (event.key === 'Tab') {
        trapDialogFocus(event);
        return;
      }
      return;
    }

    if (event.key === 'Escape') {
      if (ui.selectedId) {
        event.preventDefault();
        closeDetail(true);
        return;
      }
      if (document.activeElement === el.searchInput) {
        if (el.searchInput.value) {
          clearSearch();
        } else {
          el.searchInput.blur();
        }
      }
      return;
    }

    if (event.key === '/' && ui.page === 'startup') {
      var tag = event.target && event.target.tagName ? event.target.tagName.toLowerCase() : '';
      if (tag !== 'input' && tag !== 'textarea' && tag !== 'select' && !(event.target && event.target.isContentEditable)) {
        event.preventDefault();
        el.searchInput.focus();
        return;
      }
    }

    if ((event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') && ui.page === 'startup') {
      var active = event.target;
      if (active && active.closest && active.closest('button, a, input, select, textarea')) return;
      var row = active && active.closest ? active.closest('.row[data-id]') : null;
      if (row) {
        event.preventDefault();
        openDetail(row.dataset.id);
      }
    }
  }

  function onDocumentChange(event) {
    var target = event.target;
    if (!target) return;
    if (target.id === 'sortSelect') {
      ui.sort = target.value;
      renderList();
      return;
    }
    if (target.id === 'retentionSelect') {
      changeRetention(target.value);
      return;
    }
    if (target.id === 'fontScaleSelect') {
      changeFontScale(target.value);
    }
  }

  function bindEvents() {
    document.addEventListener('click', onDocumentClick);
    document.addEventListener('keydown', onDocumentKeyDown);
    document.addEventListener('change', onDocumentChange);
    el.searchInput.addEventListener('input', function () {
      ui.query = el.searchInput.value;
      updateSearchUi();
      renderList();
    });
    el.dialogBackdrop.addEventListener('click', function (event) {
      if (event.target === el.dialogBackdrop) closeDialog(true);
    });
  }

  /* ---------- Init ---------- */
  function cacheElements() {
    el.navCount = $('#navCount');
    el.backupNavCount = $('#backupNavCount');
    el.scanDot = $('#scanDot');
    el.scanTitle = $('#scanTitle');
    el.lastScan = $('#lastScan');
    el.summaryText = $('#summaryText');
    el.metaChip = $('#metaChip');
    el.countAll = $('#countAll');
    el.countEnabled = $('#countEnabled');
    el.countDisabled = $('#countDisabled');
    el.categoryStartupCount = $('#categoryStartupCount');
    el.categoryServiceCount = $('#categoryServiceCount');
    el.categoryTaskCount = $('#categoryTaskCount');
    el.searchBox = $('#searchBox');
    el.searchInput = $('#searchInput');
    el.sortSelect = $('#sortSelect');
    el.bulkDisable = $('#bulkDisable');
    el.listCard = $('#listCard');
    el.startupList = $('#startupList');
    el.warningsBanner = $('#warningsBanner');
    el.warningsTitle = $('#warningsTitle');
    el.warningsList = $('#warningsList');
    el.detailPanel = $('#detailPanel');
    el.detailContent = $('#detailContent');
    el.backupInfoText = $('#backupInfoText');
    el.backupPageCount = $('#backupPageCount');
    el.backupList = $('#backupList');
    el.backupHistorySection = $('#backupHistorySection');
    el.backupHistoryCount = $('#backupHistoryCount');
    el.backupHistoryList = $('#backupHistoryList');
    el.settingsContent = $('#settingsContent');
    el.toastWrap = $('#toastWrap');
    el.dialogBackdrop = $('#dialogBackdrop');
    el.dialogTitle = $('#dialogTitle');
    el.dialogDesc = $('#dialogDesc');
    el.dialogNote = $('#dialogNote');
    el.dialogCancel = $('#dialogCancel');
    el.dialogConfirm = $('#dialogConfirm');
    el.content = $('#content');
  }

  function start() {
    cacheElements();
    bindEvents();
    updateCategoryUi();
    updateFilterUi();
    updateSearchUi();
    renderAll();

    if (!window.StartupBridge) {
      ui.loading = false;
      ui.loadError = '界面脚本未能连接宿主。请重新打开应用。';
      renderAll();
      return;
    }
    Bridge = window.StartupBridge;
    Bridge.init({
      onState: handleState,
      onScanStarted: handleScanStarted,
      onScanFinished: handleScanFinished,
      onSettings: handleSettings,
      onError: handleGlobalError
    });
  }

  /* Debug surface for host/tests; not used by the UI itself. */
  window.StartupManagerUi = {
    switchPage: function (page) { switchPage(page); },
    openDetail: function (id) { openDetail(id); },
    refresh: function () { requestRefresh(); },
    openBulkDialog: function () { openBulkDialog(); },
    getState: function () {
      return {
        mode: window.StartupBridge ? window.StartupBridge.mode : 'none',
        loading: ui.loading,
        page: ui.page,
        category: ui.category,
        selectedId: ui.selectedId,
        items: ui.items.length,
        backups: ui.backups.length,
        pendingOps: ui.ops.size
      };
    }
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
