(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };

  function show(section) {
    $("loading").hidden = section !== "loading";
    $("profile").hidden = section !== "profile";
    $("error").hidden = section !== "error";
  }

  async function load() {
    show("loading");
    try {
      var response = await fetch("/api/auth/me");
      if (response.status === 401) {
        window.location.assign("/connect/bilibili");
        return;
      }
      if (!response.ok) {
        throw new Error("读取账号信息失败。");
      }
      var data = await response.json();
      render(data);
      show("profile");
    } catch (err) {
      $("error-message").textContent = err.message || "读取账号信息失败。";
      show("error");
    }
  }

  function render(data) {
    var user = data.user || {};
    $("name").textContent = user.name || "";
    $("uid").textContent = user.uid ? String(user.uid) : "";
    $("room").textContent = data.bilibiliRoomId || "未知";
    $("subject").textContent = data.providerSubject || "";

    var avatar = $("avatar");
    if (user.avatarUrl) {
      avatar.src = user.avatarUrl;
      avatar.hidden = false;
    } else {
      avatar.hidden = true;
      avatar.removeAttribute("src");
    }

    if (data.reauthorizeUrl) {
      $("reauthorize").hidden = false;
      $("reauthorize-hint").hidden = false;
      $("reauthorize").addEventListener("click", async function () {
        var button = $("reauthorize");
        button.disabled = true;
        setActionMessage("正在退出当前验证会话……");
        try {
          await logoutRequest();
          window.location.assign(data.reauthorizeUrl);
        } catch (err) {
          button.disabled = false;
          setActionMessage("退出当前验证会话失败，请重试后再重新验证。");
        }
      });
    } else {
      $("reauthorize").hidden = true;
      $("reauthorize-hint").hidden = false;
    }
  }

  function setActionMessage(text) {
    var message = $("action-message");
    message.textContent = text || "";
    message.hidden = !text;
  }

  async function logoutRequest() {
    var response = await fetch("/api/auth/provider-sessions/logout", { method: "POST" });
    if (!response.ok) {
      throw new Error("退出请求失败（HTTP " + response.status + "）");
    }
    try {
      return await response.json();
    } catch (err) {
      return {};
    }
  }

  async function logout() {
    $("logout").disabled = true;
    setActionMessage("");
    try {
      var data = await logoutRequest();
      // 配置了 Casdoor 登出地址时跳转结束 SSO 会话，否则仅退出本服务会话。
      window.location.assign(data.logoutUrl || "/connect/bilibili");
    } catch (err) {
      $("logout").disabled = false;
      setActionMessage("退出登录失败，请检查网络后重试。");
    }
  }

  $("logout").addEventListener("click", logout);
  $("login").addEventListener("click", function () {
    window.location.assign("/connect/bilibili");
  });

  load();
})();
