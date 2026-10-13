(() => {
  const win = document.getElementById('app');
  if (!win) return;

  const demo = win.closest('.demo');
  const $ = (s) => win.querySelector(s);
  const scaleBox = win.parentElement;
  const power = $('.xw__power');
  const pStatus = $('.xw__pstatus');
  const pTimer = $('.xw__ptimer');
  const progress = $('.xw__progress');
  const statusLine = $('.xw__line');
  const lines = $('.xw__lines');
  const view = $('.xw__view');
  const stat = (k) => $(`[data-stat="${k}"]`);
  const dot = (k) => $(`[data-dot="${k}"]`);
  const yt = demo.querySelector('.yt');
  const video = demo.querySelector('.yt__video');
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const VIDEO = 'media/demo.mp4';

  let lastW = 0;
  new ResizeObserver(([e]) => {
    const w = e.contentRect.width;
    if (w === lastW) return;
    lastW = w;
    requestAnimationFrame(() => scaleBox.style.setProperty('--s', Math.min(1, w / 880)));
  }).observe(scaleBox);

  let on = false;
  let videoVisible = false;
  function syncVideo() {
    if (on && videoVisible && !document.hidden && !reduced) {
      if (!video.getAttribute('src')) video.src = VIDEO;
      if (video.paused) video.play().catch(() => {});
    } else if (!video.paused) {
      video.pause();
    }
  }
  new IntersectionObserver(([e]) => { videoVisible = e.isIntersecting; syncVideo(); }, { threshold: 0.01 }).observe(video);

  const SP = '&nbsp;';
  function log(text, type = 'info') {
    const row = document.createElement('div');
    const t = `<span class="t">[${new Date().toLocaleTimeString('ru-RU')}]</span>`;
    switch (type) {
      case 'raw': row.innerHTML = text || '&nbsp;'; break;
      case 'indent': row.className = 'indent'; row.innerHTML = text; break;
      case 'rocket': row.className = 'rocket'; row.innerHTML = `<span>${text}</span>`; break;
      case 'system': row.className = 'bold'; row.innerHTML = `${t}<span class="p">[#]</span>${text}`; break;
      case 'ok': row.innerHTML = `${t}<span class="p ok">[OK]</span>${text}`; break;
      case 'error': row.className = 'err'; row.innerHTML = `${t}<span class="p err">[ERROR]</span>${text}`; break;
      case 'warn': row.innerHTML = `${t}<span class="p warn">[WARN]</span>${text}`; break;
      case 'net': row.innerHTML = `${t}<span class="p net">[NET]</span>${text}`; break;
      default: row.innerHTML = `${t}<span class="p">[INFO]</span>${text}`;
    }
    lines.appendChild(row);
    while (lines.childElementCount > 40) lines.firstElementChild.remove();
    view.scrollTop = view.scrollHeight;
  }

  function greeting() {
    const h = new Date().getHours();
    return h < 6 ? 'Доброй ночи' : h < 12 ? 'Доброе утро' : h < 18 ? 'Добрый день' : 'Добрый вечер';
  }

  function disconnectedLog() {
    lines.innerHTML = '';
    log(`🚀 ${greeting()}! Добро пожаловать в NetFix 🚀`, 'rocket');
    log('Инициализация компонентов системы...');
    log('Мой Telegram-канал: <a href="https://t.me/NetFixRuBi" target="_blank" rel="noopener">t.me/NetFixRuBi</a> - информация об обновлениях, новые способы обходов и гайды. Максимально полезная информация, советую подписаться');
    log('', 'raw');
    log('СТАТУС СЕРВИСОВ И ПРИЛОЖЕНИЙ', 'system');
    log(`Обход блокировок (Zapret):${SP.repeat(4)}<span class="s-warn">[ ВЫКЛЮЧЕН ]</span>`, 'warn');
    log(`Прокси для Telegram:${SP.repeat(10)}<span class="s-warn">[ ВЫКЛЮЧЕН ]</span>`, 'warn');
    log(`Права администратора (UAC):${SP.repeat(3)}<span class="s-ok">[ ПОДТВЕРЖДЕНЫ ]</span>`, 'ok');
    log('', 'raw');
    log('Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».');
  }

  let busy = false;
  let seconds = 0;
  let timer = 0;
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const setProgress = (p, text) => { progress.style.setProperty('--p', p + '%'); if (text) statusLine.textContent = text; };

  function runTimer(start) {
    clearInterval(timer);
    if (!start) return;
    timer = setInterval(() => {
      if (document.hidden) return;
      seconds++;
      pTimer.textContent = `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
    }, 1000);
  }

  function countTo(el, to, unit) {
    let start = 0;
    const decimals = to % 1 ? 1 : 0;
    const step = (now) => {
      if (!start) start = now;
      const k = Math.min(1, Math.max(0, (now - start) / 600));
      const v = (to * (1 - Math.pow(1 - k, 3))).toFixed(decimals).replace('.', ',');
      el.innerHTML = `${v} <i>${unit}</i>`;
      if (k < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }

  function setConnected(connected) {
    on = connected;
    win.dataset.state = connected ? 'on' : 'off';
    demo.classList.toggle('is-on', connected);
    power.setAttribute('aria-pressed', connected);
    power.setAttribute('aria-label', connected ? 'Выключить демонстрацию NetFix' : 'Починить: включить демонстрацию NetFix');
    runTimer(connected);

    if (connected) {
      pStatus.textContent = 'Подключено';
      setProgress(100, 'Всё уже работает');
      ['vpn', 'zapret', 'tg'].forEach((k) => dot(k).classList.add('on'));
      countTo(stat('ping'), 11, 'мс');
      countTo(stat('down'), 195.3, 'Мбит/с');
      countTo(stat('up'), 54.8, 'Мбит/с');
    } else {
      pStatus.textContent = 'Починить';
      setProgress(0, 'Байпас отключен');
      pTimer.textContent = '00:00';
      seconds = 0;
      ['vpn', 'zapret', 'tg'].forEach((k) => dot(k).classList.remove('on'));
      stat('ping').innerHTML = '- <i>мс</i>';
      stat('down').innerHTML = '- <i>Мбит/с</i>';
      stat('up').innerHTML = '- <i>Мбит/с</i>';
      if (video.getAttribute('src')) { video.pause(); video.removeAttribute('src'); video.load(); }
      disconnectedLog();
    }
    syncVideo();
  }

  async function toggle() {
    if (busy) return;
    if (on) {
      setConnected(false);
      log('СТАТУС: Соединение разорвано пользователем.', 'error');
      log('Службы Zapret и TgWsProxy остановлены.', 'ok');
      return;
    }
    busy = true;
    win.dataset.state = 'connecting';
    pStatus.textContent = 'Подключение';
    setProgress(10, 'Проверка состояния сервисов...');
    log('', 'raw');
    log(`БЫСТРЫЙ ЗАПУСК [ ${new Date().toLocaleTimeString('ru-RU')} ]`, 'system');
    log('', 'raw');
    log('СОСТОЯНИЕ СЕРВИСОВ', 'system');
    log('Telegram Desktop: <span class="s-warn">[ НЕ ЗАПУЩЕН ]</span>', 'net');
    log(`Discord App:${SP.repeat(6)}<span class="s-warn">[ НЕ ЗАПУЩЕН ]</span>`, 'net');

    await sleep(600);
    setProgress(30, 'Проверка сети...');
    log('', 'raw');
    log('СЕТЕВАЯ СРЕДА', 'system');
    log('Интернет-соединение: <span class="s-ok">[ ПОДКЛЮЧЕНО ]</span>', 'ok');

    await sleep(700);
    setProgress(50, 'Запуск службы Zapret...');
    log('', 'raw');
    log('ЗАПУСК ИСПРАВЛЕНИЙ', 'system');
    log('Проверяю подключение к интернету…');
    log('Интернет есть', 'ok');
    log('Проверяю последние версии инструментов…');
    log('Zapret найден: C:\\Zapret\\service.bat', 'ok');
    log('tg-ws-proxy найден: C:\\Zapret\\TgWsProxy.exe', 'ok');
    log('Zapret не запущен — запускаю...');

    await sleep(800);
    setProgress(75, 'Запуск прокси Telegram...');
    log('✓ Zapret запущен', 'ok');
    dot('zapret').classList.add('on');
    log('TgWsProxy не запущен — запускаю...');

    await sleep(700);
    log('✓ TgWsProxy запущен', 'ok');
    dot('tg').classList.add('on');
    dot('vpn').classList.add('on');
    log('', 'raw');
    log('🚀 Всё запущено и всё работает НОРМАЛЬНО!', 'rocket');
    log('Zapret включен. Discord и YouTube должны работать нормально.', 'ok');
    log('Прокси настроен. Telegram должен работать стабильно.', 'ok');
    log('Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».');

    setConnected(true);
    busy = false;

    setTimeout(() => yt.scrollIntoView({ behavior: reduced ? 'auto' : 'smooth', block: 'center' }), 350);
  }

  power.addEventListener('click', toggle);
  document.addEventListener('visibilitychange', syncVideo);
  setConnected(false);
})();
