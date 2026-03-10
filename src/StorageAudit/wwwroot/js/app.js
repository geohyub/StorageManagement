const API = {
    async get(url) {
        const res = await fetch(url);
        if (!res.ok) throw new Error(`API error: ${res.status}`);
        return res.json();
    },
    async post(url, data) {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: data ? JSON.stringify(data) : undefined
        });
        if (!res.ok) throw new Error(`API error: ${res.status}`);
        return res.json();
    }
};

const state = {
    events: [],
    stats: {},
    status: {},
    query: {
        search: '',
        actionType: null,
        minAlert: null,
        from: null,
        to: null,
        includeSelf: false,
        page: 1,
        pageSize: 50,
        sortBy: 'Timestamp',
        sortDesc: true
    },
    totalCount: 0,
    totalPages: 0,
    refreshInterval: null,
    autoRefresh: true
};

// === Initialization ===

document.addEventListener('DOMContentLoaded', () => {
    initializeUI();
    loadData();
    startAutoRefresh();
});

function initializeUI() {
    document.getElementById('searchInput').addEventListener('input', debounce(e => {
        state.query.search = e.target.value;
        state.query.page = 1;
        loadEvents();
    }, 300));

    document.getElementById('actionFilter').addEventListener('change', e => {
        state.query.actionType = e.target.value || null;
        state.query.page = 1;
        loadEvents();
    });

    document.getElementById('alertFilter').addEventListener('change', e => {
        state.query.minAlert = e.target.value || null;
        state.query.page = 1;
        loadEvents();
    });

    document.getElementById('dateFrom').addEventListener('change', e => {
        state.query.from = e.target.value || null;
        state.query.page = 1;
        loadEvents();
    });

    document.getElementById('dateTo').addEventListener('change', e => {
        state.query.to = e.target.value || null;
        state.query.page = 1;
        loadEvents();
    });

    document.getElementById('selfToggle').addEventListener('change', e => {
        state.query.includeSelf = e.target.checked;
        state.query.page = 1;
        loadEvents();
    });

    document.getElementById('autoRefreshToggle').addEventListener('change', e => {
        state.autoRefresh = e.target.checked;
        if (state.autoRefresh) startAutoRefresh();
        else stopAutoRefresh();
    });

    document.querySelectorAll('th[data-sort]').forEach(th => {
        th.addEventListener('click', () => {
            const col = th.dataset.sort;
            if (state.query.sortBy === col) {
                state.query.sortDesc = !state.query.sortDesc;
            } else {
                state.query.sortBy = col;
                state.query.sortDesc = true;
            }
            loadEvents();
        });
    });
}

// === Data Loading ===

async function loadData() {
    await Promise.all([loadStatus(), loadStats(), loadEvents()]);
}

async function loadStatus() {
    try {
        state.status = await API.get('/api/status');
        document.getElementById('watchRootDisplay').textContent = state.status.watchRoot || '-';
        document.getElementById('statusDot').style.background =
            state.status.isRunning ? '#22c55e' : '#ef4444';
    } catch (e) {
        console.error('Status load error:', e);
    }
}

async function loadStats() {
    try {
        const params = new URLSearchParams();
        if (state.query.from) params.set('from', state.query.from);
        if (state.query.to) params.set('to', state.query.to);
        state.stats = await API.get(`/api/stats?${params}`);
        renderStats();
    } catch (e) {
        console.error('Stats load error:', e);
    }
}

async function loadEvents() {
    try {
        const params = new URLSearchParams();
        if (state.query.search) params.set('search', state.query.search);
        if (state.query.actionType) params.set('actionType', state.query.actionType);
        if (state.query.minAlert) params.set('minAlert', state.query.minAlert);
        if (state.query.from) params.set('from', state.query.from);
        if (state.query.to) params.set('to', state.query.to);
        if (state.query.includeSelf) params.set('includeSelf', 'true');
        params.set('page', state.query.page);
        params.set('pageSize', state.query.pageSize);
        params.set('sortBy', state.query.sortBy);
        params.set('sortDesc', state.query.sortDesc);

        const result = await API.get(`/api/events?${params}`);
        state.events = result.items;
        state.totalCount = result.totalCount;
        state.totalPages = result.totalPages;
        renderEvents();
        renderPagination();
    } catch (e) {
        console.error('Events load error:', e);
    }
}

// === Rendering ===

function renderStats() {
    const s = state.stats;
    document.getElementById('statTotal').textContent = formatNumber(s.totalEvents || 0);
    document.getElementById('statImports').textContent = formatNumber(s.importCount || 0);
    document.getElementById('statExports').textContent = formatNumber(s.exportCount || 0);
    document.getElementById('statDeletes').textContent = formatNumber(s.deleteCount || 0);
    document.getElementById('statWarnings').textContent = formatNumber(s.warningCount || 0);
    document.getElementById('statCreated').textContent = formatNumber(s.createdCount || 0);
}

