using Avalonia.Controls;

namespace Client.Views.Login;

/// <summary>登录顶层窗口（mstsc 风格独立窗口）。登录成功后由 App 关闭并打开 MainWindow 桌面。</summary>
public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();
}
