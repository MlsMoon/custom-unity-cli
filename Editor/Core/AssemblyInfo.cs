using System.Runtime.CompilerServices;

// The helper layer (CucliAssets, CucliDescribe, CucliObjectResolver, CucliSerialized,
// CucliReflection, CucliValues, CucliPaging, CucliOptional, ...) is marked internal so the package
// does not promise a large public API surface it would then have to keep stable.
//
// Project-specific command groups, however, usually live in a *separate* assembly outside this
// repository, and they need that same helper layer to be worth anything. Granting access to exactly
// one assembly - the name `cucli extend init` documents and `cucli extend list` reports on - keeps
// the surface unpublished while still letting project commands use the real API instead of a lossy
// wrapper.
//
// Note that an extension does NOT need its own assembly at all: a plain folder under
// Assets/<anything>/Editor/ compiles into Assembly-CSharp-Editor and gets access to the public
// types. This grant only matters when a project deliberately wants an isolated assembly.
//
// If you hand-write your own extension assembly with a different name, add your own
// InternalsVisibleTo line here, or reference the package by source instead of by assembly.
[assembly: InternalsVisibleTo("CustomUnityCli.Extensions.Editor")]

// This grant is the one that actually matters for the zero-ceremony path.
//
// The recommended extension location is Assets/CustomUnityCli.Extensions/Editor/Commands/, which has
// NO asmdef and therefore compiles into Unity's predefined Assembly-CSharp-Editor. Without this line
// those extensions could only reach the small public surface (CucliCommand, CucliArgs,
// CucliCommandResult, ...) and would be locked out of the helper layer that makes writing a command
// cheap: CucliDescribe's output bounding, CucliSerialized's Undo-correct writes, CucliObjectResolver's
// addressing, CucliPaging, CucliOptional's optional-package reflection, and above all CucliErrors'
// stable error codes. An extension that cannot use those is a second-class command, which defeats the
// point of growing project-specific commands at all.
//
// The grant is deliberately to the project's own Editor assembly rather than to a list of names: in a
// Unity project that assembly IS the project's code, and the whole extension area is that code.
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
