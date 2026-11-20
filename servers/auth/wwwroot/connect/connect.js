(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var params = new URLSearchParams(window.location.search);
  var returnTo = params.get("return_to") || "";

  var state = {
    requestId: null,
    pollToken: null,
    expiresAt: 0,
    confirmExpiresAt: 0,
    stopped: false,
    pollTimer: null,
    countdownTimer: null
  };

  function show(section) {
    ["step-rooms", "step-token", "step-verified", "step-success", "step-error"].forEach(function (id) {
      $(id).hidden = id !== section;
    });
  }

  function fail(message) {
    state.stopped = true;
    stopTimers();
    $("error-message").textContent = message;
    show("step-error");
  }

  function stopTimers() {
    if (state.pollTimer) { clearTimeout(state.pollTimer); state.pollTimer = null; }
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }
  }

  async function api(path, options) {
    var response = await fetch(path, options);
    var body = null;
    try { body = await response.json(); } catch (e) { body = null; }
    if (!response.ok) {
      var code = body && body.error ? body.error.code : "";
      var message = body && body.error ? body.error.message : "请求失败（HTTP " + response.status + "）";
      var err = new Error(message);
      err.code = code;
      err.status = response.status;
      throw err;
    }
    return body;
  }

  function describeError(err) {
    if (err.code === "AUTH_DANMAKU_SOURCE_UNAVAILABLE") {
      return "弹幕事件源暂不可用，请稍后再试。";
    }
    if (err.code === "AUTH_TOKEN_RATE_LIMITED") {
      return "获取 token 太频繁，请稍后再试。";
    }
    if (err.code === "AUTH_LOGIN_REQUEST_EXPIRED") {
      return "token 已过期，请重新获取。";
    }
    if (err.code === "AUTH_LOGIN_REQUEST_NOT_FOUND") {
      return "登录请求不存在，请重新获取 token。";
    }
    if (err.status === 429) {
      return "请求过于频繁，请稍后再试。";
    }
    return err.message || "操作失败，请重试。";
  }

  function formatRemaining(milliseconds) {
    var total = Math.max(0, Math.ceil(milliseconds / 1000));
    var minutes = Math.floor(total / 60);
    var seconds = total % 60;
    return minutes + ":" + String(seconds).padStart(2, "0");
  }

  async function loadRooms() {
    try {
      var data = await api("/api/auth/danmaku/rooms");
      var list = $("room-list");
      list.textContent = "";
      var anyAvailable = false;
      data.rooms.forEach(function (room) {
        anyAvailable = anyAvailable || room.available;
        var li = document.createElement("li");
        var name = document.createElement("span");
        name.textContent = "房间 " + room.roomId;
        var status = document.createElement("span");
        status.className = room.available ? "available" : "unavailable";
        status.textContent = room.available ? "可发送" : "暂不可用";
        li.appendChild(name);
        li.appendChild(status);
        list.appendChild(li);
      });
      $("get-token").disabled = !anyAvailable;
      $("room-hint").textContent = anyAvailable
        ? "请选择任一可发送的直播间发送 token。"
        : "当前所有房间暂不可用，请稍后再试。";
    } catch (err) {
      $("room-hint").textContent = describeError(err);
    }
  }

  function startCountdown() {
    if (state.countdownTimer) { clearInterval(state.countdownTimer); }
    state.countdownTimer = setInterval(function () {
      var now = Date.now();
      $("countdown").textContent = formatRemaining(state.expiresAt - now);
      if (state.confirmExpiresAt) {
        $("confirm-countdown").textContent = formatRemaining(state.confirmExpiresAt - now);
      }
    }, 1000);
    $("countdown").textContent = formatRemaining(state.expiresAt - Date.now());
  }

  async function getToken() {
    $("get-token").disabled = true;
    try {
      var body = {};
      if (returnTo) { body.returnTo = returnTo; }
      body.clientId = "account-center";
      var data = await api("/api/auth/login-requests", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      state.requestId = data.requestId;
      state.pollToken = data.pollToken;
      state.expiresAt = Date.parse(data.expiresAt);
      $("token-value").textContent = data.token;
      $("instruction").textContent = data.instruction;
      show("step-token");
      startCountdown();
      schedulePoll(data.pollAfterMs);
    } catch (err) {
      $("get-token").disabled = false;
      fail(describeError(err));
    }
  }

  function schedulePoll(delay) {
    if (state.stopped) { return; }
    state.pollTimer = setTimeout(poll, Math.max(500, delay || 1500));
  }

  async function poll() {
    if (state.stopped || !state.requestId) { return; }
    try {
      var data = await api("/api/auth/login-requests/" + encodeURIComponent(state.requestId), {
        headers: { "X-Poll-Token": state.pollToken }
      });
      if (data.status === "verified") {
        state.confirmExpiresAt = data.confirmExpiresAt ? Date.parse(data.confirmExpiresAt) : 0;
        showUser(data.user);
        show("step-verified");
        startCountdown();
        return;
      }
      if (data.status === "consumed") {
        fail("该登录请求已使用，请重新获取 token。");
        return;
      }
      if (data.status === "expired" || data.status === "cancelled" || data.status === "failed") {
        fail(data.status === "expired" ? "token 已过期，请重新获取。" : "登录请求已失效，请重新获取。");
        return;
      }
      schedulePoll(data.retryAfterMs);
    } catch (err) {
      if (err.status === 404 || err.status === 403 || err.status === 410) {
        fail(describeError(err));
        return;
      }
      schedulePoll(2000);
    }
  }

  function showUser(user) {
    $("user-name").textContent = user ? user.name : "";
    $("user-uid").textContent = user ? String(user.uid) : "";
    var avatar = $("user-avatar");
    if (user && user.avatarUrl) {
      avatar.src = user.avatarUrl;
      avatar.hidden = false;
    } else {
      avatar.hidden = true;
      avatar.removeAttribute("src");
    }
  }

  async function confirm() {
    $("confirm").disabled = true;
    try {
      var data = await api("/api/auth/login-requests/" + encodeURIComponent(state.requestId) + "/confirm", {
        method: "POST",
        headers: { "X-Poll-Token": state.pollToken }
      });
      state.stopped = true;
      stopTimers();
      $("success-detail").textContent = "已绑定 B 站账号：" + data.user.name + "（UID " + data.user.uid + "）";
      show("step-success");
      if (data.mode === "casdoor" && data.redirectUrl) {
        $("redirect-hint").textContent = "正在跳转 Casdoor 完成统一登录……";
        window.setTimeout(function () { window.location.assign(data.redirectUrl); }, 900);
      } else {
        $("redirect-hint").textContent = "正在进入账号中心……";
        window.setTimeout(function () { window.location.assign("/account"); }, 900);
      }
    } catch (err) {
      $("confirm").disabled = false;
      fail(describeError(err));
    }
  }

  function denyRedirect() {
    if (!returnTo) { return false; }
    try {
      var url = new URL(returnTo, window.location.origin);
      url.searchParams.set("denied", "1");
      window.location.assign(url.pathname + url.search);
      return true;
    } catch (err) {
      return false;
    }
  }

  async function cancel() {
    if (state.requestId) {
      try {
        await api("/api/auth/login-requests/" + encodeURIComponent(state.requestId) + "/cancel", {
          method: "POST",
          headers: { "X-Poll-Token": state.pollToken }
        });
      } catch (err) {
        // 取消失败不阻塞重新获取
      }
    }
    // 从站点 /oauth/authorize 进入时，取消必须带 access_denied 回调，避免 Casdoor 请求悬挂。
    if (denyRedirect()) { return; }
    reset();
  }

  function reset() {
    state.stopped = true;
    stopTimers();
    state.requestId = null;
    state.pollToken = null;
    state.confirmExpiresAt = 0;
    state.stopped = false;
    $("confirm").disabled = false;
    $("get-token").disabled = false;
    $("room-hint").textContent = "";
    show("step-rooms");
    loadRooms();
  }

  async function copyToken() {
    var text = $("token-value").textContent;
    try {
      await navigator.clipboard.writeText(text);
      $("copy-token").textContent = "已复制";
      window.setTimeout(function () { $("copy-token").textContent = "复制"; }, 1500);
    } catch (err) {
      $("copy-token").textContent = "请手动复制";
    }
  }

  $("get-token").addEventListener("click", getToken);
  $("copy-token").addEventListener("click", copyToken);
  $("cancel").addEventListener("click", cancel);
  $("reject").addEventListener("click", cancel);
  $("confirm").addEventListener("click", confirm);
  $("retry").addEventListener("click", reset);

  show("step-rooms");
  loadRooms();
})();
