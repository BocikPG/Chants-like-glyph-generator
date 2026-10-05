using System.Diagnostics;
using System.Reflection;

namespace GlyphGen.Tests;

/// <summary>Marks a public, parameterless instance method as a test. A new instance of its class
/// is created for every test, so tests can't leak state into each other.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class TestFailedException : Exception
{
    public TestFailedException(string message) : base(message) { }
}

/// <summary>A deliberately tiny assertion set. If you later move to xUnit, the names line up:
/// replace [Test] with [Fact] and Assert.* with the xUnit equivalents.</summary>
public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new TestFailedException(message ?? "Expected true but was false.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new TestFailedException(message ?? "Expected false but was true.");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailedException($"{message ?? "Values differ."} Expected: {expected}  Actual: {actual}");
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new TestFailedException($"{message ?? "Values should differ."} Both were: {actual}");
    }

    public static void Near(double expected, double actual, double tolerance, string? message = null)
    {
        if (!(Math.Abs(expected - actual) <= tolerance))
            throw new TestFailedException($"{message ?? "Numbers differ."} Expected: {expected} +/- {tolerance}  Actual: {actual}");
    }

    public static void Contains(string expectedSubstring, string? actual, string? message = null)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new TestFailedException($"{message ?? "Text does not contain the expected part."}\n  Expected to contain: {expectedSubstring}\n  Actual: {actual}");
    }

    public static void DoesNotContain(string unexpectedSubstring, string? actual, string? message = null)
    {
        if (actual is not null && actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
            throw new TestFailedException($"{message ?? "Text contains something it should not."}\n  Unexpected: {unexpectedSubstring}\n  Actual: {actual}");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new TestFailedException($"{message ?? "Sequences differ."}\n  Expected: [{string.Join(", ", e)}]\n  Actual:   [{string.Join(", ", a)}]");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex)
        {
            throw new TestFailedException($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
        }

        throw new TestFailedException($"Expected {typeof(T).Name} but nothing was thrown.");
    }

    public static void Fail(string message) => throw new TestFailedException(message);
}

/// <summary>Lets a test attach a note to its result line (e.g. "snapshot recorded").</summary>
public static class Note
{
    internal static readonly List<string> Lines = new();
    public static void Add(string text) => Lines.Add(text);
}

public static class TestRunner
{
    public static int Main(string[] args)
    {
        bool listOnly = args.Contains("--list");
        var filters = args.Where(a => !a.StartsWith("--")).ToArray();

        var tests = Discover()
            .Where(t => filters.Length == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (listOnly)
        {
            foreach (var t in tests) Console.WriteLine(t.Name);
            return 0;
        }

        int passed = 0, failed = 0;
        var failures = new List<string>();
        var total = Stopwatch.StartNew();

        foreach (var test in tests)
        {
            Note.Lines.Clear();
            var timer = Stopwatch.StartNew();
            try
            {
                var instance = Activator.CreateInstance(test.Method.DeclaringType!)!;
                test.Method.Invoke(instance, null);
                passed++;
                Console.WriteLine($"  PASS  {test.Name} ({timer.ElapsedMilliseconds} ms){NoteSuffix()}");
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                failed++;
                string detail = cause is TestFailedException ? cause.Message : $"{cause.GetType().Name}: {cause.Message}";
                Console.WriteLine($"  FAIL  {test.Name} ({timer.ElapsedMilliseconds} ms)\n        {detail.Replace("\n", "\n        ")}");
                failures.Add(test.Name);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed, {tests.Count} total ({total.Elapsed.TotalSeconds:0.0} s)");
        if (failed > 0) Console.WriteLine("Failed: " + string.Join(", ", failures));
        return failed == 0 ? 0 : 1;
    }

    private static string NoteSuffix() => Note.Lines.Count == 0 ? "" : "  [" + string.Join("; ", Note.Lines) + "]";

    private sealed record Discovered(string Name, MethodInfo Method);

    private static IEnumerable<Discovered> Discover() =>
        typeof(TestRunner).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<TestAttribute>() is not null && m.GetParameters().Length == 0)
                .Select(m => new Discovered($"{t.Name}.{m.Name}", m)))
            .OrderBy(d => d.Name, StringComparer.Ordinal);
}
