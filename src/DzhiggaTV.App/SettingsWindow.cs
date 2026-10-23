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
        _main = main; Owner = main; Title = "DzhiggaTV · Настройки"; Width = 1000; Height = 770; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new(20) }; Content = dock;
        var bottom = new DockPanel(); DockPanel.SetDock(bottom,Dock.Bottom); dock.Children.Add(bottom); bottom.Children.Add(_notice);
        var close = new Button { Content = "Готово", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => Close(); bottom.Children.Add(close);
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
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex < 0) return; var value = items[c.SelectedIndex].Value; M.SetSetting(key,value); changed?.Invoke(value); }; parent.Children.Add(c);
    }
    private void Button(Panel parent,string label,Func<Task> action)
    { var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0,10,0,3) }; b.Click += async (_, _) => { b.IsEnabled=false; try { await action(); } catch { _notice.Text="Действие не завершено. Проверьте файл или доступ к сети."; } finally { b.IsEnabled=true; } }; parent.Children.Add(b); }
    private void General()
    {
        var p = Page("Общие","Личная библиотека хранится на этом компьютере. Приложение не требует внешней учётной записи.");
        Check(p,"Включать последний канал при запуске","resume");
        var adult = Check(p,"Показывать категорию для взрослых, включая поиск и EPG","adult.enabled");
        adult.Click += (_, _) =>
        {
            if (adult.IsChecked != true || M.Setting("adult.pin").Length == 0) return;
            var pin = PinPrompt.Show(this,"Введите PIN");
            if (pin is null || !PinPrompt.Verify(pin,M.Setting("adult.pin"))) { adult.IsChecked=false; M.SetSetting("adult.enabled",false); _notice.Text="PIN не подтверждён."; }
        };
        Button(p,"Задать или изменить PIN",() =>
        {
            var old = M.Setting("adult.pin");
            if (old.Length > 0 && (PinPrompt.Show(this,"Текущий PIN") is not { } previous || !PinPrompt.Verify(previous,old))) { _notice.Text="PIN не подтверждён."; return Task.CompletedTask; }
            if (PinPrompt.Show(this,"Новый PIN (не менее 4 цифр)") is { Length: >=4 } pin && pin.All(char.IsDigit)) { M.SetSetting("adult.pin",PinPrompt.Hash(pin)); _notice.Text="PIN сохранён."; }
            return Task.CompletedTask;
        });
        Button(p,"Показать скрытые каналы",async () => { M.State.Hidden.Clear(); await M.SaveAsync(); await M.ReloadAsync(); _notice.Text="Скрытые каналы возвращены в библиотеку."; });
        Button(p,"Экспортировать избранное, подборки и EPG-сопоставления…",async () => { var d=new SaveFileDialog { FileName="DzhiggaTV-settings.json",Filter="Настройки JSON|*.json" }; if(d.ShowDialog(this)==true) { await M.SaveAsync(); await M.Store.ExportSettingsAsync(d.FileName); _notice.Text="Резервная копия создана без адресов трансляций и токенов."; } });
    }
    private void Appearance()
    {
        var p=Page("Внешний вид","Основной язык — русский. В интерфейсе используются системные шрифты Windows. Анимации не требуются для навигации.");
        Choice(p,"Тема","theme",[("Тёмная","dark"),("Светлая","light")],"dark",s=>Theme.Apply(s=="light"));
        Note(p,"Изменяйте ширину списка каналов, перетаскивая разделитель. Кнопка ☰ сворачивает левую навигацию. Интерфейс не использует обязательные анимации и учитывает reduced motion отсутствием движения.");
    }
    private void Remote()
    {
        var p = Page("Remote", "Смотрите телевизор на телефоне по домашнему Wi-Fi и управляйте плеером на компьютере. Мобильный просмотр не переключает канал на ПК.");
        Button(p, "Открыть DzhiggaTV Remote", () => { new RemoteSettingsWindow(_main, this).ShowDialog(); return Task.CompletedTask; });
        Note(p, "Remote выключен по умолчанию. Для подключения отсканируйте QR-код и подтвердите устройство. Внешний сервер и отдельная учётная запись не нужны.");
    }
    private void Playlists()
    {
        var p=Page("Плейлисты","Локальные M3U/M3U8 и сетевые плейлисты. При обновлении идентификатор источника сохраняется, избранное и ручные соответствия остаются на месте.");
        var list=new ListBox { Height=160,DisplayMemberPath="Name" }; p.Children.Add(list);
        async Task Reload() => list.ItemsSource=await M.Store.GetPlaylistSourcesAsync();
        Loaded+=async(_,_)=>await Reload();
        Button(p,"Добавить файл…",async()=> { var d=new OpenFileDialog { Filter="M3U/M3U8|*.m3u;*.m3u8" }; if(d.ShowDialog(this)==true) { await M.ImportAsync(d.FileName); await Reload(); } });
        Button(p,"Добавить URL…",async()=> { if(TextPrompt.Show(this,"Сетевой плейлист","URL плейлиста") is { } location) { await M.ImportAsync(location); await Reload(); } });
        Button(p,"Обновить выбранный",async()=> { if(list.SelectedItem is DzhiggaTV.Core.PlaylistSource source) { await M.ImportAsync(source.Location,source.Id); await Reload(); } });
        Button(p,"Удалить выбранный источник",async()=> { if(list.SelectedItem is DzhiggaTV.Core.PlaylistSource source) { await M.Store.RemovePlaylistAsync(source.Id); await M.ReloadAsync(); await Reload(); } });
    }
    private void Collections()
    {
        var p=Page("Избранное и подборки","Добавляйте каналы через ☆ или контекстное меню. Канал может входить в несколько подборок. В избранном и подборках порядок меняется перетаскиванием.");
        var list=new ListBox { Height=150,ItemsSource=M.State.Collections.Keys.ToArray() }; p.Children.Add(list);
        Button(p,"Создать подборку",async()=> { if(TextPrompt.Show(this,"Подборка","Название") is { } name) { M.State.Collections.TryAdd(name,[]); await M.SaveAsync(); list.ItemsSource=M.State.Collections.Keys.ToArray(); } });
        Button(p,"Переименовать выбранную",async()=> { if(list.SelectedItem is string old && TextPrompt.Show(this,"Подборка","Новое название",old) is { } name && name!=old && !M.State.Collections.ContainsKey(name)) { M.State.Collections[name]=M.State.Collections[old]; M.State.Collections.Remove(old); await M.SaveAsync(); list.ItemsSource=M.State.Collections.Keys.ToArray(); } });
        Button(p,"Удалить выбранную подборку",async()=> { if(list.SelectedItem is string name) { M.State.Collections.Remove(name); await M.SaveAsync(); list.ItemsSource=M.State.Collections.Keys.ToArray(); } });
    }
    private void Epg()
    {
        var p=Page("Телепрограмма EPG.ONE","Основной источник предустановлен: полный XMLTV с прозрачными прямоугольными логотипами. Обновление выполняется в фоне раз в сутки; при сбое остаётся последняя рабочая база.");
        Check(p,"Фоновое ежедневное обновление","epg.enabled");
        Choice(p,"Источник XMLTV","epg.source",[("Полный · прозрачные логотипы","https://epg.one/epg2.xml.gz"),("Полный · квадратные логотипы","https://epg.one/epg.xml.gz"),("Русскоязычный","https://epg.one/ru2.xml.gz")],"https://epg.one/epg2.xml.gz");
        Note(p,"Телепрограмма и логотипы EPG.ONE подключаются автоматически, включая публичное HTTP-зеркало самого сервиса. Адреса трансляций сервису не передаются. Дополнительное разрешение для EPG.ONE не требуется.");
        var http=Check(p,"Разрешить HTTP также для других источников логотипов","epg.http"); http.Click+=async(_,_)=> { M.Epg.AllowInsecureHttp=M.Logos.AllowInsecureHttp=http.IsChecked==true; await M.RefreshLogosAsync(); };
        Button(p,"Обновить сейчас",async()=> { await M.RefreshEpgAsync(); _notice.Text=M.Status; });
        Button(p,"Поправка времени для канала…",()=> { new EpgTimingWindow(_main, this, M.Active).ShowDialog(); return Task.CompletedTask; });
        Button(p,"Диагностика и ручное сопоставление",()=> { _tabs.SelectedIndex=10; return Task.CompletedTask; });
    }
    private void Logos()
    {
        var p=Page("Логотипы","Приоритет: ваш логотип → tvg-logo плейлиста → сопоставленный XMLTV → каталог EPG.ONE → локальный архив → текстовая заглушка. Изображения загружаются по мере прокрутки и сохраняются на диске.");
        Button(p,"Импортировать архив пиконов ZIP…",async()=> { var d=new OpenFileDialog { Filter="ZIP с пиконами|*.zip" }; if(d.ShowDialog(this)==true) { await using var stream=File.OpenRead(d.FileName); await M.Logos.ImportZipAsync(stream); await M.RefreshLogosAsync(); _notice.Text="Архив пиконов импортирован."; } });
        Button(p,"Загрузить прозрачные пиконы EPG.ONE",async()=> { await M.Logos.DownloadPiconsAsync(); await M.RefreshLogosAsync(); _notice.Text="Пиконы сохранены в дисковом кэше."; });
        Note(p,"Кэш ограничен 256 МиБ. Логотип конкретного канала можно заменить через правую кнопку мыши. Автоматическое обновление не заменяет пользовательский выбор.");
    }
    private void Video()
    {
        var p=Page("Видео и HDR","Вывод gpu-next / D3D11. D3D11VA включается, когда поток и видеокарта его поддерживают. HDR не объявляется активным только по названию канала.");
        Choice(p,"HDR и цвет","hdr",[("Автоматически по параметрам дисплея","Auto"),("Преобразовывать HDR в SDR (BT.709)","ToneMapToSdr")],"Auto",v=>_main.Player.SetHdrMode(Enum.Parse<HdrMode>(v)));
        Choice(p,"Соотношение сторон","aspect",[("По источнику","Original"),("16:9","Fit16By9"),("4:3","Fit4By3"),("Растянуть","Stretch")],"Original",v=>_main.Player.SetAspect(Enum.Parse<AspectMode>(v)));
        Note(p,"Для HDR10 / HLG требуется совместимая конфигурация Windows, дисплея, драйвера и потока. Статистика показывает метаданные источника и вывода. Аппаратная проверка HDR и поведения SDR на HDR-дисплее описана в ручном тест-плане.");
    }
    private void Audio()
    {
        var p=Page("Звук","Доступные дорожки появляются в меню ••• во время воспроизведения. Несуществующие дорожки не предлагаются.");
        Text(p,"Предпочтительные языки звука (через запятую)","audio.language","rus,ru,eng,en");
        Text(p,"Предпочтительные языки субтитров","subtitle.language","rus,ru");
        var devices=_main.Player.GetAudioDevices();
        Choice(p,"Устройство вывода","audio.device",devices.Count>0?devices.Select(d=>(d.Description,d.Name)).ToArray():[("Системное устройство","auto")],"auto",v=>_main.Player.SetAudioDevice(v));
        Button(p,"Применить языки",()=> { _main.Player.SetPreferredLanguages(M.Setting("audio.language"),M.Setting("subtitle.language")); return Task.CompletedTask; });
    }
    private void Controls()
    {
        var p=Page("Управление","Клавиатура и мышь работают в обычном, полноэкранном и компактном режимах.");
        Note(p,"F11 / Alt+Enter / двойной щелчок — полный экран\nEscape — вернуться в обычное окно\nSpace — пауза\nPgUp / PgDn — каналы\nBackspace — предыдущий просмотренный\n↑ / ↓ — громкость, M — звук\nF — избранное, Ctrl+F — поиск\nC — каналы поверх видео, G — программа\nI — информация о потоке, Ctrl+M — мини-плеер\n\nКомпактная панель управления появляется при наведении на видео и скрывается, когда курсор уходит. В полном экране управление и курсор дополнительно скрываются через 3,5 секунды без движения. Мини-плеер можно перемещать за видеоповерхность.");
    }
    private void Performance()
    {
        var p=Page("Производительность","Списки используют виртуализацию, логотипы — ограниченную фоновую очередь. Сетевой буфер регулируется средствами libmpv.");
        Choice(p,"Буфер","buffer",[("Минимальная задержка","LowLatency"),("Сбалансированный (стандарт mpv)","Balanced"),("Стабильное соединение","Stable")],"Balanced",v=>_main.Player.SetBufferPreset(Enum.Parse<BufferPreset>(v)));
        Note(p,"Минимальная задержка отключает предварительное чтение; стабильный режим включает cache-pause. Сбалансированный использует стандартные объёмы mpv. Численные размеры буфера не выдуманы; фактическое заполнение доступно в статистике.");
    }
    private void Diagnostics()
    {
        var p=Page("Диагностика","Покрытие рассчитывается по фактически найденным текущим передачам. Сомнительные совпадения требуют ручного выбора.");
        var text=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new(3,10,0,15) }; p.Children.Add(text);
        var list=new ListBox { Height=230,ItemsSource=M.AllChannels.Where(M.IsAllowed).ToArray(),DisplayMemberPath="Name" }; p.Children.Add(list);
        async Task Update()
        {
            await M.RefreshEpgViewAsync(); var channels=M.AllChannels.Where(M.IsAllowed).ToArray();
            var schedules=await M.Guide.GetNowNextBatchAsync(channels.Select(c=>M.EpgFor(c.Id)).OfType<ChannelEpg>());
            var covered=channels.Count(c=>schedules.GetValueOrDefault(c.Id)?.Current is not null);
            var logos=channels.Count(c=>M.State.UserLogos.ContainsKey(c.Id)||c.Channel.Logo is not null||M.Mappings.TryGetValue(c.Id,out var map)&&M.EpgChannels.GetValueOrDefault(map.EpgId)?.Logo is not null);
            text.Text=$"Всего каналов: {M.AllChannels.Count:N0}; видимых: {channels.Length:N0}\nС текущей программой: {covered:N0} ({(channels.Length>0?(double)covered/channels.Length:0):P1})\nС доступным логотипом: {logos:N0}\nСпорных: {M.Epg.Diagnostics.Ambiguous:N0}; без назначения: {channels.Count(c=>!M.Mappings.ContainsKey(c.Id)):N0}\nПоследнее обновление: {M.Epg.LastUpdated?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"ещё не выполнялось"}";
        }
        Button(p,"Обновить диагностику",Update);
        Button(p,"Назначить программу выбранному каналу",async()=> { if(list.SelectedItem is ChannelItem item) { new MappingWindow(_main,item) { Owner=this }.ShowDialog(); await Update(); } });
        Loaded+=async(_,_)=>await Update();
    }
    private void About()
    {
        var p=Page("О программе",$"DzhiggaTV {App.DisplayVersion}\nIPTV-плеер для Windows 11 x64.");
        Note(p,"C# / .NET 10 · WPF · libmpv · SQLite\n\nАдреса трансляций и источников зашифрованы Windows DPAPI для текущего пользователя. Они не записываются в журналы и не входят в экспорт настроек.\n\nEPG.ONE — публичная программа и логотипы. Приложение не предоставляет права на IPTV-контент и не обещает телеархив или запись.\n\nЛицензии и источники зависимостей приложены к дистрибутиву. Сборка не подписана цифровым сертификатом.\n\nРасположение данных: "+M.DataDirectory);
    }
}

