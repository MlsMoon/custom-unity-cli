using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    internal static partial class CucliJobProcessor
    {
        private static CucliJobRequest TryReadRequest(string path)
        {
            try
            {
                var request = CucliJson.Deserialize<CucliJobRequest>(
                    File.ReadAllText(path, CucliJson.Utf8NoBom));
                if (request == null) return null;
                if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Command))
                {
                    return null;
                }

                if (!Guid.TryParseExact(request.Id, "N", out _)) return null;
                if (!string.Equals(
                        request.Id,
                        Path.GetFileNameWithoutExtension(path),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return request;
            }
            catch (IOException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static CucliJobRequest FallbackRequest(string path)
        {
            return new CucliJobRequest
            {
                Id = Path.GetFileNameWithoutExtension(path),
                Command = "unknown",
                CreatedAtUtc = DateTime.UtcNow.ToString("O"),
                PayloadJson = "{}"
            };
        }

        /// <summary>
        /// Resolves a request's sub-action, preferring the denormalised field so queue routing never
        /// has to parse the payload twice.
        /// </summary>
        internal static string ActionOf(CucliJobRequest request)
        {
            if (request == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(request.Action)) return request.Action.Trim();

            JToken action = CucliJson.TryParseObject(request.PayloadJson)["action"];
            if (action == null || action.Type == JTokenType.Null) return string.Empty;
            return action.Type == JTokenType.String
                ? (action.Value<string>() ?? string.Empty).Trim()
                : action.ToString(Formatting.None);
        }

        private static bool IsControlCommand(CucliJobRequest request)
        {
            string action = ActionOf(request);
            if (string.IsNullOrWhiteSpace(action)) return false;
            return ControlCommands.Contains((request.Command ?? string.Empty) + "/" + action);
        }

        private static void WriteTerminal(
            string sourcePath,
            string targetDirectory,
            CucliJobRequest request,
            bool success,
            string errorCode,
            string message,
            object data,
            long explicitDurationMs,
            string[] warnings = null,
            string[] artifacts = null)
        {
            string state = success
                ? "succeeded"
                : string.Equals(targetDirectory, CucliPaths.Cancelled, StringComparison.OrdinalIgnoreCase)
                    ? "cancelled"
                    : "failed";

            var result = new CucliJobResult
            {
                Id = request.Id,
                Command = request.Command,
                State = state,
                Success = success,
                ErrorCode = errorCode,
                Message = message,
                Data = data == null ? null : CucliJson.ToToken(data),
                Warnings = warnings != null && warnings.Length > 0 ? warnings : null,
                Artifacts = artifacts != null && artifacts.Length > 0 ? artifacts : null,
                CreatedAtUtc = request.CreatedAtUtc,
                CompletedAtUtc = DateTime.UtcNow.ToString("O"),
                DurationMs = explicitDurationMs > 0
                    ? explicitDurationMs
                    : ElapsedSince(request.CreatedAtUtc)
            };

            string targetPath = Path.Combine(targetDirectory, request.Id + ".json");
            try
            {
                CucliPaths.WriteAtomic(targetPath, CucliJson.Serialize(result));
            }
            catch (IOException)
            {
                // The CLI keeps polling; a transient lock is recoverable on the next attempt.
                return;
            }

            TryDelete(sourcePath);
            TryDelete(CucliPaths.PendingResultPath(request.Id));
            s_completed++;
        }

        /// <summary>
        /// Stores the genuine result of a reload-capable job next to its waiting marker so the
        /// reloaded worker (or the next idle tick) can publish it verbatim.
        /// </summary>
        private static void WritePendingResult(
            string executionPath,
            CucliJobRequest request,
            CucliCommandResult result,
            string[] warnings,
            string[] artifacts)
        {
            var pending = new CucliJobResult
            {
                Id = request.Id,
                Command = request.Command,
                State = "succeeded",
                Success = true,
                Message = result.Message,
                Data = result.Data == null ? null : CucliJson.ToToken(result.Data),
                Warnings = warnings,
                Artifacts = artifacts,
                CreatedAtUtc = request.CreatedAtUtc,
                CompletedAtUtc = DateTime.UtcNow.ToString("O"),
                DurationMs = ElapsedSince(request.CreatedAtUtc)
            };

            try
            {
                CucliPaths.WriteAtomic(
                    CucliPaths.PendingResultPath(request.Id),
                    CucliJson.Serialize(pending));
            }
            catch (IOException)
            {
                // If the sidecar cannot be written the waiting job still resolves synthetically.
            }
        }

        private static CucliJobResult TryReadPendingResult(string jobId)
        {
            string path = CucliPaths.PendingResultPath(jobId);
            if (!File.Exists(path)) return null;
            try
            {
                return CucliJson.Deserialize<CucliJobResult>(File.ReadAllText(path, CucliJson.Utf8NoBom));
            }
            catch (IOException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static long ElapsedSince(string createdAtUtc)
        {
            DateTime created;
            if (!DateTime.TryParse(
                    createdAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out created))
            {
                return 0L;
            }

            double milliseconds = (DateTime.UtcNow - created).TotalMilliseconds;
            return milliseconds < 0 ? 0L : (long)milliseconds;
        }

        private static bool IsExpired(CucliJobRequest request)
        {
            if (request.TimeoutSeconds <= 0) return false;
            DateTime created;
            if (!DateTime.TryParse(
                    request.CreatedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out created))
            {
                return false;
            }

            return DateTime.UtcNow - created > TimeSpan.FromSeconds(request.TimeoutSeconds);
        }

        private static string FindPendingJob(string id)
        {
            foreach (string directory in new[]
            {
                CucliPaths.Control, CucliPaths.Queued, CucliPaths.Running, CucliPaths.Waiting
            })
            {
                string path = Path.Combine(directory, id + ".json");
                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static string OldestFile(string directory)
        {
            try
            {
                return new DirectoryInfo(directory)
                    .GetFiles("*.json")
                    .OrderBy(file => file.CreationTimeUtc)
                    .Select(file => file.FullName)
                    .FirstOrDefault();
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// Claims a job by moving it between state folders. A lost race against another worker tick
        /// is not an error - the next tick simply picks a different job.
        /// </summary>
        private static bool TryClaim(string from, string to)
        {
            try
            {
                File.Move(from, to);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // A later worker tick resolves the stale runtime file.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
