-- Doküman Yönetim Sistemi — Postgres şeması
-- Bu dosya postgres container ilk açılışında otomatik çalıştırılır.
-- Lokal kurulumda elle uygulanır: psql -U dmsuser -d documentdb -f init.sql

CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS documents (
    id              UUID PRIMARY KEY,
    title           TEXT          NOT NULL,
    file_name       TEXT          NOT NULL,
    type            SMALLINT      NOT NULL,
    owner           TEXT          NOT NULL,
    size_bytes      BIGINT        NOT NULL,
    content_hash    CHAR(64)      NOT NULL,
    tags            TEXT[]        NOT NULL DEFAULT '{}',
    text_preview    TEXT          NOT NULL,
    created_at      TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ   NOT NULL DEFAULT now(),
    search_vector   tsvector
);

-- search_vector için trigger.
-- (Generated STORED column olarak yapamadık çünkü to_tsvector STABLE,
-- IMMUTABLE değil — Postgres bunu generated column'da kabul etmiyor.)
CREATE OR REPLACE FUNCTION documents_search_vector_update() RETURNS trigger AS $$
BEGIN
    NEW.search_vector :=
        setweight(to_tsvector('simple', coalesce(NEW.title, '')),        'A') ||
        setweight(to_tsvector('simple', coalesce(NEW.file_name, '')),    'B') ||
        setweight(to_tsvector('simple', array_to_string(NEW.tags, ' ')), 'C') ||
        setweight(to_tsvector('simple', coalesce(NEW.text_preview, '')), 'D');
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_documents_search_vector ON documents;
CREATE TRIGGER trg_documents_search_vector
    BEFORE INSERT OR UPDATE OF title, file_name, tags, text_preview ON documents
    FOR EACH ROW EXECUTE FUNCTION documents_search_vector_update();

-- Full-text search indeksi
CREATE INDEX IF NOT EXISTS idx_documents_search       ON documents USING GIN(search_vector);

-- Yapısal filtreler için B-tree indeksleri
CREATE INDEX IF NOT EXISTS idx_documents_type         ON documents(type);
CREATE INDEX IF NOT EXISTS idx_documents_owner        ON documents(owner);
CREATE INDEX IF NOT EXISTS idx_documents_created_at   ON documents(created_at DESC);

-- Duplicate kontrolü için hash üzerinde indeks (exact match O(log n))
CREATE INDEX IF NOT EXISTS idx_documents_content_hash ON documents(content_hash);

-- Suggest için trigram indeksi (fuzzy "did you mean" sorgularına yardım eder)
CREATE INDEX IF NOT EXISTS idx_documents_title_trgm   ON documents USING GIN(title gin_trgm_ops);
