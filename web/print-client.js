(function (global) {
  'use strict';

  var DEFAULT_WS_PORT = 17800;
  var DEFAULT_WSS_PORT = 17801;
  var TOKEN_KEY = 'print-assistant-token';

  function createClientError(code, message) {
    var error = new Error(message || code);
    error.code = code;
    return error;
  }

  function safeStorage() {
    try {
      var storage = global.localStorage;
      var probe = '__print_assistant_probe__';
      storage.setItem(probe, '1');
      storage.removeItem(probe);
      return storage;
    } catch (e) {
      var memory = {};
      return {
        getItem: function (key) {
          return Object.prototype.hasOwnProperty.call(memory, key) ? memory[key] : null;
        },
        setItem: function (key, value) {
          memory[key] = String(value);
        },
        removeItem: function (key) {
          delete memory[key];
        }
      };
    }
  }

  function generateId() {
    if (global.crypto && typeof global.crypto.randomUUID === 'function') {
      return global.crypto.randomUUID();
    }
    return 'p-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10);
  }

  function toBase64(input) {
    var bytes = input instanceof Uint8Array
      ? input
      : input instanceof ArrayBuffer
        ? new Uint8Array(input)
        : new Uint8Array(input.buffer, input.byteOffset, input.byteLength);
    var chunk = 0x8000;
    var parts = [];
    for (var i = 0; i < bytes.length; i += chunk) {
      parts.push(String.fromCharCode.apply(null, bytes.subarray(i, i + chunk)));
    }
    return global.btoa(parts.join(''));
  }

  function encodePdf(pdf) {
    if (typeof pdf === 'string') {
      return Promise.resolve(pdf.replace(/^data:[^;]+;base64,/, ''));
    }
    if (pdf && typeof pdf.arrayBuffer === 'function') {
      return pdf.arrayBuffer().then(toBase64);
    }
    if (pdf instanceof ArrayBuffer || ArrayBuffer.isView(pdf)) {
      return Promise.resolve(toBase64(pdf));
    }
    return Promise.reject(createClientError('BAD_PDF', 'pdf 必须是 base64 字符串、Blob 或 ArrayBuffer'));
  }

  function createPrintClient(options) {
    options = options || {};
    var name = options.name || 'web';
    var storage = options.storage || safeStorage();
    var storageKey = options.storageKey || TOKEN_KEY;
    var token = options.token || storage.getItem(storageKey) || '';
    var secure = options.secure != null
      ? !!options.secure
      : !!(global.location && global.location.protocol === 'https:');
    var url = options.url || (secure
      ? 'wss://127.0.0.1:' + (options.wssPort || DEFAULT_WSS_PORT) + '/ws'
      : 'ws://127.0.0.1:' + (options.wsPort || DEFAULT_WS_PORT) + '/ws');
    var requestTimeout = options.requestTimeout || 30000;
    var resultTimeout = options.resultTimeout || 180000;
    var previewTimeout = options.previewTimeout || 60000;
    var autoReconnect = options.autoReconnect !== false;
    var maxReconnectDelay = options.maxReconnectDelay || 30000;

    var socket = null;
    var connecting = null;
    var closedByUser = false;
    var paired = false;
    var printers = [];
    var helperVersion = '';
    var reconnectAttempts = 0;
    var reconnectTimer = null;
    var pending = Object.create(null);
    var resultWaiters = Object.create(null);
    var pendingPair = null;
    var pendingHello = null;
    var pendingPing = null;
    var requestSeq = 0;

    function emit(status, detail) {
      if (typeof options.onStatus === 'function') {
        try {
          options.onStatus(status, detail || {});
        } catch (e) {
          // ignore listener errors
        }
      }
    }

    function snapshot() {
      return {
        url: url,
        secure: secure,
        paired: paired,
        hasToken: !!token,
        helperVersion: helperVersion,
        printers: printers.slice()
      };
    }

    function isOpen() {
      return !!socket && socket.readyState === 1;
    }

    function rejectAll(error) {
      Object.keys(pending).forEach(function (key) {
        var entry = pending[key];
        delete pending[key];
        clearTimeout(entry.timer);
        entry.reject(error);
      });
      Object.keys(resultWaiters).forEach(function (key) {
        var entry = resultWaiters[key];
        delete resultWaiters[key];
        clearTimeout(entry.timer);
        entry.reject(error);
      });
      if (pendingPair) {
        clearTimeout(pendingPair.timer);
        pendingPair.reject(error);
        pendingPair = null;
      }
      if (pendingHello) {
        clearTimeout(pendingHello.timer);
        pendingHello.reject(error);
        pendingHello = null;
      }
      if (pendingPing) {
        clearTimeout(pendingPing.timer);
        pendingPing.reject(error);
        pendingPing = null;
      }
    }

    function scheduleReconnect() {
      if (closedByUser || !autoReconnect || reconnectTimer) {
        return;
      }
      var delay = Math.min(maxReconnectDelay, 500 * Math.pow(2, reconnectAttempts));
      delay = Math.round(delay * (0.7 + Math.random() * 0.6));
      reconnectAttempts++;
      emit('reconnecting', { delay: delay, attempt: reconnectAttempts });
      reconnectTimer = setTimeout(function () {
        reconnectTimer = null;
        connect().catch(function () {
          // close handler reschedules
        });
      }, delay);
    }

    function handleMessage(event) {
      var message;
      try {
        message = JSON.parse(event.data);
      } catch (e) {
        emit('server-error', { code: 'BAD_RESPONSE', message: '服务端返回了非法 JSON' });
        return;
      }
      if (!message || typeof message.type !== 'string') {
        return;
      }

      if (message.type === 'pair_ok') {
        token = message.token || '';
        helperVersion = message.helperVersion || helperVersion;
        if (token) {
          storage.setItem(storageKey, token);
        }
        paired = true;
        if (pendingPair) {
          var pair = pendingPair;
          pendingPair = null;
          clearTimeout(pair.timer);
          pair.resolve(message);
        }
        emit('connected', snapshot());
        return;
      }

      if (message.type === 'hello_ok') {
        paired = true;
        helperVersion = message.helperVersion || helperVersion;
        printers = Array.isArray(message.printers) ? message.printers : [];
        if (pendingHello) {
          var hello = pendingHello;
          pendingHello = null;
          clearTimeout(hello.timer);
          hello.resolve(message);
        }
        emit('connected', snapshot());
        return;
      }

      if (message.type === 'pong') {
        if (pendingPing) {
          var ping = pendingPing;
          pendingPing = null;
          clearTimeout(ping.timer);
          ping.resolve(message);
        }
        return;
      }

      if (message.type === 'result') {
        settleResult(message);
        if (typeof options.onResult === 'function') {
          try {
            options.onResult(message);
          } catch (e) {
            // ignore listener errors
          }
        }
        emit('result', message);
        return;
      }

      if (message.reqId && pending[message.reqId]) {
        var entry = pending[message.reqId];
        delete pending[message.reqId];
        clearTimeout(entry.timer);
        if (message.type === 'error') {
          entry.reject(createClientError(message.code || 'ERROR', message.message || '请求失败'));
        } else {
          entry.resolve(message);
        }
        return;
      }

      if (message.type === 'error') {
        if (pendingPair && message.reqId === 'pair') {
          var failedPair = pendingPair;
          pendingPair = null;
          clearTimeout(failedPair.timer);
          failedPair.reject(createClientError(message.code || 'ERROR', message.message || '配对失败'));
          return;
        }
        if (pendingHello && message.reqId === 'hello') {
          var failedHello = pendingHello;
          pendingHello = null;
          clearTimeout(failedHello.timer);
          failedHello.reject(createClientError(message.code || 'ERROR', message.message || '鉴权失败'));
          return;
        }
        emit('server-error', message);
      }
    }

    function openSocket() {
      return new Promise(function (resolve, reject) {
        var sock;
        try {
          sock = new global.WebSocket(url);
        } catch (e) {
          reject(createClientError('NOT_CONNECTED', '无法创建 WebSocket：' + e.message));
          return;
        }
        socket = sock;
        var settled = false;

        function finish(error, value) {
          if (settled) {
            return;
          }
          settled = true;
          if (error) {
            reject(error);
          } else {
            resolve(value);
          }
        }

        sock.onopen = function () {
          reconnectAttempts = 0;
          if (!token) {
            paired = false;
            emit('unpaired', snapshot());
            finish(null, snapshot());
            return;
          }
          hello().then(function (value) {
            finish(null, value);
          }, function (error) {
            paired = false;
            emit('unpaired', { code: error.code || 'UNAUTHORIZED', message: error.message });
            finish(null, snapshot());
          });
        };

        sock.onmessage = handleMessage;

        sock.onerror = function () {
          if (!settled) {
            finish(createClientError('NOT_CONNECTED', '无法连接打印助手，请确认助手已启动'));
          }
        };

        sock.onclose = function () {
          if (sock !== socket) {
            return;
          }
          var wasPaired = paired;
          socket = null;
          paired = false;
          if (!settled) {
            finish(createClientError('NOT_CONNECTED', '连接已关闭'));
          }
          rejectAll(createClientError('DISCONNECTED', '连接已断开'));
          if (closedByUser) {
            emit('closed', {});
            return;
          }
          emit('disconnected', { paired: wasPaired });
          scheduleReconnect();
        };
      });
    }

    function connect() {
      if (closedByUser) {
        closedByUser = false;
      }
      if (isOpen()) {
        return Promise.resolve(snapshot());
      }
      if (connecting) {
        return connecting;
      }
      emit('connecting', { url: url });
      connecting = openSocket().then(function (value) {
        connecting = null;
        return value;
      }, function (error) {
        connecting = null;
        scheduleReconnect();
        throw error;
      });
      return connecting;
    }

    function ensureOpen() {
      return connect().then(function () {
        if (!isOpen()) {
          throw createClientError('NOT_CONNECTED', '连接尚未建立');
        }
        return socket;
      });
    }

    function ensurePaired() {
      return ensureOpen().then(function () {
        if (!paired) {
          throw createClientError('UNAUTHORIZED', '尚未配对，请先调用 pair(code)');
        }
        return socket;
      });
    }

    function request(message, timeout) {
      return ensurePaired().then(function (sock) {
        return new Promise(function (resolve, reject) {
          var reqId = message.reqId || 'r' + (++requestSeq);
          message.reqId = reqId;
          var timer = setTimeout(function () {
            delete pending[reqId];
            reject(createClientError('TIMEOUT', '请求超时'));
          }, timeout || requestTimeout);
          pending[reqId] = { resolve: resolve, reject: reject, timer: timer };
          try {
            sock.send(JSON.stringify(message));
          } catch (e) {
            delete pending[reqId];
            clearTimeout(timer);
            reject(createClientError('DISCONNECTED', e.message));
          }
        });
      });
    }

    function hello() {
      return new Promise(function (resolve, reject) {
        if (!isOpen()) {
          reject(createClientError('NOT_CONNECTED', '连接尚未建立'));
          return;
        }
        if (pendingHello) {
          clearTimeout(pendingHello.timer);
          pendingHello.reject(createClientError('CANCELLED', '新的 hello 请求已发出'));
          pendingHello = null;
        }
        var timer = setTimeout(function () {
          pendingHello = null;
          reject(createClientError('TIMEOUT', '鉴权超时'));
        }, requestTimeout);
        pendingHello = { resolve: resolve, reject: reject, timer: timer };
        try {
          socket.send(JSON.stringify({ type: 'hello', version: 1, token: token, reqId: 'hello' }));
        } catch (e) {
          clearTimeout(timer);
          pendingHello = null;
          reject(createClientError('DISCONNECTED', e.message));
        }
      });
    }

    function pair(code) {
      if (!code) {
        return Promise.reject(createClientError('BAD_REQUEST', '缺少配对码'));
      }
      return ensureOpen().then(function (sock) {
        return new Promise(function (resolve, reject) {
          if (pendingPair) {
            clearTimeout(pendingPair.timer);
            pendingPair.reject(createClientError('CANCELLED', '新的配对请求已发出'));
            pendingPair = null;
          }
          var timer = setTimeout(function () {
            pendingPair = null;
            reject(createClientError('TIMEOUT', '配对超时'));
          }, requestTimeout);
          pendingPair = { resolve: resolve, reject: reject, timer: timer };
          try {
            sock.send(JSON.stringify({
              type: 'pair',
              code: String(code),
              clientName: name,
              reqId: 'pair'
            }));
          } catch (e) {
            clearTimeout(timer);
            pendingPair = null;
            reject(createClientError('DISCONNECTED', e.message));
          }
        }).then(function (value) {
          return getPrinters().catch(function () {
            return [];
          }).then(function (list) {
            return { token: value.token, helperVersion: value.helperVersion, printers: list };
          });
        });
      });
    }

    function setToken(value) {
      token = value || '';
      paired = false;
      if (token) {
        storage.setItem(storageKey, token);
      } else {
        storage.removeItem(storageKey);
      }
      emit('token', { hasToken: !!token });
    }

    function settleResult(message) {
      var keys = [message.id, message.taskId];
      var settled = [];
      keys.forEach(function (key) {
        if (!key || !resultWaiters[key]) {
          return;
        }
        var entry = resultWaiters[key];
        delete resultWaiters[key];
        clearTimeout(entry.timer);
        entry.resolve(message);
        settled.push(key);
      });
      Object.keys(resultWaiters).forEach(function (key) {
        var entry = resultWaiters[key];
        if (settled.indexOf(key) >= 0) {
          return;
        }
        if (message.taskId && entry.taskId === message.taskId) {
          delete resultWaiters[key];
          clearTimeout(entry.timer);
          entry.resolve(message);
        }
      });
    }

    function waitForResultFor(key, timeout) {
      return new Promise(function (resolve, reject) {
        if (resultWaiters[key]) {
          clearTimeout(resultWaiters[key].timer);
          resultWaiters[key].reject(createClientError('CANCELLED', '等待中的请求被替换'));
        }
        var timer = setTimeout(function () {
          delete resultWaiters[key];
          reject(createClientError('TIMEOUT', '等待打印结果超时'));
        }, timeout || resultTimeout);
        resultWaiters[key] = { resolve: resolve, reject: reject, timer: timer };
      });
    }

    function cancelResultWait(key) {
      if (resultWaiters[key]) {
        clearTimeout(resultWaiters[key].timer);
        resultWaiters[key].reject(createClientError('CANCELLED', '打印请求未受理'));
        delete resultWaiters[key];
      }
    }

    function aliasResult(taskId, key) {
      if (!taskId || !resultWaiters[key]) {
        return;
      }
      var entry = resultWaiters[key];
      resultWaiters[taskId] = { resolve: entry.resolve, reject: entry.reject, timer: entry.timer, taskId: taskId };
    }

    function printTask(payload, opts) {
      opts = opts || {};
      if (!payload || !payload.pdf) {
        return Promise.reject(createClientError('BAD_REQUEST', '缺少 pdf'));
      }
      return ensurePaired().then(function () {
        return encodePdf(payload.pdf);
      }).then(function (base64) {
        var id = payload.id || generateId();
        var waitForResult = opts.waitForResult !== false;
        var resultPromise = waitForResult ? waitForResultFor(id, opts.resultTimeout) : null;
        if (resultPromise) {
          resultPromise.catch(function () {});
        }
        var message = {
          type: 'print',
          id: id,
          pdf: base64,
          copies: payload.copies || 1,
          paper: payload.paper || undefined,
          meta: payload.meta || undefined
        };
        if (payload.printer) {
          message.printer = payload.printer;
        }
        if (payload.offsetXMm != null) {
          message.offsetXMm = payload.offsetXMm;
        }
        if (payload.offsetYMm != null) {
          message.offsetYMm = payload.offsetYMm;
        }
        return request(message, opts.timeout).then(function (accepted) {
          if (!waitForResult) {
            return accepted;
          }
          aliasResult(accepted.taskId, id);
          return resultPromise.then(function (result) {
            return {
              id: id,
              taskId: accepted.taskId,
              duplicate: !!accepted.duplicate,
              result: result
            };
          }, function (error) {
            cancelResultWait(id);
            throw error;
          });
        }, function (error) {
          if (resultPromise) {
            cancelResultWait(id);
          }
          throw error;
        });
      });
    }

    function previewTask(payload, opts) {
      opts = opts || {};
      if (!payload || !payload.pdf) {
        return Promise.reject(createClientError('BAD_REQUEST', '缺少 pdf'));
      }
      return ensurePaired().then(function () {
        return encodePdf(payload.pdf);
      }).then(function (base64) {
        var message = {
          type: 'preview',
          pdf: base64,
          paper: payload.paper || undefined
        };
        if (payload.offsetXMm != null) {
          message.offsetXMm = payload.offsetXMm;
        }
        if (payload.offsetYMm != null) {
          message.offsetYMm = payload.offsetYMm;
        }
        return request(message, opts.timeout || previewTimeout);
      }).then(function (response) {
        var pages = (Array.isArray(response.pages) ? response.pages : []).map(function (page) {
          return {
            widthPx: page.widthPx,
            heightPx: page.heightPx,
            png: page.png,
            dataUrl: 'data:image/png;base64,' + page.png
          };
        });
        return {
          dpi: response.dpi,
          paperWidthMm: response.paperWidthMm,
          paperHeightMm: response.paperHeightMm,
          offsetXMm: response.offsetXMm,
          offsetYMm: response.offsetYMm,
          pages: pages
        };
      });
    }

    function reprint(taskId, opts) {
      opts = opts || {};
      if (!taskId) {
        return Promise.reject(createClientError('BAD_REQUEST', '缺少 taskId'));
      }
      return ensurePaired().then(function () {
        return request({ type: 'reprint', taskId: taskId }, opts.timeout);
      }).then(function (accepted) {
        if (opts.waitForResult === false) {
          return accepted;
        }
        return waitForResultFor(accepted.taskId, opts.resultTimeout).then(function (result) {
          return { taskId: accepted.taskId, result: result };
        });
      });
    }

    function getPrinters() {
      return request({ type: 'printers' }).then(function (response) {
        printers = Array.isArray(response.printers) ? response.printers : [];
        return printers;
      });
    }

    function getHistory(limit) {
      return request({ type: 'history', limit: limit || 50 }).then(function (response) {
        return Array.isArray(response.tasks) ? response.tasks : [];
      });
    }

    function ping() {
      return ensureOpen().then(function (sock) {
        return new Promise(function (resolve, reject) {
          if (pendingPing) {
            clearTimeout(pendingPing.timer);
            pendingPing.reject(createClientError('CANCELLED', '新的 ping 请求已发出'));
            pendingPing = null;
          }
          var timer = setTimeout(function () {
            pendingPing = null;
            reject(createClientError('TIMEOUT', 'ping 超时'));
          }, requestTimeout);
          pendingPing = { resolve: resolve, reject: reject, timer: timer };
          try {
            sock.send(JSON.stringify({ type: 'ping' }));
          } catch (e) {
            clearTimeout(timer);
            pendingPing = null;
            reject(createClientError('DISCONNECTED', e.message));
          }
        });
      });
    }

    function reconnect() {
      closedByUser = false;
      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      if (socket) {
        try {
          socket.close();
        } catch (e) {
          // ignore
        }
        socket = null;
      }
      connecting = null;
      return connect();
    }

    function close() {
      closedByUser = true;
      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      rejectAll(createClientError('CLOSED', '客户端已关闭'));
      if (socket) {
        try {
          socket.close();
        } catch (e) {
          // ignore
        }
        socket = null;
      }
      paired = false;
      emit('closed', {});
    }

    return {
      url: url,
      connect: connect,
      pair: pair,
      setToken: setToken,
      getToken: function () {
        return token;
      },
      getPrinters: getPrinters,
      getHistory: getHistory,
      preview: previewTask,
      print: printTask,
      reprint: reprint,
      ping: ping,
      reconnect: reconnect,
      close: close,
      isConnected: function () {
        return isOpen() && paired;
      },
      snapshot: snapshot
    };
  }

  global.PrintAssistant = { createPrintClient: createPrintClient };

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = global.PrintAssistant;
  }
})(typeof window !== 'undefined' ? window : globalThis);
