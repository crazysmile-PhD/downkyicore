using System;
using System.Threading.Tasks;
using DownKyi.Desktop;

namespace DownKyi;

sealed class Program
{
    [STAThread]
    public static Task Main(string[] args)
    {
        return DesktopApplication.RunAsync(args);
    }
}
