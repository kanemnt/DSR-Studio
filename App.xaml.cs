using System.Text;
using System.Windows;

namespace DSRStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // MSB 閲岀殑鍚嶅瓧鏄?Shift-JIS锛坈p932锛夛紝闇€瑕佹敞鍐屼唬鐮侀〉 provider
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        base.OnStartup(e);
    }
}
