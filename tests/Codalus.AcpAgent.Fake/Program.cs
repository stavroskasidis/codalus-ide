// A stand-in ACP agent for the tests (M48): scripted sessions on stdio, see tests/Shared/StandInAcpAgent.cs.
return await Codalus.Tests.StandInAcpAgent.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()).ConfigureAwait(false);
