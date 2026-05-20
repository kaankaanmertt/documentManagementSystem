namespace DocumentManagementSystem.Models;

public class SearchRequest
{
    public string? Query { get; set; }
    public DocumentType? Type { get; set; }
    public string? Owner { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<string>? Tags { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class SearchHit
{
    public Document Document { get; set; } = default!;
    public double Score { get; set; }
    public List<string> MatchedTerms { get; set; } = new();

    public SearchHit() { }

    public SearchHit(Document document, double score, List<string> matchedTerms)
    {
        Document = document;
        Score = score;
        MatchedTerms = matchedTerms;
    }
}

public class SearchResponse
{
    public IReadOnlyList<SearchHit> Hits { get; set; } = Array.Empty<SearchHit>();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long ElapsedMs { get; set; }
    public List<string> Suggestions { get; set; } = new();

    public SearchResponse() { }

    public SearchResponse(IReadOnlyList<SearchHit> hits, int total, int page, int pageSize, long elapsedMs, List<string> suggestions)
    {
        Hits = hits; Total = total; Page = page; PageSize = pageSize; ElapsedMs = elapsedMs; Suggestions = suggestions;
    }
}

public class UploadRequest
{
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DocumentType Type { get; set; } = DocumentType.Other;
    public string Owner { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<string>? Tags { get; set; }
    public bool ForceUpload { get; set; }
}

public class DuplicateCandidate
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Reason { get; set; } = string.Empty; // "exact" | "similar-name"
    public double Similarity { get; set; }

    public DuplicateCandidate() { }

    public DuplicateCandidate(Guid id, string title, string fileName, string owner, DateTime createdAt, string reason, double similarity)
    {
        Id = id; Title = title; FileName = fileName; Owner = owner; CreatedAt = createdAt; Reason = reason; Similarity = similarity;
    }
}

public class UploadResponse
{
    public bool Created { get; set; }
    public Document? Document { get; set; }
    public List<DuplicateCandidate> Duplicates { get; set; } = new();
    public string Message { get; set; } = string.Empty;

    public UploadResponse() { }

    public UploadResponse(bool created, Document? document, List<DuplicateCandidate> duplicates, string message)
    {
        Created = created; Document = document; Duplicates = duplicates; Message = message;
    }
}
