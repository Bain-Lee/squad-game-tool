using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace SquadTools
{
    // Faction IDs follow SquadCalc. National flags are drawn locally, without game artwork or codecs.
    public static class FactionCatalog
    {
        private static readonly Dictionary<string,string[]> entries = new[] {
            "USA|美国陆军|US", "USMC|美国海军陆战队|US", "RGF|俄罗斯陆军|RU", "VDV|俄罗斯空降军|RU",
            "BAF|英国陆军|GB", "CAF|加拿大陆军|CA", "ADF|澳大利亚国防军|AU", "AFU|乌克兰武装部队|UA",
            "PLA|中国人民解放军|CN", "PLAAGF|解放军陆军两栖旅|CN", "PLANMC|解放军海军陆战队|CN",
            "TLF|土耳其陆军|TR", "GFI|伊朗地面部队|", "CRF|加拿大抵抗力量|", "IMF|非正规民兵|",
            "INS|叛乱军队|", "MEI|叛乱军队|", "MEA|中东联军|", "WPMC|西方雇佣兵|"
        }.Select(s=>s.Split('|')).ToDictionary(a=>a[0],StringComparer.Ordinal);
        private static readonly Dictionary<string,ImageSource> images = new Dictionary<string,ImageSource>();
        public static string Id(string unit)
        {
            if(string.IsNullOrWhiteSpace(unit) || unit.Length>180 || !Regex.IsMatch(unit,"\\A[A-Za-z0-9_-]+\\z")) return "";
            string text=unit.ToUpperInvariant();
            if(text.StartsWith("FSTEMPLATE_",StringComparison.Ordinal)) text=text.Substring(11);
            string id=text.Split('_')[0]; return entries.ContainsKey(id)?id:"";
        }
        public static string Name(string unit) { string id=Id(unit); return id.Length==0?"阵营未知":entries[id][1]; }
        public static string Country(string unit) { string id=Id(unit); return id.Length==0?"":entries[id][2]; }
        public static ImageSource Image(string unit)
        {
            string id=Id(unit), country=Country(id), key=country.Length>0?country:id;
            ImageSource cached; if(images.TryGetValue(key,out cached)) return cached;
            var group=new DrawingGroup();
            using(var dc=group.Open()) {
                dc.PushClip(new RectangleGeometry(new Rect(0,0,60,40)));
                Rect(dc,"#203749",0,0,60,40);
                switch(country) {
                case "US":
                    for(int i=0;i<13;i++) Rect(dc,i%2==0?"#B22234":"#FFFFFF",0,i*40.0/13,60,40.0/13+0.1);
                    Rect(dc,"#3C3B6E",0,0,25,40.0*7/13);
                    for(int y=0;y<9;y++) for(int x=0;x<(y%2==0?6:5);x++) Star(dc,"#FFFFFF",2.1+x*4.15+(y%2)*2.1,1.6+y*2.3,1.25,5,-Math.PI/2);
                    break;
                case "RU": Rect(dc,"#FFFFFF",0,0,60,40.0/3); Rect(dc,"#0039A6",0,40.0/3,60,40.0/3); Rect(dc,"#D52B1E",0,80.0/3,60,40.0/3); break;
                case "UA": Rect(dc,"#0057B7",0,0,60,20); Rect(dc,"#FFD700",0,20,60,20); break;
                case "GB": UnionJack(dc); break;
                case "AU":
                    Rect(dc,"#012169",0,0,60,40); dc.PushTransform(new ScaleTransform(0.5,0.5)); UnionJack(dc); dc.Pop();
                    Star(dc,"#FFFFFF",15,30,5,7,-Math.PI/2);
                    foreach(var p in new[]{new Point(45,8),new Point(36,21),new Point(53,19),new Point(45,34)}) Star(dc,"#FFFFFF",p.X,p.Y,3,7,-Math.PI/2);
                    Star(dc,"#FFFFFF",49,25,1.8,5,-Math.PI/2); break;
                case "CN":
                    Rect(dc,"#EE1C25",0,0,60,40); Star(dc,"#FFFF00",10,10,5.5,5,-Math.PI/2);
                    foreach(var p in new[]{new Point(20,4),new Point(24,8),new Point(24,14),new Point(20,18)}) Star(dc,"#FFFF00",p.X,p.Y,2,5,Math.Atan2(10-p.Y,10-p.X));
                    break;
                case "CA":
                    Rect(dc,"#FFFFFF",0,0,60,40); Rect(dc,"#D80621",0,0,15,40); Rect(dc,"#D80621",45,0,15,40);
                    dc.DrawGeometry(Brush("#D80621"),null,Geometry.Parse("M30,6 L33,13 36,11 35,20 40,16 41,20 45,19 41,26 42,29 32,28 31,35 29,35 29,28 18,29 19,26 15,19 19,20 20,16 25,20 24,11 27,13 Z")); break;
                case "TR":
                    Rect(dc,"#E30A17",0,0,60,40); dc.DrawEllipse(Brush("#FFFFFF"),null,new Point(25,20),10,10); dc.DrawEllipse(Brush("#E30A17"),null,new Point(28,20),8,8);
                    Star(dc,"#FFFFFF",39,20,5,5,-Math.PI/2); break;
                default:
                    var text=new FormattedText(id.Length==0?"?":id,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Arial"),id.Length>3?13:17,Brush("#E5EDF3"),1);
                    dc.DrawText(text,new Point((60-text.Width)/2,(40-text.Height)/2)); break;
                }
                dc.Pop();
            }
            group.Freeze(); var image=new DrawingImage(group); image.Freeze(); images[key]=image; return image;
        }
        private static SolidColorBrush Brush(string color) { var b=(SolidColorBrush)new BrushConverter().ConvertFromString(color); b.Freeze(); return b; }
        private static void Rect(DrawingContext dc,string color,double x,double y,double width,double height) { dc.DrawRectangle(Brush(color),null,new Rect(x,y,width,height)); }
        private static void UnionJack(DrawingContext dc)
        {
            Rect(dc,"#012169",0,0,60,40);
            foreach(var color in new[]{"#FFFFFF","#C8102E"}) {
                double width=color=="#FFFFFF"?8:3;
                dc.DrawLine(new Pen(Brush(color),width),new Point(0,0),new Point(60,40)); dc.DrawLine(new Pen(Brush(color),width),new Point(60,0),new Point(0,40));
            }
            Rect(dc,"#FFFFFF",24,0,12,40); Rect(dc,"#FFFFFF",0,14,60,12); Rect(dc,"#C8102E",27,0,6,40); Rect(dc,"#C8102E",0,17,60,6);
        }
        private static void Star(DrawingContext dc,string color,double x,double y,double radius,int tips,double angle)
        {
            var geometry=new StreamGeometry();
            using(var c=geometry.Open()) for(int i=0;i<tips*2;i++) {
                double a=angle+i*Math.PI/tips,r=i%2==0?radius:radius*0.4; var p=new Point(x+r*Math.Cos(a),y+r*Math.Sin(a));
                if(i==0) c.BeginFigure(p,true,true); else c.LineTo(p,true,false);
            }
            geometry.Freeze(); dc.DrawGeometry(Brush(color),null,geometry);
        }
    }
}
