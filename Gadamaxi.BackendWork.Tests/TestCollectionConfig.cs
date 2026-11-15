using Microsoft.Extensions.DependencyInjection;
using Xunit;

// 依赖全局静态 DependencyInjectionHelper 的测试必须串行执行
[assembly: CollectionBehavior(DisableTestParallelization = true)]
