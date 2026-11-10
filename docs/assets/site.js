(() => {
  const groups = [
    ['.hero-copy > *', 70],
    ['.hero-shot', 0],
    ['.fact-grid > div', 80],
    ['.section-heading > *', 70],
    ['.feature-card', 70],
    ['.step-grid > li', 90],
    ['.chip-row > li', 60],
    ['.editor-grid > *', 100],
    ['.shot-grid > *', 80],
    ['.docs-intro > *', 60],
    ['.doc-links > a', 60],
    ['.download-card > *', 100],
  ];

  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // Clips are silent and loop. With reduced motion they wait for the visitor to press play.
  document.querySelectorAll('video[data-autoplay]').forEach((video) => {
    if (reducedMotion) {
      video.controls = true;
      return;
    }
    video.muted = true;
    const start = () => video.play().catch(() => { video.controls = true; });
    if (!('IntersectionObserver' in window)) {
      start();
      return;
    }
    new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          start();
        } else {
          video.pause();
        }
      }
    }, { threshold: 0.35 }).observe(video);
  });

  if (!('IntersectionObserver' in window) || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    return;
  }

  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) {
        continue;
      }

      const element = entry.target;
      const delay = Number.parseInt(element.style.getPropertyValue('--reveal-delay'), 10) || 0;
      element.classList.add('is-revealed');
      observer.unobserve(element);
      window.setTimeout(() => {
        element.classList.remove('is-revealed');
        element.removeAttribute('data-reveal');
        element.style.removeProperty('--reveal-delay');
      }, delay + 800);
    }
  }, {
    rootMargin: '0px 0px -8% 0px',
    threshold: 0.08,
  });

  for (const [selector, stagger] of groups) {
    document.querySelectorAll(selector).forEach((element, index) => {
      element.dataset.reveal = '';
      element.style.setProperty('--reveal-delay', `${Math.min(index, 2) * stagger}ms`);
      observer.observe(element);
    });
  }

  document.documentElement.classList.add('reveal-ready');
})();
