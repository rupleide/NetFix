# NetFix — Ритм-игра: методичка и план разработки

---

## Что строим

Вкладка **«Игра»** в главном окне. Два раздела:
- **Играть** — выбираешь трек (дефолтный или пользовательский) → играешь
- **Создать уровень** — загружаешь mp3, слушаешь трек, кликаешь под бит → уровень сохраняется автоматически

---

## Архитектура файлов

```
NetFix/
├── Views/
│   ├── GameTab.xaml              ← вкладка: меню "Играть / Создать уровень"
│   ├── GameTab.xaml.cs
│   ├── RhythmGameView.xaml       ← сам игровой экран (4 дорожки, стрелки)
│   ├── RhythmGameView.xaml.cs
│   └── LevelEditorView.xaml      ← экран записи уровня
│   └── LevelEditorView.xaml.cs
├── Game/
│   ├── RhythmEngine.cs           ← логика: тайминги, спавн, детект попаданий
│   ├── LevelRecorder.cs          ← запись кликов → NoteMap
│   ├── LevelExporter.cs          ← упаковка в ZIP
│   └── ScoreCalculator.cs        ← PERFECT / GOOD / MISS, комбо, очки
├── Models/
│   ├── NoteMap.cs                ← модель уровня (список нот с временными метками)
│   └── UserLevel.cs              ← метаданные: название, автор, путь к mp3
└── Assets/
    └── DefaultTrack/
        ├── default_track.mp3
        └── default_notes.json
```

---

## Формат файла уровня (notes.json)

```json
{
  "title": "Мой крутой трек",
  "author": "username",
  "trackFile": "track.mp3",
  "bpm": 140,
  "notes": [
    { "time": 0.428, "lane": 0 },
    { "time": 0.857, "lane": 2 },
    { "time": 1.285, "lane": 1 },
    { "time": 1.714, "lane": 3 }
  ]
}
```

`lane`: 0 = ◀ (A), 1 = ▼ (S), 2 = ▲ (K), 3 = ▶ (L)  
`time`: секунды от начала трека (double)

---

## Формат экспорта

```
MyLevel_export.zip
├── track.mp3          ← оригинальный файл пользователя
└── notes.json         ← NoteMap
```

При импорте: распаковываем zip в `%AppData%\NetFix\levels\{title}\`

---

## Порядок разработки (по этапам)

### Этап 1 — Структура и навигация
- Добавить вкладку «Игра» перед «Сервисы» в MainWindow
- Сделать GameTab.xaml с двумя кнопками: Играть / Создать уровень
- Навигация между вьюхами через Frame или ContentControl

### Этап 2 — Игровой экран (RhythmGameView)
- 4 вертикальные дорожки (Canvas)
- Зона удара внизу (fixed Y позиция)
- Стрелки — это Border/TextBlock, движущиеся сверху вниз через DispatcherTimer (60fps)
- KeyDown → определяем дорожку → ищем ближайшую ноту → PERFECT/GOOD/MISS
- Отображение: Score, Combo, Accuracy наверху

### Этап 3 — RhythmEngine
- Загрузка NoteMap из JSON
- MediaPlayer для трека
- DispatcherTimer: каждый тик считаем `mediaPlayer.Position.TotalSeconds`
- Спавн стрелки когда `note.time - currentTime <= FALL_TIME_SEC`
- FALL_TIME_SEC ≈ 1.5–2.0 секунды (настраиваемо)

### Этап 4 — Редактор уровней (LevelEditorView)
- Выбор mp3 через OpenFileDialog
- Поле: название трека
- Кнопка «Начать запись» → обратный отсчёт 3-2-1
- После отсчёта: запускаем MediaPlayer + начинаем запись
- KeyDown (A/S/K/L) → `recorder.RecordNote(lane, mediaPlayer.Position.TotalSeconds)`
- Кнопка «Завершить» или трек заканчивается → сохранение

### Этап 5 — LevelRecorder и сохранение
- Пишем список NoteEntry в памяти
- По завершении: сериализуем в JSON (System.Text.Json)
- Копируем mp3 в `%AppData%\NetFix\levels\{title}\`
- Записываем notes.json рядом
- Уровень сразу появляется в списке «Играть»

### Этап 6 — Экспорт/импорт
- LevelExporter: `ZipFile.CreateFromDirectory` или `ZipArchive` вручную
- Диалог сохранения zip
- Импорт: OpenFileDialog → `ZipFile.ExtractToDirectory` → обновить список

### Этап 7 — Полировка
- Экран результатов (Score / Accuracy / Rank: S A B C)
- Анимации попаданий (вспышка на дорожке)
- BPM-пульс фона (опционально)
- Плавное появление/исчезновение стрелок

---

## Ключевые классы — что делает каждый

### RhythmEngine.cs
```csharp
// Главный класс логики игры
public class RhythmEngine
{
    public event Action<int, HitResult> OnHit;   // lane, PERFECT/GOOD/MISS
    public event Action<Note> OnNoteSpawned;      // когда спавнить стрелку

