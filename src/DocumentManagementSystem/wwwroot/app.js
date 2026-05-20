const { useState, useEffect, useCallback, useMemo, useRef } = React;

const API = '/api';

const typeLabel = {
    Contract: 'Sözleşme',
    Offer: 'Teklif',
    Invoice: 'Fatura',
    Other: 'Diğer'
};

function useDebounce(value, delay) {
    const [debounced, setDebounced] = useState(value);
    useEffect(() => {
        const t = setTimeout(() => setDebounced(value), delay);
        return () => clearTimeout(t);
    }, [value, delay]);
    return debounced;
}

/** Bloom filter ve term sözlüğünü tek noktadan yükler/tutar. */
function useClientAssets() {
    const [bloom, setBloom] = useState(null);
    const [suggester, setSuggester] = useState(null);
    const [status, setStatus] = useState({ bloom: 'loading', suggest: 'loading' });

    useEffect(() => {
        (async () => {
            try {
                const buf = await IdbCache.fetchCached('bloom-filter', `${API}/duplicate-filter`, 'binary');
                setBloom(BloomFilter.fromArrayBuffer(buf));
                setStatus(s => ({ ...s, bloom: 'ready' }));
            } catch (err) {
                console.warn('Bloom filter yüklenemedi:', err);
                setStatus(s => ({ ...s, bloom: 'error' }));
            }
        })();

        (async () => {
            try {
                const terms = await IdbCache.fetchCached('term-dictionary', `${API}/term-dictionary`, 'json');
                setSuggester(new Suggester(terms));
                setStatus(s => ({ ...s, suggest: 'ready' }));
            } catch (err) {
                console.warn('Term sözlüğü yüklenemedi:', err);
                setStatus(s => ({ ...s, suggest: 'error' }));
            }
        })();
    }, []);

    return { bloom, suggester, status };
}

