// Generates the README artwork in docs/readme/: hero banner, metrics strip, feature icons and divider (SVG,
// light and dark), and the product showcase (PNG), composed from the two screens `pnpm demo` captures to
// docs/readme/shots/ (the chat with its held slot, and the panel with the steps behind the booking).
//
//   node scripts/readme-assets.mjs
//
// GitHub shows README images through <img>: plain SVG, no external fonts or scripts. CSS animations inside the
// SVG do run there, and they stop under prefers-reduced-motion.
import { chromium } from "@playwright/test";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const out = fileURLToPath(new URL("../../docs/readme/", import.meta.url));
await mkdir(out, { recursive: true });

// Its own brand, distinct from docs-assistant (teal), claude-pr-review-bot (violet) and invoice-extractor (blue);
// amber is the shared accent, and here it is also the colour of a held slot.
const brand = { indigo: "#4338ca", peri: "#a5b4fc", amber: "#f59e0b", green: "#16a34a" };

const themes = {
	dark: { bg: "#0a0e13", panel: "#11151f", card: "#151a27", border: "#262d45", text: "#f1f5f9", muted: "#94a3b8", line: "#2c3550", dot: "#ffffff", dotOpacity: 0.06, orb: 0.32 },
	light: { bg: "#f8f8fd", panel: "#ffffff", card: "#ffffff", border: "#e2e3f0", text: "#0f172a", muted: "#475569", line: "#d4d6e8", dot: "#4338ca", dotOpacity: 0.08, orb: 0.15 },
};

const sans = `'Segoe UI', Inter, -apple-system, BlinkMacSystemFont, 'Helvetica Neue', Arial, sans-serif`;
const mono = `'Cascadia Code', 'SFMono-Regular', Consolas, Menlo, monospace`;
const escape = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

function defs(t) {
	return `
	<linearGradient id="brand" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="${brand.peri}"/><stop offset=".6" stop-color="${brand.indigo}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<linearGradient id="metric" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${brand.peri}"/><stop offset=".55" stop-color="${brand.indigo}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<radialGradient id="orbIndigo"><stop offset="0" stop-color="${brand.indigo}" stop-opacity="${t.orb}"/><stop offset="1" stop-color="${brand.indigo}" stop-opacity="0"/></radialGradient>
	<radialGradient id="orbAmber"><stop offset="0" stop-color="${brand.amber}" stop-opacity="${t.orb * 0.7}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></radialGradient>
	<pattern id="dots" width="22" height="22" patternUnits="userSpaceOnUse"><circle cx="2" cy="2" r="1.1" fill="${t.dot}" fill-opacity="${t.dotOpacity}"/></pattern>`;
}

