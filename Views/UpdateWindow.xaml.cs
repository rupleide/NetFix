using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using NetFix.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace NetFix.Views;

public partial class UpdateWindow : Window
{
    private string _downloadUrl = "";
    private const double BarWidth = 364;

    public UpdateWindow()
    {
        InitializeComponent();
        ShowWarningState(); // Сразу показываем предупреждение
    }

    // Состояние 1: Предупреждение перед проверкой
    private void ShowWarningState()
    {
        StopIndeterminateAnimation();
        SetProgressBar(0, "#3b82f6");
        StatusText.Foreground = Brush("#888888");
        StatusText.Text = "⚠️  Перед проверкой убедитесь что:\n\n• VPN отключён\n• Zapret / tg-ws-proxy остановлены";
        PrimaryBtn.Content = "Проверить обновления";
        PrimaryBtn.Background = Brush("#3b82f6");
        PrimaryBtn.Visibility = Visibility.Visible;
        SecondaryBtn.Content = "Закрыть";
    }

    // Состояние 2: Идёт проверка
    private void ShowCheckingState()
    {
        PrimaryBtn.Visibility = Visibility.Collapsed;
        StatusText.Foreground = Brush("#888888");
        SetProgressBar(0, "#3b82f6");
        StartIndeterminateAnimation();
    }

    // Состояние 3: Ошибка подключения
    private void ShowErrorState()
    {
        StopIndeterminateAnimation();
        StatusText.Text = "❌  Не удалось подключиться к GitHub.\n\nПожалуйста, выключите VPN и Zapret, затем попробуйте снова.";
        StatusText.Foreground = Brush("#ef4444");
        SetProgressBar(BarWidth, "#ef4444");
        PrimaryBtn.Content = "Попробовать снова";
        PrimaryBtn.Background = Brush("#ef4444");
        PrimaryBtn.Visibility = Visibility.Visible;
        SecondaryBtn.Content = "Закрыть";
    }

    // Состояние 4а: Нет обновлений
    private void ShowUpToDateState()
    {
        StopIndeterminateAnimation();
        StatusText.Text = "✓  У вас установлена последняя версия";
        StatusText.Foreground = Brush("#22c55e");
        SetProgressBar(BarWidth, "#22c55e");
        PrimaryBtn.Visibility = Visibility.Collapsed;
        SecondaryBtn.Content = "Закрыть";
    }

    // Состояние 4б: Есть обновление
    private void ShowUpdateAvailableState(string newVersion, string downloadUrl)
    {
        StopIndeterminateAnimation();
        _downloadUrl = downloadUrl;
        StatusText.Text = $"🚀  Доступна новая версия — {newVersion}";
        StatusText.Foreground = Brush("#f0f0f0");
        SetProgressBar(BarWidth, "#3b82f6");
        PrimaryBtn.Content = "Установить";
        PrimaryBtn.Background = Brush("#3b82f6");
        PrimaryBtn.Visibility = Visibility.Visible;
        SecondaryBtn.Content = "Закрыть";
    }

    // Проверка с 3 попытками
    private async Task CheckWithRetriesAsync()
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            StatusText.Text = $"Подключение к GitHub... (попытка {attempt}/{maxAttempts})";

            var (hasUpdate, newVersion, downloadUrl, error) = await UpdateService.CheckAsync();

            // Если есть ошибка — показываем в окне
            if (!string.IsNullOrEmpty(error))
            {
                StatusText.Text = $"⚠️ {error}";
                // не возвращаемся — идём на следующую попытку
                if (attempt < maxAttempts)
                {
                    await Task.Delay(1500);
                    continue;
                }
                else
                {
                    // Последняя попытка провалилась
                    ShowErrorState();
                    return;
                }
            }

            // Успешный ответ от GitHub
            if (!string.IsNullOrEmpty(newVersion))
            {
                if (hasUpdate)
                    ShowUpdateAvailableState(newVersion, downloadUrl);
                else
                    ShowUpToDateState();
                return;
            }

            if (attempt < maxAttempts)
                await Task.Delay(1500);
        }

        // Все попытки провалились
        ShowErrorState();
    }

    // Кнопка основного действия
    private async void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        string content = PrimaryBtn.Content.ToString() ?? "";

        if (content == "Проверить обновления" || content == "Попробовать снова")
        {
            ShowCheckingState();
            await CheckWithRetriesAsync();
            return;
        }

        if (content == "Установить")
        {
            PrimaryBtn.IsEnabled = false;
            SecondaryBtn.Content = "Отмена";
            StartIndeterminateAnimation();
            StatusText.Text = "Скачивание обновления...";
            StatusText.Foreground = Brush("#888888");

            try
            {
                await UpdateService.DownloadAndInstallAsync(_downloadUrl,
                    progress => Dispatcher.Invoke(() =>
                    {
                        StopIndeterminateAnimation();
                        StatusText.Text = $"Скачивание... {progress}%";
                        SetProgressBar(BarWidth * progress / 100, "#3b82f6");
                    }));
            }
            catch
            {
                StopIndeterminateAnimation();
                StatusText.Text = "❌  Ошибка при скачивании. Попробуйте ещё раз.";
                StatusText.Foreground = Brush("#ef4444");
                SetProgressBar(BarWidth, "#ef4444");
                PrimaryBtn.IsEnabled = true;
                SecondaryBtn.Content = "Закрыть";
            }
        }
    }

    private void SecondaryBtn_Click(object sender, RoutedEventArgs e) => Close();

    // Хелперы
    private void StartIndeterminateAnimation()
    {
        IndeterminateBar.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation
        {
            From = -80, To = BarWidth,
            Duration = TimeSpan.FromSeconds(1.3),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        BarTranslate.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void StopIndeterminateAnimation()
    {
        BarTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        IndeterminateBar.Visibility = Visibility.Collapsed;
    }

    private void SetProgressBar(double width, string hex)
    {
        ProgressFill.Width = width;
        ProgressFill.Background = Brush(hex);
        ((DropShadowEffect)ProgressFill.Effect).Color = ToColor(hex);
    }

    private static SolidColorBrush Brush(string hex) =>
        new SolidColorBrush((Color)new ColorConverter().ConvertFrom(hex)!);

    private static Color ToColor(string hex) =>
        (Color)new ColorConverter().ConvertFrom(hex)!;
}
