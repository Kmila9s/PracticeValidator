// Practice content validator.
//
// Walks every exercise listed in wwwroot/Practice/manifest.json, asks the
// PracticeEvaluator to grade the canonical solution against its own test
// cases, and reports any exercise where the solution itself doesn't pass.
//
// A green run means: every solution variant parses, simulates, and produces
// outputs that match its own self-grader. A red run flags exercises where
// the simulator chokes (parse / runtime), the test-case inputs are wrong,
// or the canonical solution doesn't actually meet the spec — exactly the
// duds we don't want students hitting first.
//
// Usage:  dotnet run --project PracticeValidator
// Optional: pass --filter <substring> to limit the run to ids matching the
// substring, useful for re-running just the previously failing ones.
//
// --audit-starters  Grade each STARTER (instead of the solution) against
//                   its own tests. Any starter that passes is a bug: the
//                   student could finish the lesson without modifying the
//                   editor. A passing FromZero starter is especially wrong;
//                   FixBug/FillGap starters MUST fail at the sampled outputs.

using Kmila.Frontend;
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
                // "Practice/m1-from-zero/easy/01-half-adder/<variant file>".
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

        var auditStarters = args.Contains("--audit-starters");
        var mode = auditStarters ? "AUDIT-STARTERS" : "VALIDATE-SOLUTIONS";
        Console.WriteLine($"[Validator] Manifest has {meta.Count} exercises (filter='{filter ?? "(none)"}'). Mode: {mode}.");
        if (auditStarters)
        {
            return await RunStarterAuditAsync(catalog, evaluator, meta);
        }
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

            // Grade the canonical solution of every supported language against
            // its own tests. A missing variant is a failure, never a skip.
            foreach (var language in HdlLanguages.Supported)
                results.Add(await GradeSolutionAsync(evaluator, ex, language));
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
    /// Grades the <paramref name="language"/> solution of <paramref name="ex"/>
    /// against its own tests and prints the one-line outcome. A missing
    /// variant is reported as a failure.
    /// </summary>
    private static async Task<ValidationResult> GradeSolutionAsync(PracticeEvaluator evaluator, PracticeExercise ex, HdlLanguage language)
    {
        var id = $"{ex.Id} [{language.Id}]";
        if (ex.Variants.For(language) is not { } variant)
        {
            Console.WriteLine($"MISSING-VARIANT ({language.Id})");
            return new ValidationResult(id, "missing-variant", $"no {language.DisplayName} variant");
        }
        try
        {
            var verdict = await evaluator.GradeAsync(ex, language, variant.Solution);
            if (verdict.Kind == PracticeVerdictKind.Solved)
            {
                Console.WriteLine($"OK  ({verdict.TotalDurationMs} ms)");
                return new ValidationResult(id, "ok", null);
            }
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
            Console.WriteLine(verdict.Kind.ToString().ToUpperInvariant());
            Console.WriteLine($"      └─ {detail}");
            return new ValidationResult(id, verdict.Kind.ToString(), detail);
        }
        catch (Exception e)
        {
            Console.WriteLine($"THROW: {e.GetType().Name}: {e.Message}");
            return new ValidationResult(id, "throw", $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Audits every starter variant by running it through the same grader the
    /// editor uses. Three buckets in the output report:
    ///   • PASSING — starter passes without modification (the bug list).
    ///   • COMPILE-ERROR — starter doesn't compile / has no entity. Expected
    ///     for FromZero after the empty-starter migration.
    ///   • FAILS-AS-INTENDED — wrong-answer / runtime error at the sampled
    ///     outputs. Correct behavior for FixBug and FillGap starters.
    /// Exit code: 0 when PASSING is empty, 1 otherwise.
    /// </summary>
    private static async Task<int> RunStarterAuditAsync(
        PracticeCatalog catalog,
        PracticeEvaluator evaluator,
        IReadOnlyList<PracticeExerciseMeta> meta)
    {
        Console.WriteLine();
        var passing       = new List<AuditRow>();
        var compileErrors = new List<AuditRow>();
        var failsIntended = new List<AuditRow>();
        var loadFailures  = new List<AuditRow>();

        int idx = 0;
        foreach (var m in meta)
        {
            idx++;
            Console.Write($"[{idx,3}/{meta.Count,3}] {m.Id,-32} ({m.Modality}/{m.Difficulty})  ... ");

            var ex = await catalog.GetAsync(m.Id);
            if (ex == null)
            {
                Console.WriteLine("LOAD-FAIL");
                loadFailures.Add(new AuditRow(m.Id, m.Modality.ToString(), m.Difficulty.ToString(), "load-fail"));
                continue;
            }

            foreach (var language in HdlLanguages.Supported)
            {
                var row = $"{m.Id} [{language.Id}]";
                if (ex.Variants.For(language) is not { } variant)
                {
                    Console.WriteLine($"MISSING-VARIANT ({language.Id})");
                    loadFailures.Add(new AuditRow(row, m.Modality.ToString(), m.Difficulty.ToString(), "missing-variant"));
                    continue;
                }
                try
                {
                    var verdict = await evaluator.GradeAsync(ex, language, variant.Starter);
                    if (verdict.Kind == PracticeVerdictKind.Solved)
                    {
                        Console.WriteLine("PASSING  ← starter solves the lesson without edits");
                        passing.Add(new AuditRow(row, m.Modality.ToString(), m.Difficulty.ToString(), "passing"));
                    }
                    else if (verdict.Kind == PracticeVerdictKind.BuildError)
                    {
                        Console.WriteLine("compile-error (expected for empty FromZero)");
                        compileErrors.Add(new AuditRow(row, m.Modality.ToString(), m.Difficulty.ToString(), verdict.BuildErrorMessage ?? "build error"));
                    }
                    else
                    {
                        Console.WriteLine($"fails-as-intended ({verdict.Kind})");
                        failsIntended.Add(new AuditRow(row, m.Modality.ToString(), m.Difficulty.ToString(), verdict.Kind.ToString()));
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"THROW: {e.GetType().Name}: {e.Message}");
                    loadFailures.Add(new AuditRow(row, m.Modality.ToString(), m.Difficulty.ToString(), $"throw: {e.Message}"));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Total exercises:          {meta.Count}");
        Console.WriteLine($"  ✗ Passing without edits:  {passing.Count}  (these are the bugs)");
        Console.WriteLine($"  · Compile-error starters: {compileErrors.Count}  (FromZero expected here)");
        Console.WriteLine($"  ✓ Failing as intended:    {failsIntended.Count}  (FixBug/FillGap correct)");
        Console.WriteLine($"  ! Load/throw failures:    {loadFailures.Count}");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        if (passing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("PASSING WITHOUT MODIFICATION — fix these starters:");
            foreach (var row in passing.OrderBy(r => r.Modality).ThenBy(r => r.Id))
            {
                Console.WriteLine($"    • [{row.Modality,-9}] {row.Id,-32} ({row.Difficulty})");
            }
        }

        if (loadFailures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("LOAD / THROW FAILURES — investigate manually:");
            foreach (var row in loadFailures)
            {
                Console.WriteLine($"    • {row.Id,-32} {row.Detail}");
            }
        }

        // Also emit a Markdown report next to the working directory so the
        // human running the audit has a file to attach to the issue tracker.
        try
        {
            var report = BuildMarkdownAuditReport(meta.Count, passing, compileErrors, failsIntended, loadFailures);
            var outPath = Path.Combine(Directory.GetCurrentDirectory(), "audit-starters.md");
            await File.WriteAllTextAsync(outPath, report);
            Console.WriteLine();
            Console.WriteLine($"[Validator] Markdown report → {outPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Validator] Failed to write markdown report: {ex.Message}");
        }

        return passing.Count == 0 ? 0 : 1;
    }

    private static string BuildMarkdownAuditReport(
        int total,
        IReadOnlyList<AuditRow> passing,
        IReadOnlyList<AuditRow> compileErrors,
        IReadOnlyList<AuditRow> failsIntended,
        IReadOnlyList<AuditRow> loadFailures)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Practice starter audit");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"- Total exercises: **{total}**");
        sb.AppendLine($"- Passing without modification: **{passing.Count}** ← bugs");
        sb.AppendLine($"- Compile-error starters: **{compileErrors.Count}** (FromZero expected)");
        sb.AppendLine($"- Failing as intended: **{failsIntended.Count}** (FixBug/FillGap correct)");
        sb.AppendLine($"- Load/throw failures: **{loadFailures.Count}**");
        sb.AppendLine();
        sb.AppendLine("## Passing without modification");
        sb.AppendLine();
        if (passing.Count == 0)
        {
            sb.AppendLine("_None — every starter requires at least one edit to pass._");
        }
        else
        {
            sb.AppendLine("| Modality | Difficulty | Id |");
            sb.AppendLine("|---|---|---|");
            foreach (var row in passing.OrderBy(r => r.Modality).ThenBy(r => r.Id))
                sb.AppendLine($"| {row.Modality} | {row.Difficulty} | `{row.Id}` |");
        }
        sb.AppendLine();
        sb.AppendLine("## Compile-error starters");
        sb.AppendLine();
        if (compileErrors.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            sb.AppendLine("| Modality | Difficulty | Id | Build error |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var row in compileErrors.OrderBy(r => r.Modality).ThenBy(r => r.Id))
                sb.AppendLine($"| {row.Modality} | {row.Difficulty} | `{row.Id}` | {Truncate(row.Detail, 80)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Failing as intended");
        sb.AppendLine();
        if (failsIntended.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            sb.AppendLine("| Modality | Difficulty | Id | Verdict |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var row in failsIntended.OrderBy(r => r.Modality).ThenBy(r => r.Id))
                sb.AppendLine($"| {row.Modality} | {row.Difficulty} | `{row.Id}` | {row.Detail} |");
        }
        if (loadFailures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Load / throw failures");
            sb.AppendLine();
            sb.AppendLine("| Id | Detail |");
            sb.AppendLine("|---|---|");
            foreach (var row in loadFailures)
                sb.AppendLine($"| `{row.Id}` | {Truncate(row.Detail, 120)} |");
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    private record AuditRow(string Id, string Modality, string Difficulty, string Detail);

    /// <summary>The Practice content folder of the source checkout (see <c>SharedContentPaths</c>), or null.</summary>
    private static string? ResolvePracticeRoot() => Kmila.Shared.Services.SharedContentPaths.Find("Practice");

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