internal sealed class PinPrompt : Window
{
    private readonly PasswordBox _value=new() { Margin=new(0,15,0,15) };
    private PinPrompt(Window owner,string label)
    { Theme.Window(this);Owner=owner;Title="DzhiggaTV · PIN";Width=360;Height=210;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;var p=new StackPanel { Margin=new(24) };Content=p;p.Children.Add(new TextBlock { Text=label });p.Children.Add(_value);var b=new Button { Content="Подтвердить",IsDefault=true };b.Click+=(_,_)=>DialogResult=true;p.Children.Add(b);Loaded+=(_,_)=>_value.Focus(); }
    public static string? Show(Window owner,string label) { var d=new PinPrompt(owner,label);return d.ShowDialog()==true?d._value.Password:null; }
    public static string Hash(string pin) { var salt=RandomNumberGenerator.GetBytes(16);var hash=Rfc2898DeriveBytes.Pbkdf2(pin,salt,150000,HashAlgorithmName.SHA256,32);return Convert.ToBase64String(salt)+":"+Convert.ToBase64String(hash); }
    public static bool Verify(string pin,string encoded) { try { var parts=encoded.Split(':');return CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(pin,Convert.FromBase64String(parts[0]),150000,HashAlgorithmName.SHA256,32),Convert.FromBase64String(parts[1])); } catch { return false; } }
}
