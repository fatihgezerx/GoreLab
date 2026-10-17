using Veneer;
using UnityEngine;

namespace GoreLab
{
    /// <summary>The GoreLab brand for the Veneer look: deep magenta-red accent, logo and banner from <c>Editor/Brand</c>.</summary>
    internal static class GoreBrand
    {
        /// <summary>The accent class the brand style sheet answers to (the exact magenta-red, instead of a kit preset).</summary>
        internal const string AccentClass = "gore-accent";

        /// <summary>The cached brand instance; use it for every Inspector and window of the system.</summary>
        internal static readonly VeneerBrand Value = VeneerBrand.FromAssets(
            name: "GoreLab",
            subtitle: "Cut-off limbs and broken objects",
            accent: new Color32(0xC2, 0x18, 0x5B, 255),
            logoPath: "Assets/Scripts/GoreLab/Editor/Brand/logo.png",
            bannerPath: "Assets/Scripts/GoreLab/Editor/Brand/banner.png",
            iconFolder: null,
            styleSheetPath: "Assets/Scripts/GoreLab/Editor/Brand/GoreBrand.uss",
            accentClass: AccentClass);
    }
}
