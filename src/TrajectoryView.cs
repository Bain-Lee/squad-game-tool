using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SquadTools
{
    public sealed class TrajectoryWindow : Window
    {
        private readonly TextBlock heading = new TextBlock(), highSummary = new TextBlock(), lowSummary = new TextBlock(), note = new TextBlock();
        private readonly CheckBox compare = new CheckBox { Content="同时显示高低抛", Foreground=Brushes.LightSteelBlue, FontSize=11, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(12,0,0,0) };
        internal readonly TrajectoryView Chart = new TrajectoryView();
        public TrajectoryWindow()
        {
            Title="弹道与地形剖面"; Width=900; Height=560; MinWidth=640; MinHeight=400;
            Background=ColorBrush("#101720"); Foreground=Brushes.White; FontFamily=new FontFamily("Microsoft YaHei UI");
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var grid=new Grid { Margin=new Thickness(22) };
            grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            heading.FontSize=17; heading.TextWrapping=TextWrapping.Wrap; heading.Margin=new Thickness(0,0,0,13); grid.Children.Add(heading);
            var summary=new StackPanel();
            highSummary.Foreground=ColorBrush("#94C9FF"); lowSummary.Foreground=ColorBrush("#EBC487");
            foreach(var text in new[]{highSummary,lowSummary}) { text.FontSize=12; text.TextWrapping=TextWrapping.Wrap; text.Margin=new Thickness(0,0,0,7); summary.Children.Add(text); }
            var summaryRow=new DockPanel(); DockPanel.SetDock(compare,Dock.Right); summaryRow.Children.Add(compare); summaryRow.Children.Add(summary);
            compare.Checked+=delegate { Chart.CompareArcs=true; }; compare.Unchecked+=delegate { Chart.CompareArcs=false; };
            Grid.SetRow(summaryRow,1); grid.Children.Add(summaryRow);
            Chart.Margin=new Thickness(0,10,0,12); Grid.SetRow(Chart,2); grid.Children.Add(Chart);
            note.FontSize=11; note.Foreground=ColorBrush("#A0AEBD"); note.TextWrapping=TextWrapping.Wrap; Grid.SetRow(note,3); grid.Children.Add(note);
            Content=grid;
        }
        private static Brush ColorBrush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        public void SetProfile(TrajectoryProfile profile, bool? selectedHigh, string title, string error)
        {
            heading.Text=title; Chart.SetProfile(profile,selectedHigh);
            if(profile==null) { highSummary.Text=lowSummary.Text=""; note.Text=error??"没有可显示的有效弹道。"; return; }
            highSummary.Text=Summary(profile,true,selectedHigh); lowSummary.Text=Summary(profile,false,selectedHigh);
            note.Text=(profile.HasTerrain?"地表采用地图 SDK 基准高程，可为负数，并非海拔；不含建筑、房顶或桥面。":"本图无地形数据：沿程地形未知，仅显示炮位、目标高程与理论弹道。")+
                "\n横轴为距炮位的水平距离，纵轴为同一基准下的高程，均为米；两轴比例独立。曲线不进行自动遮挡判定。"+
                (profile.Weapon.Deceleration>0?" UB-32 沿用距离相关速度近似。":"");
        }
        private static string Summary(TrajectoryProfile profile,bool high,bool? selected)
        {
            var shot=profile.Solution; bool usable=high?shot.HighUsable:shot.LowUsable;
            return (high?"高抛":"低抛")+(selected.HasValue && selected.Value==high?" · 当前":"")+"    "+
                (usable?profile.Weapon.FormatElevation(high?shot.HighMil:shot.LowMil)+(profile.Weapon.Unit=="mil"?" mil":"")+
                "    飞行 "+(high?shot.HighTimeSeconds:shot.LowTimeSeconds).ToString("F1",CultureInfo.InvariantCulture)+" s":"不可用（射程、仰角或弹丸寿命限制）");
        }
    }

    public sealed class TrajectoryView : FrameworkElement
    {
        public TrajectoryProfile Profile { get; private set; }
        private bool? selectedHigh;
        private bool compareArcs;
        public bool CompareArcs { get { return compareArcs; } set { compareArcs=value; InvalidateVisual(); } }
        private double minimum, maximum;
        private Rect plot;
        private static readonly Typeface Font=new Typeface("Microsoft YaHei UI");
        public TrajectoryView() { ClipToBounds=true; SizeChanged+=delegate { InvalidateVisual(); }; MouseMove+=OnMove; MouseLeave+=delegate { ToolTip=null; }; }
        public void SetProfile(TrajectoryProfile profile,bool? selected) { Profile=profile; selectedHigh=selected; ToolTip=null; InvalidateVisual(); }
        private static Brush Brush(string hex) { var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
        private static Pen Line(string color,double width,bool dashed) { var pen=new Pen(Brush(color),width); if(dashed) pen.DashStyle=DashStyles.Dash; pen.Freeze(); return pen; }
        private void Text(DrawingContext dc,string value,double x,double y,string color,double size)
        { dc.DrawText(new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Font,size,Brush(color),VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y)); }
        private Point Position(double distance,double height)
        { return new Point(plot.Left+distance/Profile.Solution.Distance*plot.Width,plot.Bottom-(height-minimum)/(maximum-minimum)*plot.Height); }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); dc.DrawRoundedRectangle(Brush("#141F2B"),null,new Rect(RenderSize),8,8);
            if(Profile==null || ActualWidth<150 || ActualHeight<100) { Text(dc,"暂无有效弹道",20,20,"#92A4B7",13); return; }
            plot=new Rect(64,28,Math.Max(10,ActualWidth-88),Math.Max(10,ActualHeight-72));
            var heights=Profile.Samples.SelectMany(s=>new[]{s.Ground,VisibleArc(true)?s.High:null,VisibleArc(false)?s.Low:null}).Where(h=>h.HasValue).Select(h=>h.Value).Concat(new[]{Profile.WeaponGround,Profile.TargetGround}).ToList();
            minimum=heights.Min(); maximum=heights.Max(); double padding=Math.Max(5,(maximum-minimum)*0.08); minimum-=padding; maximum+=padding;
            for(int i=0;i<=4;i++) {
                double height=minimum+(maximum-minimum)*i/4;
                double y=Position(0,height).Y; dc.DrawLine(Line("#2C3C4C",1,false),new Point(plot.Left,y),new Point(plot.Right,y));
                Text(dc,height.ToString("F0",CultureInfo.InvariantCulture),6,y-8,"#94A7BB",10);
                double distance=Profile.Solution.Distance*i/4, x=Position(distance,minimum).X;
                dc.DrawLine(Line("#263746",1,false),new Point(x,plot.Top),new Point(x,plot.Bottom));
                Text(dc,distance.ToString("F0",CultureInfo.InvariantCulture),x-12,plot.Bottom+8,"#94A7BB",10);
            }
            Text(dc,"基准高程 / m",8,5,"#A7B8C8",10); Text(dc,"距炮位 / m",Math.Max(64,plot.Right-65),plot.Bottom+25,"#A7B8C8",10);
            if(Profile.HasTerrain) {
                var ground=new StreamGeometry();
                using(var context=ground.Open()) {
                    context.BeginFigure(new Point(plot.Left,plot.Bottom),true,true);
                    foreach(var sample in Profile.Samples) context.LineTo(Position(sample.Distance,sample.Ground.Value),true,false);
                    context.LineTo(new Point(plot.Right,plot.Bottom),true,false);
                }
                ground.Freeze(); dc.DrawGeometry(Brush("#314638"),Line("#91AC87",1.3,false),ground);
            }
            DrawArc(dc,true,"#94C9FF"); DrawArc(dc,false,"#EBC487");
            Point start=Position(0,Profile.WeaponGround), end=Position(Profile.Solution.Distance,Profile.TargetGround);
            dc.DrawEllipse(Brush("#B8EB7E"),null,start,4,4); dc.DrawEllipse(Brush("#FFA88C"),null,end,4,4);
            Text(dc,"炮位",plot.Left+5,Math.Min(plot.Bottom-18,start.Y+5),"#B8EB7E",10);
            Text(dc,"目标",plot.Right-28,Math.Min(plot.Bottom-18,end.Y+5),"#FFA88C",10);
            if(!Profile.HasTerrain) Text(dc,"沿程地形未知",plot.Left+16,plot.Top+10,"#A3AFBC",12);
        }
        private void DrawArc(DrawingContext dc,bool high,string color)
        {
            if(!VisibleArc(high)) return;
            var geometry=new StreamGeometry();
            using(var context=geometry.Open()) {
                bool first=true;
                foreach(var sample in Profile.Samples) {
                    var point=Position(sample.Distance,(high?sample.High:sample.Low).Value);
                    if(first) { context.BeginFigure(point,false,false); first=false; } else context.LineTo(point,true,false);
                }
            }
            geometry.Freeze(); bool active=selectedHigh.HasValue && selectedHigh.Value==high;
            dc.DrawGeometry(null,Line(color,active?3:1.5,!active),geometry);
        }
        private bool VisibleArc(bool high)
        { return (high?Profile.Solution.HighUsable:Profile.Solution.LowUsable) && (CompareArcs || !selectedHigh.HasValue || selectedHigh.Value==high); }
        private void OnMove(object sender,MouseEventArgs e)
        {
            if(Profile==null || plot.Width<=0 || !plot.Contains(e.GetPosition(this))) { ToolTip=null; return; }
            double fraction=(e.GetPosition(this).X-plot.Left)/plot.Width;
            var sample=Profile.Samples[(int)Math.Round(fraction*(Profile.Samples.Count-1))];
            ToolTip="距炮位 "+sample.Distance.ToString("F1",CultureInfo.InvariantCulture)+" m\n地表 "+FormatHeight(sample.Ground)+"\n高抛 "+FormatHeight(sample.High)+"\n低抛 "+FormatHeight(sample.Low);
        }
        private static string FormatHeight(double? value) { return value.HasValue?value.Value.ToString("F1",CultureInfo.InvariantCulture)+" m":"未知 / 不可用"; }
    }
}
