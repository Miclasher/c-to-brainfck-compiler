"use strict";

const $ = (id) => document.getElementById(id);
const state = { result: null, tab: "ast", root: null, bf: "" };

// ───────────── node colours by AST kind ─────────────

const KIND_GROUP = {
  Program: "decl", FuncDef: "decl", Params: "decl", Param: "decl", VarDecl: "decl", Prototype: "decl", ArraySize: "decl", Init: "decl", InitList: "decl",
  Block: "stmt", ExprStmt: "stmt", Empty: "stmt", Cond: "stmt", Update: "stmt",
  If: "ctl", While: "ctl", DoWhile: "ctl", For: "ctl", Return: "ctl", Break: "ctl", Continue: "ctl",
  Num: "lit", Char: "lit", Str: "lit",
  Unsupported: "bad", token: "misc",
};
const GROUPS = {
  decl: ["Declarations", "--k-decl"], stmt: ["Statements", "--k-stmt"], ctl: ["Control flow", "--k-ctl"],
  expr: ["Expressions", "--k-expr"], lit: ["Literals", "--k-lit"], bad: ["Unsupported", "--k-bad"],
};
const groupOf = (kind) => KIND_GROUP[kind] ?? (kind.includes("›") || /^[a-z]/.test(kind) ? "misc" : "expr");
const css = (v) => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
const colourOf = (d) => css((GROUPS[groupOf(d.data.name)] ?? [null, "--k-misc"])[1]);

function label(d) {
  const { name, text, type } = d.data;
  if (name === "token") return text;
  let s = name;
  if (text != null && text !== "") s += ` ${text}`;
  if (type) s += ` : ${type}`;
  return s;
}

// ───────────── d3 tree ─────────────

let svg, gMain, zoom, uid = 0;

function initSvg() {
  const host = $("tree");
  host.innerHTML = "";
  svg = d3.select(host).append("svg");
  gMain = svg.append("g");
  zoom = d3.zoom().scaleExtent([0.1, 3]).on("zoom", (e) => gMain.attr("transform", e.transform));
  svg.call(zoom).on("dblclick.zoom", null);
}

function showEmpty(msg) {
  const host = $("tree");
  host.innerHTML = `<div class="empty">${msg}</div>`;
  svg = null;
}

function setTree(data, collapseDepth) {
  if (!data) { showEmpty("Nothing to show. Fix the diagnostics and compile again."); return; }
  initSvg();
  const root = d3.hierarchy(data, (d) => d.children);
  root.each((n) => { n.id = ++uid; });
  if (collapseDepth != null) root.each((n) => { if (n.depth >= collapseDepth && n.children) { n._children = n.children; n.children = null; } });
  state.root = root;
  draw(true);
}

function draw(fit) {
  const root = state.root;
  d3.tree().nodeSize([22, 1])(root);

  // horizontal spacing from the widest label on each level
  const widths = [];
  root.each((n) => { widths[n.depth] = Math.max(widths[n.depth] ?? 0, label(n).length); });
  const xAt = [0];
  for (let i = 1; i < widths.length; i++) xAt[i] = xAt[i - 1] + widths[i - 1] * 7.2 + 46;
  root.each((n) => { n.y = xAt[n.depth]; });

  const nodes = root.descendants();
  const links = root.links();

  gMain.selectAll(".link").data(links, (d) => d.target.id).join("path").attr("class", "link")
    .attr("d", d3.linkHorizontal().x((d) => d.y).y((d) => d.x));

  const node = gMain.selectAll(".node").data(nodes, (d) => d.id).join((enter) => {
    const g = enter.append("g").attr("class", "node");
    g.append("circle").attr("r", 5);
    g.append("text").attr("dy", "0.32em").attr("x", 10);
    g.append("title");
    return g;
  });
  node.attr("transform", (d) => `translate(${d.y},${d.x})`)
    .on("click", (_, d) => {
      if (d.children) { d._children = d.children; d.children = null; }
      else if (d._children) { d.children = d._children; d._children = null; }
      draw(false);
    });
  node.select("circle")
    .attr("fill", (d) => (d._children ? colourOf(d) : css("--surface")))
    .attr("stroke", colourOf)
    .attr("stroke-dasharray", (d) => (d.data.folded ? "3 2" : null));
  node.select("text").each(function (d) {
    const t = d3.select(this);
    t.text(null);
    const { name, text, type } = d.data;
    if (name === "token") { t.append("tspan").attr("class", "txt").text(text); return; }
    t.append("tspan").text(name);
    if (text) t.append("tspan").attr("class", "txt").text(` ${text}`);
    if (type) t.append("tspan").attr("class", "txt").text(` : ${type}`);
    if (d._children) t.append("tspan").attr("class", "txt").text(` (+${d._children.length})`);
  });
  node.select("title").text((d) => `${label(d)}${d.data.line ? `\nline ${d.data.line}` : ""}${d.data.folded ? "\nfolded at compile time" : ""}`);

  if (fit) fitView();
}

function fitView() {
  if (!svg) return;
  const box = gMain.node().getBBox();
  const { width, height } = $("tree").getBoundingClientRect();
  if (!box.width || !width) return;
  const k = Math.min(1, 0.95 * Math.min(width / box.width, height / box.height));
  const t = d3.zoomIdentity.translate(16 - box.x * k, height / 2 - (box.y + box.height / 2) * k).scale(k);
  svg.call(zoom.transform, t);
}

function setAll(open) {
  if (!state.root) return;
  state.root.each((n) => {
    if (open && n._children) { n.children = n._children; n._children = null; }
    else if (!open && n.depth > 0 && n.children) { n._children = n.children; n.children = null; }
  });
  if (!open) state.root.each((n) => { if (n.depth === 0 && n._children) { n.children = n._children; n._children = null; } });
  draw(true);
}

