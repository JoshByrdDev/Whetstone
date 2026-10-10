using System.Text;

namespace Whetstone.Services;

/// <summary>
/// The text encoding every redirected child process is read and written in.
/// <para>
/// A redirected stream with no encoding set is not neutral: .NET decodes it using the console's
/// code page, which on a machine started from <c>Start-Whetstone.bat</c> is OEM 437. The agent
/// CLIs and git both emit UTF-8, so an em dash — three bytes, <c>E2 80 94</c> — arrives as the
/// three characters those bytes happen to name in 437, and gets stored that way. Nothing
/// downstream can recover it, because by then it is not damaged text, it is different text that
/// was written down correctly.
/// </para>
/// <para>
/// The bug is invisible until it isn't. Launch the dashboard from a UTF-8 terminal and every run
/// is clean; launch it from the .bat and every run that quotes a dash is not. That is why this
/// lives in one place and is spelled out at every call site rather than left to the default:
/// the default is a property of whoever double-clicked, which is no way to decide how to read a
/// file.
/// </para>
/// <para>
/// No BOM. The same encoding feeds process <i>stdin</i>, and <see cref="Encoding.UTF8"/> would
/// open the prompt with a byte-order mark the CLI has to guess its way past.
/// </para>
/// </summary>
public static class ProcessText
{
    /// <summary>UTF-8, no byte-order mark. Safe for stdin, stdout and stderr alike.</summary>
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
