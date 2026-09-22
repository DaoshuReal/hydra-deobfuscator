using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace HydraDeobfuscator.Logging;

internal static class HydraLogger
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Dim = "\u001b[2m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Red = "\u001b[31m";
    private const string Magenta = "\u001b[35m";
    private const string Gray = "\u001b[90m";
    private const string White = "\u001b[97m";
    private const string Blue = "\u001b[34m";

    private static readonly object Sync = new();
    private static int _phaseIndex;
    private static DateTime _startTime;
    private static bool _colorsEnabled = true;
    private static bool _consoleInitialized;

    public static bool Verbose { get; set; }

    public static void Banner(string version)
    {
        lock (Sync)
        {
            EnsureConsole();

            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            _startTime = DateTime.UtcNow;
            _phaseIndex = 0;

            WriteLine("");
            WriteLine($"{Cyan}{Bold}  ╔══════════════════════════════════════════════╗{Reset}");
            WriteLine($"{Cyan}{Bold}  ║{Reset}  {Green}{Bold}H Y D R A   D E O B F U S C A T O R{Reset}         {Cyan}{Bold}║{Reset}");
            WriteLine($"{Cyan}{Bold}  ║{Reset}  {Dim}Clean  ·  Modular  ·  Hydra → readable IL{Reset}   {Cyan}{Bold}║{Reset}");
            WriteLine($"{Cyan}{Bold}  ╚══════════════════════════════════════════════╝{Reset}  {Gray}v{version}{Reset}");
            WriteLine("");
        }
    }

    public static void Phase(string title)
    {
        lock (Sync)
        {
            _phaseIndex++;

            string tag = _phaseIndex.ToString("D2");

            WriteLine("");
            WriteLine($"{Magenta}{Bold}  ━━ [{tag}] {title.ToUpperInvariant()}{Reset} {Dim}────────────────────────────────{Reset}");
        }
    }

    public static void Step(string message)
    {
        lock (Sync)
        {
            WriteLine($"  {Cyan}➜{Reset} {White}{message}{Reset}");
        }
    }

    public static void Success(string message)
    {
        lock (Sync)
        {
            WriteLine($"  {Green}✔{Reset} {Green}{message}{Reset}");
        }
    }

    public static void Info(string message)
    {
        lock (Sync)
        {
            WriteLine($"  {Blue}●{Reset} {message}");
        }
    }

    public static void Detail(string message)
    {
        lock (Sync)
        {
            WriteLine($"    {Gray}│ {message}{Reset}");
        }
    }

    public static void Sample(string message)
    {
        lock (Sync)
        {
            WriteLine($"    {Gray}├─ {message}{Reset}");
        }
    }

    public static void Warn(string message)
    {
        lock (Sync)
        {
            WriteLine($"  {Yellow}⚠{Reset} {Yellow}{message}{Reset}");
        }
    }

    public static void Error(string message)
    {
        lock (Sync)
        {
            WriteLine($"  {Red}✖{Reset} {Red}{message}{Reset}");
        }
    }

    public static void VerboseDetail(string message)
    {
        if (!Verbose)
            return;

        Detail(message);
    }

    public static void Stats(params (string Label, string Value)[] items)
    {
        lock (Sync)
        {
            var parts = new List<string>();

            foreach (var item in items)
                parts.Add($"{Gray}{item.Label}={Reset}{Bold}{item.Value}{Reset}");

            WriteLine($"    {Gray}╰─{Reset} " + string.Join($"  {Gray}·{Reset}  ", parts));
        }
    }

    public static void Table(string[] headers, IEnumerable<string[]> rows, int maxRows = 12)
    {
        lock (Sync)
        {
            var rowList = new List<string[]>(rows);
            int cols = headers.Length;
            var widths = new int[cols];

            for (int c = 0; c < cols; c++)
                widths[c] = headers[c].Length;

            foreach (var row in rowList)
                for (int c = 0; c < cols && c < row.Length; c++)
                    widths[c] = Math.Max(widths[c], Math.Min(58, row[c]?.Length ?? 0));

            var sb = new StringBuilder();
            sb.Append("    ");
            sb.Append($"{Gray}┌─{Reset}");

            for (int c = 0; c < cols; c++)
            {
                sb.Append(new string('─', widths[c] + 2));

                if (c < cols - 1)
                    sb.Append('┬');
            }

            sb.Append($"{Gray}─┐{Reset}");
            WriteLine(sb.ToString());

            sb.Clear();
            sb.Append("    ");
            sb.Append($"{Gray}│{Reset} ");

            for (int c = 0; c < cols; c++)
            {
                sb.Append($"{Bold}{headers[c].PadRight(widths[c])}{Reset} ");
                sb.Append($"{Gray}│{Reset} ");
            }

            WriteLine(sb.ToString());

            int shown = 0;

            foreach (var row in rowList)
            {
                if (shown >= maxRows)
                    break;

                sb.Clear();
                sb.Append("    ");
                sb.Append($"{Gray}│{Reset} ");

                for (int c = 0; c < cols; c++)
                {
                    string cell = c < row.Length ? (row[c] ?? "") : "";

                    if (cell.Length > 58)
                        cell = cell.Substring(0, 55) + "...";

                    sb.Append($"{cell.PadRight(widths[c])} ");
                    sb.Append($"{Gray}│{Reset} ");
                }

                WriteLine(sb.ToString());
                shown++;
            }

            if (rowList.Count > shown)
                WriteLine($"    {Gray}│{Reset} {Dim}… +{rowList.Count - shown} more{Reset}");

            sb.Clear();
            sb.Append("    ");
            sb.Append($"{Gray}└─{Reset}");

            for (int c = 0; c < cols; c++)
            {
                sb.Append(new string('─', widths[c] + 2));

                if (c < cols - 1)
                    sb.Append('┴');
            }

            sb.Append($"{Gray}─┘{Reset}");
            WriteLine(sb.ToString());
        }
    }

    public static void Progress(string label, int done, int total)
    {
        lock (Sync)
        {
            int width = 26;
            double ratio = total <= 0 ? 0 : (double)done / total;
            int filled = (int)(ratio * width);
            string bar = new string('█', filled) + new string('░', Math.Max(0, width - filled));

            WriteLine($"    {Gray}{label}{Reset} {Cyan}{bar}{Reset} {Bold}{done}/{total}{Reset}");
        }
    }

    public static IDisposable TimedStep(string label)
    {
        Step(label);

        return new TimedScope(label);
    }

    public static void Footer(int types, int methods, string output)
    {
        lock (Sync)
        {
            var elapsed = DateTime.UtcNow - _startTime;

            WriteLine("");
            WriteLine($"{Green}{Bold}  ╔═ DONE ═══════════════════════════════════════╗{Reset}");
            WriteLine($"   {Gray}Types{Reset}   {Bold}{types}{Reset}   {Gray}·{Reset}   {Gray}Methods{Reset}   {Bold}{methods}{Reset}   {Gray}·{Reset}   {Gray}Elapsed{Reset}   {Bold}{elapsed.TotalSeconds:F1}s{Reset}");
            WriteLine($"   {Gray}Output{Reset}  {Cyan}{output}{Reset}");
            WriteLine($"{Green}{Bold}  ╚═════════════════════════════════════════════╝{Reset}");
            WriteLine("");
        }
    }

    private static void WriteLine(string text)
    {
        EnsureConsole();

        if (!_colorsEnabled)
            text = StripAnsi(text);

        try { Console.WriteLine(text); }
        catch { Console.WriteLine(StripAnsi(text)); }
    }

    private static void EnsureConsole()
    {
        if (_consoleInitialized)
            return;

        _consoleInitialized = true;

        try
        {
            if (Console.IsOutputRedirected)
            {
                _colorsEnabled = false;

                return;
            }

            if (Environment.GetEnvironmentVariable("NO_COLOR") != null)
            {
                _colorsEnabled = false;

                return;
            }

            if (OperatingSystem.IsWindows())
                _colorsEnabled = TryEnableWindowsAnsi();
        }
        catch
        {
            _colorsEnabled = false;
        }
    }

    private static bool TryEnableWindowsAnsi()
    {
        try
        {
            IntPtr handle = GetStdHandle(-11);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return false;

            if (!GetConsoleMode(handle, out uint mode))
                return false;

            const uint enabledFlag = 0x0004;

            if ((mode & enabledFlag) != 0)
                return true;

            if (!SetConsoleMode(handle, mode | enabledFlag))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static string StripAnsi(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inEscape = false;

        foreach (char ch in text)
        {
            if (ch == '\u001b')
            {
                inEscape = true;
                continue;
            }

            if (inEscape)
            {
                if (ch == 'm')
                    inEscape = false;

                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private sealed class TimedScope : IDisposable
    {
        private readonly string _label;
        private readonly DateTime _t0;
        private bool _done;

        public TimedScope(string label)
        {
            _label = label;
            _t0 = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_done)
                return;

            _done = true;

            var dt = DateTime.UtcNow - _t0;

            lock (Sync)
            {
                WriteLine($"    {Gray}╰─ {_label} finished in {dt.TotalSeconds:F2}s{Reset}");
            }
        }
    }
}
