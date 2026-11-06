using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityWaxKit.Tests
{
    /// <summary>The notices that ship in builds are the same as THIRD_PARTY_NOTICES.md and name every shipped / ported part.</summary>
    public class NoticesTests
    {
        [Test]
        public void ShippedCopy_MatchesTheNoticesFile()
        {
            var file = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.unitywaxkit.core/THIRD_PARTY_NOTICES.md");
            var shipped = WaxKit.ThirdPartyNotices;
            Assert.IsNotNull(file);
            Assert.AreEqual(file.text.Replace("\r\n", "\n"), shipped.Replace("\r\n", "\n"),
                "Copy THIRD_PARTY_NOTICES.md to Runtime/Resources/UnityWaxKit/ThirdPartyNotices.txt after changing it.");
            foreach (var name in new[] { "BouncyCastle", "AndroidX Browser", "Newtonsoft.Json", "@wharfkit/antelope", "@proton/web-sdk", "@greymass/buoy", "libwebp 1.6.0", "PATENTS" })
                StringAssert.Contains(name, shipped);
        }
    }
}
