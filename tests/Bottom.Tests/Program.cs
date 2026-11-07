using CMUmcl.Content;
using CMUmcl.Network;
using CMUmcl.Launch;
internal static partial class Program {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
 static async Task<int> Main(){try { await Run();return 0; }catch(Exception ex){Console.WriteLine(ex.GetType().FullName);Console.WriteLine(ex.Message);Console.WriteLine(ex.StackTrace);return 1;}}
 static async Task Run(){ LauncherPaths.RootOverride=Path.Combine(Path.GetTempPath(),"CMUmcl-bottom-"+Guid.NewGuid().ToString("N"));LauncherPaths.EnsureDirectories();var store=new SettingsStore();store.Load();LauncherProxy.Initialize();
 try { await ProxyChecks(store); await AuthSessionChecks(); await RememberPasswordChecks();
 using(var memory=new System.IO.MemoryStream()) { byte[] source={1,2,3,4};await memory.WriteAsync(source.AsMemory(1,2),CancellationToken.None);Check(memory.ToArray().SequenceEqual(new byte[]{2,3}),"net48 memory-slice async write preserves bytes"); }
 Check(JavaVersionResolver.ResolveFallbackMajor("1.17.1")==16,"1.17 Java16 fallback");Check(JavaVersionResolver.ResolveFallbackMajor("1.21.9")==21,"1.21 Java21 fallback");Check(MemoryAllocation.Recommend(new MemorySnapshot(16384,12000))==8192,"automatic memory reserve");Check(MemoryAllocation.Recommend(new MemorySnapshot(16384,12000),32)==1024,"32-bit memory cap");Check(MemoryAllocation.ValidateManual(2048,new MemorySnapshot(16384,12000),32)!=null,"invalid 32-bit manual allocation rejected"); Console.WriteLine("PASS "+passed+" bottom checks"); } finally {LauncherProxy.Shutdown();} }
}
