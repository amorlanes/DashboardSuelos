using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Constructora.Wpf.Services;

public sealed class ArchivoLoggerProvider(string path) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new ArchivoLogger(this, categoryName);
    public void Dispose() { }

    private void Escribir(string category, LogLevel level, string message, Exception? exception)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} [{level}] {category}: {message}{Environment.NewLine}";
        if (exception is not null) entry += exception + Environment.NewLine;
        Trace.Write(entry);
        try
        {
            lock (_gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, entry);
            }
        }
        catch (IOException exceptionWritingLog)
        {
            Trace.WriteLine($"No se pudo escribir el registro: {exceptionWritingLog.Message}");
        }
        catch (UnauthorizedAccessException exceptionWritingLog)
        {
            Trace.WriteLine($"No se pudo escribir el registro: {exceptionWritingLog.Message}");
        }
    }

    private sealed class ArchivoLogger(ArchivoLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Escribir(category, logLevel, formatter(state, exception), exception);
        }
    }
}
