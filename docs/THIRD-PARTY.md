# Лицензии и происхождение зависимостей

Исходный код DzhiggaTV — GPL-3.0-or-later. Текст GPL v3 находится в корневом `LICENSE`; используется стандартный текст из [SPDX license-list-data](https://github.com/spdx/license-list-data/blob/main/text/GPL-3.0-or-later.txt). Это не меняет лицензии компонентов ниже.

| Компонент | Версия/источник | Лицензия и текст |
|---|---|---|
| .NET / Windows Desktop runtime | .NET 10, self-contained win-x64 при публикации; точная SDK-версия в build-manifest.json | MIT и notices встроенных компонентов: `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-ThirdPartyNotices.txt`, `licenses/WindowsDesktop-ThirdPartyNotices.txt`. [Исходники runtime](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf). |
| Microsoft.Data.Sqlite и Core | 10.0.13; [NuGet Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.13) | MIT, `licenses/Microsoft.Data.Sqlite-MIT.txt`; [исходный код тега](https://github.com/dotnet/efcore/tree/v10.0.13). |
| SQLitePCLRaw bundle/core/provider/native | 2.1.13; транзитивные пакеты Microsoft.Data.Sqlite | Apache-2.0 для обёрток и пакетов; `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/Apache-2.0.txt`; [репозиторий](https://github.com/ericsink/SQLitePCL.raw). |
| SQLite | Бинарник e_sqlite3 из указанного NuGet-пакета | SQLite — public domain; [официальное пояснение](https://www.sqlite.org/copyright.html). Версия SQLite зависит от пакета; не подменять неподтверждённой версией. |
| libmpv | shinchiro `20261018`, mpv commit `01e2533f31`, x86_64, `mpv-dev-x86_64-20261018-git-01e2533f31.7z` | mpv по умолчанию GPL-2.0-or-later; применимость LGPL зависит от флагов и всех связанных библиотек. Включены `licenses/mpv-Copyright.txt`, `licenses/GPL-2.0.txt`, `licenses/LGPL-2.1.txt`. [Copyright именно commit](https://github.com/mpv-player/mpv/blob/01e2533f31/Copyright). |

libmpv включает FFmpeg/libplacebo и другие библиотеки сборщика. Наличие одной DLL не означает отсутствие дополнительных лицензионных обязательств. DzhiggaTV не заявляет, что эта сборка libmpv имеет LGPL-only состав. Полный список фактически связанных зависимостей и их notices нужно формировать по corresponding source/build manifest конкретного бинарника.

## Закреплённый бинарник libmpv

- [Release 20261018](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261018).
- [Архив](https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261018/mpv-dev-x86_64-20261018-git-01e2533f31.7z).
- SHA-256 архива: `d7f5621f42b6e161fe032df914a10bdece28a89df3e57c6c3e16a5a250929b5e` — digest опубликован GitHub Releases API и проверен при скачивании.
- SHA-256 `libmpv-2.dll`: `82bae3b6bf7bb737afc9b52d590b477983c715f3c63c1a361f9ee06523dc5dc2` — проверен локально.
- [Workflow поставщика](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/39081116476).
- [Инструкции сборщика](https://github.com/shinchiro/mpv-winbuild-cmake) и [upstream mpv commit](https://github.com/mpv-player/mpv/tree/01e2533f31).

## Corresponding sources и дальнейшее распространение

В проверенном GitHub release 20261018 опубликованы бинарные архивы, но отдельный полный архив соответствующих исходников libmpv со всеми фактически использованными зависимостями не найден. Репозиторий сборщика и текущие ветки зависимостей сами по себе не доказывают соответствие готовой DLL. Полный такой архив не включён в этот локальный выпуск; приведённые ссылки являются provenance, а не заявлением о выполнении всех обязательств публичного распространения.

Перед публикацией бинарной сборки необходимо получить или самостоятельно подготовить соответствующие исходники именно используемого libmpv, всех включённых GPL/LGPL-компонентов, патчи, зафиксированные ревизии и сценарии сборки; обеспечить предусмотренный лицензиями доступ к ним и сохранить notices всех компонентов. Альтернатива — собрать libmpv самостоятельно из фиксированных исходников и распространять исходники рядом с бинарниками. Для воспроизведения builder предоставляет CMake/Ninja и цель `download`, однако обновление `master` не воспроизводит автоматически DLL от 20261018.

Сама DzhiggaTV не изменяет бинарник libmpv. Вызов библиотеки динамический, исходники приложения и сценарии сборки доступны в комплекте проекта. EPG.ONE, логотипы и пользовательские плейлисты не включены в исходный или portable-архив. Их доступность и право использования определяются поставщиками; приложение использует HTTP(S) по запросу пользователя.

## Установщик

Inno Setup не включён в portable-папку. Сценарий `.iss` использует установленный пользователем компилятор. Его лицензия и допустимость использования конкретной версии проверяются на [сайте Inno Setup](https://jrsoftware.org/isdl.php); приложение не устанавливает его автоматически.
