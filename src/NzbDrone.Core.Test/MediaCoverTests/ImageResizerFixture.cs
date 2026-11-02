using System;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Test.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NzbDrone.Core.Test.MediaCoverTests
{
    [TestFixture]
    public class ImageResizerFixture : CoreTest<MediaCover.ImageResizer>
    {
        [SetUp]
        public void SetUp()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns<string>(s => File.Exists(s));

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.DeleteFile(It.IsAny<string>()))
                  .Callback<string>(s => File.Delete(s));

            Mocker.SetConstant<IPlatformInfo>(Mocker.Resolve<PlatformInfo>());
        }

        [Test]
        public void should_resize_image()
        {
            var mainFile = Path.Combine(TempFolder, "logo.png");
            var resizedFile = Path.Combine(TempFolder, "logo-170.png");

            File.Copy(GetTestPath("Files/1024.png"), mainFile);

            Subject.Resize(mainFile, resizedFile, 170);

            var fileInfo = new FileInfo(resizedFile);
            fileInfo.Exists.Should().BeTrue();
            fileInfo.Length.Should().BeInRange(1000, 30000);

            using (var image = Image.Load(resizedFile))
            {
                image.Height.Should().Be(170);
                image.Width.Should().Be(170);
            }
        }

        [Test]
        public void should_resize_jpeg()
        {
            var mainFile = Path.Combine(TempFolder, "cover.jpg");
            var resizedFile = Path.Combine(TempFolder, "cover-150.jpg");

            using (var original = new Image<Rgba32>(200, 300))
            {
                original.SaveAsJpeg(mainFile);
            }

            Subject.Resize(mainFile, resizedFile, 150);

            File.Exists(resizedFile).Should().BeTrue();

            var info = Image.Identify(resizedFile);
            info.Height.Should().Be(150);
            info.Width.Should().Be(100);
            info.Metadata.DecodedImageFormat.Name.Should().Be("JPEG");
        }

        [TestCase("tif")]
        [TestCase("bmp")]
        [TestCase("gif")]
        public void should_not_decode_formats_covers_do_not_use(string extension)
        {
            var mainFile = Path.Combine(TempFolder, "cover." + extension);
            var resizedFile = Path.Combine(TempFolder, "cover-150.jpg");

            using (var original = new Image<Rgba32>(200, 300))
            {
                original.Save(mainFile);
            }

            Assert.Throws<UnknownImageFormatException>(() => Subject.Resize(mainFile, resizedFile, 150));

            File.Exists(resizedFile).Should().BeFalse();
        }

        [Test]
        public void should_delete_file_if_failed()
        {
            var mainFile = Path.Combine(TempFolder, "junk.png");
            var resizedFile = Path.Combine(TempFolder, "junk-170.png");

            File.WriteAllText(mainFile, "Just some junk data that should make it throw an Exception.");

            Assert.Throws(Is.InstanceOf<Exception>(), () => Subject.Resize(mainFile, resizedFile, 170));

            File.Exists(resizedFile).Should().BeFalse();
        }
    }
}
