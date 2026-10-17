using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// Put on a skinned mesh (or on any object above it) that a <see cref="GoreCharacter"/> must not cut at run time: a prop, a mesh with no
    /// bone weights, a piece that should stay on the body. The pieces the editor setup prepared are not affected.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GoreIgnore : MonoBehaviour
    {
    }
}
