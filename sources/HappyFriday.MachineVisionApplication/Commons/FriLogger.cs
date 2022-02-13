using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace KukaRoboter.MachineVisionSystem.Common
{
    public enum FriLogLevel
    {
        Debug, Info, Warn, Error, Fatal,
    }

    public class FriLogItem
    {
        public DateTime Time { get; }

        public FriLogLevel Level { get; }

        public string Message { get; }

        public Exception Exception { get; private set; }

        public FriLogItem(string message, FriLogLevel level = FriLogLevel.Debug, Exception exception = null)
        {
            this.Time = DateTime.Now;
            this.Level = level;
            this.Message = message;
            this.Exception = exception;
        }

        public override string ToString()
        {
            var stringBuilder = new StringBuilder();
            _ = stringBuilder.Append("\u0002");

            _ = stringBuilder.Append(this.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"));

            _ = stringBuilder.Append(" [");
            _ = stringBuilder.Append($"{this.Level,5}");
            _ = stringBuilder.Append("] ");

            _ = stringBuilder.Append(this.Message);

            if (this.Exception != null)
            {
                try
                {
                    _ = stringBuilder.AppendLine();
                    do
                    {
                        _ = stringBuilder.AppendLine("Message   : " + this.Exception.Message);
                        _ = stringBuilder.AppendLine("Source    : " + this.Exception.Source);
                        _ = stringBuilder.AppendLine("StackTrace: " + this.Exception.StackTrace);
                        _ = stringBuilder.AppendLine("Type      : " + this.Exception.GetType());
                        _ = stringBuilder.AppendLine("TargetSite: " + this.Exception.TargetSite);

                        this.Exception = this.Exception.InnerException;
                    } while (this.Exception != null);
                }
                catch
                {
                    // ignored
                }
            }

            return stringBuilder.ToString();
        }
    }

    public interface IFriLog : IDisposable
    {
        void RecordMessage(FriLogItem logItem);
    }

    public abstract class FriLogBase : IFriLog
    {
        private readonly Queue<FriLogItem> mWaitForSaveQueue;
        private readonly SimpleHybirdLock mLockForWaitForSaveQueue;
        private int mSaveStatus;
        protected SimpleHybirdLock lockForFileSave;
        protected readonly string logFileHeadString;

        protected string LogFilePath { get; set; }

        /// <summary>
        /// 实例化一个日志对象
        /// </summary>
        /// <param name="logFileHeadString">日志文件名的前缀，用以表示通过不同方式生成的日志</param>
        /// <remarks>
        /// Log_Single_   表示使用一个文件来存储日志
        /// Log_FileSize_ 表示根据指定的文件大小来存储日志。当日志文件达到或超过指定大小时，将新建一个文件。
        /// Log_DateTime_ 表示根据指定的时间周期(天)来创建日志文件。
        ///
        /// 日志存储在当前程序运行目录下的Logs文件夹下。如果程序运行时该文件夹不存在，则自动创建
        /// </remarks>
        protected FriLogBase(string logFileHeadString)
        {
            this.logFileHeadString = logFileHeadString;
            this.lockForFileSave = new SimpleHybirdLock();
            this.mLockForWaitForSaveQueue = new SimpleHybirdLock();
            this.mWaitForSaveQueue = new Queue<FriLogItem>();

            // 默认日志目录
            this.LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs\\");
            var directoryInfo = new DirectoryInfo(this.LogFilePath);
            if (!Directory.Exists(directoryInfo.FullName))
            {
                _ = Directory.CreateDirectory(directoryInfo.FullName);
            }
        }

        public void RecordMessage(FriLogItem logItem)
        {
            this.AddItemToCache(logItem);
        }

        protected abstract string GetCurrentLogFileName();

        private void AddItemToCache(FriLogItem item)
        {
            try
            {
                this.mLockForWaitForSaveQueue.Enter();
                this.mWaitForSaveQueue.Enqueue(item);
            }
            finally
            {
                this.mLockForWaitForSaveQueue.Leave();
            }

            this.StartSaveFile();
        }

        private void StartSaveFile()
        {
            if (Interlocked.CompareExchange(ref this.mSaveStatus, 1, 0) == 0)
            {
                _ = ThreadPool.QueueUserWorkItem(this.ThreadPoolSaveFile, null);
            }
        }

        private void ThreadPoolSaveFile(object obj)
        {
            var currentLogItem = GetAndRemoveLogItem();
            this.lockForFileSave.Enter();
            try
            {
                var logSaveFileName = this.GetCurrentLogFileName();
                if (!string.IsNullOrEmpty(logSaveFileName))
                {
                    StreamWriter sw = null;
                    try
                    {
                        sw = new StreamWriter(logSaveFileName, true, Encoding.UTF8);
                        while (currentLogItem != null)
                        {
                            sw.Write(currentLogItem);
                            sw.Write(Environment.NewLine);
                            currentLogItem = GetAndRemoveLogItem();
                        }
                    }
                    catch (Exception ex)
                    {
                        this.AddItemToCache(currentLogItem);
                        this.AddItemToCache(new FriLogItem("Save log to file is failed", FriLogLevel.Fatal, ex));
                    }
                    finally
                    {
                        sw?.Dispose();
                    }
                }
            }
            finally
            {
                this.lockForFileSave.Leave();
                _ = Interlocked.Exchange(ref this.mSaveStatus, 0);
            }

            // 再次检测锁是否释放完成
            if (this.mWaitForSaveQueue.Count > 0)
            {
                this.StartSaveFile();
            }

            // 内部方法
            FriLogItem GetAndRemoveLogItem()
            {
                FriLogItem result;
                try
                {
                    this.mLockForWaitForSaveQueue.Enter();
                    result = this.mWaitForSaveQueue.Count > 0 ? this.mWaitForSaveQueue.Dequeue() : null;
                }
                finally
                {
                    this.mLockForWaitForSaveQueue.Leave();
                }

                return result;
            }
        }

        #region IDisposable Support

        private bool mDisposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (this.mDisposedValue)
            {
                return;
            }

            if (disposing)
            {
                this.mLockForWaitForSaveQueue.Dispose();
                this.mWaitForSaveQueue.Clear();
                this.lockForFileSave.Dispose();
            }
            this.mDisposedValue = true;
        }

        public void Dispose()
        {
            this.Dispose(true);
        }

        protected sealed class SimpleHybirdLock : IDisposable
        {
            /// <summary>
            /// 基元内核模式构造同步锁
            /// </summary>
            private readonly AutoResetEvent mWaiterLock = new AutoResetEvent(false);

            /// <summary>
            /// 基元用户模式构造同步锁
            /// </summary>
            private int mWaiters;

            /// <summary>
            /// 获取当前锁是否在等待当中
            /// </summary>
            public bool IsWaiting => this.mWaiters != 0;

            /// <summary>
            /// 获取锁
            /// </summary>
            public void Enter()
            {
                if (Interlocked.Increment(ref this.mWaiters) == 1)
                {
                    return; //用户锁可以使用的时候，直接返回，第一次调用时发生
                }
                //当发生锁竞争时，使用内核同步构造锁
                _ = this.mWaiterLock.WaitOne();
            }

            /// <summary>
            /// 离开锁
            /// </summary>
            public void Leave()
            {
                if (Interlocked.Decrement(ref this.mWaiters) == 0)
                {
                    return; //没有可用的锁的时候
                }

                _ = this.mWaiterLock.Set();
            }

            #region IDisposable Support

            private bool mDisposedValue; // 要检测冗余调用

            private void Dispose(bool disposing)
            {
                if (!this.mDisposedValue)
                {
                    if (disposing)
                    {
                        // TODO: 释放托管状态(托管对象)。
                    }

                    // TODO: 释放未托管的资源(未托管的对象)并在以下内容中替代终结器。
                    // TODO: 将大型字段设置为 null。
                    this.mWaiterLock.Close();

                    this.mDisposedValue = true;
                }
            }

            // TODO: 仅当以上 Dispose(bool disposing) 拥有用于释放未托管资源的代码时才替代终结器。
            // ~SimpleHybirdLock() {
            //   // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
            //   Dispose(false);
            // }

            // 添加此代码以正确实现可处置模式。
            /// <summary>
            /// 释放资源
            /// </summary>
            public void Dispose()
            {
                // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
                this.Dispose(true);
                // TODO: 如果在以上内容中替代了终结器，则取消注释以下行。
                // GC.SuppressFinalize(this);
            }

            #endregion IDisposable Support
        }

        #endregion IDisposable Support
    }

    public class FriLogSingle : FriLogBase
    {
        public FriLogSingle() : base("Log_Single_")
        {
        }

        public void ClearLog()
        {
            this.lockForFileSave.Enter();
            var logSaveFileName = this.GetCurrentLogFileName();
            if (!string.IsNullOrEmpty(logSaveFileName))
            {
                File.Create(logSaveFileName).Dispose();
            }

            this.lockForFileSave.Leave();
        }

        public string GetAllSavedLog()
        {
            var result = string.Empty;
            try
            {
                this.lockForFileSave.Enter();
                var logSaveFileName = this.GetCurrentLogFileName();
                if (!string.IsNullOrEmpty(logSaveFileName))
                {
                    if (File.Exists(logSaveFileName))
                    {
                        var stream = new StreamReader(logSaveFileName, Encoding.UTF8);
                        result = stream.ReadToEnd();
                        stream.Dispose();
                    }
                }
            }
            finally
            {
                this.lockForFileSave.Leave();
            }

            return result;
        }

        protected override string GetCurrentLogFileName()
        {
            var allLogFiles = Directory.GetFiles(this.LogFilePath, this.logFileHeadString + "*.log");
            var currentLogFileName = allLogFiles.FirstOrDefault(name => name.EndsWith("runtime.log")) ?? this.LogFilePath + this.logFileHeadString + "runtime.log";

            return currentLogFileName;
        }
    }

    public class FriLogFileSize : FriLogBase
    {
        private const int FileMaxSize = 5 * 1024 * 1024;

        public FriLogFileSize() : base("Log_FileSize_")
        {
        }

        /// <inheritdoc />
        protected override string GetCurrentLogFileName()
        {
            var allLogFiles = Directory.GetFiles(this.LogFilePath, this.logFileHeadString + "*.log");
            var currentLogFileName = allLogFiles.FirstOrDefault(name => new FileInfo(name).Length < FileMaxSize) ?? this.LogFilePath + this.logFileHeadString + DateTime.Now.ToString("yyyyMMddHHmm") + ".log";

            return currentLogFileName;
        }
    }

    public class FriLogDateTime : FriLogBase
    {
        public FriLogDateTime() : base("Log_Datetime_")
        {
        }

        protected override string GetCurrentLogFileName()
        {
            var allLogFiles = Directory.GetFiles(this.LogFilePath, this.logFileHeadString + "*.log");
            var fileName = this.logFileHeadString + DateTime.Now.ToString("yyyyMMdd") + ".log";
            var currentLogFileName = allLogFiles.FirstOrDefault(name => name.EndsWith(fileName)) ?? this.LogFilePath + fileName;

            return currentLogFileName;
        }
    }
}