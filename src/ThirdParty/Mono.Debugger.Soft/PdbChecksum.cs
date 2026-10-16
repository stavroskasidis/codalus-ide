// Not part of debugger-libs: replaces Microsoft.FileFormats.PE.PdbChecksum, the one type the vendored client used
// from Microsoft.FileFormats (Codalus IDE, MIT).
namespace Mono.Debugger.Soft
{
	public sealed class PdbChecksum
	{
		public PdbChecksum (string algorithmName, byte[] checksum)
		{
			AlgorithmName = algorithmName;
			Checksum = checksum;
		}

		public string AlgorithmName { get; }

		public byte[] Checksum { get; }
	}
}
