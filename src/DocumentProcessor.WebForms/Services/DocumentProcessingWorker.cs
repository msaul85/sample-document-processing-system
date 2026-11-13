using System;
using System.Threading;
using System.Threading.Tasks;
using DocumentProcessor.WebForms.Data;
using DocumentProcessor.WebForms.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentProcessor.WebForms.Services
{
    /// <summary>
    /// Background service that summarizes queued documents one at a time through
    /// <see cref="DocumentPipeline"/>. The pipeline records Processing / Processed / Failed on
    /// the document itself, which the Default page picks up by polling.
    /// </summary>
    public sealed class DocumentProcessingWorker : BackgroundService
    {
        private readonly DocumentProcessingQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DocumentProcessingWorker> _logger;

        public DocumentProcessingWorker(
            DocumentProcessingQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<DocumentProcessingWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Re-queue documents a previous run stored as Pending but never processed: the queue
                // lives in memory only. Documents uploaded after startup are enqueued by the page itself.
                var startedAt = DateTimeOffset.UtcNow;
                try
                {
                    await using var recoveryScope = _scopeFactory.CreateAsyncScope();
                    var db = recoveryScope.ServiceProvider.GetRequiredService<DocumentDbContext>();
                    var pendingIds = await db.Documents
                        .AsNoTracking()
                        .Where(d => d.Status == DocumentStatus.Pending && !d.IsDeleted && d.UploadedAt < startedAt)
                        .OrderBy(d => d.UploadedAt)
                        .Select(d => d.Id)
                        .ToListAsync(stoppingToken);

                    foreach (var id in pendingIds)
                    {
                        await _queue.EnqueueAsync(id, stoppingToken);
                    }

                    if (pendingIds.Count > 0)
                    {
                        _logger.LogInformation("Re-queued {Count} pending document(s) from a previous run.", pendingIds.Count);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not re-queue pending documents at startup.");
                }

                await foreach (var documentId in _queue.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var pipeline = scope.ServiceProvider.GetRequiredService<DocumentPipeline>();

                        await pipeline.ProcessAsync(documentId);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background processing failed for document {DocumentId}.", documentId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutting down.
            }
        }
    }
}
