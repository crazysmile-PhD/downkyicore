using System;
using DownKyi.Desktop;

namespace DownKyi;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopApplication.Run(args);
    }
}
