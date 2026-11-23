using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Jeapfrog Emulator")]
[assembly: AssemblyProduct("Jeapfrog Emulator")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

static class Program
{
    static string Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [STAThread]
    static int Main(string[] args)
    {
        var data = Option(args, "--data") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jeapfrog");
        try
        {
            var engine = new Engine(data, Option(args, "--engine"));
            if (args.Contains("--test"))
            {
                engine.Setup(null, false, (text, percent) => engine.WriteLog(text)).GetAwaiter().GetResult();
                var sample = engine.Catalog();
                if (sample.Count < 80) throw new InvalidDataException("Catalog is incomplete.");
                string html = "<a href='" + sample[0].Download.AbsoluteUri + "'>Game</a>" +
                    "<a href='https://example.com/LeapFrog/Explorer/test.7z'>Other</a>";
                if (Engine.ReadCatalog(html).Count != 1) throw new InvalidDataException("Catalog parser failed.");
                var carts = Option(args, "--import");
                if (carts != null)
                    foreach (var file in carts.Split('|'))
                        engine.Import(file, Path.GetFileNameWithoutExtension(file)).GetAwaiter().GetResult();
                engine.WriteLog("Test passed. Catalog: " + sample.Count + ". Installed: " + engine.Installed().Count + ".");
                return 0;
            }
            using (var single = new Mutex(false, "Local\\JeapfrogEmulator"))
            {
                if (!single.WaitOne(0))
                {
                    MessageBox.Show("Jeapfrog is already open.", "Jeapfrog Emulator");
                    return 0;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var window = new MainWindow(engine))
                        Application.Run(window);
                }
                finally { single.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception exception)
        {
            if (args.Contains("--test"))
            {
                File.AppendAllText(Path.Combine(data, "jeapfrog.log"), exception + Environment.NewLine);
                return 1;
            }
            MessageBox.Show(exception.Message, "Jeapfrog Emulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

sealed class MainWindow : Form
{
    readonly Engine engine;
    readonly ToolStripButton setup = new ToolStripButton("Setup");
    readonly ToolStripButton open = new ToolStripButton("Open…");
    readonly ToolStripButton play = new ToolStripButton("Play");
    readonly ToolStripButton stop = new ToolStripButton("Stop");
    readonly ToolStripButton refresh = new ToolStripButton("Refresh");
    readonly ToolStripTextBox search = new ToolStripTextBox();
    readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
    readonly ToolStripProgressBar progress = new ToolStripProgressBar();
    readonly TabControl tabs = new TabControl();
    readonly ListView local = new ListView();
    readonly ListView online = new ListView();
    readonly ToolStripMenuItem software = new ToolStripMenuItem("Software Rendering");
    readonly List<ToolStripItem> operations = new List<ToolStripItem>();
    readonly Image playIcon = Glyph("\uE768", Color.SeaGreen);
    readonly Image downloadIcon = Glyph("\uE896", SystemColors.ControlText);
    List<Game> catalog;
    List<Game> installed;
    bool busy, playing, closeAfterStop;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public MainWindow(Engine engine)
    {
        this.engine = engine;
        Text = "Jeapfrog Emulator";
        Font = new Font("Segoe UI", 9F);
        Icon = SystemIcons.Application;
        MinimumSize = new Size(590, 350);
        Size = new Size(880, 555);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Control;
        AutoScaleMode = AutoScaleMode.Dpi;
        AllowDrop = true;
        catalog = engine.Catalog();
        installed = engine.Installed();
        ListStyle(local);
        ListStyle(online);
        online.Columns.Add("Status", 100);
        AddTab("Installed", local);
        AddTab("Game Library", online);
        tabs.Dock = DockStyle.Fill;
        tabs.Selecting += (s, e) => { if (busy && !playing) e.Cancel = true; };
        tabs.SelectedIndexChanged += (s, e) => { Populate(); UpdateActions(); };
        tabs.SelectedIndex = installed.Count > 0 ? 0 : 1;
        search.AutoSize = false;
        search.Width = 190;
        search.Alignment = ToolStripItemAlignment.Right;
        search.ToolTipText = "Search";
        search.TextChanged += (s, e) => Populate();
        search.TextBox.HandleCreated += (s, e) => SetCue();
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Stretch = true, Padding = new Padding(6, 4, 7, 4) };
        setup.Image = Glyph("\uE713", SystemColors.ControlText);
        open.Image = Glyph("\uE838", SystemColors.ControlText);
        stop.Image = Glyph("\uE71A", SystemColors.ControlText);
        refresh.Image = Glyph("\uE72C", SystemColors.ControlText);
        toolbar.Items.AddRange(new ToolStripItem[] { setup, open, new ToolStripSeparator(), play, stop,
            new ToolStripSeparator(), refresh, search });
        setup.Click += async (s, e) => await Setup(null, false);
        open.Click += async (s, e) => await Open();
        play.Click += async (s, e) => await ActivateEntry();
        stop.Click += (s, e) => Stop();
        refresh.Click += async (s, e) => await RefreshCatalog();
        var menu = MakeMenu();
        MainMenuStrip = menu;
        var footer = new StatusStrip { SizingGrip = true };
        status.Spring = true;
        status.TextAlign = ContentAlignment.MiddleLeft;
        progress.Width = 150;
        progress.Visible = false;
        footer.Items.Add(status);
        footer.Items.Add(progress);
        Controls.Add(tabs);
        Controls.Add(footer);
        Controls.Add(toolbar);
        Controls.Add(menu);
        DragEnter += (s, e) => { if (!busy && !playing && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (s, e) => { if (!busy && !playing) await ImportFiles((string[])e.Data.GetData(DataFormats.FileDrop)); };
        Shown += (s, e) => SetCue();
        FormClosing += CloseWindow;
        IdleStatus();
        Populate();
    }

    void SetCue() { SendMessage(search.TextBox.Handle, 0x1501, IntPtr.Zero, "Search games"); }

    static Image Glyph(string symbol, Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe MDL2 Assets", 13, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(symbol, font, brush, new RectangleF(0, 0, 16, 16), format);
        }
        return bitmap;
    }

    MenuStrip MakeMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        var emulation = new ToolStripMenuItem("&Emulation");
        var tools = new ToolStripMenuItem("&Tools");
        var help = new ToolStripMenuItem("&Help");
        BindMenu(file, "&Open…", open, Keys.Control | Keys.O);
        BindMenu(file, "S&etup", setup);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => Close()));
        BindMenu(emulation, "&Play / Download", play, Keys.F5);
        BindMenu(emulation, "&Stop", stop, Keys.Shift | Keys.F5);
        software.CheckOnClick = true;
        emulation.DropDownItems.Add(software);
        AddOperation(tools, "Firmware &Update", async (s, e) => await Setup(null, true));
        AddOperation(tools, "Firmware &Cache…", async (s, e) => await FirmwareCache(false));
        AddOperation(tools, "Firmware &File…", async (s, e) => await FirmwareCache(true));
        AddOperation(tools, "&Didj Support", async (s, e) => await SetupThen(() => engine.Didj(Report), "Installing Didj support…"));
        AddOperation(tools, "&Engine Tools", (s, e) => EngineTools());
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(new ToolStripMenuItem("Open &Data Folder", null, (s, e) => Shell(engine.Data)));
        tools.DropDownItems.Add(new ToolStripMenuItem("Open &Log", null, (s, e) => Shell(File.Exists(engine.Log) ? engine.Log : engine.Data)));
        tools.DropDownItems.Add(new ToolStripMenuItem("Open &Game Log", null, (s, e) => Shell(File.Exists(engine.GameLog) ? engine.GameLog : engine.Data)));
        help.DropDownItems.Add(new ToolStripMenuItem("&Controls", null, (s, e) => ControlsDialog()));
        help.DropDownItems.Add(new ToolStripMenuItem("Game &Source", null, (s, e) => Shell(Engine.CatalogUrl.AbsoluteUri)));
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (s, e) =>
            MessageBox.Show(this, "Jeapfrog Emulator 0.1\nTadpole / Glasspole engine", "About Jeapfrog")));
        menu.Items.AddRange(new ToolStripItem[] { file, emulation, tools, help });
        return menu;
    }

    static void BindMenu(ToolStripMenuItem parent, string title, ToolStripButton target, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(title, null, (s, e) => target.PerformClick()) { ShortcutKeys = shortcut };
        parent.DropDownOpening += (s, e) => item.Enabled = target.Enabled;
        parent.DropDownItems.Add(item);
    }

    void AddOperation(ToolStripMenuItem parent, string title, EventHandler action)
    {
        var item = new ToolStripMenuItem(title, null, action);
        parent.DropDownItems.Add(item);
        operations.Add(item);
    }

    void ControlsDialog()
    {
        MessageBox.Show(this, "Mouse\tStylus\nArrow keys\tD-pad\nX / Z\tA / B\nQ / W\tL / R\nHome\tMenu\nEsc\tBack\n− / =\tVolume",
            "Controls", MessageBoxButtons.OK);
    }

    void ListStyle(ListView view)
    {
        view.Dock = DockStyle.Fill;
        view.View = View.Details;
        view.FullRowSelect = true;
        view.MultiSelect = false;
        view.HideSelection = false;
        view.Columns.Add("Game", 440);
        view.Columns.Add("Region", 95);
        view.Columns.Add("Version", 120);
        view.SizeChanged += (s, e) =>
            view.Columns[0].Width = Math.Max(220, view.ClientSize.Width - view.Columns.Cast<ColumnHeader>().Skip(1).Sum(c => c.Width) - 21);
        view.SelectedIndexChanged += (s, e) => UpdateActions();
        view.DoubleClick += async (s, e) => await ActivateEntry();
        view.KeyDown += async (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; await ActivateEntry(); }
        };
    }

    void AddTab(string title, ListView view)
    {
        var page = new TabPage(title) { Padding = new Padding(3) };
        page.Controls.Add(view);
        tabs.TabPages.Add(page);
    }

    ListView ActiveList { get { return tabs.SelectedIndex == 0 ? local : online; } }
    Game Selected { get { return ActiveList.SelectedItems.Count == 0 ? null : (Game)ActiveList.SelectedItems[0].Tag; } }
    Game Target(Game game) { return game == null || game.Download == null ? game : engine.Match(game, installed); }

    void Populate()
    {
        string term = search.Text.Trim();
        Fill(local, installed, term);
        Fill(online, catalog, term);
        UpdateActions();
    }

    void Fill(ListView view, IEnumerable<Game> source, string term)
    {
        var selected = view.SelectedItems.Count > 0 ? (Game)view.SelectedItems[0].Tag : null;
        view.BeginUpdate();
        try
        {
            view.Items.Clear();
            foreach (var game in source)
            {
                string combined = game.Name + " " + game.Region + " " + game.Version;
                if (combined.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                var row = new ListViewItem(new[] { game.Name, game.Region, game.Version, "" }) { Tag = game };
                if (game.Download != null) row.SubItems[3].Text = Target(game) != null ? "Installed" : "";
                view.Items.Add(row);
                if (selected != null && (game.SourceName ?? game.Id) == (selected.SourceName ?? selected.Id)) row.Selected = true;
            }
        }
        finally { view.EndUpdate(); }
    }

    void UpdateActions()
    {
        bool idle = !busy && !playing;
        var game = Selected;
        setup.Enabled = idle && !engine.Ready;
        open.Enabled = idle;
        refresh.Enabled = idle && tabs.SelectedIndex == 1;
        play.Enabled = idle && game != null;
        bool download = game != null && Target(game) == null;
        play.Text = download ? "Download" : "Play";
        play.Image = download ? downloadIcon : playIcon;
        stop.Enabled = playing;
        foreach (var item in operations) item.Enabled = idle;
    }

    void IdleStatus()
    {
        progress.Visible = false;
        status.Text = engine.Ready ? "Ready" : "Setup required";
        UpdateActions();
    }

    void Report(string message, int? percent)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Report(message, percent))); return; }
        status.Text = message;
        progress.Visible = busy;
        progress.Style = percent.HasValue ? ProgressBarStyle.Blocks : ProgressBarStyle.Marquee;
        if (percent.HasValue) progress.Value = Math.Max(0, Math.Min(100, percent.Value));
    }

    async Task Work(Func<Task> action, string message)
    {
        if (busy || playing) return;
        busy = true;
        UpdateActions();
        Report(message, null);
        try { await action(); }
        catch (Exception error)
        {
            engine.WriteLog(error.ToString());
            string display = error is System.Net.Http.HttpRequestException ? "Download failed. Check your connection." : error.Message;
            MessageBox.Show(this, display, "Jeapfrog Emulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            installed = engine.Installed();
            Populate();
            IdleStatus();
        }
    }

    async Task Setup(string cache, bool force)
    {
        await Work(() => engine.Setup(cache, force, Report), "Setting up…");
        if (installed.Count > 0) tabs.SelectedIndex = 0;
    }

    Task SetupThen(Func<Task> operation, string label)
    {
        return Work(async () =>
        {
            if (!engine.Ready) await engine.Setup(null, false, Report);
            Report(label, null);
            await operation();
        }, label);
    }

    async Task RefreshCatalog()
    {
        await Work(async () => { catalog = await engine.RefreshCatalog(); }, "Refreshing library…");
    }

    async Task ActivateEntry()
    {
        if (busy || playing) return;
        var game = Selected;
        if (game == null) return;
        var target = Target(game);
        if (target == null)
        {
            await SetupThen(() => engine.DownloadGame(game, Report), "Downloading game…");
            return;
        }
        playing = true;
        status.Text = target.Name;
        UpdateActions();
        try { await engine.Play(target, software.Checked); }
        catch (Exception error)
        {
            engine.WriteLog(error.ToString());
            if (!closeAfterStop) MessageBox.Show(this, "Game stopped. See Tools → Open Game Log.",
                "Jeapfrog Emulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            playing = false;
            IdleStatus();
            if (closeAfterStop) Close();
        }
    }

    async Task Open()
    {
        using (var dialog = new OpenFileDialog
        {
            Title = "Open Game", Multiselect = true,
            Filter = "LeapFrog games (*.7z;*.bin;*.tar;*.zip)|*.7z;*.bin;*.tar;*.zip|All files (*.*)|*.*"
        })
            if (dialog.ShowDialog(this) == DialogResult.OK) await ImportFiles(dialog.FileNames);
    }

    async Task ImportFiles(string[] files)
    {
        await SetupThen(async () =>
        {
            foreach (var file in files)
                await engine.Import(file, Path.GetFileNameWithoutExtension(file));
        }, "Importing game…");
        if (engine.Ready) tabs.SelectedIndex = 0;
    }

    async Task FirmwareCache(bool singleFile)
    {
        if (singleFile)
        {
            using (var dialog = new OpenFileDialog { Title = "Firmware File", Filter = "Firmware files|*.lfp;*.lf2;*.zip" })
                if (dialog.ShowDialog(this) == DialogResult.OK) await Setup(dialog.FileName, true);
        }
        else
            using (var dialog = new FolderBrowserDialog { ShowNewFolderButton = false, Description = "Firmware Cache" })
                if (dialog.ShowDialog(this) == DialogResult.OK) await Setup(dialog.SelectedPath, true);
    }

    void EngineTools()
    {
        if (!engine.Ready) return;
        var info = new ProcessStartInfo(Path.Combine(engine.Root, "tadpole.exe"))
        { WorkingDirectory = engine.Root, UseShellExecute = false };
        info.EnvironmentVariables["TADPOLE_DIR"] = Path.Combine(engine.Root, "run");
        info.EnvironmentVariables["XDG_STATE_HOME"] = Path.Combine(engine.Root, "state");
        info.EnvironmentVariables["XDG_CONFIG_HOME"] = Path.Combine(engine.Root, "config");
        info.EnvironmentVariables["XDG_CACHE_HOME"] = Path.Combine(engine.Root, "cache");
        try { Process.Start(info); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Jeapfrog Emulator"); }
    }

    void Stop()
    {
        if (!playing) return;
        stop.Enabled = false;
        status.Text = "Stopping…";
        engine.Stop();
    }

    void CloseWindow(object sender, FormClosingEventArgs e)
    {
        if (busy || playing) e.Cancel = true;
        if (!playing) return;
        closeAfterStop = true;
        Stop();
    }

    void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Jeapfrog Emulator"); }
    }
}
