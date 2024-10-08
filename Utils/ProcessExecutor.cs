﻿using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using Topshelf.Logging;

namespace WbtGuardService.Utils
{
    public class ProcessExecutor : IDisposable
    {
        private readonly GuardServiceConfig config;
        private Process originProcess;
        private int originPid;
        private FileStream stdoutStream;
        private FileStream stderrorStream;
        private readonly LogWriter _logger;
        private bool _isManualStop;

        public ProcessExecutor(GuardServiceConfig config)
        {
            _logger = HostLogger.Current.Get("ProcessExecutor");
            this.config = config;
            var redirectStdOut = !string.IsNullOrEmpty(this.config.StdOutFile);
            var redirectStdErr = !string.IsNullOrEmpty(this.config.StdErrorFile);

            if (redirectStdOut)
            {
                try
                {
                    stdoutStream = new FileStream(this.config.StdOutFile, FileMode.OpenOrCreate | FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite);
                }
                catch
                {
                    _logger.Warn($"打开文件 {this.config.StdOutFile} 失败，禁用输出日志");
                    stdoutStream = null;
                }
            }

            if (redirectStdErr)
            {
                try
                {
                    stderrorStream = new FileStream(this.config.StdErrorFile, FileMode.OpenOrCreate | FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite);
                }
                catch
                {
                    _logger.Warn($"打开文件 {this.config.StdErrorFile} 失败，禁用错误输出日志");
                    stderrorStream = null;
                }
            }

            _isManualStop = !config.Autostart;
        }

        public string Name => this.config.Name;

        /// <summary>
        /// 定时检查执行
        /// </summary>
        /// <returns></returns>
        public virtual MyProcessInfo Execute()
        {
            if (_isManualStop) return null;

            var p = GetProcessById(originPid);
            originProcess = p;

            if (p != null && p.HasExited)
            {
                return new MyProcessInfo(p);
            } else if (config.Autorestart)
            {
                return StartProcess();
            }

            _isManualStop = true;
            return new MyProcessInfo(p);
        }

        private MyProcessInfo StopProcess()
        {
            var p = GetProcessById(originPid);
            if (p == null || p.HasExited)
            {
                _logger.Info($" {this.config.Name}已经关闭.");
            }
            else
            {
                try
                {
                    _logger.Info($"关闭程序 {this.config.Name} ...");
                    // kill & restart
                    p.Kill(true);
                    p.WaitForExit();
                    _logger.Info($"关闭程序 {this.config.Name} 成功");
                }
                catch (Exception ex)
                {
                    _logger.Error($"关闭程序 {this.config.Name} 时失败! ", ex);
                }
            }

            originProcess = p;
            return new MyProcessInfo(p);
        }

        private MyProcessInfo RestartProcess()
        {
            MyProcessInfo p = null;
            try
            {
                p = this.StopProcess();
                p = this.StartProcess();
            }
            catch (Exception ex)
            {
                _logger.Error($"执行启动 {this.config.Name} 时失败! ", ex);
            }

            return p;
        }

        private MyProcessInfo StartProcess()
        {
            _logger.Info($"开始程序 {this.config.Name}...");
            var bDir = !string.IsNullOrEmpty(this.config.Directory);
            var p = GetProcessById(originPid);
            if (p == null || p.HasExited)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = this.config.Command,
                    UseShellExecute = false,
                    RedirectStandardOutput = stdoutStream != null,
                    RedirectStandardError = stderrorStream != null,
                    WorkingDirectory = bDir ? this.config.Directory : AppDomain.CurrentDomain.BaseDirectory,
                    Arguments = this.config.Arguments,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    //StandardOutputEncoding = Encoding.UTF8,
                    //StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var (key, value) in this.config.GetEnvironmentVariables())
                {
                    if (value is not null)
                    {
                        startInfo.Environment[key] = value;
                    }
                    else
                    {
                        //https://github.com/dotnet/runtime/blob/212fb547303cc9c46c5e0195f530793c30b67669/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs
                        // Null value means we should remove the variable
                        // https://github.com/Tyrrrz/CliWrap/issues/109
                        // https://github.com/dotnet/runtime/issues/34446
                        startInfo.Environment.Remove(key);
                    }
                }

                Console.WriteLine($"Start process {config.Name} ....");
                p = new Process { StartInfo = startInfo, };

                p.ErrorDataReceived += P_ErrorDataReceived;
                p.OutputDataReceived += P_OutputDataReceived;

                try
                {
                    if (!p.Start())
                    {
                        throw new InvalidOperationException(
                            $"Failed to start a process with file path '{p.StartInfo.FileName}'. " +
                            "Target file is not an executable or lacks execute permissions."
                        );
                    }

                    if (stdoutStream != null) p.BeginOutputReadLine();
                    if (stderrorStream != null) p.BeginErrorReadLine();
                    _logger.Info($"程序 {this.config.Name} 启动成功.");
                }
                catch (Win32Exception ex)
                {
                    throw new Win32Exception(
                        $"Failed to start a process with file path '{p.StartInfo.FileName}'. " +
                        "Target file or working directory doesn't exist, or the provided credentials are invalid.",
                        ex
                    );
                }
            }

