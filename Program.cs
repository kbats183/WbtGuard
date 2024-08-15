using WbtGuardService;

using Microsoft.AspNetCore;

using System;
using Topshelf.Logging;
using Topshelf;
using System.Security.Principal;

var identity = WindowsIdentity.GetCurrent();
var principal = new WindowsPrincipal(identity);
if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("The service installation needs to be run with administrator privileges!");
    Console.WriteLine("Please right-click to re-run with administrator privileges!");
    Console.WriteLine("Press any key to exit the program!");
    Console.ReadKey();
    Environment.Exit(exitCode: 0);
}
var logger = HostLogger.Current.Get("UseWindowService");
var rc = HostFactory.Run(x =>
{
    x.UseNLog();
    x.Service<GuardService>(s =>
    {
        s.ConstructUsing(name => new GuardService(args));
        s.WhenStarted(tc => tc.Start());
        s.WhenStopped(tc => tc.Stop());
        s.WhenShutdown(tc => tc.Shutdown());
    });
    x.RunAsLocalSystem();
    x.EnablePowerEvents();


    x.SetDescription("the common guard service, same as  supervisor on linux");
    x.SetDisplayName("Guard Service");
    x.SetServiceName("WbtGuard");
});

var exitCode = (TypeCode)Convert.ChangeType(rc, rc.GetTypeCode());
Environment.ExitCode = (int)exitCode;

if (exitCode != 0)
{
    logger.Error($"The program exits abnormally: {exitCode.ToString()} {rc.ToString()}");
}