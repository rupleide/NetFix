Посмотрел твой код — всё есть, можно работать! Но это большая задача, давай сделаю методичку чтобы в следующем чате нейронка сразу всё поняла и сделала.

---

# NetFix — Методичка: Визуальные улучшения ритм-игры

## Что нужно сделать

Максимально красивый визуал игрового экрана `GamePlayView` — эффекты, свечение, аура, частицы, полосы дорожек.

---

## 1. Полосы дорожек (Lane Lines)

В `RebuildGameCanvasBase()` добавить вертикальные полупрозрачные полосы для каждой из 4 дорожек:

```csharp
// Фон дорожки — вертикальная полоса
var laneBackground = new Border
{
    Width = 50,
    Height = canvasH,
    Background = new LinearGradientBrush(
        Color.FromArgb(15, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B),
        Color.FromArgb(5, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B),
        90)
};
Canvas.SetLeft(laneBackground, 10 + i * 60);
Canvas.SetTop(laneBackground, 0);
GameCanvas.Children.Add(laneBackground);

// Боковая линия дорожки (левый край)
var laneLine = new Rectangle
{
    Width = 1,
    Height = canvasH,
    Fill = new SolidColorBrush(Color.FromArgb(30, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B))
};
Canvas.SetLeft(laneLine, 10 + i * 60);
Canvas.SetTop(laneLine, 0);
GameCanvas.Children.Add(laneLine);
```

---

## 2. Зона удара (Hit Zone) — свечение снизу

Заменить простые Border в хит-зоне на красивые с эффектом:

```csharp
// Линия хит-зоны через всю ширину поля
var hitLine = new Rectangle
{
    Width = 240,
    Height = 2,
    Fill = new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromArgb(0, 255,255,255), 0),
        new GradientStop(Color.FromArgb(60, 255,255,255), 0.5),
        new GradientStop(Color.FromArgb(0, 255,255,255), 1),
    }, new Point(0,0), new Point(1,0))
};
Canvas.SetLeft(hitLine, 0);
Canvas.SetTop(hitLine, hitY + 25);
GameCanvas.Children.Add(hitLine);

// Кнопка с DropShadowEffect цвета дорожки
var hz = new Border { ... };
hz.Effect = new System.Windows.Media.Effects.DropShadowEffect
{
    Color = LaneColors[i],
    BlurRadius = 8,
    ShadowDepth = 0,
    Opacity = 0.4
};
```

---

## 3. Стрелки-ноты — свечение и тень

В GameTick при создании стрелки:

```csharp
var arrow = new Border { ... };
arrow.Effect = new System.Windows.Media.Effects.DropShadowEffect
{
    Color = LaneColors[note.Lane],
    BlurRadius = 14,
    ShadowDepth = 0,
    Opacity = 0.7
};
```

Добавить градиентный фон стрелки вместо просто цвета:

```csharp
Background = new LinearGradientBrush(
    Color.FromArgb(60, LaneColors[note.Lane].R, LaneColors[note.Lane].G, LaneColors[note.lane].B),
    Color.FromArgb(20, LaneColors[note.Lane].R, LaneColors[note.Lane].G, LaneColors[note.Lane].B),
    90)
```

---

## 4. Эффект попадания — вспышка + частицы

Создать метод `SpawnHitEffect(int lane, string judge)`:

