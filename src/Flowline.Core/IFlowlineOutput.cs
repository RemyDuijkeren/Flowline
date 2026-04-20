namespace Flowline.Core;

public interface IFlowlineOutput
{
    void Info(string message);
    void Skip(string message);
    void Verbose(string message);
}

public class NullFlowlineOutput : IFlowlineOutput
{
    public void Info(string message) { }
    public void Skip(string message) { }
    public void Verbose(string message) { }
}
