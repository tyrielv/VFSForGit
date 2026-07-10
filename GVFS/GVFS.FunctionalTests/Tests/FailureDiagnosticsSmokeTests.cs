using GVFS.FunctionalTests.Tools;
using NUnit.Framework;

namespace GVFS.FunctionalTests.Tests
{
    // THROWAWAY: exists only on the ft-failure-diagnostics-citest branch to
    // exercise CaptureFailureDiagnostics (mount minidump + log preservation) in
    // CI. Deliberately fails while the mount is still alive. Do NOT merge.
    [TestFixture]
    [Category(FailureDiagnosticsSmokeTests.SmokeCategory)]
    public class FailureDiagnosticsSmokeTests
    {
        public const string SmokeCategory = "FailureDiagnosticsSmoke";

        private GVFSFunctionalTestEnlistment enlistment;

        [SetUp]
        public void SetUp()
        {
            this.enlistment = GVFSFunctionalTestEnlistment.CloneAndMount(GVFSTestConfig.PathToGVFS);
        }

        [TearDown]
        public void TearDown()
        {
            if (this.enlistment != null)
            {
                // Dump the still-live mount and preserve its logs before we
                // unmount, so the diagnostics artifact contains a real dump.
                this.enlistment.CaptureFailureDiagnostics();
                this.enlistment.UnmountAndDeleteAll();
            }
        }

        [TestCase]
        public void IntentionalFailureWithLiveMount()
        {
            Assert.Fail("Intentional failure to exercise failure-diagnostics capture (mount minidump + log preservation).");
        }
    }
}