function renderEvents() {
    const tbody = document.getElementById('eventTableBody');
    if (state.events.length === 0) {
        tbody.innerHTML = '<tr><td colspan="10" style="text-align:center;color:var(--text-muted);padding:40px;">No events found</td></tr>';
        return;
    }

    tbody.innerHTML = state.events.map(e => {
        const alertClass = e.alert !== 'Normal' ? ` alert-${escapeAttr(e.alert)}` : '';
        const selfClass = e.isSelfGenerated ? ' self-generated' : '';
        const time = formatTimestamp(e.timestamp);
        const size = formatSize(e.fileSizeBytes);
        const path = e.oldPath
            ? `${escapeHtml(e.oldPath)} → ${escapeHtml(e.fullPath)}`
            : escapeHtml(e.fullPath);

        return `<tr class="${escapeAttr(alertClass)}${escapeAttr(selfClass)}">
            <td title="${escapeAttr(e.timestamp)}">${escapeHtml(time)}</td>
            <td><span class="badge badge-${escapeAttr(e.actionType)}">${escapeHtml(e.actionType)}</span></td>
            <td title="${escapeAttr(e.fileName)}">${escapeHtml(e.fileName)}</td>
            <td title="${escapeAttr(path)}">${path}</td>
            <td><span class="badge badge-direction-${escapeAttr(e.direction)}">${escapeHtml(e.direction)}</span></td>
            <td>${escapeHtml(size)}</td>
            <td>${escapeHtml(e.extension || '-')}</td>
            <td><span class="confidence">${escapeHtml(e.confidence)}</span></td>
            <td><span class="badge badge-alert-${escapeAttr(e.alert)}">${escapeHtml(e.alert)}</span></td>
            <td title="${escapeAttr(e.notes || '')}">${escapeHtml(truncate(e.notes || '', 50))}</td>
        </tr>`;
    }).join('');
}

function renderPagination() {
    document.getElementById('pageInfo').textContent =
        `Page ${state.query.page} of ${state.totalPages} (${formatNumber(state.totalCount)} events)`;

    document.getElementById('prevBtn').disabled = state.query.page <= 1;
    document.getElementById('nextBtn').disabled = state.query.page >= state.totalPages;
}

// === Actions ===

function prevPage() {
    if (state.query.page > 1) {
        state.query.page--;
        loadEvents();
    }
}

function nextPage() {
    if (state.query.page < state.totalPages) {
        state.query.page++;
        loadEvents();
    }
}

function refreshNow() {
    loadData();
    showToast('Data refreshed', 'info');
}

async function exportData(format) {
    try {
        const params = new URLSearchParams();
        if (state.query.from) params.set('from', state.query.from);
        if (state.query.to) params.set('to', state.query.to);
        if (state.query.actionType) params.set('actionType', state.query.actionType);
        if (state.query.includeSelf) params.set('includeSelf', 'true');

        const result = await API.post(`/api/export/${encodeURIComponent(format)}?${params}`);
        // 다운로드 트리거
        const a = document.createElement('a');
        a.href = result.downloadUrl;
        a.download = result.fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        showToast(`Exported as ${format.toUpperCase()}`, 'success');
    } catch (e) {
        showToast(`Export failed: ${e.message}`, 'error');
    }
}

function openSettings() {
    document.getElementById('settingsModal').classList.add('active');
    document.getElementById('watchRootInput').value = state.status.watchRoot || '';
    document.getElementById('ignorePatternsInput').value =
        (state.status.ignorePatterns || []).join('\n');
}

function closeSettings() {
    document.getElementById('settingsModal').classList.remove('active');
}

async function saveSettings() {
    try {
        const newRoot = document.getElementById('watchRootInput').value.trim();
        const patterns = document.getElementById('ignorePatternsInput').value
            .split('\n')
            .map(p => p.trim())
            .filter(p => p.length > 0);

        if (newRoot && newRoot !== state.status.watchRoot) {
            await API.post('/api/config/watchroot', { path: newRoot });
        }

        if (patterns.length > 0) {
            await API.post('/api/config/ignorepatterns', { patterns });
        }

        closeSettings();
        showToast('Settings saved', 'success');
        loadData();
    } catch (e) {
        showToast(`Failed to save: ${e.message}`, 'error');
    }
}

// === Auto Refresh ===

function startAutoRefresh() {
    stopAutoRefresh();
    state.refreshInterval = setInterval(() => {
        if (state.autoRefresh) loadData();
    }, 3000);
}

function stopAutoRefresh() {
    if (state.refreshInterval) {
        clearInterval(state.refreshInterval);
        state.refreshInterval = null;
    }
}

// === Utilities ===

function formatTimestamp(ts) {
    const d = new Date(ts);
    return d.toLocaleString('ko-KR', {
        year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit', second: '2-digit'
    });
}

function formatSize(bytes) {
    if (bytes == null) return '-';
    const units = ['B', 'KB', 'MB', 'GB'];
    let i = 0, b = bytes;
    while (b >= 1024 && i < units.length - 1) { b /= 1024; i++; }
    return `${b.toFixed(1)} ${units[i]}`;
}

function formatNumber(n) {
    return n.toLocaleString();
}

function truncate(str, len) {
    return str.length > len ? str.substring(0, len) + '...' : str;
}

function escapeHtml(str) {
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
}

function escapeAttr(str) {
    if (str == null) return '';
    return String(str).replace(/[&"'<>]/g, c => ({
        '&': '&amp;', '"': '&quot;', "'": '&#39;', '<': '&lt;', '>': '&gt;'
    })[c]);
}

function debounce(fn, ms) {
    let timer;
    return (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => fn(...args), ms);
    };
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toastContainer');
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.textContent = message;
    container.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
}
