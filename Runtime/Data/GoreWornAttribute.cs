using System;

namespace GoreLab
{
    /// <summary>
    /// Marks a field that holds something a character can wear: a prefab, a component of a prefab (an item script), a mesh, or a list or a
    /// serializable class that holds them. Put it on the fields of your own data (a look, a loadout, an equipment table, any ScriptableObject)
    /// that name the equipment your characters are dressed with at run time; then drop that asset into the Worn equipment card of the Gore
    /// Studio. The studio follows only the marked fields, finds the skinned meshes in what they refer to and can switch Read/Write on for
    /// them, which cutting a worn piece at run time needs. The attribute does nothing at run time.
    /// </summary>
    /// <remarks>
    /// On a field that holds a class or a list of classes it marks every object reference inside; on a field that holds an object reference
    /// (or a list of them) it marks exactly that. Use it with <c>#if HAS_GORE_LAB</c> around it when your code must also compile
    /// without the GoreLab.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class GoreWornAttribute : Attribute
    {
    }
}
