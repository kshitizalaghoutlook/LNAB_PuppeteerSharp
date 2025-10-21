
public sealed class Service1 : IService1
{
    public void startProcess()
    {
        // wake your processing loop, etc.
        Program.SignalKick();
    }

    public void reStart()
    { 
        Program.RestartApp();
    }
}