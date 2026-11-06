'use strict';
const $ = id => document.getElementById(id);
const ns = 'http://www.w3.org/1999/xhtml';
const el = (tag, text, className) => {
  const node = document.createElementNS(ns, tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.setAttribute('class', className);
  return node;
};
const number = value => new Intl.NumberFormat('en-US').format(value);
const money = value => new Intl.NumberFormat('en-US', {style:'currency', currency:'USD', maximumFractionDigits:2}).format(value);
let report = null;
let selectedQuery = 'products';
let loading = false;
let busEnabled = false;
let pipelineReady = false;
let pendingBusRun = null;
let pipelinePaused = false;
let lastProgress = null;
let observedRevision = null;
let refreshRequested = false;
let liveRefreshing = false;

function notify(text, error = false) {
  $('notice').textContent = text;
  $('notice').setAttribute('class', error ? 'notice error' : 'notice');
}
function connected(version) {
  $('connection').setAttribute('class', 'connection');
  $('connection-label').textContent = 'ClickHouse ' + version + ' connected';
}
function offline() {
  $('connection').setAttribute('class', 'connection offline');
  $('connection-label').textContent = 'Connection unavailable';
}
function busy(value) {
  loading = value;
  ['seed', 'refresh', 'run', 'country'].forEach(id => { $(id).disabled = value; });
  $('queue-events').disabled = value || !pipelineReady || pendingBusRun !== null;
  $('refresh').setAttribute('class', value ? 'icon-button pulse' : 'icon-button');
  if (!value) queueMicrotask(drainLiveUpdates);
}
async function api(url, options = {}) {
  const response = await fetch(url, { ...options, headers: { 'X-Demo-Request':'1' }, signal: AbortSignal.timeout(90000) });
  const body = await response.json();
  if (!response.ok) throw new Error(body.error || 'The database request failed. Please retry.');
  return body;
}
function clearDashboard() {
  report = null;
  ['revenue','events','units','latency'].forEach(id => { $(id).textContent = '—'; });
  $('daily-chart').replaceChildren(el('div', 'Load sample data to see your revenue chart.', 'empty-chart'));
  $('product-chart').replaceChildren(el('div', 'Your product breakdown will appear here.', 'empty-chart'));
  $('updated').textContent = 'Waiting for data';
  renderQuery();
}
async function loadRuns(preferred) {
  const result = await api('/api/runs');
  connected(result.version);
  const oldRun = preferred || $('run').value || 'all';
  $('run').replaceChildren();
  const allOption = el('option', 'All runs · ' + number(result.runCount) + ' runs');
  allOption.value = 'all';
  $('run').append(allOption);
  result.runs.rows.forEach(row => {
    const option = el('option', 'Run ' + row[0].slice(0,8) + ' · ' + number(row[1]) + ' events');
    option.value = row[0];
    $('run').append(option);
  });
  if (!result.runs.rows.length) {
    clearDashboard();
    notify('Your database is ready. Click “Load 10,000 events” to create your first data run.');
    return;
  }
  if (oldRun === 'all' || result.runs.rows.some(row => row[0] === oldRun)) $('run').value = oldRun;
  await loadDashboard();
}
async function loadDashboard() {
  if (!$('run').value) return;
  const params = new URLSearchParams({ country:$('country').value });
  if ($('run').value !== 'all') params.set('runId', $('run').value);
  report = await api('/api/dashboard?' + params);
  const [events, units, revenue, runs] = report.totals.rows[0];
  $('revenue').textContent = money(revenue);
  $('events').textContent = number(events);
  $('units').textContent = number(units);
  $('latency').textContent = number(report.elapsedMs);
  $('events-scope').textContent = report.runId ? 'From the selected data run' : 'Across ' + number(runs) + ' local data runs';
  renderDaily(report.reports.daily.rows);
  renderProducts(report.reports.products.rows);
  renderQuery();
  $('updated').textContent = 'Updated ' + new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'});
  notify(number(events) + ' sales events · ' + (report.country || 'All countries') + ' · '
    + (report.runId ? 'Selected run.' : 'All ' + number(runs) + ' local data runs combined.'));
}
function renderDaily(rows) {
  if (!rows.length) {
    $('daily-chart').replaceChildren(el('div', 'No events match this country filter.', 'empty-chart'));
    return;
  }
  const ceiling = Math.ceil(Math.max(...rows.map(row => Number(row[2]))) / 100000) * 100000 || 100000;
  const left = 56, right = 628, top = 18, bottom = 173;
  const step = (right - left) / rows.length;
  let svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 650 220" role="img" aria-labelledby="chart-title"><title id="chart-title">Daily revenue in USD, January 1 through 7, 2026</title>';
  for (let i=0;i<5;i++) {
    const y = top + (bottom-top)*i/4;
    const amount = ceiling*(1-i/4);
    svg += '<line x1="'+left+'" y1="'+y+'" x2="'+right+'" y2="'+y+'" stroke="#edf0e7" stroke-dasharray="3 4" />';
    svg += '<text x="44" y="'+(y+3)+'" text-anchor="end" font-size="10" fill="#9ca48f">$'+Math.round(amount/1000)+'k</text>';
  }
  rows.forEach((row, i) => {
    const value = Number(row[2]);
    const height = (value/ceiling)*(bottom-top);
    const x = left + i*step + step*.18;
    svg += '<rect x="'+x+'" y="'+(bottom-height)+'" width="'+step*.64+'" height="'+height+'" rx="4" fill="'+(i===rows.length-1?'#abc75d':'#d3e9a3')+'"><title>'+row[0]+': '+money(value)+'</title></rect>';
    svg += '<text x="'+(left+i*step+step/2)+'" y="198" text-anchor="middle" font-size="10" fill="#919b82">Jan '+Number(row[0].slice(-2))+'</text>';
  });
  $('daily-chart').innerHTML = svg + '</svg>';
}
function renderProducts(rows) {
  $('product-chart').replaceChildren();
  if (!rows.length) {
    $('product-chart').append(el('div', 'No products match this country filter.', 'empty-chart'));
    return;
  }
  const max = Math.max(...rows.map(row=>Number(row[3]))) || 1;
  rows.forEach(row => {
    const item=el('div',undefined,'product-row');
    const label=el('div',undefined,'product-label');
    label.append(el('span',row[0]),el('strong',money(row[3])));
    const track=el('div',undefined,'bar-track');
    const fill=el('div',undefined,'bar-fill');
    fill.style.width = Number(row[3])/max*100 + '%';
    track.append(fill); item.append(label,track); $('product-chart').append(item);
  });
}
function renderQuery() {
  document.querySelectorAll('.query-tab').forEach(tab => {
    const active = tab.getAttribute('data-query') === selectedQuery;
    tab.setAttribute('class',active?'query-tab selected':'query-tab');
    tab.setAttribute('aria-selected',String(active));
  });
  $('result-head').replaceChildren(); $('result-body').replaceChildren();
  if (!report) {
    $('sql').textContent = 'Select a data run to inspect its query.';
    $('parameters').textContent = 'Values are bound through the C# driver.';
    $('row-count').textContent = '0 rows'; $('table-empty').hidden = false;
    $('copy-sql').disabled = true;
    return;
  }
  const data = report.reports[selectedQuery];
  $('copy-sql').disabled = false;
  $('sql').textContent = data.sql.replace(/ (FROM|WHERE|GROUP BY|ORDER BY|LIMIT) /g,'\n$1 ');
  $('parameters').textContent = (report.runId ? 'runId = ' + report.runId : 'scope = all local runs (no run filter)')
    + '\ncountry = ' + (report.country ? '"'+report.country+'"' : '"" (all countries)');
  const header = el('tr');
  data.columns.forEach(name => { const th=el('th',name.replaceAll('_',' ')); th.setAttribute('scope','col'); header.append(th); });
  $('result-head').append(header);
  data.rows.forEach(row => {
    const tr=el('tr');
    row.forEach((value,i) => {
      const name=data.columns[i];
      const display = name==='revenue'||name==='unit_price' ? money(value) : typeof value==='number' ? number(value) : String(value??'');
      tr.append(el('td',display));
    });
    $('result-body').append(tr);
  });
  $('row-count').textContent = data.rows.length + ' rows';
  $('table-empty').hidden = data.rows.length !== 0;
}
async function action(task) {
  if (loading) return;
  busy(true);
  try { await task(); }
  catch(error) { clearDashboard(); offline(); notify(error.message || 'Could not connect. Please retry.',true); }
  finally { busy(false); $('seed-label').textContent=busEnabled ? 'Load directly' : 'Load 10,000 events'; }
}
$('seed').addEventListener('click',()=>action(async()=>{
  $('seed-label').textContent='Loading events…';
  notify('Generating and inserting 10,000 sales events. Existing data runs will be preserved.');
  const result = await api('/api/seed',{method:'POST'});
  $('country').value='';
  await loadRuns('all');
  notify('Verified ✓ ' + number(result.inserted) + ' new events added. All runs now total '
    + number(report.totals.rows[0][0]) + ' events and ' + money(report.totals.rows[0][2]) + ' in revenue.');
}));
$('refresh').addEventListener('click',()=>action(()=>loadRuns()));
$('run').addEventListener('change',()=>action(()=>loadDashboard()));
$('country').addEventListener('change',()=>action(()=>loadDashboard()));
document.querySelectorAll('.query-tab').forEach(tab=>tab.addEventListener('click',()=>{
  selectedQuery=tab.getAttribute('data-query'); renderQuery();
}));
$('copy-sql').addEventListener('click',async()=>{
  try { await navigator.clipboard.writeText($('sql').textContent); $('copy-sql').textContent='Copied ✓'; }
  catch { notify('Copy is unavailable in this browser. Select the SQL text and copy it manually.',true); }
  setTimeout(()=>{ $('copy-sql').textContent='Copy SQL'; },1800);
});
action(()=>loadRuns());

function renderPipeline(state) {
    busEnabled = state.enabled;
    pipelineReady = busEnabled && ['Ready','Paused','Processing'].includes(state.state);
    pipelinePaused = state.paused;
    $('pipeline').hidden = !busEnabled;
    $('queue-events').hidden = !busEnabled;
    $('queue-events').disabled = loading || !pipelineReady || pendingBusRun !== null;
    if (!loading) $('seed-label').textContent = busEnabled ? 'Load directly' : 'Load 10,000 events';
    if (!busEnabled) return;
    $('worker-status').textContent = 'Worker: ' + state.state + (state.lastError ? ' · Reconnecting / retrying' : '');
    $('pipeline-counters').textContent = number(state.publishedMessages) + ' messages sent · ' + number(state.completedMessages) + ' handled · ' + number(state.deadLetteredMessages) + ' dead-lettered (this app session)';
    $('pause-worker').textContent = state.paused ? 'Resume worker' : 'Pause worker';
    if (pendingBusRun && lastProgress) renderProgress(lastProgress);
}
function renderProgress(progress) {
  $('ingestion-progress').value = Math.min(progress.storedEvents,progress.expectedEvents);
  $('ingestion-detail').textContent = 'Run ' + progress.runId.slice(0,8) + ' · ' + number(progress.storedEvents) + ' / ' + number(progress.expectedEvents)
    + ' events in ClickHouse · ' + money(progress.revenue)
    + (progress.verified ? ' · Verified ✓' : pipelinePaused ? ' · Worker paused; messages wait in the queue.' : '');
}
function requestLiveRefresh(revision) {
  if (revision !== undefined) {
    if (revision === observedRevision) return;
    observedRevision = revision;
  }
  refreshRequested = true;
  queueMicrotask(drainLiveUpdates);
}
async function drainLiveUpdates() {
  if (liveRefreshing || loading || !refreshRequested) return;
  liveRefreshing = true;
  try {
    // Coalesce bursts and retain notifications that arrive while a button request/report is running.
    while (refreshRequested && !loading) {
      refreshRequested = false;
      let verified = false;
      let mismatch = false;
      await action(async()=>{
        if (pendingBusRun) {
          lastProgress = await api('/api/ingestion/' + pendingBusRun);
          renderProgress(lastProgress);
          verified = lastProgress.verified;
          mismatch = lastProgress.storedEvents > lastProgress.expectedEvents;
          if (verified || mismatch) pendingBusRun = null;
        }
        await loadRuns($('run').value || 'all');
      });
      if (verified) notify('Service Bus delivery verified ✓ 10,000 events are stored in ClickHouse. The aggregate includes the new sales.');
      if (mismatch) notify('This run contains unexpected extra events. Inspect its records before loading more.',true);
    }
  } finally { liveRefreshing=false; }
}
$('queue-events').addEventListener('click',async()=>{
  if (loading || pendingBusRun) return;
  busy(true);
  $('queue-label').textContent='Queuing events…';
  try {
    notify('Sending sales through Service Bus. The worker will load them into ClickHouse in the background.');
    const result = await api('/api/queue',{method:'POST'});
    pendingBusRun = result.runId;
    lastProgress = null;
    $('country').value='';
    $('ingestion-progress').value=0;
    $('ingestion-detail').textContent=number(result.queuedMessages)+' messages queued, carrying '+number(result.queuedEvents)+' sales events. Waiting for the C# worker…';
    await loadRuns('all');
    notify(number(result.queuedMessages)+' messages queued ✓ Watch the progress bar and combined totals as the worker loads 10,000 events.');
  } catch(error) {
    notify(error.message || 'Service Bus is unavailable. Check the emulator.',true);
  } finally {
    $('queue-label').textContent='Queue 10,000 events';
    busy(false);
    requestLiveRefresh();
  }
});
$('pause-worker').addEventListener('click',async()=>{
  $('pause-worker').disabled=true;
  try {
    renderPipeline(await api('/api/pipeline/pause?paused='+(!pipelinePaused),{method:'POST'}));
  } catch(error) { notify('Could not change the worker state. Please retry.',true); }
  finally { $('pause-worker').disabled=false; }
});
const live = new EventSource('/api/events');
live.onopen = () => {
  $('live-status').textContent='Live · SSE';
  $('live-status').setAttribute('class','live-status');
  requestLiveRefresh(); // Always resync after a reconnect, including a server restart.
};
live.addEventListener('pipeline', event => {
  const state = JSON.parse(event.data);
  renderPipeline(state);
  requestLiveRefresh(state.dataRevision);
});
live.addEventListener('dataChanged', event => requestLiveRefresh(JSON.parse(event.data).dataRevision));
live.onerror = () => {
  $('live-status').textContent='SSE reconnecting…';
  $('live-status').setAttribute('class','live-status reconnecting');
};
