using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests
{
    [TestFixture]
    public class RefreshBookServiceFixture : CoreTest<RefreshBookService>
    {
        private const string OldId = "353082";
        private const string MergedId = "1005576";

        private AuthorMetadata _metadata;
        private Author _author;
        private Book _book;

        [SetUp]
        public void Setup()
        {
            _metadata = Builder<AuthorMetadata>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.ForeignAuthorId = "500")
                .Build();

            _author = Builder<Author>.CreateNew()
                .With(a => a.Id = 1)
                .With(a => a.AuthorMetadataId = 1)
                .With(a => a.Metadata = _metadata)
                .Build();

            // A book an author refresh added, with no files: the kind a refresh deletes once it can't be found.
            _book = Builder<Book>.CreateNew()
                .With(b => b.Id = 10)
                .With(b => b.ForeignBookId = OldId)
                .With(b => b.AuthorMetadataId = 1)
                .With(b => b.AuthorMetadata = _metadata)
                .With(b => b.Author = _author)
                .With(b => b.AddOptions = new AddBookOptions { AddType = BookAddType.Automatic })
                .Build();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByBook(It.IsAny<int>()))
                .Returns(new List<BookFile>());

            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.FindById("500"))
                .Returns(_author);

            Mocker.GetMock<IProvideAuthorInfo>()
                .Setup(s => s.GetAuthorInfo("500", It.IsAny<bool>()))
                .Returns(() => Builder<Author>.CreateNew().With(a => a.Metadata = Builder<AuthorMetadata>.CreateNew().With(m => m.ForeignAuthorId = "500").Build()).Build());

            Mocker.GetMock<IEditionService>()
                .Setup(s => s.GetEditionsForRefresh(It.IsAny<int>(), It.IsAny<List<string>>()))
                .Returns(new List<Edition>());
        }

        private void GivenMetadataAnswersWith(string foreignBookId, string foreignAuthorId = "500")
        {
            var remote = Builder<Book>.CreateNew()
                .With(b => b.Id = 0)
                .With(b => b.ForeignBookId = foreignBookId)
                .With(b => b.Title = _book.Title)
                .With(b => b.AuthorMetadata = Builder<AuthorMetadata>.CreateNew().With(m => m.ForeignAuthorId = foreignAuthorId).Build())
                .With(b => b.Editions = new List<Edition>())
                .Build();

            Mocker.GetMock<IProvideBookInfo>()
                .Setup(s => s.GetBookInfo(OldId))
                .Returns(Tuple.Create(foreignAuthorId, remote, new List<AuthorMetadata>()));
        }

        [Test]
        public void should_move_a_merged_book_to_its_new_id_instead_of_deleting_it()
        {
            // The author's book list carries the surviving work's ID, so the old one isn't in it.
            GivenMetadataAnswersWith(MergedId);

            Subject.RefreshBookInfo(_book, new List<Book>(), _author, false);

            Mocker.GetMock<IBookService>().Verify(s => s.DeleteBook(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IBookService>().Verify(s => s.UpdateMany(It.Is<List<Book>>(l => l.Count == 1 && l[0].Id == 10 && l[0].ForeignBookId == MergedId)), Times.AtLeastOnce());
        }

        [Test]
        public void should_still_delete_a_book_the_metadata_source_no_longer_has()
        {
            Mocker.GetMock<IProvideBookInfo>()
                .Setup(s => s.GetBookInfo(OldId))
                .Throws(new BookNotFoundException(OldId));

            Subject.RefreshBookInfo(_book, new List<Book>(), _author, false);

            Mocker.GetMock<IBookService>().Verify(s => s.DeleteBook(10, false, It.IsAny<bool>()), Times.Once());
        }

        [Test]
        public void should_not_monitor_new_books_of_an_author_added_to_hold_a_moved_book()
        {
            // The surviving work is credited to someone not in the library yet.
            GivenMetadataAnswersWith(MergedId, "600");

            Mocker.GetMock<IProvideAuthorInfo>()
                .Setup(s => s.GetAuthorInfo("600", It.IsAny<bool>()))
                .Returns(() => Builder<Author>.CreateNew().With(a => a.Metadata = Builder<AuthorMetadata>.CreateNew().With(m => m.ForeignAuthorId = "600").Build()).Build());

            Subject.RefreshBookInfo(_book, new List<Book>(), _author, false);

            Mocker.GetMock<IAddAuthorService>().Verify(s => s.AddAuthor(It.Is<Author>(a => a.MonitorNewItems == NewItemMonitorTypes.None), It.IsAny<bool>()), Times.Once());
        }
    }
}
