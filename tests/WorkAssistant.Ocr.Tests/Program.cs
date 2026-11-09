using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Tools;
using WorkAssistant.Infrastructure;

// Compile against the actual candidate assemblies, in an isolated test copy.
static class OcrAcceptance
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    sealed class Deny : IApprovalProvider { public Task<bool> RequestAsync(ApprovalRequest r, CancellationToken t) { return XpTask.FromResult(false); } }
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            NativeRuntimeBootstrap.Configure();
            string nativeRoot=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,IntPtr.Size==8?"x64":"x86"));
            foreach(ProcessModule module in Process.GetCurrentProcess().Modules)
                if(module.ModuleName.Equals("vcruntime140.dll",StringComparison.OrdinalIgnoreCase) || module.ModuleName.Equals("msvcp140.dll",StringComparison.OrdinalIgnoreCase))
                    Check(Path.GetFullPath(module.FileName).StartsWith(nativeRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"VC module loaded from package: "+module.ModuleName);
            if(args.Length>0 && args[0]=="host-only")
            {
                string hostRoot=Path.GetFullPath(args[2]); Directory.CreateDirectory(hostRoot);
                Host(hostRoot,Path.GetFullPath(args[1]),false); Host(hostRoot,Path.GetFullPath(args[1]),true);
                Console.WriteLine("PASSED "+count+" host-only");return 0;
            }
            string source = Path.GetFullPath(args[0]), fixtures = Path.GetFullPath(args[1]);
            string root = Path.Combine(Path.GetFullPath(args[2]), "case-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            WorkspacePolicy.EnsureLayout(root);
            File.Copy(source, Path.Combine(root, "inputs", "scan.pdf"));
            foreach (string file in new[] { "text.pdf", "mixed.pdf", "blank.pdf", "broken.pdf" }) File.Copy(Path.Combine(fixtures, file), Path.Combine(root, "inputs", file));
            string cache = Path.Combine(root, "internal-cache");
            DocumentLimits.Begin(CancellationToken.None, "read_document_text");
            int progress = 0;
            DocumentLimits.ConfigurePdfReading(cache, delegate(string message) { progress++; Console.WriteLine("PROGRESS " + message); });
            var timer = Stopwatch.StartNew();
            var scan = PdfText.Extract(source, "scan.pdf");
            Check(scan.PageCount == 10 && scan.Pages.Count == 10, "real scanned PDF keeps all 10 pages");
            Check(scan.OcrPages == 10 && scan.Pages.All(p => p.OcrUsed), "each scanned page uses local OCR");
            Check(scan.TotalChars > 3000 && scan.Pages.All(p => p.Text.Length > 100), "all 10 pages contain recognized text");
            Check(scan.Pages.Select(p => p.PageNumber).SequenceEqual(Enumerable.Range(1,10)), "OCR retains page order");
            Check(System.Text.RegularExpressions.Regex.Replace(scan.Pages[0].Text,"\\s+","").Contains("高一语文试题"), "first-page recognized title matches visual source");
            Check(scan.Pages.All(p => p.OcrConfidence >= 0 && p.OcrConfidence <= 1), "OCR reports bounded confidence");
            Check(scan.OcrNotice.Contains("OCR") && scan.OcrNotice.Contains("核对"), "OCR uncertainty is visible");
            Check(progress >= 20, "per-page recognition progress emitted");
            File.WriteAllText(Path.Combine(root, "recognized-review.txt"), string.Join("\n\n", scan.Pages.Select(p => "[PAGE " + p.PageNumber + "]\n" + p.Text)), Encoding.UTF8);
            Console.WriteLine("REAL_SCAN chars=" + scan.TotalChars + " seconds=" + timer.Elapsed.TotalSeconds.ToString("F2") + " review=" + root);
            var stamps = Directory.GetFiles(cache,"*.json",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.GetLastWriteTimeUtc(p));
            Check(stamps.Count == 10, "task-local page caches persisted");
            timer.Restart(); var cached = PdfText.Extract(source,"scan.pdf");
            Check(cached.Pages.Select(p=>p.Text).SequenceEqual(scan.Pages.Select(p=>p.Text)), "cached text exactly matches first extraction");
            Check(stamps.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value), "repeated extraction does not rewrite completed pages");
            Console.WriteLine("CACHE seconds=" + timer.Elapsed.TotalSeconds.ToString("F2"));
            string damaged=stamps.Keys.First(p=>Path.GetFileName(p)=="page-1.json"); File.WriteAllText(damaged,"{broken-cache");
            var repaired=PdfText.Extract(source,"scan.pdf",1);
            Check(repaired.Pages[0].Text==scan.Pages[0].Text,"corrupt OCR cache is recomputed from original PDF");
            Check((string)JObject.Parse(File.ReadAllText(damaged))["text"]==scan.Pages[0].Text,"corrupt OCR cache is atomically repaired");
            var text = PdfText.Extract(Path.Combine(root,"inputs","text.pdf"),"text.pdf");
            Check(text.OcrPages == 0 && text.Pages[0].Text.Contains("TEXT-ONLY CONTROL"), "text PDF keeps native text fast path");
            var mixed = PdfText.Extract(Path.Combine(root,"inputs","mixed.pdf"),"mixed.pdf");
            Check(mixed.OcrPages == 1 && mixed.TotalChars > 300, "mixed text-header/image page is recognized");
            Check(mixed.Pages[0].Text.Contains("MIXED PAGE HEADER"),"mixed page retains original native text");
            Check(mixed.Pages[0].OcrImageFallback && mixed.OcrNotice.Contains("阅读顺序"),"image-layer fallback scope is explicit");
            var blank = PdfText.Extract(Path.Combine(root,"inputs","blank.pdf"),"blank.pdf");
            Check(blank.TotalChars == 0 && blank.OcrPages == 1, "blank page does not invent text");
            try { PdfText.Extract(Path.Combine(root,"inputs","broken.pdf"),"broken.pdf"); Check(false,"malformed PDF rejected"); }
            catch (ToolException e) { Check(e.Code==ErrorCodes.UnsupportedFormat,"malformed PDF reports format failure"); }
            var policy = new WorkspacePolicy(root); string id=Guid.NewGuid().ToString("N");
            var context = new ToolContext(id,policy,new InMemoryAggregationStore(),CancellationToken.None);
            var reader = new ReadDocumentTextTool(policy);
            var first = reader.ExecuteAsync(context,"{\"path\":\"inputs/scan.pdf\",\"max_lines\":1}",CancellationToken.None).GetAwaiter().GetResult();
            Check(first.Ok,"read_document_text accepts scanned PDF");
            var data = JObject.FromObject(first.Data);
            Check((bool)data["has_more"] && (int)data["next_line"]==2,"scanned PDF preserves pagination cursor");
            Check(((string)data["scope_notice"]).Contains("OCR"),"read tool carries OCR scope warning");
            var search = new SearchInputTextTool(policy).ExecuteAsync(context,"{\"paths\":[\"inputs/scan.pdf\"],\"query\":\"1\"}",CancellationToken.None).GetAwaiter().GetResult();
            Check(search.Ok,"search reuses recognized PDF text");
            var extract = new ExtractPdfTextTool(policy).ExecuteAsync(context,"{\"path\":\"inputs/scan.pdf\",\"max_pages\":1}",CancellationToken.None).GetAwaiter().GetResult();
            Check(extract.Ok && (bool)JObject.FromObject(extract.Data)["partial_document"],"extract tool reports partial page scope");
            Check(reader.Definition.TimeoutSeconds==600 && new ExtractPdfTextTool(policy).Definition.TimeoutSeconds==600,"reader and worker share OCR timeout");
            var cts = new CancellationTokenSource(); cts.Cancel(); DocumentLimits.Begin(cts.Token,"read_document_text");
            try { PdfText.Extract(source,"scan.pdf"); Check(false,"cancel honored"); }
            catch (OperationCanceledException) { Check(true,"canceled PDF processing cannot report success"); }
            DocumentLimits.Begin(CancellationToken.None,"read_document_text");
            var canceledTool = reader.ExecuteAsync(new ToolContext(id,policy,new InMemoryAggregationStore(),cts.Token),"{\"path\":\"inputs/scan.pdf\"}",cts.Token);
            try { canceledTool.GetAwaiter().GetResult(); Check(false,"tool cancellation honored"); }
            catch (OperationCanceledException) { Check(true,"tool propagates cancellation"); }
            Host(root,fixtures,false); Host(root,fixtures,true);
            Console.WriteLine("PASSED " + count + " bitness=" + (IntPtr.Size*8)); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
    static void Host(string root,string fixtures,bool cancel)
    {
        string path=Path.Combine(root,cancel?"host-cancel":"host-read"); WorkspacePolicy.EnsureLayout(path);
        File.Copy(Path.Combine(fixtures,"mixed.pdf"),Path.Combine(path,"inputs","mixed.pdf"));
        var store=new LocalTaskStore(Path.Combine(path,"state"));
        var task=new TaskRecord(Guid.NewGuid().ToString("N"),"Read scanned PDF locally",path);store.SaveTask(task);
        task.SnapshotRoot=Path.Combine(store.Root,task.TaskId,"snapshot");
        VerifiedFiles.CaptureInputs(path,task.SnapshotRoot,CancellationToken.None);
        string fileId=(string)JObject.Parse(File.ReadAllText(Path.Combine(task.SnapshotRoot,"manifest.json")))["files"][0]["file_id"];
        store.SaveTask(task);
        LocalToolResponse response=null; int progress=0; var cts=new CancellationTokenSource(); var timer=Stopwatch.StartNew();
        var result=new TaskHostClient().RunAsync(new HostRequest { Task=task, StoreRoot=store.Root, LocalToolOperation=true, Fake=true,
            LocalToolCall=new ToolCall("ocr-read","read_document_text",new JObject(new JProperty("path",fileId),new JProperty("max_lines",10)).ToString()) },new Deny(),delegate(TaskEvent e) {
                if(e.Kind==TaskEventKind.ToolProgress) { progress++; if(cancel) cts.Cancel(); }
            },cts.Token,onLocalToolResponse:delegate(LocalToolResponse r){response=r;}).GetAwaiter().GetResult();
        if(cancel)
        {
            Check(progress>0 && result.State==TaskState.Cancelled,"cancel during native OCR stops Host/Worker job");
            Check(timer.Elapsed.TotalSeconds<30,"native OCR cancellation is bounded");
        }
        else
        {
            Check(response!=null && response.Result!=null && response.Result.Ok,"real Host/Worker reads scanned mixed PDF: "+(response==null?result.ErrorMessage:response.Result==null?"no result":response.Result.Error==null?"ok":response.Result.Error.Message));
            Check(progress>=2,"worker OCR progress reaches parent client");
            Check(store.LoadEvents(task.TaskId).Any(e=>e.Kind==TaskEventKind.ToolProgress),"OCR progress persisted with task");
            Check(result.Artifacts.Count==0,"read-only OCR creates no user delivery artifacts");
        }
    }
}
