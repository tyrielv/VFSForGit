using GVFS.Common.Git;
using GVFS.Tests.Should;
using GVFS.UnitTests.Mock.Common;
using NUnit.Framework;
using System.Collections.Generic;

namespace GVFS.UnitTests.Common.Git
{
    [TestFixture]
    public class RequiredGitConfigTests
    {
        [TestCase]
        public void DefaultOverloadMatchesDisabledOverload()
        {
            MockGVFSEnlistment enlistment = new MockGVFSEnlistment();

            Dictionary<string, string> viaDefault = RequiredGitConfig.GetRequiredSettings(enlistment);
            Dictionary<string, string> viaDisabled = RequiredGitConfig.GetRequiredSettings(enlistment, autoSparseIndexEnabled: false);

            viaDefault.Count.ShouldEqual(viaDisabled.Count);
            foreach (KeyValuePair<string, string> setting in viaDefault)
            {
                viaDisabled.ContainsKey(setting.Key).ShouldBeTrue("Missing key: " + setting.Key);
                viaDisabled[setting.Key].ShouldEqual(setting.Value);
            }
        }

        [TestCase]
        public void DisabledDoesNotWriteSparseIndexKeys()
        {
            MockGVFSEnlistment enlistment = new MockGVFSEnlistment();

            Dictionary<string, string> settings = RequiredGitConfig.GetRequiredSettings(enlistment, autoSparseIndexEnabled: false);

            settings.ContainsKey(GitConfigSetting.CoreSparseCheckoutConeName).ShouldBeFalse();
            settings.ContainsKey(GitConfigSetting.IndexSparseName).ShouldBeFalse();
            settings.ContainsKey(GitConfigSetting.SparseExpectFilesOutsideOfPatternsName).ShouldBeFalse();
        }

        [TestCase]
        public void EnabledAddsExactlyTheThreeSparseIndexKeys()
        {
            MockGVFSEnlistment enlistment = new MockGVFSEnlistment();

            Dictionary<string, string> disabled = RequiredGitConfig.GetRequiredSettings(enlistment, autoSparseIndexEnabled: false);
            Dictionary<string, string> enabled = RequiredGitConfig.GetRequiredSettings(enlistment, autoSparseIndexEnabled: true);

            enabled.Count.ShouldEqual(disabled.Count + 3);

            enabled[GitConfigSetting.CoreSparseCheckoutConeName].ShouldEqual("true");
            enabled[GitConfigSetting.IndexSparseName].ShouldEqual("true");
            enabled[GitConfigSetting.SparseExpectFilesOutsideOfPatternsName].ShouldEqual("true");

            // Every setting present when disabled must be unchanged when enabled.
            foreach (KeyValuePair<string, string> setting in disabled)
            {
                enabled.ContainsKey(setting.Key).ShouldBeTrue("Missing key: " + setting.Key);
                enabled[setting.Key].ShouldEqual(setting.Value);
            }
        }
    }
}
