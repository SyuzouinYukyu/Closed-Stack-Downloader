using System.Windows.Forms;

namespace Closed_Stack_Downloader;
internal static class Program
{
    [STAThread]
    static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}
