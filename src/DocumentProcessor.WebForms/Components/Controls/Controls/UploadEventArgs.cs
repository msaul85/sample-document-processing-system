using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Components.Forms;

namespace DocumentProcessor.WebForms.Components.Controls.Controls
{
    /// <summary>
    /// Raised by <see cref="DocumentUploader"/> once the picked files have passed the size,
    /// count and extension checks. Replaces the Web Forms UploadEventArgs that carried
    /// HttpPostedFile instances.
    /// </summary>
    public class UploadEventArgs : EventArgs
    {
        /// <summary>Largest stream a single browser file may be read through (50 MB).</summary>
        public const long MaxAllowedSize = 50L * 1024L * 1024L;

        public UploadEventArgs(IList<IBrowserFile> files)
        {
            Files = files ?? throw new ArgumentNullException(nameof(files));
        }

        /// <summary>
        /// The accepted files. They can only be read while the handler for
        /// <see cref="DocumentUploader.UploadRequested"/> is running: the uploader resets its
        /// file input once the handler returns.
        /// </summary>
        public IList<IBrowserFile> Files { get; }

        /// <summary>Opens the upload stream with the 50 MB per-file limit applied.</summary>
        public Stream OpenReadStream(IBrowserFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            return file.OpenReadStream(MaxAllowedSize);
        }
    }
}
