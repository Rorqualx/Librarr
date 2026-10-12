using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace Readarr.Http.Frontend.Mappers
{
    public abstract class StaticResourceMapperBase : IMapHttpRequestsToDisk
    {
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly StringComparison _caseSensitive;
        private readonly IContentTypeProvider _mimeTypeProvider;

        protected StaticResourceMapperBase(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;

            _mimeTypeProvider = new FileExtensionContentTypeProvider();
            _caseSensitive = RuntimeInfo.IsProduction ? DiskProviderBase.PathStringComparison : StringComparison.OrdinalIgnoreCase;
        }

        // The only folder this mapper may serve files from. Abstract so that a
        // new mapper cannot be written without declaring one.
        protected abstract string RootFolder { get; }

        public abstract string Map(string resourceUrl);

        public abstract bool CanHandle(string resourceUrl);

        public IActionResult GetResponse(string resourceUrl)
        {
            var filePath = Map(resourceUrl);

            // Answered exactly like a missing file, so the response says nothing
            // about what does or does not exist outside the folder.
            if (!IsInsideRootFolder(filePath))
            {
                _logger.Warn("Refusing to serve {0}, it is outside of {1}", resourceUrl, RootFolder);

                return null;
            }

            if (_diskProvider.FileExists(filePath, _caseSensitive))
            {
                if (!_mimeTypeProvider.TryGetContentType(filePath, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                return new FileStreamResult(GetContentStream(filePath), new MediaTypeHeaderValue(contentType)
                {
                    Encoding = contentType == "text/plain" ? Encoding.UTF8 : null
                });
            }

            _logger.Warn("File {0} not found", filePath);

            return null;
        }

        protected bool IsInsideRootFolder(string filePath)
        {
            return PathContainment.IsInside(RootFolder, filePath, _caseSensitive);
        }

        protected virtual Stream GetContentStream(string filePath)
        {
            return File.OpenRead(filePath);
        }
    }
}
