// Practice content validator.
//
// For every exercise in wwwroot/Practice/manifest.json and every supported HDL
// language (a missing variant is a failure, never a skip):
//   1. solution  — the canonical solution passes its own tests in Kmila (the
//                  grader the editor uses);
//   2. oracle    — Kmila and the language's reference simulator (GHDL for VHDL),
//                  running the solution under the same stimulus (a generated
//                  testbench), agree on the graded outputs at the graded instants;
//                  failures must be listed in known-gaps.json (ratchet);
//   3. starter   — the starter does NOT pass unmodified (otherwise the student
//                  could finish the lesson without editing).
//
// Usage:  dotnet run --project PracticeValidator [--filter <substring>]

using Kmila.Frontend;
using Kmila.Shared.Models.Practice;
using Kmila.Shared.Services.Practice;
using Kmila.TestKit.Cases;
using Kmila.TestKit.Practice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kmila.Tools.PracticeValidator;

/// <summary>
/// Entry point of the Practice content validator: solution, external oracle and starter
/// checks for every exercise and supported language (see the file header).
/// </summary>
internal static class Program
{
    /// <summary>
    /// Program entry point. Resolves the practice content root, builds the catalog and
    /// evaluator, checks every exercise (optionally filtered by id substring) and prints
    /// one line per exercise and language plus a final tally.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments. Supports the optional flag <c>--filter &lt;substring&gt;</c>
    /// to limit the run to exercise ids containing the substring (case-insensitive).
    /// </param>
    /// <returns>
    /// <c>0</c> when every solution passes, every starter fails unmodified and the oracle
    /// agrees with known-gaps.json; <c>1</c> otherwise; <c>2</c> if the practice content
    /// root could not be located on disk.
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

        // The catalog reads "Practice/<path>" through a stream delegate; we are rooted at Practice/.
        var catalog = new PracticeCatalog(
            openFileAsync: async path =>
            {
                var rel = path.StartsWith("Practice/", StringComparison.Ordinal) ? path["Practice/".Length..] : path;
                var full = Path.Combine(practiceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) throw new FileNotFoundException($"Practice asset not found: {path}");
                return File.OpenRead(full);
            },
            logger: NullLogger<PracticeCatalog>.Instance);
        var evaluator = new PracticeEvaluator(NullLogger<PracticeEvaluator>.Instance);

        var meta = await catalog.ListAsync();
        var filter = args.SkipWhile(a => a != "--filter").Skip(1).FirstOrDefault();
        if (filter != null) meta = meta.Where(m => m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"[Validator] {meta.Count} exercises (filter='{filter ?? "(none)"}') × {HdlLanguages.Supported.Count} language(s).");
        Console.WriteLine();

        var failures = new List<string>();
        var oracleRuns = new List<(string Case, string Variant, bool Pass)>();
        int solutions = 0, oracles = 0, starters = 0, idx = 0;
        foreach (var m in meta)
        {
            idx++;
            var ex = await catalog.GetAsync(m.Id);
            if (ex == null)
            {
                Console.WriteLine($"[{idx,3}/{meta.Count,3}] {m.Id,-32} LOAD-FAIL");
                failures.Add($"{m.Id}: catalog couldn't build the exercise");
                continue;
            }
            foreach (var language in HdlLanguages.Supported)
            {
                Console.Write($"[{idx,3}/{meta.Count,3}] {m.Id,-32} [{language.Id}] ");
                if (ex.Variants.For(language) is not { } variant)
                {
                    Console.WriteLine("MISSING-VARIANT");
                    failures.Add($"{m.Id} [{language.Id}]: no {language.DisplayName} variant");
                    continue;
                }
                var solution = await GradeAsync(evaluator, ex, language, variant.Solution);
                if (solution.Kind == PracticeVerdictKind.Solved) solutions++;
                else failures.Add($"{m.Id} [{language.Id}] solution: {Describe(solution)}");

                var oracle = await PracticeOracle.CheckAsync(ex, language);
                bool agree = oracle.All(o => o.Pass);
                if (agree) oracles++;
                oracleRuns.Add((m.Id, language.Id, agree));

                var starter = await GradeAsync(evaluator, ex, language, variant.Starter);
                bool starterFails = starter.Kind != PracticeVerdictKind.Solved;
                if (starterFails) starters++;
                else failures.Add($"{m.Id} [{language.Id}] starter: passes without modification");

                Console.WriteLine($"solution={(solution.Kind == PracticeVerdictKind.Solved ? "ok" : solution.Kind.ToString())}  " +
                    $"oracle={(agree ? "ok" : "DIFF")}  starter={(starterFails ? "fails-as-intended" : "PASSES")}");
                foreach (var o in oracle.Where(o => !o.Pass))
                    Console.WriteLine($"      └─ {o.TestId}: {o.Error ?? string.Join("; ", o.Diff!.Where(d => !d.Equal).Select(d => d.Describe("reference", "kmila")))}");
            }
        }

        // The oracle is ratcheted: a disagreement must be a listed gap, and a listed gap that agrees is stale.
        var (violations, _) = KnownGaps.Apply(KnownGaps.Load(KnownGapsPath()), oracleRuns);
        failures.AddRange(violations);

        int total = oracleRuns.Count;
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Variants checked:            {total}");
        Console.WriteLine($"  ✓ Solutions pass:            {solutions}/{total}");
        Console.WriteLine($"  ✓ Agree with the reference:  {oracles}/{total}  (rest listed in known-gaps.json)");
        Console.WriteLine($"  ✓ Starters fail unmodified:  {starters}/{total}");
        Console.WriteLine($"  ✗ Problems:                  {failures.Count}");
        Console.WriteLine("════════════════════════════════════════════════════════════");
        foreach (var f in failures) Console.WriteLine($"    • {f}");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Grades <paramref name="code"/> with the editor's grader; a throw becomes a runtime-error verdict.</summary>
    private static async Task<PracticeAttemptVerdict> GradeAsync(PracticeEvaluator evaluator, PracticeExercise ex, HdlLanguage language, string code)
    {
        try { return await evaluator.GradeAsync(ex, language, code); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or IOException)
        {
            return new PracticeAttemptVerdict(PracticeVerdictKind.RuntimeError, ex.Id, DateTime.UtcNow, 0, 0, e.Message, null, []);
        }
    }

    private static string Describe(PracticeAttemptVerdict v)
    {
        var first = v.TestResults.FirstOrDefault(r => !r.Passed);
        return v.Kind switch
        {
            PracticeVerdictKind.BuildError => $"build-error: {v.BuildErrorMessage}",
            PracticeVerdictKind.WrongAnswer when first != null =>
                $"WA at {first.TestCaseId}: {first.FirstMismatchSignal}@{first.FirstMismatchTimeNs}ns actual={first.ActualAtMismatch} expected={first.ExpectedAtMismatch}",
            _ => $"{v.Kind}: {first?.ErrorClass ?? v.BuildErrorMessage}",
        };
    }

    /// <summary>The oracle's known gaps, next to this project.</summary>
    private static string KnownGapsPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PracticeValidator.csproj"))) return Path.Combine(dir.FullName, "known-gaps.json");
        return Path.Combine(Directory.GetCurrentDirectory(), "known-gaps.json");
    }

    /// <summary>The Practice content folder of the source checkout (see <c>SharedContentPaths</c>), or null.</summary>
    private static string? ResolvePracticeRoot() => Kmila.Shared.Services.SharedContentPaths.Find("Practice");
}
