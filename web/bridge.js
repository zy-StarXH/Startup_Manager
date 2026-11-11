/*
 * StartupManager web bridge.
 *
 * Native mode: window.chrome.webview exists (WPF + WebView2 host).
 *   - sends { type: "ready" } on startup
 *   - sends typed commands with a unique requestId
 *   - receives state / scanStarted / scanFinished / commandResult / error / settings
 *
 * Demo mode: no WebView2. A realistic in-memory host from demo-data.js emulates
 * the same message contract so the UI can be reviewed and tested in a browser.
 *
 * Debugging:
 *   window.StartupBridge.mode                 -> "native" | "demo"
 *   window.StartupBridge.handleHostMessage(m) -> inject a host message
 *   window.StartupBridge.sendRaw(m)           -> post a raw web->host message
 *   window.StartupBridge.pendingCount         -> unresolved command count
 *   console logs are prefixed with [StartupBridge]
 */
(function () {
  'use strict';

  var webview = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;
  var nativeMode = !!webview;
  var handlers = {
    onState: null,
    onScanStarted: null,
    onScanFinished: null,
    onSettings: null,
    onError: null,
    onDebug: null
  };
  var pending = new Map();
  var requestSeq = 0;
  var demoHost = null;
  var initialized = false;

  function debug(direction, payload) {
    try {
      console.debug('[StartupBridge]', direction, payload);
    } catch (ignore) { /* console unavailable */ }
    if (typeof handlers.onDebug === 'function') {
      try { handlers.onDebug(direction, payload); } catch (ignore) { /* debug handler must not break flow */ }
    }
  }

  function makeRequestId() {
    requestSeq++;
    return 'r-' + Date.now().toString(36) + '-' + requestSeq;
  }

  function bridgeError(message, hostMessage) {
    var error = new Error(message || '命令失败。');
    error.name = 'StartupBridgeError';
    if (hostMessage) {
      error.requestId = hostMessage.requestId || null;
      error.requiresElevation = !!hostMessage.requiresElevation;
      error.elevationCanceled = !!hostMessage.elevationCanceled;
      error.hostMessage = hostMessage;
    }
    return error;
  }

  function settleCommand(message) {
    var entry = pending.get(message.requestId);
    if (!entry) {
      debug('unmatched-command-result', message);
      return;
    }
    pending.delete(message.requestId);
    if (message.ok === false || message.elevationCanceled === true) {
      var reason = message.message || (message.elevationCanceled ? 'UAC 已取消，未做任何更改。' : '命令未完成。');
      entry.reject(bridgeError(reason, message));
      return;
    }
    entry.resolve(message);
  }

  function rejectCommand(message) {
    var entry = pending.get(message.requestId);
    if (!entry) {
      // Some hosts send commandResult(ok=false) and then a detailed error for
      // the same request. Once the command has been settled, the late error is
      // duplicate feedback and must not produce a second global toast.
      if (message.requestId) {
        debug('late-error-after-result', message);
        return;
      }
      debug('unmatched-error', message);
      if (typeof handlers.onError === 'function') handlers.onError(message);
      return;
    }
    pending.delete(message.requestId);
    entry.reject(bridgeError(message.message || '操作失败。', message));
  }

  /**
   * Central host-message dispatcher. Exposed for host-side debugging and tests.
   */
  function handleHostMessage(raw) {
    var message = raw;
    if (typeof message === 'string') {
      try { message = JSON.parse(message); } catch (error) {
        debug('malformed-host-message', raw);
        return;
      }
    }
    if (!message || typeof message !== 'object' || typeof message.type !== 'string') {
      debug('malformed-host-message', raw);
      return;
    }

    debug('host->web', message);

    switch (message.type) {
      case 'state':
        if (typeof handlers.onState === 'function') handlers.onState(message);
        break;
      case 'scanStarted':
        if (typeof handlers.onScanStarted === 'function') handlers.onScanStarted(message);
        break;
      case 'scanFinished':
        if (typeof handlers.onScanFinished === 'function') handlers.onScanFinished(message);
        break;
      case 'settings':
        if (typeof handlers.onSettings === 'function') handlers.onSettings(message.settings || null);
        break;
      case 'commandResult':
        settleCommand(message);
        break;
      case 'error':
        rejectCommand(message);
        break;
      default:
        debug('unknown-host-message', message);
        break;
    }
  }

  function postToHost(message) {
    if (nativeMode) {
      try {
        webview.postMessage(message);
      } catch (error) {
        debug('post-failed', { message: message, error: String(error) });
      }
    } else if (demoHost) {
      demoHost.send(message);
    }
  }

  /**
   * Send a raw message to the host (or the demo host).
   */
  function sendRaw(message) {
    debug('web->host', message);
    postToHost(message);
  }

  /**
   * Send a command and return a promise. The promise also exposes requestId.
   * Resolves on commandResult.ok, rejects on commandResult.ok=false,
   * elevationCanceled=true, or an error message.
   */
  function command(name, payload) {
    var requestId = makeRequestId();
    var message = { type: 'command', requestId: requestId, command: name };
    if (payload) {
      Object.keys(payload).forEach(function (key) {
        if (payload[key] !== undefined) message[key] = payload[key];
      });
    }
    var promise = new Promise(function (resolve, reject) {
      pending.set(requestId, { resolve: resolve, reject: reject, command: name });
    });
    promise.requestId = requestId;
    debug('web->host', message);
    postToHost(message);
    return promise;
  }

  function listen() {
    if (!nativeMode) return;
    var listener = function (event) { handleHostMessage(event.data); };
    if (typeof webview.addEventListener === 'function') {
      webview.addEventListener('message', listener);
    } else {
      webview.onmessage = listener;
    }
  }

  function readDemoOptions() {
    var params = new URLSearchParams(window.location.search);
    var delay = Number(params.get('demoDelay'));
    return {
      delay: isFinite(delay) && delay >= 0 ? delay : undefined,
      warnings: params.get('warnings') === '1'
    };
  }

  /**
   * Register UI handlers and start the host handshake.
   * Native mode sends {type:"ready"} and waits for the first state message.
   * Demo mode starts the local demo host immediately (state arrives async).
   */
  function init(options) {
    if (!initialized) {
      initialized = true;
      listen();
    }
    if (options) {
      Object.keys(options).forEach(function (key) {
        if (typeof options[key] === 'function') handlers[key] = options[key];
      });
    }

    if (nativeMode) {
      sendRaw({ type: 'ready' });
      return;
    }

    if (!window.StartupDemo || typeof window.StartupDemo.create !== 'function') {
      if (typeof handlers.onError === 'function') {
        handlers.onError({ type: 'error', requestId: null, message: '演示数据未加载，界面无法初始化。' });
      }
      return;
    }

    if (!demoHost) {
      demoHost = window.StartupDemo.create(handleHostMessage, readDemoOptions());
      demoHost.start();
    }
    document.documentElement.setAttribute('data-bridge-mode', 'demo');
  }

  window.StartupBridge = {
    mode: nativeMode ? 'native' : 'demo',
    isNative: nativeMode,
    init: init,
    command: command,
    sendRaw: sendRaw,
    handleHostMessage: handleHostMessage,
    get pendingCount() { return pending.size; }
  };

  if (nativeMode) {
    document.documentElement.setAttribute('data-bridge-mode', 'native');
  }
})();
