using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoncaAudioInspector;
using SoncaAudioInspector.Update;

internal static class PhaseA2Checks
{
    private const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static IEnumerable<T> Tree<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)yield return found;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var item in Tree<T>(child))yield return item;
    }
    private static void Render(FrameworkElement element,string path,int width,int height)
    {
        element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    private static void RenderPrompt(ModernMessageBox prompt,string path)
    {
        prompt.WindowStartupLocation=WindowStartupLocation.Manual;prompt.Left=-30000;prompt.Top=-30000;
        prompt.ShowActivated=false;prompt.ShowInTaskbar=false;if(!prompt.IsVisible)prompt.Show();
        prompt.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);prompt.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(prompt.ActualWidth),(int)Math.Ceiling(prompt.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(prompt);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    public static void Run(string appDirectory,string output,Action<bool,string> check)
    {
        string artifact=Directory.GetParent(output)!.Parent!.FullName;output=Path.Combine(output,"phase-a2");Directory.CreateDirectory(output);
        var parentCheck=check;check=(ok,text)=>{parentCheck(ok,text);File.AppendAllText(Path.Combine(output,"checks.txt"),"PASS "+text+Environment.NewLine);};
        CheckNotes(appDirectory,output,check);
        CheckCaptureAlias(appDirectory,artifact,output,check);
    }
    private static void CheckNotes(string appDirectory,string output,Action<bool,string> check)
    {
        string[] release=File.ReadAllLines("docs/release-notes/1.4.0.txt",Encoding.UTF8);
        check(release[0].Length<=90&&release.Length<=11&&release.Length>1,"P1 release file: <=90 character summary and <=10 details");
        check(!release.Any(line=>line.Contains("Phase",StringComparison.OrdinalIgnoreCase)),"P1 release notes use user-facing wording (no internal phase names)");
        var manifest=new UpdateManifest(1,"SoncaAudioInspector","1.4.0","win-x64",DateTimeOffset.UtcNow,"",[new("SoncaAudioInspector.dll",1,new string('a',64))],release);
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(manifest,UpdateManifest.JsonOptions);
        var read=UpdateManifest.Read(bytes);read.Validate();
        check(read.FormatFeatures()==release[0]&&read.FeatureDetails().SequenceEqual(release.Skip(1)),"P1 existing Features-only feed retains summary and every detail");
        check(!Encoding.UTF8.GetString(bytes).Contains("\"summary\""),"P1 no new manifest field or signed payload schema");
        var legacy=manifest with{Features=null,Notes="Ghi chú feed cũ\nChi tiết cũ"};
        check(legacy.FormatFeatures()=="Ghi chú feed cũ"&&legacy.FeatureDetails().SequenceEqual(["Chi tiết cũ"]),"P1 legacy feed without Features uses Notes fallback");
        var longList=manifest with{Features=Enumerable.Range(0,20).Select(i=>"feature-"+i).ToArray()};
        check(longList.FormatFeatures()=="feature-0"&&longList.FeatureDetails().Last()=="feature-19","P1 details have no eight-line truncation");

        string fixture=Path.Combine(output,"installed");Directory.CreateDirectory(fixture);
        var installed=new InstalledVersionInfo(AppVersion.Numeric,DateTimeOffset.Parse("2026-11-09T00:00:00Z"),
            [AppVersion.Display+": ghi chú gọn, giữ dữ liệu trạm.","Chi tiết thứ nhất","Chi tiết thứ hai"]);
        File.WriteAllBytes(Path.Combine(fixture,"version-info.json"),JsonSerializer.SerializeToUtf8Bytes(installed,UpdateManifest.JsonOptions));
        var view=new VersionInfoView();view.LoadFromDirectory(fixture);
        view.Bind(new UpdateCheckState{CheckRequested=_=>Task.CompletedTask});
        var notes=Tree<ReleaseNotesView>(view).Single();
        check(notes.Summary.Text==installed.Features[0]&&!notes.DetailExpander.IsExpanded,"P1 version tab: summary present, detail expander closed initially");
        check(notes.Details.Text.Contains(installed.Features[1])&&notes.Details.Text.Contains(installed.Features[2]),"P1 old version-info with only Features retains all details");
        var versionHost=new Border{Background=new SolidColorBrush(Color.FromRgb(24,24,27)),Child=view};
        Render(versionHost,Path.Combine(output,"version-collapsed.png"),1050,430);
        notes.DetailExpander.IsExpanded=true;Render(versionHost,Path.Combine(output,"version-expanded.png"),1050,500);
        check(notes.DetailExpander.IsExpanded&&notes.Details.ActualHeight>0,"P1 opening version expander lays out complete details");
        view.LoadFromDirectory(fixture);check(!notes.DetailExpander.IsExpanded&&notes.Summary.Text.Length>0,"P1 reloading notes closes details and keeps summary");
        File.Delete(Path.Combine(fixture,"version-info.json"));view.LoadFromDirectory(fixture);
        check(notes.Summary.Text.Contains("Bản phát triển")&&notes.DetailExpander.Visibility==Visibility.Collapsed,"P1 missing installed file still shows development summary");

        var prompt=(ModernMessageBox)typeof(MainWindow).GetMethod("CreateUpdatePrompt",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[new UpdatePlan(manifest,manifest.Files)])!;
        var promptNotes=Tree<ReleaseNotesView>(prompt).Single();
        check(promptNotes.Summary.Text==release[0]&&!promptNotes.DetailExpander.IsExpanded,"P1 production update prompt factory shows compact summary by default");
        check(promptNotes.Details.Text.Split('\n').Length==release.Length-1&&promptNotes.Details.Text.Contains(release.Last()),"P1 prompt retains every release detail");
        check(Tree<Button>(prompt).Any(b=>b.Content?.ToString()=="Cập nhật ngay")&&Tree<Button>(prompt).Any(b=>b.Content?.ToString()=="Để sau"),"P1 update decisions preserve existing labels");
        RenderPrompt(prompt,Path.Combine(output,"update-prompt-collapsed.png"));
        promptNotes.DetailExpander.IsExpanded=true;RenderPrompt(prompt,Path.Combine(output,"update-prompt-expanded.png"));
        check(promptNotes.Details.ActualHeight>0,"P1 actual update prompt expands all details");
        Tree<Button>(prompt).Single(b=>b.Name=="BtnNo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(!prompt.Accepted,"P1 defer action keeps update confirmation unaccepted");
    }
    private static void CheckCaptureAlias(string appDirectory,string artifact,string output,Action<bool,string> check)
    {
        // Committed fixtures keep this check runnable anywhere: checking_config.json and baseline identities as of a903e0f.
        const string fixtureDir="tools/ConfigAcceptanceCheck/Fixtures/PhaseA2";
        var before=JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(Path.Combine(fixtureDir,"checking_config-a903e0f.json")))!;
        var after=JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(Path.Combine(appDirectory,"checking_config.json")))!;
        var old=before.models.Single(m=>m.model=="C360");var renamed=after.models.Single(m=>m.model=="C360");
        check(old.testItems.InOut.Devices.Output.ContainsKey("LINE OUT")&&renamed.testItems.InOut.Devices.Output.ContainsKey("RECORDING TYPE C"),"P2 actual checking_config C360 old/new keys");
        foreach(var other in before.models.Where(m=>m.model!="C360"))
            check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(other),JsonSerializer.SerializeToNode(after.models.Single(m=>m.model==other.model))),"P2 other model unchanged: "+other.model);
        var oldPlan=Plan(old.testItems.InOut);var newPlan=Plan(renamed.testItems.InOut);
        check(C360ModelDefinition.Matches(oldPlan.Configuration)&&C360ModelDefinition.Matches(newPlan.Configuration),"P2 Matches and Validate accept both complete C360 configurations");
        check(oldPlan.Setup.CaptureDisplayName=="RECORDING TYPE C"&&newPlan.Setup.CaptureDisplayName=="RECORDING TYPE C","P2 C360 shared setup display label covers legacy configuration");
        check(oldPlan.Setup.LineOutCaptureId==newPlan.Setup.LineOutCaptureId&&oldPlan.CaptureIds.SequenceEqual(newPlan.CaptureIds),"P2 physical endpoint IDs and channels unchanged");
        check(C360ModelDefinition.ReferenceConfiguration(old.testItems.InOut)==C360ModelDefinition.ReferenceConfiguration(renamed.testItems.InOut),"P2 resume configuration bytes canonicalize labels/description/property order");
        var seed=C360ModelDefinition.Create();C360ModelDefinition.Validate(seed.testItems.InOut);
        check(seed.testItems.InOut.Tests.Where(t=>t.SpeakerStage!="AcousticMic").All(t=>t.RecordingIn=="RECORDING TYPE C"),"P2 C360 seed electronic defaults use new alias");
        var acoustic=Acoustic3ModelDefinition.Create("analog","capture","usb",true,true,true,true);var acousticPlan=Plan(acoustic.testItems.InOut);
        check(acoustic.testItems.InOut.Devices.Output.ContainsKey("LINE OUT")&&!acoustic.testItems.InOut.Devices.Output.ContainsKey("RECORDING TYPE C")
            &&acoustic.testItems.InOut.Tests.Where(t=>t.SpeakerStage!="AcousticMic").All(t=>t.RecordingIn=="LINE OUT")&&acousticPlan.Setup.CaptureDisplayName=="LINE OUT","P2 ACOUSTIC 3 seed, tests and shared messages keep LINE OUT");
        string expectedPath=Path.Combine(fixtureDir,"baseline-a903e0f.json");
        var expected=File.Exists(expectedPath)?JsonNode.Parse(File.ReadAllText(expectedPath))!.AsObject():null;
        // Baseline identities come only from committed source fixtures; no artifact/environment dependencies.
        AssemblyLoadContext? context=null;Assembly? assembly=null;
        check(expected!=null||assembly!=null,"P2 a903e0f baseline available (committed fixture or baseline DLL)");
        string? baselineAcousticJson=expected?["acousticInOut"]?.GetValue<string>();
        if(assembly!=null)
        {
            var baselineAcoustic=assembly.GetType(typeof(Acoustic3ModelDefinition).FullName!)!.GetMethod("Create")!.Invoke(null,["analog","capture","usb",true,true,true,true])!;
            string fromDll=JsonNode.Parse(JsonSerializer.Serialize(baselineAcoustic,baselineAcoustic.GetType()))!["testItems"]!["InOut"]!.ToJsonString();
            if(baselineAcousticJson!=null) check(baselineAcousticJson==fromDll,"P2 committed ACOUSTIC 3 baseline fixture equals baseline DLL");
            baselineAcousticJson??=fromDll;
        }
        check(baselineAcousticJson==JsonSerializer.Serialize(acoustic.testItems.InOut),"P2 ACOUSTIC 3 factory configuration bytes/order equal a903e0f baseline");
        var baselinePlan=assembly==null?null:JsonSerializer.Deserialize(JsonSerializer.Serialize(oldPlan),assembly.GetType(typeof(C360ModelPlan).FullName!)!)!;
        string mixer=SoncaMixerControls.Fingerprint(new Dictionary<ushort,ushort>{[0x6254]=73},[],44100);
        var hashes=new List<object>();var fixture=Path.Combine(output,"legacy-standards");Directory.CreateDirectory(fixture);
        var generatedStages=new JsonObject();
        foreach(var stage in oldPlan.Stages)
        {
            string oldKey=C360Reference.ConfigurationKey("C360",oldPlan,stage,mixer),newKey=C360Reference.ConfigurationKey("C360",newPlan,stage,mixer);
            var a=oldPlan.Configuration.Tests.Single(t=>t.IsEnabled&&C360ModelDefinition.Stage(t)==stage);
            var b=newPlan.Configuration.Tests.Single(t=>t.IsEnabled&&C360ModelDefinition.Stage(t)==stage);
            string? baselineKey=expected?["stages"]?[stage.ToString()]?["key"]?.GetValue<string>();
            string? baselineFingerprint=expected?["stages"]?[stage.ToString()]?["fingerprint"]?.GetValue<string>();
            if(assembly!=null)
            {
                var baselineStage=Enum.Parse(assembly.GetType(typeof(C360ElectronicStage).FullName!)!,stage.ToString());
                string dllKey=(string)assembly.GetType(typeof(C360Reference).FullName!)!.GetMethod("ConfigurationKey")!.Invoke(null,["C360",baselinePlan,baselineStage,mixer,null])!;
                string dllFingerprint=Fingerprint(assembly,oldPlan.Configuration,a);
                if(baselineKey!=null) check(baselineKey==dllKey&&baselineFingerprint==dllFingerprint,"P2 committed baseline fixture equals baseline DLL: "+stage);
                baselineKey??=dllKey;baselineFingerprint??=dllFingerprint;
            }
            check(baselineKey!=null&&oldKey==newKey&&newKey==baselineKey,"P2 C360 reference hash equals a903e0f baseline and renamed config: "+stage);
            string oldFingerprint=Fingerprint(typeof(AudioRouting).Assembly,oldPlan.Configuration,a),newFingerprint=Fingerprint(typeof(AudioRouting).Assembly,newPlan.Configuration,b);
            check(baselineFingerprint!=null&&oldFingerprint==newFingerprint&&newFingerprint==baselineFingerprint,"P2 CSV model fingerprint equals baseline and renamed config: "+stage);
            generatedStages[stage.ToString()]=new JsonObject{["key"]=baselineKey,["fingerprint"]=baselineFingerprint};
            check(C360ModelDefinition.ReferenceRoute(oldPlan.Configuration,a)==C360ModelDefinition.ReferenceRoute(newPlan.Configuration,b),"P2 route token keeps legacy reference path: "+stage);
            string oldPink=PinkContext(a),newPink=PinkContext(b);
            check(oldPink==newPink&&PinkRubBuzzStimulus.Key(new(),oldPink+mixer)==PinkRubBuzzStimulus.Key(new(),newPink+mixer)
                &&ChirpRubBuzzStimulus.Key(new(),oldPink+mixer)==ChirpRubBuzzStimulus.Key(new(),newPink+mixer),"P2 real Pink context formatter and Pink/Chirp hashes unchanged: "+stage);
            var saved=new C360Reference(baselineKey,DateTimeOffset.Parse("2026-11-09T00:00:00Z"),new(){[100]=0,[1000]=0,[10000]=0},null,null,2);
            File.WriteAllText(Path.Combine(fixture,"c360-"+baselineKey+".json"),JsonSerializer.Serialize(saved,C360Reference.Json));
            var bench=new NeverCapture();
            if(C360ReferenceBench.UsesNoReference(stage))
            {
                // Owner 2026-11-19: echo/reverb, FBX and Rè never use a LINE CHUẨN, so a saved legacy file is not read: the stage is measured.
                try{new C360ReferenceBench(bench,newPlan,"C360",fixture,mixer,true,3).CaptureAsync(stage,newPlan.SetupFor(b),default).GetAwaiter().GetResult();}catch(Exception ex) when(ex.Message=="Legacy reference was not reused"){}
                check(bench.Captures==1,"P2 no-reference stage ignores a saved LINE CHUẨN and measures: "+stage);
                hashes.Add(new{stage=stage.ToString(),baselineKey,oldKey,newKey,baselineFingerprint,oldFingerprint,newFingerprint,
                    route=C360ModelDefinition.ReferenceRoute(newPlan.Configuration,b),pinkContext=oldPink,pinkKey=PinkRubBuzzStimulus.Key(new(),oldPink+mixer),chirpKey=ChirpRubBuzzStimulus.Key(new(),oldPink+mixer),mixer});
                continue;
            }
            var loaded=new C360ReferenceBench(bench,newPlan,"C360",fixture,mixer,true,3).CaptureAsync(stage,newPlan.SetupFor(b),default).GetAwaiter().GetResult();
            check(loaded.Reference?.Key==baselineKey&&bench.Captures==0&&loaded.Summary.Contains("ĐÃ CÓ LINE CHUẨN"),"P2 actual reference loader reuses legacy baseline fixture without capture: "+stage);
            using (ResultPersistencePolicy.BeginTrial(true))
            {
                var trialGate = new C360ReferenceBench(bench,newPlan,"C360",Path.Combine(output,"missing-trial-standard"),mixer,false,3)
                    .CaptureAsync(stage,newPlan.SetupFor(b),default).GetAwaiter().GetResult();
                check(trialGate.Status=="INVALID"&&bench.Captures==0&&trialGate.Summary.Contains("LINE CHUẨN THỬ"),"Trial Auto Test requires trial reference before emitting a stimulus: "+stage);
            }
            hashes.Add(new{stage=stage.ToString(),baselineKey,oldKey,newKey,baselineFingerprint,oldFingerprint,newFingerprint,
                route=C360ModelDefinition.ReferenceRoute(newPlan.Configuration,b),pinkContext=oldPink,pinkKey=PinkRubBuzzStimulus.Key(new(),oldPink+mixer),chirpKey=ChirpRubBuzzStimulus.Key(new(),oldPink+mixer),mixer});
        }
        if(expected==null)
        {
            Directory.CreateDirectory(fixtureDir);
            File.WriteAllText(expectedPath,new JsonObject{["source"]="a903e0f baseline DLL",["acousticInOut"]=baselineAcousticJson,["stages"]=generatedStages}
                .ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        }
        File.WriteAllText(Path.Combine(output,"reference-hashes.json"),JsonSerializer.Serialize(hashes,new JsonSerializerOptions{WriteIndented=true}));
        context?.Unload();
    }
    private static string Fingerprint(Assembly assembly,InOutConfig io,TestConfig test)
    {
        var type=assembly.GetType(typeof(AudioRouting).FullName!)!;var instance=RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_activeInOutConfig",Private)!.SetValue(instance,JsonSerializer.Deserialize(JsonSerializer.Serialize(io),assembly.GetType(typeof(InOutConfig).FullName!)!));
        type.GetField("_appliedTestConfig",Private)!.SetValue(instance,JsonSerializer.Deserialize(JsonSerializer.Serialize(test),assembly.GetType(typeof(TestConfig).FullName!)!));
        return (string)type.GetMethod("AppliedModelTestFingerprint",Private)!.Invoke(instance,[false])!;
    }
    private static C360ModelPlan Plan(InOutConfig io)
    {
        var endpoints=io.Devices.Input.Values.Distinct().Select((name,index)=>new C360ModelEndpoint("out-"+index,name,false))
            .Concat(io.Devices.Output.Values.Distinct().Select((name,index)=>new C360ModelEndpoint("in-"+index,name,true))).ToArray();
        return C360ModelPlan.Create(io,endpoints,new("unused","","unused"));
    }
    private static string PinkContext(TestConfig test) => (string)typeof(AudioRouting).GetMethod("FormatPinkRubBuzzContext",BindingFlags.NonPublic|BindingFlags.Static)!
        .Invoke(null,[test,"out-id","in-id","C360",44100,1d,false,44100,44100,0,0,.5f,.5f])!;
    private sealed class NeverCapture:IC360ElectronicBench
    {
        public int Captures;public string DeviceIdentity=>"offline";
        public Task<IReadOnlyDictionary<ushort,ushort>> ReadAsync(CancellationToken token)=>throw new Exception("Unexpected device read");
        public Task WriteAsync(ushort register,ushort value,CancellationToken token)=>throw new Exception("Unexpected device write");
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage,C360ElectronicSetup setup,CancellationToken token){Captures++;throw new Exception("Legacy reference was not reused");}
        public void StopAudio(){}
    }
}
