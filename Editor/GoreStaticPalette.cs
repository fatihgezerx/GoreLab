using UnityEngine;

namespace GoreLab
{
    /// <summary>The colours of the pieces of a static mesh in the studio's preview: twelve that can be told apart at a glance, then the same ones darker and shifted.</summary>
    internal static class GoreStaticPalette
    {
        /// <summary>The colour of the rest (the triangles that belong to no piece): the same grey as the body of a character.</summary>
        internal static readonly Color32 RestColor = new Color32(138, 142, 150, 255);

        // No yellow here: yellow is the colour of the triangles that were added.
        private static readonly Color32[] Base =
        {
            new Color32(225, 65, 65, 255),
            new Color32(60, 140, 235, 255),
            new Color32(80, 190, 85, 255),
            new Color32(250, 150, 30, 255),
            new Color32(155, 90, 215, 255),
            new Color32(35, 195, 195, 255),
            new Color32(235, 95, 165, 255),
            new Color32(150, 205, 60, 255),
            new Color32(125, 85, 55, 255),
            new Color32(95, 95, 215, 255),
            new Color32(140, 225, 170, 255),
            new Color32(60, 110, 90, 255)
        };

        // Each time the palette is used up, the colours get a little darker and their hue moves a little.
        private const float CycleDarken = 0.18f;
        private const float CycleMinimum = 0.45f;
        private const float CycleHueShift = 0.035f;

        /// <summary>The colour of piece <paramref name="piece"/> (any number; a negative one is the rest).</summary>
        internal static Color32 Of(int piece)
        {
            if (piece < 0)
            {
                return RestColor;
            }

            var color = Base[piece % Base.Length];
            var cycle = piece / Base.Length;
            if (cycle == 0)
            {
                return color;
            }

            Color.RGBToHSV(color, out var hue, out var saturation, out var value);
            hue = (hue + CycleHueShift * cycle) % 1f;
            value *= Mathf.Max(CycleMinimum, 1f - CycleDarken * cycle);
            return Color.HSVToRGB(hue, saturation, value);
        }
    }
}
