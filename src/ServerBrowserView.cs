using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml;

namespace SquadTools
{
    public sealed class ServerBrowserView : Border, IDisposable
    {
        private readonly EosClient client;
        private readonly Func<DateTime> utcNow;
        private readonly Func<ServerRecord, string, CancellationToken, Task<string>> launchJoin;
        private readonly CountEvidence evidence = new CountEvidence();
        private readonly HashSet<string> favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool testMode;
        private readonly string favoritePath;
        private readonly DataGrid table;
        private readonly TextBox search;
        private readonly ComboBox kind;
        private readonly CheckBox favoritesOnly, populated, fold, hideMismatch, autoCheck;
        private readonly TextBlock summary, status, detail;
        private readonly Button refresh, check, favorite, join, cancel;
        private readonly DispatcherTimer timer;
        private CancellationTokenSource operation, joinOperation;
        private bool active, busy, joining, disposed, refreshPending, favoritesReadable = true;
        private DateTime lastAttempt, lastJoin;
        private ServerSnapshot snapshot = new ServerSnapshot();
        private string followedSession;
        public event Action<ServerRecord> ServerChosen;
        public event Action<ServerRecord> FollowedServerUpdated;
        public bool HasRequestedNetwork { get; private set; }
        public ServerBrowserView(bool testing) : this(testing, new EosClient(), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "server-favorites.xml"), () => DateTime.UtcNow, SteamJoinBridge.Launch) { }

