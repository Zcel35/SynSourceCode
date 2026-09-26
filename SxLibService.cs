using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace Synapse_UI_WPF.SxLib
{
    public class SxLibClient
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public int State { get; set; }

        public string DisplayText => $"{Name} | PID: {Pid}";

        // тут статусы dll
        //   0 = failed to attach
        //   1 = injecting (attaching)
        //   2 = injected
        //   3 = injected into game
        public Brush StateBrush
        {
            get
            {
                switch (State)
                {
                    case 0: return Brushes.Red;
                    case 1: return Brushes.Yellow;
                    case 2: return Brushes.Cyan;
                    case 3: return Brushes.LightGreen;
                    default: return Brushes.White;
                }
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case 0: return "Failed to attach";
                    case 1: return "Injecting (attaching)";
                    case 2: return "Injected";
                    case 3: return "Injected";
                    default: return "Unknown";
                }
            }
        }

        public override bool Equals(object obj)
        {
            if (!(obj is SxLibClient other)) return false;
            return Pid == other.Pid
                && Name == other.Name
                && Version == other.Version
                && State == other.State;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Pid;
                h = (h * 397) ^ (Name?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Version?.GetHashCode() ?? 0);
                h = (h * 397) ^ State;
                return h;
            }
        }
    }

    public sealed class SxLibService : IDisposable
    {

        [DllImport("sxlib.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetClients();

        [DllImport("sxlib.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr Version();

        [DllImport("sxlib.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern void Execute(byte[] script, int[] PIDs, int count);

        [DllImport("sxlib.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetSetting(int settingID, int value);

        [DllImport("sxlib.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Attach();

        [DllImport("sxlib.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Initialize(bool useConsole);

        private static readonly Lazy<SxLibService> _instance =
            new Lazy<SxLibService>(() => new SxLibService());
        public static SxLibService Instance => _instance.Value;

        public bool IsLoaded { get; private set; }
        public string LoadError { get; private set; } = "";

        public ReadOnlyObservableCollection<SxLibClient> Clients { get; }
        private readonly ObservableCollection<SxLibClient> _clients = new ObservableCollection<SxLibClient>();

        public event Action ClientsChanged;

        public int PollIntervalMs { get; set; } = 250;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _pollTask;
        private List<SxLibClient> _lastSnapshot = new List<SxLibClient>();
        private bool _stopped;

        private SxLibService()
        {
            Clients = new ReadOnlyObservableCollection<SxLibClient>(_clients);
        }

        public void Init(bool useConsole = false)
        {
            var originalDir = Environment.CurrentDirectory;

            try
            {
                Initialize(useConsole);
                IsLoaded = true;
                LoadError = "";

                try { SetSetting(1, 0); } catch { }
                try { SetSetting(0, 0); } catch { }
            }
            catch (DllNotFoundException ex)
            {
                IsLoaded = false;
                LoadError = "DLL not found: " + ex.Message;
            }
            catch (BadImageFormatException ex)
            {
                IsLoaded = false;
                LoadError = "Bad architecture: " + ex.Message;
            }
            catch (Exception ex)
            {
                IsLoaded = false;
                LoadError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try { Environment.CurrentDirectory = originalDir; } catch { }
            }
        }

        public void Start()
        {
            if (_pollTask != null || _stopped) return;
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            try
            {
                if (!_cts.IsCancellationRequested)
                    _cts.Cancel();
            }
            catch { }

            try
            {
                if (_pollTask != null && !_pollTask.IsCompleted)
                    _pollTask.Wait(500);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            try { _cts.Dispose(); } catch { }
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = FetchClients();

                    if (!SnapshotsEqual(_lastSnapshot, snapshot))
                    {
                        _lastSnapshot = snapshot;
                        UpdateCollection(snapshot);
                        try { ClientsChanged?.Invoke(); } catch { }
                    }
                }
                catch { }

                try { await Task.Delay(PollIntervalMs, token); }
                catch (TaskCanceledException) { break; }
                catch (OperationCanceledException) { break; }
            }
        }

        private List<SxLibClient> FetchClients()
        {
            var result = new List<SxLibClient>();
            if (!IsLoaded || _stopped) return result;

            IntPtr ptr;
            try { ptr = GetClients(); }
            catch { return result; }

            if (ptr == IntPtr.Zero) return result;

            string raw = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            JArray jarray;
            try { jarray = JArray.Parse(raw); }
            catch { return result; }

            foreach (JToken token in jarray)
            {
                if (!(token is JArray inner) || inner.Count < 1) continue;

                int pid;
                try { pid = inner[0].Value<int>(); }
                catch { continue; }

                string name = inner.Count >= 2 ? inner[1].Value<string>() : "";
                string version = inner.Count >= 3 ? inner[2].Value<string>() : "";
                int state = inner.Count >= 4 ? inner[3].Value<int>() : -1;

                if (pid <= 0) continue;
                if (state == 0) continue;

                result.Add(new SxLibClient
                {
                    Pid = pid,
                    Name = name,
                    Version = version,
                    State = state
                });
            }

            return result;
        }

        private static bool SnapshotsEqual(List<SxLibClient> a, List<SxLibClient> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private void UpdateCollection(List<SxLibClient> snapshot)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (!snapshot.Any(c => c.Pid == _clients[i].Pid))
                    _clients.RemoveAt(i);
            }

            foreach (var c in snapshot)
            {
                var existing = _clients.FirstOrDefault(x => x.Pid == c.Pid);
                if (existing == null)
                {
                    _clients.Add(c);
                }
                else
                {
                    existing.Name = c.Name;
                    existing.Version = c.Version;
                    existing.State = c.State;
                }
            }
        }

        public void DoAttach()
        {
            if (!IsLoaded) return;
            Attach();
        }

        public void DoExecute(string script, int[] pids)
        {
            if (!IsLoaded || pids == null || pids.Length == 0) return;
            script = script.Replace("printidentity()", "print(\"Current identity is 7\")");
            Execute(Encoding.UTF8.GetBytes(script), pids, pids.Length);
        }

        public int[] GetPids()
        {
            return _clients.Select(c => c.Pid).ToArray();
        }

        public string GetVersion()
        {
            try
            {
                IntPtr p = Version();
                if (p == IntPtr.Zero) return "";
                return Marshal.PtrToStringAnsi(p) ?? "";
            }
            catch { return ""; }
        }
    }
}