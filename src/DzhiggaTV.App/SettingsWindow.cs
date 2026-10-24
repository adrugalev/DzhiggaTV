using L = DzhiggaTV.Localization.Texts;
using System.Globalization;
using System.Security.Cryptography;
using DzhiggaTV.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DzhiggaTV.Player;
using Microsoft.Win32;

namespace DzhiggaTV.App;

public sealed class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private MainViewModel M => _main.Model;
    private readonly TabControl _tabs = new() { TabStripPlacement = Dock.Left };
    private readonly TextBlock _notice = new() { Foreground = Brushes.Turquoise, TextWrapping = TextWrapping.Wrap, Margin = new(10) };
    public SettingsWindow(MainWindow main)
    {
        Theme.Window(this);
        _tabs.SetResourceReference(BackgroundProperty,"PanelBrush");
        _main = main; Owner = main; Title = L.T("DzhiggaTV · Настройки"); Width = 1000; Height = 770; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new(20) }; Content = dock;
        var bottom = new DockPanel(); DockPanel.SetDock(bottom,Dock.Bottom); dock.Children.Add(bottom); bottom.Children.Add(_notice);
        var close = new Button { Content = L.T("Готово"), HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => Close(); bottom.Children.Add(close);
        dock.Children.Add(_tabs);
        General(); Appearance(); Remote(); Playlists(); Collections(); Epg(); Logos(); Video(); Audio(); Controls(); Performance(); Diagnostics(); About();
        Closing += async (_, _) => await M.SaveAsync();
    }
    private StackPanel Page(string name, string intro)
    {
        var panel = new StackPanel { Margin = new(25,14,20,20) };
        panel.Children.Add(new TextBlock { Text = name, FontSize = 26, Margin = new(0,0,0,14) });
        Note(panel,intro);
        _tabs.Items.Add(new TabItem { Header = name, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
        return panel;
    }
    private static void Note(Panel parent,string text) => parent.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(3,0,0,18), LineHeight = 21 });
    private CheckBox Check(Panel parent,string label,string key,bool fallback=false)
    { var c = new CheckBox { Content = label, IsChecked = M.Flag(key,fallback) }; c.Click += (_, _) => M.SetSetting(key,c.IsChecked == true); parent.Children.Add(c); return c; }
    private TextBox Text(Panel parent,string label,string key,string fallback)
    { parent.Children.Add(new TextBlock { Text = label, Margin = new(3,12,0,7) }); var t = new TextBox { Text = M.Setting(key,fallback), MaxWidth = 550, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 330 }; t.TextChanged += (_, _) => M.SetSetting(key,t.Text); parent.Children.Add(t); return t; }
    private void Choice(Panel parent,string label,string key,(string Label,string Value)[] items,string fallback,Action<string>? changed=null)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new(3,12,0,6) });
        var c = new ComboBox { ItemsSource = items.Select(i=>i.Label).ToArray(), SelectedIndex = Math.Max(0,Array.FindIndex(items,i=>i.Value==M.Setting(key,fallback))), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 300 };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex < 0) return; var value = items[c.SelectedIndex].Value; if (M.Setting(key,fallback)==value) return; M.SetSetting(key,value); changed?.Invoke(value); }; parent.Children.Add(c);
    }
    private void Button(Panel parent,string label,Func<Task> action)
    { var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0,10,0,3) }; b.Click += async (_, _) => { b.IsEnabled=false; try { await action(); } catch { _notice.Text=L.T("Действие не завершено. Проверьте файл или доступ к сети."); } finally { b.IsEnabled=true; } }; parent.Children.Add(b); }
    private void General()
    {
        var p = Page(L.T("Общие"),L.T("Личная библиотека хранится на этом компьютере. Приложение не требует внешней учётной записи."));
        p.Children.Add(new TextBlock { Text = L.T("Язык интерфейса"), Margin = new(3, 8, 0, 6) });
        var language = new ComboBox { ItemsSource = LanguageChoice.All, ItemTemplate = LanguageChoice.Template(), SelectedValuePath = "Code", SelectedValue = L.DefaultLanguage, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(language, L.T("Язык интерфейса"));
        language.SelectionChanged += async (_, _) =>
        {
            if (language.SelectedValue is not string code || code == L.DefaultLanguage) return;
            await _main.GuardAsync(async () => { M.SetSetting("ui.language", code); await M.SaveAsync(); L.SetLanguage(code); });
        };
        p.Children.Add(language);
        Note(p, L.T("Язык меняется сразу. Названия каналов и тексты телепрограммы остаются в исходном виде."));
        Check(p,L.T("Включать последний канал при запуске"),"resume");
        var adult = Check(p,L.T("Показывать категорию для взрослых, включая поиск и EPG"),"adult.enabled");
        adult.Click += (_, _) =>
        {
            if (adult.IsChecked != true || M.Setting("adult.pin").Length == 0) return;
            var pin = PinPrompt.Show(this,L.T("Введите PIN"));
            if (pin is null || !PinPrompt.Verify(pin,M.Setting("adult.pin"))) { adult.IsChecked=false; M.SetSetting("adult.enabled",false); _notice.Text=L.T("PIN не подтверждён."); }
        };
        Button(p,L.T("Задать или изменить PIN"),() =>
        {
            var old = M.Setting("adult.pin");
            if (old.Length > 0 && (PinPrompt.Show(this,L.T("Текущий PIN")) is not { } previous || !PinPrompt.Verify(previous,old))) { _notice.Text=L.T("PIN не подтверждён."); return Task.CompletedTask; }
            if (PinPrompt.Show(this,L.T("Новый PIN (не менее 4 цифр)")) is { Length: >=4 } pin && pin.All(char.IsDigit)) { M.SetSetting("adult.pin",PinPrompt.Hash(pin)); _notice.Text=L.T("PIN сохранён."); }
            return Task.CompletedTask;
        });
        Button(p,L.T("Показать скрытые каналы"),async () => { M.State.Hidden.Clear(); await M.SaveAsync(); await M.ReloadAsync(); _notice.Text=L.T("Скрытые каналы возвращены в библиотеку."); });
        Button(p,L.T("Экспортировать избранное, подборки и EPG-сопоставления…"),async () => { var d=new SaveFileDialog { FileName="DzhiggaTV-settings.json",Filter=L.T("Настройки JSON|*.json") }; if(d.ShowDialog(this)==true) { await M.SaveAsync(); await M.Store.ExportSettingsAsync(d.FileName); _notice.Text=L.T("Резервная копия создана без адресов трансляций и токенов."); } });
    }
    private void Appearance()
    {
        var p=Page(L.T("Внешний вид"),L.T("В интерфейсе используются системные шрифты Windows. Анимации не требуются для навигации."));
        Choice(p,L.T("Тема"),"theme",[(L.T("Тёмная"),"dark"),(L.T("Светлая"),"light")],"dark",s=>Theme.Apply(s=="light"));
        Note(p,L.T("Изменяйте ширину списка каналов, перетаскивая разделитель. Кнопка ☰ сворачивает левую навигацию. Интерфейс не использует обязательные анимации и учитывает reduced motion отсутствием движения."));
    }
    private void Remote()
    {
        var p = Page("Remote", L.T("Смотрите телевизор на телефоне по домашнему Wi-Fi и управляйте плеером на компьютере. Мобильный просмотр не переключает канал на ПК."));
        Button(p, L.T("Открыть DzhiggaTV Remote"), () => { new RemoteSettingsWindow(_main, this).ShowDialog(); return Task.CompletedTask; });
        Note(p, L.T("Remote выключен по умолчанию. Для подключения отсканируйте QR-код и подтвердите устройство. Внешний сервер и отдельная учётная запись не нужны."));
    }
    private void Playlists()
    {
        var p=Page(L.T("Плейлисты"),L.T("Локальные M3U/M3U8 и сетевые плейлисты. При обновлении идентификатор источника сохраняется, избранное и ручные соответствия остаются на месте."));
        var list=new ListBox { Height=160,DisplayMemberPath="Name" }; p.Children.Add(list);
        async Task Reload() => list.ItemsSource=await M.Store.GetPlaylistSourcesAsync();
        Loaded+=async(_,_)=>await Reload();
        Button(p,L.T("Добавить файл…"),async()=> { var d=new OpenFileDialog { Filter="M3U/M3U8|*.m3u;*.m3u8" }; if(d.ShowDialog(this)==true) { await M.ImportAsync(d.FileName); await Reload(); } });
        Button(p,L.T("Добавить URL…"),async()=> { if(TextPrompt.Show(this,L.T("Сетевой плейлист"),L.T("URL плейлиста")) is { } location) { await M.ImportAsync(location); await Reload(); } });
        Button(p,L.T("Обновить выбранный"),async()=> { if(list.SelectedItem is DzhiggaTV.Core.PlaylistSource source) { await M.ImportAsync(source.Location,source.Id); await Reload(); } });
        Button(p,L.T("Удалить выбранный источник"),async()=> { if(list.SelectedItem is DzhiggaTV.Core.PlaylistSource source) { await M.Store.RemovePlaylistAsync(source.Id); await M.ReloadAsync(); await Reload(); } });
    }
    private void Collections()
    {
        var p=Page(L.T("Избранное и подборки"),L.T("Добавляйте каналы через ☆ или контекстное меню. Канал может входить в несколько подборок. В избранном и подборках порядок меняется перетаскиванием."));
        var list=new ListBox { Height=150,ItemsSource=M.State.Collections.Keys.ToArray() }; p.Children.Add(list);
        Button(p,L.T("Создать подборку"),async()=> { if(TextPrompt.Show(this,L.T("Подборка"),L.T("Название")) is { } name) { M.State.Collections.TryAdd(name,[]); await M.SaveAsync(); list.ItemsSource=M.State.Collections.Keys.ToArray(); } });
        Button(p,L.T("Переименовать выбранную"),async()=> { if(list.SelectedItem is string old && TextPrompt.Show(this,L.T("Подборка"),L.T("Новое название"),old) is { } name && name!=old && !M.State.Collections.ContainsKey(name)) { M.State.Collections[name]=M.State.Collections[old]; M.State.Collections.Remove(old); await M.SaveAsync(); list.ItemsSource=M.State.Collections.Keys.ToArray(); } });
        Button(p,L.T("Удалить выбранную подборку"),async()=> { if(list.SelectedItem is string name) { M.State.Collections.Remove(name); await M.SaveAsync(); list.ItemsSource=M.State.Collections.Keys.ToArray(); } });
    }
    private void Epg()
    {
        var p=Page(L.T("Телепрограмма EPG.ONE"),L.T("Основной источник предустановлен: полный XMLTV с прозрачными прямоугольными логотипами. Обновление выполняется в фоне раз в сутки; при сбое остаётся последняя рабочая база."));
        Check(p,L.T("Фоновое ежедневное обновление"),"epg.enabled");
        Choice(p,L.T("Источник XMLTV"),"epg.source",[(L.T("Полный · прозрачные логотипы"),"https://epg.one/epg2.xml.gz"),(L.T("Полный · квадратные логотипы"),"https://epg.one/epg.xml.gz"),(L.T("Русскоязычный"),"https://epg.one/ru2.xml.gz")],"https://epg.one/epg2.xml.gz");
        Note(p,L.T("Телепрограмма и логотипы EPG.ONE подключаются автоматически, включая публичное HTTP-зеркало самого сервиса. Адреса трансляций сервису не передаются. Дополнительное разрешение для EPG.ONE не требуется."));
        var http=Check(p,L.T("Разрешить HTTP также для других источников логотипов"),"epg.http"); http.Click+=async(_,_)=> { M.Epg.AllowInsecureHttp=M.Logos.AllowInsecureHttp=http.IsChecked==true; await M.RefreshLogosAsync(); };
        Button(p,L.T("Обновить сейчас"),async()=> { await M.RefreshEpgAsync(); _notice.Text=M.Status; });
        Button(p,L.T("Поправка времени для канала…"),()=> { new EpgTimingWindow(_main, this, M.Active).ShowDialog(); return Task.CompletedTask; });
        Button(p,L.T("Диагностика и ручное сопоставление"),()=> { _tabs.SelectedIndex=10; return Task.CompletedTask; });
    }
    private void Logos()
    {
        var p=Page(L.T("Логотипы"),L.T("Приоритет: ваш логотип → tvg-logo плейлиста → сопоставленный XMLTV → каталог EPG.ONE → локальный архив → текстовая заглушка. Изображения загружаются по мере прокрутки и сохраняются на диске."));
        Button(p,L.T("Импортировать архив пиконов ZIP…"),async()=> { var d=new OpenFileDialog { Filter=L.T("ZIP с пиконами|*.zip") }; if(d.ShowDialog(this)==true) { await using var stream=File.OpenRead(d.FileName); await M.Logos.ImportZipAsync(stream); await M.RefreshLogosAsync(); _notice.Text=L.T("Архив пиконов импортирован."); } });
        Button(p,L.T("Загрузить прозрачные пиконы EPG.ONE"),async()=> { await M.Logos.DownloadPiconsAsync(); await M.RefreshLogosAsync(); _notice.Text=L.T("Пиконы сохранены в дисковом кэше."); });
        Note(p,L.T("Кэш ограничен 256 МиБ. Логотип конкретного канала можно заменить через правую кнопку мыши. Автоматическое обновление не заменяет пользовательский выбор."));
    }
    private void Video()
    {
        var p=Page(L.T("Видео и HDR"),L.T("Вывод gpu-next / D3D11. D3D11VA включается, когда поток и видеокарта его поддерживают. HDR не объявляется активным только по названию канала."));
        Choice(p,L.T("HDR и цвет"),"hdr",[(L.T("Автоматически по параметрам дисплея"),"Auto"),(L.T("Преобразовывать HDR в SDR (BT.709)"),"ToneMapToSdr")],"Auto",v=>_main.Player.SetHdrMode(Enum.Parse<HdrMode>(v)));
        Choice(p,L.T("Соотношение сторон"),"aspect",[(L.T("По источнику"),"Original"),("16:9","Fit16By9"),("4:3","Fit4By3"),(L.T("Растянуть"),"Stretch")],"Original",v=>_main.Player.SetAspect(Enum.Parse<AspectMode>(v)));
        Note(p,L.T("Для HDR10 / HLG требуется совместимая конфигурация Windows, дисплея, драйвера и потока. Статистика показывает метаданные источника и вывода. Аппаратная проверка HDR и поведения SDR на HDR-дисплее описана в ручном тест-плане."));
    }
    private void Audio()
    {
        var p=Page(L.T("Звук"),L.T("Доступные дорожки появляются в меню ••• во время воспроизведения. Несуществующие дорожки не предлагаются."));
        Text(p,L.T("Предпочтительные языки звука (через запятую)"),"audio.language","rus,ru,eng,en");
        Text(p,L.T("Предпочтительные языки субтитров"),"subtitle.language","rus,ru");
        var devices=_main.Player.GetAudioDevices();
        Choice(p,L.T("Устройство вывода"),"audio.device",devices.Count>0?devices.Select(d=>(d.Description,d.Name)).ToArray():[(L.T("Системное устройство"),"auto")],"auto",v=>_main.Player.SetAudioDevice(v));
        Button(p,L.T("Применить языки"),()=> { _main.Player.SetPreferredLanguages(M.Setting("audio.language"),M.Setting("subtitle.language")); return Task.CompletedTask; });
    }
    private void Controls()
    {
        var p=Page(L.T("Управление"),L.T("Клавиатура и мышь работают в обычном, полноэкранном и компактном режимах."));
        Note(p,L.T("F11 / Alt+Enter / двойной щелчок — полный экран\nEscape — вернуться в обычное окно\nSpace — пауза\nPgUp / PgDn — каналы\nBackspace — предыдущий просмотренный\n↑ / ↓ — громкость, M — звук\nF — избранное, Ctrl+F — поиск\nC — каналы поверх видео, G — программа\nI — информация о потоке, Ctrl+M — мини-плеер\n\nКомпактная панель управления появляется при наведении на видео и скрывается, когда курсор уходит. В полном экране и мини-плеере панели скрываются через секунду после ухода курсора. Наведение и взаимодействие удерживают их видимыми. Мини-плеер можно перемещать за видеоповерхность."));
    }
    private void Performance()
    {
        var p=Page(L.T("Производительность"),L.T("Списки используют виртуализацию, логотипы — ограниченную фоновую очередь. Сетевой буфер регулируется средствами libmpv."));
        Choice(p,L.T("Буфер"),"buffer",[(L.T("Минимальная задержка"),"LowLatency"),(L.T("Сбалансированный (стандарт mpv)"),"Balanced"),(L.T("Стабильное соединение"),"Stable")],"Balanced",v=>_main.Player.SetBufferPreset(Enum.Parse<BufferPreset>(v)));
        Note(p,L.T("Минимальная задержка отключает предварительное чтение; стабильный режим включает cache-pause. Сбалансированный использует стандартные объёмы mpv. Численные размеры буфера не выдуманы; фактическое заполнение доступно в статистике."));
    }
    private void Diagnostics()
    {
        var p=Page(L.T("Диагностика"),L.T("Покрытие рассчитывается по фактически найденным текущим передачам. Сомнительные совпадения требуют ручного выбора."));
        var text=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new(3,10,0,15) }; p.Children.Add(text);
        var list=new ListBox { Height=230,ItemsSource=M.AllChannels.Where(M.IsAllowed).ToArray(),DisplayMemberPath="Name" }; p.Children.Add(list);
        async Task Update()
        {
            await M.RefreshEpgViewAsync(); var channels=M.AllChannels.Where(M.IsAllowed).ToArray();
            var schedules=await M.Guide.GetNowNextBatchAsync(channels.Select(c=>M.EpgFor(c.Id)).OfType<ChannelEpg>());
            var covered=channels.Count(c=>schedules.GetValueOrDefault(c.Id)?.Current is not null);
            var logos=channels.Count(c=>M.State.UserLogos.ContainsKey(c.Id)||c.Channel.Logo is not null||M.Mappings.TryGetValue(c.Id,out var map)&&M.EpgChannels.GetValueOrDefault(map.EpgId)?.Logo is not null);
            text.Text=L.F("Всего каналов: {0:N0}; видимых: {1:N0}\nС текущей программой: {2:N0} ({3:P1})\nС доступным логотипом: {4:N0}\nСпорных: {5:N0}; без назначения: {6:N0}\nПоследнее обновление: {7}", M.AllChannels.Count, channels.Length, covered, (channels.Length>0?(double)covered/channels.Length:0), logos, M.Epg.Diagnostics.Ambiguous, channels.Count(c=>!M.Mappings.ContainsKey(c.Id)), M.Epg.LastUpdated?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??L.T("ещё не выполнялось"));
        }
        Button(p,L.T("Обновить диагностику"),Update);
        Button(p,L.T("Назначить программу выбранному каналу"),async()=> { if(list.SelectedItem is ChannelItem item) { new MappingWindow(_main,item) { Owner=this }.ShowDialog(); await Update(); } });
        Loaded+=async(_,_)=>await Update();
    }
    private void About()
    {
        var p=Page(L.T("О программе"),L.F("DzhiggaTV {0}\nIPTV-плеер для Windows 11 x64.", App.DisplayVersion));
        Note(p,L.T("C# / .NET 10 · WPF · libmpv · SQLite\n\nАдреса трансляций и источников зашифрованы Windows DPAPI для текущего пользователя. Они не записываются в журналы и не входят в экспорт настроек.\n\nEPG.ONE — публичная программа и логотипы. Приложение не предоставляет права на IPTV-контент и не обещает телеархив или запись.\n\nЛицензии и источники зависимостей приложены к дистрибутиву. Сборка не подписана цифровым сертификатом.\n\nРасположение данных: ")+M.DataDirectory);
    }
}

