namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Stable machine-readable error codes. The CLI surfaces these verbatim in
    /// <c>errors[].code</c>; callers are expected to branch on them instead of parsing messages.
    /// </summary>
    public static class CucliErrors
    {
        // Host / bootstrap
        public const string Usage = "USAGE_ERROR";
        public const string ProjectNotFound = "PROJECT_NOT_FOUND";
        public const string WorkerUnavailable = "WORKER_UNAVAILABLE";
        public const string WorkerBusy = "WORKER_BUSY";
        public const string WorkerReloaded = "WORKER_RELOADED";
        public const string Timeout = "TIMEOUT";
        public const string Cancelled = "CANCELLED";
        public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";

        // Registry / dispatch
        public const string CommandNotFound = "EDITOR_COMMAND_NOT_FOUND";
        public const string CommandAmbiguous = "EDITOR_COMMAND_AMBIGUOUS";
        public const string CommandDuplicated = "EDITOR_COMMAND_DUPLICATE";
        public const string CommandFailed = "EDITOR_COMMAND_FAILED";
        public const string CommandException = "EDITOR_COMMAND_EXCEPTION";
        public const string MissingParameter = "MISSING_PARAMETER";
        public const string InvalidParameter = "INVALID_PARAMETER";
        public const string JobInvalid = "JOB_INVALID";
        public const string JobExpired = "JOB_EXPIRED";
        public const string ControlCommandNotAllowed = "CONTROL_COMMAND_NOT_ALLOWED";
        public const string UnsupportedAction = "UNSUPPORTED_ACTION";

        // Domain
        public const string TargetNotFound = "TARGET_NOT_FOUND";
        public const string TargetAmbiguous = "TARGET_AMBIGUOUS";
        public const string AssetNotFound = "ASSET_NOT_FOUND";
        public const string AssetExists = "ASSET_EXISTS";
        public const string MemberNotFound = "MEMBER_NOT_FOUND";
        public const string TypeNotFound = "TYPE_NOT_FOUND";
        public const string WrongMode = "WRONG_EDITOR_MODE";
        public const string CompilationFailed = "SCRIPT_COMPILATION_FAILED";
        public const string CompilationInProgress = "COMPILATION_IN_PROGRESS";
        public const string OptionalPackageMissing = "OPTIONAL_PACKAGE_MISSING";
        public const string CompileError = "CODE_COMPILE_ERROR";
    }
}
