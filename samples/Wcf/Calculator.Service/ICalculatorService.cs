using System.Runtime.Serialization;
using CoreWCF;

namespace Calculator.Service;

[ServiceContract(Namespace = "http://codalus.dev/samples/calculator")]
public interface ICalculatorService
{
    [OperationContract]
    int Add(int a, int b);

    [OperationContract]
    string Greet(string name);

    [OperationContract]
    double Distance(Point from, Point to);
}

[DataContract(Namespace = "http://codalus.dev/samples/calculator")]
public sealed class Point
{
    [DataMember]
    public double X { get; set; }

    [DataMember]
    public double Y { get; set; }
}
