namespace MagicCircle;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 二重起動するとフックが二重に掛かるため、単一インスタンスに限定する
        using var mutex = new Mutex(true, @"Local\MagicCircle.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("魔法陣はすでに起動しています。タスクトレイを確認してください。",
                "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}
