using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.DevTools;

public sealed class GameAgentTraceHtmlReportOptions
{
    public string Title { get; set; } = "OpenGameAgent Trace";

    public int MaximumRenderedEntries { get; set; } = 100_000;

    public bool IncludeDetails { get; set; } = true;

    internal GameAgentTraceHtmlReportOptions CopyAndValidate()
    {
        var copy = (GameAgentTraceHtmlReportOptions)MemberwiseClone();
        if (string.IsNullOrWhiteSpace(copy.Title) || copy.Title.Length > 1_024)
        {
            throw new ArgumentException("A report title of at most 1,024 characters is required.", nameof(Title));
        }

        if (copy.MaximumRenderedEntries < 1 || copy.MaximumRenderedEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRenderedEntries));
        }

        return copy;
    }
}

/// <summary>
/// Creates a local, dependency-free playback report. Playback projects recorded observations only;
/// it never invokes a model, a tool, an action handler, or a game host.
/// </summary>
public static class GameAgentTraceHtmlReport
{
    public static async Task WriteAsync(
        GameAgentTraceRecording recording,
        string outputPath,
        GameAgentTraceHtmlReportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        }

        var validated = (options ?? new GameAgentTraceHtmlReportOptions()).CopyAndValidate();
        var html = Create(recording, validated);
        var path = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(html);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, useAsync: true))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static string Create(
        GameAgentTraceRecording recording,
        GameAgentTraceHtmlReportOptions? options = null)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }

        var validated = (options ?? new GameAgentTraceHtmlReportOptions()).CopyAndValidate();
        var summary = GameAgentTraceSummary.Create(recording);
        var selected = recording.Entries.Take(validated.MaximumRenderedEntries).Select(entry =>
        {
            JsonElement? details = null;
            if (validated.IncludeDetails)
            {
                using var document = JsonDocument.Parse(entry.DetailsJson, new JsonDocumentOptions { MaxDepth = 128 });
                details = document.RootElement.Clone();
            }

            return new
            {
                entry.Sequence,
                entry.Kind,
                entry.SessionId,
                entry.ActorId,
                entry.InputId,
                timeline = entry.Moment.TimelineId,
                tick = entry.Moment.Tick,
                calendar = entry.Moment.CalendarJson,
                timestamp = entry.OperationalTimestamp,
                details,
            };
        }).ToArray();
        var payload = new
        {
            summary = new
            {
                summary.Entries,
                summary.Sessions,
                summary.Actors,
                runs = summary.Runs.Count,
                summary.FailedRuns,
                summary.ToolCalls,
                summary.ToolErrors,
                durationMilliseconds = summary.Duration.TotalMilliseconds,
            },
            entries = selected,
            truncated = selected.Length < recording.Entries.Count,
            ignoredTruncatedFinalLine = recording.IgnoredTruncatedFinalLine,
            detailsIncluded = validated.IncludeDetails,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var title = Html(validated.Title);

        return """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:">
<title>__TITLE__</title>
<style>
:root{color-scheme:dark;--bg:#090c10;--panel:#11161d;--panel2:#171e27;--line:#27313e;--text:#edf3f8;--muted:#93a4b7;--cyan:#65d7ff;--green:#68e2a2;--red:#ff6b78;--amber:#ffc76a}
*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 20% -20%,#172b3b 0,transparent 38%),var(--bg);color:var(--text);font:14px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace}
header{padding:28px clamp(18px,4vw,56px) 18px;border-bottom:1px solid var(--line);position:sticky;top:0;background:#090c10e8;backdrop-filter:blur(12px);z-index:5}h1{margin:0 0 7px;font:700 clamp(22px,3vw,36px)/1.1 system-ui,sans-serif;letter-spacing:-.03em}.sub{color:var(--muted)}.safe{color:var(--green)}
main{padding:22px clamp(18px,4vw,56px) 60px;max-width:1500px;margin:auto}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;margin-bottom:18px}.card,.controls,.event{border:1px solid var(--line);background:linear-gradient(145deg,var(--panel),#0e1319);border-radius:12px}.card{padding:15px}.metric{font:700 24px/1 system-ui,sans-serif}.label{color:var(--muted);margin-top:7px;font-size:12px}.controls{padding:14px;display:grid;grid-template-columns:auto minmax(130px,1fr) repeat(4,minmax(110px,200px));gap:10px;align-items:center;margin-bottom:14px}button,input,select{background:var(--panel2);border:1px solid var(--line);color:var(--text);padding:9px 10px;border-radius:8px;font:inherit}button{cursor:pointer;color:var(--cyan)}button:disabled{cursor:not-allowed;color:var(--muted)}input[type=range]{padding:0;width:100%}.status,.pager{color:var(--muted);margin:10px 2px}.pager{display:flex;align-items:center;justify-content:flex-end;gap:9px}.event{padding:13px 15px;margin:8px 0;display:grid;grid-template-columns:78px minmax(170px,.7fr) minmax(180px,1fr) 110px;gap:12px;cursor:pointer}.event.active{border-color:var(--cyan);box-shadow:0 0 0 1px #65d7ff33}.seq{color:var(--muted)}.kind{color:var(--cyan);font-weight:700}.identity{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.moment{color:var(--amber);text-align:right}.details{display:none;grid-column:1/-1;white-space:pre-wrap;overflow:auto;background:#080b0f;border-radius:8px;padding:12px;color:#c8d5e2;max-height:460px}.event.open .details{display:block}.failed .kind,.error{color:var(--red)}.empty{padding:40px;color:var(--muted);text-align:center;border:1px dashed var(--line);border-radius:12px}
@media(max-width:850px){.controls{grid-template-columns:1fr 1fr}.controls input[type=range]{grid-column:1/-1}.event{grid-template-columns:60px 1fr}.identity,.moment{grid-column:2}.moment{text-align:left}}
</style>
</head>
<body>
<header><h1>__TITLE__</h1><div class="sub"><span class="safe">Observation-only playback.</span> No model, tool, action handler, or game host is executed.</div></header>
<main><section class="cards" id="cards"></section>
<section class="controls"><button id="play">Play</button><input id="scrub" type="range" min="0" max="0" value="0"><input id="search" placeholder="Search IDs or details"><select id="kind"><option value="">All event kinds</option></select><select id="actor"><option value="">All actors</option></select><select id="speed"><option value="1200">0.5×</option><option value="600" selected>1×</option><option value="250">2×</option><option value="100">4×</option></select></section>
<div class="status" id="status"></div><section id="events"></section><div class="pager"><button id="previous">Previous</button><span id="page"></span><button id="next">Next</button></div></main>
<script>
'use strict';
const data=JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('__DATA__'),c=>c.charCodeAt(0))));
const cards=document.getElementById('cards'),eventsEl=document.getElementById('events'),statusEl=document.getElementById('status'),search=document.getElementById('search'),kind=document.getElementById('kind'),actor=document.getElementById('actor'),scrub=document.getElementById('scrub'),play=document.getElementById('play'),speed=document.getElementById('speed'),previous=document.getElementById('previous'),next=document.getElementById('next'),pageEl=document.getElementById('page');
const metrics=[['Entries',data.summary.entries],['Runs',data.summary.runs],['Actors',data.summary.actors],['Failed',data.summary.failedRuns],['Tool calls',data.summary.toolCalls],['Tool errors',data.summary.toolErrors],['Duration',(data.summary.durationMilliseconds/1000).toFixed(2)+'s']];
for(const [label,value] of metrics){const c=document.createElement('div');c.className='card';const m=document.createElement('div');m.className='metric';m.textContent=String(value);const l=document.createElement('div');l.className='label';l.textContent=label;c.append(m,l);cards.append(c)}
const uniq=(xs)=>[...new Set(xs)].sort();for(const value of uniq(data.entries.map(e=>e.kind))){const o=document.createElement('option');o.value=value;o.textContent=value;kind.append(o)}for(const value of uniq(data.entries.map(e=>e.sessionId+'/'+e.actorId))){const o=document.createElement('option');o.value=value;o.textContent=value;actor.append(o)}
let filtered=[],selected=0,page=0,timer=null;const pageSize=500,isFailure=e=>e.kind==='run.failed'||e.kind==='kernel.runfaulted'||(e.kind==='run.completed'&&e.details&&e.details.succeeded===false);
function apply(){const q=search.value.toLowerCase();filtered=data.entries.filter(e=>(!kind.value||e.kind===kind.value)&&(!actor.value||e.sessionId+'/'+e.actorId===actor.value)&&(!q||JSON.stringify(e).toLowerCase().includes(q)));selected=Math.min(selected,Math.max(0,filtered.length-1));page=Math.floor(selected/pageSize);scrub.max=String(Math.max(0,filtered.length-1));scrub.value=String(selected);render()}
function render(){eventsEl.replaceChildren();const pages=Math.max(1,Math.ceil(filtered.length/pageSize));page=Math.min(page,pages-1);const begin=page*pageSize,end=Math.min(filtered.length,begin+pageSize);statusEl.textContent=filtered.length+' visible of '+data.entries.length+' recorded · rendering '+(filtered.length?begin+1:0)+'–'+end+(data.truncated?' (report entry limit reached)':'')+(data.ignoredTruncatedFinalLine?' · crash-truncated final line ignored':'')+(data.detailsIncluded?'':' · details omitted');pageEl.textContent='Page '+(page+1)+' / '+pages;previous.disabled=page===0;next.disabled=page>=pages-1;if(!filtered.length){const e=document.createElement('div');e.className='empty';e.textContent='No events match the current filters.';eventsEl.append(e);return}const frag=document.createDocumentFragment();filtered.slice(begin,end).forEach((e,offset)=>{const i=begin+offset,row=document.createElement('article');row.className='event'+(i===selected?' active':'')+(isFailure(e)?' failed':'');const seq=document.createElement('div');seq.className='seq';seq.textContent='#'+e.sequence;const k=document.createElement('div');k.className='kind';k.textContent=e.kind;const id=document.createElement('div');id.className='identity';id.textContent=e.sessionId+' / '+e.actorId+' / '+e.inputId;const moment=document.createElement('div');moment.className='moment';moment.textContent=e.timeline+':'+e.tick;const d=document.createElement('pre');d.className='details';d.textContent=e.details===null?'Details were omitted.':JSON.stringify(e.details,null,2);row.append(seq,k,id,moment,d);row.addEventListener('click',()=>{selected=i;scrub.value=String(i);row.classList.toggle('open');document.querySelectorAll('.event.active').forEach(x=>x.classList.remove('active'));row.classList.add('active')});frag.append(row)});eventsEl.append(frag);document.querySelector('.event.active')?.scrollIntoView({block:'nearest'})}
function stop(){if(timer!==null){clearInterval(timer);timer=null}play.textContent='Play'}function start(){if(!filtered.length)return;stop();play.textContent='Pause';timer=setInterval(()=>{if(selected>=filtered.length-1){stop();return}selected++;page=Math.floor(selected/pageSize);scrub.value=String(selected);render()},Number(speed.value))}
play.addEventListener('click',()=>timer===null?start():stop());speed.addEventListener('change',()=>{if(timer!==null)start()});scrub.addEventListener('input',()=>{selected=Number(scrub.value);page=Math.floor(selected/pageSize);render()});previous.addEventListener('click',()=>{page=Math.max(0,page-1);selected=page*pageSize;scrub.value=String(selected);render()});next.addEventListener('click',()=>{page=Math.min(Math.ceil(filtered.length/pageSize)-1,page+1);selected=page*pageSize;scrub.value=String(selected);render()});for(const el of [search,kind,actor])el.addEventListener(el===search?'input':'change',()=>{stop();selected=0;page=0;apply()});apply();
</script></body></html>
"""
            .Replace("__DATA__", base64, StringComparison.Ordinal)
            .Replace("__TITLE__", title, StringComparison.Ordinal);
    }

    private static string Html(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);
}
