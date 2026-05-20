namespace DocumentManagementSystem.Models;

public enum DocumentType
{
    Contract = 1,
    Offer = 2,
    Invoice = 3,
    Other = 99
}

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DocumentType Type { get; set; }
    public string Owner { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Lightweight text content for indexing. In a real system this would be
    // an extracted/OCR'd preview kept separate from the original blob.
    public string TextPreview { get; set; } = string.Empty;
}
