<div align="center">

<img src="Assets/icon.ico" width="100" height="100" alt="NetFix Logo"/>

<h1>NetFix</h1>

<p><b>Одна кнопка — и интернет снова работает</b></p>

[![Release](https://img.shields.io/github/v/release/rupleide/NetFix?style=flat-square&color=3b82f6&label=версия)](https://github.com/rupleide/NetFix/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/rupleide/NetFix/total?style=flat-square&color=22c55e&label=скачиваний)](https://github.com/rupleide/NetFix/releases)
[![License](https://img.shields.io/badge/лицензия-MIT-blue?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/платформа-Windows-0078d4?style=flat-square&logo=windows)](https://github.com/rupleide/NetFix/releases/latest)
[![Telegram](https://img.shields.io/badge/Telegram-канал-26A5E4?style=flat-square&logo=telegram)](https://t.me/NetFixRuBi)

</div>

---

## 💡 О проекте

В современных реалиях доступ к привычным сервисам вроде **Telegram** и **Discord** часто ограничивается или полностью блокируется РКН. Настройка обходных путей через `zapret` или `tg-ws-proxy` — задача нетривиальная.

**NetFix** — это интуитивно понятная оболочка (GUI), которая автоматизирует весь процесс. Вам не нужно открывать консоль или редактировать конфиги. Скачал, нажал кнопку — и всё работает.

> [!NOTE]
> **От автора:** Я создал это приложение специально для своих друзей, которые постоянно просили помочь с Telegram и Discord. Благодаря им вы видите этот проект. Я сделал всё, чтобы вам не пришлось искать "знакомого программиста" — программа сама всё объяснит и наладит.

---

## 📥 Скачать

<div align="center">

| Источник | Ссылка |
|:---:|:---:|
| 🚀 GitHub (рекомендуется) | **[Скачать последнюю версию](https://github.com/rupleide/NetFix/releases/latest)** |
| ☁️ Google Drive (зеркало) | **[Открыть зеркало](https://drive.google.com/file/d/1djW9sOeUevblH1OM--PSFI8RwZl16Rwx/view?usp=sharing)** |

</div>

---

> [!IMPORTANT]
> **📢 Telegram-канал проекта**
>
> Я создал канал специально для этого проекта. Там я публикую новые найденные обходы, сообщаю о важных обновлениях Zapret, TgWsProxy и самого NetFix. Планирую активно развивать его — советую подписаться, чтобы не пропустить ничего важного.
>
> <a href="https://t.me/NetFixRuBi" target="_blank">
>   <img src="https://img.shields.io/badge/Подписаться%20на%20канал-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white" alt="Telegram Button" />
> </a>

</div>

---

## 🛡 Безопасность

> [!CAUTION]
> Программы, меняющие сетевые настройки, часто вызывают подозрение. Отвечу прямо.

- **Здесь НЕТ вирусов.** Мне нет никакого смысла заражать компьютеры своих друзей или пользователей.
- **Чистое ядро:** Приложение использует оригинальные наработки разработчика **[Flowseal](https://github.com/Flowseal)**.
- **Прозрачность:** NetFix — это просто удобный инструмент управления. Исходный код открыт и доступен для проверки.

> [!WARNING]
> WinDivert может вызвать реакцию антивируса. Это нормально — WinDivert используется для перехвата трафика и может детектироваться как `Not-a-virus:RiskTool`. Добавьте папку программы в исключения антивируса.

---

## 📸 Обзор функций

<div align="center">

<img src="Assets/Screenshots/0318.gif" width="800" alt="NetFix в действии"/>

<p><i>Вся программа за 30 секунд — от кнопки до результата.</i></p>

<br/>

<img src="Assets/Screenshots/Main.png" width="800" alt="Главный экран"/>

<p><i><b>Главный экран:</b> Одна кнопка запускает диагностику, проверяет соединение и активирует необходимые службы. Лог в реальном времени покажет каждый шаг.</i></p>

<br/>

<img src="Assets/Screenshots/Servers.png" width="800" alt="Диагностика"/>

<p><i><b>Расширенная диагностика:</b> Проверяет пинг и доступность каждого дата-центра Telegram (DC1–DC5). Сразу видно, где именно "затык" в сети.</i></p>

<br/>

<table border="0" cellpadding="12">
  <tr>
    <td width="50%" align="center">
      <img src="Assets/Screenshots/Settings.png" width="380"/><br/><br/>
      <b>⚙️ Гибкие настройки</b><br/>
      <i>Управление путями к файлам, автозагрузкой и уведомлениями в одном окне.</i>
    </td>
    <td width="50%" align="center">
      <img src="Assets/Screenshots/Services.png" width="380"/><br/><br/>
      <b>🛠 Управление сервисами</b><br/>
      <i>Быстрый доступ к запуску и остановке Zapret и TgWsProxy по отдельности.</i>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="Assets/Screenshots/Update.png" width="380"/><br/><br/>
      <b>🔄 Автообновление</b><br/>
      <i>Программа сама следит за новыми версиями и предлагает обновиться в один клик.</i>
    </td>
    <td width="50%" align="center">
      <img src="Assets/Screenshots/Frequent%20questions.png" width="380"/><br/><br/>
      <b>❓ Понятный FAQ</b><br/>
      <i>Все типичные проблемы и их решения — чтобы не пришлось никого просить о помощи.</i>
    </td>
  </tr>
</table>

</div>

---

## 📜 Лицензия

Данное ПО распространяется под лицензией **MIT**.

- **GUI и код автоматизации (NetFix):** © 2024–2026 [rupleide](https://github.com/rupleide). Свободно для изменения и распространения при сохранении авторства.
- **Сторонние компоненты (Zapret, TgWsProxy):** Все права принадлежат их автору — **[Flowseal](https://github.com/Flowseal)**. Именно его сборки являются сердцем этого приложения. Я не являюсь автором инструментов `Zapret` и `TgWsProxy`.

**Отказ от ответственности:** Программа предоставляется «как есть». Автор не несёт ответственности за последствия использования ПО. Используя NetFix, вы подтверждаете, что делаете это на свой страх и риск.

---

<div align="center">
  <sub>Разработано с ❤️ для тех, кто хочет просто нажать на кнопку</sub><br/>
  <sub>v1.0.5 · 2026</sub>
</div>
