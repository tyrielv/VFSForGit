using GVFS.Common;
using GVFS.Common.FileSystem;
using GVFS.Platform.Windows;
using GVFS.Tests.Should;
using GVFS.UnitTests.Mock.Common;
using Moq;
using NUnit.Framework;
using System;
using System.IO;

namespace GVFS.UnitTests.Windows.Platform
{
    [TestFixture]
    public class ProjFSFilterTests
    {
        private const string ProjFSNativeLibFileName = "ProjectedFSLib.dll";

        private readonly string system32NativeLibPath = Path.Combine(Environment.SystemDirectory, ProjFSNativeLibFileName);
        private readonly string nonInboxNativeLibInstallPath = Path.Combine(ProcessHelper.GetCurrentProcessLocation(), ProjFSNativeLibFileName);

        private Mock<PhysicalFileSystem> mockFileSystem;
        private MockTracer mockTracer;

        [SetUp]
        public void Setup()
        {
            this.mockFileSystem = new Mock<PhysicalFileSystem>(MockBehavior.Strict);
            this.mockTracer = new MockTracer();
        }

        [TearDown]
        public void TearDown()
        {
            this.mockFileSystem.VerifyAll();
        }

        [TestCase]
        public void IsNativeLibInstalled_ReturnsTrueWhenLibInSystem32()
        {
            this.mockFileSystem.Setup(fileSystem => fileSystem.FileExists(this.system32NativeLibPath)).Returns(true);
            this.mockFileSystem.Setup(fileSystem => fileSystem.FileExists(this.nonInboxNativeLibInstallPath)).Returns(false);
            ProjFSFilter.IsNativeLibInstalled(this.mockTracer, this.mockFileSystem.Object).ShouldBeTrue();
        }

        [TestCase]
        public void IsNativeLibInstalled_ReturnsTrueWhenLibInNonInboxInstallLocation()
        {
            this.mockFileSystem.Setup(fileSystem => fileSystem.FileExists(this.system32NativeLibPath)).Returns(false);
            this.mockFileSystem.Setup(fileSystem => fileSystem.FileExists(this.nonInboxNativeLibInstallPath)).Returns(true);
            ProjFSFilter.IsNativeLibInstalled(this.mockTracer, this.mockFileSystem.Object).ShouldBeTrue();
        }

        [TestCase]
        public void IsNativeLibInstalled_ReturnsFalseWhenNativeLibraryDoesNotExistInAnyInstallLocation()
        {
            this.mockFileSystem.Setup(fileSystem => fileSystem.FileExists(It.IsAny<string>())).Returns(false);
            ProjFSFilter.IsNativeLibInstalled(this.mockTracer, this.mockFileSystem.Object).ShouldBeFalse();
        }
    }
}
