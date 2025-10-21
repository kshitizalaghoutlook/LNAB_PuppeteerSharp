using CWCF = CoreWCF;
using SSM = System.ServiceModel;

// Server-side contract (CoreWCF) — used by your listener/host
[CWCF.ServiceContract(Name = "IService1", Namespace = "http://tempuri.org/")]
public interface IService1
{
    [CWCF.OperationContract] void startProcess();
    [CWCF.OperationContract] void reStart();
}

// Client-side contract (System.ServiceModel) — used by ChannelFactory on the caller side
[SSM.ServiceContract(Name = "IService1", Namespace = "http://tempuri.org/")]
public interface IService1Client
{
    [SSM.OperationContract] void startProcess();
    [SSM.OperationContract] void reStart();
}
