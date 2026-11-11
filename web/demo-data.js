/*
 * StartupManager demo host.
 *
 * This file is only used when the page is NOT running inside WebView2
 * (no window.chrome.webview). It emulates the native host contract so the
 * production UI can be reviewed and automated in a normal browser.
 *
 * Demo mutations never touch the operating system. Every simulated command
 * follows the same order as the real host: fresh state first, commandResult
 * second. The native bridge never loads or calls this host.
 */
(function () {
  'use strict';

  /* ---------- Optional demo icons (real PNG data URIs) ---------- */
  var ICON_CLOUD = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAYAAABXAvmHAAAE7ElEQVR42tWaX0yTVxjGT5Yt8aaKwubcxljHAAXnBJUtzkFZtmS7WsxuvEKGc2wORUeABgvyr0MRFEfEIZUQNdEsAwR0ghOcCAKldJVS/hQolA0IUBoYRKG0fZbTftskK7S0hfq9yXvTq9/79jnvd855DiFPRViR9rmwSxP+PJEmhSfSiEMLx7UhF8fmQwpGjR8UjGLPjyPYc2EE7+cPY/f5IVO+l/eHOX8YRPA5NYJzBxCc24+dZ1XYeUaFHTl9CMruRVB2DwKzlNie1Y3tp7rxzskubMvsxLbMDrwtVGCrUIGADDkC0uXwT2/DltRH2JIq029OkU35nZDK/JKlp32TWnf4CCTPE0vxYZF2TVjRxD6eSNPME2l0oYXjCLk4hpCCUbgIHptTZPA7IYVfshS+ya16H4FE7iNoifI+LuZYgNd+yxNp/uSJNHgG4eEjkMBH0IK3jos13oniJO/EZs5/sjF3ng3w8E6k2azl8puiufzGFwij+WYWwYPLbwKX39jFTWjcTZgFq2MZPLgJjQav+IYLhJk2bIOHV3wDvOIalIQZlWyEh1dc/RPCzHk2wsMz9oGRuPAj5Sg8PGPrQNgM/9p390HYDP/qsd9A2AxvKoDN8K/E3ANhM/ymmFpzASsJH5D2CP6pKwO/6UgNyErA83I7EfuzGlfF46jpmkRt1ySut4yDXzqAsBw5fJMkToF/+fBdpgAnwb+bpcCpO8MYmJiDwYj/hdEIDE/OIa92CLuEUofhN0bTApwEH3q2E9Udk9BbIrdQyMO+KYSdljkEvzH6VxBndZ7CW0dfGE2qKezKkNgN/9KhOyDO0DyVjS2dt/RPFNYNgZvw0C74Fw9V0wIcX7BU8/aGZlqHj7KldsF7fFMF4gj81ow2xJaoYUfzF4Swst8ueI+vb4MsFz7opAIRl1UobBjD3c4pKEYew9G4361F5KV2fJLTCl/+A5vh3WkBy4H/9LwSVR2TeKIzwJlB14LBaMTMrB6S/ilEFbfj9WP3rMK7R/0Cshz49uHHWI2gDTpTpYLn0Zol4ZkCbJMN7fxqxqzOgCNX2peE3/DVLRBbFizVvLNlY0t0DE3DL652UXhTAbZMG7pgXRE6vQGfn2tZFH7DwZsgtoxKOm1cFXHXFIvCr6cFWNsS0+1wTZfrCki4rlgUfv2XlSC2HEaKG10joXmDEfvyWhaFZwqwfpI6eLUXc3rjqhfQNzoD/7iaReHdDlSC2HIMDMyQob53dWU0rzdC8FMH3JeAdztQAWLrGfaz/A4MaGZXDb64bhCeh6uXhHeLrABZzgF8b74C4v6/LG6d6S8Go335tOapbGjnbYFfF1kOstzbg8A0CWKu9aBcNo7fB6chG5zG7TYNcqrVyLzVb86bKnxPs1IFYWUfhBU0e5FRTrPHlOk3lKZMK+tGZrkSmRVKHL0iN2nemmz+gTcVYO/Vx5v8RvglmvON+Hq79/NLfaSswa/74gbISt/brCT8WnMBJiuTlfBrI8pAGB+WlfBrI8rmCGMisxEenP2lA8TkgJtNZLbBGzn7Sy8Txr6XswyeppoTXvoxoW8PqH3POOBsgZ/hhJcKOOEla0xuPX17wNj3WnbAl+Rywks8FryXoG8PGPu+izGRn0XNq5nOe1h8sULfHlD7njrg1ESmPiy1Ml09Kum0oQuWav5f2TDxN+L+OsgurrftAAAAAElFTkSuQmCC';
  var ICON_CHAT = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAYAAABXAvmHAAAEMUlEQVR42tWa3VOUVRzHzzg144WLqJRpOcwOruBrioQvwLI44//QFZpUpqJOOSKDy8sKxNtKVJOKrjTTTVdgoVG+lKngvrBuK5uFFgYZOavLozg7U2yw3+b37Emh2YvzUDx4vjPn5rn6fM/8zu/8nvkexsYpr0WZkXdyaJnFEaqwOEKe3BP3FfPxe3+Zm4PRnOYgso/dRfbRu8g68js2fjSorvUf3omtD35F5vsDyGzqR2bTL8h47zYyGm9j7eE+pNt/Rrr9J6ypv4XV9Texuu4mXq7txaqaH7Gq5gesrL6BFdU3sLwqgOWVASyr7MFS23UstflH0yr8w6nlPn9qma9hSem1tSar9xkWT5talJl5LUOvWhwht8URiuSeuA/z8XswNwcxTfBIq/AjtdyH1DIflpRdGzVZvQGTtXt7ykGPIQ68ssviCP1mcYTwFMLDZPXCZO3G4oOeUEqJpzSlxG14UjaxnZcBHikltNyKsdhVaCx2Pst4zbslgoex2AVjsbPXeMC5kfEDG5EMHsYDzrHkoq6jjHcb2eCRXNSF5P1dtxhvlTLCI3l/5x+M93kZ4bFo35Uom8ZL6r/CY9G+y2Ayw7/0ziUwmeFffPtbMJnhVQNa4K0dQfQPRTA6hv9dY9EoBh+MwNbej8UlLiH4hXsvgmnZeYKfagWHR7DG1i0Ev2DvNzEDomUzFTv/b0UBbKj2CsEv2PM1mJaa10tZ73qF4F/YfYEbEDyw+hnoFoKfX0gGNHQbvbSh2iMEP7/wPJiWVqmbgSqPEPzzO8+Baenz8TT8Zxh256c4dOVjBMPKpL9PMFDpFoJ/budZMiB+ScUTwcyq36yuPWebJv19vNZXuoTgk3Z8Bablho0n2sl/gArO1E76+3itO+QSgk9660swLeNB3IsnrKg7STB9DwYn/X2CAZtTCH4eGdAy2+ilTNtVIfh52zvAtAxmuhmo6BKC5wbEp0q99Ep5pxD83De/ANMyEuuljLJOIXjVgJZ5fiyqj4H00stC8HPfOAOm5Wdk8OHIlMMr4QjSii4Kwc8hA1r+pKo67iD4KILoFMLbO/qwcPc5Ifg5r58G0/IbuKnxe+Q1BmA53AOLvQe5DdeR2+CHud6PnLrvkFPnQ3atD1k1XnUkpqmSBjOabWg8oBuWLinq89QqqdvQgaWap7KhndcCzw3o+w8resOKwCcWnAaTGT6xoB1MZvjEbe1gMsPP3vY5mMzwqgGZ4We/9hmYzPAJMQNqlCklfMLWU2A8h5USPmHrqRHGQ2QZ4WHY0tbP1AQ8FiLLBh81bGn7hPH4PiAZPK0BQ37bZkZvDyi+5wm4LPBhQ36b1ZDfOlNN6+ntAY/vFTngW5sM+a1JE95L0NsDHt/38hD5aaz5Ab7zSXFfrNDbA4rvKQGnEJlyWIoyp7tVUrehA0s1/7hsuP4GyxTts3QHnnEAAAAASUVORK5CYII=';

  /* ---------- Deterministic identifiers ---------- */
  var HEX = '0123456789abcdef';
  function fakeHash(input) {
    var out = '';
    for (var salt = 0; salt < 8; salt++) {
      var h = (2166136261 ^ (salt * 2654435761)) >>> 0;
      for (var i = 0; i < input.length; i++) {
        h ^= input.charCodeAt(i);
        h = Math.imul(h, 16777619) >>> 0;
      }
      out += ('00000000' + h.toString(16)).slice(-8);
    }
    return out;
  }
  function itemId(prefix, seed) { return prefix + '-' + fakeHash(seed); }
  function backupId(seed) { return fakeHash('backup|' + seed).slice(0, 32); }

  var HOUR = 3600000;
  var DAY = 24 * HOUR;
  var NOW = Date.now();
  function ago(ms) { return new Date(NOW - ms).toISOString(); }

  /* ---------- Items ---------- */
  var REG_HKCU = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run';
  var REG_HKLM = 'HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Run';
  var FOLDER_CURRENT = 'C:\\Users\\Zy\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup';
  var FOLDER_ALL = 'C:\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\StartUp';

  var ID = {
    oneDrive: itemId('reg', 'HKCU|Run|OneDrive'),
    wechat: itemId('reg', 'HKCU|Run|WeChat'),
    sogou: itemId('reg', 'HKCU|Run|SogouCloud'),
    steam: itemId('reg', 'HKCU|Run|Steam'),
    thunder: itemId('reg', 'HKCU|Run|Thunder'),
    acrobat: itemId('reg', 'HKLM|Run|AcrobatAssistant'),
    intel: itemId('reg', 'HKLM|Run|IGCCTray'),
    dingtalk: itemId('dir', 'currentUser|DingTalk.lnk'),
    spotify: itemId('dir', 'currentUser|Spotify.lnk'),
    lenovo: itemId('dir', 'allUsers|LenovoVantage.lnk'),
    adobeCC: itemId('tsk', '\\Adobe Creative Cloud'),
    nvidiaGFE: itemId('tsk', '\\NVIDIA GeForce Experience'),
    google: itemId('tsk', '\\GoogleUpdateTaskMachineUA'),
    edge: itemId('tsk', '\\Microsoft\\Windows\\EdgeUpdate\\MicrosoftEdgeUpdateTaskMachineUA'),
    teamViewer: itemId('svc', 'teamviewer'),
    realtek: itemId('svc', 'rtkaudioservice'),
    nvidiaDisplay: itemId('svc', 'nvdisplay.containerlocalsystem'),
    dell: itemId('svc', 'supportassistagent')
  };

  var ITEMS = [
    {
      id: ID.oneDrive,
      name: 'Microsoft OneDrive',
      publisher: 'Microsoft Corporation',
      source: 'registry',
      scope: 'currentUser',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: REG_HKCU,
      subLocation: '值名称：OneDrive',
      command: '"C:\\Users\\Zy\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe" /background',
      targetPath: 'C:\\Users\\Zy\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe',
      mechanism: 'startupApproved',
      lastModified: ago(8 * DAY),
      icon: ICON_CLOUD
    },
    {
      id: ID.wechat,
      name: '微信',
      publisher: '腾讯科技（深圳）有限公司',
      source: 'registry',
      scope: 'currentUser',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: REG_HKCU,
      subLocation: '值名称：WeChat',
      command: '"C:\\Program Files\\Tencent\\WeChat\\WeChat.exe"',
      targetPath: 'C:\\Program Files\\Tencent\\WeChat\\WeChat.exe',
      mechanism: 'startupApproved',
      lastModified: ago(14 * DAY),
      icon: ICON_CHAT
    },
    {
      id: ID.sogou,
      name: '搜狗输入法',
      publisher: '北京搜狗科技发展有限公司',
      source: 'registry',
      scope: 'currentUser',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: REG_HKCU,
      subLocation: '值名称：SogouCloud',
      command: '"C:\\Program Files (x86)\\SogouInput\\Components\\SogouCloud.exe" -autorun',
      targetPath: 'C:\\Program Files (x86)\\SogouInput\\Components\\SogouCloud.exe',
      mechanism: 'startupApproved',
      lastModified: ago(54 * DAY),
      icon: null
    },
    {
      id: ID.steam,
      name: 'Steam 客户端',
      publisher: 'Valve Corporation',
      source: 'registry',
      scope: 'currentUser',
      enabled: false,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: REG_HKCU,
      subLocation: '值名称：Steam',
      command: '"C:\\Program Files (x86)\\Steam\\steam.exe" -silent',
      targetPath: 'C:\\Program Files (x86)\\Steam\\steam.exe',
      mechanism: 'startupApproved',
      lastModified: ago(3 * DAY),
      icon: null
    },
    {
      id: ID.thunder,
      name: '迅雷',
      publisher: '深圳市迅雷网络技术有限公司',
      source: 'registry',
      scope: 'currentUser',
      enabled: false,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: REG_HKCU,
      subLocation: '值名称：Thunder',
      command: '"C:\\Program Files (x86)\\Thunder Network\\Thunder\\Program\\Thunder.exe" -StartType:AutoRun',
      targetPath: 'C:\\Program Files (x86)\\Thunder Network\\Thunder\\Program\\Thunder.exe',
      mechanism: 'startupApproved',
      lastModified: ago(40 * DAY),
      icon: null
    },
    {
      id: ID.acrobat,
      name: 'Adobe Acrobat 更新程序',
      publisher: 'Adobe Inc.',
      source: 'registry',
      scope: 'allUsers',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: true,
      managedByApp: false,
      location: REG_HKLM,
      subLocation: '值名称：Acrobat Assistant 8.0',
      command: '"C:\\Program Files (x86)\\Adobe\\Acrobat DC\\Acrobat\\Acrobat.exe" /min',
      targetPath: 'C:\\Program Files (x86)\\Adobe\\Acrobat DC\\Acrobat\\Acrobat.exe',
      mechanism: 'startupApproved',
      lastModified: ago(5 * DAY),
      icon: null
    },
    {
      id: ID.intel,
      name: 'Intel 显卡命令中心',
      publisher: 'Intel Corporation',
      source: 'registry',
      scope: 'allUsers',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: true,
      managedByApp: false,
      location: REG_HKLM,
      subLocation: '值名称：IGCCTray',
      command: '"C:\\Program Files\\Intel\\Intel Graphics Command Center\\IGCCTray.exe"',
      targetPath: 'C:\\Program Files\\Intel\\Intel Graphics Command Center\\IGCCTray.exe',
      mechanism: 'startupApproved',
      lastModified: ago(21 * DAY),
      icon: null
    },
    {
      id: ID.dingtalk,
      name: '钉钉',
      publisher: '钉钉（中国）信息技术有限公司',
      source: 'folder',
      scope: 'currentUser',
      enabled: false,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: FOLDER_CURRENT + '\\DingTalk.lnk',
      subLocation: '快捷方式 · 当前用户启动文件夹',
      command: '"C:\\Program Files (x86)\\DingDing\\DingtalkLauncher.exe" --auto-start',
      targetPath: 'C:\\Program Files (x86)\\DingDing\\DingtalkLauncher.exe',
      mechanism: 'startupApproved',
      lastModified: ago(4 * DAY),
      icon: null
    },
    {
      id: ID.spotify,
      name: 'Spotify',
      publisher: 'Spotify AB',
      source: 'folder',
      scope: 'currentUser',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: FOLDER_CURRENT + '\\Spotify.lnk',
      subLocation: '快捷方式 · 当前用户启动文件夹',
      command: '"C:\\Users\\Zy\\AppData\\Roaming\\Spotify\\Spotify.exe" --autostart --minimized',
      targetPath: 'C:\\Users\\Zy\\AppData\\Roaming\\Spotify\\Spotify.exe',
      mechanism: 'startupApproved',
      lastModified: ago(12 * DAY),
      icon: null
    },
    {
      id: ID.lenovo,
      name: '联想电脑管家',
      publisher: '联想（北京）有限公司',
      source: 'folder',
      scope: 'allUsers',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: true,
      managedByApp: false,
      location: FOLDER_ALL + '\\LenovoVantage.lnk',
      subLocation: '快捷方式 · 所有用户启动文件夹',
      command: '"C:\\Program Files (x86)\\Lenovo\\Vantage\\LenovoVantage.exe" --background',
      targetPath: 'C:\\Program Files (x86)\\Lenovo\\Vantage\\LenovoVantage.exe',
      mechanism: 'startupApproved',
      lastModified: ago(30 * DAY),
      icon: null
    },
    {
      id: ID.adobeCC,
      name: 'Adobe Creative Cloud',
      publisher: 'Adobe Inc.',
      source: 'task',
      scope: 'currentUser',
      enabled: false,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: '任务计划程序库 \\Adobe Creative Cloud',
      subLocation: '触发器：登录时 · 用户：Zy',
      command: '"C:\\Program Files\\Adobe\\Adobe Creative Cloud\\ACC\\Creative Cloud.exe" --background',
      targetPath: 'C:\\Program Files\\Adobe\\Adobe Creative Cloud\\ACC\\Creative Cloud.exe',
      mechanism: 'scheduledTask',
      lastModified: ago(18 * DAY),
      icon: null
    },
    {
      id: ID.nvidiaGFE,
      name: 'NVIDIA GeForce Experience',
      publisher: 'NVIDIA Corporation',
      source: 'task',
      scope: 'currentUser',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: false,
      managedByApp: false,
      location: '任务计划程序库 \\NVIDIA GeForce Experience',
      subLocation: '触发器：当前用户登录时',
      command: '"C:\\Program Files\\NVIDIA Corporation\\NVIDIA GeForce Experience\\NVIDIA GeForce Experience.exe" -tray',
      targetPath: 'C:\\Program Files\\NVIDIA Corporation\\NVIDIA GeForce Experience\\NVIDIA GeForce Experience.exe',
      mechanism: 'scheduledTask',
      lastModified: ago(60 * DAY),
      icon: null
    },
    {
      id: ID.google,
      name: 'Google 更新任务',
      publisher: 'Google LLC',
      source: 'task',
      scope: 'allUsers',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: true,
      managedByApp: false,
      location: '任务计划程序库 \\GoogleUpdateTaskMachineUA',
      subLocation: '触发器：每 4 小时 · 用户：SYSTEM',
      command: '"C:\\Program Files (x86)\\Google\\Update\\GoogleUpdate.exe" /ua /installsource scheduler',
      targetPath: 'C:\\Program Files (x86)\\Google\\Update\\GoogleUpdate.exe',
      mechanism: 'scheduledTask',
      lastModified: ago(7 * DAY),
      icon: null
    },
    {
      id: ID.edge,
      name: 'Microsoft Edge 更新任务',
      publisher: 'Microsoft Corporation',
      source: 'task',
      scope: 'allUsers',
      enabled: true,
      protected: true,
      protectionReason: '位于 \\Microsoft\\Windows 下的系统任务，由 Windows 维护。本工具不会修改它。',
      requiresAdmin: true,
      managedByApp: false,
      location: '任务计划程序库 \\Microsoft\\Windows\\EdgeUpdate\\MicrosoftEdgeUpdateTaskMachineUA',
      subLocation: '触发器：每 6 小时 · 用户：SYSTEM',
      command: '"C:\\Program Files (x86)\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe" /ua /installsource scheduler',
      targetPath: 'C:\\Program Files (x86)\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe',
      mechanism: 'scheduledTask',
      lastModified: ago(2 * DAY),
      icon: null
    },
    {
      id: ID.teamViewer,
      name: 'TeamViewer 服务',
      publisher: 'TeamViewer Germany GmbH',
      source: 'service',
      scope: 'system',
      enabled: true,
      protected: false,
      protectionReason: null,
      requiresAdmin: true,
      managedByApp: false,
      location: '服务：TeamViewer',
      subLocation: '服务名称：TeamViewer · 启动类型：自动',
      command: '"C:\\Program Files\\TeamViewer\\TeamViewer_Service.exe"',
      targetPath: 'C:\\Program Files\\TeamViewer\\TeamViewer_Service.exe',
      mechanism: 'serviceStartType',
      lastModified: ago(33 * DAY),
      icon: null
    },
    {
      id: ID.realtek,
      name: 'Realtek 高清晰音频管理器',
      publisher: 'Realtek Semiconductor Corp.',
      source: 'service',
      scope: 'system',
      enabled: true,
      protected: true,
      protectionReason: '音频驱动的核心服务。禁用可能导致扬声器或耳机没有声音，因此已锁定。',
      requiresAdmin: true,
      managedByApp: false,
      location: '服务：RtkAudioUniversalService',
      subLocation: '服务名称：RtkAudioUniversalService · 启动类型：自动',
      command: 'C:\\Windows\\System32\\DriverStore\\FileRepository\\realtekservice.inf_amd64\\RtkAudUService64.exe',
      targetPath: 'C:\\Windows\\System32\\DriverStore\\FileRepository\\realtekservice.inf_amd64\\RtkAudUService64.exe',
      mechanism: 'serviceStartType',
      lastModified: ago(42 * DAY),
      icon: null
    },
    {
      id: ID.nvidiaDisplay,
      name: 'NVIDIA Display Container LS',
      publisher: 'NVIDIA Corporation',
      source: 'service',
      scope: 'system',
      enabled: true,
      protected: true,
      protectionReason: '显示驱动的核心容器服务。禁用可能导致分辨率切换、多屏输出等显示功能异常，因此已锁定。',
      requiresAdmin: true,
      managedByApp: false,
      location: '服务：NVDisplay.ContainerLocalSystem',
      subLocation: '服务名称：NVDisplay.ContainerLocalSystem · 启动类型：自动',
      command: 'C:\\Program Files\\NVIDIA Corporation\\Display.NvContainer\\NVDisplay.Container.exe',
      targetPath: 'C:\\Program Files\\NVIDIA Corporation\\Display.NvContainer\\NVDisplay.Container.exe',
      mechanism: 'serviceStartType',
      lastModified: ago(42 * DAY),
      icon: null
    },
    {
      id: ID.dell,
      name: 'Dell SupportAssist 服务',
      publisher: 'Dell Inc.',
      source: 'service',
      scope: 'system',
      enabled: false,
      protected: false,
      protectionReason: null,
      requiresAdmin: true,
      managedByApp: true,
      location: '服务：SupportAssistAgent',
      subLocation: '服务名称：SupportAssistAgent · 启动类型：手动（原为自动）',
      command: '"C:\\Program Files\\Dell\\SupportAssist\\SupportAssistAgent.exe"',
      targetPath: 'C:\\Program Files\\Dell\\SupportAssist\\SupportAssistAgent.exe',
      mechanism: 'serviceStartType',
      lastModified: ago(6 * HOUR),
      icon: null
    }
  ];

  /* ---------- Backups ---------- */
  function itemById(id) {
    for (var i = 0; i < ITEMS.length; i++) { if (ITEMS[i].id === id) return ITEMS[i]; }
    return null;
  }
  function makeBackup(seed, overrides) {
    var base = {
      id: backupId(seed),
      itemId: '',
      source: 'registry',
      scope: 'currentUser',
      requiresAdmin: false,
      displayName: '',
      location: '',
      command: '',
      createdAt: ago(2 * HOUR),
      restoredAt: null,
      status: 'active',
      summary: '',
      error: null
    };
    for (var key in overrides) {
      if (Object.prototype.hasOwnProperty.call(overrides, key)) base[key] = overrides[key];
    }
    return base;
  }

  var backups = [
    makeBackup('steam', {
      itemId: ID.steam,
      source: 'registry',
      scope: 'currentUser',
      requiresAdmin: false,
      displayName: 'Steam 客户端',
      location: REG_HKCU,
      command: '"C:\\Program Files (x86)\\Steam\\steam.exe" -silent',
      createdAt: ago(2.5 * HOUR),
      status: 'active',
      summary: 'StartupApproved：已禁用（原始 Run 值保留）'
    }),
    makeBackup('dingtalk', {
      itemId: ID.dingtalk,
      source: 'folder',
      scope: 'currentUser',
      requiresAdmin: false,
      displayName: '钉钉',
      location: FOLDER_CURRENT + '\\DingTalk.lnk',
      command: '"C:\\Program Files (x86)\\DingDing\\DingtalkLauncher.exe" --auto-start',
      createdAt: ago(27 * HOUR),
      status: 'active',
      summary: 'StartupApproved：已禁用（原始快捷方式保留）'
    }),
    makeBackup('adobe-cc', {
      itemId: ID.adobeCC,
      source: 'task',
      scope: 'currentUser',
      requiresAdmin: false,
      displayName: 'Adobe Creative Cloud',
      location: '任务计划程序库 \\Adobe Creative Cloud',
      command: '"C:\\Program Files\\Adobe\\Adobe Creative Cloud\\ACC\\Creative Cloud.exe" --background',
      createdAt: ago(53 * HOUR),
      status: 'active',
      summary: '任务状态：已禁用'
    }),
    makeBackup('thunder', {
      itemId: ID.thunder,
      source: 'registry',
      scope: 'currentUser',
      requiresAdmin: false,
      displayName: '迅雷',
      location: REG_HKCU,
      command: '"C:\\Program Files (x86)\\Thunder Network\\Thunder\\Program\\Thunder.exe" -StartType:AutoRun',
      createdAt: ago(78 * HOUR),
      status: 'active',
      summary: 'StartupApproved：已禁用（原始 Run 值保留）'
    }),
    makeBackup('dell', {
      itemId: ID.dell,
      source: 'service',
      scope: 'system',
      requiresAdmin: true,
      displayName: 'Dell SupportAssist 服务',
      location: '服务：SupportAssistAgent',
      command: '"C:\\Program Files\\Dell\\SupportAssist\\SupportAssistAgent.exe"',
      createdAt: ago(3 * HOUR),
      status: 'active',
      summary: '启动类型：手动（原为自动）'
    }),
    makeBackup('baidu', {
      itemId: itemId('dir', 'currentUser|BaiduNetdisk.lnk'),
      source: 'folder',
      scope: 'currentUser',
      requiresAdmin: false,
      displayName: '百度网盘',
      location: FOLDER_CURRENT + '\\BaiduNetdisk.lnk',
      command: '"C:\\Program Files (x86)\\Baidu\\BaiduNetdisk\\BaiduNetdisk.exe" --autostart',
      createdAt: ago(8 * DAY),
      restoredAt: ago(6 * DAY),
      status: 'restored',
      summary: 'StartupApproved：已恢复原始状态'
    }),
    makeBackup('firefox-maint', {
      itemId: itemId('svc', 'mozillamaintenanceservice'),
      source: 'service',
      scope: 'system',
      requiresAdmin: true,
      displayName: 'Mozilla 维护服务',
      location: '服务：MozillaMaintenance',
      command: '"C:\\Program Files (x86)\\Mozilla Maintenance Service\\maintenanceservice.exe"',
      createdAt: ago(4 * DAY),
      status: 'failed',
      summary: '启动类型：手动（原为自动）',
      error: '验证失败：服务启动类型与预期不一致，已回滚到原始配置。'
    })
  ];

  /* ---------- Settings / system ---------- */
  var settings = {
    includeRegistry: true,
    includeStartupFolders: true,
    includeScheduledTasks: true,
    includeServices: true,
    startWithWindows: false,
    minimizeToTray: true,
    backupRetentionDays: 30
  };

  var system = {
    userName: 'Zy',
    machineName: 'DESKTOP-STARTUP',
    backupDirectory: 'C:\\Users\\Zy\\AppData\\Local\\StartupManager\\Backups',
    logDirectory: 'C:\\Users\\Zy\\AppData\\Local\\StartupManager\\Logs'
  };

  function clone(value) { return JSON.parse(JSON.stringify(value)); }

  /* ---------- Demo host ---------- */
  function createDemoHost(emit, options) {
    options = options || {};
    var items = clone(ITEMS);
    var backupList = clone(backups);
    var currentSettings = clone(settings);
    var currentSystem = clone(system);
    var warnings = options.warnings
      ? ['演示提示：1 个计划任务未能读取触发器信息，已按登录启动处理。']
      : [];
    var scannedAt = ago(4 * 60 * 1000);
    var started = false;
    var refreshBusy = false;
    var timers = new Set();

    function later(fn, ms) {
      var timer = setTimeout(function () {
        timers.delete(timer);
        fn();
      }, ms);
      timers.add(timer);
      return timer;
    }

    function findItem(id) {
      for (var i = 0; i < items.length; i++) { if (items[i].id === id) return items[i]; }
      return null;
    }

    function findBackup(id) {
      for (var i = 0; i < backupList.length; i++) { if (backupList[i].id === id) return backupList[i]; }
      return null;
    }

    function makeSnapshot() {
      var enabled = 0, disabled = 0, protectedCount = 0, adminCount = 0;
      items.forEach(function (item) {
        if (item.enabled) enabled++; else disabled++;
        if (item.protected) protectedCount++;
        if (!item.protected && item.requiresAdmin) adminCount++;
      });
      return {
        items: clone(items),
        scannedAt: scannedAt,
        warnings: warnings.slice(),
        isElevated: false,
        enabledCount: enabled,
        disabledCount: disabled,
        protectedCount: protectedCount,
        adminCount: adminCount
      };
    }

    function emitState() {
      emit({
        type: 'state',
        snapshot: makeSnapshot(),
        backups: clone(backupList),
        settings: clone(currentSettings),
        system: clone(currentSystem)
      });
    }

    function reply(requestId, message, extra) {
      var payload = { type: 'commandResult', requestId: requestId, ok: true, message: message, requiresElevation: false, elevationCanceled: false };
      if (extra) {
        for (var key in extra) {
          if (Object.prototype.hasOwnProperty.call(extra, key)) payload[key] = extra[key];
        }
      }
      emit(payload);
    }

    function fail(requestId, message) {
      emit({ type: 'error', requestId: requestId, message: message });
    }

    function currentBackupSummary(item) {
      if (item.source === 'task') return '任务状态：已禁用';
      if (item.source === 'service') return '启动类型：手动（原为自动）';
      if (item.source === 'folder') return 'StartupApproved：已禁用（原始快捷方式保留）';
      return 'StartupApproved：已禁用（原始 Run 值保留）';
    }

    function addBackup(item) {
      var already = backupList.some(function (backup) {
        return backup.itemId === item.id && backup.status === 'active';
      });
      if (already) return;
      backupList.push(makeBackup('demo|' + item.id + '|' + Date.now(), {
        itemId: item.id,
        source: item.source,
        scope: item.scope,
        requiresAdmin: item.requiresAdmin,
        displayName: item.name,
        location: item.location,
        command: item.command,
        createdAt: new Date().toISOString(),
        status: 'active',
        summary: currentBackupSummary(item)
      }));
    }

    function markRestored(itemId) {
      var now = new Date().toISOString();
      backupList.forEach(function (backup) {
        if (backup.itemId === itemId && backup.status === 'active') {
          backup.status = 'restored';
          backup.restoredAt = now;
          backup.summary = backup.summary.replace('已禁用', '已恢复原始状态');
        }
      });
    }

    function anyRequiresAdmin(targets) {
      return targets.some(function (item) { return item.requiresAdmin; });
    }

    function applySetEnabled(requestId, itemIds, enabled) {
      if (!Array.isArray(itemIds) || itemIds.length === 0) {
        fail(requestId, '缺少要修改的启动项。');
        return;
      }
      var targets = [];
      for (var i = 0; i < itemIds.length; i++) {
        var item = findItem(itemIds[i]);
        if (!item) { fail(requestId, '包含未知的启动项，请重新扫描后再试。'); return; }
        if (item.protected) { fail(requestId, '「' + item.name + '」受系统保护，不能修改。'); return; }
        targets.push(item);
      }
      var changed = 0;
      targets.forEach(function (item) {
        if (item.enabled === enabled) return;
        item.enabled = enabled;
        item.lastModified = new Date().toISOString();
        item.managedByApp = item.source === 'service' ? !enabled : false;
        if (enabled) markRestored(item.id); else addBackup(item);
        changed++;
      });
      var delay = anyRequiresAdmin(targets) ? 1150 : 420;
      later(function () {
        emitState();
        reply(requestId, changed === 0 ? '没有需要更改的项目。' : (enabled ? '已启用 ' : '已关闭 ') + changed + ' 项', {
          requiresElevation: anyRequiresAdmin(targets)
        });
      }, delay);
    }

    function applyRestoreBackup(requestId, backupIdValue) {
      var backup = findBackup(backupIdValue);
      if (!backup) { fail(requestId, '备份不存在或已被清理，请重新扫描。'); return; }
      if (backup.status !== 'active') { fail(requestId, '该备份不是可恢复状态。'); return; }
      var item = findItem(backup.itemId);
      later(function () {
        backup.status = 'restored';
        backup.restoredAt = new Date().toISOString();
        if (item) {
          item.enabled = true;
          item.managedByApp = false;
          item.lastModified = new Date().toISOString();
        }
        emitState();
        reply(requestId, '已恢复「' + backup.displayName + '」', { requiresElevation: backup.requiresAdmin });
      }, backup.requiresAdmin ? 1150 : 680);
    }

    function applySaveSettings(requestId, nextSettings) {
      if (!nextSettings || typeof nextSettings !== 'object') {
        fail(requestId, '设置数据无效。');
        return;
      }
      var bools = ['includeRegistry', 'includeStartupFolders', 'includeScheduledTasks', 'includeServices', 'startWithWindows', 'minimizeToTray'];
      bools.forEach(function (key) {
        if (typeof nextSettings[key] === 'boolean') currentSettings[key] = nextSettings[key];
      });
      var retention = Number(nextSettings.backupRetentionDays);
      if (isFinite(retention) && retention >= 1 && retention <= 3650) {
        currentSettings.backupRetentionDays = Math.round(retention);
      }
      later(function () {
        emit({ type: 'settings', settings: clone(currentSettings) });
        reply(requestId, '设置已保存。');
      }, 260);
    }

    function applyRefresh(requestId) {
      if (refreshBusy) { fail(requestId, '扫描正在进行中，请稍候。'); return; }
      refreshBusy = true;
      emit({ type: 'scanStarted' });
      later(function () {
        scannedAt = new Date().toISOString();
        emitState();
        emit({ type: 'scanFinished', ok: true, message: null });
        reply(requestId, '扫描完成。');
        refreshBusy = false;
      }, 750);
    }

    function handleLocationCommand(requestId, command, payload) {
      if (command === 'openBackupFolder') {
        reply(requestId, '演示模式：备份文件夹位于 ' + currentSystem.backupDirectory);
        return;
      }
      if (command === 'openLogFolder') {
        reply(requestId, '演示模式：日志文件夹位于 ' + currentSystem.logDirectory);
        return;
      }
      var item = findItem(payload.itemId);
      if (!item) { fail(requestId, '启动项不存在，请重新扫描。'); return; }
      if (command === 'copyLocation') {
        var text = item.location + (item.subLocation ? ' · ' + item.subLocation : '');
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).catch(function () {});
        }
        reply(requestId, '演示模式：已复制位置。');
        return;
      }
      var target = item.source === 'task' ? '任务计划程序' : (item.source === 'service' ? '服务管理器' : '资源管理器');
      reply(requestId, '演示模式：将打开' + target + '并定位到「' + item.name + '」。');
    }

    function handleCommand(message) {
      var requestId = message.requestId;
      var payload = message;
      if (!requestId) {
        emit({ type: 'error', requestId: null, message: '命令缺少 requestId。' });
        return;
      }
      switch (message.command) {
        case 'refresh':
          applyRefresh(requestId);
          break;
        case 'setEnabled':
          applySetEnabled(requestId, payload.itemIds, payload.enabled === true);
          break;
        case 'restoreBackup':
          applyRestoreBackup(requestId, payload.backupId);
          break;
        case 'saveSettings':
          applySaveSettings(requestId, payload.settings);
          break;
        case 'openItemLocation':
        case 'copyLocation':
        case 'openBackupFolder':
        case 'openLogFolder':
          handleLocationCommand(requestId, message.command, payload);
          break;
        default:
          fail(requestId, '未知命令：' + String(message.command));
      }
    }

    return {
      start: function () {
        if (started) return;
        started = true;
        var initialDelay = typeof options.delay === 'number' && options.delay >= 0 ? options.delay : 260;
        later(function () {
          scannedAt = new Date().toISOString();
          emitState();
        }, initialDelay);
      },
      send: function (message) {
        if (!message || typeof message !== 'object') return;
        if (message.type === 'command') {
          handleCommand(message);
          return;
        }
        if (message.type === 'ready') return;
      },
      dispose: function () {
        timers.forEach(function (timer) { clearTimeout(timer); });
        timers.clear();
      }
    };
  }

  window.StartupDemo = {
    create: createDemoHost,
    itemIds: ID,
    backupCount: backups.length
  };
})();