function App() {
    const { bloom, suggester, status: assetStatus } = useClientAssets();

    const [query, setQuery] = useState('');
    const debouncedQuery = useDebounce(query, 250);
    const [type, setType] = useState('');
    const [owner, setOwner] = useState('');
    const [from, setFrom] = useState('');
    const [to, setTo] = useState('');
    const [page, setPage] = useState(1);
    const pageSize = 12;

    const [data, setData] = useState({ hits: [], total: 0, page: 1, pageSize, elapsedMs: 0 });
    const [loading, setLoading] = useState(false);
    const [owners, setOwners] = useState([]);
    const [formMode, setFormMode] = useState(null);
    const [editingDoc, setEditingDoc] = useState(null);
    const [duplicateInfo, setDuplicateInfo] = useState(null);
    const [pendingPayload, setPendingPayload] = useState(null);
    const [toast, setToast] = useState(null);

    useEffect(() => {
        fetch(`${API}/documents/owners`).then(r => r.json()).then(setOwners).catch(() => {});
    }, []);

    const runSearch = useCallback(async () => {
        setLoading(true);
        try {
            const body = {
                query: debouncedQuery.trim() || null,
                type: type || null,
                owner: owner || null,
                from: from ? new Date(from).toISOString() : null,
                to: to ? new Date(to + 'T23:59:59').toISOString() : null,
                page,
                pageSize
            };
            const res = await fetch(`${API}/search`, {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify(body)
            });
            const json = await res.json();
            setData(json);
        } catch {
            setToast({ msg: 'Arama sırasında bir hata oluştu.', kind: 'error' });
        } finally {
            setLoading(false);
        }
    }, [debouncedQuery, type, owner, from, to, page]);

    useEffect(() => { runSearch(); }, [runSearch]);
    useEffect(() => { setPage(1); }, [debouncedQuery, type, owner, from, to]);

    useEffect(() => {
        if (!toast) return;
        const t = setTimeout(() => setToast(null), 3500);
        return () => clearTimeout(t);
    }, [toast]);

    // Client-side suggest: sonuç azken term sözlüğünden öneri üret
    const clientSuggestions = useMemo(() => {
        if (!suggester || !debouncedQuery || data.total >= 3) return [];
        return suggester.suggest(debouncedQuery, 5);
    }, [suggester, debouncedQuery, data.total]);

    const closeForm = () => { setFormMode(null); setEditingDoc(null); };

    const submitCreate = async (payload, force = false) => {
        try {
            const res = await fetch(`${API}/documents`, {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ ...payload, forceUpload: force })
            });
            const json = await res.json();

            if (res.status === 201) {
                setToast({ msg: 'Doküman başarıyla eklendi.', kind: 'success' });
                closeForm();
                setDuplicateInfo(null);
                setPendingPayload(null);
                runSearch();
                return;
            }
            if (res.status === 409) {
                setDuplicateInfo(json);
                setPendingPayload(payload);
                setFormMode(null);
                return;
            }
            setToast({ msg: json.message || 'Yükleme başarısız.', kind: 'error' });
        } catch {
            setToast({ msg: 'Sunucuya ulaşılamadı.', kind: 'error' });
        }
    };

    const submitEdit = async (payload) => {
        try {
            const res = await fetch(`${API}/documents/${editingDoc.id}`, {
                method: 'PUT',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify(payload)
            });
            if (res.ok) {
                setToast({ msg: 'Doküman güncellendi.', kind: 'success' });
                closeForm();
                runSearch();
                return;
            }
            const json = await res.json().catch(() => ({}));
            setToast({ msg: json.message || 'Güncelleme başarısız.', kind: 'error' });
        } catch {
            setToast({ msg: 'Sunucuya ulaşılamadı.', kind: 'error' });
        }
    };

    const handleDelete = async () => {
        if (!editingDoc) return;
        if (!window.confirm(`"${editingDoc.title}" silinsin mi? Bu işlem geri alınamaz.`)) return;
        try {
            const res = await fetch(`${API}/documents/${editingDoc.id}`, { method: 'DELETE' });
            if (res.ok) {
                setToast({ msg: 'Doküman silindi.', kind: 'success' });
                closeForm();
                runSearch();
            } else {
                setToast({ msg: 'Silme başarısız.', kind: 'error' });
            }
        } catch {
            setToast({ msg: 'Sunucuya ulaşılamadı.', kind: 'error' });
        }
    };

    const resetFilters = () => {
        setQuery(''); setType(''); setOwner(''); setFrom(''); setTo('');
    };

    const totalPages = Math.max(1, Math.ceil(data.total / pageSize));
    const activeFilterCount = useMemo(() =>
        [type, owner, from, to].filter(Boolean).length, [type, owner, from, to]);

    const openCreate = () => { setEditingDoc(null); setFormMode('create'); };
    const openEdit = (doc) => { setEditingDoc(doc); setFormMode('edit'); };

    return (
        <div className="app">
            <Header onUploadClick={openCreate} status={assetStatus} />

            <div className="layout">
                <Sidebar
                    type={type} setType={setType}
                    owner={owner} setOwner={setOwner} owners={owners}
                    from={from} setFrom={setFrom} to={to} setTo={setTo}
                    onReset={resetFilters}
                    activeCount={activeFilterCount}
                />

                <main className="main">
                    <SearchBar query={query} setQuery={setQuery} loading={loading} />

                    <ResultsHeader
                        total={data.total}
                        elapsedMs={data.elapsedMs}
                        loading={loading}
                        query={debouncedQuery}
                    />

                    {clientSuggestions.length > 0 && (
                        <Suggestions terms={clientSuggestions} onPick={setQuery} />
                    )}

                    {loading ? (
                        <SkeletonGrid />
                    ) : data.hits.length === 0 ? (
                        <EmptyState query={debouncedQuery} hasFilters={activeFilterCount > 0} onReset={resetFilters} />
                    ) : (
                        <DocGrid hits={data.hits} onSelect={openEdit} />
                    )}

                    {data.hits.length > 0 && totalPages > 1 && (
                        <Pagination page={data.page} totalPages={totalPages} onChange={setPage} />
                    )}
                </main>
            </div>

            <FloatingButton onClick={openCreate} />

            {formMode && (
                <DocumentFormModal
                    mode={formMode}
                    initial={editingDoc}
                    bloom={bloom}
                    onClose={closeForm}
                    onSubmit={formMode === 'edit' ? submitEdit : (p => submitCreate(p, false))}
                    onDelete={formMode === 'edit' ? handleDelete : null}
                />
            )}

            {duplicateInfo && (
                <DuplicateModal
                    info={duplicateInfo}
                    onCancel={() => {
                        setDuplicateInfo(null); setPendingPayload(null);
                        setToast({ msg: 'Mevcut belgeyi kullanmayı seçtiniz. Yeni yükleme iptal edildi.', kind: 'success' });
                    }}
                    onForce={() => submitCreate(pendingPayload, true)}
                />
            )}

            {toast && <Toast {...toast} />}
        </div>
    );
}