// ── Hero: what it does, drawn — a message becomes a held slot, a person's click confirms it, the calendar shows it ──
function hero(name) {
	const t = themes[name];
	const W = 1280;
	const H = 460;
	const accent = name === "dark" ? brand.peri : brand.indigo;
	const chips = ["Claude Haiku 5.5", "A person confirms", "No double booking", "Live panel", "Evals 28/28"];
	let x = 72;
	const chipSvg = chips
		.map((chip) => {
			const w = chip.length * 8.4 + 30;
			const g = `<g transform="translate(${x} 352)"><rect width="${w}" height="32" rx="16" fill="${t.panel}" stroke="${t.border}"/><text x="${w / 2}" y="21" text-anchor="middle" font-family="${sans}" font-size="14" font-weight="600" fill="${t.text}">${escape(chip)}</text></g>`;
			x += w + 8;
			return g;
		})
		.join("");

	// The calendar: a week of columns, one booking amber (held) becoming indigo (confirmed).
	const cols = [0, 1, 2, 3, 4].map((i) => `<rect x="${16 + i * 36}" y="44" width="32" height="118" rx="6" fill="${t.line}" fill-opacity=".35"/>`).join("");

	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-labelledby="title desc">
<title id="title">booking-agent</title>
<desc id="desc">Customers book by chatting; the assistant holds the slot and a person confirms it with a button. Postgres makes double booking impossible, and the clinic's calendar updates live.</desc>
<style>
	.flow { stroke-dasharray: 6 7; animation: flow 1.6s linear infinite; }
	.pop { opacity: 0; animation: pop .5s ease-out forwards; }
	.glow { animation: glow 2.4s ease-in-out infinite alternate; }
	@keyframes flow { to { stroke-dashoffset: -26; } }
	@keyframes pop { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
	@keyframes glow { from { opacity: .55; } to { opacity: 1; } }
	@media (prefers-reduced-motion: reduce) { .flow, .glow { animation: none; } .pop { animation: none; opacity: 1; } }
</style>
<defs>${defs(t)}
	<clipPath id="frame"><rect width="${W}" height="${H}" rx="28"/></clipPath>
	<filter id="soft" x="-20%" y="-20%" width="140%" height="160%"><feDropShadow dx="0" dy="14" stdDeviation="16" flood-color="#000" flood-opacity="${name === "dark" ? 0.45 : 0.12}"/></filter>
</defs>
<g clip-path="url(#frame)">
	<rect width="${W}" height="${H}" fill="${t.bg}"/>
	<rect width="${W}" height="${H}" fill="url(#dots)"/>
	<circle cx="180" cy="40" r="340" fill="url(#orbIndigo)"/>
	<circle cx="1060" cy="470" r="360" fill="url(#orbAmber)"/>
</g>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="28" fill="none" stroke="${t.border}"/>

<g transform="translate(72 70)">
	<rect width="236" height="30" rx="15" fill="${t.panel}" stroke="${t.border}"/>
	<circle cx="18" cy="15" r="4.5" fill="${brand.peri}" class="glow"/>
	<text x="31" y="20" font-family="${sans}" font-size="12.5" font-weight="700" letter-spacing=".1em" fill="${t.muted}">OPEN SOURCE · .NET 10 · MIT</text>
</g>
<text x="68" y="172" font-family="${sans}" font-size="62" font-weight="800" letter-spacing="-.03em" fill="${t.text}">booking<tspan fill="url(#brand)">-agent</tspan></text>
<text x="72" y="230" font-family="${sans}" font-size="24" font-weight="600" fill="${t.text}">Appointments booked by chatting.</text>
<text x="72" y="268" font-family="${sans}" font-size="20" fill="${t.muted}">The assistant finds and holds the time; a person confirms it.</text>
<text x="72" y="300" font-family="${sans}" font-size="20" fill="${t.muted}">It can never confirm, and no slot is ever booked twice.</text>
${chipSvg}

<!-- The customer's message -->
<g transform="translate(676 74)" filter="url(#soft)">
	<rect width="218" height="64" rx="16" fill="${brand.indigo}"/>
	<text x="16" y="27" font-family="${sans}" font-size="13" font-weight="600" fill="#fff">Limpieza el lunes</text>
	<text x="16" y="46" font-family="${sans}" font-size="13" font-weight="600" fill="#fff">a las 10, por favor</text>
</g>

<path d="M786 140 C 786 168, 800 170, 800 188" fill="none" stroke="${accent}" stroke-width="2.2" class="flow"/>

<!-- The held slot, with the button only the person presses -->
<g transform="translate(676 190)" filter="url(#soft)">
	<rect width="236" height="142" rx="18" fill="${t.card}" stroke="${brand.amber}" stroke-width="1.5"/>
	<text x="18" y="30" font-family="${sans}" font-size="11.5" font-weight="700" letter-spacing=".08em" fill="${brand.amber}">HORARIO APARTADO</text>
	<text x="18" y="54" font-family="${sans}" font-size="14" font-weight="700" fill="${t.text}">Limpieza dental</text>
	<text x="18" y="74" font-family="${sans}" font-size="12.5" fill="${t.muted}">lunes 26 · 10:00 · 4:57 min</text>
	<g transform="translate(18 92)"><g class="pop" style="animation-delay:.4s">
		<rect width="118" height="32" rx="9" fill="${brand.indigo}"/>
		<text x="59" y="21" text-anchor="middle" font-family="${sans}" font-size="13" font-weight="700" fill="#fff">Confirmar</text>
	</g></g>
	<g transform="translate(118 112)"><g class="pop" style="animation-delay:.9s">
		<path d="M0 0 l0 18 l5 -5 l4 8 l3 -1.5 l-4 -8 l7 0 z" fill="${t.text}" stroke="${t.card}" stroke-width="1.2"/>
	</g></g>
</g>

<path d="M912 262 C 960 262, 958 200, 1000 196" fill="none" stroke="${brand.amber}" stroke-width="2.2" class="flow"/>

<!-- The clinic's calendar, live -->
<g transform="translate(998 104)" filter="url(#soft)">
	<rect width="216" height="182" rx="18" fill="${t.card}" stroke="${t.border}"/>
	<text x="16" y="30" font-family="${sans}" font-size="13" font-weight="700" fill="${t.text}">Panel · en vivo</text>
	<circle cx="196" cy="25" r="4.5" fill="${brand.green}" class="glow"/>
	${cols}
	<g transform="translate(16 92)"><g class="pop" style="animation-delay:1.3s">
		<rect width="32" height="30" rx="5" fill="${brand.indigo}" fill-opacity=".18" stroke="${brand.indigo}"/>
		<rect x="4" y="6" width="12" height="5" rx="2" fill="${brand.indigo}"/>
		<rect x="4" y="15" width="22" height="4" rx="2" fill="${t.muted}" fill-opacity=".7"/>
	</g></g>
	<text x="16" y="172" font-family="${mono}" font-size="10.5" fill="${t.muted}">exclusion constraint ✓</text>
</g>
</svg>
`;
}

// ── Metrics: measured by this repo's evals and tests (evals/results and the integration tests, 23 Oct 2026) ──
const metricItems = [
	["28 / 28", "eval conversations as expected", "Claude Haiku 5.5, 23 Oct 2026"],
	["0", "bookings changed by the agent", "it holds; a person confirms"],
	["1 of 50", "simultaneous holds win a slot", "Postgres exclusion constraint"],
	["USD 0.0024", "per conversation", "average of the 28 scenarios"],
];

function metrics(name) {
	const t = themes[name];
	const W = 1280;
	const H = 150;
	const col = W / metricItems.length;
	const cells = metricItems
		.map(
			([value, label, note], i) => `
	<g transform="translate(${col * i + col / 2} 0)">
		<text y="64" text-anchor="middle" font-family="${sans}" font-size="40" font-weight="800" letter-spacing="-.02em" fill="url(#metric)">${escape(value)}</text>
		<text y="94" text-anchor="middle" font-family="${sans}" font-size="16" font-weight="600" fill="${t.text}">${escape(label)}</text>
		<text y="116" text-anchor="middle" font-family="${sans}" font-size="13.5" fill="${t.muted}">${escape(note)}</text>
	</g>${i > 0 ? `<line x1="${col * i}" y1="36" x2="${col * i}" y2="114" stroke="${t.border}"/>` : ""}`,
		)
		.join("");
	const label = metricItems.map(([value, label, note]) => `${value} ${label}, ${note}.`).join(" ");
	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${escape(label)}">
<defs>${defs(t)}</defs>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="22" fill="${t.panel}" stroke="${t.border}"/>
${cells}
</svg>
`;
}

// ── Feature icons: Lucide glyphs (ISC license) on a gradient tile ─────────────────────────────────────────
const glyphs = {
	chat: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/><path d="M8 9h8"/><path d="M8 13h5"/>',
	confirm: '<path d="m9 9 5 12 1.774-5.226L21 14 9 9z"/><path d="m16.071 16.071 4.243 4.243"/><path d="m7.188 2.239.777 2.897"/><path d="m5.136 7.965-2.898-.777"/><path d="M13.95 4.05l-2.122 2.122"/>',
	shield: '<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="m9 12 2 2 4-4"/>',
	clock: '<circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>',
	bell: '<path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.94 1.94 0 0 0 3.4 0"/>',
	mail: '<rect width="20" height="16" x="2" y="4" rx="2"/><path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7"/>',
	calendar: '<rect width="18" height="18" x="3" y="4" rx="2"/><path d="M16 2v4"/><path d="M8 2v4"/><path d="M3 10h18"/><path d="M8 14h.01"/><path d="M12 14h.01"/><path d="M16 14h.01"/>',
	evals: '<path d="M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2"/><path d="M6.453 15h11.094"/><path d="M8.5 2h7"/>',
	play: '<rect width="20" height="14" x="2" y="3" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/><path d="m10 7 4 3-4 3z"/>',
};

const tiles = {
	chat: ["#4338ca", "#818cf8"],
	confirm: ["#b45309", "#fbbf24"],
	shield: ["#9f1239", "#fb7185"],
	clock: ["#0e7490", "#22d3ee"],
	bell: ["#7c2d12", "#f97316"],
	mail: ["#334155", "#64748b"],
	calendar: ["#166534", "#4ade80"],
	evals: ["#5b21b6", "#a78bfa"],
	play: ["#1e3a8a", "#60a5fa"],
};

function iconSvg(name) {
	const [from, to] = tiles[name];
	return `<svg xmlns="http://www.w3.org/2000/svg" width="56" height="56" viewBox="0 0 56 56" aria-hidden="true">
<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient></defs>
<rect width="56" height="56" rx="16" fill="url(#g)"/>
<rect x=".75" y=".75" width="54.5" height="54.5" rx="15.25" fill="none" stroke="#fff" stroke-opacity=".18" stroke-width="1.5"/>
<g transform="translate(16 16)" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${glyphs[name]}</g>
</svg>
`;
}

const divider = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="24" viewBox="0 0 1280 24" aria-hidden="true">
<defs><linearGradient id="d" x1="0" x2="1"><stop offset="0" stop-color="${brand.peri}" stop-opacity="0"/><stop offset=".3" stop-color="${brand.indigo}"/><stop offset=".7" stop-color="${brand.amber}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></linearGradient></defs>
<rect y="11" width="1280" height="2" rx="1" fill="url(#d)"/>
<rect x="632" y="4" width="16" height="16" rx="5" fill="${brand.indigo}"/><path d="M636.5 12l2.5 2.5 4.5-5" fill="none" stroke="#fff" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
`;

for (const name of ["dark", "light"]) {
	await writeFile(`${out}hero-${name}.svg`, hero(name));
	await writeFile(`${out}metrics-${name}.svg`, metrics(name));
}

for (const name of Object.keys(glyphs)) {
	await writeFile(`${out}icon-${name}.svg`, iconSvg(name));
}

await writeFile(`${out}divider.svg`, divider);

// ── Showcase: the real chat and panel, two browser windows on each theme's background ─────────────────────
const shot = async (file) => `data:image/png;base64,${(await readFile(`${out}shots/${file}`)).toString("base64")}`;
const chat = await shot("chat.png");
const panel = await shot("panel.png");
const browser = await chromium.launch();

for (const name of ["dark", "light"]) {
	const t = themes[name];
	const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1.5 });
	const window = (left, top, url, image, z) => `
		<div class="window" style="left:${left}px;top:${top}px;z-index:${z}"><div class="bar"><span class="d" style="background:#f87171"></span><span class="d" style="background:#fbbf24"></span><span class="d" style="background:#34d399"></span>
			<span class="url">${url}</span></div><div class="view"><img src="${image}"></div></div>`;
	await page.setContent(
		`<!doctype html><html><head><meta charset="utf-8"><style>
		* { box-sizing: border-box; margin: 0; } body { width: 100vw; height: 100vh; overflow: hidden; position: relative; background: ${t.bg}; font-family: ${sans}; }
		.dots { position: absolute; inset: 0; background-image: radial-gradient(${t.dot} 1px, transparent 1.2px); background-size: 22px 22px; opacity: ${t.dotOpacity * 1.4}; }
		.orb { position: absolute; border-radius: 50%; filter: blur(100px); }
		.o1 { width: 700px; height: 700px; left: -200px; top: -260px; background: ${brand.indigo}; opacity: ${name === "dark" ? 0.38 : 0.16}; }
		.o2 { width: 700px; height: 700px; right: -220px; bottom: -300px; background: ${brand.amber}; opacity: ${name === "dark" ? 0.25 : 0.16}; }
		.window { position: absolute; width: 700px; border-radius: 16px; overflow: hidden; background: ${t.panel}; border: 1px solid ${t.border};
			box-shadow: 0 50px 100px -20px rgba(0,0,0,${name === "dark" ? 0.65 : 0.25}); }
		.bar { height: 40px; display: flex; align-items: center; gap: 8px; padding: 0 14px; border-bottom: 1px solid ${t.border}; }
		.d { width: 11px; height: 11px; border-radius: 50%; }
		.url { margin: 0 auto; width: 300px; height: 26px; border-radius: 8px; background: ${t.bg}; border: 1px solid ${t.border}; color: ${t.muted};
			font-size: 12.5px; display: flex; align-items: center; justify-content: center; }
		.view { height: 786px; overflow: hidden; } .view img { display: block; width: 100%; }
		</style></head><body><div class="dots"></div><div class="orb o1"></div><div class="orb o2"></div>
		${window(90, 60, "localhost:3020/chat", chat, 1)}${window(810, 110, "localhost:3020/panel", panel, 2)}</body></html>`,
		{ waitUntil: "load" },
	);
	await page.screenshot({ path: `${out}showcase-${name}.png` });
	await page.close();
}

await browser.close();
console.log(`README artwork written to ${out}`);
