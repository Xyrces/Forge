namespace Forge.Tools.E2E;

internal sealed record BenchmarkFixture(
    string Id,
    string Title,
    string Prompt,
    string ImplementationPath,
    string StarterSource,
    string FakeSolution,
    string KnownBadSolution,
    string GraderSource)
{
    public static BenchmarkFixture Get(string id) => id switch
    {
        "calculator" => Calculator,
        "normalize" => Normalize,
        "invoice" => Invoice,
        _ => throw new ArgumentException(
            $"Unknown benchmark case '{id}'. Expected calculator, normalize, or invoice."),
    };

    public void WriteScaffold(string clone)
    {
        File.WriteAllText(Path.Combine(clone, "Fixture.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(clone, "NuGet.Config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(clone, ".gitignore"), """
            bin/
            obj/
            """);
        File.WriteAllText(Path.Combine(clone, ImplementationPath), StarterSource);
        File.WriteAllText(Path.Combine(clone, "README.md"), $"# {Id} benchmark fixture\n\n{Prompt}\n");
    }

    public void WriteTrustedGrader(string graderRoot, string implementationRoot, string reportPath)
    {
        Directory.CreateDirectory(graderRoot);
        var fixtureProject = Path.Combine(implementationRoot, "Fixture.csproj");
        File.WriteAllText(Path.Combine(graderRoot, "Grader.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{EscapeXml(fixtureProject)}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(graderRoot, "NuGet.Config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(graderRoot, "Program.cs"),
            GraderPreamble(reportPath).Replace(GraderSourceMarker, GraderSource, StringComparison.Ordinal));
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string GraderPreamble(string reportPath) => $$"""
        using System.Text.Json;
        using BenchFixture;

        var checks = new List<Check>();
        void Check(string name, bool passed, string detail) => checks.Add(new(name, passed, detail));
        void Throws<T>(string name, Action action) where T : Exception
        {
            try { action(); Check(name, false, $"expected {typeof(T).Name}"); }
            catch (T) { Check(name, true, $"threw {typeof(T).Name}"); }
            catch (Exception ex) { Check(name, false, $"threw {ex.GetType().Name}"); }
        }

        {{GraderSourceMarker}}

        var report = new Report(checks);
        File.WriteAllText({{ToCSharpLiteral(reportPath)}}, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return checks.All(c => c.Passed) ? 0 : 1;

        internal sealed record Check(string Name, bool Passed, string Detail);
        internal sealed record Report(IReadOnlyList<Check> Checks);
        """;

    private const string GraderSourceMarker = "// fixture checks run here";

    private static string ToCSharpLiteral(string value) => "\"" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static readonly BenchmarkFixture Calculator = new(
        "calculator",
        "Implement checked integer division",
        """
        Implement Calculator.Divide(int dividend, int divisor). Return the C# integer quotient,
        including truncation toward zero for negative operands. Throw DivideByZeroException for a
        zero divisor and preserve C# checked overflow behavior for int.MinValue / -1. Change only
        Calculator.cs. Do not add packages or tests; acceptance is graded independently.
        """,
        "Calculator.cs",
        """
        namespace BenchFixture;

        public static class Calculator
        {
            public static int Divide(int dividend, int divisor) => throw new NotImplementedException();
        }
        """,
        """
        namespace BenchFixture;

        public static class Calculator
        {
            public static int Divide(int dividend, int divisor) => checked(dividend / divisor);
        }
        """,
        """
        namespace BenchFixture;

        public static class Calculator
        {
            public static int Divide(int dividend, int divisor) =>
                (int)Math.Floor((double)dividend / divisor);
        }
        """,
        """
        Check("positive quotient", Calculator.Divide(17, 5) == 3, "17 / 5 should truncate to 3");
        Check("negative truncation", Calculator.Divide(-17, 5) == -3, "-17 / 5 should truncate toward zero");
        Throws<DivideByZeroException>("zero divisor", () => Calculator.Divide(1, 0));
        Throws<OverflowException>("overflow edge", () => Calculator.Divide(int.MinValue, -1));
        """);

    private static readonly BenchmarkFixture Normalize = new(
        "normalize",
        "Normalize user-entered text",
        """
        Implement TextNormalizer.Normalize(string? value). Null or whitespace-only input becomes
        the empty string. Otherwise trim the ends, collapse every run of Unicode whitespace to one
        ASCII space, and lowercase using invariant culture. Change only TextNormalizer.cs. Do not
        add packages or tests; acceptance is graded independently.
        """,
        "TextNormalizer.cs",
        """
        namespace BenchFixture;

        public static class TextNormalizer
        {
            public static string Normalize(string? value) => throw new NotImplementedException();
        }
        """,
        """
        using System.Text;

        namespace BenchFixture;

        public static class TextNormalizer
        {
            public static string Normalize(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return string.Empty;
                var result = new StringBuilder();
                var pendingSpace = false;
                foreach (var rune in value.EnumerateRunes())
                {
                    if (Rune.IsWhiteSpace(rune))
                    {
                        pendingSpace = result.Length > 0;
                        continue;
                    }
                    if (pendingSpace) result.Append(' ');
                    pendingSpace = false;
                    result.Append(rune.ToString().ToLowerInvariant());
                }
                return result.ToString();
            }
        }
        """,
        """
        namespace BenchFixture;

        public static class TextNormalizer
        {
            public static string Normalize(string? value) =>
                value?.Trim().ToLowerInvariant() ?? string.Empty;
        }
        """,
        """
        Check("trim and lowercase", TextNormalizer.Normalize("  Hello WORLD  ") == "hello world", "outer spaces removed");
        Check("mixed whitespace", TextNormalizer.Normalize("A\t\nB\u00A0C") == "a b c", "Unicode whitespace collapsed");
        Check("null", TextNormalizer.Normalize(null) == "", "null becomes empty");
        Check("whitespace only", TextNormalizer.Normalize(" \r\n\t") == "", "whitespace becomes empty");
        var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Check("invariant casing", TextNormalizer.Normalize("I") == "i", "result is independent of current culture");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }
        """);

    private static readonly BenchmarkFixture Invoice = new(
        "invoice",
        "Calculate invoice totals safely",
        """
        Implement InvoiceCalculator.TotalCents(IEnumerable<InvoiceLine>? lines, int discountPercent).
        Reject null lines, discount outside 0..100, and negative quantity or unit price with
        ArgumentException (ArgumentNullException is valid for null). Sum quantity * unit price using
        checked integer arithmetic, then apply the integer percentage discount after summing. Use
        a wide intermediate for the percentage so a valid int total at 0% discount remains valid.
        Change only InvoiceCalculator.cs. Do not add packages or tests; acceptance is graded independently.
        """,
        "InvoiceCalculator.cs",
        """
        namespace BenchFixture;

        public sealed record InvoiceLine(int Quantity, int UnitPriceCents);

        public static class InvoiceCalculator
        {
            public static int TotalCents(IEnumerable<InvoiceLine>? lines, int discountPercent) =>
                throw new NotImplementedException();
        }
        """,
        """
        namespace BenchFixture;

        public sealed record InvoiceLine(int Quantity, int UnitPriceCents);

        public static class InvoiceCalculator
        {
            public static int TotalCents(IEnumerable<InvoiceLine>? lines, int discountPercent)
            {
                ArgumentNullException.ThrowIfNull(lines);
                if (discountPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(discountPercent));
                var total = 0;
                checked
                {
                    foreach (var line in lines)
                    {
                        if (line.Quantity < 0 || line.UnitPriceCents < 0) throw new ArgumentException("Invoice values cannot be negative.");
                        total += line.Quantity * line.UnitPriceCents;
                    }
                    return checked((int)((long)total * (100 - discountPercent) / 100));
                }
            }
        }
        """,
        """
        namespace BenchFixture;

        public sealed record InvoiceLine(int Quantity, int UnitPriceCents);

        public static class InvoiceCalculator
        {
            public static int TotalCents(IEnumerable<InvoiceLine>? lines, int discountPercent)
            {
                ArgumentNullException.ThrowIfNull(lines);
                if (discountPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(discountPercent));
                var total = 0;
                checked
                {
                    foreach (var line in lines)
                    {
                        if (line.Quantity < 0 || line.UnitPriceCents < 0) throw new ArgumentException("negative");
                        total += checked((int)((long)line.Quantity * line.UnitPriceCents * (100 - discountPercent) / 100));
                    }
                }
                return total;
            }
        }
        """,
        """
        var ordinary = new[] { new InvoiceLine(2, 125), new InvoiceLine(1, 50) };
        Check("ordinary discounted total", InvoiceCalculator.TotalCents(ordinary, 10) == 270, "300 cents less 10%");
        Check("empty invoice", InvoiceCalculator.TotalCents([], 25) == 0, "empty invoice is zero");
        Check("discount after sum", InvoiceCalculator.TotalCents([new(1, 1), new(1, 1)], 50) == 1, "discount is applied once after summing");
        Check("full discount", InvoiceCalculator.TotalCents(ordinary, 100) == 0, "100 percent discount is zero");
        Check("max total without discount", InvoiceCalculator.TotalCents([new(1, int.MaxValue)], 0) == int.MaxValue, "wide percentage intermediate avoids false overflow");
        Throws<ArgumentNullException>("null lines", () => InvoiceCalculator.TotalCents(null, 0));
        Throws<ArgumentException>("invalid high discount", () => InvoiceCalculator.TotalCents([], 101));
        Throws<ArgumentException>("invalid negative discount", () => InvoiceCalculator.TotalCents([], -1));
        Throws<ArgumentException>("negative quantity", () => InvoiceCalculator.TotalCents([new(-1, 10)], 0));
        Throws<ArgumentException>("negative price", () => InvoiceCalculator.TotalCents([new(1, -10)], 0));
        Throws<OverflowException>("overflow edge", () => InvoiceCalculator.TotalCents([new(int.MaxValue, 2)], 0));
        """);
}
