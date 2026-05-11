# Оставшиеся изменения для Discord Rich Presence

## В RenderDiagReport - в самом конце метода добавить:
```csharp
_discord.SetAllGood(
    r.AppStatus?.ZapretRunning == true,
    r.AppStatus?.TgWsProxyRunning == true);
```

## В StartGame - найти `_missCount = 0;` и добавить рядом:
```csharp
_maxCombo = 0;
_currentTrackTitle = title;
_gameStartDateTime = DateTime.Now;
```

## В StartGame - после `cdTimer.Start();` добавить:
```csharp
_discordGameTimer?.Stop();
_discordGameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
_discordGameTimer.Tick += (_, _) => {
    int acc = _totalNotes > 0 ? (int)((double)_hitNotes / _totalNotes * 100) : 100;
    _discord.SetGamePlaying(_currentTrackTitle, _gameCombo, acc, _gameStartDateTime);
};
_discordGameTimer.Start();
// Первое обновление сразу
_discord.SetGamePlaying(_currentTrackTitle, 0, 100, _gameStartDateTime);
```

## В HitNote - найти `_gameCombo++;` и добавить после:
```csharp
if (_gameCombo > _maxCombo) _maxCombo = _gameCombo;
```

## В GameOver - найти `string rank = ...` и добавить сразу после вычисления rank:
```csharp
_discordGameTimer?.Stop();
_discordGameTimer = null;
_discord.SetGameResults(_currentTrackTitle, rank, _gameScore, acc, _maxCombo);
```

## В StopGame - после `_dangerModeActive = false;`:
```csharp
_discordGameTimer?.Stop();
_discordGameTimer = null;
_discord.SetMainMenu();
```

## В EditorStartBtn_Click - перед `_editorPlayer.Play();`:
```csharp
_discord.SetLevelEditor(EditorTrackTitle.Text.Trim(), 0, DateTime.Now);
```

## В StopEditorRecording - в начале метода после проверки `if (!_editorRecording ...)`:
```csharp
_discord.SetMainMenu();
```
