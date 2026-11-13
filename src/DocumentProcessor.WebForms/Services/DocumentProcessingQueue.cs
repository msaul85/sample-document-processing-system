using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DocumentProcessor.WebForms.Services
{
    /// <summary>
    /// In-memory queue of documents waiting to be summarized. Replaces the legacy page calling
    /// DocumentPipeline.Process on the request thread: the page stores the upload, enqueues its
    /// id and returns at once; <see cref="DocumentProcessingWorker"/> drains the queue.
    /// </summary>
    public sealed class DocumentProcessingQueue
    {
        private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        /// <summary>Queues a stored document for text extraction and summarization.</summary>
        public ValueTask EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(documentId, cancellationToken);
        }

        /// <summary>Yields queued document ids until <paramref name="cancellationToken"/> is cancelled.</summary>
        public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
