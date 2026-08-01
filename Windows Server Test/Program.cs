using Windows_Server_Tester.Categories.Authentication;

// 用法：
//   非交互：dotnet run -- <username> <password>
//   交互  ：dotnet run
// 注意：命令行参数模式密码会出现在进程列表中，仅用于本地演示/测试。

if (args.Length >= 2)
{
    var r = WindowsCredentialVerifier.Verify(args[0], args[1]);
    PrintResult(r);
    return r.Success ? 0 : 1;
}

Console.WriteLine("=== Windows 凭据验证（演示）===");
Console.Write("请输入账号: ");
var username = Console.ReadLine() ?? string.Empty;
Console.Write("请输入密码: ");
var password = ReadPassword();
Console.WriteLine();

var result = WindowsCredentialVerifier.Verify(username, password);
PrintResult(result);
return result.Success ? 0 : 1;

static string ReadPassword()
{
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Remove(sb.Length - 1, 1);
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }
    return sb.ToString();
}

static void PrintResult(CredentialVerifyResult r)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = r.Success
        ? ConsoleColor.Green
        : r.Error == CredentialError.InvalidInput ? ConsoleColor.Yellow : ConsoleColor.Red;
    Console.WriteLine($"[{(r.Success ? "PASS" : "FAIL")}] {r.Message}");
    Console.ForegroundColor = prev;
    if (r.Win32ErrorCode.HasValue)
        Console.WriteLine($"  Win32 错误码: {r.Win32ErrorCode}  错误类型: {r.Error}");
}
