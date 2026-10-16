// A small preview runtime for the design files in this folder, so a .dc.html file opens in a plain browser.
// It understands the subset the files use: {{path}} holes in text and attributes, <sc-for>, <sc-if>, <helmet>,
// on* handlers bound to functions, and a `class Component extends DCLogic` with renderVals() and setState().
// Props start from each prop's default in data-props; the query string overrides them: Harbor.dc.html?theme=Dark
(() => {
  'use strict';

  const ANY = /\{\{([^}]+)\}\}/g;
  const WHOLE = /^\s*\{\{([^}]+)\}\}\s*$/;
  let template = null;
  let root = null;
  let component = null;

  class DCLogic {
    constructor(props) {
      this.props = props;
      this.state = {};
    }

    setState(patch) {
      Object.assign(this.state, typeof patch === 'function' ? patch(this.state, this.props) : patch);
      render();
    }

    forceUpdate() {
      render();
    }
  }

  function lookup(path, scope) {
    const p = path.trim();
    if (p === 'true') return true;
    if (p === 'false') return false;
    if (p === 'null') return null;
    if (/^-?\d+(\.\d+)?$/.test(p)) return Number(p);
    if (/^'.*'$|^".*"$/.test(p)) return p.slice(1, -1);
    return p.split('.').reduce((value, key) => (value == null ? undefined : value[key]), scope);
  }

  function hole(attr, scope) {
    const m = WHOLE.exec(attr ?? '');
    return m ? lookup(m[1], scope) : attr;
  }

  function interpolate(text, scope) {
    return text.replace(ANY, (_, path) => {
      const value = lookup(path, scope);
      return value == null ? '' : String(value);
    });
  }

  function renderChildren(node, scope, out) {
    node.childNodes.forEach((child) => renderNode(child, scope, out));
  }

  function renderNode(node, scope, out) {
    if (node.nodeType === Node.TEXT_NODE) {
      out.appendChild(document.createTextNode(interpolate(node.nodeValue, scope)));
      return;
    }
    if (node.nodeType !== Node.ELEMENT_NODE) return;

    const tag = node.localName;
    if (tag === 'helmet') return;
    if (tag === 'sc-for') {
      const list = hole(node.getAttribute('list'), scope) || [];
      const name = node.getAttribute('as') || 'item';
      list.forEach((item, index) => renderChildren(node, { ...scope, [name]: item, $index: index }, out));
      return;
    }
    if (tag === 'sc-if') {
      if (hole(node.getAttribute('value'), scope)) renderChildren(node, scope, out);
      return;
    }

    const el = document.createElementNS(node.namespaceURI, node.tagName.includes(':') ? node.tagName : tag);
    for (const attr of node.attributes) {
      if (attr.name.startsWith('hint-')) continue;
      const whole = WHOLE.exec(attr.value);
      if (whole && attr.name.startsWith('on')) {
        const handler = lookup(whole[1], scope);
        if (typeof handler === 'function') el.addEventListener(attr.name.slice(2), handler);
        continue;
      }
      el.setAttribute(attr.name, interpolate(attr.value, scope));
    }
    renderChildren(node, scope, el);
    out.appendChild(el);
  }

  function render() {
    const fragment = document.createDocumentFragment();
    renderChildren(template, component.renderVals(), fragment);
    root.replaceChildren(fragment);
  }

  function start() {
    const host = document.querySelector('x-dc');
    const script = document.querySelector('script[data-dc-script]');
    if (!host || !script) return;

    const declared = JSON.parse(script.getAttribute('data-props') || '{}');
    const props = {};
    for (const [name, spec] of Object.entries(declared)) {
      if (!name.startsWith('$') && spec && 'default' in spec) props[name] = spec.default;
    }
    for (const [name, value] of new URLSearchParams(location.search)) props[name] = value;

    const helmet = host.querySelector('helmet');
    if (helmet) [...helmet.children].forEach((child) => document.head.appendChild(child));

    template = host;
    root = document.createElement('div');
    host.replaceWith(root);

    const Component = new Function('DCLogic', `${script.textContent}\nreturn Component;`)(DCLogic);
    component = new Component(props);
    render();
    if (typeof component.componentDidMount === 'function') component.componentDidMount();
    document.documentElement.dataset.rendered = 'true';
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
