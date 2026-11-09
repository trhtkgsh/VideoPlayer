using LibVLCSharp.Shared;
using System.Windows;

namespace VideoPlayer;

public partial class App : Application
{
    public App()
    {
        Core.Initialize();
    }
}

