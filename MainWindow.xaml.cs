#define USE_UPDATE_CHECKS

using CefSharp;
using CefSharp.Wpf;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Synapse_UI_WPF.Controls;
using Synapse_UI_WPF.Interfaces;
using Synapse_UI_WPF.Static;
using Synapse_UI_WPF.SxLib;
using Synapse_UI_WPF.Watcher;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Process = System.Diagnostics.Process;

namespace Synapse_UI_WPF
{
    public partial class MainWindow
    {
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        public ProcessWatcher Watcher;

        public delegate void InteractMessageEventHandler(object sender, string Input);
        public event InteractMessageEventHandler InteractMessageRecieved;

        [Obfuscation(Feature = "virtualization", Exclude = false)]
        private int RobloxIdOverride;
        [Obfuscation(Feature = "virtualization", Exclude = false)]
        private int RobloxIdTemp;
        [Obfuscation(Feature = "virtualization", Exclude = false)]
        private static int RbxId;

        public bool OptionsOpen;
        public bool ScriptHubOpen;
        private bool ScriptHubInit;
        public bool IsInlineUpdating;
        private bool IsAttaching;
        private bool IsAttached;
        private bool SxLibInitialized;
        private bool IsInGame;

        private System.Windows.Threading.DispatcherTimer WindowWatcher;
        private bool LastWindowState;

        public static bool Debounce;

        private static ThemeInterface.TAttachStrings AttachStrings;

        private readonly string BaseDirectory;
        private readonly string ScriptsDirectory;
        private readonly string AutoexecDirectory;
        private readonly string ExeWorkspaceDirectory;
        private readonly string XenoWorkspaceDirectory;
        private List<string> AutoexecFiles = new List<string>();

        private FileSystemWatcher WorkspaceWatcher;
        private readonly object SyncLock = new object();

        public static BackgroundWorker Worker = new BackgroundWorker();
        public static BackgroundWorker HubWorker = new BackgroundWorker();

