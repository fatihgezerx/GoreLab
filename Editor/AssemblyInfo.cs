using System.Runtime.CompilerServices;

// The studio-look Inspectors and the Gore Studio shell live in their own assembly (they need Veneer, which is optional)
// but reuse the plain editor code of this one: GoreGUI, GoreProfileSummary, GoreBloodAssets and the Gore Studio window itself are internal.
[assembly: InternalsVisibleTo("GoreLab.Editor.Studio")]