```csharp
private void SpawnHitEffect(int lane, string judge)
{
    double canvasH = GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 500;
    double hitY = canvasH - 70;
    double laneX = 10 + lane * 60 + 25; // центр дорожки

    // 1. Вспышка на хит-зоне
    var flash = new Ellipse
    {
        Width = 60,
        Height = 60,
        Fill = new RadialGradientBrush(
            Color.FromArgb(180, LaneColors[lane].R, LaneColors[lane].G, LaneColors[lane].B),
            Color.FromArgb(0, LaneColors[lane].R, LaneColors[lane].G, LaneColors[lane].B)),
        IsHitTestVisible = false,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(0.3, 0.3)
    };
    Canvas.SetLeft(flash, laneX - 30);
    Canvas.SetTop(flash, hitY);
    GameCanvas.Children.Add(flash);

    // Анимация вспышки — масштаб и прозрачность
    var scaleX = new DoubleAnimation(0.3, 2.0, TimeSpan.FromMilliseconds(300));
    var scaleY = new DoubleAnimation(0.3, 2.0, TimeSpan.FromMilliseconds(300));
    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
    fadeOut.Completed += (_, _) => GameCanvas.Children.Remove(flash);
    ((ScaleTransform)flash.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
    ((ScaleTransform)flash.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    flash.BeginAnimation(UIElement.OpacityProperty, fadeOut);

    // 2. Частицы — 6 штук разлетаются в стороны
    var rng = new Random();
    for (int p = 0; p < 6; p++)
    {
        double angle = p * 60 + rng.Next(-20, 20);
        double rad = angle * Math.PI / 180;
        double dist = 40 + rng.Next(20, 40);

        var particle = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(LaneColors[lane]),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TranslateTransform()
        };
        Canvas.SetLeft(particle, laneX - 3);
        Canvas.SetTop(particle, hitY + 25);
        GameCanvas.Children.Add(particle);

        var tx = new DoubleAnimation(0, Math.Cos(rad) * dist, TimeSpan.FromMilliseconds(400))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var ty = new DoubleAnimation(0, Math.Sin(rad) * dist - 20, TimeSpan.FromMilliseconds(400))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) => GameCanvas.Children.Remove(particle);

        ((TranslateTransform)particle.RenderTransform).BeginAnimation(TranslateTransform.XProperty, tx);
        ((TranslateTransform)particle.RenderTransform).BeginAnimation(TranslateTransform.YProperty, ty);
        particle.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // 3. Текст PERFECT / GOOD всплывает вверх
    if (judge == "PERFECT" || judge == "GOOD")
    {
        var judgeColor = judge == "PERFECT" ? LaneColors[lane] : Color.FromRgb(0xa1, 0xa1, 0xaa);
        var floatText = new TextBlock
        {
            Text = judge,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = judge == "PERFECT" ? 15 : 13,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(judgeColor),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = judgeColor,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.8
            }
        };
        Canvas.SetLeft(floatText, laneX - 30);
        Canvas.SetTop(floatText, hitY - 10);
        GameCanvas.Children.Add(floatText);

        var moveUp = new DoubleAnimation(hitY - 10, hitY - 55, TimeSpan.FromMilliseconds(600))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fadeText = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600));
        fadeText.Completed += (_, _) => GameCanvas.Children.Remove(floatText);

        floatText.BeginAnimation(Canvas.TopProperty, moveUp);
        floatText.BeginAnimation(UIElement.OpacityProperty, fadeText);
    }
}
```

Вызывать в `HitNote()`:
```csharp
SpawnHitEffect(lane, judge);
```

---

## 5. Аура фона — перенос с главного экрана

В `GamePlayView` добавить Aurora-слой перед `GameField`:

```xml
<!-- Aurora Background (как на главной) -->
<Grid IsHitTestVisible="False" Panel.ZIndex="0">
    <Rectangle x:Name="GameAuroraRect1" Opacity="0.4">
        <Rectangle.Fill>
            <RadialGradientBrush Center="0.5,0.8" GradientOrigin="0.5,0.8" RadiusX="0.5" RadiusY="0.3">
                <GradientStop Color="#336366f1" Offset="0"/>
                <GradientStop Color="#00000000" Offset="1"/>
            </RadialGradientBrush>
        </Rectangle.Fill>
    </Rectangle>
</Grid>
```

Менять цвет при попадании — пульс ауры через `_auroraTimer` или отдельный таймер.

---

## 6. Счётчик КОМБО — пульс при росте

В `UpdateHUD()` при новом комбо:

```csharp
// Пульс-анимация на ComboText
var pulse = new DoubleAnimation(1.3, 1.0, TimeSpan.FromMilliseconds(200))
    { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1 } };
GameCombo.RenderTransformOrigin = new Point(0.5, 0.5);
if (GameCombo.RenderTransform is not ScaleTransform)
    GameCombo.RenderTransform = new ScaleTransform(1, 1);
((ScaleTransform)GameCombo.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
((ScaleTransform)GameCombo.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
```

---

## 7. Анимация стрелок при приближении к хит-зоне

В `GameTick` при движении стрелки, когда она близко к хит-зоне — увеличивать свечение:

```csharp
double distToHit = Math.Abs(top - hitY);
if (distToHit < 80 && note.Visual != null)
{
    double proximity = 1 - (distToHit / 80.0); // 0..1
    if (note.Visual.Effect is DropShadowEffect dse)
        dse.BlurRadius = 14 + proximity * 20;
}
```

---

## Файлы которые надо показать нейронке

1. `MainWindow.xaml` — весь файл (уже есть в этом чате)
2. `MainWindow.xaml.cs` — весь файл (уже есть)

Методичка полная, в следующем чате скидывай её + оба файла и нейронка сразу всё сделает.