// ───────────── views ─────────────

function renderLegend() {
  $("legend").innerHTML = state.tab === "cst"
    ? `<span>Chains of single-child rules are merged with ›. Click a node to fold or unfold it.</span>`
    : Object.values(GROUPS).map(([n, v]) => `<span><i style="background:var(${v})"></i>${n}</span>`).join("") +
      `<span><i style="border:2px dashed var(--muted);background:none"></i>constant-folded</span>`;
}

function render() {
  const r = state.result;
  const isBf = state.tab === "bf";
  $("tree").hidden = isBf;
  $("bf-view").hidden = !isBf;
  $("fold-toggle").hidden = state.tab !== "ast";
  $("expand").hidden = $("collapse").hidden = isBf;
  document.querySelectorAll("[role=tab]").forEach((b) => b.setAttribute("aria-selected", String(b.dataset.tab === state.tab)));
  renderLegend();
  if (!r) { showEmpty("Press Compile to build the tree."); return; }

  if (state.tab === "ast") {
    const data = $("folded").checked ? r.foldedAst : r.ast;
    if (!data) return showEmpty("No AST: the source has syntax errors.");
    setTree(data, d3.hierarchy(data, (d) => d.children).descendants().length > 400 ? 6 : null);
  } else if (state.tab === "cst") {
    setTree(r.cst, 5);
  } else {
    $("bf").textContent = r.brainfuck ?? "// no code generated, see diagnostics";
    state.bf = r.brainfuck ?? "";
    const ops = (state.bf.match(/[+\-<>\[\].,]/g) ?? []).length;
    $("bf-stats").textContent = r.brainfuck ? `${ops.toLocaleString()} instructions` : "";
  }
}

function renderDiagnostics(list) {
  const ul = $("diagnostics");
  ul.innerHTML = "";
  const errors = list.filter((d) => d.severity === "error").length;
  $("diag-count").textContent = list.length ? `${errors} error${errors === 1 ? "" : "s"}, ${list.length - errors} warning${list.length - errors === 1 ? "" : "s"}` : "";
  if (!list.length) { ul.innerHTML = `<li class="ok">No problems.</li>`; return; }
  for (const d of list) {
    const li = document.createElement("li");
    li.className = d.severity;
    li.innerHTML = `<span class="sev">${d.severity}</span><span></span>`;
    li.lastChild.textContent = `${d.line ? `${d.line}${d.column ? ":" + d.column : ""} ` : ""}[${d.phase}] ${d.message}`;
    li.onclick = () => goToLine(d.line);
    ul.appendChild(li);
  }
}

function goToLine(line) {
  if (!line) return;
  const ta = $("source");
  const lines = ta.value.split("\n");
  const start = lines.slice(0, line - 1).join("\n").length + (line > 1 ? 1 : 0);
  ta.focus();
  ta.setSelectionRange(start, start + (lines[line - 1] ?? "").length);
}

async function compile() {
  const btn = $("compile");
  btn.disabled = true;
  try {
    const res = await fetch("/api/compile", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ source: $("source").value, annotate: $("annotate").checked }),
    });
    state.result = await res.json();
    renderDiagnostics(state.result.diagnostics);
    if (state.tab === "bf" && !state.result.brainfuck) state.tab = "ast";
    else if (state.result.brainfuck && state.tab !== "cst") { /* keep current tab */ }
    render();
  } catch (e) {
    renderDiagnostics([{ severity: "error", phase: "network", line: 0, column: 0, message: String(e) }]);
  } finally { btn.disabled = false; }
}

// ───────────── wiring ─────────────

document.querySelectorAll("[role=tab]").forEach((b) => b.addEventListener("click", () => { state.tab = b.dataset.tab; render(); }));
$("compile").onclick = compile;
$("folded").onchange = render;
$("expand").onclick = () => setAll(true);
$("collapse").onclick = () => setAll(false);
$("copy").onclick = async () => {
  try { await navigator.clipboard.writeText(state.bf); $("copy").textContent = "Copied"; }
  catch { $("copy").textContent = "Copy failed"; }
  setTimeout(() => ($("copy").textContent = "Copy"), 1200);
};
$("download").onclick = () => {
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([state.bf], { type: "text/plain" }));
  a.download = ($("sample").value || "program") + ".bf";
  a.click();
  URL.revokeObjectURL(a.href);
};
$("source").addEventListener("keydown", (e) => {
  if (e.ctrlKey && e.key === "Enter") { e.preventDefault(); compile(); }
  if (e.key === "Tab") {
    e.preventDefault();
    const t = e.target, s = t.selectionStart;
    t.value = t.value.slice(0, s) + "    " + t.value.slice(t.selectionEnd);
    t.selectionStart = t.selectionEnd = s + 4;
  }
});
window.addEventListener("resize", () => { if (state.tab !== "bf") fitView(); });

(async function init() {
  let samples = [];
  try { samples = await (await fetch("/api/samples")).json(); } catch { /* offline */ }
  const sel = $("sample");
  for (const s of samples) sel.add(new Option(s.name, s.name));
  sel.add(new Option("(custom)", ""));
  const load = () => { const s = samples.find((x) => x.name === sel.value); if (s) { $("source").value = s.source; compile(); } };
  sel.onchange = load;
  if (samples.length) { sel.value = (samples.find((s) => s.name === "functions") ?? samples[0]).name; load(); }
  else showEmpty("Press Compile to build the tree.");
  $("source").addEventListener("input", () => { sel.value = ""; });
})();
