namespace RDPTools;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instanceMutex = new Mutex(true, @"Local\RDPTools.MsrdcWindowHook", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("RDP Tools is already running.", "RDP Tools", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var applicationContext = new TrayApplicationContext();
        try
        {
            applicationContext.Start();
            Application.Run(applicationContext);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"RDP Tools could not install its input hooks.\n\n{exception.Message}",
                "RDP Tools",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly MsrdcWindowController _windowController;
    private readonly InputHookService _inputHooks;
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _windowController = new MsrdcWindowController();
        _inputHooks = new InputHookService(_windowController);

        var menu = new ContextMenuStrip();
        var enabledItem = new ToolStripMenuItem("Enabled")
        {
            Checked = true,
            CheckOnClick = true,
        };
        enabledItem.CheckedChanged += (_, _) =>
        {
            _inputHooks.Enabled = enabledItem.Checked;
            if (!enabledItem.Checked)
            {
                _windowController.RestoreAll();
            }
        };

        menu.Items.Add(enabledItem);
        menu.Items.Add("Restore managed windows", null, (_, _) => _windowController.RestoreAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = "RDP Tools",
            Visible = true,
        };
    }

    internal void Start() => _inputHooks.Start();

    protected override void ExitThreadCore()
    {
        DisposeResources();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
        }

        base.Dispose(disposing);
    }

    private void DisposeResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inputHooks.Dispose();
        _windowController.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}