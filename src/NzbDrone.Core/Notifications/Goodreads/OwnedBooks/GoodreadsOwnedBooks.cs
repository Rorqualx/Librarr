using System;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Notifications.Goodreads
{
    public class GoodreadsOwnedBooks : GoodreadsNotificationBase<GoodreadsOwnedBooksNotificationSettings>
    {
        public GoodreadsOwnedBooks(IHttpClient httpClient,
                                   Logger logger)
        : base(httpClient, logger)
        {
        }

        public override string Name => "Goodreads Owned Books";
        public override string Link => "https://goodreads.com/";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var edition = message.Book.PrimaryEdition();

            if (edition == null)
            {
                _logger.Debug("Not adding {0} to Goodreads owned books: it has no editions", message.Book);
                return;
            }

            AddOwnedBook(edition.ForeignEditionId);
        }

        private void AddOwnedBook(string bookId)
        {
            var req = RequestBuilder()
                .Post()
                .SetSegment("route", "owned_books.xml")
                .AddFormParameter("owned_book[book_id]", bookId)
                .AddFormParameter("owned_book[condition_code]", Settings.Condition)
                .AddFormParameter("owned_book[original_purchase_date]", DateTime.Now.ToString("O"));

            if (Settings.Description.IsNotNullOrWhiteSpace())
            {
                req.AddFormParameter("owned_book[condition_description]", Settings.Description);
            }

            if (Settings.Location.IsNotNullOrWhiteSpace())
            {
                req.AddFormParameter("owned_book[original_purchase_location]", Settings.Location);
            }

            OAuthExecute(req);
        }
    }
}
