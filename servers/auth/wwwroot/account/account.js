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
      $("reauthorize").addEventListener("click", function () {
        window.location.assign(data.reauthorizeUrl);
      });
    } else {
      $("reauthorize").hidden = true;
      $("reauthorize-hint").hidden = false;
    }
  }

  async function logout() {
    $("logout").disabled = true;
    try {
      await fetch("/api/auth/provider-sessions/logout", { method: "POST" });
    } catch (err) {
      // ignore
    }
    window.location.assign("/connect/bilibili");
  }

  $("logout").addEventListener("click", logout);
  $("login").addEventListener("click", function () {
    window.location.assign("/connect/bilibili");
  });

  load();
})();