            originPid = p?.Id ?? 0;
            originProcess = p;
            return new MyProcessInfo(p);
        }

        private void P_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e?.Data != null)
            {
                try
                {
                    stdoutStream.Write(Encoding.UTF8.GetBytes(e.Data));
                    stdoutStream.Write(Encoding.UTF8.GetBytes("\r\n"));
                    stdoutStream.Flush();
                }
                catch
                {
                    _logger.Warn($"程序 {this.config.Name} 输出日志失败! ");
                }
            }
        }

        private void P_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e?.Data != null)
            {
                try
                {
                    stderrorStream.Write(Encoding.UTF8.GetBytes(e.Data));
                    stderrorStream.Write(Encoding.UTF8.GetBytes("\r\n"));
                    stderrorStream.Flush();
                }
                catch { _logger.Warn($"程序 {this.config.Name} 输出错误日志失败! "); }
            }
        }

        public void Dispose()
        {
            if (stdoutStream != null)
            {
                stdoutStream.Close();
                stdoutStream.Dispose();
                _logger.Warn($"程序 {this.config.Name} 输出日志关闭! ");
            }

            if (stderrorStream != null)
            {
                stderrorStream.Close();
                stderrorStream.Dispose();
                _logger.Warn($"程序 {this.config.Name} 输出错误日志关闭! ");
            }
        }

        public object ExecuteCommand(string command, string content)
        {
            var redirectStdOut = !string.IsNullOrEmpty(this.config.StdOutFile);
            var redirectStdErr = !string.IsNullOrEmpty(this.config.StdErrorFile);
            MyProcessInfo p = null;
            if (command == "Start")
            {
                _isManualStop = false;
                p = this.StartProcess();
            }
            else if (command == "Stop")
            {
                _isManualStop = true;
                p = this.StopProcess();
            }
            else if (command == "Restart")
            {
                _isManualStop = false;
                p = this.RestartProcess();
            }
            else if (command == "LastLogs")
            {
                if (redirectStdOut)
                {
                    return ReadTailBytesFromFile(this.config.StdOutFile);;
                }
            }
            else if (command == "LastErrorLogs")
            {
                if (redirectStdErr)
                {
                    return ReadTailBytesFromFile(this.config.StdErrorFile);
                }
            }
            else if (command == "ClearLogs")
            {
                if (redirectStdOut)
                {
                    try
                    {
                        using (var stream = new FileStream(this.config.StdOutFile, FileMode.Truncate,
                                   FileAccess.Write, FileShare.ReadWrite))
                        {
                            stream.Seek(0, SeekOrigin.Begin);
                            stream.SetLength(0);
                            stream.Flush();
                        }

                        stdoutStream.Close();
                        stdoutStream = new FileStream(this.config.StdOutFile, FileMode.OpenOrCreate | FileMode.Append,
                            FileAccess.Write, FileShare.ReadWrite);
                        _logger.Warn($"清空文件 {this.config.StdOutFile} 内容");
                    }
                    catch
                    {
                        _logger.Warn($"清空文件 {this.config.StdOutFile} 失败");
                    }
                }

                if (redirectStdErr)
                {
                    try
                    {
                        using (var stream = new FileStream(this.config.StdErrorFile, FileMode.Truncate,
                                   FileAccess.Write, FileShare.ReadWrite))
                        {
                            stream.Seek(0, SeekOrigin.Begin);
                            stream.SetLength(0);
                            stream.Flush();
                        }

                        stderrorStream.Close();
                        stderrorStream = new FileStream(this.config.StdErrorFile,
                            FileMode.OpenOrCreate | FileMode.Append,
                            FileAccess.Write, FileShare.ReadWrite);

                        _logger.Warn($"清空文件 {this.config.StdErrorFile} 内容");
                    }
                    catch
                    {
                        _logger.Warn($"清空文件 {this.config.StdErrorFile} 失败");
                    }
                }
            }
            else if (command == "Status")
            {
                p = new MyProcessInfo(GetProcessById(originPid));
            }

            return p;
        }

        private static Process GetProcessById(int processId)
        {
            if (processId == 0)
            {
                return null;
            }

            Process p = null;
            try
            {
                p = Process.GetProcessById(processId);
                if (p.HasExited)
                {
                    p = null;
                }
            }
            catch (ArgumentException)
            {
            }

            return p;
        }

        private string ReadTailBytesFromFile(string file)
        {
            try
            {
                var buffer = new byte[8192];
                using (var stream = new FileStream(file, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = stream.Length;
                    if (len >= 8192)
                    {
                        stream.Seek(len - 8192, SeekOrigin.Begin);
                        var n = stream.Read(buffer);
                        return Encoding.UTF8.GetString(buffer, 0, n);
                    }
                    else if (len == 0)
                    {
                        return "";
                    }
                    else
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                        var n = stream.Read(buffer);
                        return Encoding.UTF8.GetString(buffer, 0, Math.Min(n, (int)len));;
                    }
                }
            }
            catch
            {
                _logger.Warn($"打开文件 {file} 失败");
                return null;
            }
            
        } 
    }
}