        public MainWindow()
        {
            Cef.EnableHighDPISupport();
            var settings = new CefSettings();
            settings.SetOffScreenRenderingBestPerformanceArgs();
            Cef.Initialize(settings);

            InitializeComponent();

            Worker.DoWork += Worker_DoWork;
            HubWorker.DoWork += HubWorker_DoWork;

            StreamReader InteractReader = null;
            StreamReader LaunchReader;

            try
            {
                Watcher = new ProcessWatcher("RobloxPlayerBeta.exe");
                Watcher.ProcessCreated += Proc =>
                {
                    if (!Globals.Options.AutoAttach || Globals.Options.AutoLaunch) return;
                    RobloxIdOverride = Convert.ToInt32(Proc.ProcessId);
                    Worker.RunWorkerAsync();
                };
                Watcher.ProcessDeleted += Proc =>
                {
                    if (Proc.ProcessId == RobloxIdTemp)
                        InteractReader?.Close();
                };
                Watcher.Start();
            }
            catch
            {
                if (!DataInterface.Exists("failnotice"))
                {
                    MessageBox.Show(
                        "Synapse failed to create its process watching agent. AutoLaunch will not be as usable.",
                        "Synapse X", MessageBoxButton.OK, MessageBoxImage.Warning);
                    DataInterface.Save("failnotice", true);
                }
            }

            if (DataInterface.Exists("savedpid"))
            {
                var Saved = DataInterface.Read<Data.SavedPid>("savedpid");
                try
                {
                    if (Process.GetProcessById(Saved.Pid).StartTime == Saved.StartTime)
                        RbxId = Saved.Pid;
                    DataInterface.Delete("savedpid");
                }
                catch { DataInterface.Delete("savedpid"); }
            }

            var TMain = Globals.Theme.Main;
            ThemeInterface.ApplyWindow(this, TMain.Base);
            ThemeInterface.ApplyLogo(IconBox, TMain.Logo);
            ThemeInterface.ApplySeperator(TopBox, TMain.TopBox);
            ThemeInterface.ApplyFormatLabel(TitleBox, TMain.TitleBox, Globals.Version);
            ThemeInterface.ApplyListBox(ScriptBox, TMain.ScriptBox);
            ThemeInterface.ApplyButton(MiniButton, TMain.MinimizeButton);
            ThemeInterface.ApplyButton(CloseButton, TMain.ExitButton);
            ThemeInterface.ApplyButton(ExecuteButton, TMain.ExecuteButton);
            ThemeInterface.ApplyButton(ClearButton, TMain.ClearButton);
            ThemeInterface.ApplyButton(OpenFileButton, TMain.OpenFileButton);
            ThemeInterface.ApplyButton(ExecuteFileButton, TMain.ExecuteFileButton);
            ThemeInterface.ApplyButton(SaveFileButton, TMain.SaveFileButton);
            ThemeInterface.ApplyButton(OptionsButton, TMain.OptionsButton);
            ThemeInterface.ApplyButton(AttachButton, TMain.AttachButton);
            ThemeInterface.ApplyButton(ScriptHubButton, TMain.ScriptHubButton);

            ScaleTransform.ScaleX = Globals.Options.WindowScale;
            ScaleTransform.ScaleY = Globals.Options.WindowScale;

            AttachStrings = TMain.BaseStrings;

            BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            ScriptsDirectory = Path.Combine(BaseDirectory, "scripts");
            if (!Directory.Exists(ScriptsDirectory))
                Directory.CreateDirectory(ScriptsDirectory);

            AutoexecDirectory = Path.Combine(BaseDirectory, "autoexec");
            if (!Directory.Exists(AutoexecDirectory))
                Directory.CreateDirectory(AutoexecDirectory);

            ExeWorkspaceDirectory = Path.Combine(BaseDirectory, "workspace");
            if (!Directory.Exists(ExeWorkspaceDirectory))
                Directory.CreateDirectory(ExeWorkspaceDirectory);

            XenoWorkspaceDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Xeno", "workspace");
            if (!Directory.Exists(XenoWorkspaceDirectory))
                Directory.CreateDirectory(XenoWorkspaceDirectory);

            if (Directory.Exists(ScriptsDirectory))
            {
                foreach (var FilePath in Directory.GetFiles(ScriptsDirectory))
                    ScriptBox.Items.Add(Path.GetFileName(FilePath));
            }

            try
            {
                WorkspaceWatcher = new FileSystemWatcher(XenoWorkspaceDirectory)
                {
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size
                                 | NotifyFilters.CreationTime,
                    Filter = "*.*",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                WorkspaceWatcher.Created += WorkspaceWatcher_Changed;
                WorkspaceWatcher.Changed += WorkspaceWatcher_Changed;
                WorkspaceWatcher.Deleted += WorkspaceWatcher_Changed;
                WorkspaceWatcher.Renamed += WorkspaceWatcher_Renamed;
            }
            catch { }

            try { SyncWorkspace(); } catch { }

            // здесь Pipe syn interact
            new Thread(() =>
            {
                var PS = new PipeSecurity();
                var Rule = new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow);
                PS.AddAccessRule(Rule);
                var Server = new NamedPipeServerStream("SynapseInteract", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, PS);
                Server.WaitForConnection();
                InteractReader = new StreamReader(Server);
                while (true)
                {
                    string Line;
                    try { Line = InteractReader.ReadLine(); }
                    catch { Line = "SYN_INTERRUPT"; }
                    if (string.IsNullOrWhiteSpace(Line)) Line = "SYN_INTERRUPT";
                    InteractMessageRecieved?.Invoke(this, Line);
                    if (Line != "SYN_READY" && Line != "SYN_REATTACH_READY" && Line != "SYN_INTERRUPT") continue;
                    InteractReader.Close();
                    Server.Close();
                    Thread.Sleep(3000);
                    Server = new NamedPipeServerStream("SynapseInteract", PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, PS);
                    Server.WaitForConnection();
                    InteractReader = new StreamReader(Server);
                }
            }).Start();

            // а тут загрузка
            new Thread(() =>
            {
                var PS = new PipeSecurity();
                var Rule = new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow);
                PS.AddAccessRule(Rule);
                var Server = new NamedPipeServerStream("SynapseLaunch", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, PS);
                Server.WaitForConnection();
                LaunchReader = new StreamReader(Server);
                while (true)
                {
                    string Line;
                    try { Line = LaunchReader.ReadLine(); }
                    catch { Line = "SYN_INTERRUPT"; }

                    if (Line?.Split('|')[0] == "SYN_LAUNCH_NOTIIFCATION")
                        RobloxIdTemp = int.Parse(Line.Split('|')[1]);

                    if (Line?.Split('|')[0] != "SYN_LAUNCH_NOTIIFCATION") continue;
                    LaunchReader.Close();
                    Server.Close();
                    Thread.Sleep(3000);
                    Server = new NamedPipeServerStream("SynapseLaunch", PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, PS);
                    Server.WaitForConnection();
                    LaunchReader = new StreamReader(Server);
                }
            }).Start();

