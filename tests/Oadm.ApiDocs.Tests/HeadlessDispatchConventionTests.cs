using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Oadm.Tests.Shared;

namespace Oadm.ApiDocs.Tests;

/// <summary>
/// Headless UI tests run their body through <c>HeadlessUnitTestSession.Dispatch</c>. It has overloads for
/// <c>Action</c>, <c>Func&lt;T&gt;</c> and <c>Func&lt;Task&lt;T&gt;&gt;</c>, none for a plain <c>Func&lt;Task&gt;</c>: an
/// async lambda without a value binds to <c>Func&lt;T&gt;</c> with <c>T = Task</c>, Dispatch returns when the lambda
/// reaches its first await, and no assertion in it reaches the test (a failure only faults the unawaited Task). The body
/// must return a value
/// (<c>return true;</c>) so the <c>Func&lt;Task&lt;T&gt;&gt;</c> overload awaits it to its end.
/// This check reads the test sources (Roslyn is here through Oadm.ApiDocs).
/// </summary>
public sealed class HeadlessDispatchConventionTests
{
    [Fact]
    public void Every_async_body_given_to_Dispatch_returns_a_value()
    {
        var offenders = new List<string>();
        foreach (var root in new[] { TestRepository.Path("tests"), TestRepository.Path("samples") })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // HeadlessDispatchTests shows the trap on purpose.
                if (IsBuildOutput(file) || Path.GetFileName(file) == "HeadlessDispatchTests.cs")
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (!text.Contains("Dispatch(", StringComparison.Ordinal))
                {
                    continue;
                }

                var tree = CSharpSyntaxTree.ParseText(text, path: file);
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (DispatchedLambda(call) is { } lambda && !AwaitedToItsEnd(lambda))
                    {
                        var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        offenders.Add($"{Path.GetRelativePath(TestRepository.Root, file)}:{line}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Dispatch with an async body that returns no value stops awaiting at the first await (asserts after it are " +
            "skipped). End the body with \"return true;\":" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("s.Dispatch(async () => { await X(); Assert.True(y); }, ct);", false)]
    [InlineData("s.Dispatch(async () => { await X(); return true; }, ct);", true)]
    [InlineData("s.Dispatch(async () => await X(), ct);", false)]
    [InlineData("s.Dispatch(() => RunAsync(), ct);", false)]
    [InlineData("s.Dispatch(() => { return RunAsync(); }, ct);", false)]
    [InlineData("s.Dispatch(() => { a(); return 1; }, ct);", true)]
    [InlineData("s.Dispatch(() => Task.FromResult(work()), ct);", true)]
    [InlineData("s.Dispatch(async () => { Func<Task> f = async () => { return; }; await f(); }, ct);", false)]
    [InlineData("s.Dispatch(async () => { int F() { return 1; } await X(); return F(); }, ct);", true)]
    public void The_check_tells_a_value_from_a_plain_task(string statement, bool ok)
    {
        var call = SyntaxFactory.ParseStatement(statement).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First(c => DispatchedLambda(c) is not null);

        Assert.Equal(ok, AwaitedToItsEnd(DispatchedLambda(call)!));
    }

    private static bool IsBuildOutput(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private static LambdaExpressionSyntax? DispatchedLambda(InvocationExpressionSyntax call)
    {
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };
        return name == "Dispatch" && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LambdaExpressionSyntax lambda
            ? lambda
            : null;
    }

    /// <summary>
    /// True when the lambda gives Dispatch a value (or a <c>Task&lt;T&gt;</c>) to await, false when it is a plain Task
    /// Dispatch would not await: an async lambda without a returned value or with an expression body, or a lambda that
    /// returns the result of an "...Async" call.
    /// </summary>
    private static bool AwaitedToItsEnd(LambdaExpressionSyntax lambda)
    {
        var isAsync = lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
        if (lambda.ExpressionBody is { } expression)
        {
            return !isAsync && !CallsAsyncMethod(expression);
        }

        var returns = OwnReturns(lambda.Block!).ToList();
        return isAsync
            ? returns.Any(r => r.Expression is not null)
            : !returns.Any(r => r.Expression is { } value && CallsAsyncMethod(value));
    }

    private static bool CallsAsyncMethod(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax { Expression: var callee } && callee switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal),
            IdentifierNameSyntax id => id.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal),
            _ => false,
        };

    /// <summary>The return statements of the body itself, not of nested lambdas or local functions.</summary>
    private static IEnumerable<ReturnStatementSyntax> OwnReturns(BlockSyntax body) =>
        body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<ReturnStatementSyntax>();
}
