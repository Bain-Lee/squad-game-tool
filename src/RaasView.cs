using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SquadTools
{
    public sealed class RaasCandidate
    {
        public RaasPoint Point;
        public double Probability;
        public override string ToString() { return Point.Name+"  "+Probability.ToString("P0",CultureInfo.InvariantCulture); }
    }
    public sealed class RaasView : Border
    {
        private readonly RaasRepository repository;
        private readonly ComboBox mapPicker = new ComboBox(), layerPicker = new ComboBox(), mainPicker = new ComboBox();
        private readonly ListBox candidates = new ListBox();
        private readonly TextBlock message = new TextBlock(), progress = new TextBlock(), chain = new TextBlock();
        private readonly CheckBox allPoints = new CheckBox { Content="显示后续可能点", IsChecked=false };
        private readonly Button confirm = new Button { Content="确认所选点" }, undo = new Button { Content="上一步" }, reset = new Button { Content="重置" };
        private bool updating;
        private string mapId, layerKey, contextLayer, contextServerId, team1, team2, teamSource;
        public string MapId { get { return mapId; } }
        public bool HasLayer(string id,string raw) { var info=repository.FindMap(id); return info!=null && info.Layers.Any(l=>string.Equals(l.RawName,raw,StringComparison.OrdinalIgnoreCase)); }
        public RaasSession Session { get; private set; }
        public event Action<string> MapRequested, LayerRequested;
        public event Action<IList<RaasMarker>> MarkersChanged;
        public RaasView(RaasRepository data)
        {
            repository=data;
            var grid=new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            var top=new StackPanel();
            top.Children.Add(Title("地图",11)); top.Children.Add(mapPicker);
            top.Children.Add(Title("图层",11)); top.Children.Add(layerPicker);
            top.Children.Add(Title("己方基地",11)); top.Children.Add(mainPicker);
            allPoints.Foreground=new SolidColorBrush(Color.FromRgb(166,178,193)); allPoints.FontSize=11; allPoints.Margin=new Thickness(0,12,0,12); allPoints.Content="显示后续候选"; top.Children.Add(allPoints);
            progress.Foreground=new SolidColorBrush(Color.FromRgb(173,205,249)); progress.FontSize=12; progress.TextWrapping=TextWrapping.Wrap; progress.Margin=new Thickness(0,0,0,10); top.Children.Add(progress); grid.Children.Add(top);
            candidates.Background=new SolidColorBrush(Color.FromRgb(17,24,32)); candidates.Foreground=Brushes.White; candidates.BorderBrush=Brushes.Transparent; candidates.FontSize=12;
            candidates.Margin=new Thickness(0,0,0,10); Grid.SetRow(candidates,1); grid.Children.Add(candidates);
            var bottom=new StackPanel(); bottom.Children.Add(confirm);
            var actions=new Grid { Margin=new Thickness(0,8,0,8) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
            undo.Margin=new Thickness(0,0,5,0); reset.Margin=new Thickness(5,0,0,0); Grid.SetColumn(reset,1); actions.Children.Add(undo); actions.Children.Add(reset); bottom.Children.Add(actions);
            chain.FontSize=11; chain.Foreground=new SolidColorBrush(Color.FromRgb(166,178,193)); chain.MaxHeight=16; chain.TextTrimming=TextTrimming.CharacterEllipsis; bottom.Children.Add(chain);
            message.FontSize=10; message.Foreground=Brushes.LightSlateGray; message.TextWrapping=TextWrapping.Wrap; message.MaxHeight=30; message.TextTrimming=TextTrimming.CharacterEllipsis; message.Margin=new Thickness(0,8,0,0); bottom.Children.Add(message);
            bottom.Children.Add(new TextBlock { Text="概率为路线估计", ToolTip="概率来自剩余路线模型，不代表已知本局隐藏旗点；游戏更新后可能需要更新数据。", FontSize=10, Foreground=Brushes.LightSlateGray, Margin=new Thickness(0,8,0,0) });
            Grid.SetRow(bottom,2); grid.Children.Add(bottom); Child=grid;
            mapPicker.ItemsSource=repository.Maps;
            mapPicker.SelectionChanged+=delegate { if(!updating && mapPicker.SelectedItem!=null && MapRequested!=null) MapRequested(((RaasMapInfo)mapPicker.SelectedItem).Id); };
            layerPicker.SelectionChanged+=delegate { if(!updating && layerPicker.SelectedItem!=null) { string raw=((RaasLayerInfo)layerPicker.SelectedItem).RawName; contextLayer=raw; contextServerId=null; LoadLayer(raw); if(LayerRequested!=null) LayerRequested(raw); } };
            mainPicker.SelectionChanged+=delegate { if(!updating && Session!=null && mainPicker.SelectedIndex>0) { Session.ChooseMain(Session.Layer.Mains[mainPicker.SelectedIndex-1]); Render(); } };
            allPoints.Checked+=delegate { Render(); }; allPoints.Unchecked+=delegate { Render(); };
            candidates.SelectionChanged+=delegate { confirm.IsEnabled=candidates.SelectedItem!=null; };
            candidates.MouseDoubleClick+=delegate(object sender,MouseButtonEventArgs e) { if(ItemsControl.ContainerFromElement(candidates,e.OriginalSource as DependencyObject) is ListBoxItem) { ConfirmSelected(); e.Handled=true; } };
            candidates.PreviewKeyDown+=delegate(object sender,KeyEventArgs e) { if(e.Key==Key.Enter) { ConfirmSelected(); e.Handled=true; } };
            confirm.Click+=delegate { ConfirmSelected(); }; undo.Click+=delegate { if(Session!=null) { Session.Undo(); Render(); } }; reset.Click+=delegate { if(Session!=null) { Session.Reset(); Render(); } };
            Render();
        }
        private static TextBlock Title(string text,int size) { return new TextBlock { Text=text,FontSize=size,Foreground=new SolidColorBrush(Color.FromRgb(150,165,182)),Margin=new Thickness(0,6,0,6) }; }
        public void SetMap(string id,string rawName,string sourceServerId = null)
        {
            var info=repository.FindMap(id);
            if(mapId==id && string.Equals(contextLayer,rawName,StringComparison.OrdinalIgnoreCase) && contextServerId==sourceServerId) return;
            mapId=id; contextLayer=rawName; contextServerId=sourceServerId; updating=true;
            try {
                mapPicker.SelectedItem=info; layerPicker.ItemsSource=info==null?null:info.Layers;
                layerPicker.IsEnabled=info!=null && info.Layers.Count>0;
                var selected=info==null?null:info.Layers.Find(l=>string.Equals(l.RawName,rawName,StringComparison.OrdinalIgnoreCase));
                if(rawName==null && info!=null) selected=info.Layers.FirstOrDefault();
                layerPicker.SelectedItem=selected; layerKey=rawName;
                if(selected!=null) LoadLayer(selected.RawName);
                else {
                    Session=null; mainPicker.ItemsSource=null; mainPicker.IsEnabled=false;
                    message.Text=info==null?"自定义地图没有内置 RAAS 图层。":info.Layers.Count==0?info.Note:"当前服务器图层未收录或不是 RAAS："+rawName+"。可手动选择本图的 RAAS 图层。";
                    message.ToolTip=message.Text;
                    Render();
                }
            } finally { updating=false; }
        }
        private void LoadLayer(string rawName)
        {
            layerKey=rawName;
            try {
                Session=new RaasSession(repository.Load(mapId,rawName));
                team1=Session.Layer.Team1; team2=Session.Layer.Team2; teamSource="图层默认阵营（手动模式）";
                bool previous=updating; updating=true;
                try {
                    mainPicker.ItemsSource=MainChoices();
                    mainPicker.SelectedIndex=0; mainPicker.IsEnabled=true;
                } finally { updating=previous; }
                ShowNotice(null);
            } catch(Exception ex) { if(!(ex is FormatException) && !(ex is System.IO.IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException)) throw; Session=null; mainPicker.IsEnabled=false; message.Text="图层无法用于猜点："+ex.Message; message.ToolTip=message.Text; }
            Render();
        }
        public void ShowNotice(string notice)
        {
            if(Session!=null) {
                message.Text=notice ?? (contextServerId==null?"手动 · 默认阵营":"服务器当前阵营");
                message.ToolTip="内置图层 "+Session.Layer.RawName+"\n"+teamSource+"\n数据 "+repository.Updated.Substring(0,Math.Min(10,repository.Updated.Length))+(notice==null?"":"\n"+notice);
            }
            else if(notice!=null) { message.Text=notice; message.ToolTip=notice; }
        }
        private int Team(RaasPoint main) { if(main.Id.IndexOf("Team1",StringComparison.OrdinalIgnoreCase)>=0) return 1; if(main.Id.IndexOf("Team2",StringComparison.OrdinalIgnoreCase)>=0) return 2; return main.Id==Session.Solver.Start?1:2; }
        private string MainName(RaasPoint main) { int team=Team(main); return "队伍 "+team+" · "+FactionCatalog.Name(team==1?team1:team2); }
        private List<string> MainChoices() { return new[]{"请选择己方主基地"}.Concat(Session.Layer.Mains.Select(MainName)).ToList(); }
        public void SetFactions(string first,string second,string source)
        {
            first=FactionCatalog.Id(first); second=FactionCatalog.Id(second);
            if(Session==null || team1==first && team2==second && teamSource==source) return;
            team1=first; team2=second; teamSource=source;
            bool previous=updating; updating=true; int selected=mainPicker.SelectedIndex;
            try { mainPicker.ItemsSource=MainChoices(); mainPicker.SelectedIndex=selected; } finally { updating=previous; }
            ShowNotice(null); Render();
        }
        public void UseManualMode()
        {
            contextServerId=null;
            if(Session!=null) SetFactions(Session.Layer.Team1,Session.Layer.Team2,"图层默认阵营（手动模式）");
        }
        private void ConfirmSelected() { var candidate=candidates.SelectedItem as RaasCandidate; if(candidate!=null) ClickPoint(candidate.Point); }
        public void ClickPoint(RaasPoint point)
        {
            if(Session==null) return;
            if(point.IsMain) {
                Session.ChooseMain(point); updating=true;
                try { mainPicker.SelectedIndex=Session.Layer.Mains.IndexOf(point)+1; } finally { updating=false; }
            } else {
                int index=Session.Confirmed.IndexOf(point);
                if(index>=0) { while(Session.Confirmed.Count>index) Session.Undo(); }
                else if(!Session.Confirm(point)) return;
            }
            Render();
        }
        private void Render()
        {
            var markers=new List<RaasMarker>(); var next=new List<RaasCandidate>();
            if(Session!=null) {
                foreach(var main in Session.Layer.Mains) markers.Add(new RaasMarker { Point=main,Label=Team(main).ToString(CultureInfo.InvariantCulture),Caption=MainName(main),FactionId=Team(main)==1?team1:team2,Color=Session.Main==main?"#F6C85F":"#D99940",Clickable=true,SelectedMain=Session.Main==main });
                if(Session.Main!=null) foreach(var flag in Session.Layer.Flags) {
                    int confirmedAt=Session.Confirmed.IndexOf(flag);
                    var steps=Session.Result.Steps.Keys.Where(s=>Session.Result.Chance(flag,s)>0).OrderBy(s=>s).ToList();
                    double chance=Session.Result.Chance(flag,Session.NextStep);
                    if(chance>0 && confirmedAt<0) next.Add(new RaasCandidate { Point=flag,Probability=chance });
                    if(confirmedAt<0 && (steps.Count==0 || allPoints.IsChecked!=true && chance<=0)) continue;
                    string label=confirmedAt>=0?(confirmedAt+1).ToString(CultureInfo.InvariantCulture):allPoints.IsChecked!=true?Session.NextStep.ToString(CultureInfo.InvariantCulture):string.Join("–",steps.Select(s=>s.ToString(CultureInfo.InvariantCulture)));
                    double total=steps.Sum(s=>Session.Result.Chance(flag,s));
                    markers.Add(new RaasMarker { Point=flag,Label=label,Caption=flag.Name+(confirmedAt>=0?" · 已确认":"\n"+(chance>0?"第"+Session.NextStep+"旗 ":"后续合计 ")+(chance>0?chance:total).ToString("P0",CultureInfo.InvariantCulture)),Color=confirmedAt>=0?"#E5EDF3":chance>0?"#B8EB7E":"#6E9DD6",Clickable=confirmedAt>=0||chance>0,ConfirmedStep=confirmedAt+1 });
                }
                progress.Text=Session.Main==null?"选择己方基地开始":Session.Complete?"旗点链已确认":string.Format("第 {0} 旗 · {1} 个候选",Session.NextStep,next.Count);
                progress.ToolTip="剩余路线 "+Session.Result.Alive+" / "+Session.Result.Total;
                chain.Text=Session.Confirmed.Count==0?"尚未确认旗点。":string.Join(" → ",Session.Confirmed.Select((f,i)=>(i+1)+". "+f.Name));
                chain.ToolTip=chain.Text;
            } else { progress.Text="当前地图暂无可用的 RAAS 猜点数据。"; progress.ToolTip=null; chain.Text=""; chain.ToolTip=null; }
            candidates.ItemsSource=next.OrderByDescending(c=>c.Probability).ThenBy(c=>c.Point.Name).ToList(); confirm.IsEnabled=false;
            undo.IsEnabled=reset.IsEnabled=Session!=null && Session.Confirmed.Count>0;
            if(MarkersChanged!=null) MarkersChanged(markers);
        }
    }
}