        internal ServerBrowserView(bool testing, EosClient eosClient, string favoritesFile, Func<DateTime> clock, Func<ServerRecord, string, CancellationToken, Task<string>> joinLauncher)
        {
            testMode = testing; client = eosClient; favoritePath = favoritesFile; utcNow = clock; launchJoin = joinLauncher;
            Child = (UIElement)XamlReader.Parse(Layout);
            var root = (FrameworkElement)Child;
            table = (DataGrid)root.FindName("Servers"); search = (TextBox)root.FindName("Search"); kind = (ComboBox)root.FindName("Kind");
            favoritesOnly = (CheckBox)root.FindName("FavoritesOnly"); populated = (CheckBox)root.FindName("Populated"); fold = (CheckBox)root.FindName("Fold"); hideMismatch = (CheckBox)root.FindName("HideMismatch"); autoCheck = (CheckBox)root.FindName("AutoCheck");
            summary = (TextBlock)root.FindName("Summary"); status = (TextBlock)root.FindName("Status"); detail = (TextBlock)root.FindName("Detail");
            refresh = (Button)root.FindName("Refresh"); check = (Button)root.FindName("Check"); favorite = (Button)root.FindName("Favorite"); join = (Button)root.FindName("Join"); cancel = (Button)root.FindName("Cancel");
            kind.ItemsSource = new[] { "全部", "许可服", "自定义", "Fireteam" }; kind.SelectedIndex = 0;
            search.TextChanged += delegate { ApplyFilter(); }; kind.SelectionChanged += delegate { ApplyFilter(); };
            foreach (var box in new[] { favoritesOnly, populated, fold, hideMismatch }) { box.Checked += delegate { ApplyFilter(); }; box.Unchecked += delegate { ApplyFilter(); }; }
            refresh.Click += async delegate { await Refresh(); };
            check.Click += async delegate { await CheckSelected(); };
            cancel.Click += delegate { refreshPending = false; if (operation != null) operation.Cancel(); if (joinOperation != null) joinOperation.Cancel(); };
            table.SelectionChanged += delegate { ShowDetail(); };
            table.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(delegate(object sender, MouseButtonEventArgs e) {
                if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(table, e.OriginalSource as DependencyObject) is DataGridRow) ChooseSelected();
            }), true);
            table.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ChooseSelected(); e.Handled = true; } };
            table.SizeChanged += delegate {
                double others = table.Columns.Where((column, index) => index != 1).Sum(column => column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth);
                double width = Math.Max(220, table.ActualWidth - others - 20);
                if (Math.Abs(table.Columns[1].ActualWidth - width) > 1) table.Columns[1].Width = new DataGridLength(width);
            };
            favorite.Click += delegate { ToggleFavorite(); };
            join.Click += async delegate { await JoinSelected(); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async delegate { await Pulse(); };
            if (!testMode) LoadFavorites();
            ShowDetail();
        }

        public async void SetActive(bool value)
        {
            if (disposed) return;
            active = value;
            if (!value) { if (followedSession == null) timer.Stop(); if (operation != null) operation.Cancel(); if (joinOperation != null) joinOperation.Cancel(); return; }
            ApplyFilter();
            if (!testMode) {
                timer.Start();
                refreshPending = snapshot.RetrievedAt == DateTime.MinValue || utcNow() - snapshot.RetrievedAt > TimeSpan.FromSeconds(60);
                await Pulse();
            }
        }

        private void ChooseSelected()
        {
            var selected = table.SelectedItem as ServerRecord;
            if (!disposed && selected != null && ServerChosen != null) ServerChosen(selected);
        }

        public void FollowServer(string sessionId)
        {
            followedSession = sessionId;
            if (!testMode && !disposed && (active || followedSession != null)) timer.Start();
            else if (!active) { timer.Stop(); if (operation != null) operation.Cancel(); }
        }

        public void ShowMapSyncMessage(string message) { status.Text = message; }

        private async Task Pulse()
        {
            if (disposed || !active && followedSession == null || testMode) return;
            if (ExpireEvidence()) ApplyFilter();
            UpdateButtons();
            var elapsed = utcNow() - lastAttempt;
            if (!busy && !joining && (elapsed >= TimeSpan.FromSeconds(60) || refreshPending && elapsed >= TimeSpan.FromSeconds(10))) await Refresh();
        }

        public async Task Refresh()
        {
            if (busy || joining || disposed || testMode) return;
            if (utcNow() - lastAttempt < TimeSpan.FromSeconds(10)) { status.Text = "刚刚查询过，请稍等几秒再刷新。"; return; }
            refreshPending = false; lastAttempt = utcNow(); Begin();
            try
            {
                status.Text = "正在获取服务器列表…"; HasRequestedNetwork = true;
                var next = await client.GetServers(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                var previous = snapshot.Servers.ToDictionary(s => s.SessionId);
                foreach (var server in next.Servers) {
                    server.Favorite = favorites.Contains(server.FavoriteKey); ServerRecord old;
                    if (previous.TryGetValue(server.SessionId, out old)) {
                        if (server.Reported != old.Reported) evidence.Reset(server.SessionId);
                        if (utcNow() - old.ObservedAt < TimeSpan.FromSeconds(180)) { server.Observed = old.Observed; server.ObservedAt = old.ObservedAt; server.CheckState = server.Reported == old.Reported ? old.CheckState : "人数已变化 · 待复核"; server.PersistentMismatch = old.PersistentMismatch && server.Reported == old.Reported; }
                    }
                }
                snapshot = next; evidence.Prune(next.Servers.Select(s => s.SessionId)); ApplyFilter();
                status.Text = "列表已更新。人数来自不同口径，核验结果不等同于真人活跃人数。";
                if (followedSession != null && FollowedServerUpdated != null) FollowedServerUpdated(snapshot.Servers.Find(s => s.SessionId == followedSession));
                if ((active || followedSession == null) && autoCheck.IsChecked == true) {
                    var candidates = ServerData.Filter(snapshot.Servers, search.Text, (string)kind.SelectedItem, favoritesOnly.IsChecked == true, populated.IsChecked == true, fold.IsChecked == true, false)
                        .Where(s => s.Reported.GetValueOrDefault() > 0).Take(10).ToList();
                    await Verify(candidates, operation.Token);
                }
            }
            catch (OperationCanceledException) { status.Text = "查询已取消或超时；已取得的列表保留。"; }
            catch (Exception ex) { status.Text = "刷新未完成：" + ErrorText(ex) + (snapshot.Servers.Count > 0 ? " 当前显示上次取得的数据。" : " 点击刷新可重试。"); }
            finally { End(); }
        }

        private async Task Verify(List<ServerRecord> targets, CancellationToken cancellation)
        {
            for (int i = 0; i < targets.Count; i += 5)
            {
                status.Text = "正在核验可见成员（" + (i + 1) + "–" + Math.Min(i + 5, targets.Count) + " / " + targets.Count + "）…";
                var batch = targets.Skip(i).Take(5).ToList();
                var counts = await client.GetRosterCounts(batch.Select(s => s.SessionId), cancellation);
                cancellation.ThrowIfCancellationRequested();
                foreach (var server in batch) evidence.Apply(server, counts[server.SessionId], utcNow());
                ApplyFilter();
            }
            status.Text = targets.Count == 0 ? "没有需要核验的服务器。" : "已核验 " + targets.Count + " 台。持续差异需要间隔至少 20 秒的两次样本；没有成员记录时显示未知。";
        }

        private async Task CheckSelected()
        {
            var selected = table.SelectedItem as ServerRecord;
            if (busy || joining || selected == null || testMode || disposed) return;
            if (utcNow() - selected.ObservedAt < TimeSpan.FromSeconds(20)) { status.Text = "刚刚核验过这台服务器，请至少间隔 20 秒再核验。"; return; }
            if (utcNow() - selected.RetrievedAt > TimeSpan.FromMinutes(2)) { status.Text = "列表较旧，请先刷新再核验人数。"; return; }
            Begin();
            try { HasRequestedNetwork = true; await Verify(new List<ServerRecord> { selected }, operation.Token); }
            catch (OperationCanceledException) { status.Text = "人数核验已取消或超时，未改写为 0 人。"; }
            catch (Exception ex) { status.Text = "人数核验失败：" + ErrorText(ex); }
            finally { End(); }
        }

        private void Begin() { busy = true; operation = new CancellationTokenSource(); UpdateButtons(); }
        private void End() { busy = false; if (operation != null) { operation.Dispose(); operation = null; } if (!disposed) UpdateButtons(); else client.Dispose(); }
        private static string ErrorText(Exception ex) { return ex is IOException || ex is FormatException ? ex.Message : "网络连接失败，请检查网络后重试。"; }

        private bool ExpireEvidence()
        {
            bool changed = false;
            foreach (var server in snapshot.Servers) {
                if (server.ObservedAt == DateTime.MinValue || utcNow() - server.ObservedAt <= TimeSpan.FromSeconds(180) || server.CheckState == "核验已过期") continue;
                server.PersistentMismatch = false; server.Observed = null; server.CheckState = "核验已过期"; evidence.Reset(server.SessionId); changed = true;
            }
            return changed;
        }

        private void ApplyFilter()
        {
            if (table == null || kind.SelectedItem == null) return;
            string selectedId = (table.SelectedItem as ServerRecord) == null ? null : ((ServerRecord)table.SelectedItem).SessionId;
            ExpireEvidence();
            var rows = ServerData.Filter(snapshot.Servers, search.Text, (string)kind.SelectedItem, favoritesOnly.IsChecked == true, populated.IsChecked == true, fold.IsChecked == true, hideMismatch.IsChecked == true);
            var sorting = table.Items.SortDescriptions.ToArray();
            var directions = table.Columns.Select(column => column.SortDirection).ToArray();
            table.ItemsSource = rows;
            foreach (var sort in sorting) table.Items.SortDescriptions.Add(sort);
            for (int i = 0; i < directions.Length; i++) table.Columns[i].SortDirection = directions[i];
            table.SelectedItem = rows.Find(s => s.SessionId == selectedId);
            if (table.SelectedItem == null && rows.Count > 0) table.SelectedIndex = 0;
            int aliases = snapshot.Servers.Where(s => s.Endpoint != "").GroupBy(s => s.Endpoint).Sum(g => Math.Max(0, g.Count() - 1));
            summary.Text = snapshot.RetrievedAt == DateTime.MinValue ? "进入此页后联网查询；迫击炮仍可离线使用。" : string.Format("显示 {0} / {1} 台  ·  相同连接入口多余记录 {2} 条  ·  更新 {3:HH:mm:ss}{4}", rows.Count, snapshot.Servers.Count, aliases, snapshot.RetrievedAt.ToLocalTime(), snapshot.Partial ? "  ·  部分结果" : "");
            ShowDetail();
        }

        private void ShowDetail()
        {
            var server = table.SelectedItem as ServerRecord;
            if (server == null) detail.Text = "选择服务器后查看连接地址、数据来源和核验时间。";
            else detail.Text = server.Name + "\n" + server.Kind + "  ·  " + (server.Endpoint == "" ? "连接地址未提供" : server.Endpoint) + "  ·  " + server.Version + (server.Password ? "  ·  需要密码" : "") +
                "\n上报 " + ServerRecord.Value(server.Reported) + " 人 / EOS 登记 " + ServerRecord.Value(server.Registered) + " 人 / 可见成员 " + ServerRecord.Value(server.Observed) + " 人。公开容量 " + ServerRecord.Value(server.Capacity) + "；排队：普通 " + ServerRecord.Value(server.Queue) + "，预留 " + ServerRecord.Value(server.ReservedQueue) + "。" +
                "\n" + (server.ObservedAt == DateTime.MinValue ? "此服务器尚未核验。" : "最近核验 " + server.ObservedAt.ToLocalTime().ToString("HH:mm:ss") + " · " + server.CheckState + "。") +
                (server.DuplicateCount > 1 ? " 有 " + server.DuplicateCount + " 条记录指向相同连接入口，可取消折叠查看；尚不能确认原服身份。" : "") +
                "\n点击服务器行或按 Enter 将射击地图同步到该服务器。可见成员可能缺失或滞后；加入功能为实验性，尚未完成游戏内验收。";
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var selected = table.SelectedItem as ServerRecord;
            refresh.IsEnabled = !busy && !joining; cancel.IsEnabled = busy || joining; check.IsEnabled = !busy && !joining && selected != null;
            cancel.Content = joining ? "取消加入" : "取消查询";
            favorite.IsEnabled = selected != null && favoritesReadable; favorite.Content = selected != null && selected.Favorite ? "取消收藏" : "收藏服务器";
            favorite.ToolTip = favoritesReadable ? null : "收藏文件无法读取，已暂停写入以保留原文件；修复文件后重启可恢复。";
            join.IsEnabled = selected != null && !busy && !joining && utcNow() - lastJoin >= TimeSpan.FromSeconds(90);
        }

        private void LoadFavorites()
        {
            if (!File.Exists(favoritePath)) return;
            try { var doc = new XmlDocument { XmlResolver = null }; using (var reader = XmlReader.Create(favoritePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 })) doc.Load(reader); if (doc.DocumentElement == null || doc.DocumentElement.Name != "favorites") throw new FormatException(); foreach (XmlNode node in doc.SelectNodes("/favorites/server")) if (node.InnerText.Length <= 150) favorites.Add(node.InnerText); }
            catch (Exception) { favoritesReadable = false; status.Text = "收藏文件无法读取，已暂停收藏写入并保留原文件；修复文件后重启可恢复。"; }
        }
        private void ToggleFavorite()
        {
            var server = table.SelectedItem as ServerRecord; if (server == null || !favoritesReadable) return;
            bool next = !server.Favorite;
            if (next) favorites.Add(server.FavoriteKey); else favorites.Remove(server.FavoriteKey);
            string temp = null;
            try {
                if (!testMode) {
                    Directory.CreateDirectory(Path.GetDirectoryName(favoritePath)); temp = favoritePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) })) { writer.WriteStartElement("favorites"); foreach (string key in favorites.OrderBy(k => k)) writer.WriteElementString("server", key); writer.WriteEndElement(); }
                    if (File.Exists(favoritePath)) File.Replace(temp, favoritePath, null); else File.Move(temp, favoritePath);
                }
                foreach (var item in snapshot.Servers) item.Favorite = favorites.Contains(item.FavoriteKey);
                ApplyFilter();
            } catch (Exception) { if (next) favorites.Remove(server.FavoriteKey); else favorites.Add(server.FavoriteKey); status.Text = "收藏保存失败，请确认软件目录可写。"; }
            finally { if (temp != null) { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        }

        private async Task JoinSelected()
        {
            var server = table.SelectedItem as ServerRecord; if (server == null || busy || joining || testMode || disposed || utcNow() - lastJoin < TimeSpan.FromSeconds(90)) return;
            if (utcNow() - server.RetrievedAt > TimeSpan.FromMinutes(2)) { status.Text = "列表较旧，请刷新后再加入。"; return; }
            string build = SteamJoinBridge.BuildId(server.Version);
            if (build == null) { status.Text = "未识别此服务器的游戏版本，暂不能生成加入入口。"; return; }
            joining = true; joinOperation = new CancellationTokenSource(); UpdateButtons(); status.Text = "正在准备 Steam 加入入口…";
            try {
                string result = await launchJoin(server, build, joinOperation.Token);
                if (result == "LAUNCHED") lastJoin = utcNow();
                if (!disposed) status.Text = result == "LAUNCHED" ? "已向 Steam 发起加入，请查看游戏。入口保留 90 秒；这不代表已成功进服。" : result != null && result.StartsWith("ERROR:") ? result.Substring(6) : "加入入口未能启动，请确认 Steam 已运行。";
            } catch (OperationCanceledException) { if (!disposed) status.Text = "加入准备已取消。"; }
            catch (Exception) { if (!disposed) status.Text = "加入入口启动失败，请确认 Steam 已安装并运行。"; }
            finally { joining = false; joinOperation.Dispose(); joinOperation = null; if (!disposed) UpdateButtons(); }
        }

        public void LoadSmokeSnapshot(ServerSnapshot sample)
        {
            if (!testMode) throw new InvalidOperationException("Test-only path.");
            snapshot = sample; ApplyFilter();
            if (sample.Servers.Count == 0 || table.Items.Count == 0) throw new InvalidOperationException("Server table is empty.");
            var first = (ServerRecord)table.Items[0]; search.Text = "no_match_" + Guid.NewGuid().ToString("N");
            if (table.Items.Count != 0 || join.IsEnabled) throw new InvalidOperationException("Search/empty state failed.");
            search.Text = ""; kind.SelectedItem = "全部"; populated.IsChecked = false;
            ToggleFavorite(); favoritesOnly.IsChecked = true;
            if (table.Items.Count == 0) throw new InvalidOperationException("Favorite filter failed.");
            favoritesOnly.IsChecked = false; ToggleFavorite();
            fold.IsChecked = false; fold.IsChecked = true;
            if (HasRequestedNetwork) throw new InvalidOperationException("Smoke test performed network requests.");
            status.Text = "验证预览 · " + (sample.RetrievedAt == DateTime.MinValue ? "离线测试数据" : "已取得的数据快照，非实时刷新");
        }

        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); if (operation != null) operation.Cancel(); if (joinOperation != null) joinOperation.Cancel(); if (!busy) client.Dispose(); }

        private const string Layout = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Grid.Resources>
  <Style TargetType='CheckBox'><Setter Property='Foreground' Value='#AFC1CC'/><Setter Property='FontSize' Value='11'/><Setter Property='Margin' Value='0,0,18,0'/><Setter Property='VerticalAlignment' Value='Center'/></Style>
  <Style TargetType='DataGridColumnHeader'><Setter Property='Background' Value='#203340'/><Setter Property='Foreground' Value='#AFC1CC'/><Setter Property='Padding' Value='8,12'/><Setter Property='BorderThickness' Value='0'/></Style>
  <Style TargetType='DataGridRow'><Setter Property='Background' Value='#14212C'/><Setter Property='Foreground' Value='#DCE5E8'/><Setter Property='MinHeight' Value='42'/><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='#304D41'/><Setter Property='Foreground' Value='#FFFFFF'/></Trigger></Style.Triggers></Style>
  <Style TargetType='DataGridCell'><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='7,5'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='DataGridCell'><Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter VerticalAlignment='Center'/></Border></ControlTemplate></Setter.Value></Setter><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='#304D41'/><Setter Property='Foreground' Value='#FFFFFF'/></Trigger></Style.Triggers></Style>
 </Grid.Resources>
 <Grid.RowDefinitions><RowDefinition Height='64'/><RowDefinition Height='82'/><RowDefinition Height='51'/><RowDefinition Height='*'/><RowDefinition Height='170'/></Grid.RowDefinitions>
  <DockPanel LastChildFill='False'><StackPanel DockPanel.Dock='Left'><TextBlock Text='服务器列表' FontSize='27' FontWeight='SemiBold'/><TextBlock Text='点击服务器同步射击地图；每 60 秒跟随换图，可在射击页停止跟随。' Foreground='#8298A8' FontSize='12' Margin='0,7,0,0'/></StackPanel><StackPanel DockPanel.Dock='Right' Orientation='Horizontal' VerticalAlignment='Center'><Button x:Name='Cancel' Content='取消查询' Margin='0,0,10,0'/><Button x:Name='Refresh' Content='刷新列表' Background='#B8EB7E' Foreground='#16251B'/></StackPanel></DockPanel>
 <StackPanel Grid.Row='1'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='130'/></Grid.ColumnDefinitions><Grid Margin='0,0,12,0'><TextBox x:Name='Search' ToolTip='搜索服务器名称、地图、模式或 IP' FontSize='13' Padding='10,7'/><TextBlock Text='搜索服务器名称 / 地图 / 模式 / IP' IsHitTestVisible='False' Foreground='#728A9B' FontSize='12' VerticalAlignment='Center' Margin='11,0'><TextBlock.Style><Style TargetType='TextBlock'><Setter Property='Visibility' Value='Collapsed'/><Style.Triggers><DataTrigger Binding='{Binding Text, ElementName=Search}' Value=''><Setter Property='Visibility' Value='Visible'/></DataTrigger></Style.Triggers></Style></TextBlock.Style></TextBlock></Grid><ComboBox x:Name='Kind' Grid.Column='1' TextElement.Foreground='#EDF4F3'><ComboBox.ItemTemplate><DataTemplate><TextBlock Text='{Binding}' Foreground='#EDF4F3'/></DataTemplate></ComboBox.ItemTemplate></ComboBox></Grid><WrapPanel Margin='0,12,0,0'><CheckBox x:Name='FavoritesOnly' Content='只看收藏'/><CheckBox x:Name='Populated' Content='隐藏上报空服'/><CheckBox x:Name='Fold' Content='折叠相同连接入口' IsChecked='True'/><CheckBox x:Name='HideMismatch' Content='隐藏持续人数差异' ToolTip='仅隐藏经过连续核验的显著差异；不能等同于已确认阴兵'/><CheckBox x:Name='AutoCheck' Content='刷新后核验前 10 台' IsChecked='True'/></WrapPanel></StackPanel>
 <StackPanel Grid.Row='2'><TextBlock x:Name='Summary' Foreground='#B8EB7E' FontSize='11'/><TextBlock x:Name='Status' Text='列表每 60 秒刷新；离开此页暂停查询。' Foreground='#8298A8' FontSize='11' Margin='0,7,0,0' TextTrimming='CharacterEllipsis'/></StackPanel>
 <DataGrid x:Name='Servers' Grid.Row='3' AutoGenerateColumns='False' IsReadOnly='True' CanUserAddRows='False' CanUserDeleteRows='False' CanUserReorderColumns='False' EnableRowVirtualization='True' EnableColumnVirtualization='True' SelectionMode='Single' SelectionUnit='FullRow' HeadersVisibility='Column' GridLinesVisibility='Horizontal' HorizontalGridLinesBrush='#243541' BorderBrush='#283B49' Background='#101B24' RowHeaderWidth='0' FontSize='11' ScrollViewer.CanContentScroll='True'>
  <DataGrid.Columns><DataGridTextColumn Header='★' Binding='{Binding FavoriteText}' Width='32'/><DataGridTextColumn Header='服务器' Binding='{Binding Name}' Width='*' MinWidth='220'/><DataGridTextColumn Header='地图' Binding='{Binding Map}' Width='160'/><DataGridTextColumn Header='上报人数' Binding='{Binding PlayersText}' SortMemberPath='Reported' Width='90'/><DataGridTextColumn Header='EOS登记' Binding='{Binding RegisteredText}' SortMemberPath='Registered' Width='72'/><DataGridTextColumn Header='可见成员' Binding='{Binding ObservedText}' SortMemberPath='Observed' Width='72'/><DataGridTextColumn Header='排队 / 预留' Binding='{Binding QueueText}' Width='83'/><DataGridTextColumn Header='核验状态' Binding='{Binding StatusText}' Width='172'/></DataGrid.Columns>
 </DataGrid>
 <Border Grid.Row='4' Background='#14212C' BorderBrush='#283B49' BorderThickness='1' CornerRadius='6' Padding='14' Margin='0,12,0,0'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='146'/></Grid.ColumnDefinitions><ScrollViewer VerticalScrollBarVisibility='Auto'><TextBlock x:Name='Detail' TextWrapping='Wrap' FontSize='11' LineHeight='22' Foreground='#AFC1CC' Margin='0,0,12,0'/></ScrollViewer><StackPanel Grid.Column='1'><Button x:Name='Join' Content='加入（实验性）' Background='#B8EB7E' Foreground='#16251B' Margin='0,0,0,8'/><Button x:Name='Check' Content='核验所选人数' Margin='0,0,0,8'/><Button x:Name='Favorite' Content='收藏服务器'/></StackPanel></Grid></Border>
</Grid>";
    }
}
