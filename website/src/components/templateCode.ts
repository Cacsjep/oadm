// The code of a sample (samples/) as the templates write it (tools/Oadm.NewPlugin/Templates.cs): without the
// "Oadm.Samples." prefix, the plugin projects (not the test projects) on the project SDK from NuGet. The tutorials show it
// this way, so what the reader sees is what "dotnet new oadm-..." created.
export function template(code: string): string {
	const plain = code.replaceAll('Oadm.Samples.', '');
	return plain.includes('Microsoft.NET.Test.Sdk')
		? plain
		: plain.replace('<Project Sdk="Microsoft.NET.Sdk">', '<Project Sdk="Oadm.Plugin.Sdk/x.y.z">');
}
