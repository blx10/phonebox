// Shared by the panel and desktop editor. Omitted values preserve the original look.
(() => {
  'use strict';
  const presets = Object.freeze({
    default: {},
    cards: { radius: 18, gap: 12, padding: 12, border: true, shadow: false },
    flat: { radius: 12, gap: 12, padding: 12, border: false, shadow: false },
    borderless: { radius: 0, gap: 12, padding: 12, border: false, shadow: false },
    seamless: { radius: 0, gap: 0, padding: 12, border: true, shadow: false },
  });
  const limits = { radius: 32, gap: 24, padding: 24, pagePadding: 32 };
  function resolve(...layers) {
    let result = {};
    for (const layer of layers) {
      if (!layer || typeof layer !== 'object') continue;
      if (typeof layer.style === 'string' && Object.prototype.hasOwnProperty.call(presets, layer.style)) {
        result = layer.style === 'default' ? { style: 'default' } : { ...result, ...presets[layer.style], style: layer.style };
      }
      for (const [key, max] of Object.entries(limits))
        if (typeof layer[key] === 'number' && Number.isFinite(layer[key])) result[key] = Math.max(0, Math.min(max, Math.round(layer[key])));
      for (const key of ['border', 'shadow']) if (typeof layer[key] === 'boolean') result[key] = layer[key];
    }
    return result;
  }
  function widget(element, appearance = {}) {
    for (const key of ['radius', 'padding']) {
      const active = appearance[key] != null;
      element.classList.toggle('has-widget-' + key, active);
      if (active) element.style.setProperty('--widget-' + key, appearance[key] + 'px');
      else element.style.removeProperty('--widget-' + key);
    }
    for (const key of ['border', 'shadow']) {
      if (appearance[key] != null) element.dataset['widget' + key[0].toUpperCase() + key.slice(1)] = String(appearance[key]);
      else delete element.dataset['widget' + key[0].toUpperCase() + key.slice(1)];
    }
    if (appearance.style && appearance.style !== 'default') element.dataset.widgetSurface = appearance.style;
    else delete element.dataset.widgetSurface;
  }
  function page(element, grid, appearance = {}) {
    grid.style.gap = appearance.gap != null ? appearance.gap + 'px' : '';
    if (appearance.pagePadding != null) {
      const n = appearance.pagePadding + 'px';
      element.style.padding = 'max(' + n + ', env(safe-area-inset-top)) max(' + n + ', env(safe-area-inset-right)) max(' + n + ', 17px, env(safe-area-inset-bottom)) max(' + n + ', env(safe-area-inset-left))';
    } else element.style.padding = '';
  }
  globalThis.PhoneBoxAppearance = Object.freeze({ presets, resolve, widget, page });
})();
