using Codeer.Friendly;
using Codeer.Friendly.Windows;
using Codeer.Friendly.Windows.Grasp;
using Microsoft.Win32;
using RM.Friendly.WPFStandardControls;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HideYukarisan
{
    static class Program
    {
        static Voiceroid2 v;
        static ReaderWriterLockSlim engineUpdating = new ReaderWriterLockSlim();
        public static Voiceroid2 Engine()
        {
            engineUpdating.EnterReadLock();
            try
            {
                return v;
            }
            finally
            {
                engineUpdating.ExitReadLock();
            }
        }

        public static void ResetEngine()
        {
            engineUpdating.EnterWriteLock();
            try
            {
                if (v != null)
                {
                    v.Dispose();
                }
                v = new Voiceroid2();
            }
            finally
            {
                engineUpdating.ExitWriteLock();
            }
        }

        /// <summary>
        /// アプリケーションのメイン エントリ ポイントです。
        /// </summary>
        [STAThread]
        static void Main()
        {
            ResetEngine();
            new Task(serverStart).Start();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

        static void serverStart()
        {
            var lis = new TcpListener(IPAddress.Loopback, 4532);
            lis.Start();
            while (true)
            {
                var client = lis.AcceptTcpClient();
                Task.Run(() => handler(client));
            }
        }

        static async void handler(TcpClient client)
        {
            using (var bw = new BinaryReader(client.GetStream()))
            {
                try
                {
                    var command = bw.ReadInt16();
                    var speed = bw.ReadInt16();
                    var tone = bw.ReadInt16();
                    var volume = bw.ReadInt16();
                    var voice = bw.ReadInt16();
                    var encoding = bw.ReadByte();
                    var msgLen = bw.ReadInt32();
                    var messageBytes = bw.ReadBytes(msgLen);
                    if (encoding != 0)
                    {
                        Console.WriteLine("Unsupproted encoding: " + encoding);
                    }
                    Engine().Play(Encoding.UTF8.GetString(messageBytes));
                }
                catch (EndOfStreamException eof)
                {

                }
            }
        }

    }

    class Voiceroid2 : IDisposable
    {
        bool cancelled = false;
        BlockingCollection<string> messageQueue;
        WindowsAppFriend _app = null;
        WindowControl uiTreeTop = null;

        WPFTextBox talkTextBox = null;
        WPFButtonBase playButton = null;
        WPFButtonBase stopButton = null;
        WPFButtonBase beginButton = null;

        Process voiceroidProcess;
        Task initTask;

        void initVoiceroid2()
        {
            voiceroidProcess = GetOrStartVoiceroid();
            var timeout = TimeSpan.FromSeconds(30);
            var begin = DateTime.Now;
            _app = new WindowsAppFriend(voiceroidProcess);
            while (DateTime.Now - begin < timeout)
            {
                try
                {
                    uiTreeTop = WindowControl.FromZTop(_app);
                    var editUis = uiTreeTop.GetFromTypeFullName("AI.Talk.Editor.TextEditView")[0].LogicalTree();

                    talkTextBox = new WPFTextBox(editUis[4]);
                    playButton = new WPFButtonBase(editUis[6]);
                    stopButton = new WPFButtonBase(editUis[11]);
                    beginButton = new WPFButtonBase(editUis[15]);
                    break;
                }
                catch (Exception ev2)
                {
                    Console.WriteLine("waiting VOICEROID2 init :{0}", ev2.Message + ev2.StackTrace);
                    Thread.Sleep(1000);
                }
            }
        }

        public Voiceroid2()
        {
            Console.WriteLine(FindVoiceroid2Path());
            initTask = Task.Run((Action)initVoiceroid2);
            Task.Run((Action)consumeMessage);
            messageQueue = new BlockingCollection<string>();
        }

        async void consumeMessage()
        {
            await initTask;
            var lastPlay = DateTime.Now;
            var talkCooldown = TimeSpan.FromSeconds(0.3);
            while (!cancelled)
            {
                string message;
                if (!messageQueue.TryTake(out message, 1000))
                {
                    continue;
                }
                // VOICEROID2が発話中の時は「先頭」ボタンが無効になるので、それを利用して発話中かどうかを判定します
                while (!beginButton.IsEnabled)
                {
                    Thread.Sleep(50); // spin wait
                }
                var now = DateTime.Now;
                if ((now - lastPlay) < talkCooldown)
                {
                    Thread.Sleep(talkCooldown - (now - lastPlay));
                }
                lastPlay = DateTime.Now;
                stopButton.EmulateClick();
                talkTextBox.EmulateChangeText(message);
                beginButton.EmulateClick();
                playButton.EmulateClick();
            }
        }

        public async void Play(string text, bool async = false)
        {
            if (cancelled) return;
            await initTask;
            messageQueue.Add(text);
        }

        private List<string> FindVoiceroid2Path()
        {
            var parent = Registry.ClassesRoot.OpenSubKey(@"Installer\Assemblies");
            var children = parent.GetSubKeyNames()
                .Where(k => k.EndsWith("VoiceroidEditor.exe"))
                .Select(k => k.Replace("|", "\\"))
                .ToList();
            return children;
        }

        private Process GetOrStartVoiceroid()
        {
            var p = GetVoiceroidEditorProcess();
            if (p != null)
            {
                return p;
            }
            return Process.Start(FindVoiceroid2Path().First());
        }

        private Process GetVoiceroidEditorProcess()
        {
            string winTitle1 = "VOICEROID2";
            string winTitle2 = winTitle1 + "*";

            int RetryCount = 3;
            int RetryWaitms = 500;
            Process p = null;

            for (int i = 0; i < 3; i++)
            {
                Process[] ps = Process.GetProcesses();

                foreach (Process pitem in ps)
                {
                    if ((pitem.MainWindowHandle != IntPtr.Zero) &&
                         ((pitem.MainWindowTitle.Equals(winTitle1)) || (pitem.MainWindowTitle.Equals(winTitle2))))
                    {
                        p = pitem;
                        if (i < (RetryCount - 1)) Thread.Sleep(RetryWaitms);
                    }
                }
            }

            return p;
        }

        void closeVoiceroid2(Process proc)
        {
            SetForegroundWindow(proc.Handle);
            proc.CloseMainWindow();
            Thread.Sleep(1000);
            SendKeys.Send("y");
        }

        public void Dispose()
        {
            cancelled = true;
            if (voiceroidProcess != null)
            {
                closeVoiceroid2(voiceroidProcess);
            }
        }

        [DllImport ("User32.dll")]
        static extern int SetForegroundWindow(IntPtr point);
    }
}