internal sealed class PinPrompt : Window
{
    private readonly PasswordBox _value=new() { Margin=new(0,15,0,15) };
    private PinPrompt(Window owner,string label)
    { Theme.Window(this);Owner=owner;Title="DzhiggaTV · PIN";Width=360;Height=210;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;var p=new StackPanel { Margin=new(24) };Content=p;p.Children.Add(new TextBlock { Text=label });p.Children.Add(_value);var b=new Button { Content=L.T("Подтвердить"),IsDefault=true };b.Click+=(_,_)=>DialogResult=true;p.Children.Add(b);Loaded+=(_,_)=>_value.Focus(); }
    public static string? Show(Window owner,string label) { var d=new PinPrompt(owner,label);return d.ShowDialog()==true?d._value.Password:null; }
    public static string Hash(string pin) { var salt=RandomNumberGenerator.GetBytes(16);var hash=Rfc2898DeriveBytes.Pbkdf2(pin,salt,150000,HashAlgorithmName.SHA256,32);return Convert.ToBase64String(salt)+":"+Convert.ToBase64String(hash); }
    public static bool Verify(string pin,string encoded) { try { var parts=encoded.Split(':');return CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(pin,Convert.FromBase64String(parts[0]),150000,HashAlgorithmName.SHA256,32),Convert.FromBase64String(parts[1])); } catch { return false; } }
}