function Header({ onUploadClick, status }) {
    const ready = status.bloom === 'ready' && status.suggest === 'ready';
    return (
        <header className="app-header">
            <div className="brand">
                <div className="logo">📂</div>
                <div>
                    <div className="title">Doküman Yönetim Sistemi</div>
                    <div className="subtitle">
                        {ready
                            ? 'Akıllı arama ve anlık duplicate kontrolü hazır'
                            : 'Yardımcı dosyalar yükleniyor...'}
                    </div>
                </div>
            </div>
            <button className="btn primary" onClick={onUploadClick}>
                <span>+</span> Yeni doküman
            </button>
        </header>
    );
}

function Sidebar({ type, setType, owner, setOwner, owners, from, setFrom, to, setTo, onReset, activeCount }) {
    const types = [
        { value: '', label: 'Tümü' },
        { value: 'Contract', label: 'Sözleşme' },
        { value: 'Offer', label: 'Teklif' },
        { value: 'Invoice', label: 'Fatura' },
        { value: 'Other', label: 'Diğer' }
    ];
    return (
        <aside className="sidebar">
            <div className="filter-section">
                <div className="filter-label">Doküman tipi</div>
                <div className="pill-group">
                    {types.map(t => (
                        <button
                            key={t.value || 'all'}
                            className={`pill ${type === t.value ? 'active' : ''} ${t.value ? `pill-${t.value}` : ''}`}
                            onClick={() => setType(t.value)}
                        >{t.label}</button>
                    ))}
                </div>
            </div>
            <div className="filter-section">
                <div className="filter-label">Sahip</div>
                <select value={owner} onChange={e => setOwner(e.target.value)}>
                    <option value="">Tüm kullanıcılar</option>
                    {owners.map(o => <option key={o} value={o}>{o}</option>)}
                </select>
            </div>
            <div className="filter-section">
                <div className="filter-label">Tarih aralığı</div>
                <input type="date" value={from} onChange={e => setFrom(e.target.value)} />
                <input type="date" value={to} onChange={e => setTo(e.target.value)} />
            </div>
            {activeCount > 0 && (
                <button className="btn ghost full" onClick={onReset}>
                    Filtreleri temizle ({activeCount})
                </button>
            )}
        </aside>
    );
}

function SearchBar({ query, setQuery, loading }) {
    return (
        <div className={`searchbar ${loading ? 'loading' : ''}`}>
            <span className="search-icon">🔍</span>
            <input
                type="search"
                placeholder="Doküman adı, müşteri, etiket veya içerik ara..."
                value={query}
                onChange={e => setQuery(e.target.value)}
                autoFocus
            />
            {query && (
                <button className="clear-btn" onClick={() => setQuery('')} aria-label="Temizle">×</button>
            )}
        </div>
    );
}

function ResultsHeader({ total, elapsedMs, loading, query }) {
    if (loading) return <div className="results-header"><span className="muted">Aranıyor...</span></div>;
    return (
        <div className="results-header">
            <span className="muted">
                <strong className="result-count">{total.toLocaleString('tr-TR')}</strong> sonuç
                {query && <> · "<em>{query}</em>" için</>}
                <span className="separator">·</span>
                {elapsedMs} ms
            </span>
        </div>
    );
}

