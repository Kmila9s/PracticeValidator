// Practice content validator.
//
// Walks every exercise listed in wwwroot/Practice/manifest.json, asks the
// PracticeEvaluator to grade the canonical solution against its own test
// cases, and reports any exercise where the solution itself doesn't pass.
//
// A green run means: every solution.vhdl parses, simulates, and produces
// outputs that match its own self-grader. A red run flags exercises where
// the simulator chokes (parse / runtime), the test-case inputs are wrong,
// or the canonical solution doesn't actually meet the spec — exactly the
// duds we don't want students hitting first.
//
// Usage:  dotnet run --project App/Kmila/Tools/PracticeValidator
// Optional: pass --filter <substring> to limit the run to ids matching the
// substring, useful for re-running just the previously failing ones.

using Kmila.Shared.Models.Practice;
using Kmila.Shared.Services.Practice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kmila.Tools.PracticeValidator;

/// <summary>
/// Entry point of the Practice content validator CLI tool.
/// Iterates over every exercise declared in <c>wwwroot/Practice/manifest.json</c> and uses
/// <see cref="PracticeEvaluator"/> to grade each canonical solution against its own
/// self-tests, returning a non-zero exit code if any exercise fails.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Program entry point. Resolves the practice content root, builds the catalog
    /// + evaluator, runs every exercise (optionally filtered by id substring), prints
    /// a per-exercise verdict and a final tally grouped by failure class.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments. Supports the optional flag <c>--filter &lt;substring&gt;</c>
    /// to limit the run to exercise ids containing the substring (case-insensitive).
    /// </param>
    /// <returns>
    /// <c>0</c> if every exercise was graded as <see cref="PracticeVerdictKind.Solved"/>,
    /// <c>1</c> if at least one exercise failed grading, or <c>2</c> if the practice
    /// content root could not be located on disk.
    /// </returns>
    private static async Task<int> Main(string[] args)
    {
        var practiceRoot = ResolvePracticeRoot();
        if (practiceRoot == null)
        {
            Console.Error.WriteLine("[ERROR] Could not locate wwwroot/Practice. Run from repo root or App/Kmila/.");
            return 2;
        }
        Console.WriteLine($"[Validator] Practice root: {practiceRoot}");

        // PracticeCatalog reads everything via a stream delegate keyed by
        // path-relative-to-Practice-root. We give it the obvious file-system
        // implementation; same paths the manifest declares.
        var catalog = new PracticeCatalog(
            openFileAsync: async path =>
            {
                // Catalog uses paths like "Practice/manifest.json" and
                // "Practice/m1-from-zero/easy/01-half-adder/solution.vhdl".
                // Strip the leading "Practice/" since we already rooted there.
                var rel = path.StartsWith("Practice/", StringComparison.Ordinal)
                    ? path["Practice/".Length..]
                    : path;
                var full = Path.Combine(practiceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                    throw new FileNotFoundException($"Practice asset not found: {path}");
                return File.OpenRead(full);
            },
            logger: NullLogger<PracticeCatalog>.Instance);

        var evaluator = new PracticeEvaluator(NullLogger<PracticeEvaluator>.Instance);

        var meta = await catalog.ListAsync();
        var filter = args.SkipWhile(a => a != "--filter").Skip(1).FirstOrDefault();
        if (filter != null)
        {
            meta = meta.Where(m => m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        Console.WriteLine($"[Validator] Manifest has {meta.Count} exercises (filter='{filter ?? "(none)"}').");
        Console.WriteLine();

        var results = new List<ValidationResult>();
        int idx = 0;
        foreach (var m in meta)
        {
            idx++;
            Console.Write($"[{idx,3}/{meta.Count,3}] {m.Id,-32} ({m.Modality}/{m.Difficulty})  ... ");

            var ex = await catalog.GetAsync(m.Id);
            if (ex == null)
            {
                Console.WriteLine("LOAD-FAIL");
                results.Add(new ValidationResult(m.Id, "load-fail", "Catalog couldn't build the exercise"));
                continue;
            }

            // Grade the canonical solution against its own tests. A correct
            // exercise should come back Solved on every test case.
            try
            {
                var verdict = await evaluator.GradeAsync(ex, ex.SolutionVhdl);
                if (verdict.Kind == PracticeVerdictKind.Solved)
                {
                    Console.WriteLine($"OK  ({verdict.TotalDurationMs} ms)");
                    results.Add(new ValidationResult(m.Id, "ok", null));
                }
                else
                {
                    var firstFail = verdict.TestResults.FirstOrDefault(r => !r.Passed);
                    var detail = verdict.Kind switch
                    {
                        PracticeVerdictKind.BuildError => $"build-error: {verdict.BuildErrorMessage}",
                        PracticeVerdictKind.RuntimeError => $"runtime: {(firstFail?.ErrorClass ?? "unknown")}",
                        PracticeVerdictKind.WrongAnswer => firstFail == null
                            ? "wrong-answer (no fail row?)"
                            : $"WA at {firstFail.TestCaseId}: {firstFail.FirstMismatchSignal}@{firstFail.FirstMismatchTimeNs}ns " +
                              $"actual={firstFail.ActualAtMismatch} expected={firstFail.ExpectedAtMismatch}",
                        _ => verdict.Kind.ToString()
                    };
                    Console.WriteLine($"{verdict.Kind.ToString().ToUpperInvariant()}");
                    Console.WriteLine($"      └─ {detail}");
                    results.Add(new ValidationResult(m.Id, verdict.Kind.ToString(), detail));
                }
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"THROW: {ex2.GetType().Name}: {ex2.Message}");
                results.Add(new ValidationResult(m.Id, "throw", $"{ex2.GetType().Name}: {ex2.Message}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        var ok    = results.Count(r => r.Status == "ok");
        var bad   = results.Count - ok;
        Console.WriteLine($"  Total:   {results.Count}");
        Console.WriteLine($"  ✓ OK:    {ok}");
        Console.WriteLine($"  ✗ Bad:   {bad}");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        if (bad > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures, grouped by class:");
            foreach (var grp in results.Where(r => r.Status != "ok").GroupBy(r => r.Status).OrderBy(g => g.Key))
            {
                Console.WriteLine($"  [{grp.Key}] — {grp.Count()} exercise(s):");
                foreach (var r in grp)
                {
                    Console.WriteLine($"    • {r.Id,-32} {r.Detail}");
                }
            }
        }

        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// Searches up the directory tree for <c>App/Kmila/Kmila.Shared/wwwroot/Practice</c>,
    /// so the tool works from any working directory (repo root, App/Kmila, or the
    /// validator's own folder).
    /// </summary>
    /// <returns>
    /// The absolute path to the resolved Practice content directory, or <c>null</c>
    /// if no candidate directory was found while walking up to the filesystem root.
    /// </returns>
    private static string? ResolvePracticeRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "App", "Kmila", "Kmila.Shared", "wwwroot", "Practice");
            if (Directory.Exists(candidate)) return candidate;
            // Also handle running from inside App/Kmila/Tools/PracticeValidator/
            candidate = Path.Combine(dir.FullName, "Kmila.Shared", "wwwroot", "Practice");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "..", "Kmila.Shared", "wwwroot", "Practice");
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            candidate = Path.Combine(dir.FullName, "..", "..", "Kmila.Shared", "wwwroot", "Practice");
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Aggregated outcome of grading a single practice exercise during a validation run.
    /// </summary>
    /// <param name="Id">Stable exercise identifier as declared in the practice manifest.</param>
    /// <param name="Status">
    /// Outcome bucket — <c>"ok"</c> for a fully solved exercise, otherwise one of
    /// <c>"load-fail"</c>, <c>"throw"</c>, or the lowercased
    /// <see cref="PracticeVerdictKind"/> name (e.g. <c>"BuildError"</c>, <c>"WrongAnswer"</c>).
    /// </param>
    /// <param name="Detail">Free-form human-readable detail about the failure, or <c>null</c> when <paramref name="Status"/> is <c>"ok"</c>.</param>
    private record ValidationResult(string Id, string Status, string? Detail);
}
