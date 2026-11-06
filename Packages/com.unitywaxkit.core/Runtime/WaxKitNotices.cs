using UnityEngine;

namespace UnityWaxKit
{
    public static partial class WaxKit
    {
        /// <summary>Resources path of the kit's third-party notices (a copy of THIRD_PARTY_NOTICES.md, in every build).</summary>
        internal const string NoticesResource = "UnityWaxKit/ThirdPartyNotices";

        /// <summary>
        /// The kit's third-party notices as text, for a licenses / credits screen. They cover Unity WAX Kit only, not the
        /// game's other plugins. The build step also puts the file next to WebGL and Windows builds and into Android APKs.
        /// </summary>
        public static string ThirdPartyNotices => Resources.Load<TextAsset>(NoticesResource)?.text ?? "";
    }
}
