using TMPro;
using UnityEngine;

namespace Gulkonster.WaxKit
{
    /// <summary>Colors and font of <see cref="WaxDefaultUI"/>. Set it on <see cref="WaxKit.Theme"/>. The QR always stays black on white.</summary>
    public sealed class WaxUiTheme
    {
        public Color Dim;
        public Color Panel;
        public Color Primary;
        public Color PrimaryText;
        public Color Secondary;
        public Color SecondaryText;
        public Color Text;
        /// <summary>Null uses the TextMeshPro default font.</summary>
        public TMP_FontAsset Font;

        /// <summary>A new dark theme each time, so changing it never changes the preset.</summary>
        public static WaxUiTheme Dark => new WaxUiTheme
        {
            Dim = new Color(0, 0, 0, 0.6f),
            Panel = new Color(0.11f, 0.12f, 0.14f),
            Primary = new Color(0.22f, 0.4f, 0.85f),
            PrimaryText = Color.white,
            Secondary = new Color(0.24f, 0.26f, 0.31f),
            SecondaryText = new Color(0.93f, 0.94f, 0.96f),
            Text = new Color(0.93f, 0.94f, 0.96f),
        };

        /// <summary>A new light theme each time, so changing it never changes the preset.</summary>
        public static WaxUiTheme Light => new WaxUiTheme
        {
            Dim = new Color(0, 0, 0, 0.6f),
            Panel = new Color(0.97f, 0.97f, 0.98f),
            Primary = new Color(0.22f, 0.4f, 0.85f),
            PrimaryText = Color.white,
            Secondary = new Color(0.86f, 0.87f, 0.9f),
            SecondaryText = new Color(0.1f, 0.1f, 0.12f),
            Text = new Color(0.1f, 0.1f, 0.12f),
        };
    }
}
