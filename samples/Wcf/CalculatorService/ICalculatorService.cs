using System.Runtime.Serialization;
using CoreWCF;

namespace CalculatorService;

/// <summary>The fixture's contract: simple values, a data contract with an enum and a list, arrays, and a fault.</summary>
[ServiceContract(Namespace = Namespaces.Contract)]
public interface ICalculatorService
{
    [OperationContract]
    double Add(double a, double b);

    /// <summary>Faults when <paramref name="b"/> is zero.</summary>
    [OperationContract]
    [FaultContract(typeof(CalculationFault))]
    double Divide(double a, double b);

    [OperationContract]
    string Greet(Person person);

    [OperationContract]
    Person[] FindPeople(string prefix, int max);
}

public static class Namespaces
{
    public const string Contract = "http://samples.codalus.dev/calculator";
}

[DataContract(Namespace = Namespaces.Contract)]
public sealed class Person
{
    [DataMember(Order = 0)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 1)]
    public int Age { get; set; }

    [DataMember(Order = 2)]
    public Mood Mood { get; set; }

    [DataMember(Order = 3)]
    public List<string> Tags { get; set; } = [];
}

[DataContract(Namespace = Namespaces.Contract)]
public enum Mood
{
    [EnumMember]
    Calm,

    [EnumMember]
    Happy,

    [EnumMember]
    Grumpy,
}

[DataContract(Namespace = Namespaces.Contract)]
public sealed class CalculationFault
{
    [DataMember]
    public string Operation { get; set; } = string.Empty;

    [DataMember]
    public string Problem { get; set; } = string.Empty;
}
