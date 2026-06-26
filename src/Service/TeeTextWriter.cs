using System.Text;

namespace TafClient.Service;

/// <summary>
/// Duplicates everything written to it to two underlying TextWriters — used
/// to make Console.WriteLine output go to both the real console AND a log
/// file, since Serilog's WriteTo.Console()/WriteTo.File() sinks only
/// capture what's written THROUGH Serilog's own logger (_log.LogInformation
/// etc.) — they do not intercept raw Console.WriteLine calls made elsewhere
/// in the process, which bypass Serilog entirely and go straight to the
/// real OS stdout.
///
/// This was confirmed as a real, significant gap: large parts of this
/// codebase's diagnostics (HostGameDialog, PlayTabWidget, GameLaunchService,
/// and more) use Console.WriteLine directly rather than the structured
/// logger, and none of that output was ever appearing in taf-client*.log —
/// only Serilog's own [INF]/[WRN]-prefixed lines were, which is exactly why
/// several "did X actually happen" questions this session were hard to
/// answer from the log file alone, even though the diagnostic line existed
/// in the source and was genuinely being printed — just never captured.
/// </summary>
public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override Encoding Encoding => _first.Encoding;

    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void Write(string? value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
    }

    public override void Flush()
    {
        _first.Flush();
        _second.Flush();
    }
}
