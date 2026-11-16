using System.Threading.Tasks;
using DownKyi.Desktop;

namespace DownKyi;

sealed class Program
{
    public static Task Main(string[] args)
    {
        return DesktopApplication.RunAsync(args);
    }
}
