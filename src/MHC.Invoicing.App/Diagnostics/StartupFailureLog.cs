using System.Diagnostics;
using System.Globalization;
using System.Text;
using MHC.Invoicing.Infrastructure.Storage;

namespace MHC.Invoicing.App.Diagnostics;

internal static class StartupFailureLog
{
    private const int MaxRecordCharacters = 16 * 1024;

    internal static void TryWrite(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            AppDataPaths paths = AppDataPaths.CreateDefault();
            string directory = Path.Combine(paths.RootDirectory, "Logs");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "startup-failure.log");
            string temporary = destination + ".tmp";
            File.WriteAllText(temporary, Format(exception), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            // Startup diagnostics must never mask the original startup failure.
        }
    }

    private static string Format(Exception exception)
    {
        StringBuilder output = new();
        output.Append("UTC: ").AppendLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        output.Append("Version: ").AppendLine(typeof(StartupFailureLog).Assembly.GetName().Version?.ToString() ?? "unknown");
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            output.Append("Type: ").AppendLine(current.GetType().FullName ?? current.GetType().Name);
            output.Append("HRESULT: 0x").AppendLine(current.HResult.ToString("X8", CultureInfo.InvariantCulture));
            foreach (StackFrame frame in new StackTrace(current, fNeedFileInfo: false).GetFrames() ?? [])
            {
                var method = frame.GetMethod();
                if (method is not null)
                    output.Append("At: ").Append(method.DeclaringType?.FullName).Append('.').AppendLine(method.Name);
            }
        }
        string record = output.ToString();
        return record.Length <= MaxRecordCharacters ? record : record[..MaxRecordCharacters];
    }
}