            // сэйф редактора
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(15000);
                    try
                    {
                        var EditorText = "";
                        Dispatcher.Invoke(() => { EditorText = Browser.GetText(); });
                        DataInterface.Save("savedws", EditorText);
                    }
                    catch { }
                }
            }).Start();

            InteractMessageRecieved += (Sender, Input) =>
            {
                Dispatcher.Invoke(() =>
                {
                    switch (Input)
                    {
                        case "SYN_CHECK_WL": SetTitle(AttachStrings.CheckingWhitelist); break;
                        case "SYN_SCANNING": SetTitle(AttachStrings.Scanning); break;
                        case "SYN_INTERRUPT": SetTitle(" (failed to attach!)", 3000); break;
                        case "SYN_READY":
                        case "SYN_REATTACH_READY": SetTitle(AttachStrings.Ready, 3000); break;
                    }
                });
            };
        }
         // а тут чего обычный workspace
        private void WorkspaceWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            try { Thread.Sleep(300); } catch { }
            try { SyncWorkspace(); } catch { }
        }

        private void WorkspaceWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            try { Thread.Sleep(300); } catch { }
            try { SyncWorkspace(); } catch { }
        }
        private void SyncWorkspace()
        {
            lock (SyncLock)
            {
                try
                {
                    if (!Directory.Exists(XenoWorkspaceDirectory)) return;
                    if (!Directory.Exists(ExeWorkspaceDirectory))
                        Directory.CreateDirectory(ExeWorkspaceDirectory);

                    SyncDirectoryRecursive(XenoWorkspaceDirectory, ExeWorkspaceDirectory);
                    RemoveExtrasRecursive(XenoWorkspaceDirectory, ExeWorkspaceDirectory);
                }
                catch { }
            }
        }

        private void SyncDirectoryRecursive(string srcDir, string dstDir)
        {
            try
            {
                if (!Directory.Exists(dstDir))
                    Directory.CreateDirectory(dstDir);

                foreach (var srcFile in Directory.GetFiles(srcDir))
                {
                    try
                    {
                        var name = Path.GetFileName(srcFile);
                        if (string.Equals(name, "XenoIcon.png", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var dstFile = Path.Combine(dstDir, name);

                        bool needCopy = true;
                        if (File.Exists(dstFile))
                        {
                            try
                            {
                                var a = File.ReadAllBytes(srcFile);
                                var b = File.ReadAllBytes(dstFile);
                                if (a.Length == b.Length)
                                {
                                    needCopy = false;
                                    for (int i = 0; i < a.Length; i++)
                                    {
                                        if (a[i] != b[i]) { needCopy = true; break; }
                                    }
                                }
                            }
                            catch { needCopy = true; }
                        }

                        if (needCopy)
                            File.Copy(srcFile, dstFile, true);
                    }
                    catch { }
                }

                foreach (var srcSub in Directory.GetDirectories(srcDir))
                {
                    try
                    {
                        var subName = Path.GetFileName(srcSub);
                        var dstSub = Path.Combine(dstDir, subName);
                        SyncDirectoryRecursive(srcSub, dstSub);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void RemoveExtrasRecursive(string srcDir, string dstDir)
        {
            try
            {
                if (!Directory.Exists(dstDir)) return;

                foreach (var dstFile in Directory.GetFiles(dstDir))
                {
                    try
                    {
                        var name = Path.GetFileName(dstFile);
                        if (string.Equals(name, "XenoIcon.png", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var srcFile = Path.Combine(srcDir, name);
                        if (!File.Exists(srcFile))
                            File.Delete(dstFile);
                    }
                    catch { }
                }

                foreach (var dstSub in Directory.GetDirectories(dstDir))
                {
                    try
                    {
                        var subName = Path.GetFileName(dstSub);
                        var srcSub = Path.Combine(srcDir, subName);

                        if (!Directory.Exists(srcSub))
                        {
                            try { Directory.Delete(dstSub, true); } catch { }
                        }
                        else
                        {
                            RemoveExtrasRecursive(srcSub, dstSub);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

       // убиваю крэшхэндлер тк вылетает роблокс
        private void KillCrashHandler()
        {
            try
            {
                var list = Process.GetProcessesByName("RobloxCrashHandler");
                foreach (var p in list)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                    catch { }
                }

                // немного ждем чтобы система отпустила хендлы
                if (list.Length > 0)
                    Thread.Sleep(500);
            }
            catch { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Title = "Synapse X v" + Globals.Version + " - " + WebInterface.RandomString(WebInterface.Rnd.Next(10, 32));

            if (!SxLibInitialized)
            {
                SxLibInitialized = true;
                // не трогай sxlib 
                SxLibService.Instance.Init(false);
                if (!SxLibService.Instance.IsLoaded)
                {
                    MessageBox.Show("sxlib.dll not loaded:\n\n" + SxLibService.Instance.LoadError,
                        "Synapse X", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                SxLibService.Instance.ClientsChanged += OnClientsChanged;
                SxLibService.Instance.Start();

                WindowWatcher = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                WindowWatcher.Tick += WindowWatcher_Tick;
                WindowWatcher.Start();
            }
        }

        private void WindowWatcher_Tick(object sender, EventArgs e)
        {
            bool windowOpen;
            try { windowOpen = IsRobloxWindowOpen(); }
            catch { windowOpen = false; }

            if (LastWindowState && !windowOpen)
            {
                IsAttached = false;
                IsInGame = false;
                try
                {
                    TitleBox.Content = ThemeInterface.ConvertFormatString(
                        Globals.Theme.Main.TitleBox, Globals.Version);
                }
                catch { }
            }

            LastWindowState = windowOpen;
        }

        private void OnClientsChanged()
        {
            Dispatcher.Invoke(() =>
            {
                var clients = SxLibService.Instance.Clients;
                if (clients.Count == 0) return;

                var last = clients[clients.Count - 1];

                switch (last.State)
                {
                    case 2:
                    case 3:
                        if (!IsAttached)
                        {
                            IsAttached = true;
                            SetTitle(AttachStrings.Ready, 3000);
                        }
                        break;

                    case 1:
                        if (!IsAttached)
                            SetTitle(AttachStrings.Injecting);
                        break;

                    case 0:
                        break;
                }

                if (last.State == 3 && !IsInGame)
                {
                    IsInGame = true;
                    RunAutoexec();
                }
                else if (last.State == 2)
                {
                    IsInGame = false;
                }
            });
        }

        private void RunAutoexec()
        {
            try
            {
                AutoexecFiles = new List<string>();

                if (!Directory.Exists(AutoexecDirectory))
                    Directory.CreateDirectory(AutoexecDirectory);

                var all = Directory.GetFiles(AutoexecDirectory);
                AutoexecFiles.AddRange(all);

                foreach (var path in AutoexecFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(path);
                        if (string.IsNullOrWhiteSpace(content)) continue;
                        Execute(content);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void SetTitle(string Str, int Delay = 0)
        {
            Dispatcher.Invoke(() =>
            {
                TitleBox.Content =
                    ThemeInterface.ConvertFormatString(Globals.Theme.Main.TitleBox, Globals.Version) + Str;
            });

            if (Delay != 0)
            {
                new Thread(() =>
                {
                    Thread.Sleep(Delay);
                    Dispatcher.Invoke(() =>
                    {
                        TitleBox.Content =
                            ThemeInterface.ConvertFormatString(Globals.Theme.Main.TitleBox, Globals.Version);
                    });
                }).Start();
            }
        }

        public bool Ready()
        {
            return IsAttached;
        }

        public void Execute(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            if (!SxLibService.Instance.IsLoaded) return;
            if (!IsAttached) return;

            try
            {
                var pids = SxLibService.Instance.GetPids();
                if (pids.Length == 0) return;

                SxLibService.Instance.DoExecute(data, pids);
            }
            catch { }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && !Debounce)
                DragMove();
        }

        public void Attach_()
        {
            if (Worker.IsBusy || IsInlineUpdating) return;
            Worker.RunWorkerAsync();
        }

        public void SetEditor(string Text) => Browser.SetText(Text);

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                try
                {
                    var text = Browser.GetText();
                    DataInterface.Save("savedws", text);
                }
                catch { }

                try { SxLibService.Instance.Stop(); } catch { }
                try { WorkspaceWatcher?.Dispose(); } catch { }
                try { Application.Current.Shutdown(); } catch { }
            }
            catch { }

            Environment.Exit(0);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            try
            {
                try
                {
                    var text = Browser.GetText();
                    DataInterface.Save("savedws", text);
                }
                catch { }

                try { SxLibService.Instance.Stop(); } catch { }
                try { WindowWatcher?.Stop(); } catch { }
                try { WorkspaceWatcher?.Dispose(); } catch { }
            }
            catch { }

            Environment.Exit(0);
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (OptionsOpen) return;
            Debounce = true;
            try
            {
                var Options = new OptionsWindow(this);
                Options.Show();
            }
            catch { }
            finally { Debounce = false; }
        }

        private void MiniButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            try { Browser.SetText(""); } catch { }
        }

        private void IconBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            MessageBox.Show("Synapse X was developed by 3dsboy08, brack4712, Louka, DefCon42, and Eternal.",
                "Synapse X Credits", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            var OpenDialog = new OpenFileDialog
            {
                Filter = "Script Files (*.lua, *.txt)|*.lua;*.txt",
                Title = "Synapse X - Open File",
                FileName = ""
            };
            if (OpenDialog.ShowDialog() != true) return;
            try { Browser.SetText(File.ReadAllText(OpenDialog.FileName)); }
            catch (Exception ex) { Console.WriteLine(ex); }
        }

        private void ExecuteFileButton_Click(object sender, RoutedEventArgs e)
        {
            var OpenDialog = new OpenFileDialog
            {
                Filter = "Script Files (*.lua, *.txt)|*.lua;*.txt",
                Title = "Synapse X - Execute File",
                FileName = ""
            };
            if (OpenDialog.ShowDialog() != true) return;

            try { Execute(File.ReadAllText(OpenDialog.FileName)); }
            catch { }
        }

        private void SaveFileButton_Click(object sender, RoutedEventArgs e)
        {
            var SaveDialog = new SaveFileDialog
            {
                Filter = "Script Files (*.lua, *.txt)|*.lua;*.txt",
                FileName = ""
            };
            SaveDialog.FileOk += (o, args) =>
            {
                try
                {
                    var text = Browser.GetText();
                    File.WriteAllText(SaveDialog.FileName, text);
                }
                catch { }
            };
            SaveDialog.ShowDialog();
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (Worker.IsBusy) return;
            Worker.RunWorkerAsync();
        }

        private void ScriptHubButton_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptHubOpen || ScriptHubInit) return;

            ScriptHubOpen = true;
            ScriptHubInit = true;

            ScriptHubButton.Content = Globals.Theme.Main.ScriptHubButton.TextYield;

            new Thread(() =>
            {
                Thread.Sleep(1200);

                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        HubWorker.RunWorkerAsync();
                    }
                    catch
                    {
                        ScriptHubInit = false;
                        ScriptHubButton.Content = Globals.Theme.Main.ScriptHubButton.TextNormal;
                    }
                });
            }).Start();
        }

        private void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var data = Browser.GetText();
                Execute(data);
            }
            catch { }
        }

        private void ExecuteItem_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptBox.SelectedIndex == -1) return;
            try
            {
                var Element = ScriptBox.Items[ScriptBox.SelectedIndex].ToString();
                Execute(File.ReadAllText(Path.Combine(ScriptsDirectory, Element)));
            }
            catch { }
        }

        private void LoadItem_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptBox.SelectedIndex == -1) return;
            try
            {
                var Element = ScriptBox.Items[ScriptBox.SelectedIndex].ToString();
                Browser.SetText(File.ReadAllText(Path.Combine(ScriptsDirectory, Element)));
            }
            catch
            {
                MessageBox.Show("Failed to read file. Check if it is accessible.",
                    "Synapse X", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RefreshItem_Click(object sender, RoutedEventArgs e)
        {
            ScriptBox.Items.Clear();
            if (!Directory.Exists(ScriptsDirectory)) return;
            foreach (var FilePath in Directory.GetFiles(ScriptsDirectory))
                ScriptBox.Items.Add(Path.GetFileName(FilePath));
        }

        private void HubWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");

            var data = new Data.ScriptHubHolder
            {
                Entries = new List<Data.ScriptHubEntry>
                {
                    new Data.ScriptHubEntry
                    {
                        Name = "Dark Dex",
                        Description = "A version of popular Dex explorer with patches specifically for Synapse X.",
                        Url = "https://github.com/AZYsGithub/DexPlusPlus/releases/latest/download/out.lua",
                        Picture = new Uri(Path.Combine(assetsDir, "Dex.jpeg")).AbsoluteUri
                    },
                    new Data.ScriptHubEntry
                    {
                        Name = "Unnamed ESP",
                        Description = "ESP Made by ic3w0lf using the Drawing API.",
                        Url = "https://raw.githubusercontent.com/Zcel35/Unnamed-ESP-for-all-exploits/refs/heads/main/Unnamed%20ESP.txt",
                        Picture = new Uri(Path.Combine(assetsDir, "ESP.jpeg")).AbsoluteUri
                    },
                    new Data.ScriptHubEntry
                    {
                        Name = "Remote Spy",
                        Description = "Allows you to view RemoteEvents and RemoteFunctions called.",
                        Url = "https://raw.githubusercontent.com/infyiff/backup/main/SimpleSpyV3/main.lua",
                        Picture = new Uri(Path.Combine(assetsDir, "rspy.png")).AbsoluteUri
                    },
                    new Data.ScriptHubEntry
                    {
                        Name = "Script Dumper",
                        Description = "Saveinstance() - Dumps the place as a .rbxl file in your workspace folder.",
                        Url = "saveinstance({decomptype = new})",
                        Picture = new Uri(Path.Combine(assetsDir, "dump.png")).AbsoluteUri
                    }
                }
            };

            Dispatcher.Invoke(() =>
            {
                ScriptHubInit = false;
                ScriptHubButton.Content = Globals.Theme.Main.ScriptHubButton.TextNormal;

                var ScriptHub = new ScriptHubWindow(this, data);
                ScriptHub.Show();
            });
        }

        public void InlineAutoUpdate()
        {
            SetTitle(" (not running latest version! updating...)", 3000);
            IsInlineUpdating = false;
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            if (!SxLibService.Instance.IsLoaded)
            {
                Dispatcher.Invoke(() => SetTitle(" (sxlib.dll not loaded)", 3000));
                return;
            }

            IsAttaching = true;
            try
            {
                KillCrashHandler();

                Dispatcher.Invoke(() => SetTitle(AttachStrings.Scanning));
                Thread.Sleep(1000);

                if (!IsRobloxWindowOpen())
                {
                    Dispatcher.Invoke(() => SetTitle(AttachStrings.FailedToFindRoblox, 3000));
                    return;
                }

                Dispatcher.Invoke(() => SetTitle(AttachStrings.Injecting));
                SxLibService.Instance.DoAttach();

                for (int i = 0; i < 100; i++)
                {
                    if (IsAttached) break;
                    Thread.Sleep(100);
                }

                if (!IsAttached)
                {
                    Dispatcher.Invoke(() => SetTitle(AttachStrings.NotInjected, 3000));
                    return;
                }

                Dispatcher.Invoke(() => SetTitle(AttachStrings.Scanning));
                Thread.Sleep(500);

                if (!IsRobloxWindowOpen())
                {
                    Dispatcher.Invoke(() => SetTitle(AttachStrings.FailedToFindRoblox, 3000));
                    return;
                }

                Dispatcher.Invoke(() => SetTitle(AttachStrings.CheckingWhitelist));
                Thread.Sleep(1500);

                Dispatcher.Invoke(() => SetTitle(AttachStrings.Ready, 3000));
            }
            catch
            {
                Dispatcher.Invoke(() => SetTitle(AttachStrings.NotInjected, 3000));
            }
            finally
            {
                IsAttaching = false;
            }
        }

        private bool IsRobloxWindowOpen()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (!IsWindowVisible(p.MainWindowHandle)) continue;
                    if (string.IsNullOrEmpty(p.MainWindowTitle)) continue;

                    RECT r;
                    if (!GetWindowRect(p.MainWindowHandle, out r)) continue;
                    if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) continue;

                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
// наконец то конец файла