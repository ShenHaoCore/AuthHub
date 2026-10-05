// 集成测试会通过环境变量切换数据库等配置，而环境变量是进程级的，
// 因此整个程序集关闭并行执行，避免用例之间互相污染。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
