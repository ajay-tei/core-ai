/* Diva AI Widget Embed Script — served from API origin */
(function () {
  'use strict';

  var script = document.currentScript;
  var widgetId = script.getAttribute('data-widget-id');
  var position = script.getAttribute('data-position') || 'bottom-right';
  if (!widgetId) return;

  var API = script.src.replace(/\/widget\.js(\?.*)?$/, '');
  var isRight = position.indexOf('right') !== -1;
  var iframeVisible = false;

  /* ── Launcher button ────────────────────────────────────────────────── */
  var btn = document.createElement('button');
  btn.id = 'diva-widget-launcher';
  btn.setAttribute('aria-label', 'Open chat');
  btn.style.cssText = [
    'position:fixed',
    isRight ? 'right:24px' : 'left:24px',
    'bottom:24px',
    'width:56px',
    'height:56px',
    'border-radius:50%',
    'background:#6366f1',
    'border:none',
    'cursor:pointer',
    'z-index:2147483646',
    'box-shadow:0 4px 12px rgba(0,0,0,.28)',
    'display:flex',
    'align-items:center',
    'justify-content:center',
    'transition:transform .15s ease,box-shadow .15s ease',
  ].join(';');
  btn.innerHTML =
    '<svg xmlns="http://www.w3.org/2000/svg" width="26" height="26" fill="none" viewBox="0 0 24 24">' +
    '<path fill="#fff" d="M12 2C6.477 2 2 6.254 2 11.5c0 2.398.94 4.583 2.47 6.214L3.05 21.12a.75.75 0 0 0 .976.976l3.406-1.42A10.093 10.093 0 0 0 12 21c5.523 0 10-4.254 10-9.5S17.523 2 12 2Z"/>' +
    '</svg>';
  btn.onmouseenter = function () {
    btn.style.transform = 'scale(1.08)';
    btn.style.boxShadow = '0 6px 16px rgba(0,0,0,.32)';
  };
  btn.onmouseleave = function () {
    btn.style.transform = '';
    btn.style.boxShadow = '0 4px 12px rgba(0,0,0,.28)';
  };
  document.body.appendChild(btn);

  /* ── iframe ─────────────────────────────────────────────────────────── */
  var narrow = window.innerWidth < 480;
  var iframeW = narrow ? (window.innerWidth - 16) + 'px' : '400px';
  var iframeLeft = isRight ? 'auto' : '24px';
  var iframeRight = isRight ? '24px' : 'auto';

  // Cap the height to the visible viewport (minus the 90px bottom offset and a
  // small top gutter) so the panel never runs off the top of short phones.
  function iframeHeight() {
    return Math.max(320, Math.min(600, window.innerHeight - 106)) + 'px';
  }

  var iframe = document.createElement('iframe');
  iframe.id = 'diva-widget-frame';
  iframe.src = API + '/widget-ui?id=' + encodeURIComponent(widgetId);
  iframe.allow = 'microphone';
  iframe.title = 'Diva AI Chat';
  iframe.style.cssText = [
    'display:none',
    'position:fixed',
    'right:' + iframeRight,
    'left:' + iframeLeft,
    'bottom:90px',
    'width:' + iframeW,
    'height:' + iframeHeight(),
    'border:none',
    'border-radius:16px',
    'box-shadow:0 8px 32px rgba(0,0,0,.22)',
    'z-index:2147483647',
    'overflow:hidden',
    'transition:opacity .2s ease,transform .2s ease',
    'opacity:0',
    'transform:translateY(8px)',
  ].join(';');
  document.body.appendChild(iframe);

  /* ── Toggle open/close ───────────────────────────────────────────────── */
  function openWidget() {
    iframe.style.display = 'block';
    // Force reflow so transition fires
    void iframe.offsetWidth;
    iframe.style.opacity = '1';
    iframe.style.transform = 'translateY(0)';
    btn.setAttribute('aria-label', 'Close chat');
    iframeVisible = true;
  }

  function closeWidget() {
    iframe.style.opacity = '0';
    iframe.style.transform = 'translateY(8px)';
    setTimeout(function () { iframe.style.display = 'none'; }, 200);
    btn.setAttribute('aria-label', 'Open chat');
    iframeVisible = false;
  }

  btn.onclick = function () {
    if (iframeVisible) { closeWidget(); } else { openWidget(); }
  };

  /* ── postMessage: SSO token provision ───────────────────────────────── */
  window.addEventListener('message', function (e) {
    if (e.source !== iframe.contentWindow) return;

    if (e.data && e.data.type === 'DIVA_SSO_REQUEST') {
      if (typeof window.__divaSsoProvider === 'function') {
        Promise.resolve(window.__divaSsoProvider())
          .then(function (token) {
            iframe.contentWindow.postMessage({ type: 'DIVA_SSO_TOKEN', token: token || null }, API);
          })
          .catch(function () {
            iframe.contentWindow.postMessage({ type: 'DIVA_SSO_TOKEN', token: null }, API);
          });
      } else {
        iframe.contentWindow.postMessage({ type: 'DIVA_SSO_TOKEN', token: null }, API);
      }
    }

    if (e.data && e.data.type === 'DIVA_CLOSE') {
      closeWidget();
    }
  });

  /* ── Responsive resize ───────────────────────────────────────────────── */
  window.addEventListener('resize', function () {
    var nowNarrow = window.innerWidth < 480;
    iframe.style.width = nowNarrow ? (window.innerWidth - 16) + 'px' : '400px';    iframe.style.height = iframeHeight();  });
})();
