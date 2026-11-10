using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ControlTasksTests
{
    [Fact]
    public async Task TasksReportOutputAndRejectUnknownOrBuiltinCommands()
    {
        var directory=Path.Combine(Path.GetTempPath(),"phonebox-task-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var executable=OperatingSystem.IsWindows()?Path.Combine(Environment.SystemDirectory,"cmd.exe"):"/bin/sh";
            var arguments=OperatingSystem.IsWindows()?"/c echo owned-task-proof":"-c \"printf owned-task-proof\"";
            var path=Path.Combine(directory,"config.json");
            File.WriteAllText(path,JsonSerializer.Serialize(new {shortcuts=new[]{new {id="proof",cmd=executable,args=arguments,cwd=directory},new {id="keys",cmd="builtin:keys",args="Ctrl+A",cwd=directory}}}));
            using var tasks=new ControlTasks(new ConfigStore(path),NullLogger.Instance);
            Assert.False(tasks.Start(new(){Id="bad",Target="not-configured"},false).Ok);
            Assert.False(tasks.Start(new(){Id="builtin",Target="keys"},false).Ok);
            Assert.True(tasks.Start(new(){Id="run",Target="proof"},false).Ok);
            await tasks.StopWhenCompleted();
            Assert.Equal("Completed",tasks.State("run"));Assert.Contains("owned-task-proof",tasks.Output("run"));
            Assert.False(tasks.Start(new(){Id="run",Target="proof"},true).Ok);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task StoppingAnOwnedServerDoesNotStopAnUnrelatedProcess()
    {
        var directory=Path.Combine(Path.GetTempPath(),"phonebox-server-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var executable=OperatingSystem.IsWindows()?Path.Combine(Environment.SystemDirectory,"cmd.exe"):"/bin/sh";
        var arguments=OperatingSystem.IsWindows()?"/c ping -n 60 127.0.0.1 > nul":"-c \"sleep 60\"";
        using var unrelated=Process.Start(new ProcessStartInfo(executable,arguments){UseShellExecute=false,CreateNoWindow=true})!;
        try
        {
            var path=Path.Combine(directory,"config.json");File.WriteAllText(path,JsonSerializer.Serialize(new{shortcuts=new[]{new{id="server",cmd=executable,args=arguments,cwd=directory}}}));
            using var tasks=new ControlTasks(new ConfigStore(path),NullLogger.Instance);
            var action=new DeckAction{Id="owned",Target="server",Kind="server"};
            Assert.True(tasks.Start(action,false).Ok);
            Assert.False(tasks.Start(action,false).Ok);
            Assert.True(tasks.Start(action,true).Ok);
            await tasks.StopAsync();
            Assert.Equal("Stopped",tasks.State("owned"));Assert.False(unrelated.HasExited);
        }
        finally {if(!unrelated.HasExited)unrelated.Kill(entireProcessTree:true);await unrelated.WaitForExitAsync();Directory.Delete(directory,true);}
    }
}

internal static class TaskTestWait
{
    internal static async Task StopWhenCompleted(this ControlTasks tasks)
    {
        for(var i=0;i<100 && tasks.State("run") is "Running" or "Starting";i++)await Task.Delay(25);
        Assert.Equal("Completed",tasks.State("run"));await tasks.StopAsync();
    }
}
