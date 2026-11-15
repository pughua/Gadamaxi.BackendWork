using Cronos;
using Gadamaxi.BackendWork.Resources;
using Newtonsoft.Json;
using System.Globalization;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Gadamaxi.BackendWork
{
    public static class Helper
    {
        #region constant
        public static readonly char[] CronSplitChars = new[] { ' ', '\t' };
        public const string Star = "*";
        public const string Comma = ",";

        public const int Zero = 0;
        public const int One = 1;
        public const int Five = 5;
        /// <summary>每轮批量扫描的默认上限（#22）。实际值取 <c>WorkServerOption.BatchSize</c>，未配置时回退到此常量。</summary>
        public const int DefaultBatchSize = 10;
        /// <summary>未配置 WorkerCount 时，自动推算工作线程数的上限（#15）。</summary>
        public const int DefaultMaxWorkThreads = 15;
        /// <summary>兼容旧引用：每轮批量扫描上限的默认常量，等价于 <see cref="DefaultBatchSize"/>。</summary>
        public const int BatchSize = DefaultBatchSize;
        public const int MinutePerSecond = 60;

        public const string DefaultQueue = "defaultqueue";

        public const string RecurringKey = "recurring";

        public const string BatchKey = "batch";

        public const string BatchIdKey = "BatchId";
        public const string BatchItemIdKey = "BatchItemId";

        /// <summary>saga 调度缓存的 key（<see cref="Storages.IWorkStorage.AddCacheSet"/> 的 Type 列）。</summary>
        public const string SagaKey = "saga";

        /// <summary>步骤工作参数：所属 saga Id。saga 状态回写管线据此识别"这不是普通任务"。</summary>
        public const string SagaIdKey = "SagaId";

        /// <summary>步骤工作参数：所属步骤 Id。</summary>
        public const string SagaStepIdKey = "SagaStepId";

        /// <summary>步骤工作参数：是否为补偿方向（补偿工作为 "1"，正向工作不写该参数）。</summary>
        public const string SagaCompensateKey = "SagaCompensate";

        /// <summary>步骤工作参数：正向返回值要写入变量池的键（空表示不采集）。</summary>
        public const string SagaOutputKeyKey = "SagaOutputKey";

        /// <summary>变量池占位符的前缀/后缀，形如 <c>${orderId}</c>。</summary>
        public const string SagaVariablePrefix = "${";

        public static TimeSpan DistributedLockTimeOut = TimeSpan.FromMinutes(1);
        public static TimeSpan DefaultRecurringPollingDelay = TimeSpan.FromMinutes(1);
        public static TimeSpan DefaultSchedulerPollingDelay = TimeSpan.FromMinutes(1);
        public static TimeSpan DefaultWorkPollingDelay = TimeSpan.FromSeconds(15);
        public static TimeSpan DefaultHeartbeatDelay = TimeSpan.FromMinutes(5);
        public static TimeSpan DefaultWorkIntervalDelay = TimeSpan.FromMilliseconds(100);
        public static TimeSpan DefaultRecoveryPollingDelay = TimeSpan.FromMinutes(1);
        /// <summary>过期数据清理进程轮询间隔（默认 1 小时）。</summary>
        public static TimeSpan DefaultCleanupPollingDelay = TimeSpan.FromHours(1);

        /// <summary>
        /// saga 调度轮询间隔（默认 1 秒）。Saga 是严格串行推进，「一步一轮」，
        /// 该值直接决定步骤之间的空转延迟，因此比批量/周期的轮询间隔小得多。
        /// </summary>
        public static TimeSpan DefaultSagaPollingDelay = TimeSpan.FromSeconds(1);

        /// <summary>saga 整体执行超时（默认 30 分钟）：创建起算，超时仍在 Running 则转补偿。</summary>
        public static TimeSpan DefaultSagaTimeout = TimeSpan.FromMinutes(30);

        /// <summary>
        /// saga 超时扫描进程的轮询间隔（默认 1 分钟）。超时判定的粒度本来就是分钟级，
        /// 没必要跟着 1 秒的推进轮询一起扫，否则只是白白增加查询量。
        /// </summary>
        public static TimeSpan DefaultSagaTimeoutPollingDelay = TimeSpan.FromMinutes(1);

        /// <summary>终态 saga 的保留时长（默认 7 天），与批量保持同一口径，供监控回溯。</summary>
        public static TimeSpan DefaultSagaExpireTime = TimeSpan.FromDays(7);

        /// <summary>
        /// 优雅停机超时：停止取新任务后，等待在途任务完成的最长时间。
        /// 超时后不再等待（在途线程是后台线程，进程可正常退出），并记日志（#9）。
        /// </summary>
        public static TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 队列消息可见性超时（分钟）：消息被取走后超过该时长仍未确认完成，
        /// 将被重新投递，保证执行节点崩溃后消息不丢失。
        /// </summary>
        public const int QueueFetchTimeoutMinutes = 5;

        /// <summary>
        /// 任务名最大长度。监控面板需要按名字识别人工任务，因此任务名是必填项，
        /// 该值与 Sql 存储中 Work.Name / Batch.Name / BatchItem.Name 的列长度一致。
        /// </summary>
        public const int MaxWorkNameLength = 200;

        /// <summary>批量子任务名的后缀预留长度（形如 "#12"），批量名上限据此收紧。</summary>
        public const int BatchItemNameSuffixLength = 8;

        /// <summary>批量任务名最大长度（为子任务的 "#序号" 后缀预留空间）。</summary>
        public const int MaxBatchNameLength = MaxWorkNameLength - BatchItemNameSuffixLength;

        /// <summary>批量子任务名中批量名与序号的连接符，形如 "月结对账#3"。</summary>
        public const char WorkNameIndexSeparator = '#';

        /// <summary>saga 补偿步骤名的后缀，形如 "订单履约#1~undo"。</summary>
        public const string SagaCompensateNameSuffix = "~undo";

        /// <summary>补偿步骤名后缀预留长度。</summary>
        public const int SagaCompensateNameSuffixLength = 5;

        /// <summary>
        /// saga 名最大长度：步骤名派生自 saga 名（形如 "订单履约#1"），补偿步骤名还要再加 "~undo"，
        /// 因此比批量名再收紧一个后缀长度，否则补偿步骤名会突破 Work.Name 的列长而落库失败。
        /// </summary>
        public const int MaxSagaNameLength = MaxBatchNameLength - SagaCompensateNameSuffixLength;

        public const int DefaultDelayTimes = 1;
        public const int DefaultBatchRetryCount = 3;
        public const int DefaultWorkRetryCount = 5;
        public static TimeSpan DefaultWorkExpireTime = TimeSpan.FromDays(1);
        public static TimeSpan DefaultBatchExpireUtcTime = TimeSpan.FromDays(7);
        public static string NewId()
        {
            return MassTransit.NewId.NextSequentialGuid().ToString().Replace("-", "");
        }

        #endregion

        #region Cron
        /// <summary>
        /// parse corn expression
        /// </summary>
        /// <param name="cronExpression"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="CronFormatException"></exception>
        public static CronExpression ParseCron(string cronExpression)
        {
            if (cronExpression == null)
            {
                throw new ArgumentNullException(nameof(cronExpression));
            }

            var format = CronFormat.Standard;

            if (!cronExpression.StartsWith("@", StringComparison.OrdinalIgnoreCase))
            {
                var parts = cronExpression.Split(CronSplitChars, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5)
                {
                    throw new CronFormatException(string.Format(Resource.CronLengthError, cronExpression));
                }
            }

            try
            {
                return CronExpression.Parse(cronExpression, format);
            }
            catch(Exception ex) when (!(ex is StackOverflowException))
            {
                throw new CronFormatException(string.Format(Resource.CronFomatError, cronExpression));
            }
        }

        /// <summary>
        /// Execute once per hour
        /// </summary>
        /// <param name="minute"></param>
        /// <returns></returns>
        public static string Minute(int minute)
        {
            return Cron5Len(minute);
        }

        /// <summary>
        /// Execute once per daily
        /// </summary>
        /// <param name="hour"></param>
        /// <param name="minute"></param>
        /// <returns></returns>
        public static string HourMinute(int hour, int minute)
        {
            return Cron5Len(minute, hour);
        }

        public static string Cron5Len(int? minute = null
            ,int? hour = null
            ,int? day=null
            ,int? month=null
            ,DayOfWeek? dayOfweek=null)
        {
            var minutestr = "*";
            var hourstr = "*";
            var daystr = "*";
            var monthstr = "*";
            var dayOfweekstr = "*";

            if (minute != null)
            {
                CheckRangeValueError(0, 59, minute.Value, nameof(minute));
                minutestr = minute.Value.ToString();
            }
            if (hour != null)
            {
                CheckRangeValueError(0, 23, hour.Value, nameof(hour));
                hourstr = hour.Value.ToString();
            }
            if (month != null)
            {
                CheckRangeValueError(1, 12, month.Value, nameof(month));
                monthstr = month.Value.ToString();
            }
            if (day != null)
            {
                CheckRangeValueError(1, 31, day.Value, nameof(day));
                daystr = day.Value.ToString();
            }
            if(dayOfweek != null)
            {
                dayOfweekstr = ((int)dayOfweek.Value).ToString();
            }
            return $"{minutestr} {hourstr} {daystr} {monthstr} {dayOfweekstr}";
        }

        /// <summary>兼容旧拼写（Corn5Len）：请改用 <see cref="Cron5Len"/>。</summary>
        [Obsolete("拼写修正：请改用 Cron5Len。")]
        public static string Corn5Len(int? minute = null
            , int? hour = null
            , int? day = null
            , int? month = null
            , DayOfWeek? dayOfweek = null)
            => Cron5Len(minute, hour, day, month, dayOfweek);

        #endregion

        #region Datetime
        private static readonly DateTime UnixTimeStart = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static DateTime CurrentUtcTime()
        {
            return DateTime.UtcNow;
        }

        public static long UnixSecondtamp(this DateTime datetime)
        {
            var span = datetime.ToUniversalTime() - UnixTimeStart;
            return (long)span.TotalSeconds;
        }

        public static long UnixMillisecondStamp(this DateTime datetime)
        {
            var span = datetime.ToUniversalTime() - UnixTimeStart;
            return (long)span.TotalMilliseconds;
        }

        /// <param name="stopToken">
        /// 服务停止令牌。传入后等待可被停止信号打断（抛出 OperationCanceledException），
        /// 工作线程才能真正退出，而不是卡在轮询间隔里空转
        /// </param>
        public static async Task Delay(TimeSpan inputDely, TimeSpan defaultDelay, int works,
            CancellationToken stopToken = default)
        {
            var delay = inputDely;
            if (delay <= TimeSpan.Zero)
            {
                delay = defaultDelay;
            }

            if (works <= Helper.Zero)
            {
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * Helper.DefaultDelayTimes);
            }

            await Task.Delay(delay, stopToken);
        }

        public static DateTime ToUtcDateTime(this long millisecond)
        {
            var dateTime = UnixTimeStart.AddMilliseconds(millisecond);
            return dateTime;
        }

        public static DateTimeOffset ToDateTimeOffset(this long millisecond,TimeSpan offset)
        {
            var dateTime = new DateTime(UnixTimeStart.Ticks, DateTimeKind.Unspecified).AddMilliseconds(millisecond).Add(offset);
            var dtoff = new DateTimeOffset(dateTime, offset);
            return dtoff;
        }
        #endregion

        #region CheckException

        public static void CheckArgumentNull(string? str,string name)
        {
            if (string.IsNullOrEmpty(str))
            {
                throw new ArgumentNullException(string.Format(Resource.ArgumentNullError, name));
            }
        }

        public static void CheckArgumentNull(object? obj, string name)
        {
            if (obj == null)
            {
                throw new ArgumentNullException(string.Format(Resource.CronFomatError, name));
            }
        }

        public static void CheckMiniValueError(double minvalue, double value, string name)
        {
            if (value < minvalue)
            {
                throw new ArgumentException(string.Format(Resource.MinValueError, minvalue, value), name);
            }
        }

        /// <summary>
        /// 校验任务名（工作名 / 批量名 / 周期任务名共用）。
        /// 任务名用于监控面板人工识别，因此不允许空名落库；超长名在入口直接拒绝。
        /// </summary>
        /// <param name="maxLength">最大长度，批量名需传 <see cref="MaxBatchNameLength"/></param>
        public static void CheckWorkName(string? name, int maxLength = MaxWorkNameLength)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(string.Format(Resource.ArgumentNullError, nameof(name)));
            }

            if (name!.Length > maxLength)
            {
                throw new ArgumentException(
                    string.Format(Resource.RangeValueError, $"{nameof(name)}({name.Length})", Zero, maxLength),
                    nameof(name));
            }
        }

        public static void CheckRangeValueError(double minvalue,double maxvalue,double value,string name)
        {
            if (value < minvalue || value>maxvalue)
            {
                throw new ArgumentException(string.Format(Resource.RangeValueError,$"{name}({value})", minvalue, maxvalue), name);
            }
        }

        public static void UnknownError(Exception ex)
        {
            throw new Exception(string.Format(Resource.UnknownError, ex.Message), ex);
        }

        public static string GetStacks(this Exception ex)
        {
            var inner = ex.InnerException == null ? string.Empty : ex.InnerException.StackTrace;
            return $"{ex.StackTrace} \n{inner}";
        }

        public static void KeyExistError(string key)
        {
            throw new Exception(string.Format(Resource.KeyExistError, key));
        }

        public static void CannotCreateInstance(Type type)
        {
            throw new Exception(string.Format(Resource.CannotCreateInstance,type.FullName));
        }
        #endregion

        #region Serialize

        private static JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new PublicAndNonPublicPropertyResolver()
        };
        public static string? Serialize<T>(T obj)
        {
            if (obj == null)
            {
                return null;
            }

            if (FullConvertTypes.Contains(obj.GetType()) && obj.GetType() != typeof(DateTime))
            {
                return Convert.ToString(obj);
            }

            //var serializerSettings = new JsonSerializerSettings();
            //serializerSettings.TypeNameHandling = TypeNameHandling.Objects;
            //serializerSettings.MaxDepth = 128;
            //var typeNameAssemblyFormatHandling =
            //    typeof(JsonSerializerSettings).GetRuntimeProperty("TypeNameAssemblyFormatHandling");
            //var typeNameAssemblyFormat = typeof(JsonSerializerSettings).GetRuntimeProperty("TypeNameAssemblyFormat");

            //var property = typeNameAssemblyFormatHandling ?? typeNameAssemblyFormat;
            //property.SetValue(serializerSettings, Enum.Parse(property.PropertyType, "Simple"));


            //return JsonConvert.SerializeObject(obj,typeof(T), serializerSettings);
            return JsonConvert.SerializeObject(obj);
        }

        public static T Deserialize<T>(string value)
        {
            return (T)Deserialize(value, typeof(T));
        }

        public static object? Deserialize(string value,Type type)
        {
            CheckArgumentNull(type, nameof(type));
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            // 时间跨度需要单独处理：状态数据用秒数字符串记录（见 ScheduledWorkState.GetData），
            // 而负载参数等场景是 JSON 文本（"00:00:01"），两者都无法被 Json.NET 直接转换
            if (type == typeof(TimeSpan))
            {
                return ParseTimeSpan(value);
            }

            if (FullConvertTypes.Contains(type) && type != typeof(DateTime))
            {
                return Convert.ChangeType(value, type);
            }

            var obj = JsonConvert.DeserializeObject(value,type, jsonSerializerSettings);
            return obj;
        }

        /// <summary>
        /// 解析时间跨度：既有"秒数"形态（状态数据），也有 TimeSpan 文本形态（JSON 序列化结果）。
        /// </summary>
        private static TimeSpan ParseTimeSpan(string value)
        {
            var text = value.Trim().Trim('"');

            if (!text.Contains(':')
                && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        }

        public static List<string?> SerializeToList(IEnumerable<object> arguments)
        {
            var strArgs = new List<string?>();
            foreach (var arg in arguments)
            {
                var type = arg.GetType();
                string? value = null;
                if(arg is string str)
                {
                    value = str;
                }
                else if(arg is CancellationToken)
                {
                    value = null;
                }
                else
                {
                    value = Serialize(arg);
                }

                strArgs.Add(value);
            }

            return strArgs;
        }
        #endregion

        #region Type help
        private static readonly Assembly CoreLibrary = typeof(int).GetTypeInfo().Assembly;
        private static readonly Assembly ConsoleLibrary = typeof(Console).GetTypeInfo().Assembly;
        private static readonly ConcurrentDictionary<string, Assembly> AssemblyCache = new ConcurrentDictionary<string, Assembly>();
        private static readonly AssemblyName MscorlibAssemblyName = new AssemblyName("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        private static readonly HashSet<Type> FullConvertTypes = new HashSet<Type>
        {
            typeof(bool),typeof(byte),typeof(char),typeof(DateTime),
            typeof(decimal),typeof(double),typeof(short),typeof(int),
            typeof(long),typeof(sbyte),typeof(float),typeof(ushort),
            typeof(string),typeof(uint),typeof(ulong)
        };

        public static void SetPropertyValue(this object  obj, string propertyName, object value)
        {
            if (value == null || (value is string str && string.IsNullOrEmpty(str)))
            {
                return;
            }

            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                object? val = value;
                if (!property.PropertyType.IsAssignableFrom(val.GetType()) && val.GetType() != property.PropertyType)
                {
                    if (FullConvertTypes.Contains(property.PropertyType))
                    {
                        val = Convert.ChangeType(value, property.PropertyType);
                    }
                    else if (value is string strv)
                    {
                        val = Deserialize(strv, property.PropertyType);
                    }
                }

                if (val != null)
                {
                    property.SetValue(obj, val);
                }
            }
        }

        public static Type GetType(string name)
        {
            var type = Type.GetType(name, CachedAssemblyResolver, TypeResolver,true);

            return type;
        }

        private static Assembly CachedAssemblyResolver(AssemblyName assemblyName)
        {
            return AssemblyCache.GetOrAdd(assemblyName.FullName, (assemblyString) =>
            {
                var assemblyName = new AssemblyName(assemblyString);

                if (assemblyName.Name.Equals("System.Console", StringComparison.OrdinalIgnoreCase) ||
                    assemblyName.Name.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase) ||
                    assemblyName.Name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
                {
                    assemblyName = MscorlibAssemblyName;
                }
                else if (assemblyName.Name.Equals("System.Private.Xml.Linq", StringComparison.OrdinalIgnoreCase))
                {
                    assemblyName = new AssemblyName("System.Xml.Linq, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                }

                var publicKeyToken = assemblyName.GetPublicKeyToken();

                try
                {
                    return Assembly.Load(assemblyName);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    var shortName = new AssemblyName(assemblyName.Name);
                    if (publicKeyToken != null)
                    {
                        shortName.SetPublicKeyToken(publicKeyToken);
                    }

                    return Assembly.Load(shortName);
                }
            });
        }

        private static Type TypeResolver(Assembly assembly, string typeName, bool ignoreCase)
        {
            var dtype = Type.GetType(typeName, ignoreCase);
            if (dtype != null)
            {
                assembly = dtype.Assembly;
                return dtype;
            }
            else
            {
                //There is a problem with delayed loading
                foreach (var item in AppDomain.CurrentDomain.GetAssemblies())
                {
                    dtype = item.GetType(typeName, ignoreCase);
                    if(dtype != null)
                    {
                        assembly = item;
                        return dtype;
                    }
                }
            }

            if (typeName.Equals("System.Diagnostics.Debug", StringComparison.Ordinal))
            {
                return typeof(System.Diagnostics.Debug);
            }

            if (assembly == null && typeName == "System.Console")
            {
                assembly = ConsoleLibrary;
            }

            if (assembly != CoreLibrary &&
                assembly.GetName().Name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
            {
                // Everything defaults to `mscorlib` for interoperability reasons between
                // .NET Framework and .NET Core. Most of the types have the proper forwarding,
                // but newer types like DateOnly or TimeOnly don't. So for types from `mscorlib`
                // we perform the first search in the current core library.
                var type = CoreLibrary.GetType(typeName, false, ignoreCase);
                if (type != null) return type;
            }

            return assembly.GetType(typeName, true, ignoreCase);
        }

        public static bool IsAwaitableType(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (type == typeof(Task) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)))
            {
                return true;
            }

            if (type == typeof(ValueTask) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            {
                return true;
            }

            if (type.GetInterface("IAwaitable", false) != null)
            {
                return true;
            }
            return false;
        }
        #endregion

        #region Retry
        public static TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);
        public const int DefaultRetryCount = 3;

        /// <summary>指数退避的上限（#18）：避免 2^n 把重试间隔放大到不可接受的时长。</summary>
        public static TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

        private static int _randomSeed = Environment.TickCount;
        [ThreadStatic]
        private static Random? _threadRandom;

        /// <summary>线程安全的随机数（jitter 用）。netstandard2.1 没有 Random.Shared，这里按线程各持一个实例。</summary>
        private static Random RandomInstance
        {
            get
            {
                if (_threadRandom == null)
                {
                    _threadRandom = new Random(Interlocked.Increment(ref _randomSeed));
                }
                return _threadRandom;
            }
        }

        public static async Task<Exception?> Retry(Func<int,Task> action, int retryCount, TimeSpan delayTime)
        {
            var indx = 0;
            Exception? exResult = null; ;
            while (indx < retryCount)
            {
                try
                {
                    await action(indx);
                    return exResult;
                }
                catch(Exception ex)
                {
                    indx++;
                    exResult = ex;
                    // 最后一次已失败：再等一个间隔毫无意义，直接返回异常
                    if (indx >= retryCount)
                    {
                        break;
                    }
                    await Task.Delay(GetRetryDelay(delayTime, indx));
                }
            }

            return exResult;
        }

        /// <summary>
        /// 指数退避 + jitter（#18）：baseDelay × 2^(attempt-1)，上限 <see cref="MaxRetryDelay"/>，
        /// 再乘 [0.5,1.0) 的随机因子打散各节点的重试时刻。
        /// 修复前固定间隔重试，DB 抖动时所有节点会在同一时刻一起重试，形成重试风暴。
        /// </summary>
        public static TimeSpan GetRetryDelay(TimeSpan baseDelay, int attempt)
        {
            var factor = Math.Pow(2, Math.Max(0, attempt - 1));
            var jitter = 0.5 + RandomInstance.NextDouble() * 0.5;
            var ms = baseDelay.TotalMilliseconds * factor * jitter;
            if (ms > MaxRetryDelay.TotalMilliseconds)
            {
                ms = MaxRetryDelay.TotalMilliseconds;
            }
            return TimeSpan.FromMilliseconds(ms);
        }

        #endregion
    }
}
