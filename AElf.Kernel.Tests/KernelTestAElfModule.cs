using AElf.ChainController;
using AElf.Execution;
using AElf.Kernel.Tests.Concurrency.Execution;
using AElf.Modularity;
using AElf.TestBase;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Modularity;

namespace AElf.Kernel.Tests
{
    
    [DependsOn(typeof(KernelAElfModule),
        //TODO: only test kernel aelf module here
        typeof(ChainAElfModule),typeof(ExecutionAElfModule), 
        typeof(TestBaseAElfModule))]
    public class KernelTestAElfModule : AElfModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var services = context.Services;
            services.AddTransient<MockSetup>();
        }

        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            //init test data here
        }

    }
}