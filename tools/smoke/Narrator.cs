namespace EAuction.Smoke;

/// <summary>
/// The smoke test's output is the point of it: someone who has not read the code
/// should be able to follow what the platform did and see where it stopped. Every
/// step prints what it asserted, not just that it passed.
/// </summary>
public sealed class Narrator
{
    private readonly List<string> _failures = [];
    private int _step;

    public bool Failed => _failures.Count > 0;

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} {new string('─', Math.Max(0, 68 - title.Length))}");
    }

    public void Step(string what, string detail = "")
    {
        _step++;
        Console.Write($"  {_step,2}. {what,-46}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("ok");
        Console.ResetColor();
        Console.WriteLine(detail.Length > 0 ? $"  {detail}" : "");
    }

    public void Fail(string what, string detail)
    {
        _step++;
        Console.Write($"  {_step,2}. {what,-46}");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("FAILED");
        Console.ResetColor();
        Console.WriteLine($"  {detail}");
        _failures.Add($"{what}: {detail}");
    }

    public void Note(string text) => Console.WriteLine($"      {text}");

    public int Summarise()
    {
        Console.WriteLine();
        if (_failures.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"All {_step} checks passed.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{_failures.Count} of {_step} checks failed:");
        foreach (var f in _failures) Console.WriteLine($"  - {f}");
        Console.ResetColor();
        return 1;
    }
}
