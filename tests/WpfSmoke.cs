using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Runs the actual embedded UI and backend in-process without opening a desktop window.
internal static class WpfSmoke
{
    static object app;
    static Type type;
    static Window window;
    static FrameworkElement root;
    static string step;
    static object Call(string name, params object[] args) { return type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, args); }
    static T Find<T>(string name) where T : class { return window.FindName(name) as T; }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame); Thread.Sleep(10);
    }
    static void Wait(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed.TotalSeconds > 30) throw new Exception("UI operation timed out"); Pump(); }
    }
    static void Render(string name)
    {
        root.Measure(new Size(1280, 780)); root.Arrange(new Rect(0,0,1280,780)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1920,1170,144,144,PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var output=File.Create(Path.Combine(Path.GetTempPath(),name))) encoder.Save(output);
    }
    [STAThread]
    static int Main(string[] args)
    {
        try {
            step="load application";
            Console.WriteLine(step);
            var framework = new Application(); var assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"galtools.exe"));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using(var stream=assembly.GetManifestResourceStream("Galtools.Theme.xaml")) framework.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            Console.WriteLine("theme loaded");
            type=assembly.GetType("Galtools.DesktopApp"); app=Activator.CreateInstance(type,true);
            Console.WriteLine("desktop created");
            window=(Window)type.GetProperty("Window",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app,null);
            root=(FrameworkElement)window.Content; window.Content=null;
            System.Windows.Documents.TextElement.SetFontFamily(root,window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(root,window.FontSize);
            ((Grid)root).Background=window.Background;
            step="render games"; Render("galtools-games-review.png");
            Call("ShowPage",true);
            step="open archive"; Call("OpenArchive",args[0],true,"自动","");
            Wait(()=>Find<DataGrid>("Files").Items.Count==147);
            Find<DataGrid>("Files").SelectedIndex=2;
            Wait(()=>Find<Image>("PreviewImage").Source!=null);
            var preview=(BitmapSource)Find<Image>("PreviewImage").Source;
            if(preview.PixelHeight>480)throw new Exception("Preview should remain at 480p");
            step="render resource"; Render("galtools-resources-review.png");
            var address=Find<TextBox>("Address"); var caret=address.GetRectFromCharacterIndex(1);
            if(caret.IsEmpty || caret.Height<10 || caret.Bottom>address.ActualHeight)throw new Exception("Address text is clipped");
            Call("SetView",true); Render("galtools-grid-review.png");
            if(Find<ListBox>("Tiles").Items.Count>60)throw new Exception("Grid page is unbounded");
            if(Find<ListBox>("Tiles").SelectedItem!=Find<DataGrid>("Files").SelectedItem)throw new Exception("Selection lost while switching views");
            var record=type.GetField("games",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app) as System.Collections.IList;
            if(record!=null && record.Count>0) { Call("ShowGame",record[0]); Render("galtools-details-review.png"); Call("EditGame",record[0]); Render("galtools-edit-review.png"); Call("CloseModal"); }
            var backend=type.GetField("archive",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app) as IDisposable;
            if(backend!=null)backend.Dispose();
            Console.WriteLine("PASS: embedded styles, folder/archive navigation, 480p preview, bounded grid, selection, address layout, detail/edit render.");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine("STEP: " + step); for(int i=0;ex!=null&&i<6;i++,ex=ex.InnerException){Console.Error.WriteLine(ex.GetType().FullName);Console.Error.WriteLine(ex.Message);}return 1;}
    }
}
