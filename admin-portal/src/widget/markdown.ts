// ─────────────────────────────────────────────────────────────────────────────
// Lightweight, dependency-free Markdown → HTML renderer for the embeddable
// widget. The widget bundle is size-sensitive, so we deliberately avoid pulling
// in `marked` + `DOMPurify` (which would multiply the bundle size).
//
// Safety: all input is HTML-escaped FIRST, then only a fixed whitelist of tags
// is re-introduced from controlled transforms. Link hrefs are restricted to
// http(s)/mailto, so agent-authored content cannot inject scripts or
// javascript: URIs. Content originates from the tenant's own agent, not
// arbitrary third parties.
// ─────────────────────────────────────────────────────────────────────────────

function escapeHtml(s: string): string
{
    return s
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#39;");
}

function safeHref(url: string): string | null
{
    const u = url.trim();
    if (/^(https?:\/\/|mailto:)/i.test(u)) return u;
    return null;
}

// Inline: code, bold, italic, links. Operates on already-escaped text.
function renderInline(text: string): string
{
    let out = text;
    // inline code (protect from further formatting via placeholder)
    const codes: string[] = [];
    out = out.replace(/`([^`]+)`/g, (_m, c) =>
    {
        codes.push(`<code class="diva-md-code">${ c }</code>`);
        return `\u0000${ codes.length - 1 }\u0000`;
    });
    // links [text](url)
    out = out.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_m, label, url) =>
    {
        const href = safeHref(url);
        if (!href) return label;
        return `<a href="${ href }" target="_blank" rel="noopener noreferrer" class="diva-md-link">${ label }</a>`;
    });
    // bold then italic
    out = out.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    out = out.replace(/(^|[^*])\*([^*]+)\*/g, "$1<em>$2</em>");
    out = out.replace(/_([^_]+)_/g, "<em>$1</em>");
    // restore inline code
    out = out.replace(/\u0000(\d+)\u0000/g, (_m, i) => codes[Number(i)]);
    return out;
}

function renderTable(rows: string[]): string
{
    const cells = (line: string) =>
        line
            .replace(/^\||\|$/g, "")
            .split("|")
            .map((c) => c.trim());
    const header = cells(rows[0]);
    const body = rows.slice(2).map(cells);
    const thead = `<thead><tr>${ header.map((h) => `<th class="diva-md-th">${ renderInline(h) }</th>`).join("") }</tr></thead>`;
    const tbody = `<tbody>${ body
        .map(
            (r) => `<tr>${ r.map((c) => `<td class="diva-md-td">${ renderInline(c) }</td>`).join("") }</tr>`,
        )
        .join("") }</tbody>`;
    return `<div class="diva-md-table-wrap"><table class="diva-md-table">${ thead }${ tbody }</table></div>`;
}

export function renderMarkdown(src: string): string
{
    const escaped = escapeHtml(src);
    const lines = escaped.split("\n");
    const html: string[] = [];
    let i = 0;

    while (i < lines.length)
    {
        const line = lines[i];

        // Fenced code block
        const fence = /^```(\w*)\s*$/.exec(line);
        if (fence)
        {
            const buf: string[] = [];
            i++;
            while (i < lines.length && !/^```\s*$/.test(lines[i])) buf.push(lines[i++]);
            i++; // closing fence
            html.push(`<pre class="diva-md-pre"><code>${ buf.join("\n") }</code></pre>`);
            continue;
        }

        // GFM table (header + separator row of ---)
        if (line.includes("|") && i + 1 < lines.length && /^\s*\|?[\s:|-]+\|?\s*$/.test(lines[i + 1]) && lines[i + 1].includes("-"))
        {
            const tbl: string[] = [line];
            i++;
            tbl.push(lines[i++]); // separator
            while (i < lines.length && lines[i].includes("|") && lines[i].trim() !== "") tbl.push(lines[i++]);
            html.push(renderTable(tbl));
            continue;
        }

        // Heading
        const heading = /^(#{1,6})\s+(.*)$/.exec(line);
        if (heading)
        {
            const level = Math.min(heading[1].length, 6);
            html.push(`<div class="diva-md-h diva-md-h${ level }">${ renderInline(heading[2]) }</div>`);
            i++;
            continue;
        }

        // Unordered list
        if (/^\s*[-*]\s+/.test(line))
        {
            const items: string[] = [];
            while (i < lines.length && /^\s*[-*]\s+/.test(lines[i]))
            {
                items.push(`<li>${ renderInline(lines[i].replace(/^\s*[-*]\s+/, "")) }</li>`);
                i++;
            }
            html.push(`<ul class="diva-md-ul">${ items.join("") }</ul>`);
            continue;
        }

        // Ordered list
        if (/^\s*\d+\.\s+/.test(line))
        {
            const items: string[] = [];
            while (i < lines.length && /^\s*\d+\.\s+/.test(lines[i]))
            {
                items.push(`<li>${ renderInline(lines[i].replace(/^\s*\d+\.\s+/, "")) }</li>`);
                i++;
            }
            html.push(`<ol class="diva-md-ol">${ items.join("") }</ol>`);
            continue;
        }

        // Blank line
        if (line.trim() === "")
        {
            i++;
            continue;
        }

        // Paragraph (merge consecutive non-blank, non-special lines)
        const para: string[] = [line];
        i++;
        while (
            i < lines.length &&
            lines[i].trim() !== "" &&
            !/^```/.test(lines[i]) &&
            !/^(#{1,6})\s/.test(lines[i]) &&
            !/^\s*[-*]\s+/.test(lines[i]) &&
            !/^\s*\d+\.\s+/.test(lines[i]) &&
            !lines[i].includes("|")
        )
        {
            para.push(lines[i++]);
        }
        html.push(`<p class="diva-md-p">${ renderInline(para.join(" ")) }</p>`);
    }

    return html.join("");
}
