// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    [TestFixture]
    public class ActionRegistryTests
    {
        // ── GetActions ────────────────────────────────────────────────────────────

        [Test]
        public void GetActions_FilesTable_ContainsGetPath()
        {
            var actions = ActionRegistry.GetActions("Files").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("get_path"));
        }

        [Test]
        public void GetActions_FilesTable_ContainsOpen()
        {
            var actions = ActionRegistry.GetActions("Files").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("open"));
        }

        [Test]
        public void GetActions_FilesTable_ContainsShowInFolder()
        {
            var actions = ActionRegistry.GetActions("Files").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("show_in_folder"));
        }

        [Test]
        public void GetActions_GmailTable_ContainsGetUrl()
        {
            var actions = ActionRegistry.GetActions("Gmail").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("get_url"));
        }

        [Test]
        public void GetActions_GmailTable_ContainsOpenUrl()
        {
            var actions = ActionRegistry.GetActions("Gmail").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("open_url"));
        }

        [Test]
        public void GetActions_GDriveTable_ContainsGetUrl()
        {
            var actions = ActionRegistry.GetActions("GDrive").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("get_url"));
        }

        [Test]
        public void GetActions_UnknownTable_ReturnsEmpty()
        {
            var actions = ActionRegistry.GetActions("NonExistentTable").ToArray();
            Assert.That(actions, Is.Empty);
        }

        [Test]
        public void GetActions_NullTable_ReturnsEmpty()
        {
            var actions = ActionRegistry.GetActions(null).ToArray();
            Assert.That(actions, Is.Empty);
        }

        [Test]
        public void GetActions_AllActionsHaveDescriptions()
        {
            foreach (var table in new[] { "Files", "Gmail", "GDrive", "MSMail", "Dropbox", "OneDrive" })
            {
                foreach (var (action, description) in ActionRegistry.GetActions(table))
                {
                    Assert.That(description, Is.Not.Null.And.Not.Empty,
                        "table=" + table + " action=" + action + " has no description");
                }
            }
        }

        [Test]
        public void GetActions_TableNameIsCaseInsensitive()
        {
            var lower = ActionRegistry.GetActions("files").ToArray();
            var upper = ActionRegistry.GetActions("FILES").ToArray();
            Assert.That(lower.Length, Is.EqualTo(upper.Length));
            Assert.That(lower.Length, Is.GreaterThan(0));
        }

        // ── IsActionSupported ─────────────────────────────────────────────────────

        [Test]
        public void IsActionSupported_Files_Open_ReturnsTrue()
        {
            Assert.That(ActionRegistry.IsActionSupported("Files", "open"), Is.True);
        }

        [Test]
        public void IsActionSupported_Files_GetPath_ReturnsTrue()
        {
            Assert.That(ActionRegistry.IsActionSupported("Files", "get_path"), Is.True);
        }

        [Test]
        public void IsActionSupported_Files_Delete_ReturnsFalse()
        {
            Assert.That(ActionRegistry.IsActionSupported("Files", "delete"), Is.False);
        }

        [Test]
        public void IsActionSupported_Gmail_GetUrl_ReturnsTrue()
        {
            Assert.That(ActionRegistry.IsActionSupported("Gmail", "get_url"), Is.True);
        }

        [Test]
        public void IsActionSupported_Gmail_GetPath_ReturnsFalse()
        {
            Assert.That(ActionRegistry.IsActionSupported("Gmail", "get_path"), Is.False);
        }

        [Test]
        public void IsActionSupported_UnknownTable_ReturnsFalse()
        {
            Assert.That(ActionRegistry.IsActionSupported("NoSuchTable", "open"), Is.False);
        }

        [Test]
        public void IsActionSupported_NullTable_ReturnsFalse()
        {
            Assert.That(ActionRegistry.IsActionSupported(null, "open"), Is.False);
        }

        [Test]
        public void IsActionSupported_NullAction_ReturnsFalse()
        {
            Assert.That(ActionRegistry.IsActionSupported("Files", null), Is.False);
        }

        [Test]
        public void IsActionSupported_ActionNameIsCaseInsensitive()
        {
            Assert.That(ActionRegistry.IsActionSupported("Files", "OPEN"), Is.True);
            Assert.That(ActionRegistry.IsActionSupported("Files", "Get_Path"), Is.True);
        }

        // ── #3: open action for cloud tables ──────────────────────────────────────

        [Test]
        public void GetActions_OneDrive_ContainsOpen()
        {
            var actions = ActionRegistry.GetActions("OneDrive").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("open"));
        }

        [Test]
        public void GetActions_OneDrive_ContainsOpenUrl()
        {
            var actions = ActionRegistry.GetActions("OneDrive").Select(a => a.action).ToArray();
            Assert.That(actions, Does.Contain("open_url"));
        }

        [Test]
        public void GetActions_GDrive_OpenNotDuplicated()
        {
            var openCount = ActionRegistry.GetActions("GDrive")
                .Count(a => string.Equals(a.action, "open", System.StringComparison.OrdinalIgnoreCase));
            Assert.That(openCount, Is.EqualTo(1), "GDrive should expose 'open' exactly once");
        }

        [Test]
        public void GetActions_SharePointAndSP365_ContainOpen()
        {
            Assert.That(ActionRegistry.GetActions("SharePoint").Select(a => a.action), Does.Contain("open"));
            Assert.That(ActionRegistry.GetActions("SP365").Select(a => a.action), Does.Contain("open"));
        }

        // ── #6: HasPreview ────────────────────────────────────────────────────────

        [Test]
        public void HasPreview_PreviewTables_ReturnTrue()
        {
            foreach (var table in new[] { "OneDrive", "GDrive", "MSMail", "Exchange", "Gmail", "Files" })
                Assert.That(ActionRegistry.HasPreview(table), Is.True, "expected preview for " + table);
        }

        [Test]
        public void HasPreview_NonPreviewTable_ReturnsFalse()
        {
            Assert.That(ActionRegistry.HasPreview("Teams"), Is.False);
            Assert.That(ActionRegistry.HasPreview("Dropbox"), Is.False);
        }

        [Test]
        public void HasPreview_NullOrUnknown_ReturnsFalse()
        {
            Assert.That(ActionRegistry.HasPreview(null), Is.False);
            Assert.That(ActionRegistry.HasPreview("NoSuchTable"), Is.False);
        }

        [Test]
        public void HasPreview_IsCaseInsensitive()
        {
            Assert.That(ActionRegistry.HasPreview("onedrive"), Is.True);
            Assert.That(ActionRegistry.HasPreview("FILES"), Is.True);
        }
    }
}
