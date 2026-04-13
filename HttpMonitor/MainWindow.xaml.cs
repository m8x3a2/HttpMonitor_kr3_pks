using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace HttpMonitor;

public partial class MainWindow : Window
{
    private readonly HttpServer _server = new();
    private readonly HttpClient _client = new();
    private readonly DispatcherTimer _uiTimer;
    private bool _chartByMinute = true;

    public MainWindow()
    {
        InitializeComponent();

        // Server events
        _server.OnLogAdded += msg =>
            Dispatcher.Invoke(() =>
            {
                ServerLogTextBox.AppendText(msg + "\n");
                ServerLogTextBox.ScrollToEnd();
            });

        _server.OnRequestHandled += _ =>
            Dispatcher.Invoke(() =>
            {
                UpdateStats();
                UpdateLogsDataGrid();
                DrawChart();
            });

        // UI refresh timer (uptime)
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateStats();
        _uiTimer.Start();
    }

    // ──────────────────────────────────────────────
    // SERVER
    // ──────────────────────────────────────────────

    private void StartServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Введите корректный порт (1–65535).", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _server.Start(port.ToString());
            StartServerButton.IsEnabled = false;
            StopServerButton.IsEnabled = true;
            PortTextBox.IsEnabled = false;
            ServerStatusText.Text = $"Статус: работает на порту {port}";
            ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка запуска сервера:\n{ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopServerButton_Click(object sender, RoutedEventArgs e)
    {
        _server.Stop();
        StartServerButton.IsEnabled = true;
        StopServerButton.IsEnabled = false;
        PortTextBox.IsEnabled = true;
        ServerStatusText.Text = "Статус: остановлен";
        ServerStatusText.Foreground = System.Windows.Media.Brushes.Red;
    }

    private void SaveLogsButton_Click(object sender, RoutedEventArgs e)
        => SaveLogsToFile();

    // ──────────────────────────────────────────────
    // CLIENT
    // ──────────────────────────────────────────────

    private void MethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RequestBodyTextBox == null) return;
        bool isPost = (MethodComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "POST";
        RequestBodyTextBox.IsEnabled = isPost;
    }

    private async void SendRequestButton_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        string method = (MethodComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";

        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("Введите URL.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ResponseTextBox.Text = "Отправка запроса...";
            HttpResponseMessage response;

            if (method == "GET")
            {
                response = await _client.GetAsync(url);
            }
            else
            {
                string body = RequestBodyTextBox.Text;
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                response = await _client.PostAsync(url, content);
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            ResponseTextBox.Text = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n\n{responseBody}";
        }
        catch (Exception ex)
        {
            ResponseTextBox.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async void QuickPostButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_server.IsRunning)
        {
            MessageBox.Show("Сервер не запущен.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string port = PortTextBox.Text.Trim();
        string url = $"http://localhost:{port}/";
        string body = RequestBodyTextBox.Text;

        try
        {
            ResponseTextBox.Text = "Отправка POST на сервер...";
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync(url, content);
            string responseBody = await response.Content.ReadAsStringAsync();
            ResponseTextBox.Text = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n\n{responseBody}";
        }
        catch (Exception ex)
        {
            ResponseTextBox.Text = $"Ошибка: {ex.Message}";
        }
    }

    // ──────────────────────────────────────────────
    // STATS & LOGS
    // ──────────────────────────────────────────────

    private void UpdateStats()
    {
        GetCountText.Text = _server.GetGetRequestsCount().ToString();
        PostCountText.Text = _server.GetPostRequestsCount().ToString();
        AvgTimeText.Text = _server.GetAverageProcessingTime().ToString("F1");
        UptimeText.Text = _server.GetUptime().ToString(@"hh\:mm\:ss");
    }

    private void UpdateLogsDataGrid()
    {
        // Добавьте эту проверку в самое начало метода
        if (FilterMethodCombo == null || FilterStatusCombo == null || LogsDataGrid == null)
            return;

        string? filterMethod = null;
        string? filterStatus = null;

        var methodItem = FilterMethodCombo.SelectedItem as ComboBoxItem;
        if (methodItem?.Content?.ToString() != "Все")
            filterMethod = methodItem?.Content?.ToString();

        var statusItem = FilterStatusCombo.SelectedItem as ComboBoxItem;
        if (statusItem?.Content?.ToString() != "Все")
            filterStatus = statusItem?.Content?.ToString();

        LogsDataGrid.ItemsSource = _server.GetFilteredLogs(filterMethod, filterStatus);
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
        => UpdateLogsDataGrid();

    private void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterMethodCombo.SelectedIndex = 0;
        FilterStatusCombo.SelectedIndex = 0;
        UpdateLogsDataGrid();
    }

    private void SaveLogsToFile()
    {
        var dialog = new SaveFileDialog
        {
            FileName = "logs.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt"
        };

        if (dialog.ShowDialog() == true)
        {
            var logs = _server.GetLogs();
            var sb = new StringBuilder();
            sb.AppendLine($"HTTP Monitor Logs — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('-', 80));
            foreach (var log in logs)
            {
                sb.AppendLine($"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] {log.Method} {log.Url} -> {log.StatusCode} ({log.ProcessingTimeMs}ms)");
                if (!string.IsNullOrEmpty(log.RequestBody))
                    sb.AppendLine($"  Body: {log.RequestBody}");
            }
            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"Логи сохранены в:\n{dialog.FileName}", "Сохранено",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ──────────────────────────────────────────────
    // CHART (OxyPlot)
    // ──────────────────────────────────────────────

    private void DrawChart()
    {
        if (RequestsPlotView == null) return;
        var model = new PlotModel { Title = _chartByMinute ? "Запросы по минутам" : "Запросы по часам" };

        var data = _chartByMinute
            ? _server.GetRequestsPerMinute()
            : _server.GetRequestsPerHour();

        var barSeries = new BarSeries
        {
            FillColor = OxyColors.SteelBlue,
            StrokeColor = OxyColors.SteelBlue,
            StrokeThickness = 1
        };

        var labels = new List<string>();

        foreach (var (time, count) in data.OrderBy(x => x.Key))
        {
            barSeries.Items.Add(new BarItem(count));
            labels.Add(_chartByMinute ? time.ToString("HH:mm") : time.ToString("HH:00"));
        }

        model.Series.Add(barSeries);

        model.Axes.Add(new CategoryAxis
        {
            Position = AxisPosition.Left,
            ItemsSource = labels
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Кол-во запросов",
            MinimumPadding = 0.1,
            MaximumPadding = 0.1
        });

        RequestsPlotView.Model = model;
    }

    private void ChartModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _chartByMinute = (ChartModeCombo.SelectedIndex == 0);
        DrawChart();
    }
}
