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
        private readonly CountEvidence evidence = new CountEvidence();
        private readonly HashSet<string> favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool testMode;
        private readonly string favoritePath;
        private readonly DataGrid table;
        private readonly TextBox search;
        private readonly ComboBox kind;
        private readonly CheckBox favoritesOnly, populated, fold, hideMismatch, autoCheck;
        private readonly TextBlock summary, status, detail, followStatus;
        private readonly TextBlock detailTitle, detailMeta, detailReported, detailRegistered, detailObserved, detailQueue, detailInfo;
        private readonly Button refresh, check, favorite, cancel, follow;
        private readonly DispatcherTimer timer;
        private CancellationTokenSource operation;
        private bool active, busy, disposed, refreshPending, favoritesReadable = true;
        private DateTime lastAttempt;
        private ServerSnapshot snapshot = new ServerSnapshot();
        private string followedSession;
        public event Action<ServerRecord> ServerChosen;
        public event Action FollowStopped;
        public event Action<ServerRecord> FollowedServerUpdated;
        public bool HasRequestedNetwork { get; private set; }
        public ServerBrowserView(bool testing) : this(testing, new EosClient(), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "server-favorites.xml"), () => DateTime.UtcNow) { }

        internal ServerBrowserView(bool testing, EosClient eosClient, string favoritesFile, Func<DateTime> clock)
        {
            testMode = testing; client = eosClient; favoritePath = favoritesFile; utcNow = clock;
            Child = (UIElement)XamlReader.Parse(Layout);
            var root = (FrameworkElement)Child;
            table = (DataGrid)root.FindName("Servers"); search = (TextBox)root.FindName("Search"); kind = (ComboBox)root.FindName("Kind");
            favoritesOnly = (CheckBox)root.FindName("FavoritesOnly"); populated = (CheckBox)root.FindName("Populated"); fold = (CheckBox)root.FindName("Fold"); hideMismatch = (CheckBox)root.FindName("HideMismatch"); autoCheck = (CheckBox)root.FindName("AutoCheck");
            summary = (TextBlock)root.FindName("Summary"); status = (TextBlock)root.FindName("Status"); detail = (TextBlock)root.FindName("Detail");
            detailTitle=(TextBlock)root.FindName("DetailTitle"); detailMeta=(TextBlock)root.FindName("DetailMeta");
            detailReported=(TextBlock)root.FindName("DetailReported"); detailRegistered=(TextBlock)root.FindName("DetailRegistered"); detailObserved=(TextBlock)root.FindName("DetailObserved"); detailQueue=(TextBlock)root.FindName("DetailQueue"); detailInfo=(TextBlock)root.FindName("DetailInfo");
            followStatus = (TextBlock)root.FindName("FollowStatus"); follow = (Button)root.FindName("Follow");
            refresh = (Button)root.FindName("Refresh"); check = (Button)root.FindName("Check"); favorite = (Button)root.FindName("Favorite"); cancel = (Button)root.FindName("Cancel");
            kind.ItemsSource = new[] { "全部", "许可服", "自定义", "Fireteam" }; kind.SelectedIndex = 0;
            search.TextChanged += delegate { ApplyFilter(); }; kind.SelectionChanged += delegate { ApplyFilter(); };
            foreach (var box in new[] { favoritesOnly, populated, fold, hideMismatch }) { box.Checked += delegate { ApplyFilter(); }; box.Unchecked += delegate { ApplyFilter(); }; }
            refresh.Click += async delegate { await Refresh(); };
            check.Click += async delegate { await CheckSelected(); };
            cancel.Click += delegate { refreshPending = false; if (operation != null) operation.Cancel(); };
            table.SelectionChanged += delegate { ShowDetail(); };
            follow.Click += delegate {
                var selected = table.SelectedItem as ServerRecord;
                if (selected != null && selected.SessionId == followedSession) { if (FollowStopped != null) FollowStopped(); }
                else ChooseSelected();
            };
            table.SizeChanged += delegate {
                double others = table.Columns.Where((column, index) => index != 1).Sum(column => column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth);
                double width = Math.Max(220, table.ActualWidth - others - 20);
                if (Math.Abs(table.Columns[1].ActualWidth - width) > 1) table.Columns[1].Width = new DataGridLength(width);
            };
            favorite.Click += delegate { ToggleFavorite(); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async delegate { await Pulse(); };
            if (!testMode) LoadFavorites();
            ShowDetail();
        }

        public async void SetActive(bool value)
        {
            if (disposed) return;
            active = value;
            if (!value) { if (followedSession == null) timer.Stop(); if (operation != null) operation.Cancel(); return; }
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

        public void FollowServer(string sessionId, string serverName = null)
        {
            followedSession = sessionId;
            followStatus.Text = sessionId == null ? "未启用地图跟随" : "跟随 · " + (serverName ?? "所选服务器");
            followStatus.ToolTip=sessionId==null?"选中服务器后，可单独启用地图跟随。":"已启用跟随："+(serverName??"所选服务器")+"。手动进入地图页面后同步；非 RAAS 模式下猜点保持手动。";
            UpdateButtons();
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
            if (!busy && (elapsed >= TimeSpan.FromSeconds(60) || refreshPending && elapsed >= TimeSpan.FromSeconds(10))) await Refresh();
        }

        public async Task Refresh()
        {
            if (busy || disposed || testMode) return;
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
            if (busy || selected == null || testMode || disposed) return;
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
            summary.Text = snapshot.RetrievedAt == DateTime.MinValue ? "尚未刷新" : string.Format("{0} / {1} 台 · {2:HH:mm} 更新{3}",rows.Count,snapshot.Servers.Count,snapshot.RetrievedAt.ToLocalTime(),snapshot.Partial?" · 部分结果":"");
            summary.ToolTip="相同连接入口的多余记录："+aliases+"。上报人数、EOS 登记和可见成员为不同口径，均不能证明真人活跃。";
            ShowDetail();
        }

        private void ShowDetail()
        {
            var server = table.SelectedItem as ServerRecord;
            detailTitle.Text=server==null?"选择服务器":server.Name;
            detailMeta.Text=server==null?"点选列表查看状态":server.Map+" · "+server.TeamsText;
            detailReported.Text=ServerRecord.Value(server==null?null:server.Reported); detailRegistered.Text=ServerRecord.Value(server==null?null:server.Registered);
            detailObserved.Text=ServerRecord.Value(server==null?null:server.Observed); detailQueue.Text=server==null?"—":server.QueueText;
            detail.Text=server==null?"地图跟随需单独启用":(server.CheckState??"待核验")+(server.ObservedAt==DateTime.MinValue?"":" · "+server.ObservedAt.ToLocalTime().ToString("HH:mm"))+(server.DuplicateCount>1?" · 同入口 ×"+server.DuplicateCount:"");
            string full=server==null?"选中服务器后查看状态，点击启用地图跟随后再手动切换页面。":server.Name + "\n" + server.Kind + "  ·  " + (server.Endpoint == "" ? "连接地址未提供" : server.Endpoint) + "  ·  " + server.Version + (server.Password ? "  ·  需要密码" : "") +
                "\n图层："+server.Map+
                "\n上报 " + ServerRecord.Value(server.Reported) + " 人 / EOS 登记 " + ServerRecord.Value(server.Registered) + " 人 / 可见成员 " + ServerRecord.Value(server.Observed) + " 人。公开容量 " + ServerRecord.Value(server.Capacity) + "；排队：普通 " + ServerRecord.Value(server.Queue) + "，预留 " + ServerRecord.Value(server.ReservedQueue) + "。" +
                "\n" + (server.ObservedAt == DateTime.MinValue ? "此服务器尚未核验。" : "最近核验 " + server.ObservedAt.ToLocalTime().ToString("HH:mm:ss") + " · " + server.CheckState + "。") +
                (server.DuplicateCount > 1 ? " 有 " + server.DuplicateCount + " 条记录指向相同连接入口，可取消折叠查看；尚不能确认原服身份。" : "") +
                "\n对阵：" + server.TeamsText + "。\n可见成员可能缺失或滞后，不等同于真人活跃人数。";
            foreach(var block in new[]{detailTitle,detailMeta,detail,detailInfo}) block.ToolTip=new TextBlock{Text=full,TextWrapping=TextWrapping.Wrap,MaxWidth=490,FontSize=12,LineHeight=21};
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var selected = table.SelectedItem as ServerRecord;
            refresh.IsEnabled = !busy; cancel.IsEnabled = busy; check.IsEnabled = !busy && selected != null;
            favorite.IsEnabled = selected != null && favoritesReadable; favorite.Content = selected != null && selected.Favorite ? "已收藏" : "收藏";
            follow.IsEnabled = selected != null && !disposed;
            follow.Content = selected != null && selected.SessionId == followedSession ? "停止跟随此服务器" : followedSession == null ? "启用地图跟随" : "改为跟随所选服务器";
            favorite.ToolTip = favoritesReadable ? null : "收藏文件无法读取，已暂停写入以保留原文件；修复文件后重启可恢复。";
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

        public void LoadSmokeSnapshot(ServerSnapshot sample)
        {
            if (!testMode) throw new InvalidOperationException("Test-only path.");
            snapshot = sample; ApplyFilter();
            if (sample.Servers.Count == 0 || table.Items.Count == 0) throw new InvalidOperationException("Server table is empty.");
            var first = (ServerRecord)table.Items[0]; search.Text = "no_match_" + Guid.NewGuid().ToString("N");
            if (table.Items.Count != 0 || check.IsEnabled || favorite.IsEnabled) throw new InvalidOperationException("Search/empty state failed.");
            search.Text = ""; kind.SelectedItem = "全部"; populated.IsChecked = false;
            ToggleFavorite(); favoritesOnly.IsChecked = true;
            if (table.Items.Count == 0) throw new InvalidOperationException("Favorite filter failed.");
            favoritesOnly.IsChecked = false; ToggleFavorite();
            fold.IsChecked = false; fold.IsChecked = true;
            if (HasRequestedNetwork) throw new InvalidOperationException("Smoke test performed network requests.");
            status.Text = "验证预览 · " + (sample.RetrievedAt == DateTime.MinValue ? "离线测试数据" : "已取得的数据快照，非实时刷新");
        }

        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); if (operation != null) operation.Cancel(); if (!busy) client.Dispose(); }

        private const string Layout = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Grid.Resources>
  <Style TargetType='CheckBox'><Setter Property='Foreground' Value='#A6B2C1'/><Setter Property='FontSize' Value='11'/><Setter Property='Margin' Value='0,0,18,0'/><Setter Property='VerticalAlignment' Value='Center'/></Style>
  <Style TargetType='DataGridColumnHeader'><Setter Property='Background' Value='#1D2835'/><Setter Property='Foreground' Value='#98A8BB'/><Setter Property='Padding' Value='10,12'/><Setter Property='BorderThickness' Value='0'/></Style>
  <Style TargetType='DataGridRow'><Setter Property='Background' Value='#141D27'/><Setter Property='Foreground' Value='#DCE4EE'/><Setter Property='MinHeight' Value='44'/><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='#283E55'/><Setter Property='Foreground' Value='#FFFFFF'/></Trigger></Style.Triggers></Style>
  <Style TargetType='DataGridCell'><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='10,6'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='DataGridCell'><Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter VerticalAlignment='Center'/></Border></ControlTemplate></Setter.Value></Setter><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='#283E55'/><Setter Property='Foreground' Value='#FFFFFF'/></Trigger></Style.Triggers></Style>
 </Grid.Resources>
 <Grid.RowDefinitions><RowDefinition Height='68'/><RowDefinition Height='Auto'/><RowDefinition Height='52'/><RowDefinition Height='*' MinHeight='100'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
 <DockPanel LastChildFill='False'><StackPanel DockPanel.Dock='Left'><TextBlock Text='服务器' FontSize='25' FontWeight='SemiBold'/><TextBlock Text='查看状态，按需跟随地图。' Foreground='#8F9EAF' FontSize='12' Margin='0,6,0,0'/></StackPanel><StackPanel DockPanel.Dock='Right' Orientation='Horizontal' VerticalAlignment='Top'><Button x:Name='Cancel' Content='取消' Background='Transparent' Margin='0,0,8,0'/><Button x:Name='Refresh' Content='刷新' Background='#3B628D' Foreground='#EDF4FF'/></StackPanel></DockPanel>
 <StackPanel Grid.Row='1' Margin='0,0,0,12'>
  <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='12'/><ColumnDefinition Width='116'/></Grid.ColumnDefinitions><Grid><TextBox x:Name='Search' ToolTip='搜索名称、地图、模式或 IP' FontSize='13' Padding='10,8'/><TextBlock Text='搜索服务器、地图或 IP' IsHitTestVisible='False' Foreground='#718398' FontSize='12' VerticalAlignment='Center' Margin='11,0'><TextBlock.Style><Style TargetType='TextBlock'><Setter Property='Visibility' Value='Collapsed'/><Style.Triggers><DataTrigger Binding='{Binding Text, ElementName=Search}' Value=''><Setter Property='Visibility' Value='Visible'/></DataTrigger></Style.Triggers></Style></TextBlock.Style></TextBlock></Grid><ComboBox x:Name='Kind' Grid.Column='2' TextElement.Foreground='#EDF1F7'><ComboBox.ItemTemplate><DataTemplate><TextBlock Text='{Binding}' Foreground='#EDF1F7'/></DataTemplate></ComboBox.ItemTemplate></ComboBox></Grid>
  <Grid Margin='0,12,0,0'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition/></Grid.ColumnDefinitions><CheckBox x:Name='FavoritesOnly' Content='仅收藏'/><CheckBox x:Name='Populated' Grid.Column='1' Content='隐藏空服' ToolTip='隐藏明确上报为空的服务器；未知人数保留。'/><Expander x:Name='AdvancedFilters' Grid.Column='2' Header='更多筛选' HorizontalAlignment='Right'><WrapPanel Margin='12,10,0,0'><CheckBox x:Name='Fold' Content='合并相同入口' IsChecked='True' ToolTip='折叠相同 IP 与端口的多条记录，不判断原服归属。'/><CheckBox x:Name='HideMismatch' Content='隐藏持续差异' ToolTip='仅隐藏连续核验存在显著差异的记录；不能据此确认阴兵。'/><CheckBox x:Name='AutoCheck' Content='自动核验前 10 台' IsChecked='True'/></WrapPanel></Expander></Grid>
 </StackPanel>
 <Grid Grid.Row='2'><Grid.RowDefinitions><RowDefinition Height='22'/><RowDefinition/></Grid.RowDefinitions><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions><TextBlock x:Name='Summary' Foreground='#AAB9CB' FontSize='11' TextTrimming='CharacterEllipsis' ToolTip='{Binding Text, RelativeSource={RelativeSource Self}}'/><TextBlock x:Name='FollowStatus' Grid.Column='1' Text='未启用地图跟随' Foreground='#9CBDE5' FontSize='11' HorizontalAlignment='Right' MaxWidth='480' TextTrimming='CharacterEllipsis' ToolTip='{Binding Text, RelativeSource={RelativeSource Self}}'/><TextBlock x:Name='Status' Grid.Row='1' Grid.ColumnSpan='2' Text='每分钟刷新' Foreground='#7E91A7' FontSize='11' TextTrimming='CharacterEllipsis' ToolTip='{Binding Text, RelativeSource={RelativeSource Self}}'/></Grid>
 <DataGrid x:Name='Servers' Grid.Row='3' AutoGenerateColumns='False' IsReadOnly='True' CanUserAddRows='False' CanUserDeleteRows='False' CanUserReorderColumns='False' EnableRowVirtualization='True' EnableColumnVirtualization='True' SelectionMode='Single' SelectionUnit='FullRow' HeadersVisibility='Column' GridLinesVisibility='Horizontal' HorizontalGridLinesBrush='#222F3E' BorderBrush='#2A3441' Background='#111A24' RowHeaderWidth='0' FontSize='12' ScrollViewer.CanContentScroll='True'>
  <DataGrid.Columns>
   <DataGridTextColumn Header='☆' Binding='{Binding FavoriteText}' Width='36'/>
   <DataGridTextColumn Header='服务器' Binding='{Binding Name}' Width='*' MinWidth='220'><DataGridTextColumn.ElementStyle><Style TargetType='TextBlock'><Setter Property='TextTrimming' Value='CharacterEllipsis'/><Setter Property='ToolTip' Value='{Binding Name}'/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
   <DataGridTextColumn Header='地图' Binding='{Binding Map}' Width='190'><DataGridTextColumn.ElementStyle><Style TargetType='TextBlock'><Setter Property='TextTrimming' Value='CharacterEllipsis'/><Setter Property='ToolTip' Value='{Binding Map}'/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
   <DataGridTextColumn Header='上报人数' Binding='{Binding PlayersText}' SortMemberPath='Reported' Width='94'/>
   <DataGridTextColumn Header='可见成员' Binding='{Binding ObservedText}' SortMemberPath='Observed' Width='94'/>
   <DataGridTextColumn Header='核验状态' Binding='{Binding StatusText}' Width='180'><DataGridTextColumn.ElementStyle><Style TargetType='TextBlock'><Setter Property='TextTrimming' Value='CharacterEllipsis'/><Setter Property='ToolTip' Value='{Binding StatusText}'/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
  </DataGrid.Columns>
 </DataGrid>
 <Border x:Name='DetailCard' Grid.Row='4' Background='#171F29' BorderBrush='#2A3441' BorderThickness='1' CornerRadius='13' Padding='18,16' Margin='0,14,0,0'>
  <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='22'/><ColumnDefinition Width='160'/></Grid.ColumnDefinitions>
   <StackPanel>
    <TextBlock x:Name='DetailTitle' Text='选择服务器' FontSize='14' FontWeight='Medium' TextTrimming='CharacterEllipsis'/>
    <TextBlock x:Name='DetailMeta' Foreground='#8F9EAF' FontSize='11' Margin='0,6,0,0' TextTrimming='CharacterEllipsis'/>
    <Grid Margin='0,16,0,13'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
     <StackPanel><TextBlock Text='上报人数' Foreground='#8F9EAF' FontSize='10'/><TextBlock x:Name='DetailReported' Text='—' FontSize='17' Margin='0,5,0,0'/></StackPanel>
     <StackPanel Grid.Column='1'><TextBlock Text='EOS 登记' Foreground='#8F9EAF' FontSize='10'/><TextBlock x:Name='DetailRegistered' Text='—' FontSize='17' Margin='0,5,0,0'/></StackPanel>
     <StackPanel Grid.Column='2'><TextBlock Text='可见成员' Foreground='#8F9EAF' FontSize='10'/><TextBlock x:Name='DetailObserved' Text='—' FontSize='17' Margin='0,5,0,0'/></StackPanel>
     <StackPanel Grid.Column='3'><TextBlock Text='排队 · 普通 / 预留' Foreground='#8F9EAF' FontSize='10'/><TextBlock x:Name='DetailQueue' Text='—' FontSize='17' Margin='0,5,0,0'/></StackPanel>
    </Grid>
    <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><TextBlock x:Name='Detail' Foreground='#8F9EAF' FontSize='11' TextTrimming='CharacterEllipsis'/><TextBlock x:Name='DetailInfo' Grid.Column='1' Text='详情 ⓘ' Foreground='#9CBDE5' FontSize='11' Margin='12,0,0,0' Cursor='Help' ToolTipService.ShowDuration='30000'/></Grid>
   </StackPanel>
   <StackPanel Grid.Column='2' VerticalAlignment='Center'><Button x:Name='Follow' Content='启用地图跟随' Background='#304A64' Margin='0,0,0,8' ToolTip='启用后仍停留在列表；手动切换地图页面时生效。'/><Button x:Name='Check' Content='核验人数' Margin='0,0,0,8'/><Button x:Name='Favorite' Content='收藏'/></StackPanel>
  </Grid>
 </Border>
</Grid>";
    }
}