    public void Load(NoteMap map, string trackPath) { ... }
    public void Start() { ... }   // запускает MediaPlayer + таймер
    public void Stop()  { ... }
    public HitResult TryHit(int lane) { ... }  // вызывается из KeyDown
}
```

### LevelRecorder.cs
```csharp
public class LevelRecorder
{
    private List<NoteEntry> _notes = new();
    private MediaPlayer _player = new();

    public void StartRecording(string mp3Path) { ... }   // 3-2-1 потом Play
    public void RecordHit(int lane) { ... }               // KeyDown → записать
    public NoteMap Finish() { ... }                       // вернуть NoteMap
}
```

### ScoreCalculator.cs
```csharp
public class ScoreCalculator
{
    // Окна попадания (в секундах)
    const double PERFECT_WINDOW = 0.05;  // ±50ms
    const double GOOD_WINDOW    = 0.12;  // ±120ms

    public HitResult Evaluate(double offset) { ... }
    // offset = |currentTime - note.time|
}
```

---

## NuGet зависимости

Нужны только стандартные — ничего нового ставить не надо:
- `System.Text.Json` — сериализация NoteMap (уже в .NET 6+)
- `System.IO.Compression` — ZipFile для экспорта (уже в .NET)
- `System.Windows.Media.MediaPlayer` — воспроизведение mp3 (WPF)
- `System.Windows.Threading.DispatcherTimer` — игровой тик (WPF)

---

## Промт для нейронки (используй когда начнёшь кодить)

```
Я разрабатываю WPF приложение NetFix на C# (.NET 8).
Хочу добавить вкладку "Игра" — ритм-игру в стиле osu/StepMania.

Покажи мне следующие существующие файлы чтобы понять структуру проекта:
1. MainWindow.xaml — хочу видеть как сделаны вкладки (TabControl или что-то другое)
2. MainWindow.xaml.cs — логика переключения вкладок
3. Views/DiagnosticsView.xaml (или любую существующую вкладку) — пример структуры View
4. App.xaml — тема/стили приложения
5. Любой существующий Service (например DiagnosticsEngine.cs) — чтобы понять паттерн

После того как покажешь эти файлы, я расскажу что именно нужно реализовать.

Что нужно реализовать:
- GameTab.xaml — вкладка с кнопками "Играть" и "Создать уровень"
- RhythmGameView.xaml — игровой экран: 4 вертикальные дорожки (Canvas),
  стрелки ◀▼▲▶ падают сверху вниз, зона удара снизу, управление A S K L
- LevelEditorView.xaml — экран записи: загрузка mp3, поле названия,
  кнопка "Начать запись", обратный отсчёт 3-2-1, KeyDown пишет тайминги
- RhythmEngine.cs — игровая логика: DispatcherTimer 60fps, спавн нот,
  детект попаданий (PERFECT ±50ms, GOOD ±120ms, MISS)
- LevelRecorder.cs — запись кликов → NoteMap → сохранение в JSON
- LevelExporter.cs — ZIP архив (track.mp3 + notes.json)
- NoteMap.cs — модель: { title, author, trackFile, notes: [{time, lane}] }

Стиль приложения: тёмный (#18181B фон), акцент #6366f1 (индиго).
Существующие вкладки: Сервисы, Частые вопросы, Диагностика.
Новая вкладка "Игра" должна быть первой.

Начни с просьбы показать файлы структуры, затем выдай все файлы по очереди.
```

---

## Оценка сложности

| Этап | Сложность | Время |
|------|-----------|-------|
| Структура + навигация | Низкая | 1–2 часа |
| Игровой экран (UI) | Средняя | 3–4 часа |
| RhythmEngine (логика) | Средняя | 4–6 часов |
| Редактор уровней | Средняя | 3–4 часа |
| Сохранение/экспорт | Низкая | 1–2 часа |
| Полировка и анимации | Средняя | 3–4 часа |
| **Итого** | | **~2–3 дня** |

---

## Советы

1. **Начни с RhythmEngine без UI** — протестируй тайминги в unit-тесте
2. **FALL_TIME** сделай настраиваемым константой — потом будешь подбирать
3. **KeyDown на уровне Window**, не на Canvas — иначе фокус теряется
4. **MediaPlayer.Position** немного лагает — используй `Stopwatch` для точного времени, синхронизируй с позицией раз в секунду
5. **Уровни храни в `%AppData%\NetFix\levels\`** — не в папке приложения, иначе проблемы при обновлении

---

*Методичка сгенерирована под проект NetFix. Версия плана: 1.0*
