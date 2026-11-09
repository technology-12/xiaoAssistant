using System;
using System.IO;
using System.Linq;
using System.Threading;
using WorkAssistant.Tools;
class QuickProbe
{
    static int Main(string[] args)
    {
        try {
            WorkAssistant.Infrastructure.NativeRuntimeBootstrap.Configure();
            DocumentLimits.Begin(CancellationToken.None,"read_document_text");
            DocumentLimits.ConfigurePdfReading(Path.GetFullPath(args[1]),Console.WriteLine);
            var value=PdfText.Extract(Path.GetFullPath(args[0]),"probe.pdf",1);
            Console.WriteLine("RESULT chars="+value.TotalChars+" ocr="+value.OcrPages+" fallback="+value.Pages[0].OcrImageFallback);
            return 0;
        } catch(Exception e) { Console.WriteLine(e); return 1; }
    }
}
