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
        private string mapId, layerKey, contextLayer;
        public RaasSession Session { get; private set; }
        public event Action<string> MapRequested, LayerRequested;
        public event Action<IList<RaasMarker>> MarkersChanged;
        public RaasView(RaasRepository data)
        {
            repository=data;
            var grid=new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            var top=new StackPanel(); top.Children.Add(Title("RAAS 路线猜点",16));
            top.Children.Add(Title("游戏地图",11)); top.Children.Add(mapPicker);
            top.Children.Add(Title("RAAS 图层",11)); top.Children.Add(layerPicker);
            top.Children.Add(Title("己方主基地 / 计数方向",11)); top.Children.Add(mainPicker);
            allPoints.Foreground=Brushes.LightGray; allPoints.FontSize=11; allPoints.Margin=new Thickness(0,12,0,10); top.Children.Add(allPoints);
            progress.Foreground=Brushes.LightGreen; progress.FontSize=12; progress.TextWrapping=TextWrapping.Wrap; progress.Margin=new Thickness(0,0,0,8); top.Children.Add(progress); grid.Children.Add(top);
            candidates.Background=new SolidColorBrush(Color.FromRgb(16,27,36)); candidates.Foreground=Brushes.White; candidates.BorderBrush=new SolidColorBrush(Color.FromRgb(40,59,73)); candidates.FontSize=12;
            candidates.Margin=new Thickness(0,0,0,10); Grid.SetRow(candidates,1); grid.Children.Add(candidates);
            var bottom=new StackPanel(); bottom.Children.Add(confirm);
            var actions=new Grid { Margin=new Thickness(0,8,0,8) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
            undo.Margin=new Thickness(0,0,5,0); reset.Margin=new Thickness(5,0,0,0); Grid.SetColumn(reset,1); actions.Children.Add(undo); actions.Children.Add(reset); bottom.Children.Add(actions);
            chain.FontSize=10; chain.Foreground=Brushes.LightGray; chain.TextWrapping=TextWrapping.Wrap; chain.MaxHeight=50; chain.TextTrimming=TextTrimming.CharacterEllipsis; bottom.Children.Add(chain);
            message.FontSize=10; message.Foreground=Brushes.LightSlateGray; message.TextWrapping=TextWrapping.Wrap; message.Margin=new Thickness(0,8,0,0); bottom.Children.Add(message);
            bottom.Children.Add(new TextBlock { Text="概率为路线模型估计；不代表已知本局隐藏旗点。\n点击旗点依次确认；右键拖动、滚轮缩放。", FontSize=10, Foreground=Brushes.LightSlateGray, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,9,0,0) });
            Grid.SetRow(bottom,2); grid.Children.Add(bottom); Child=grid;
            mapPicker.ItemsSource=repository.Maps;
            mapPicker.SelectionChanged+=delegate { if(!updating && mapPicker.SelectedItem!=null && MapRequested!=null) MapRequested(((RaasMapInfo)mapPicker.SelectedItem).Id); };
            layerPicker.SelectionChanged+=delegate { if(!updating && layerPicker.SelectedItem!=null) { string raw=((RaasLayerInfo)layerPicker.SelectedItem).RawName; contextLayer=raw; LoadLayer(raw); if(LayerRequested!=null) LayerRequested(raw); } };
            mainPicker.SelectionChanged+=delegate { if(!updating && Session!=null && mainPicker.SelectedIndex>0) { Session.ChooseMain(Session.Layer.Mains[mainPicker.SelectedIndex-1]); Render(); } };
            allPoints.Checked+=delegate { Render(); }; allPoints.Unchecked+=delegate { Render(); };
            candidates.SelectionChanged+=delegate { confirm.IsEnabled=candidates.SelectedItem!=null; };
            candidates.MouseDoubleClick+=delegate(object sender,MouseButtonEventArgs e) { if(ItemsControl.ContainerFromElement(candidates,e.OriginalSource as DependencyObject) is ListBoxItem) { ConfirmSelected(); e.Handled=true; } };
            candidates.PreviewKeyDown+=delegate(object sender,KeyEventArgs e) { if(e.Key==Key.Enter) { ConfirmSelected(); e.Handled=true; } };
            confirm.Click+=delegate { ConfirmSelected(); }; undo.Click+=delegate { if(Session!=null) { Session.Undo(); Render(); } }; reset.Click+=delegate { if(Session!=null) { Session.Reset(); Render(); } };
            Render();
        }
        private static TextBlock Title(string text,int size) { return new TextBlock { Text=text,FontSize=size,Foreground=Brushes.LightGray,Margin=new Thickness(0,0,0,7) }; }
        public void SetMap(string id,string rawName)
        {
            var info=repository.FindMap(id);
            if(mapId==id && string.Equals(contextLayer,rawName,StringComparison.OrdinalIgnoreCase)) return;
            mapId=id; contextLayer=rawName; updating=true;
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
                    Render();
                }
            } finally { updating=false; }
        }
        private void LoadLayer(string rawName)
        {
            layerKey=rawName;
            try {
                Session=new RaasSession(repository.Load(mapId,rawName));
                bool previous=updating; updating=true;
                try {
                    mainPicker.ItemsSource=new[]{"请选择己方主基地"}.Concat(Session.Layer.Mains.Select(m=>m.Id==Session.Solver.Start?"队伍 1 主基地":"队伍 2 主基地")).ToList();
                    mainPicker.SelectedIndex=0; mainPicker.IsEnabled=true;
                } finally { updating=previous; }
                ShowNotice(null);
            } catch(Exception ex) { if(!(ex is FormatException) && !(ex is System.IO.IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException)) throw; Session=null; mainPicker.IsEnabled=false; message.Text="图层无法用于猜点："+ex.Message; }
            Render();
        }
        public void ShowNotice(string notice)
        {
            if(notice!=null) message.Text=notice;
            else if(Session!=null) message.Text="内置图层 "+Session.Layer.RawName+"\n数据 "+repository.Updated.Substring(0,Math.Min(10,repository.Updated.Length));
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
                foreach(var main in Session.Layer.Mains) markers.Add(new RaasMarker { Point=main,Label=main.Id==Session.Solver.Start?"1":"2",Caption=main.Id==Session.Solver.Start?"队伍1主基地":"队伍2主基地",Color=Session.Main==main?"#F6C85F":"#D99940",Clickable=true,SelectedMain=Session.Main==main });
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
                progress.Text=Session.Main==null?"先选择己方主基地，或点击地图上的基地标记。":Session.Complete?"已确认完整旗点链。":string.Format("下一点：第 {0} 旗 · {1} 个候选\n剩余路线 {2} / {3}",Session.NextStep,next.Count,Session.Result.Alive,Session.Result.Total);
                chain.Text=Session.Confirmed.Count==0?"尚未确认旗点。":string.Join(" → ",Session.Confirmed.Select((f,i)=>(i+1)+". "+f.Name));
            } else { progress.Text="当前地图暂无可用的 RAAS 猜点数据。"; chain.Text=""; }
            candidates.ItemsSource=next.OrderByDescending(c=>c.Probability).ThenBy(c=>c.Point.Name).ToList(); confirm.IsEnabled=false;
            undo.IsEnabled=reset.IsEnabled=Session!=null && Session.Confirmed.Count>0;
            if(MarkersChanged!=null) MarkersChanged(markers);
        }
    }
}