function Suggestions({ terms, onPick }) {
    return (
        <div className="suggestions">
            <span>💡 Bunu mu demek istediniz?</span>
            {terms.map(t => (
                <button key={t} onClick={() => onPick(t)}>{t}</button>
            ))}
        </div>
    );
}

function SkeletonGrid() {
    return (
        <div className="cards">
            {Array.from({ length: 6 }).map((_, i) => <div key={i} className="card skeleton" />)}
        </div>
    );
}

function EmptyState({ query, hasFilters, onReset }) {
    return (
        <div className="empty">
            <div className="empty-icon">🔎</div>
            <h3>Sonuç bulunamadı</h3>
            <p>
                {query
                    ? <>"<em>{query}</em>" için eşleşen doküman yok.</>
                    : 'Bu kriterlere uyan doküman yok.'}
                {hasFilters && ' Filtreleri gevşetmeyi deneyin.'}
            </p>
            {hasFilters && <button className="btn ghost" onClick={onReset}>Filtreleri temizle</button>}
        </div>
    );
}

function DocGrid({ hits, onSelect }) {
    return (
        <div className="cards">
            {hits.map(h => <DocCard key={h.document.id} hit={h} onSelect={onSelect} />)}
        </div>
    );
}

function DocCard({ hit, onSelect }) {
    const d = hit.document;
    const date = new Date(d.createdAt).toLocaleDateString('tr-TR', {
        year: 'numeric', month: 'short', day: 'numeric'
    });
    const sizeKb = (d.sizeBytes / 1024).toFixed(1);

    const handleKey = (e) => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect?.(d); }
    };

    return (
        <article
            className={`card card-${d.type}`}
            onClick={() => onSelect?.(d)}
            onKeyDown={handleKey}
            tabIndex={0}
            role="button"
            aria-label={`${d.title} — düzenle`}
        >
            <div className="card-header">
                <span className={`type-badge type-${d.type}`}>{typeLabel[d.type]}</span>
                {hit.score > 0 && (
                    <span className="match-pill" title={`Alaka puanı: ${hit.score.toFixed(3)}`}>
                        ✓ eşleşme
                    </span>
                )}
            </div>
            <h3 className="card-title">{d.title}</h3>
            <div className="card-file">📎 {d.fileName}</div>
            <div className="card-meta">
                <span>👤 {d.owner}</span>
                <span>📅 {date}</span>
                <span>{sizeKb} KB</span>
            </div>
            {d.tags?.length > 0 && (
                <div className="card-tags">
                    {d.tags.map(t => <span key={t} className="tag">#{t}</span>)}
                </div>
            )}
        </article>
    );
}

function Pagination({ page, totalPages, onChange }) {
    return (
        <div className="pagination">
            <button className="btn ghost" disabled={page <= 1} onClick={() => onChange(page - 1)}>← Önceki</button>
            <span className="page-info">Sayfa <strong>{page}</strong> / {totalPages.toLocaleString('tr-TR')}</span>
            <button className="btn ghost" disabled={page >= totalPages} onClick={() => onChange(page + 1)}>Sonraki →</button>
        </div>
    );
}

function FloatingButton({ onClick }) {
    return <button className="fab" onClick={onClick} aria-label="Yeni doküman ekle" title="Yeni doküman">+</button>;
}

function DocumentFormModal({ mode, initial, bloom, onClose, onSubmit, onDelete }) {
    const isEdit = mode === 'edit';

    const [form, setForm] = useState(() => ({
        title: initial?.title ?? '',
        fileName: initial?.fileName ?? '',
        type: initial?.type ?? 'Contract',
        owner: initial?.owner ?? '',
        tags: (initial?.tags ?? []).join(', '),
        content: initial?.textPreview ?? ''
    }));

    /**
     * dupState:
     *   { kind: 'idle' }
     *   { kind: 'checking' }
     *   { kind: 'maybe', existing?: {...}, hash: string }   <- bloom dedi var, server teyit ediyor
     *   { kind: 'confirmed', existing: {...} }              <- DB'de gerçekten var
     *   { kind: 'clean' }                                   <- bloom yok dedi, network bile yok
     */
    const [dupState, setDupState] = useState({ kind: 'idle' });
    const lastCheckRef = useRef(0);

    // İçerik değiştiğinde, anlık bloom-filter kontrolü.
    // Bloom "yok" derse hiç network'e gitmiyoruz. "Var" derse server'a tek tek probe.
    useEffect(() => {
        if (isEdit) return; // edit modunda dup-check kapalı
        if (!bloom || !form.content) {
            setDupState({ kind: 'idle' });
            return;
        }
        const myId = ++lastCheckRef.current;
        let cancelled = false;

        const t = setTimeout(async () => {
            try {
                setDupState({ kind: 'checking' });
                const hash = await sha256Hex(form.content);
                if (cancelled || myId !== lastCheckRef.current) return;

                if (!bloom.mightContain(hash)) {
                    setDupState({ kind: 'clean' });
                    return;
                }
                // Bloom muhtemelen var dedi → server'a teyit
                const res = await fetch(`${API}/documents/by-hash/${hash}`);
                if (cancelled || myId !== lastCheckRef.current) return;
                if (res.status === 200) {
                    setDupState({ kind: 'confirmed', existing: await res.json() });
                } else {
                    // bloom yalan söyledi (false positive) — gerçekte yokmuş
                    setDupState({ kind: 'clean' });
                }
            } catch {
                setDupState({ kind: 'idle' });
            }
        }, 400);

        return () => { cancelled = true; clearTimeout(t); };
    }, [form.content, bloom, isEdit]);

    const update = (k, v) => setForm(f => ({ ...f, [k]: v }));

    const handleSubmit = (e) => {
        e.preventDefault();
        if (dupState.kind === 'confirmed') {
            const ok = window.confirm(
                `Bu içerik zaten "${dupState.existing.title}" başlığıyla sistemde var. Yine de yüklemek istiyor musunuz?`
            );
            if (!ok) return;
        }
        onSubmit({
            title: form.title.trim(),
            fileName: form.fileName.trim(),
            type: form.type,
            owner: form.owner.trim(),
            tags: form.tags.split(',').map(t => t.trim()).filter(Boolean),
            content: form.content
        });
    };

    const title = isEdit ? 'Dokümanı düzenle' : 'Yeni doküman ekle';
    const submitLabel = isEdit ? 'Kaydet' : 'Yükle';

    return (
        <Modal title={title} onClose={onClose} size="medium">
            <div className="info-banner">
                {isEdit
                    ? 'ℹ️ Düzenleme yaptığınızda kayıt güncellenir. Oluşturma tarihi korunur.'
                    : 'ℹ️ Yüklemeden önce sistem benzer dokümanları kontrol eder.'}
            </div>

            {isEdit && initial && (
                <div className="edit-meta">
                    <span>📅 Oluşturulma: {new Date(initial.createdAt).toLocaleDateString('tr-TR')}</span>
                    <span>·</span>
                    <span>Son güncelleme: {new Date(initial.updatedAt).toLocaleDateString('tr-TR')}</span>
                </div>
            )}

            <form onSubmit={handleSubmit} className="upload-form">
                <label className="field">
                    <span>Başlık *</span>
                    <input required value={form.title} onChange={e => update('title', e.target.value)}
                        placeholder="Sözleşme - Müşteri X 2026" />
                </label>
                <label className="field">
                    <span>Dosya adı *</span>
                    <input required value={form.fileName} onChange={e => update('fileName', e.target.value)}
                        placeholder="sozlesme-musteri-x.pdf" />
                </label>
                <label className="field">
                    <span>Tip</span>
                    <select value={form.type} onChange={e => update('type', e.target.value)}>
                        <option value="Contract">Sözleşme</option>
                        <option value="Offer">Teklif</option>
                        <option value="Invoice">Fatura</option>
                        <option value="Other">Diğer</option>
                    </select>
                </label>
                <label className="field">
                    <span>Sahip *</span>
                    <input required value={form.owner} onChange={e => update('owner', e.target.value)}
                        placeholder="kullanici.adi" />
                </label>
                <label className="field full">
                    <span>Etiketler</span>
                    <input value={form.tags} onChange={e => update('tags', e.target.value)}
                        placeholder="virgülle ayırın: 2026, müşteri-x" />
                </label>
                <label className="field full">
                    <span>İçerik özeti *</span>
                    <textarea required rows={4} value={form.content} onChange={e => update('content', e.target.value)}
                        placeholder="Belgenin kısa içeriği..." />
                    {!isEdit && <DupIndicator state={dupState} />}
                </label>

                <div className="modal-actions">
                    {isEdit && onDelete && (
                        <button type="button" className="btn danger" onClick={onDelete}>Sil</button>
                    )}
                    <div className="modal-actions-right">
                        <button type="button" className="btn ghost" onClick={onClose}>Vazgeç</button>
                        <button type="submit" className="btn primary">{submitLabel}</button>
                    </div>
                </div>
            </form>
        </Modal>
    );
}

/** Anlık duplicate kontrolünün durum göstergesi. */
function DupIndicator({ state }) {
    if (state.kind === 'idle') return null;
    if (state.kind === 'checking') {
        return <div className="dup-indicator checking">⏳ Duplicate kontrolü…</div>;
    }
    if (state.kind === 'clean') {
        return <div className="dup-indicator clean">✓ Bu içerik sistemde yok. Güvenle yükleyebilirsiniz.</div>;
    }
    if (state.kind === 'confirmed') {
        const e = state.existing;
        return (
            <div className="dup-indicator warn">
                ⚠️ Bu içerik zaten yüklü: <strong>{e.title}</strong>
                <div className="small muted">
                    {e.fileName} · {e.owner} · {new Date(e.createdAt).toLocaleDateString('tr-TR')}
                </div>
            </div>
        );
    }
    return null;
}

function DuplicateModal({ info, onCancel, onForce }) {
    const exact = info.duplicates.find(c => c.reason === 'exact');
    const canForce = !exact;
    const title = exact ? 'Bu doküman zaten yüklü' : 'Benzer dokümanlar bulundu';

    return (
        <Modal title={title} onClose={onCancel}>
            <div className={`info-banner ${exact ? 'warn' : ''}`}>
                {exact ? '⚠️' : '🔍'} {info.message}
            </div>
            <div className="dup-list">
                {info.duplicates.map(c => (
                    <div key={c.id} className="dup-item">
                        <div className="dup-info">
                            <strong>{c.title}</strong>
                            <div className="muted small">
                                📎 {c.fileName} · 👤 {c.owner} · 📅 {new Date(c.createdAt).toLocaleDateString('tr-TR')}
                            </div>
                        </div>
                        <span className={`badge badge-${c.reason}`}>
                            {c.reason === 'exact' ? 'Birebir aynı' : `%${Math.round(c.similarity * 100)} benzer`}
                        </span>
                    </div>
                ))}
            </div>
            <div className="modal-actions">
                <button className="btn primary" onClick={onCancel}>Mevcut belgeyi kullanacağım</button>
                {canForce && <button className="btn ghost" onClick={onForce}>Yine de yükle</button>}
            </div>
        </Modal>
    );
}

function Modal({ title, children, onClose, size = 'small' }) {
    useEffect(() => {
        const onKey = (e) => { if (e.key === 'Escape') onClose(); };
        window.addEventListener('keydown', onKey);
        return () => window.removeEventListener('keydown', onKey);
    }, [onClose]);

    return (
        <div className="modal-overlay" onClick={onClose}>
            <div className={`modal modal-${size}`} onClick={e => e.stopPropagation()}>
                <div className="modal-header">
                    <h3>{title}</h3>
                    <button className="icon-btn" onClick={onClose} aria-label="Kapat">×</button>
                </div>
                <div className="modal-body">{children}</div>
            </div>
        </div>
    );
}

function Toast({ msg, kind }) {
    return <div className={`toast toast-${kind}`}>{msg}</div>;
}

ReactDOM.createRoot(document.getElementById('root')).render(<App />);
