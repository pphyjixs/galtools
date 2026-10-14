using System;
using System.Windows;
using System.Windows.Markup;
using System.Reflection;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace Galtools
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            var application = new Application();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Galtools.Theme.xaml"))
                application.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            var desktop = new DesktopApp();
            application.Run(desktop.Window);
        }
    }
}
