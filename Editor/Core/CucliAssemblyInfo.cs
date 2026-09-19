using System.Runtime.CompilerServices;

// Project-local command groups live OUTSIDE this package, in the host project, so that
// project-specific code never ends up in the public repository. Those assemblies still need the
// helpers that make a command pleasant to write - bounded description, object resolution,
// SerializedObject writes, paging - which are deliberately `internal` rather than public API.
//
// InternalsVisibleTo is the narrow way to grant that: the helper surface stays undocumented and
// free to change, while the two assembly names a project extension can realistically use still
// compile against it.
//
//  * CustomUnityCliExtensions - the asmdef that `cucli meta scaffold` creates.
//  * Assembly-CSharp-Editor   - for projects that prefer an Editor folder with no asmdef at all.
//
// Add further names here only if a real extension pattern needs them.
[assembly: InternalsVisibleTo("CustomUnityCliExtensions")]
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
