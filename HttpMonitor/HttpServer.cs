using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace HttpMonitor;

public class HttpServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentBag<LogEntry> _logs = new();
    private readonly List<Message> _messages = new();
    private readonly object _messagesLock = new();
    private DateTime _startTime;

    // Events for UI updates
    public event Action<string>? OnLogAdded;
    public event Action<LogEntry>? OnRequestHandled;

    public bool IsRunning => _listener?.IsListening ?? false;

    public void Start(string port)
    {
        if (IsRunning) return;

        _startTime = DateTime.Now;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        Task.Run(() => Listen(_cts.Token));

        AddLog($"Сервер запущен на порту {port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        AddLog("Сервер остановлен.");
    }

    private async Task Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested && (_listener?.IsListening ?? false))
        {
            try
            {
                var context = await _listener.GetContextAsync();
                // Многопоточная обработка: каждый запрос в отдельном таске
                _ = Task.Run(() => ProcessRequest(context), token);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                AddLog($"Ошибка прослушивания: {ex.Message}");
            }
        }
    }

    private async Task ProcessRequest(HttpListenerContext context)
    {
        var sw = Stopwatch.StartNew();
        var request = context.Request;
        var response = context.Response;

        string method = request.HttpMethod;
        string url = request.Url?.ToString() ?? "";
        string? requestBody = null;
        string statusCode = "200";
        string responseBody = "";

        try
        {
            if (method == "GET")
            {
                var uptime = DateTime.Now - _startTime;
                var info = new
                {
                    status = "running",
                    uptime = uptime.ToString(@"hh\:mm\:ss"),
                    totalRequests = _logs.Count,
                    getRequests = GetGetRequestsCount(),
                    postRequests = GetPostRequestsCount(),
                    messagesStored = GetMessagesCount()
                };
                responseBody = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            }
            else if (method == "POST")
            {
                using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
                requestBody = await reader.ReadToEndAsync();

                try
                {
                    var doc = JsonDocument.Parse(requestBody);
                    string text = doc.RootElement.GetProperty("message").GetString() ?? "";
                    var msg = new Message { Text = text };

                    lock (_messagesLock)
                        _messages.Add(msg);

                    responseBody = JsonSerializer.Serialize(new { id = msg.Id, receivedAt = msg.ReceivedAt });
                }
                catch
                {
                    statusCode = "400";
                    responseBody = JsonSerializer.Serialize(new { error = "Invalid JSON or missing 'message' field" });
                }
            }
            else
            {
                statusCode = "405";
                responseBody = JsonSerializer.Serialize(new { error = "Method Not Allowed" });
            }
        }
        catch (Exception ex)
        {
            statusCode = "500";
            responseBody = JsonSerializer.Serialize(new { error = ex.Message });
        }
        finally
        {
            sw.Stop();
            response.StatusCode = int.Parse(statusCode);
            response.ContentType = "application/json; charset=utf-8";
            byte[] buffer = Encoding.UTF8.GetBytes(responseBody);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.OutputStream.Close();

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Method = method,
                Url = url,
                StatusCode = statusCode,
                ProcessingTimeMs = sw.ElapsedMilliseconds,
                RequestBody = requestBody
            };

            _logs.Add(entry);

            string logLine = $"[{entry.Timestamp:HH:mm:ss}] {method} {url} -> {statusCode} ({sw.ElapsedMilliseconds}ms)";
            if (requestBody != null)
                logLine += $"\n  Body: {requestBody}";

            AddLog(logLine);
            OnRequestHandled?.Invoke(entry);
        }
    }

    private void AddLog(string message)
    {
        OnLogAdded?.Invoke(message);
    }

    public List<LogEntry> GetLogs() => _logs.ToList();

    public List<LogEntry> GetFilteredLogs(string? method = null, string? statusCode = null)
    {
        var logs = _logs.AsEnumerable();
        if (!string.IsNullOrEmpty(method))
            logs = logs.Where(l => l.Method.Equals(method, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(statusCode))
            logs = logs.Where(l => l.StatusCode == statusCode);
        return logs.OrderByDescending(l => l.Timestamp).ToList();
    }

    public int GetGetRequestsCount() => _logs.Count(l => l.Method == "GET");
    public int GetPostRequestsCount() => _logs.Count(l => l.Method == "POST");
    public int GetMessagesCount() { lock (_messagesLock) return _messages.Count; }

    public double GetAverageProcessingTime()
    {
        var times = _logs.Select(l => l.ProcessingTimeMs).ToList();
        return times.Count == 0 ? 0 : times.Average();
    }

    public TimeSpan GetUptime() => IsRunning ? DateTime.Now - _startTime : TimeSpan.Zero;

    public Dictionary<DateTime, int> GetRequestsPerMinute()
    {
        return _logs
            .GroupBy(l => new DateTime(l.Timestamp.Year, l.Timestamp.Month, l.Timestamp.Day,
                                       l.Timestamp.Hour, l.Timestamp.Minute, 0))
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public Dictionary<DateTime, int> GetRequestsPerHour()
    {
        return _logs
            .GroupBy(l => new DateTime(l.Timestamp.Year, l.Timestamp.Month, l.Timestamp.Day,
                                       l.Timestamp.Hour, 0, 0))
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
