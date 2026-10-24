// Codalus IDE website: tabs, the visitor's OS, copy buttons, heading anchors and the docs search.
// Every page works without it: tab panels are all shown, and the docs search box stays hidden.
// Pages have a <base href>, so links within a page carry its path ("#x" alone would lead to the home page).
(() => {
  "use strict";

  const detectOs = () => {
    const platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
    const agent = navigator.userAgent || "";
    if (/win/i.test(platform) || /windows/i.test(agent)) return "windows";
    if (/mac|iphone|ipad/i.test(platform) || /mac os/i.test(agent)) return "macos";
    if (/linux|x11/i.test(platform) || /linux/i.test(agent)) return "linux";
    return null;
  };

  // Tabs: [data-tabs] holds a hidden [role=tablist] of buttons and its [role=tabpanel]s (all visible without script).
  for (const root of document.querySelectorAll("[data-tabs]")) {
    const list = root.querySelector('[role="tablist"]');
    const tabs = [...list.querySelectorAll('[role="tab"]')];
    const panels = tabs.map((tab) => document.getElementById(tab.getAttribute("aria-controls")));
    const select = (index, focus) => {
      tabs.forEach((tab, i) => {
        const on = i === index;
        tab.setAttribute("aria-selected", String(on));
        tab.tabIndex = on ? 0 : -1;
        panels[i].hidden = !on;
      });
      if (focus) tabs[index].focus();
    };
    tabs.forEach((tab, i) => {
      tab.addEventListener("click", () => {
        select(i, false);
        if (root.dataset.tabsHash !== undefined) history.replaceState(null, "", location.pathname + location.search + "#" + tab.dataset.key);
      });
      tab.addEventListener("keydown", (e) => {
        const step = { ArrowRight: 1, ArrowLeft: -1 }[e.key];
        if (step) { e.preventDefault(); select((i + step + tabs.length) % tabs.length, true); }
        else if (e.key === "Home") { e.preventDefault(); select(0, true); }
        else if (e.key === "End") { e.preventDefault(); select(tabs.length - 1, true); }
      });
    });
    const wanted = root.dataset.tabsHash !== undefined
      ? location.hash.slice(1) || (root.dataset.tabsDetect !== undefined ? detectOs() : null)
      : null;
    const initial = Math.max(0, tabs.findIndex((tab) => tab.dataset.key === wanted));
    list.hidden = false;
    root.classList.add("is-tabbed");
    select(initial, false);
  }

  // "Download for <OS>" on the home page: the visitor's recommended file from the release (data-<os>, set when the
  // site knows a release), else the Download page at that OS's tab.
  const os = detectOs();
  const osNames = { windows: "Windows", macos: "macOS", linux: "Linux" };
  const arm64 = /aarch64|arm64/i.test(navigator.userAgent || "");
  for (const link of document.querySelectorAll("[data-download-os]")) {
    if (!os) continue;
    const file = (os === "linux" && arm64 && link.dataset.linuxArm64) || link.dataset[os];
    link.href = file || link.href.split("#")[0] + "#" + os;
    const label = link.querySelector("[data-label]");
    if (label) label.textContent = "Download for " + osNames[os];
  }

  // Copy buttons on command blocks.
  if (navigator.clipboard) {
    for (const pre of document.querySelectorAll("pre[data-copy]")) {
      const wrap = document.createElement("div");
      wrap.className = "code-wrap";
      pre.replaceWith(wrap);
      wrap.append(pre);
      const button = document.createElement("button");
      button.type = "button";
      button.className = "copy";
      button.textContent = "Copy";
      button.setAttribute("aria-label", "Copy the commands");
      button.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(pre.innerText.trim());
          button.textContent = "Copied";
          button.classList.add("is-copied");
        } catch {
          button.textContent = "Select and copy";
        }
        setTimeout(() => {
          button.textContent = "Copy";
          button.classList.remove("is-copied");
        }, 1600);
      });
      wrap.append(button);
    }
  }

  // Docs: anchors on headings, and the guide's contents folded on small screens.
  for (const heading of document.querySelectorAll(".prose h2[id], .prose h3[id]")) {
    const anchor = document.createElement("a");
    anchor.className = "heading-anchor";
    anchor.href = location.pathname + "#" + heading.id;
    anchor.textContent = "#";
    anchor.setAttribute("aria-label", "Link to " + heading.textContent);
    heading.append(anchor);
  }
  const docsNav = document.querySelector(".docs-nav");
  if (docsNav) {
    const narrow = matchMedia("(max-width: 860px)");
    const fit = () => { docsNav.open = !narrow.matches; };
    fit();
    narrow.addEventListener("change", fit);
  }

  // Docs search over the index at data-search (relative to the base), loaded on first use. Its urls are base-relative too.
  const search = document.querySelector("[data-search]");
  if (search) {
    search.hidden = false;
    const input = search.querySelector("input");
    const results = search.querySelector(".search-results");
    const source = new URL(search.dataset.search, document.baseURI);
    let index = null;
    let loading = null;
    const load = () => loading ??= fetch(source).then((r) => r.json()).then((data) => (index = data));
    const escape = (s) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
    const snippet = (text, term) => {
      const at = text.toLowerCase().indexOf(term);
      if (at < 0) return text.slice(0, 110);
      const start = Math.max(0, at - 40);
      return (start > 0 ? "…" : "") + text.slice(start, start + 120) + "…";
    };
    const run = () => {
      const terms = input.value.toLowerCase().split(/\s+/).filter((t) => t.length > 1);
      if (!terms.length || !index) { results.hidden = true; results.innerHTML = ""; return; }
      const hits = [];
      for (const page of index) {
        for (const section of page.sections) {
          const head = (page.title + " " + section.heading).toLowerCase();
          const body = section.text.toLowerCase();
          let score = 0;
          for (const term of terms) {
            if (head.includes(term)) score += 5;
            else if (body.includes(term)) score += 1;
            else { score = 0; break; }
          }
          if (score) hits.push({ page, section, score });
        }
      }
      hits.sort((a, b) => b.score - a.score);
      results.innerHTML = hits.length
        ? hits.slice(0, 8).map(({ page, section }) =>
            `<li><a href="${escape(page.url)}${section.id ? "#" + escape(section.id) : ""}">` +
            `<span>${escape(section.heading || page.title)}</span>` +
            `<small>${escape(section.heading ? page.title + " · " : "")}${escape(snippet(section.text, terms[0]))}</small></a></li>`).join("")
        : '<li class="empty">No matches</li>';
      results.hidden = false;
    };
    input.addEventListener("focus", () => load().then(run), { once: true });
    input.addEventListener("input", () => load().then(run));
    input.addEventListener("keydown", (e) => {
      if (e.key === "Escape") { input.value = ""; run(); }
      if (e.key === "ArrowDown") { e.preventDefault(); results.querySelector("a")?.focus(); }
    });
    results.addEventListener("keydown", (e) => {
      const links = [...results.querySelectorAll("a")];
      const i = links.indexOf(document.activeElement);
      if (e.key === "ArrowDown" && i < links.length - 1) { e.preventDefault(); links[i + 1].focus(); }
      if (e.key === "ArrowUp") { e.preventDefault(); (i > 0 ? links[i - 1] : input).focus(); }
      if (e.key === "Escape") { input.focus(); input.value = ""; run(); }
    });
  }
})();
