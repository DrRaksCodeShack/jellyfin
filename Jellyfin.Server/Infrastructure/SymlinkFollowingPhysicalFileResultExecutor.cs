﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Server.Infrastructure
{
    /// <inheritdoc />
    public class SymlinkFollowingPhysicalFileResultExecutor : PhysicalFileResultExecutor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SymlinkFollowingPhysicalFileResultExecutor"/> class.
        /// </summary>
        /// <param name="loggerFactory"></param>
        public SymlinkFollowingPhysicalFileResultExecutor(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        /// <inheritdoc />
        protected override FileMetadata GetFileInfo(string path)
        {
            var fileInfo = new FileInfo(path);
            var length = fileInfo.Length;
            // This may or may not be fixed in .NET 6, but looks like it will not https://github.com/dotnet/aspnetcore/issues/34371
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                using Stream thisFileStream = AsyncFile.OpenRead(path);
                length = thisFileStream.Length;
            }

            return new FileMetadata
            {
                Exists = fileInfo.Exists,
                Length = length,
                LastModified = fileInfo.LastWriteTimeUtc
            };
        }

        /// <inheritdoc />
        protected override Task WriteFileAsync(ActionContext context, PhysicalFileResult result, RangeItemHeaderValue range, long rangeLength)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (range != null && rangeLength == 0)
            {
                return Task.CompletedTask;
            }

            // It's a bit of wasted IO to perform this check again, but non-symlinks shouldn't use this code
            if (!IsSymLink(result.FileName))
            {
                return base.WriteFileAsync(context, result, range, rangeLength);
            }

            var response = context.HttpContext.Response;

            if (range != null)
            {
                return SendFileAsync(result.FileName,
                    response,
                    offset: range.From ?? 0L,
                    count: rangeLength);
            }

            return SendFileAsync(result.FileName,
                response,
                offset: 0,
                count: null);
        }

        private async Task SendFileAsync(string filePath, HttpResponse response, long offset, long? count)
        {
            var fileInfo = GetFileInfo(filePath);
            if (offset < 0 || offset > fileInfo.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, string.Empty);
            }

            if (count.HasValue
                && (count.Value < 0 || count.Value > fileInfo.Length - offset))
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, string.Empty);
            }

            // Copied from SendFileFallback.SendFileAsync
            const int bufferSize = 1024 * 16;

            await using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: bufferSize,
                options: (AsyncFile.UseAsyncIO ? FileOptions.Asynchronous : FileOptions.None) | FileOptions.SequentialScan);

            fileStream.Seek(offset, SeekOrigin.Begin);
            await StreamCopyOperation
                .CopyToAsync(fileStream, response.Body, count, bufferSize, CancellationToken.None)
                .ConfigureAwait(true);
        }

        private static bool IsSymLink(string path)
        {
            var fileInfo = new FileInfo(path);
            return (fileInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
    }
}
