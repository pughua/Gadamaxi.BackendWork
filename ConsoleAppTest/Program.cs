using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LinqToDB;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Sql;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using IdGen;
using System.Reflection.Emit;
using NLog.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;

namespace ConsoleAppTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            RunHost();
            Test().Wait();
            //Test_RecurringWork();
            Console.ReadKey();

        }

        static async Task Test()
        {
            while (true)
            {
                try
                {
                    Console.WriteLine("请输入类型，1,队列，2,定时任务，3,周期任务，4,连续任务，5创建批量任务，6,Saga分布式事务");
                    var key = Console.ReadLine();
                    if (key == "2")
                    {
                        await WorkHelper.Manager.Schedule(() => Console.WriteLine($"Schedule {Helper.CurrentUtcTime()}"), TimeSpan.FromSeconds(60), "schedule-demo");
                    }
                    else if (key == "3")
                    {
                        await WorkHelper.Recurring.AddOrUpdate< Person>($"Recurring_{Helper.CurrentUtcTime().UnixMillisecondStamp()}",
                            (p) => p.SetName($"Recurring {Helper.CurrentUtcTime()}"),
                            "*/5 * * * *", "test", new RecurringWorkContext{
                                TimeZoneInfo = TimeZoneInfo.Local
                            }, "recurring-demo");
                    }
                    else if (key == "4")
                    {
                        var id = await WorkHelper.Manager.Enqueue(() => Console.WriteLine($"Enqueue continue {Helper.CurrentUtcTime()}"), "enqueue-demo");
                        await WorkHelper.Manager.Awaiting(() => Console.WriteLine($"Awaiting {id} {Helper.CurrentUtcTime()}"), id, "awaiting-demo");
                    }
                    else if(key == "5")
                    {
                        await WorkHelper.Batch.Create((ctx) =>
                        {
                            for(var i = 0; i < 100; i++)
                            {
                                ctx.Enqueue(() => Console.WriteLine($"order {i}"));
                            }
                        }, "batch-demo");
                    }
                    else if (key == "6")
                    {
                        // Saga 分布式事务演示：三步正向 + 逆序补偿，步骤与补偿都声明在 OrderSaga 里。
                        // CorrelationId 是业务关联键（唯一），重复启动会返回同一个 saga（幂等）。
                        var sagaId = await WorkHelper.Saga.Start(
                            new OrderSaga { Corr = $"order-{Helper.CurrentUtcTime().UnixMillisecondStamp()}" });

                        Console.WriteLine($"saga started: {sagaId}");
                    }
                    else
                    {
                        await WorkHelper.Manager.Enqueue<Person>((p) => p.SetName($"Enqueue {Helper.CurrentUtcTime()}"), "enqueue-person");
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.GetStacks());
                }
            }
        }

        static void RunHost()
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables().Build();
            var host = new HostBuilder()
                //.ConfigureLogging(x => x.AddConsole().SetMinimumLevel(LogLevel.Information))
                .ConfigureLogging(x => x.AddNLog(config))
                .ConfigureServices((ctx, services) =>
                {
                    services.AddBackendWork(new Gadamaxi.BackendWork.Server.WorkServerOption
                    {

                    }, (config) =>
                    {
                        config.UseSqlBackendWork(services, () =>
                        {
                            return new DataOptions().UseSqlServer("Data Source=(localdb)\\ProjectModels;Initial Catalog=backendworkdb;Integrated Security=True;Connect Timeout=30;Encrypt=False;Trust Server Certificate=False;Application Intent=ReadWrite;Multi Subnet Failover=False");
                            //return new DataOptions().UseSQLite("Data Source=C:\\Users\\lemonnc\\Downloads\\test.db;Cache=Shared;BinaryGUID=False;");
                        });
                    });
                })
            .Build();

            Console.WriteLine("Start finish!");
            var runner = host.Services.GetRequiredService<IMigrationRunner>();

            runner.MigrateUp();

            Console.WriteLine("Migrate finish!");

            host.RunAsync();
        } 

        static void Test_RecurringWork()
        {
            var work = new RecurringWork("")
            {
                Cron = "*/2 10-20 13 3 4", //分钟 小时 日期 月份 星期1-7
                TimeZone = TimeZoneInfo.Local.Id
            };

            do
            {
                Console.WriteLine("等待1分钟");
                Thread.Sleep(1000 * 20);
                var times = work.GenNextExecuteTime(work.LastExecuteUtcTime ?? work.CreateUtcTime, Helper.CurrentUtcTime());
                var flag = work.CheckCurrentTimeRun(Helper.CurrentUtcTime().UnixMillisecondStamp());
                Console.WriteLine($"NextExecuteUtcTime={work.NextExecuteUtcTime},{flag},now={Helper.CurrentUtcTime()}");
                work.LastExecuteUtcTime = Helper.CurrentUtcTime();
            } while (true);


        }
    }

    /// <summary>
    /// Saga 演示实现类：三步正向（扣库存 → 扣款 → 发通知）+ 逆序补偿，
    /// 步骤与补偿都声明为本类实例方法；跨步骤传值靠 outputKey 变量池（回填同名属性）。
    /// </summary>
    public class OrderSaga : Saga<OrderSaga>
    {
        /// <summary>订单号（业务关联键，幂等创建）</summary>
        public string Corr { get; set; } = string.Empty;

        /// <summary>第一步返回的库存单号；第二步经变量池按名回填后读 this 拿到</summary>
        public string StockNo { get; set; } = string.Empty;

        public override string CorrelationId => Corr;

        protected override void Configure(SagaDefinition<OrderSaga> def)
        {
            // 第一步把返回值采集进变量池，后续步骤可用同名属性引用
            def.Step(s => s.DeductStock(), s => s.RollbackStock(), outputKey: "stockNo");
            def.Step(s => s.DeductPayment(), s => s.Refund());
            // 只读/通知类步骤：不需要撤销副作用
            def.StepNoCompensate(s => s.SendNotification());
        }

        public Task<string> DeductStock()
        {
            var stockNo = $"stock-{Helper.CurrentUtcTime().UnixMillisecondStamp()}";
            Console.WriteLine($"扣库存 {Helper.CurrentUtcTime()} -> {stockNo}");
            return Task.FromResult(stockNo);
        }

        public Task DeductPayment()
        {
            Console.WriteLine($"扣款 {Helper.CurrentUtcTime()}（库存单 {StockNo}）");
            return Task.CompletedTask;
        }

        public Task SendNotification()
        {
            Console.WriteLine($"发送通知 {Helper.CurrentUtcTime()}");
            return Task.CompletedTask;
        }

        public Task RollbackStock()
        {
            Console.WriteLine($"回滚库存 {Helper.CurrentUtcTime()}");
            return Task.CompletedTask;
        }

        public Task Refund()
        {
            Console.WriteLine($"退款 {Helper.CurrentUtcTime()}");
            return Task.CompletedTask;
        }
    }

    public class Person
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }

        public void SetName(string name)
        {
            Name = name;
            Console.WriteLine($"{Name}{Helper.CurrentUtcTime()}");
            //throw new Exception($"error {name}");
        }
    }


}
