using UnityEditor;
using UnityEditor.Compilation;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Remembers whether the last compilation produced errors. Kept in <see cref="SessionState"/>
    /// so it survives the domain reload that the compilation itself triggers.
    /// </summary>
    [InitializeOnLoad]
    internal static class CucliCompilationStatus
    {
        private const string ErrorKey = "CustomUnityCli.Compilation.HasErrors";
        private const string MessageKey = "CustomUnityCli.Compilation.FirstError";

        static CucliCompilationStatus()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
        }

        internal static bool HasErrors => SessionState.GetBool(ErrorKey, false);

        internal static string FirstError => SessionState.GetString(MessageKey, string.Empty);

        private static void OnCompilationStarted(object context)
        {
            SessionState.SetBool(ErrorKey, false);
            SessionState.SetString(MessageKey, string.Empty);
        }

        private static void OnAssemblyFinished(string assembly, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;
            for (int index = 0; index < messages.Length; index++)
            {
                if (messages[index].type != CompilerMessageType.Error) continue;
                SessionState.SetBool(ErrorKey, true);
                if (string.IsNullOrEmpty(SessionState.GetString(MessageKey, string.Empty)))
                {
                    SessionState.SetString(
                        MessageKey,
                        messages[index].file + "(" + messages[index].line + "): " + messages[index].message);
                }

                return;
            }
        }
    }
}
