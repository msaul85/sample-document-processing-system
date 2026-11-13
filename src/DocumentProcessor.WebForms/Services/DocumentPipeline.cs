using System;
using System.Threading;
using System.Threading.Tasks;
using DocumentProcessor.WebForms.Data;
using DocumentProcessor.WebForms.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocumentProcessor.WebForms.Services
{
    /// <summary>Extracts text from a stored document and saves an AI-generated summary.</summary>
    public class DocumentPipeline
    {
        private readonly DocumentDbContext _db;
        private readonly IDocumentStorage _storage;
        private readonly DocumentTextExtractor _textExtractor;
        private readonly IDocumentSummarizer _summarizer;
        private readonly ILogger<DocumentPipeline> _logger;

        public DocumentPipeline(
            DocumentDbContext db,
            IDocumentStorage storage,
            DocumentTextExtractor textExtractor,
            IDocumentSummarizer summarizer,
            ILogger<DocumentPipeline> logger)
        {
            _db = db;
            _storage = storage;
            _textExtractor = textExtractor;
            _summarizer = summarizer;
            _logger = logger;
        }

        public async Task ProcessAsync(Guid documentId, CancellationToken cancellationToken = default)
        {
            // No global query filter, so every query remembers the soft-delete condition itself.
            var document = await _db.Documents
                .FirstOrDefaultAsync(d => d.Id == documentId && !d.IsDeleted, cancellationToken);

            if (document == null)
            {
                _logger.LogWarning("Document {DocumentId} not found; skipping processing.", documentId);
                return;
            }

            try
            {
                document.Status = DocumentStatus.Processing;
                await _db.SaveChangesAsync(cancellationToken);

                using (var content = _storage.OpenRead(document.StoragePath))
                {
                    var text = _textExtractor.Extract(document.FileExtension, content);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        throw new InvalidOperationException(
                            "No text could be extracted from the document.");
                    }

                    document.Summary = await _summarizer.SummarizeAsync(
                        document.OriginalFileName, text, cancellationToken);
                }

                document.Status = DocumentStatus.Processed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Processing failed for document {DocumentId}.", documentId);
                document.Status = DocumentStatus.Failed;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
