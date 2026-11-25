using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace LGLauncher
{
    internal sealed class GameSessions : IDisposable
    {
        private sealed class Session
        {
            public Process Process;
            public Stopwatch Clock;
        }

        private readonly Dictionary<string, Session> active = new Dictionary<string, Session>();
        private readonly Dictionary<string, long> completed = new Dictionary<string, long>();
        private readonly object gate = new object();
        public event Action<string, long> Ended;

        public int Count { get { lock (gate) return active.Count; } }
        public bool IsRunning(string id) { lock (gate) return active.ContainsKey(id); }

        public void Launch(string id, string path, string arguments, bool setProcessPriority = false, bool trackPlaytime = true)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The executable could not be found. Edit the game to update its location.", path);
            lock (gate)
            {
                if (active.ContainsKey(id)) throw new InvalidOperationException("This game is already running.");
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(path, arguments ?? "")
                    {
                        WorkingDirectory = Path.GetDirectoryName(path),
                        UseShellExecute = true
                    }
                };
                try
                {
                    process.Exited += (sender, args) => Finish(id, process);
                    if (!process.Start()) throw new InvalidOperationException("Windows did not return a game process.");
                    active.Add(id, new Session { Process = process, Clock = trackPlaytime ? Stopwatch.StartNew() : null });
                    if (setProcessPriority && Environment.OSVersion.Platform == PlatformID.Win32NT)
                        TrySetHighPriority(process);
                    process.EnableRaisingEvents = true;
                }
                catch
                {
                    active.Remove(id);
                    process.Dispose();
                    throw;
                }
            }
        }

        private static void TrySetHighPriority(Process process)
        {
            // A short-lived or protected executable may reject the change. Keep tracking it.
            try { process.PriorityClass = ProcessPriorityClass.High; }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
        }

        private void Finish(string id, Process process)
        {
            long seconds;
            lock (gate)
            {
                Session session;
                if (!active.TryGetValue(id, out session) || session.Process != process) return;
                seconds = session.Clock == null ? 0 : (long)session.Clock.Elapsed.TotalSeconds;
                active.Remove(id);
                if (session.Clock != null) completed[id] = seconds;
                process.Dispose();
            }
            var handler = Ended;
            if (handler != null) handler(id, seconds);
        }

        public Dictionary<string, long> CurrentTime()
        {
            lock (gate)
            {
                var result = new Dictionary<string, long>(completed);
                foreach (var pair in active)
                    if (pair.Value.Clock != null) result[pair.Key] = (long)pair.Value.Clock.Elapsed.TotalSeconds;
                return result;
            }
        }

        public long Acknowledge(string id)
        {
            lock (gate) return completed.Remove(id, out var seconds) ? seconds : 0;
        }

        public void StopTrackingPlaytime()
        {
            lock (gate)
            {
                foreach (var session in active.Values) session.Clock = null;
                completed.Clear();
            }
        }

        public void ResetPlaytime()
        {
            lock (gate)
            {
                foreach (var session in active.Values) session.Clock?.Restart();
                completed.Clear();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (var session in active.Values) session.Process.Dispose();
                active.Clear();
                completed.Clear();
            }
        }
    }
}
