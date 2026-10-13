(() => {
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;

  const nav = document.getElementById('nav');
  new IntersectionObserver(([e]) => nav.classList.toggle('is-island', !e.isIntersecting))
    .observe(document.querySelector('.top-sentinel'));

  const hero = document.querySelector('.hero');
  const sw = document.querySelector('.switch');
  const tabs = sw.querySelectorAll('button');
  function setMode(mode, { scroll = true } = {}) {
    if (sw.dataset.mode === mode) return;
    hero.dataset.mode = sw.dataset.mode = document.documentElement.dataset.mode = mode;
    tabs.forEach((t) => {
      const on = t.dataset.target === mode;
      const pane = document.getElementById(t.getAttribute('aria-controls'));
      t.setAttribute('aria-selected', on);
      pane.hidden = !on;
      pane.classList.toggle('is-swap', on);
    });
    closeMenu();
    history.replaceState(null, '', mode === 'mob' ? '#android' : location.pathname + location.search);
    if (scroll) scrollTo({ top: 0, behavior: 'auto' });
    dispatchEvent(new Event('resize'));
  }
  tabs.forEach((tab) => tab.addEventListener('click', () => setMode(tab.dataset.target)));

  document.addEventListener('click', (e) => {
    const a = e.target.closest('a[href^="#"], [data-target]');
    if (!a || a.closest('.switch')) return;
    let to = a.dataset.target;
    const id = a.getAttribute('href') && a.getAttribute('href').slice(1);
    const el = id && document.getElementById(id);
    const page = el && el.closest('.page');
    if (!to && page && page.hidden) to = page.id === 'page-mob' ? 'mob' : 'pc';
    if (!to || to === sw.dataset.mode) return;
    e.preventDefault();
    setMode(to, { scroll: !page || id === 'top' });
    if (el && id !== 'top') requestAnimationFrame(() => el.scrollIntoView({ behavior: 'auto' }));
  });

  const glide = (() => {
    if (reduced || !matchMedia('(hover: hover) and (pointer: fine)').matches) return null;
    const root = document.documentElement;
    root.style.scrollBehavior = 'auto';
    let target = scrollY, current = scrollY, raf = 0, last = 0;
    const max = () => root.scrollHeight - innerHeight;
    const step = (now) => {
      const dt = Math.min(48, now - (last || now)); last = now;
      current += (target - current) * (1 - Math.pow(.9, dt / 16.7));
      if (Math.abs(target - current) < .4) current = target;
      scrollTo(0, current);
      raf = current === target ? 0 : requestAnimationFrame(step);
    };
    const to = (y) => {
      if (!raf) { current = scrollY; last = 0; raf = requestAnimationFrame(step); }
      target = Math.max(0, Math.min(max(), y));
    };
    const scrollsItself = (el) => {
      for (; el && el !== document.body && el !== root; el = el.parentElement) {
        const cs = getComputedStyle(el);
        if (/(auto|scroll)/.test(cs.overflowY) && el.scrollHeight > el.clientHeight + 1) return true;
      }
      return false;
    };
    addEventListener('wheel', (e) => {
      if (e.ctrlKey || e.defaultPrevented || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
      if (e.target.closest && e.target.closest('.zoom:not([hidden]), .ask:not([hidden]), .nav__sheet:not([hidden])')) return;
      if (scrollsItself(e.target)) return;
      e.preventDefault();
      const d = e.deltaMode === 1 ? e.deltaY * 40 : e.deltaMode === 2 ? e.deltaY * innerHeight : e.deltaY;
      to((raf ? target : scrollY) + d);
    }, { passive: false });
    addEventListener('scroll', () => {
      if (raf && Math.abs(scrollY - Math.round(current)) > 2) { cancelAnimationFrame(raf); raf = 0; }
      if (!raf) target = current = scrollY;
    }, { passive: true });
    return { to };
  })();
  if (glide) document.addEventListener('click', (e) => {
    const a = e.target.closest('a[href^="#"]');
    if (!a || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey) return;
    const id = a.getAttribute('href').slice(1);
    const el = id === 'top' ? document.body : document.getElementById(id);
    if (!el || el.closest('[hidden]')) return;
    e.preventDefault();
    const pad = parseFloat(getComputedStyle(document.documentElement).scrollPaddingTop) || 0;
    glide.to(id === 'top' ? 0 : el.getBoundingClientRect().top + scrollY - pad);
  });

  const bar = nav.querySelector('.nav__in');
  const burger = nav.querySelector('.nav__burger');
  const sheet = document.getElementById('nav-sheet');
  const slot = sheet.querySelector('.nav__sheet-slot');
  const dl = bar.querySelector('.pill');

  function setMenu(open) {
    sheet.hidden = !open;
    nav.classList.toggle('is-open', open);
    burger.setAttribute('aria-expanded', open);
  }
  function closeMenu() { if (!sheet.hidden) setMenu(false); }

  burger.addEventListener('click', () => setMenu(sheet.hidden));
  sheet.addEventListener('click', (e) => { if (e.target.closest('a')) closeMenu(); });
  addEventListener('keydown', (e) => { if (e.key === 'Escape') closeMenu(); });
  addEventListener('pointerdown', (e) => {
    const t = e.target;
    if (!(t instanceof Element) || !t.closest('header.nav')) closeMenu();
  }, true);
  addEventListener('scroll', closeMenu, { passive: true });

  const narrow = matchMedia('(max-width: 960px)');
  function placeSwitch() {
    const home = narrow.matches ? slot : bar;
    if (sw.parentElement === home) return;
    if (home === bar) bar.insertBefore(sw, dl);
    else slot.append(sw);
  }
  placeSwitch();
  narrow.addEventListener('change', () => { closeMenu(); placeSwitch(); });

  const title = document.querySelector('.hero__title');
  if (title) {
  const letters = [...title.querySelectorAll('.l')];
  const measureTitle = () => {
    const x0 = letters[0].offsetLeft;
    letters.forEach((l) => l.style.setProperty('--x', `${l.offsetLeft - x0}px`));
  };
  measureTitle();
  addEventListener('resize', measureTitle);
  if (document.fonts) document.fonts.ready.then(measureTitle);

  const cat = title.querySelector('.cat');
  const probe = title.querySelector('.cat-probe');
  const glyphCtx = document.createElement('canvas').getContext('2d');
  function runCat() {
    const hr = title.getBoundingClientRect();
    const em = parseFloat(getComputedStyle(title).fontSize);
    const base = probe.getBoundingClientRect().top - hr.top;
    const cs = getComputedStyle(letters[0]);
    glyphCtx.font = `${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
    const W = 0.52 * em;
    const H = 0.364 * em;
    const speed = 1.05 * em;
    const tops = letters.map((l) => {
      const r = l.getBoundingClientRect();
      return { l, L: r.left - hr.left, R: r.right - hr.left, y: base - glyphCtx.measureText(l.textContent).actualBoundingBoxAscent };
    });

    const pts = [];
    const lands = [];
    let t = 0;
    const at = (x, y, o = 1, sx = 1, sy = 1, easing = 'linear') => pts.push({ x, y, o, sx, sy, t, easing });
    const last = () => pts[pts.length - 1];
    const hop = (x, y, dur, onto) => {
      const p = last();
      t += 0.09; at(p.x, p.y, 1, 1.1, 0.86, 'ease-out');
      t += dur / 2; at((p.x + x) / 2, Math.min(p.y, y) - 0.17 * em, 1, 0.93, 1.1, 'ease-in');
      t += dur / 2; at(x, y, 1, 1.12, 0.85, 'ease-out');
      if (onto) lands.push({ t, l: onto });
      t += 0.1; at(x, y);
    };
    const walk = (x) => { const p = last(); t += Math.abs(x - p.x) / speed; at(x, p.y); };

    at(tops[0].L - W * 0.7, base, 0);
    t += 0.3;
    at(tops[0].L - W * 0.7, base);
    tops.forEach((g, k) => {
      const inset = Math.min(0.08 * em, (g.R - g.L) / 2);
      hop(g.L + inset, g.y, k ? 0.34 : 0.48, g.l);
      if (k < tops.length - 1) walk(Math.max(g.L + inset, g.R - inset));
      else walk((g.L + g.R) / 2);
    });
    const sitFrom = t;
    t += 3.6;
    at(last().x, last().y);
    const sitTo = t;
    const lastG = tops[tops.length - 1];
    hop(lastG.R + W * 0.8, base, 0.5);
    t += 0.25;
    at(last().x, base, 0);

    const total = t;
    cat.classList.add('is-walk');
    const anim = cat.animate(pts.map((p) => ({
      offset: p.t / total,
      easing: p.easing,
      opacity: p.o,
      transform: `translate(${p.x - W / 2}px, ${p.y - H}px) scale(${p.sx}, ${p.sy})`,
    })), { duration: total * 1000, fill: 'forwards' });
    const later = (fn, sec) => setTimeout(fn, sec * 1000);
    const timers = [
      later(() => { cat.classList.remove('is-walk'); cat.classList.add('is-sit'); }, sitFrom),
      later(() => cat.classList.add('is-meow'), sitFrom + 0.9),
      later(() => cat.classList.remove('is-meow'), sitFrom + 2.5),
      later(() => { cat.classList.remove('is-sit'); cat.classList.add('is-walk'); }, sitTo),
      ...lands.map(({ t: lt, l }) => later(() => l.animate(
        [{ transform: 'none' }, { transform: 'scale(1.05, .93)' }, { transform: 'none' }],
        { duration: 280, easing: 'ease-out' },
      ), lt)),
    ];
    anim.onfinish = () => { timers.forEach(clearTimeout); cat.classList.remove('is-walk', 'is-sit', 'is-meow'); anim.cancel(); };
    return total;
  }

  if (!reduced) {
    const SCENES = ['hop', 'cat', 'wave', 'jelly', 'pop', 'glitch', 'cat', 'domino', 'nod'];
    let scene = 0;
    let titleVisible = true;
    new IntersectionObserver(([e]) => { titleVisible = e.isIntersecting; }).observe(title);
    const play = () => {
      if (document.hidden || !titleVisible) { setTimeout(play, 1000); return; }
      const name = SCENES[scene++ % SCENES.length];
      title.removeAttribute('data-anim');
      void title.offsetWidth;
      title.dataset.anim = name;
      const busy = name === 'cat' ? runCat() * 1000 + 1200 : 0;
      setTimeout(play, Math.max(6000, busy));
    };
    setTimeout(() => {
      title.classList.add('is-landed');
      play();
    }, 2600);
  }
  }

  function splitWords(el) {
    let i = 0;
    const walk = (node) => {
      [...node.childNodes].forEach((n) => {
        if (n.nodeType === 3) {
          const frag = document.createDocumentFragment();
          n.textContent.split(/( +)/).forEach((part) => {
            if (!part) return;
            if (/^ +$/.test(part)) { frag.append(part); return; }
            const w = document.createElement('span');
            w.className = 'w';
            w.style.setProperty('--i', i++);
            w.textContent = part;
            frag.append(w);
          });
          n.replaceWith(frag);
        } else if (n.nodeType === 1 && n.tagName !== 'BR') {
          walk(n);
        }
      });
    };
    walk(el);
  }
  document.querySelectorAll('[data-split]').forEach(splitWords);

  const INTRO_MS = reduced ? 0 : 1900;
  function reveal(el) {
    el.classList.add('in');
    if (el.hasAttribute('data-reveal')) {
      const d = parseFloat(getComputedStyle(el).getPropertyValue('--d')) || 0;
      setTimeout(() => { el.removeAttribute('data-reveal'); }, 1300 + d * 90);
    }
  }
  const io = new IntersectionObserver((entries) => {
    entries.forEach((e) => {
      if (!e.isIntersecting) return;
      io.unobserve(e.target);
      const wait = INTRO_MS - performance.now();
      if (wait > 0) setTimeout(() => reveal(e.target), wait);
      else reveal(e.target);
    });
  }, { rootMargin: '0px 0px -8% 0px', threshold: 0.12 });
  document.querySelectorAll('[data-reveal], [data-split]').forEach((el) => io.observe(el));

  const fmt = new Intl.NumberFormat('ru-RU');
  if (!reduced) {
    setTimeout(() => {
      document.querySelectorAll('[data-count]').forEach((el) => {
        const to = +el.dataset.count;
        let start = 0;
        const step = (now) => {
          if (!start) start = now;
          const k = Math.min(1, Math.max(0, (now - start) / 3200));
          el.textContent = fmt.format(Math.round(to * (1 - Math.pow(1 - k, 4))));
          if (k < 1) requestAnimationFrame(step);
        };
        requestAnimationFrame(step);
      });
    }, 900);
  }

  const zoom = document.getElementById('zoom');
  if (zoom) {
    const zPan = zoom.querySelector('.zoom__pan');
    const zImg = zPan.querySelector('img');
    const zCap = zoom.querySelector('.zoom__cap');
    const zClose = zoom.querySelector('.zoom__x');
    let returnTo = null;

    function openZoom(img) {
      returnTo = document.activeElement;
      zImg.src = img.currentSrc || img.src;
      zImg.alt = img.alt || '';
      zCap.textContent = img.alt || '';
      zoom.hidden = false;
      document.documentElement.classList.add('zoom-lock');
      requestAnimationFrame(() => {
        zPan.scrollLeft = Math.max(0, (zPan.scrollWidth - zPan.clientWidth) / 2);
        zPan.scrollTop = 0;
        zClose.focus();
      });
    }
    function closeZoom() {
      if (zoom.hidden) return;
      zoom.hidden = true;
      zImg.removeAttribute('src');
      document.documentElement.classList.remove('zoom-lock');
      if (returnTo && returnTo.focus) returnTo.focus();
    }

    document.querySelectorAll('.shot, .rev img').forEach((img) => {
      img.classList.add('is-zoomable');
      img.tabIndex = 0;
      img.setAttribute('role', 'button');
      img.addEventListener('click', () => openZoom(img));
      img.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openZoom(img); }
      });
    });

    zoom.addEventListener('click', (e) => { if (e.target !== zImg) closeZoom(); });
    addEventListener('keydown', (e) => { if (e.key === 'Escape') closeZoom(); });
  }

  if (matchMedia('(hover: hover) and (pointer: fine)').matches) {
    document.querySelectorAll('.nocatch li, .contact').forEach((card) => card.addEventListener('pointermove', (e) => {
      const r = card.getBoundingClientRect();
      card.style.setProperty('--mx', `${e.clientX - r.left}px`);
      card.style.setProperty('--my', `${e.clientY - r.top}px`);
    }));
  }

  const ask = document.getElementById('ask');
  const askSheet = ask && ask.querySelector('.ask__sheet');
  const isPhone = matchMedia('(max-width: 700px)').matches
    && (matchMedia('(pointer: coarse)').matches || /Android|iPhone|iPod|Mobile/i.test(navigator.userAgent));
  if (ask && isPhone) {
    let returnFocus = null;
    const openAsk = () => {
      if (hero.dataset.mode === 'mob') return;
      if (document.documentElement.classList.contains('zoom-lock') || nav.classList.contains('is-open')) { setTimeout(openAsk, 1500); return; }
      returnFocus = document.activeElement;
      ask.hidden = false;
      document.documentElement.classList.add('zoom-lock');
      ask.querySelector('[data-ask="android"]').focus({ preventScroll: true });
    };
    const closeAsk = (then) => {
      if (ask.hidden || ask.classList.contains('is-closing')) return;
      ask.classList.add('is-closing');
      askSheet.style.transform = '';
      setTimeout(() => {
        ask.hidden = true;
        ask.classList.remove('is-closing');
        document.documentElement.classList.remove('zoom-lock');
        if (returnFocus && returnFocus.focus) returnFocus.focus({ preventScroll: true });
        if (then) then();
      }, reduced ? 0 : 320);
    };
    setTimeout(openAsk, 2600);

    ask.querySelector('.ask__backdrop').addEventListener('click', () => closeAsk());
    ask.querySelector('[data-ask="close"]').addEventListener('click', () => closeAsk());
    ask.querySelector('[data-ask="android"]').addEventListener('click', () => closeAsk(() => {
      setMode('mob');
    }));
    addEventListener('keydown', (e) => { if (e.key === 'Escape') closeAsk(); });

    let y0 = null;
    let dy = 0;
    askSheet.addEventListener('pointerdown', (e) => { y0 = e.clientY; dy = 0; askSheet.style.transition = 'none'; });
    askSheet.addEventListener('pointermove', (e) => {
      if (y0 === null) return;
      dy = Math.max(0, e.clientY - y0);
      if (dy > 6) askSheet.style.transform = `translateY(${dy}px)`;
    });
    const release = () => {
      if (y0 === null) return;
      y0 = null;
      askSheet.style.transition = '';
      if (dy > 90) closeAsk();
      else askSheet.style.transform = '';
    };
    askSheet.addEventListener('pointerup', release);
    askSheet.addEventListener('pointercancel', release);
  }

  document.querySelectorAll('[data-carousel]').forEach((track) => {
    const items = () => [...track.querySelectorAll(track.dataset.carousel)];
    const box = document.createElement('div');
    box.className = 'car';
    track.before(box);
    box.append(track);

    const next = document.createElement('button');
    next.type = 'button';
    next.className = 'car__next';
    next.setAttribute('aria-label', 'Листать дальше');
    next.innerHTML = '<svg class="ic" aria-hidden="true"><use href="#i-chevron"/></svg>';
    const dots = document.createElement('div');
    dots.className = 'car__dots';
    dots.setAttribute('aria-hidden', 'true');
    items().forEach(() => dots.append(document.createElement('i')));
    box.append(next, dots);

    let ticking = false;
    const stepOf = (list) => (list.length > 1 ? list[1].offsetLeft - list[0].offsetLeft : track.clientWidth);
    function sync() {
      ticking = false;
      const list = items();
      const max = track.scrollWidth - track.clientWidth;
      const end = track.scrollLeft >= max - 4;
      const cur = end ? list.length - 1 : Math.round(track.scrollLeft / stepOf(list));
      [...dots.children].forEach((d, i) => d.classList.toggle('on', i === cur));
      box.classList.toggle('is-end', end);
      box.classList.toggle('is-static', max <= 4);
      next.setAttribute('aria-label', end ? 'Листать в начало' : 'Листать дальше');
    }
    track.addEventListener('scroll', () => { if (!ticking) { ticking = true; requestAnimationFrame(sync); } }, { passive: true });
    addEventListener('resize', sync);
    next.addEventListener('click', () => {
      const step = stepOf(items());
      const end = track.scrollLeft >= track.scrollWidth - track.clientWidth - 4;
      track.scrollTo({ left: end ? 0 : track.scrollLeft + step, behavior: reduced ? 'auto' : 'smooth' });
    });
    sync();
  });

  async function latestRelease(repo, ext, linkSel, verSel) {
    const KEY = `nf-latest-${repo}`;
    let data = null;
    try { data = JSON.parse(sessionStorage.getItem(KEY)); } catch (e) { }
    if (!data) {
      const res = await fetch(`https://api.github.com/repos/rupleide/${repo}/releases/latest`, {
        headers: { Accept: 'application/vnd.github+json' },
      });
      if (!res.ok) return;
      const json = await res.json();
      const file = (json.assets || []).find((f) => ext.test(f.name));
      if (!file) return;
      data = { url: file.browser_download_url, tag: json.tag_name };
      try { sessionStorage.setItem(KEY, JSON.stringify(data)); } catch (e) { }
    }
    document.querySelectorAll(linkSel).forEach((el) => { el.href = data.url; });
    document.querySelectorAll(verSel).forEach((el) => { el.textContent = data.tag; });
  }
  (window.requestIdleCallback || setTimeout)(() => {
    latestRelease('NetFix', /\.exe$/i, '.js-dl', '.js-ver').catch(() => {});
    latestRelease('NetFixMobile', /\.apk$/i, '.js-apk', '.js-apk-ver').catch(() => {});
  }, { timeout: 2500 });

  const mobPage = document.getElementById('page-mob');
  const pcPage = document.getElementById('page-pc');
  const clamp01 = (v) => Math.min(1, Math.max(0, v));
  const smooth = (t) => t * t * (3 - 2 * t);
  const easeOut = (t) => 1 - Math.pow(1 - t, 3);
  const rnd = (i) => { const s = Math.sin(i * 12.9898 + 4.1) * 43758.5453; return s - Math.floor(s); };

  const globes = [];
  function globe(canvas, opts) {
    const ctx = canvas.getContext('2d');
    const N = opts.n || 1100;
    const pts = [];
    const ga = Math.PI * (3 - Math.sqrt(5));
    for (let i = 0; i < N; i++) {
      const y = 1 - (i / (N - 1)) * 2;
      const r = Math.sqrt(1 - y * y);
      pts.push([Math.cos(ga * i) * r, y, Math.sin(ga * i) * r]);
    }
    const orbits = [[.9, .3, .05, 1.08, 1], [-.4, .9, .5, 1.16, -.8], [.2, -.6, 1, 1.24, .65]].map(([a, b, c, rad, sp]) => {
      const len = Math.hypot(a, b, c); const n = [a / len, b / len, c / len];
      const t = Math.abs(n[0]) < .9 ? [1, 0, 0] : [0, 1, 0];
      let u = [n[1] * t[2] - n[2] * t[1], n[2] * t[0] - n[0] * t[2], n[0] * t[1] - n[1] * t[0]];
      const ul = Math.hypot(...u); u = u.map((v) => v / ul);
      const v = [n[1] * u[2] - n[2] * u[1], n[2] * u[0] - n[0] * u[2], n[0] * u[1] - n[1] * u[0]];
      return { u, v, rad, sp, a0: rnd(rad * 10) * 6.28 };
    });
    const stars = Array.from({ length: opts.stars || 0 }, (_, i) => [rnd(i * 3.1), rnd(i * 7.7 + 1), .4 + rnd(i + 5) * 1.1, rnd(i + 9) * 6.28, .5 + rnd(i + 2) * 1.5]);
    const g = { canvas, on: opts.on || 0, target: opts.on || 0, ripple: -1, visible: false, raf: 0, last: 0, rot: 0, w: 0, h: 0 };
    const size = () => {
      const dpr = Math.min(devicePixelRatio || 1, 2);
      g.w = canvas.clientWidth; g.h = canvas.clientHeight;
      canvas.width = Math.round(g.w * dpr); canvas.height = Math.round(g.h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    const tiltC = Math.cos(.42), tiltS = Math.sin(.42);
    function draw(now) {
      g.raf = 0;
      if (g.last && now - g.last < 6) { if (g.visible && !reduced) g.raf = requestAnimationFrame(draw); return; }
      const dt = Math.min(64, now - (g.last || now)); g.last = now;
      g.on += (g.target - g.on) * Math.min(1, dt / 500);
      g.rot += dt * .00007;
      const { x: cx, y: cy, r: R } = opts.center(g);
      ctx.clearRect(0, 0, g.w, g.h);
      const on = g.on;
      if (stars.length) {
        ctx.fillStyle = '#fff';
        stars.forEach(([sx, sy, sz, ph, sp]) => {
          const tw = Math.sin(now * .001 * sp + ph);
          ctx.globalAlpha = .12 + .5 * tw * tw;
          ctx.fillRect(sx * g.w, sy * g.h, sz, sz);
        });
      }
      ctx.fillStyle = `rgb(${150 - 76 * on | 0},${156 + 66 * on | 0},${152 - 24 * on | 0})`;
      const cr = Math.cos(g.rot), sr = Math.sin(g.rot);
      const rp = g.ripple >= 0 ? (now - g.ripple) / 1500 : -1;
      if (rp > 1) g.ripple = -1;
      const rr = rp * R * 1.9, rw = R * .16;
      const dotK = opts.dot || 1;
      for (let i = 0; i < N; i++) {
        const p = pts[i];
        const x = p[0] * cr - p[2] * sr;
        const z0 = p[0] * sr + p[2] * cr;
        const y = p[1] * tiltC - z0 * tiltS;
        const z = p[1] * tiltS + z0 * tiltC;
        const depth = (z + 1) / 2;
        const light = Math.max(0, -x * .45 - y * .5 + z * .75);
        let a = (.03 + .55 * depth * depth) * (.3 + .7 * light) * (opts.alpha || 1);
        const sx = cx + x * R, sy = cy + y * R;
        if (rp >= 0) {
          const d = Math.hypot(sx - cx, sy - cy) - rr;
          a += Math.exp(-(d * d) / (rw * rw)) * (1 - rp) * .9 * depth;
        }
        if (a < .012) continue;
        ctx.globalAlpha = Math.min(1, a);
        ctx.beginPath();
        ctx.arc(sx, sy, (.45 + depth * 1.25) * dotK, 0, 6.2832);
        ctx.fill();
      }
      if (on > .02) {
        ctx.lineCap = 'round';
        orbits.forEach((o) => {
          const head = o.a0 + now * .00045 * o.sp;
          const P = (t) => {
            const c = Math.cos(t) * o.rad, s = Math.sin(t) * o.rad;
            const x = o.u[0] * c + o.v[0] * s, y = o.u[1] * c + o.v[1] * s, z = o.u[2] * c + o.v[2] * s;
            return [cx + x * R, cy + y * R, (z + 1) / 2];
          };
          ctx.globalAlpha = .07 * on; ctx.strokeStyle = '#5ee08f'; ctx.lineWidth = 1;
          ctx.beginPath();
          for (let k = 0; k <= 64; k++) { const q = P(k / 64 * 6.2832); k ? ctx.lineTo(q[0], q[1]) : ctx.moveTo(q[0], q[1]); }
          ctx.stroke();
          const STEPS = 34;
          let prev = P(head);
          for (let k = 1; k <= STEPS; k++) {
            const q = P(head - k * .03 * Math.sign(o.sp));
            ctx.globalAlpha = (1 - k / STEPS) * on * (.35 + .65 * prev[2]);
            ctx.lineWidth = 2.2 * (1 - k / STEPS) + .4;
            ctx.beginPath(); ctx.moveTo(prev[0], prev[1]); ctx.lineTo(q[0], q[1]); ctx.stroke();
            prev = q;
          }
          const h = P(head);
          const grd = ctx.createRadialGradient(h[0], h[1], 0, h[0], h[1], 10);
          grd.addColorStop(0, 'rgba(255,255,255,1)'); grd.addColorStop(.3, 'rgba(94,224,143,.8)'); grd.addColorStop(1, 'rgba(34,197,94,0)');
          ctx.globalAlpha = on * (.4 + .6 * h[2]); ctx.fillStyle = grd;
          ctx.beginPath(); ctx.arc(h[0], h[1], 10, 0, 6.2832); ctx.fill();
          ctx.fillStyle = `rgb(${150 - 76 * on | 0},${156 + 66 * on | 0},${152 - 24 * on | 0})`;
        });
      }
      ctx.globalAlpha = 1;
      if (g.visible && !reduced) g.raf = requestAnimationFrame(draw);
    }
    g.kick = () => { if (!g.raf && g.visible) g.raf = requestAnimationFrame(draw); };
    g.set = (on, ripple) => {
      g.target = on ? 1 : 0;
      if (reduced) g.on = g.target; else if (on && ripple) g.ripple = performance.now();
      g.kick();
    };
    new ResizeObserver(() => { size(); g.kick(); }).observe(canvas);
    new IntersectionObserver(([e]) => {
      g.visible = e.isIntersecting;
      if (g.visible) { g.last = 0; g.kick(); }
    }).observe(canvas);
    globes.push(g);
    return g;
  }

  const starfields = [];
  function starfield(canvas, opts = {}) {
    const ctx = canvas.getContext('2d');
    const N = opts.n || 220;
    const stars = Array.from({ length: N }, (_, i) => {
      const z = rnd(i * 5.3 + 2);
      return { x: rnd(i * 3.1), y: rnd(i * 7.7 + 1), z, r: .35 + z * z * 1.5, ph: rnd(i + 9) * 6.28, sp: .4 + rnd(i + 2) * 1.6 };
    });
    const f = { canvas, visible: false, raf: 0, last: 0, w: 0, h: 0, mx: 0, my: 0, tx: 0, ty: 0, meteor: null, next: 2500 };
    const size = () => {
      const dpr = Math.min(devicePixelRatio || 1, 2);
      f.w = canvas.clientWidth; f.h = canvas.clientHeight;
      canvas.width = Math.round(f.w * dpr); canvas.height = Math.round(f.h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    const draw = (now) => {
      f.raf = 0;
      if (f.last && now - f.last < 6) { if (f.visible && !reduced) f.raf = requestAnimationFrame(draw); return; }
      const dt = Math.min(64, now - (f.last || now)); f.last = now;
      f.mx += (f.tx - f.mx) * Math.min(1, dt / 400);
      f.my += (f.ty - f.my) * Math.min(1, dt / 400);
      const scroll = opts.scroll ? opts.scroll() : 0;
      ctx.clearRect(0, 0, f.w, f.h);
      ctx.fillStyle = '#fff';
      for (const s of stars) {
        if (!reduced) { s.x -= dt * .0000045 * (.3 + s.z); s.y -= dt * .0000018 * (.3 + s.z); }
        if (s.x < 0) s.x += 1; if (s.y < 0) s.y += 1;
        let x = s.x * f.w + f.mx * (6 + s.z * 26);
        let y = s.y * f.h + f.my * (4 + s.z * 18) - scroll * (40 + s.z * 220);
        x = ((x % f.w) + f.w) % f.w; y = ((y % f.h) + f.h) % f.h;
        const tw = Math.sin(now * .001 * s.sp + s.ph);
        ctx.globalAlpha = (.15 + .6 * tw * tw) * (.35 + s.z * .65);
        ctx.beginPath(); ctx.arc(x, y, s.r, 0, 6.2832); ctx.fill();
        if (s.r > 1.4) { ctx.globalAlpha *= .18; ctx.beginPath(); ctx.arc(x, y, s.r * 3.2, 0, 6.2832); ctx.fill(); }
      }
      if (!reduced) {
        f.next -= dt;
        if (!f.meteor && f.next <= 0) {
          f.meteor = { x: (.2 + Math.random() * .7) * f.w, y: Math.random() * f.h * .35, t: 0, a: 2.45 + Math.random() * .3, len: 140 + Math.random() * 160 };
          f.next = 4000 + Math.random() * 6000;
        }
        const m = f.meteor;
        if (m) {
          m.t += dt / 1100;
          const d = m.t * m.len * 3.2;
          const hx = m.x + Math.cos(m.a) * d, hy = m.y + Math.sin(m.a) * d;
          const tx = hx - Math.cos(m.a) * m.len, ty = hy - Math.sin(m.a) * m.len;
          const fade = Math.sin(Math.min(1, m.t) * Math.PI);
          const grd = ctx.createLinearGradient(hx, hy, tx, ty);
          grd.addColorStop(0, `rgba(255,255,255,${.9 * fade})`); grd.addColorStop(.2, `rgba(167,243,196,${.45 * fade})`); grd.addColorStop(1, 'rgba(167,243,196,0)');
          ctx.globalAlpha = 1; ctx.strokeStyle = grd; ctx.lineWidth = 1.6; ctx.lineCap = 'round';
          ctx.beginPath(); ctx.moveTo(hx, hy); ctx.lineTo(tx, ty); ctx.stroke();
          if (m.t >= 1) f.meteor = null;
        }
      }
      ctx.globalAlpha = 1;
      if (f.visible && !reduced) f.raf = requestAnimationFrame(draw);
    };
    f.kick = () => { if (!f.raf && f.visible) f.raf = requestAnimationFrame(draw); };
    if (!reduced && matchMedia('(hover: hover) and (pointer: fine)').matches) {
      (opts.pointerEl || canvas.parentElement).addEventListener('pointermove', (ev) => {
        f.tx = (ev.clientX / innerWidth - .5) * -2; f.ty = (ev.clientY / innerHeight - .5) * -2;
      });
    }
    new ResizeObserver(() => { size(); f.last = 0; if (!f.raf) f.raf = requestAnimationFrame(draw); }).observe(canvas);
    new IntersectionObserver(([e]) => { f.visible = e.isIntersecting; if (f.visible) { f.last = 0; f.kick(); } }).observe(canvas);
    starfields.push(f);
    return f;
  }

  const scrollers = [];
  let ticking = false;
  const tick = () => {
    ticking = false;
    const vh = innerHeight;
    scrollers.forEach((s) => {
      if (s.page === undefined) s.page = s.el.closest('.page');
      if (s.page && s.page.hidden) return;
      const r = s.el.getBoundingClientRect();
      if (r.bottom < -vh * .2 || r.top > vh * 1.2) return;
      s.fn(r, vh);
    });
  };
  const requestTick = () => { if (!ticking) { ticking = true; requestAnimationFrame(tick); } };
  addEventListener('scroll', requestTick, { passive: true });
  addEventListener('resize', requestTick);
  const pinP = (r, vh) => clamp01(-r.top / (r.height - vh));

  document.querySelectorAll('[data-pc-window]').forEach((device) => {
    const src = document.getElementById('app');
    if (!src) return;
    const make = (slot, on) => {
      const w = src.cloneNode(true);
      w.removeAttribute('id');
      w.setAttribute('inert', '');
      w.querySelectorAll('[id]').forEach((el) => el.removeAttribute('id'));
      w.dataset.state = on ? 'on' : 'off';
      const q = (sel) => w.querySelector(sel);
      if (on) {
        w.querySelectorAll('.dot').forEach((d) => d.classList.add('on'));
        w.querySelector('.xw__power').setAttribute('aria-pressed', 'true');
        q('.xw__pstatus').textContent = 'Подключено';
        q('.xw__ptimer').textContent = '00:05';
        q('[data-stat="down"]').innerHTML = '195,3 <i>Мбит/с</i>';
        q('[data-stat="up"]').innerHTML = '54,8 <i>Мбит/с</i>';
        q('[data-stat="ping"]').innerHTML = '11 <i>мс</i>';
        q('.xw__progress').style.setProperty('--p', '100%');
        q('.xw__progress').style.setProperty('--pk', '1');
        q('.xw__line').textContent = 'Всё уже работает';
      }
      const rows = on
        ? [['ok', 'Служба обхода блокировок Zapret запущена.'], ['ok', 'Прокси для Telegram запущен на порту 1080.'], ['ok', 'Zapret включен. Discord и YouTube должны работать нормально.'], ['ok', 'Прокси настроен. Telegram должен работать стабильно.']]
        : [['info', 'Инициализация компонентов системы...'], ['warn', 'Обход блокировок (Zapret): [ ВЫКЛЮЧЕН ]'], ['warn', 'Прокси для Telegram: [ ВЫКЛЮЧЕН ]'], ['ok', 'Права администратора (UAC): [ ПОДТВЕРЖДЕНЫ ]']];
      q('.xw__lines').innerHTML = rows.map(([t, text]) => '<div><span class="t">[21:15:0' + (on ? 5 : 1) + ']</span><span class="p ' + (t === 'info' ? '' : t) + '">[' + (t === 'info' ? 'INFO' : t.toUpperCase()) + ']</span>' + text + '</div>').join('');
      slot.append(w);
    };
    make(device.querySelector('.pc-win--off'), false);
    make(device.querySelector('.pc-win--on'), true);

    const SP = (n) => '&nbsp;'.repeat(n);
    const row = (time, type, text) => {
      if (type === 'gap') return '<div>&nbsp;</div>';
      if (type === 'rocket') return '<div class="rocket"><span>' + text + '</span></div>';
      const t = '<span class="t">[' + time + ']</span>';
      const tag = { ok: '<span class="p ok">[OK]</span>', warn: '<span class="p warn">[WARN]</span>', sys: '<span class="p">[#]</span>', info: '<span class="p">[INFO]</span>', net: '<span class="p net">[NET]</span>' }[type];
      return '<div' + (type === 'sys' ? ' class="bold"' : '') + '>' + t + tag + text + '</div>';
    };
    const BASE = [
      ['', 'rocket', '🚀 Добрый день! Добро пожаловать в NetFix 🚀'],
      ['17:00:38', 'info', 'Инициализация компонентов системы...'],
      ['17:00:38', 'info', 'Мой Telegram-канал: <a href="https://t.me/NetFixRuBi" target="_blank" rel="noopener" tabindex="-1">t.me/NetFixRuBi</a> - информация об обновлениях, новые способы обходов и гайды. Максимально полезная информация, советую подписаться'],
      ['', 'gap'],
      ['17:00:38', 'sys', 'СТАТУС СЕРВИСОВ И ПРИЛОЖЕНИЙ'],
      ['17:00:38', 'warn', 'Обход блокировок (Zapret):' + SP(4) + '<span class="s-warn">[ ВЫКЛЮЧЕН ]</span>'],
      ['17:00:39', 'warn', 'Прокси для Telegram:' + SP(10) + '<span class="s-warn">[ ВЫКЛЮЧЕН ]</span>'],
      ['17:00:39', 'ok', 'Права администратора (UAC):' + SP(3) + '<span class="s-ok">[ ПОДТВЕРЖДЕНЫ ]</span>'],
      ['', 'gap'],
      ['17:00:39', 'info', 'Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».'],
    ];
    const RUN = [
      ['', 'gap'],
      ['17:00:55', 'sys', 'БЫСТРЫЙ ЗАПУСК [ 17:00:55 ]'],
      ['', 'gap'],
      ['17:00:55', 'sys', 'СОСТОЯНИЕ СЕРВИСОВ'],
      ['17:00:55', 'net', 'Telegram Desktop: <span class="s-warn">[ НЕ ЗАПУЩЕН ]</span>'],
      ['17:00:55', 'net', 'Discord App:' + SP(6) + '<span class="s-warn">[ НЕ ЗАПУЩЕН ]</span>'],
      ['', 'gap'],
      ['17:00:55', 'sys', 'СЕТЕВАЯ СРЕДА'],
      ['17:00:56', 'ok', 'Интернет-соединение: <span class="s-ok">[ ПОДКЛЮЧЕНО ]</span>'],
      ['', 'gap'],
      ['17:00:56', 'sys', 'ЗАПУСК ИСПРАВЛЕНИЙ'],
      ['17:00:56', 'info', 'Проверяю подключение к интернету…'],
      ['17:00:56', 'ok', 'Интернет есть'],
      ['17:00:56', 'info', 'Проверяю последние версии инструментов…'],
      ['17:00:56', 'ok', 'Zapret найден: C:\\Zapret\\service.bat'],
      ['17:00:56', 'ok', 'tg-ws-proxy найден: C:\\Zapret\\TgWsProxy.exe'],
      ['17:00:56', 'info', 'Zapret не запущен — запускаю...'],
      ['17:00:57', 'ok', '✓ Zapret запущен'],
      ['17:00:57', 'info', 'TgWsProxy не запущен — запускаю...'],
      ['17:00:57', 'ok', '✓ TgWsProxy запущен'],
      ['', 'gap'],
      ['', 'rocket', '🚀 Всё запущено и всё работает НОРМАЛЬНО!'],
      ['17:00:58', 'ok', 'Zapret включен. Discord и YouTube должны работать нормально.'],
      ['17:00:58', 'ok', 'Прокси настроен. Telegram должен работать стабильно.'],
      ['17:00:58', 'info', 'Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».'],
    ];
    const ZAP = RUN.findIndex((r) => r[2] === '✓ Zapret запущен');
    const TGW = RUN.findIndex((r) => r[2] === '✓ TgWsProxy запущен');
    const html = (n) => BASE.concat(RUN.slice(0, n)).map(([t, ty, tx]) => row(t, ty, tx)).join('');
    const STAGES = [[0, 'Байпас отключен'], [.02, 'Проверка состояния сервисов...'], [.3, 'Проверка сети...'], [.45, 'Запуск службы Zapret...'], [.75, 'Запуск прокси Telegram...'], [.95, 'Всё уже работает']];

    const w = device.querySelector('.pc-win--off .xw');
    const q = (sel) => w.querySelector(sel);
    const view = q('.xw__view'), lines = q('.xw__lines');
    const wOn = device.querySelector('.pc-win--on .xw');
    const qOn = (sel) => wOn.querySelector(sel);
    wOn.querySelector('.xw__lines').innerHTML = html(RUN.length);
    requestAnimationFrame(() => { const v = wOn.querySelector('.xw__view'); v.scrollTop = v.scrollHeight; });
    let shown = -1, clock = 0, sec = 0, running = false;
    const timer = (on) => {
      if (on === running) return;
      running = on; clearInterval(clock); sec = 0; qOn('.xw__ptimer').textContent = '00:00';
      if (on) clock = setInterval(() => { sec++; qOn('.xw__ptimer').textContent = String(Math.floor(sec / 60)).padStart(2, '0') + ':' + String(sec % 60).padStart(2, '0'); }, 1000);
    };
    device.scrub = (t, on) => {
      const st = t <= 0 ? 'off' : 'connecting';
      if (w.dataset.state !== st) w.dataset.state = st;
      const n = Math.round(t * RUN.length);
      if (n !== shown) {
        if (shown < 0) { lines.innerHTML = html(n); } else {
          while (lines.childElementCount > BASE.length + n) lines.lastElementChild.remove();
          for (let i = shown; i < n; i++) lines.insertAdjacentHTML('beforeend', row(...RUN[i]));
        }
        shown = n;
        view.scrollTop = view.scrollHeight;
      }
      q('.xw__progress').style.setProperty('--pk', t.toFixed(3));
      const stage = STAGES.filter(([at]) => t >= at).pop()[1];
      if (q('.xw__line').textContent !== stage) q('.xw__line').textContent = stage;
      q('[data-dot="zapret"]').classList.toggle('on', n > ZAP);
      q('[data-dot="tg"]').classList.toggle('on', n > TGW);
      q('[data-dot="vpn"]').classList.toggle('on', n > TGW);
      const label = st === 'off' ? 'Починить' : 'Подключение';
      if (q('.xw__pstatus').textContent !== label) q('.xw__pstatus').textContent = label;
      timer(on > .5);
    };
    device.scrub(0, 0);
    new ResizeObserver(([e]) => device.style.setProperty('--s', e.contentRect.width / 880)).observe(device);
    const btn = device.querySelector('.pc-win--off .xw__power');
    const root = btn.closest('.xw');
    device.measureBtn = () => {
    let x = btn.offsetWidth / 2, y = btn.offsetHeight / 2;
    for (let el = btn; el && el !== root; el = el.offsetParent) { x += el.offsetLeft; y += el.offsetTop; }
    if (root.offsetWidth) {
      device.dataset.bx = x / root.offsetWidth; device.dataset.by = y / root.offsetHeight;
      device.style.setProperty('--btx', (x / root.offsetWidth * 100).toFixed(2) + '%');
      device.style.setProperty('--bty', (y / root.offsetHeight * 100).toFixed(2) + '%');
      device.style.setProperty('--btw', (btn.offsetWidth / root.offsetWidth).toFixed(4));
    }
    };
    device.measureBtn();
    addEventListener('resize', () => device.measureBtn());
  });

  document.querySelectorAll('.and-intro').forEach((intro) => {
    const sticky = intro.querySelector('.and-intro__sticky');
    const copy = intro.querySelector('.and-intro__copy');
    const stage = intro.querySelector('.and-intro__stage');
    const phone = intro.querySelector('.and-intro__phone');
    let bx = .499, by = .417;
    const btnPos = () => { if (phone.measureBtn) phone.measureBtn(); bx = +phone.dataset.bx || .499; by = +phone.dataset.by || .417; };
    btnPos();
    const widgets = [...intro.querySelectorAll('.and-widget')];
    const fade = document.createElement('span');
    fade.className = 'and-intro__fade'; fade.setAttribute('aria-hidden', 'true');
    sticky.append(fade);
    const pick = (sel) => [...intro.querySelectorAll(sel)];
    const pEls = [copy, fade, ...pick('[data-dots], .and-intro__hint, .and-intro__card')];
    const eEls = [copy, stage, phone, ...pick('.and-intro__sky, .and-intro__off, .and-intro__phone .and-glare')];
    const onEls = [phone, ...pick('.and-intro__sky, .and-intro__bloom, .and-intro__on, .pc-glow')];
    const bloom = intro.querySelector('.and-intro__bloom');
    const setAll = (els, name, v) => { for (const el of els) el.style.setProperty(name, v); };
    let lastP = 0;
    starfield(intro.querySelector('[data-stars]'), { n: 260, scroll: () => lastP });
    const dots = globe(intro.querySelector('[data-dots]'), {
      n: 1300,
      center: (g) => {
        const c = g.canvas.getBoundingClientRect(), r = phone.getBoundingClientRect();
        return { x: r.left - c.left + r.width * bx, y: r.top - c.top + r.height * by, r: Math.max(r.width * (phone.dataset.bx ? .62 : 1.3), 260) };
      },
    });
    let lit = false;
    const start = () => {
      intro.classList.remove('is-live');
      void intro.offsetWidth;
      intro.classList.add('is-live');
    };
    const measure = () => {
      const vh = innerHeight;
      const ph = stage.offsetHeight;
      const copyBottom = copy.offsetTop + copy.offsetHeight;
      stage.style.setProperty('--istart', `${Math.max(0, copyBottom + 26 - (vh - ph * .92) / 2)}px`);
    };
    addEventListener('resize', measure);
    scrollers.push({
      el: intro,
      fn: (r, vh) => {
        const p = reduced ? 0 : pinP(r, vh);
        const e = smooth(clamp01((p - .02) / .62));
        const on = reduced ? 1 : smooth(clamp01((p - .03) / .2));
        lastP = p;
        setAll(pEls, '--ip', p.toFixed(4));
        setAll(eEls, '--ie', e.toFixed(4));
        let lightNow = on;
        if (phone.scrub) {
          const t = reduced ? 1 : clamp01((p - .012) / .12);
          const lightUp = reduced ? 1 : smooth(clamp01((p - .12) / .1));
          phone.scrub(t, lightUp);
          lightNow = lightUp;
        }
        setAll(onEls, '--ion', lightNow.toFixed(4));
        const onNow = lightNow;
        if (!lit && onNow > .55) { lit = true; intro.classList.add('is-on'); dots.set(true, true); }
        if (lit && onNow < .25) { lit = false; intro.classList.remove('is-on'); dots.set(false); }
        intro.classList.toggle('is-gone', p > .42);
        intro.classList.toggle('is-up', e > .999);
        const w0 = .42;
        widgets.forEach((w, i) => w.style.setProperty('--k', easeOut(clamp01((p - w0 - i * .06) / .3)).toFixed(4)));
        const pr = phone.getBoundingClientRect(), sr = sticky.getBoundingClientRect();
        bloom.style.setProperty('--iby', `${pr.top - sr.top + pr.height * by}px`);
        dots.kick();
      },
    });
    if (!reduced && matchMedia('(hover: hover) and (pointer: fine)').matches) {
      sticky.addEventListener('pointermove', (ev) => {
        const x = ((ev.clientX / innerWidth) - .5) * 2, y = ((ev.clientY / innerHeight) - .5) * 2;
        for (const w of widgets) { w.style.setProperty('--px', x); w.style.setProperty('--py', y); }
      });
    }
    intro.start = () => { btnPos(); measure(); start(); };
  });

  document.querySelectorAll('.and-words').forEach((wordsSec) => {
    const lines = [...wordsSec.querySelectorAll('.and-words__line')].map((l) => [...l.children]);
    const para = wordsSec.querySelector('[data-assemble]');
    splitWords(para);
    const pw = [...para.querySelectorAll('.w')];
    const n = pw.length;
    const seeds = pw.map((_, i) => [(rnd(i) - .5) * 640, (rnd(i + 91) - .5) * 260, (rnd(i + 37) - .5) * 40]);
    const set = (el, x, y, s, rot, o, b) => {
      el.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0) scale(${s.toFixed(3)}) rotate(${rot.toFixed(1)}deg)`;
      el.style.opacity = o.toFixed(3);
      el.style.filter = b > .2 ? `blur(${b.toFixed(1)}px)` : 'none';
    };
    if (!reduced) scrollers.push({
      el: wordsSec,
      fn: (r, vh) => {
        const p = pinP(r, vh);
        wordsSec.style.setProperty('--p', p.toFixed(4));
        const enter = clamp01((vh - r.top) / (vh * .85));
        const spread = innerWidth * .24;
        let j = 0;
        lines.forEach((items, k) => {
          const t = clamp01((p - (.06 + k * .09)) / .28);
          const te = smooth(t);
          items.forEach((el) => {
            const q = easeOut(clamp01(enter * 1.5 - j++ * .06));
            const chip = el.classList.contains('and-chip');
            const side = +el.dataset.side || 0;
            const fade = 1 - clamp01((t - (chip ? .35 : .12)) / .55);
            set(el,
              side * te * spread,
              (1 - q) * 70 - (chip ? te * 40 : 0),
              chip ? 1 - te * .3 : 1,
              chip ? te * -12 : 0,
              q * fade,
              (1 - q) * 12 + t * (chip ? 10 : 18));
          });
        });
        pw.forEach((el, i) => {
          const s = .5 + (i / n) * .3;
          const q = easeOut(clamp01((p - s) / .16));
          const [dx, dy, rot] = seeds[i];
          set(el, dx * (1 - q), dy * (1 - q), .8 + q * .2, rot * (1 - q), q, (1 - q) * 14);
        });
      },
    });
  });

  document.querySelectorAll('.and-story').forEach((story) => {
    const stepsEls = [...story.querySelectorAll('.and-story__step')];
    const shots = [...story.querySelectorAll('.and-story__tilt > [data-steps]')];
    const notes = [...story.querySelectorAll('.and-note')];
    const tilt = story.querySelector('.and-story__tilt');
    const bar = story.querySelector('.and-story__bar');
    const show = (i) => {
      story.dataset.screen = i;
      stepsEls.forEach((el, k) => el.classList.toggle('is-on', k === i));
      shots.forEach((img, k) => img.classList.toggle('is-on', k === 0 || Math.min(...img.dataset.steps.split(' ').map(Number)) <= i));
      notes.forEach((el) => el.classList.toggle('is-on', +el.dataset.step === i));
      bar.style.setProperty('--p', (i + 1) / stepsEls.length);
    };
    const storyIO = new IntersectionObserver((entries) => entries.forEach((e) => {
      if (e.isIntersecting) show(+e.target.dataset.step);
    }), { rootMargin: '-45% 0px -45% 0px' });
    stepsEls.forEach((el) => storyIO.observe(el));
    show(0);
    if (!reduced) scrollers.push({
      el: story,
      fn: (r, vh) => {
        const sp = clamp01((vh - r.top) / (r.height + vh));
        tilt.style.setProperty('--ry', `${(Math.sin(sp * Math.PI * 3) * (story.classList.contains('and-story--pc') ? 5 : 9)).toFixed(2)}deg`);
        tilt.style.setProperty('--rx', `${(Math.cos(sp * Math.PI * 2) * 4).toFixed(2)}deg`);
        tilt.style.setProperty('--sp', sp.toFixed(4));
      },
    });
  });

  if (!reduced) document.querySelectorAll('.and-float').forEach((float) => scrollers.push({ el: float, fn: (r, vh) => float.style.setProperty('--p', pinP(r, vh).toFixed(4)) }));

  document.querySelectorAll('[data-say]').forEach((say) => {
    splitWords(say);
    const words = [...say.querySelectorAll('.w')];
    scrollers.push({
      el: say,
      fn: (r, vh) => {
        const p = reduced ? 1 : clamp01((vh * .85 - r.top) / (r.height + vh * .45));
        const lit = Math.round(p * words.length);
        words.forEach((w, k) => w.classList.toggle('is-lit', k < lit));
      },
    });
  });

  document.querySelectorAll('[data-and-app]').forEach((app) => {
    const btn = app.querySelector('.and-app__btn');
    const wrap = app.querySelector('.and-app__btnwrap');
    const label = app.querySelector('.and-app__label');
    const hint = app.querySelector('.and-app__hint');
    const log = [...app.querySelectorAll('.and-app__log li')];
    const ping = app.querySelector('[data-ping]');
    const quality = app.querySelector('[data-quality]');
    const dots = globe(app.querySelector('[data-dots]'), {
      n: 700, dot: .85, alpha: .8,
      center: (g) => {
        const c = g.canvas.getBoundingClientRect(), r = wrap.getBoundingClientRect();
        return { x: r.left - c.left + r.width / 2, y: r.top - c.top + r.height / 2, r: r.width * .92 };
      },
    });
    let timers = [];
    let clock = 0;
    const later = (fn, ms) => timers.push(setTimeout(fn, ms));
    const mark = (i) => log.forEach((li, k) => { li.classList.toggle('is-done', k < i); li.classList.toggle('is-active', k === i); });
    const fmt = (sec) => [sec / 3600, (sec / 60) % 60, sec % 60].map((v) => String(Math.floor(v)).padStart(2, '0')).join(':');
    function off() {
      timers.forEach(clearTimeout); timers = []; clearInterval(clock);
      app.dataset.state = 'off'; btn.setAttribute('aria-pressed', 'false');
      label.textContent = 'Включить'; hint.textContent = 'нажмите, чтобы начать';
      ping.innerHTML = '— <i>мс</i>'; quality.textContent = 'Нет соединения';
      mark(-1);
      dots.set(false);
    }
    function on() {
      const STEP = reduced ? 0 : 800;
      app.dataset.state = 'busy'; label.textContent = 'Подключение'; hint.textContent = '…';
      log.forEach((li, i) => later(() => mark(i), i * STEP));
      later(() => {
        mark(log.length);
        app.dataset.state = 'on'; btn.setAttribute('aria-pressed', 'true');
        label.textContent = 'Подключено';
        dots.set(true, true);
        let sec = 0;
        hint.textContent = fmt(sec);
        clock = setInterval(() => { hint.textContent = fmt(++sec); }, 1000);
        quality.textContent = 'Отличная';
        const t0 = performance.now();
        const count = (now) => {
          const k = Math.min(1, (now - t0) / 700);
          ping.innerHTML = `${Math.round(45 * (1 - Math.pow(1 - k, 3)))} <i>мс</i>`;
          if (k < 1) requestAnimationFrame(count);
        };
        requestAnimationFrame(count);
      }, log.length * STEP);
    }
    btn.addEventListener('click', () => {
      if (app.dataset.state === 'busy') return;
      if (app.dataset.state === 'on') off(); else on();
    });
  });

  document.querySelectorAll('[data-stars]:not(.and-intro [data-stars])').forEach((c) => starfield(c, { n: +c.dataset.stars || 120 }));

  document.querySelectorAll('[data-dots="final"]').forEach((finalDots) => globe(finalDots, { n: 1000, on: 1, alpha: .8, stars: 90, center: (g) => ({ x: g.w * (g.w > 900 ? .74 : .5), y: g.h * .98, r: Math.min(g.w * .3, 380) }) }));

  document.querySelectorAll('[data-waves]').forEach((canvas) => {
    const ctx = canvas.getContext('2d');
    let w = 0, h = 0, visible = false, raf = 0, drawn = 0, grad = null;
    const size = () => {
      const dpr = Math.min(devicePixelRatio || 1, 1.5);
      w = canvas.clientWidth; h = canvas.clientHeight;
      canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      grad = null;
    };
    const draw = (now) => {
      raf = 0;
      if (visible && !reduced && now - drawn < 12) { raf = requestAnimationFrame(draw); return; }
      drawn = now;
      const t = reduced ? 0 : now * .00035;
      ctx.clearRect(0, 0, w, h);
      ctx.save();
      ctx.translate(w / 2, h / 2);
      ctx.rotate(-.5);
      const span = Math.hypot(w * .42, h * .4);
      const L = 44, gap = (span * 2) / L;
      if (!grad) {
        grad = ctx.createLinearGradient(-span * .7, 0, span * .7, 0);
        grad.addColorStop(0, '#0d9488'); grad.addColorStop(.45, '#22c55e'); grad.addColorStop(1, '#bbf7d0');
      }
      ctx.strokeStyle = grad; ctx.lineWidth = Math.max(2, w / 560); ctx.lineCap = 'round';
      ctx.beginPath();
      for (let i = 0; i < L; i++) {
        const y0 = -span + i * gap;
        for (let x = -span; x <= span; x += 18) {
          const y = y0 + Math.sin(x * .0042 + t * 2 + i * .22) * 34 + Math.sin(x * .011 - t * 3.2 + i * .41) * 12;
          x === -span ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
        }
      }
      ctx.stroke();
      ctx.restore();
      if (visible && !reduced) raf = requestAnimationFrame(draw);
    };
    new ResizeObserver(() => { size(); if (!raf) raf = requestAnimationFrame(draw); }).observe(canvas);
    new IntersectionObserver(([e]) => { visible = e.isIntersecting; if (visible && !raf) raf = requestAnimationFrame(draw); }).observe(canvas);
  });
  if (!reduced) document.querySelectorAll('.and-name').forEach((nameSec) => scrollers.push({ el: nameSec, fn: (r, vh) => nameSec.style.setProperty('--p', clamp01((vh - r.top) / (vh + r.height)).toFixed(4)) }));

  if (!reduced) document.querySelectorAll('.and-stack__list').forEach((stackList) => {
    const cards = [...stackList.querySelectorAll('.and-stack__card')];
    scrollers.push({
      el: stackList,
      fn: () => cards.forEach((card, i) => {
        const next = cards[i + 1];
        if (!next) return;
        const r1 = card.getBoundingClientRect(), r2 = next.getBoundingClientRect();
        card.style.setProperty('--k', clamp01((r1.bottom - r2.top) / r1.height).toFixed(4));
      }),
    });
  });

  if (!reduced && matchMedia('(hover: hover) and (pointer: fine)').matches) {
    document.querySelectorAll('.and-hw').forEach((card) => {
      const inner = card.querySelector('.and-hw__in');
      card.addEventListener('pointermove', (ev) => {
        const r = card.getBoundingClientRect();
        const x = (ev.clientX - r.left) / r.width, y = (ev.clientY - r.top) / r.height;
        card.style.setProperty('--ry', ((x - .5) * 14).toFixed(2) + 'deg');
        card.style.setProperty('--rx', ((.5 - y) * 14).toFixed(2) + 'deg');
        inner.style.setProperty('--mx', (x * 100).toFixed(1) + '%');
        inner.style.setProperty('--my', (y * 100).toFixed(1) + '%');
      });
      card.addEventListener('pointerleave', () => { card.style.setProperty('--rx', '0deg'); card.style.setProperty('--ry', '0deg'); });
    });
  }

  if (matchMedia('(hover: hover) and (pointer: fine)').matches) {
    document.querySelectorAll('.and-tile').forEach((tile) => tile.addEventListener('pointermove', (ev) => {
      const r = tile.getBoundingClientRect();
      tile.style.setProperty('--mx', `${ev.clientX - r.left}px`);
      tile.style.setProperty('--my', `${ev.clientY - r.top}px`);
    }));
  }

  [pcPage, mobPage].forEach((page) => {
    if (!page) return;
    const opened = () => {
      if (page.hidden) return;
      requestAnimationFrame(() => {
        page.querySelectorAll('.and-intro').forEach((intro) => intro.start && intro.start());
        globes.forEach((g) => g.kick()); starfields.forEach((f) => f.kick()); requestTick();
      });
    };
    new MutationObserver(opened).observe(page, { attributes: true, attributeFilter: ['hidden'] });
    opened();
  });

  if (/^#and/.test(location.hash)) {
    const el = document.getElementById(location.hash.slice(1));
    setMode('mob', { scroll: false });
    if (el) requestAnimationFrame(() => el.scrollIntoView());
  }

  const canvas = document.querySelector('.bg');
  const gl = canvas.getContext('webgl', { antialias: false, alpha: false, powerPreference: 'low-power' });
  if (!gl) return;

  const FRAG = `
    precision mediump float;
    uniform vec2 r;
    uniform float t;
    uniform float k;
    uniform float e;

    vec3 silk(vec2 p, float tt, vec3 lo, vec3 hi) {
      vec3 col = vec3(0.0);
      for (int i = 0; i < 24; i++) {
        float f = float(i) / 23.0;
        float tw = sin(p.x * 0.9 + tt * 0.23);
        float y = 0.30 * sin(p.x * 1.35 + tt * 0.31)
                + 0.10 * sin(p.x * 2.70 - tt * 0.19 + f * 1.4)
                + (f - 0.5) * (0.10 + 0.32 * tw * tw);
        float d = p.y - y;
        col += mix(lo, hi, f) * (exp(-d * d * 3200.0) * 0.085 + 0.0007 / (abs(d) + 0.0025) * 0.02);
      }
      float v = 1.0 - smoothstep(0.25, 1.35, length(p * vec2(0.7, 1.1)));
      return col * (0.3 + 0.7 * v);
    }

    void main() {
      vec2 q = (gl_FragCoord.xy - 0.5 * r) / r.y;
      float a = mix(mix(-0.25, 1.15, k), 0.0, e);
      float c = cos(a), s = sin(a);
      mat2 rot = mat2(c, -s, s, c);
      vec2 off = vec2(0.0, mix(mix(-0.03, 0.62, k), 0.12, e));
      vec3 g = silk(rot * q + off, t, vec3(0.02, 0.32, 0.24), vec3(0.30, 0.95, 0.55));
      vec3 bl = silk(rot * vec2(-q.x, q.y) + off, t * 0.9 + 23.0, vec3(0.03, 0.10, 0.32), vec3(0.37, 0.58, 1.0));

      vec3 col = vec3(0.043, 0.047, 0.043);
      col += g * mix(mix(1.0, 0.75, k), 0.95, e);
      col += bl * 0.85 * smoothstep(0.2, 0.95, k) * (1.0 - smoothstep(0.0, 0.7, e));
      gl_FragColor = vec4(col, 1.0);
    }`;
  const VERT = 'attribute vec2 a; void main() { gl_Position = vec4(a, 0.0, 1.0); }';

  function shader(type, src) {
    const sh = gl.createShader(type);
    gl.shaderSource(sh, src);
    gl.compileShader(sh);
    return sh;
  }
  const prog = gl.createProgram();
  gl.attachShader(prog, shader(gl.VERTEX_SHADER, VERT));
  gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, FRAG));
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) return;
  gl.useProgram(prog);

  gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
  const loc = gl.getAttribLocation(prog, 'a');
  gl.enableVertexAttribArray(loc);
  gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
  const uR = gl.getUniformLocation(prog, 'r');
  const uT = gl.getUniformLocation(prog, 't');
  const uK = gl.getUniformLocation(prog, 'k');
  const uE = gl.getUniformLocation(prog, 'e');

  const SCALE = innerWidth < 760 ? 0.34 : 0.45;
  function resize() {
    const w = Math.max(1, Math.round(innerWidth * SCALE));
    const h = Math.max(1, Math.round(innerHeight * SCALE));
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
      gl.viewport(0, 0, w, h);
    }
  }

  let kTarget = 0;
  let eTarget = 0;
  const readScroll = () => {
    kTarget = Math.min(1, scrollY / (innerHeight * 0.9));
    const left = document.documentElement.scrollHeight - innerHeight - scrollY;
    eTarget = Math.min(1, Math.max(0, 1 - left / (innerHeight * 2)));
  };
  addEventListener('scroll', readScroll, { passive: true });
  readScroll();
  let kNow = kTarget;
  let eNow = eTarget;

  const T0 = 40;
  function draw(time) {
    resize();
    kNow += (kTarget - kNow) * 0.06;
    eNow += (eTarget - eNow) * 0.04;
    gl.uniform2f(uR, canvas.width, canvas.height);
    gl.uniform1f(uT, T0 + time / 1000);
    gl.uniform1f(uK, kNow);
    gl.uniform1f(uE, eNow);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  let raf = 0;
  let last = 0;
  function loop(now) {
    raf = requestAnimationFrame(loop);
    if (now - last < 33) return;
    last = now;
    draw(now);
  }
  function sync() {
    const run = !document.hidden && !reduced;
    if (run && !raf) raf = requestAnimationFrame(loop);
    if (!run && raf) { cancelAnimationFrame(raf); raf = 0; }
  }

  draw(0);
  canvas.classList.add('is-on');
  if (reduced) {
    addEventListener('scroll', () => { kNow = kTarget; eNow = eTarget; draw(0); }, { passive: true });
    return;
  }
  document.addEventListener('visibilitychange', sync);
  sync();
})();
