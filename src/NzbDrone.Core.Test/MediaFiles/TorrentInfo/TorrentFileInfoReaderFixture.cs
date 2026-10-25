using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.TorrentInfo
{
    // Download clients match grabs by info hash: it must stay the 40-character
    // uppercase hex SHA-1 of the bencoded info dictionary across MonoTorrent
    // upgrades (v3 replaced InfoHash with InfoHashes).
    [TestFixture]
    public class TorrentFileInfoReaderFixture : CoreTest<TorrentFileInfoReader>
    {
        private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

        [Test]
        public void should_return_uppercase_sha1_of_info_dictionary()
        {
            var pieces = Enumerable.Repeat((byte)0xAB, 20).ToArray();
            var info = Ascii("d6:lengthi1e4:name5:a.txt12:piece lengthi16384e6:pieces20:")
                       .Concat(pieces)
                       .Concat(Ascii("e"))
                       .ToArray();
            var torrent = Ascii("d4:info").Concat(info).Concat(Ascii("e")).ToArray();

            var expected = Convert.ToHexString(SHA1.HashData(info));

            var hash = Subject.GetHashFromTorrentFile(torrent);

            hash.Should().Be(expected);
            hash.Should().HaveLength(40).And.MatchRegex("^[0-9A-F]+$");
        }

        [Test]
        public void should_throw_on_invalid_torrent()
        {
            Assert.That(() => Subject.GetHashFromTorrentFile(Ascii("not a torrent")), Throws.Exception);
        }
    }
}
