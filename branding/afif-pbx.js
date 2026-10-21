/* AFIF-PBX web interface touches: intro rendering + header logo shine */
(function () {
  function ready(fn) {
    if (document.readyState !== "loading") fn(); else document.addEventListener("DOMContentLoaded", fn);
  }
  ready(function () {
    // Header logo: wrap it so the CSS light sweep can run across it
    var logo = document.getElementById("MENU_BRAND_IMAGE_TANGO_LEFT");
    if (logo && !logo.parentNode.classList.contains("afif-logo-wrap")) {
      var wrap = document.createElement("span");
      wrap.className = "afif-logo-wrap";
      logo.parentNode.insertBefore(wrap, logo);
      wrap.appendChild(logo);
    }

    // Intro rendering, once per browser session
    var seen = false;
    try { seen = sessionStorage.getItem("afifIntro") === "1"; sessionStorage.setItem("afifIntro", "1"); } catch (e) {}
    if (seen) return;
    var o = document.createElement("div");
    o.id = "afif-intro";
    o.innerHTML =
      '<div class="ring"></div><div class="ring r2"></div>' +
      '<img src="assets/afif/logo.png" alt="AFIF-PBX"><div class="shine"></div>' +
      '<div class="tag">COMMUNICATIONS PLATFORM</div>';
    document.body.appendChild(o);
    o.addEventListener("click", function () { o.remove(); });
    setTimeout(function () { if (o.parentNode) o.remove(); }, 3400);
  });
})();
