using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;
using Readarr.Http;
using Readarr.Http.REST;

namespace Readarr.Api.V1.RootFolders
{
    [V1ApiController]
    public class RootFolderController : RestControllerWithSignalR<RootFolderResource, RootFolder>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibreProxy;

        public RootFolderController(IRootFolderService rootFolderService,
                                ICalibreProxy calibreProxy,
                                IBroadcastSignalRMessage signalRBroadcaster,
                                RecycleBinValidator recycleBinValidator,
                                RootFolderValidator rootFolderValidator,
                                PathExistsValidator pathExistsValidator,
                                MappedNetworkDriveValidator mappedNetworkDriveValidator,
                                StartupFolderValidator startupFolderValidator,
                                SystemFolderValidator systemFolderValidator,
                                FolderWritableValidator folderWritableValidator,
                                QualityProfileExistsValidator qualityProfileExistsValidator,
                                MetadataProfileExistsValidator metadataProfileExistsValidator)
            : base(signalRBroadcaster)
        {
            _rootFolderService = rootFolderService;
            _calibreProxy = calibreProxy;

            SharedValidator.RuleFor(c => c.Path)
                .Cascade(CascadeMode.Stop)
                .IsValidPath()
                .SetValidator(mappedNetworkDriveValidator)
                .SetValidator(startupFolderValidator)
                .SetValidator(recycleBinValidator)
                .SetValidator(pathExistsValidator)
                .SetValidator(systemFolderValidator)
                .SetValidator(folderWritableValidator);

            PostValidator.RuleFor(c => c.Path)
                .SetValidator(rootFolderValidator);

            SharedValidator.RuleFor(c => c)
                .Must(x => CalibreLibraryOnlyUsedOnce(x))
                .When(x => x.IsCalibreLibrary)
                .WithMessage("Calibre library is already configured as a root folder");

            SharedValidator.RuleFor(c => c.Name)
                .NotEmpty();

            SharedValidator.RuleFor(c => c.DefaultMetadataProfileId).Cascade(CascadeMode.Stop)
                .ValidId()
                .SetValidator(metadataProfileExistsValidator);

            SharedValidator.RuleFor(c => c.DefaultQualityProfileId).Cascade(CascadeMode.Stop)
                .ValidId()
                .SetValidator(qualityProfileExistsValidator);

            // Deliberately not the same rule as the one above. 0 here means
            // "authors added under this root folder stay single-format",
            // which is both the default and a legitimate choice, and
            // ValidId() rejects 0 outright -- so copying the rule would make
            // every root folder unsaveable until a second profile was picked.
            // When a profile IS named it still has to exist.
            SharedValidator.RuleFor(c => c.DefaultAudiobookQualityProfileId).Cascade(CascadeMode.Stop)
                .SetValidator(qualityProfileExistsValidator)
                .When(c => c.DefaultAudiobookQualityProfileId > 0);

            // Calibre roots hold ebooks only (see CalibreFormats.IsAudio), so
            // an audiobook profile here would only arrange for audiobooks to
            // be downloaded and then refused at import.
            SharedValidator.RuleFor(c => c.DefaultAudiobookQualityProfileId)
                .Equal(0)
                .When(c => c.IsCalibreLibrary)
                .WithMessage("Calibre libraries can only hold ebooks. Clear the audiobook quality profile, or use an ordinary root folder for audiobooks");

            SharedValidator.RuleFor(c => c.Host).ValidHost().When(x => x.IsCalibreLibrary);
            SharedValidator.RuleFor(c => c.Port).InclusiveBetween(1, 65535).When(x => x.IsCalibreLibrary);
            SharedValidator.RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());
            SharedValidator.RuleFor(c => c.Username).NotEmpty().When(c => !string.IsNullOrWhiteSpace(c.Password));
            SharedValidator.RuleFor(c => c.Password).NotEmpty().When(c => !string.IsNullOrWhiteSpace(c.Username));

            SharedValidator.RuleFor(c => c.OutputFormat).Must(x => x.Split(',').All(y => Enum.TryParse<CalibreFormat>(y, true, out _))).When(x => x.OutputFormat.IsNotNullOrWhiteSpace()).WithMessage("Invalid output formats");
            SharedValidator.RuleFor(c => c.OutputProfile).IsEnumName(typeof(CalibreProfile));
        }

        private bool CalibreLibraryOnlyUsedOnce(RootFolderResource settings)
        {
            var newUri = GetLibraryUri(settings);
            return !_rootFolderService.All().Exists(x => x.Id != settings.Id &&
                                                    x.CalibreSettings != null &&
                                                    GetLibraryUri(x.CalibreSettings) == newUri);
        }

        private string GetLibraryUri(RootFolderResource settings)
        {
            return HttpUri.CombinePath(HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase), settings.Library);
        }

        private string GetLibraryUri(CalibreSettings settings)
        {
            return HttpUri.CombinePath(HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase), settings.Library);
        }

        protected override RootFolderResource GetResourceById(int id)
        {
            return _rootFolderService.Get(id).ToResource();
        }

        [RestPostById]
        public ActionResult<RootFolderResource> CreateRootFolder([FromBody] RootFolderResource rootFolderResource)
        {
            var model = rootFolderResource.ToModel();

            if (model.IsCalibreLibrary)
            {
                _calibreProxy.Test(model.CalibreSettings);
            }

            return Created(_rootFolderService.Add(model).Id);
        }

        [RestPutById]
        public ActionResult<RootFolderResource> UpdateRootFolder([FromBody] RootFolderResource rootFolderResource)
        {
            var model = rootFolderResource.ToModel();

            if (model.Path != rootFolderResource.Path)
            {
                throw new BadRequestException("Cannot edit root folder path");
            }

            if (model.IsCalibreLibrary)
            {
                _calibreProxy.Test(model.CalibreSettings);
            }

            _rootFolderService.Update(model);

            return Accepted(model.Id);
        }

        [HttpGet]
        public List<RootFolderResource> GetRootFolders()
        {
            return _rootFolderService.AllWithSpaceStats().ToResource();
        }

        [RestDeleteById]
        public void DeleteFolder(int id)
        {
            _rootFolderService.Remove(id);
        }
    }
}
