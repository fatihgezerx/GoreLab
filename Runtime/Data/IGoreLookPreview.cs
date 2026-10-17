using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// Optional: implemented by a data asset that the user drops into the Worn equipment card of the Gore Studio (a look, a loadout). When the
    /// studio puts the clothes on the naked character in its preview it only ties the pieces to the skeleton; whatever else a dressing system
    /// does to make the character look as it does in the game (skin materials put on the naked body, materials changed on the pieces, naked
    /// parts a piece hides) is done by this asset. The GoreLab knows nothing about any dressing system: the project's own code implements
    /// this, inside <c>#if HAS_GORE_LAB</c>. Editor only in effect: the studio calls it on a working copy of the character, never at run time.
    /// </summary>
    public interface IGoreLookPreview
    {
        /// <summary>
        /// Called after the clothes were put on. <paramref name="character"/> is the studio's working copy of the naked character (change it freely:
        /// the studio gives its materials back when the clothes are taken off); <paramref name="items"/> are the roots of the clothes that were put
        /// on; <paramref name="hidden"/> is where to add the body renderers the look hides (the studio hides those meshes in the preview).
        /// </summary>
        void PreparePreview(GameObject character, IReadOnlyList<GameObject> items, ICollection<Renderer> hidden);
    }
}
