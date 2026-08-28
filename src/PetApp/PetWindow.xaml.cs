using System;
using System.Windows;
using System.Windows.Interop;

namespace PetApp;

public partial class PetWindow : Window
{
    public PetWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNonInteractive(hwnd);
    }
}
