﻿using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RobloxStudioModManager
{
    public partial class FVariableScanner : Form
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        private HttpListener server;
        private FVariableEditor editor;

        private Process studioProc;
        private bool pingedStudio = false;

        private List<string> stringList;
        private List<string> fvars;
        private bool stringListReady = false;
        private int processed = 0;
        private bool finished = false;

        private string errorState = "Scan failed!";
        private int timeout = 0;

        private void checkTimeout(int threshold)
        {
            if (timeout > threshold)
            {
                errorState = "Roblox Studio timed out!";
                Close();
            }
            else
            {
                timeout++;
            }
        }

        private async void runServerSession()
        {
            while (server.IsListening)
            {
                try
                {
                    Task<HttpListenerContext> getContext = server.GetContextAsync();
                    getContext.Wait();
                    pingedStudio = true;

                    HttpListenerContext context = getContext.Result;
                    HttpListenerRequest request = context.Request;

                    HttpListenerResponse response = context.Response;
                    string query = request.Headers.Get("Query");
                    if (query != null)
                    {
                        if (query == "GetStringList")
                        {
                            Console.Beep();
                            while (!stringListReady)
                                await Task.Delay(100);

                            string file = string.Join("\n", stringList.ToArray());
                            Stream outStream = response.OutputStream;
                            byte[] buffer = Encoding.ASCII.GetBytes(file);
                            outStream.Write(buffer, 0, buffer.Length);
                            response.StatusCode = 200;
                        }
                        else if (query == "PingProgress")
                        {
                            string s_processed = request.Headers.Get("Count");
                            if (s_processed != null)
                                int.TryParse(s_processed, out processed);
                        }
                        else if (query == "SendFVariables")
                        {
                            string fvarChunk = request.Headers.Get("FVariables");
                            if (fvarChunk != null)
                            {
                                foreach (string fvar in fvarChunk.Split(';'))
                                    fvars.Add(fvar);
                            }
                        }
                        else if (query == "Finished")
                        {
                            finished = true;
                            editor.receiveFVariableScan(fvars);
                        }
                    }
                    response.Close();
                }
                catch {}
            }
        }

        private async Task setStatus(string status)
        {
            statusLbl.Text = status;
            await Task.Delay(300);
        }

        private void checkStudioState()
        {
            if (studioProc != null)
            {
                if (studioProc.HasExited)
                {
                    errorState = "Roblox Studio was closed prematurely!";
                    Close();
                }
                else
                {
                    // Hide the window
                    ShowWindowAsync(studioProc.MainWindowHandle, 0);
                }
            }
        }

        public FVariableScanner(FVariableEditor _editor)
        {
            Application.ApplicationExit += new EventHandler(FVariableScanner_AppExit);
            editor = _editor;

            InitializeComponent();
        }

        private async void FVariableScanner_Load(object sender, EventArgs e)
        {
            await setStatus("Initializing listener");
            server = new HttpListener();
            server.Prefixes.Add("http://localhost:20326/");
            server.Start();

            await setStatus("Initializing listener thread");
            Task serverThread = Task.Run(() => runServerSession());

            await setStatus("Initializing plugin");

            string appData = Environment.GetEnvironmentVariable("LocalAppData");
            string studioRoot = Path.Combine(appData, "Roblox Studio");
            string studioPath = Path.Combine(studioRoot, "RobloxStudioBeta.exe");

            try
            {
                Assembly self = Assembly.GetExecutingAssembly();
                string fvarExtractor;

                using (Stream stream = self.GetManifestResourceStream("RobloxStudioModManager.Resources.FVariableExtractor.lua"))
                using (StreamReader reader = new StreamReader(stream))
                {
                    fvarExtractor = reader.ReadToEnd();
                }

                string dir = Path.Combine(studioRoot, "BuiltInPlugins");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string fvarExtractorFile = Path.Combine(dir, "__rbxModManagerFVarExtract.lua");
                FileInfo info = new FileInfo(fvarExtractorFile);
                if (info.Exists)
                    info.Attributes = FileAttributes.Normal;

                File.WriteAllText(fvarExtractorFile, fvarExtractor);
                info.Attributes = FileAttributes.ReadOnly;
            }
            catch
            {
                Close();
            }

            await setStatus("Starting Roblox Studio...");

            ProcessStartInfo studioProcInfo = new ProcessStartInfo();
            studioProcInfo.FileName = studioPath;
            studioProcInfo.Arguments = "-task EditPlace -placeId 1327419651";
            studioProc = Process.Start(studioProcInfo);

            // Process the string list we're going to use in the mean time.
            stringList = new List<string>();
            using (FileStream readStudio = File.OpenRead(studioPath))
            {
                MatchCollection matches;
                using (StreamReader reader = new StreamReader(readStudio))
                {
                    string studio = reader.ReadToEnd();
                    matches = Regex.Matches(studio, "[A-Z][a-z][A-z]+");
                }
                foreach (Match match in matches)
                {
                    string word = match.Value;
                    if (word.Length > 8 && word.Length < 32)
                        stringList.Add(word);
                }
            }

            fvars = new List<string>();
            stringListReady = true;
            await setStatus("Waiting for Roblox Studio...");
            

            while (!pingedStudio)
            {
                checkStudioState();
                checkTimeout(100);
                await Task.Delay(100);
            }

            await setStatus("Collecting FVariables...");
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Maximum = stringList.Count;
            timeout = 0;

            while (!finished)
            {
                checkStudioState();
                checkTimeout(200);
                progressBar.Value = processed;
                await Task.Delay(100);
            }

            Close();
        }

        private void stopScan()
        {
            try
            {
                server.Stop();
                server.Close();
            }
            catch { }
            if (studioProc != null && !studioProc.HasExited)
                studioProc.Kill();
        }

        private void FVariableScanner_FormClosed(object sender, FormClosedEventArgs e)
        {
            stopScan();
            if (!editor.Enabled)
            {
                editor.Enabled = true;
                editor.BringToFront();

                if (!finished)
                    MessageBox.Show(editor, errorState, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void FVariableScanner_AppExit(object sender, EventArgs e)
        {
            stopScan();
        }
    }
}