// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1672: MemoryMappedFileReader vendors X1.Common.Utils.Files.ReadStringFromMemoryMappedFile,
    /// which SearchManagerCallbacks uses to read large search-result/content payloads that
    /// X1ServiceHost.exe writes out-of-band from the WCF channel (OnSearchResultsChangedMMF /
    /// OnSearchResultsReadyMMF). That path has zero prior coverage - the existing
    /// SearchManagerCallbacksTests only exercise the array-based OnSearchResultsChanged/
    /// OnSearchResultsReady callbacks directly, never a real MMF.
    /// </summary>
    [TestFixture]
    public class MemoryMappedFileReaderTests
    {
        [Test]
        public void IsMemoryMappedFile_X1Prefix_ReturnsTrue()
        {
            Assert.That(MemoryMappedFileReader.IsMemoryMappedFile("x1:%%42%%somename"), Is.True);
        }

        [Test]
        public void IsMemoryMappedFile_NoPrefix_ReturnsFalse()
        {
            Assert.That(MemoryMappedFileReader.IsMemoryMappedFile("C:\\some\\path.txt"), Is.False);
        }

        [Test]
        public void IsMemoryMappedFile_Null_ReturnsFalse()
        {
            Assert.That(MemoryMappedFileReader.IsMemoryMappedFile(null), Is.False);
        }

        [Test]
        public void ReadStringFromMemoryMappedFile_NonMmfPath_ReturnsNullWithoutThrowing()
        {
            string result = MemoryMappedFileReader.ReadStringFromMemoryMappedFile("C:\\not\\an\\mmf.txt");
            Assert.That(result, Is.Null);
        }

        [Test]
        public void ReadStringFromMemoryMappedFile_NonExistentMmfName_ReturnsNullWithoutThrowing()
        {
            string result = MemoryMappedFileReader.ReadStringFromMemoryMappedFile("x1:%%10%%doesnotexist_" + System.Guid.NewGuid());
            Assert.That(result, Is.Null);
        }

        [Test]
        public void ReadStringFromMemoryMappedFile_RealMmf_RoundTripsContent()
        {
            // Mirrors production: X1ServiceHost creates the MMF using the full, prefixed
            // "x1:%%size%%name" string as the OS-level MMF name, and the reader opens it via
            // that same full string - not a stripped name.
            string mmfName = "x1:%%64%%testmmf_" + System.Guid.NewGuid().ToString("N");
            const string expected = "<results><item>hello from the fake service host</item></results>";

            using (MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mmfName, expected.Length * 2 + 1))
            {
                using (MemoryMappedViewStream stream = mmf.CreateViewStream())
                using (var writer = new StreamWriter(stream, Encoding.Unicode))
                    writer.Write(expected);

                string actual = MemoryMappedFileReader.ReadStringFromMemoryMappedFile(mmfName);
                Assert.That(actual, Is.EqualTo(expected));
            }
        }
    }
}
