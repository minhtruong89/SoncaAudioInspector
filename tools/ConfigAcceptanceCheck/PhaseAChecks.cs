using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoncaAudioInspector;

internal static class PhaseAChecks
{
    private static T Ui<T>(FrameworkElement owner,string name) => (T)owner.FindName(name);
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Instance)!.Invoke(owner, args);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Instance)!.SetValue(owner, value);
    private static T Get<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static void Property(object owner, string name, object value) => owner.GetType().GetProperty(name, Instance)!.SetValue(owner, value);
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static T AwaitResult<T>(Task<T> task) { Await(task); return task.GetAwaiter().GetResult(); }
    private static IEnumerable<T> Elements<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (object item in LogicalTreeHelper.GetChildren(parent))
            if (item is DependencyObject child) { if (child is T t) yield return t; foreach (var nested in Elements<T>(child)) yield return nested; }
    }
    private static void Render(FrameworkElement root, string path, double width, double height)
    {
        root.Measure(new Size(width,height)); root.Arrange(new Rect(0,0,width,height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(path); png.Save(stream);
    }
    public static void Run(string output, Action<bool,string> check)
    {
        output=Path.Combine(output,"phase-a"); Directory.CreateDirectory(output);
        var previousContext=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var recordCheck=check;check=(ok,message)=>{recordCheck(ok,message);File.AppendAllText(Path.Combine(output,"checks-progress.txt"),"PASS "+message+Environment.NewLine);};
        string? previousData=Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR",Path.Combine(output,"session-data"));
        string? admin=Environment.GetEnvironmentVariable("SONCA_CONFIG_ADMIN");Environment.SetEnvironmentVariable("SONCA_CONFIG_ADMIN",null);
        var sessions=SessionModelConfigurations.Current;
        var context=new ProductMeasurementContext();int generated=0;
        string Generate(){generated++;return "AUTO-001";}
        context.Receive(" USER-001 ","Demo",SerialSource.Scanned);
        check(context.Resolve("Demo","DEVICE-001",Generate)=="USER-001"&&generated==0,"A1 scanned beats device and auto");
        check(context.RequiresQaConfirmation,"A2 scanned requires QA confirmation");
        context.ConfirmQa("FAIL","offline operator");
        check(!context.RequiresQaConfirmation&&context.QaConfirmedAt!=null&&context.QaConfirmedBy=="offline operator","A2 confirmation stores actor and time");
        context.Receive("MANUAL-001","Demo",SerialSource.Manual);check(context.Resolve("Demo","DEVICE-001",Generate)=="MANUAL-001"&&context.RequiresQaConfirmation,"A1 manual precedence resets QA");
        context.Clear();check(context.Resolve("Demo","DEVICE-001",Generate)=="DEVICE-001"&&context.Source==SerialSource.Device,"A1 device beats generated identity");
        context.Clear();check(context.Resolve("Demo",null,Generate)=="AUTO-001"&&context.Source==SerialSource.Auto&&!context.RequiresQaConfirmation&&context.QaVerdict=="NONE","A1/A2 blank auto skips popup");
        foreach(string bad in new[]{"",new string('x',65),"abc\tdef","ab\u200Bcd","ab\u2028cd"})
        {bool rejected=false;try{ProductMeasurementContext.ValidateSerial(bad);}catch(ArgumentException){rejected=true;}check(rejected,"A1 rejects invalid serial length/printability");}
        check(ProductMeasurementContext.ValidateSerial("  ẮC-001  ")=="ẮC-001","A1 Unicode printable trim");
        foreach(var (app,qa,label) in new[]{("PASS","PASS","KHỚP"),("FAIL","FAIL","KHỚP"),("PASS","FAIL","LỆCH · App PASS / QA FAIL"),("FAIL","PASS","LỆCH · App FAIL / QA PASS"),("PASS","NONE","CHƯA CÓ QA")})
            check(QaComparison.Compare(app,qa).Text==label,"A2 pure comparison "+app+"/"+qa);

        var official=JsonSerializer.Deserialize<ModelConfig>("""{"model":"Demo","testItems":{"InOut":{"Devices":{"Input":{"OUT1":"synthetic"},"Output":{"IN1":"synthetic"}},"Tests":[{"id":"IO001","name":"offline","Playback Out":"OUT1","Recording In":"IN1","freq_range":[20,20000]}]}}}""")!;
        string officialJson=JsonSerializer.Serialize(official);
        var shell=(MainWindow)Activator.CreateInstance(typeof(MainWindow),Instance|BindingFlags.Public,null,[false],null)!;
        var route=new AudioRouting(); using var engine=new AudioEngine();var runner=new TestRunner(engine);
        Set(route,"_audioEngine",engine);Set(route,"_testRunner",runner);Set(shell,"_audioRoutingView",route);
        Set(shell,"_checkingConfig",new CheckingConfig{models=[official]});
        var previousMain=Application.Current.MainWindow;Application.Current.MainWindow=shell;
        Property(shell,"ApplySessionModelOverride",(Action<InOutConfig>)(io=>{Set(route,"_activeInOutConfig",io);Set(route,"_appliedTestConfig",io.Tests[0]);}));
        Ui<ComboBox>(shell,"ComboModels").Items.Add("Demo");Ui<ComboBox>(shell,"ComboModels").SelectedItem="Demo";
        try
        {
            int prompts=0;bool answer=false;
            Property(shell,"QaConfirmationPresenter",(Func<PreviousQaWindow,bool?>)(dialog=>{prompts++;return answer;}));
            Ui<TextBox>(shell,"TxtSerialNumber").Text=" REAL-001 ";Await((Task)Call(shell,"AcceptEnteredSerialAsync",SerialSource.Manual)!);
            check(prompts==0&&shell.IsEnteredSerialReady&&Ui<TextBlock>(shell,"TxtSerialStatus").Text=="Offline · chưa tra được QA","A8 AcceptEnteredSerialAsync validates serial without opening popup");
            Call(route,"BtnStartAutoTest_Click",route,new RoutedEventArgs());
            check(prompts==1&&!Get<bool>(route,"_routingWorkflowActive")&&Ui<Button>(route,"BtnStartAutoTest").ToolTip.ToString()!.Contains("kết quả QA"),"A8 Start button opens QA popup and stops when canceled");
            answer=true;Await((Task)Call(shell,"AcceptEnteredSerialAsync",SerialSource.Scanned)!);
            check(prompts==1&&shell.IsEnteredSerialReady&&shell.MeasurementContext.Source==SerialSource.Scanned,"A8 scanned accept trims serial without opening popup");
            answer=true;check(AwaitResult((Task<bool>)Call(route,"ConfirmAutoTestQaBeforeStartAsync")!)&&prompts==2&&!shell.MeasurementContext.RequiresQaConfirmation,"A8 ConfirmAutoTestQaBeforeStartAsync confirms QA on Start");
            check((string)Call(shell,"ResolveMeasurementSerial","Demo","DEVICE-002")! == "REAL-001","A1 actual start resolver preserves scanned over device serial");
            foreach(var (key,verdict) in new[]{(Key.P,"PASS"),(Key.F,"FAIL"),(Key.N,"NONE")})
            {
                var keyboardPopup=new PreviousQaWindow("keyboard","Demo"){WindowStartupLocation=WindowStartupLocation.Manual,Left=-30000,Top=-30000,ShowActivated=false};
                keyboardPopup.Loaded+=(_,_)=>keyboardPopup.Dispatcher.BeginInvoke(new Action(()=>
                {
                    foreach(var pressed in new[]{key,Key.Enter})keyboardPopup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(keyboardPopup),Environment.TickCount,pressed){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                }));
                check(keyboardPopup.ShowDialog()==true&&keyboardPopup.Verdict==verdict,"A2 real popup keyboard "+key+" then Enter");
            }
            var apiKey=typeof(ServerEngine).GetProperty("ApiKey")!;var staffId=typeof(ServerEngine).GetProperty("StaffID")!;
            object? oldKey=apiKey.GetValue(null),oldStaff=staffId.GetValue(null);
            try
            {
                apiKey.SetValue(null,"synthetic-offline-key");staffId.SetValue(null,"synthetic-staff");
                Property(shell,"SerialLookup",(Func<string,string?,Task<ProductInfo?>>)((serial,model)=>Task.FromResult<ProductInfo?>(new(){SerialNumber=serial,Model=model,QaStatus="FAIL"})));
                string? suggestion=null;Property(shell,"QaConfirmationPresenter",(Func<PreviousQaWindow,bool?>)(dialog=>{suggestion=dialog.Verdict;return true;}));
                Ui<TextBox>(shell,"TxtSerialNumber").Text="SERVER-EXISTS";Await((Task)Call(shell,"AcceptEnteredSerialAsync",SerialSource.Manual)!);
                check(suggestion==null&&Ui<TextBlock>(shell,"TxtSerialStatus").Text=="Đã có trên server · QA FAIL","A8 existing product lookup updates status without opening popup");
                check(AwaitResult((Task<bool>)Call(route,"ConfirmAutoTestQaBeforeStartAsync")!)&&suggestion=="FAIL","A8 Start popup preselects cached server QA FAIL");
                Property(shell,"SerialLookup",(Func<string,string?,Task<ProductInfo?>>)((_,_)=>Task.FromResult<ProductInfo?>(null)));
                Ui<TextBox>(shell,"TxtSerialNumber").Text="SERVER-NEW";Await((Task)Call(shell,"AcceptEnteredSerialAsync",SerialSource.Manual)!);
                check(Ui<TextBlock>(shell,"TxtSerialStatus").Text=="Serial mới · chưa có QA","A8 fake online missing serial label");
                Property(shell,"SerialLookup",(Func<string,string?,Task<ProductInfo?>>)((_,_)=>new TaskCompletionSource<ProductInfo?>().Task));
                Ui<TextBox>(shell,"TxtSerialNumber").Text="TIMEOUT";bool pumped=false;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};timer.Tick+=(_,_)=>{pumped=true;timer.Stop();};timer.Start();
                Await((Task)Call(shell,"AcceptEnteredSerialAsync",SerialSource.Manual)!);
                check(pumped&&shell.IsEnteredSerialReady&&Ui<TextBlock>(shell,"TxtSerialStatus").Text.StartsWith("Offline"),"A1 real timeout keeps dispatcher responsive and allows offline serial");
            }
            finally{apiKey.SetValue(null,oldKey);staffId.SetValue(null,oldStaff);}
            shell.MeasurementContext.ConfirmQa("FAIL","synthetic staff");Call(route,"FreezeMeasurementIdentity");
            Call(route,"CompleteProductVerdict",true);
            Ui<TextBlock>(route,"LblVerdict").Text="ĐẠT";
            check(Ui<TextBlock>(route,"TxtQaComparison").Text=="LỆCH · App PASS / QA FAIL","A2 real verdict call site displays mismatch");
            Render(Ui<Border>(route,"BorderVerdict"),Path.Combine(output,"qa-mismatch.png"),800,190);
            shell.MeasurementContext.ConfirmQa("PASS","synthetic staff");Call(route,"FreezeMeasurementIdentity");Call(route,"CompleteProductVerdict",true);
            Render(Ui<Border>(route,"BorderVerdict"),Path.Combine(output,"qa-match.png"),800,190);
            Call(shell,"CompleteMeasurementIdentity");int before=prompts;
            string auto=(string)Call(shell,"ResolveMeasurementSerial","Demo",null)!;
            check(prompts==before&&shell.MeasurementContext.Source==SerialSource.Auto&&shell.IsEnteredSerialReady,"A2 real generated-identity call site bypasses popup");
            Call(shell,"CompleteMeasurementIdentity");check(Ui<TextBox>(shell,"TxtSerialNumber").Text==""&&shell.MeasurementContext.Serial=="","A1 product completion clears input and identity");
            var popup=new PreviousQaWindow("REAL-001","Demo","FAIL");
            check(popup.Verdict=="FAIL"&&Elements<Button>(popup).Count()==5,"A8 popup preselects server verdict with three QA choices plus Confirm/Cancel");
            Render((FrameworkElement)popup.Content,Path.Combine(output,"qa-popup.png"),700,340);popup.Close();
            CheckA8PopupAndVolume(shell,route,output,check);

            int oldSaves=0,oldUploads=0,editors=0;
            Property(shell,"ModelEditorPresenter",(Func<ModelConfigEditorWindow,bool?>)(editor=>{editors++;editor.Close();return false;}));
            Ui<Button>(shell,"BtnModelConfig").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            typeof(MainWindow).GetMethod("TryHandleModelShortcut",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[Key.M,ModifierKeys.Control|ModifierKeys.Shift,false,(Action<bool>)(create=>Call(shell,"OpenModelEditor",create))]);
            check(editors==2,"A3 gear and Ctrl+Shift+M reach same real editor handler");
            var editor=new ModelConfigEditorWindow(official);editor.SaveModel=_=>oldSaves++;editor.PublishModel=m=>{oldUploads++;return Task.FromResult(m);};
            editor.SaveTemporaryModel=m=>Call(shell,"ApplyTemporaryModel",m,"Demo");
            Elements<TextBox>(editor).Single(t=>t.ToolTip?.ToString()?.StartsWith("THD_1000Hz")==true).Text=".123";
            string dataDirectory=Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR")!;
            string FilesSnapshot()=>string.Join("|",Directory.GetFiles(dataDirectory,"*",SearchOption.AllDirectories).Order().Select(p=>p+":"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))));
            string beforeTemporary=FilesSnapshot();
            Call(editor,"SaveTemporary",false);
            check(oldSaves==0&&oldUploads==0&&sessions.IsTemporary("Demo"),"A5 real Lưu tạm never calls disk save or server publish");
            check(JsonSerializer.Serialize(official)==officialJson&&sessions.Resolve(official).testItems.InOut.Tests[0].Thd1000Hz==.123,"A5 detached override leaves official model bytes unchanged");
            check(FilesSnapshot()==beforeTemporary,"A5 real temporary apply leaves persisted session data byte-for-byte unchanged");
            check(!Elements<Button>(editor).Any(b=>b.Name=="PublishModel")&&Elements<Button>(editor).Any(b=>b.Content?.ToString()=="Gửi cấu hình thử"),"A5 station action uses the requested trial label");
            check(new SessionModelConfigurations().Resolve(official).testItems.InOut.Tests[0].Thd1000Hz==official.testItems.InOut.Tests[0].Thd1000Hz,"A5 fresh process store has no override");
            var probeStart=new System.Diagnostics.ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=output};
            foreach(string arg in new[]{Assembly.GetExecutingAssembly().Location,Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!,output,"--phase-a-session-store-probe"})probeStart.ArgumentList.Add(arg);
            using(var probe=System.Diagnostics.Process.Start(probeStart)!)
            {
                bool exited=probe.WaitForExit(10000);string result=exited?probe.StandardOutput.ReadToEnd():"TIMEOUT";
                File.WriteAllText(Path.Combine(output,"restart-probe.txt"),result);
                check(exited&&probe.ExitCode==0&&result.Contains("EMPTY_SESSION_STORE"),"A5 separate process starts without active RAM override; main GUI not launched");
            }
            var newer=ModelConfigEditorWindow.ParseModel(official.model,officialJson);newer.testItems.InOut.Tests[0].Thd1000Hz=.9;
            check(sessions.Resolve(newer).testItems.InOut.Tests[0].Thd1000Hz==.123,"A5 incoming official sync cannot overwrite temporary model");
            Ui<TextBox>(shell,"TxtSerialNumber").Text="";Render((FrameworkElement)shell.Content,Path.Combine(output,"header-serial-gear-trial.png"),1200,270);
            check(Ui<Border>(shell,"TrialConfigBanner").Visibility==Visibility.Visible&&Ui<TextBlock>(shell,"TxtTrialConfig").Text.Contains("DỮ LIỆU THỬ"),"A5 real MainWindow trial banner");
            editor.Close();
            CheckPersistence(output,route,check);
            var retainedTrialRun=new PinkRubBuzzRun(new(),"synthetic","offline",44100,new(false,"synthetic invalid capture"));
            int referencePrompts=0;Property(route,"TrialReferenceConfirmation",(Func<bool>)(()=>{referencePrompts++;return false;}));
            Call(route,"BtnSaveStandardReference_Click",route,new RoutedEventArgs());
            check(referencePrompts==1&&!Get<bool>(route,"_routingWorkflowActive"),"A5 real Tìm line chuẩn stops when trial confirmation canceled");
            Call(route,"CompleteProductVerdict",true);check(Ui<TextBlock>(route,"TxtTrialResult").Text==ResultPersistencePolicy.TrialLabel,"A5 trial result visibly labeled");
            Ui<TextBlock>(route,"LblVerdict").Text="ĐẠT";
            Render(Ui<Border>(route,"BorderVerdict"),Path.Combine(output,"trial-result.png"),800,190);
            Ui<Button>(shell,"BtnDiscardTrialConfig").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!sessions.IsTemporary("Demo")&&JsonSerializer.Serialize(sessions.Resolve(official))==officialJson,"A5 real discard restores exact original model");
            check(Get<InOutConfig>(route,"_activeInOutConfig").Tests[0].Thd1000Hz==official.testItems.InOut.Tests[0].Thd1000Hz&&Ui<Border>(shell,"TrialConfigBanner").Visibility==Visibility.Collapsed,"A5 discard reapplies official route and hides banner");
            string retainedFolder=PinkRubBuzzStandard.Export(retainedTrialRun,Path.Combine(output,"retained-trial"));
            check(retainedTrialRun.PersistenceAllowed&&Directory.Exists(retainedFolder),"A5 retained trial run remains exportable after discarding override");
            CheckPercent(official,output,check);
            Environment.SetEnvironmentVariable("SONCA_CONFIG_ADMIN","1");var adminEditor=new ModelConfigEditorWindow(official);
            check(Elements<Button>(adminEditor).Any(b=>b.Name=="PublishModel"&&b.Content?.ToString()=="Lưu & đồng bộ (admin)"),"A5 legacy save exposed only in admin mode");adminEditor.Close();
        }
        finally{sessions.Discard("Demo");sessions.SelectedModel="";Application.Current.MainWindow=previousMain;shell.Close();Environment.SetEnvironmentVariable("SONCA_CONFIG_ADMIN",admin);Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR",previousData);SynchronizationContext.SetSynchronizationContext(previousContext);}
    }
    private static void CheckPersistence(string output, AudioRouting route, Action<bool,string> check)
    {
        check(ResultPersistencePolicy.CanPersist&&ResultPersistencePolicy.IsTrial,"Trial measurements can persist with an explicit trial destination");
        var routeConfig=new InOutConfig { Devices=new() { Input=new() { ["ON"]="active out",["OFF"]="disabled out",["UNUSED"]="unused out" },
            Output=new() { ["ON"]="active in",["OFF"]="disabled in" } }, Tests=[new() { id="IO001",name="Enabled test",PlaybackOut="ON",RecordingIn="ON",Enable=true },new() { id="IO002",name="Disabled test",PlaybackOut="OFF",RecordingIn="OFF",Enable=false }] };
        var requirements=AudioRouting.EnabledModelDeviceRequirements(routeConfig);
        check(requirements.Input.Keys.SequenceEqual(["ON"])&&requirements.Output.Keys.SequenceEqual(["ON"]),"Connection popup and device monitor require only enabled-test routes");
        routeConfig.Tests[0].Enable=false;routeConfig.Tests[1].Enable=true;
        requirements=AudioRouting.EnabledModelDeviceRequirements(routeConfig);
        check(requirements.Input.Keys.SequenceEqual(["OFF"])&&requirements.Output.Keys.SequenceEqual(["OFF"]),"Changing trial switches refreshes required devices without stale routes");
        var shell=(MainWindow)Application.Current.MainWindow;var popupText=new TextBlock();
        Set(shell,"_pendingDeviceConfig",routeConfig);Set(shell,"_deviceConnectionStatus",popupText);Call(shell,"UpdateDeviceConnectionStatus","missing active route");
        check(popupText.Text.Contains("missing active route")&&!popupText.Text.Contains("Enabled test")&&popupText.Text.Split((char)10).Length<=3,"Connection popup is short (owner 2026-11-18): only what is missing, no test list");
        Set(shell,"_pendingDeviceConfig",null);Set(shell,"_deviceConnectionStatus",null);
        shell.MeasurementContext.Receive("APP-SERIAL-001","Demo",SerialSource.Auto);
        string referenceSerial=(string)Call(route,"ResolveC360RunSerial","Demo",false,true,null)!;
        check(referenceSerial=="APP-SERIAL-001"&&Ui<TextBlock>(route,"TxtProductSerial").Text==referenceSerial
            &&Ui<TextBox>(shell,"TxtSerialNumber").Text==referenceSerial,"Trial reference preserves the exact ID shown in both app ID fields");
        string autoSerial=(string)Call(route,"ResolveC360RunSerial","Demo",true,false,null)!;
        check(autoSerial==referenceSerial&&Get<MeasurementIdentity>(route,"_measurementIdentity").Serial==referenceSerial,"Reference, next Auto Test and frozen upload identity share the same app ID");
        string trialReference=(string)Call(route,"GetStandardsDirectory")!;
        check(trialReference.Contains("trial standards")&&Directory.Exists(trialReference),"Trial reference has a separate writable directory");
        File.WriteAllText(Path.Combine(trialReference,"trial-sentinel.txt"),"trial");
        var config=Get<InOutConfig>(route,"_activeInOutConfig");double? old=config.Tests[0].PlaybackLevelDbfs;
        config.Tests[0].PlaybackLevelDbfs=-37;
        string changedReference=(string)Call(route,"GetStandardsDirectory")!;
        check(changedReference!=trialReference&&!File.Exists(Path.Combine(changedReference,"trial-sentinel.txt")),"Changing trial configuration requires its own reference before Auto Test");
        config.Tests[0].PlaybackLevelDbfs=old;
        var identity=new ProductMeasurementContext();identity.Receive("TRIAL-001","Demo",SerialSource.Auto);
        var frozen=identity.Snapshot();
        check(frozen.IsTrial&&frozen.TrialConfigJson!=null,"Measurement identity freezes trial mode and configuration snapshot");
        string root=LocalQaStorage.Root;
        check(Path.GetFileName(root)=="test data","Trial QA root is separate from production fail data");
        string folder=Path.Combine(root,"offline-trial-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string graph=Path.Combine(folder,"FRA-fixture.png");File.WriteAllBytes(graph,[137,80,78,71]);
        var envelope=new AudioQaConfiguration(null,"TRIAL-001",IsTrial:true,SpeakerModel:"Demo",ConfigSnapshot:frozen.TrialConfigJson);
        string? pending=LocalQaStorage.SavePending(new("fake","staff","id",true,[new("fake","PASS","")],[Path.GetFileName(graph)],true,"trial",DateTimeOffset.UtcNow,Configuration:envelope),[graph]);
        check(pending!=null&&LocalQaStorage.ReadPending(root,pending)?.Configuration?.IsTrial==true,"Durable retry envelope retains trial destination");
        using(var csv=ResultPersistencePolicy.CreateText(Path.Combine(folder,"run.csv")))csv.WriteLine("trial");
        check(File.Exists(Path.Combine(folder,"run.csv")),"Trial CSV is saved");
        using(ResultPersistencePolicy.BeginMeasurement())
        {
            SessionModelConfigurations.Current.SelectedModel="official";
            check(ResultPersistencePolicy.IsTrial&&LocalQaStorage.Root==root,"In-flight trial remains isolated after model selection changes");
            check(frozen.IsTrial&&frozen.TrialConfigJson!=null,"Frozen trial identity survives model selection changes");
            SessionModelConfigurations.Current.SelectedModel="Demo";
        }
        using(ResultPersistencePolicy.BeginTrial(false))
            check((string)Call(route,"GetStandardsDirectory")! != trialReference,"Production cannot read the trial standard directory");
        using(ResultPersistencePolicy.BeginResult(false))
        {
            ResultPersistencePolicy.WriteAllText(Path.Combine(folder,"blocked.txt"),"blocked");
            check(!File.Exists(Path.Combine(folder,"blocked.txt")),"Explicit blocked-result scopes still prevent writes");
        }
    }
    private static void CheckPercent(ModelConfig official,string output,Action<bool,string> check)
    {
        var editor=new ModelConfigEditorWindow(official);
        var tabs=Elements<TabControl>(editor).ToArray();var sections=tabs.First(t=>t.Items.OfType<TabItem>().Any(i=>i.Header?.ToString()=="Biên & khoảng xét hõm"));
        sections.SelectedItem=sections.Items.OfType<TabItem>().Single(i=>i.Header?.ToString()=="Biên & khoảng xét hõm");
        var controls=Elements<CheckBox>(editor).Where(c=>c.Name=="LineOutliers").ToArray();
        check(controls.Length==1&&controls[0].Content.ToString()!.Contains("toàn dải"),"A4 exactly one whole-range quota in dip/limits area");
        var test=Get<ModelConfig>(editor,"_draft").testItems.InOut.Tests[0];
        var curve=Enumerable.Range(0,20).ToDictionary(i=>30d*Math.Pow(600,i/19d),_=>0d);
        editor.ResponsePreviewProvider=_=>new(curve,curve,new Dictionary<double,FrequencyLimitPoint>(),3,"Dữ liệu tổng hợp offline");
        controls[0].IsChecked=true;
        check(((ModelConfig)Call(editor,"ReadEditorModel")!).testItems.InOut.Tests[0].EffectiveLineOutlierPercent==10,"A4 enabled quota retains 10 percent default");
        var percent=Elements<TextBox>(editor).Single(t=>t.ToolTip?.ToString()=="Tỷ lệ số điểm được vượt biên (%)");percent.Text="25";
        var changed=(ModelConfig)Call(editor,"ReadEditorModel")!;
        check(changed.testItems.InOut.Tests[0].AllowedLineOutlierPercent==25,"A4 actual percentage input binds correct model property");
        check(Elements<TextBlock>(editor).Any(t=>t.Name=="LineOutlierCount"&&t.Text.Contains("20 điểm xét · cho phép tối đa 5 điểm")),"A4 preview shows N and max K using actual assessment");
        foreach(string invalid in new[]{"-1","101","NaN"}){percent.Text=invalid;bool rejected=false;try{Call(editor,"ReadEditorModel");}catch(TargetInvocationException){rejected=true;}check(rejected,"A4 real editor rejects "+invalid);}
        percent.Text="25";Call(editor,"ReadEditorModel");
        var content=(FrameworkElement)editor.Content;Render(content,Path.Combine(output,"editor-limits-dips-percent.png"),1120,1050);
        foreach(double delta in new[]{0d,2d,4d})
        {
            var measured=curve.ToDictionary(p=>p.Key,_=>delta);
            var before=new ResponseLimitPolicy(ResponseLimitSettings.Capture(test,3),curve,null).Assess(measured,test.EffectiveLineOutlierPercent);
            var round=ModelConfigEditorWindow.ParseModel(changed.model,JsonSerializer.Serialize(changed)).testItems.InOut.Tests[0];
            var after=new ResponseLimitPolicy(ResponseLimitSettings.Capture(round,3),curve,null).Assess(measured,round.EffectiveLineOutlierPercent);
            check(before.Passed==after.Passed&&before.Total==after.Total&&before.Outside==after.Outside,"A4 same-config PASS/FAIL regression delta="+delta);
        }
        editor.Close();
    }

    private sealed class MockVolumeBench : IC360ElectronicBench
    {
        public string DeviceIdentity => "BLE-MOCK-C360";
        public Dictionary<ushort, ushort> State { get; } = new()
        {
            [SoncaBleMixerProfile.MasterVolume] = 8192,
            [SoncaBleMixerProfile.MusicVolume] = 3968,
            [SoncaBleMixerProfile.MicVolume] = 4096,
            [SoncaBleMixerProfile.EchoVolume] = 4096,
            [SoncaBleMixerProfile.ReverbVolume] = 4096,
            [SoncaBleMixerProfile.LiveVolume] = 4096,
            [SoncaBleMixerProfile.InputSource] = 2,
            [SoncaBleMixerProfile.InputCapabilities] = 0x0FFF,
            [SoncaBleMixerProfile.MicMute] = 0,
            [SoncaBleMixerProfile.MusicMute] = 0,
            [SoncaBleMixerProfile.MasterMute] = 0,
            [SoncaBleMixerProfile.LiveMute] = 0,
            [SoncaBleMixerProfile.EchoEnable] = 1,
            [SoncaBleMixerProfile.ReverbEnable] = 1
        };
        public List<(ushort Register, ushort Value)> Writes { get; } = new();
        public Dictionary<C360ElectronicStage, ushort> StageMicVolumes { get; } = new();
        public bool IgnoreWrites { get; set; }
        public C360ElectronicStage? CancelAtStage { get; set; }
        public C360ElectronicStage? ThrowAtStage { get; set; }
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyDictionary<ushort, ushort>>(new Dictionary<ushort, ushort>(State));
        public Task WriteAsync(ushort register, ushort value, CancellationToken token)
        {
            Writes.Add((register, value));
            if (!IgnoreWrites) State[register] = value;
            return Task.CompletedTask;
        }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        {
            StageMicVolumes[stage] = State[SoncaBleMixerProfile.MicVolume];
            if (CancelAtStage == stage) throw new OperationCanceledException("cancel stage");
            if (ThrowAtStage == stage) throw new IOException("error stage");
            return Task.FromResult(new C360ElectronicResult(stage, "PASS", "ok"));
        }
        public void StopAudio() { }
    }

    private static void CheckA8PopupAndVolume(MainWindow shell, AudioRouting route, string output, Action<bool, string> check)
    {
        // 1. Unaccepted serial text in TxtSerialNumber is accepted automatically when clicking Start
        shell.MeasurementContext.Clear();
        Ui<TextBox>(shell, "TxtSerialNumber").Text = " TYPED-001 ";
        string? seenTitle = null, seenStatus = null;
        Property(shell, "QaConfirmationPresenter", (Func<PreviousQaWindow, bool?>)(dlg =>
        {
            seenTitle = dlg.Title;
            seenStatus = dlg.ServerQaStatusText;
            dlg.NoteTextBox.Text = "  Ghi chú gõ trực tiếp  ";
            return true;
        }));
        check(AwaitResult((Task<bool>)Call(route, "ConfirmAutoTestQaBeforeStartAsync")!)
            && shell.MeasurementContext.Serial == "TYPED-001"
            && seenTitle!.Contains("TYPED-001")
            && shell.MeasurementContext.OperatorNote == "Ghi chú gõ trực tiếp", "A8 Start accepts unaccepted serial in TxtSerialNumber and opens QA popup");

        // 2. Blank serial opens QA popup with 'ID tự tạo khi đo' and preserves QA + OperatorNote across ResolveMeasurementSerial
        shell.MeasurementContext.Clear();
        Ui<TextBox>(shell, "TxtSerialNumber").Text = "";
        string? blankVerdict = null;
        Property(shell, "QaConfirmationPresenter", (Func<PreviousQaWindow, bool?>)(dlg =>
        {
            seenTitle = dlg.Title;
            seenStatus = dlg.ServerQaStatusText;
            blankVerdict = dlg.Verdict;
            Elements<Button>(dlg).Single(b => b.Name == "QaFAIL").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dlg.NoteTextBox.Text = "Loa rè nhẹ ở 120Hz";
            return true;
        }));
        check(AwaitResult((Task<bool>)Call(route, "ConfirmAutoTestQaBeforeStartAsync")!)
            && seenTitle!.Contains("ID tự tạo khi đo")
            && seenStatus!.Contains("Chưa nhập serial · không tra được QA")
            && blankVerdict == "NONE", "A8 blank serial opens QA popup with auto-ID title and NONE preselected");
        string resolvedAuto = (string)Call(shell, "ResolveMeasurementSerial", "Demo", null)!;
        var snapAfterAuto = shell.MeasurementContext.Snapshot();
        check(!string.IsNullOrWhiteSpace(resolvedAuto)
            && snapAfterAuto.QaVerdict == "FAIL"
            && snapAfterAuto.OperatorNote == "Loa rè nhẹ ở 120Hz", "A8 blank serial preserves confirmed QA verdict and OperatorNote after auto-ID resolution");

        // 3. Clicking QaPASS/QaFAIL/QaNONE does not close PreviousQaWindow; ConfirmQaStart closes with true; CancelQaStart closes with false
        var choicePopup = new PreviousQaWindow("SN-BTN", "Demo", "NONE") { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false };
        choicePopup.Show();
        try
        {
            Elements<Button>(choicePopup).Single(b => b.Name == "QaPASS").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(choicePopup.IsVisible && choicePopup.ConfirmedResult == null && choicePopup.Verdict == "PASS", "A8 clicking QaPASS selects PASS without closing dialog");
            Elements<Button>(choicePopup).Single(b => b.Name == "QaFAIL").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(choicePopup.IsVisible && choicePopup.ConfirmedResult == null && choicePopup.Verdict == "FAIL", "A8 clicking QaFAIL selects FAIL without closing dialog");
            Elements<Button>(choicePopup).Single(b => b.Name == "QaNONE").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(choicePopup.IsVisible && choicePopup.ConfirmedResult == null && choicePopup.Verdict == "NONE", "A8 clicking QaNONE selects NONE without closing dialog");
            Elements<Button>(choicePopup).Single(b => b.Name == "ConfirmQaStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(choicePopup.ConfirmedResult == true, "A8 clicking ConfirmQaStart confirms dialog");
        }
        finally { choicePopup.Close(); }

        // 4. Focus-aware keyboard shortcuts in PreviousQaWindow & 4000-char normalization
        var focusPopup = new PreviousQaWindow("SN-FOCUS", "Demo", "PASS") { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false };
        focusPopup.Show();
        try
        {
            focusPopup.FocusOperatorNote();
            var src = PresentationSource.FromVisual(focusPopup);
            foreach (var k in new[] { Key.F, Key.N, Key.P })
                focusPopup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, Environment.TickCount, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check(focusPopup.Verdict == "PASS" && focusPopup.ConfirmedResult == null, "A8 typing P/F/N while OperatorNote is focused does not change Verdict");
            focusPopup.NoteTextBox.Text = "Dòng 1";
            focusPopup.NoteTextBox.CaretIndex = focusPopup.NoteTextBox.Text.Length;
            focusPopup.InsertNewLineInNote();
            check(focusPopup.ConfirmedResult == null && focusPopup.NoteTextBox.Text.Contains("Dòng 1\n"), "A8 Shift+Enter in OperatorNote inserts newline without closing dialog");
            focusPopup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check(focusPopup.ConfirmedResult == true, "A8 Enter in OperatorNote confirms and closes dialog");
        }
        finally { focusPopup.Close(); }

        var escPopup = new PreviousQaWindow("SN-ESC", "Demo", "PASS") { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false };
        escPopup.Show();
        try
        {
            escPopup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escPopup), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check(escPopup.ConfirmedResult == false, "A8 Esc cancels PreviousQaWindow");
        }
        finally { escPopup.Close(); }

        check(ProductMeasurementContext.NormalizeOperatorNote(new string('N', 4500))!.Length == 4000, "A8 OperatorNote longer than 4000 chars is truncated to 4000");
        check(ProductMeasurementContext.NormalizeOperatorNote("   \r\n\t  ") == null, "A8 whitespace-only OperatorNote normalizes to null");

        // 7. Auto-set BLE volume (MASTER/MUSIC/MIC) without opening popup when write succeeds
        var c360Policy = ModelTestVolume.C360Defaults();
        var bench = new MockVolumeBench();
        var restoreState = new C360BleRestoreState();
        int preflightCalls = 0;
        route.VolumePreflightPresenter = _ => { preflightCalls++; return Task.FromResult<bool?>(false); };
        try
        {
            var afterVol = AwaitResult(route.ConfirmModelVolumeAsync(c360Policy, () => bench.ReadAsync(CancellationToken.None), new Dictionary<ushort, ushort>(bench.State), CancellationToken.None,
                (reg, val, ct) => bench.WriteAsync(reg, val, ct),
                (orig, regs) => restoreState.RecordOriginalVolume(orig, regs),
                ct => restoreState.RestoreOriginalVolumeAsync(bench, ct),
                settleDelayMs: 0));
            check(c360Policy.Mismatches(afterVol).Length == 0 && preflightCalls == 0, "A8 auto-set BLE volume succeeds without opening ModelVolumePreflightWindow");
            check(bench.Writes.Count == 1 && bench.Writes[0] == (SoncaBleMixerProfile.MusicVolume, (ushort)4096), "A8 auto-set writes only mismatched register 0x001E=4096 (does not write 0x001D or 0x001F)");
            check(bench.State[SoncaBleMixerProfile.MusicVolume] == 4096 && restoreState.OriginalVolumeState[SoncaBleMixerProfile.MusicVolume] == 3968, "A8 auto-set updates MUSIC to 4096 and records original 3968");

            // 8. Fallback popup when BLE ignores initial write; clicking ApplyBleVolume succeeds and closes
            var stubbornBench = new MockVolumeBench { IgnoreWrites = true };
            var stubbornRestore = new C360BleRestoreState();
            route.VolumePreflightPresenter = async dlg =>
            {
                preflightCalls++;
                stubbornBench.IgnoreWrites = false;
                var applyBtn = Elements<Button>(dlg).Single(b => b.Name == "ApplyBleVolume");
                applyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Yield(); return dlg.ConfirmedResult;
            };
            var fallbackAfter = AwaitResult(route.ConfirmModelVolumeAsync(c360Policy, () => stubbornBench.ReadAsync(CancellationToken.None), new Dictionary<ushort, ushort>(stubbornBench.State), CancellationToken.None,
                (reg, val, ct) => stubbornBench.WriteAsync(reg, val, ct),
                (orig, regs) => stubbornRestore.RecordOriginalVolume(orig, regs),
                ct => stubbornRestore.RestoreOriginalVolumeAsync(stubbornBench, ct),
                settleDelayMs: 0));
            check(c360Policy.Mismatches(fallbackAfter).Length == 0 && preflightCalls == 1 && stubbornBench.State[SoncaBleMixerProfile.MusicVolume] == 4096, "A8 fallback ModelVolumePreflightWindow opens on write mismatch and ApplyBleVolume button applies 4096");
        }
        finally { route.VolumePreflightPresenter = null; }

        // 9. C360 suite stage MicVolume checks (4096 on Mic stages, 0 on LineIn/UsbA) and restore to original 3968 on completion, cancel, and error
        var suiteSetup = new C360ElectronicSetup("analog-out", "usb-out", "line-in");
        foreach (string mode in new[] { "complete", "cancel", "error" })
        {
            var runBench = new MockVolumeBench();
            if (mode == "cancel") runBench.CancelAtStage = C360ElectronicStage.UsbA;
            if (mode == "error") runBench.ThrowAtStage = C360ElectronicStage.UsbA;
            var runRestore = new C360BleRestoreState();
            var postConfirm = AwaitResult(route.ConfirmModelVolumeAsync(c360Policy, () => runBench.ReadAsync(CancellationToken.None), new Dictionary<ushort, ushort>(runBench.State), CancellationToken.None,
                (reg, val, ct) => runBench.WriteAsync(reg, val, ct),
                (orig, regs) => runRestore.RecordOriginalVolume(orig, regs),
                ct => runRestore.RestoreOriginalVolumeAsync(runBench, ct),
                settleDelayMs: 0));
            runRestore.BaselineState = new Dictionary<ushort, ushort>(postConfirm);
            var suite = new C360ElectronicSuite();
            try
            {
                Await(suite.RunAsync(runBench, suiteSetup, null, CancellationToken.None, [C360ElectronicStage.MicEffects, C360ElectronicStage.LineIn, C360ElectronicStage.UsbA, C360ElectronicStage.MicFrequencyShift, C360ElectronicStage.MicDry], postConfirm, reg => runRestore.DirtyRegisters.Add(reg)));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            finally
            {
                Await(runRestore.RestoreOriginalVolumeAsync(runBench, CancellationToken.None));
            }
            if (mode == "complete")
            {
                check(runBench.StageMicVolumes[C360ElectronicStage.MicEffects] == 4096
                    && runBench.StageMicVolumes[C360ElectronicStage.MicFrequencyShift] == 4096
                    && runBench.StageMicVolumes[C360ElectronicStage.MicDry] == 4096
                    && runBench.StageMicVolumes[C360ElectronicStage.LineIn] == 0
                    && runBench.StageMicVolumes[C360ElectronicStage.UsbA] == 0, "A8 C360 stages keep MicVolume=4096 on Mic stages and 0 on LineIn/UsbA");
            }
            check(runBench.State[SoncaBleMixerProfile.MusicVolume] == 3968
                && runBench.State[SoncaBleMixerProfile.MasterVolume] == 8192
                && runBench.State[SoncaBleMixerProfile.MicVolume] == 4096, $"A8 C360 restores original BLE volume (MUSIC=3968) after {mode}");
        }

        // 10. Resume preserves first run's original volume (3968) even if speaker is at 4096 when resuming
        var resumeBench = new MockVolumeBench { CancelAtStage = C360ElectronicStage.UsbA };
        var sharedRestore = new C360BleRestoreState();
        var resumePost1 = AwaitResult(route.ConfirmModelVolumeAsync(c360Policy, () => resumeBench.ReadAsync(CancellationToken.None), new Dictionary<ushort, ushort>(resumeBench.State), CancellationToken.None,
            (reg, val, ct) => resumeBench.WriteAsync(reg, val, ct),
            (orig, regs) => sharedRestore.RecordOriginalVolume(orig, regs),
            ct => sharedRestore.RestoreOriginalVolumeAsync(resumeBench, ct),
            settleDelayMs: 0));
        sharedRestore.BaselineState = new Dictionary<ushort, ushort>(resumePost1);
        try
        {
            Await(new C360ElectronicSuite().RunAsync(resumeBench, suiteSetup, null, CancellationToken.None, [C360ElectronicStage.MicEffects, C360ElectronicStage.UsbA], resumePost1, reg => sharedRestore.DirtyRegisters.Add(reg)));
        }
        catch (OperationCanceledException) { }
        // Simulate speaker staying at 4096 when user clicks Resume
        resumeBench.State[SoncaBleMixerProfile.MusicVolume] = 4096;
        resumeBench.CancelAtStage = null;
        var resumePost2 = AwaitResult(route.ConfirmModelVolumeAsync(c360Policy, () => resumeBench.ReadAsync(CancellationToken.None), new Dictionary<ushort, ushort>(resumeBench.State), CancellationToken.None,
            (reg, val, ct) => resumeBench.WriteAsync(reg, val, ct),
            (orig, regs) => sharedRestore.RecordOriginalVolume(orig, regs),
            ct => sharedRestore.RestoreOriginalVolumeAsync(resumeBench, ct),
            settleDelayMs: 0));
        check(sharedRestore.OriginalVolumeState[SoncaBleMixerProfile.MusicVolume] == 3968, "A8 Resume preserves original pre-session MUSIC=3968 instead of overwriting with 4096");
        Await(new C360ElectronicSuite().RunAsync(resumeBench, suiteSetup, null, CancellationToken.None, [C360ElectronicStage.UsbA], resumePost2, reg => sharedRestore.DirtyRegisters.Add(reg)));
        Await(sharedRestore.RestoreOriginalVolumeAsync(resumeBench, CancellationToken.None));
        check(resumeBench.State[SoncaBleMixerProfile.MusicVolume] == 3968, "A8 Resume final restore returns MUSIC to 3968");

        // 11. Reference fingerprint invariance
        string c360RefKey = C360ModelDefinition.ReferenceConfiguration(C360ModelDefinition.Create().testItems.InOut);
        check(c360RefKey.Contains("\"BleMasterPercent\":100") && c360RefKey.Contains("\"BleMusicPercent\":50") && c360RefKey.Contains("\"BleMicPercent\":50"), "A8 C360 ReferenceConfiguration preserves exact volume configuration");
        shell.MeasurementContext.Clear();
        Ui<TextBox>(shell, "TxtSerialNumber").Text = "";
    }
}
