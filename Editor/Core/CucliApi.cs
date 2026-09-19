using System;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Optional assembly-level marker for a project-local extension assembly.
    ///
    /// Implementing <see cref="ICucliCommand"/> in an assembly outside this package is already
    /// enough to be discovered, so this attribute is not required. It exists to give the extension a
    /// human-readable owner that shows up in <c>cucli meta extensions</c> and in the catalog, which
    /// matters once a project has more than one team contributing commands.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class CucliExtensionAttribute : Attribute
    {
        public CucliExtensionAttribute(string owner)
        {
            Owner = owner;
        }

        /// <summary>Free-form label, e.g. a team or tool name. Never used for dispatch.</summary>
        public string Owner { get; private set; }
    }

    /// <summary>
    /// The stable surface a project-local command may rely on.
    ///
    /// This package is designed to be extended from outside, which means the extension contract has
    /// to be versioned independently of the package version. <see cref="ExtensionApiVersion"/> only
    /// changes when something an extension can call is removed or changed incompatibly; adding a new
    /// action, helper or command group never bumps it, so extensions do not break on every upgrade.
    ///
    /// An extension that wants to be defensive can guard on it:
    ///
    /// <code>
    /// [assembly: CucliExtension("gameplay")]
    ///
    /// internal sealed class GameplayCommand : CucliCommand
    /// {
    ///     public override CucliCommandResult Execute(CucliCommandContext context)
    ///     {
    ///         if (!CucliApi.IsExtensionCompatible(1))
    ///         {
    ///             return CucliCommandResult.Fail(
    ///                 CucliErrors.CommandFailed,
    ///                 "本扩展针对 cucli 扩展 API v1 编写，当前包为 v" + CucliApi.ExtensionApiVersion + "。");
    ///         }
    ///
    ///         // ...
    ///     }
    /// }
    /// </code>
    /// </summary>
    public static class CucliApi
    {
        /// <summary>
        /// Version of the surface extensions may depend on. Bumped only on an incompatible change.
        /// </summary>
        public const int ExtensionApiVersion = 1;

        /// <summary>UPM package name, useful when an extension wants to report or detect the host.</summary>
        public const string PackageName = "com.igp.custom-unity-cli";

        /// <summary>Dotted command-name separator used by the CLI grammar.</summary>
        public const char GroupSeparator = '.';

        /// <summary>True when an extension written against <paramref name="extensionApiVersion"/> still works.</summary>
        public static bool IsExtensionCompatible(int extensionApiVersion)
        {
            return extensionApiVersion == ExtensionApiVersion;
        }

        /// <summary>
        /// Marker written into scaffolded local code so a future `cucli meta scaffold --upgrade`
        /// can recognise files it owns. Changing this string is a breaking change.
        /// </summary>
        public const string ScaffoldMarker = "cucli:scaffold";
    }
}
