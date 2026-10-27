using System.IO;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Cabinette.App.Services.Ai;
using Cabinette.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace Cabinette.App.Tests;

/// <summary>A test that needs Office's own text extractor (OFFFILTX.DLL) for a type: skipped, with the reason, without it.</summary>
public sealed class OfficeFilterFactAttribute : FactAttribute
{
    public OfficeFilterFactAttribute(string extension)
    {
        var filter = IFilterRegistry.FindFilter(extension);
        var dll = filter is null ? null : IFilterRegistry.FindFilterDll(filter.Value);
        if (dll is null || !string.Equals(Path.GetFileName(dll), "OFFFILTX.DLL", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Office's text extractor (OFFFILTX.DLL) isn't registered for .{extension} files on this PC.";
        }
    }
}

/// <summary>
/// The extraction child's sandbox (decided 2026-10-24): it runs at Low integrity with CET shadow stacks off, so that
/// Office's filter (which loads the .NET Framework, which crashes under shadow stacks) works, while Cabinette.exe itself
/// stays CET compatible. Each test extracts a real document through the real child (always with the safety arguments,
/// see <see cref="SafeChild"/>) and measures that child from outside while it runs: its token's integrity level and
/// privileges, and its user shadow stack policy.
/// </summary>
public sealed class FileContentSandboxTests(ITestOutputHelper output) : IDisposable
{
    private readonly ContentFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Cabinette_exe_itself_stays_cet_compatible()
    {
        SafeChild.EnsureSameBuild();
        Assert.True(ProcessSecurity.IsCetCompatible(SafeChild.Exe), "Cabinette.exe lost its CETCOMPAT flag");
    }

    /// <summary>
    /// The child's token holds no privilege but SeChangeNotifyPrivilege (DISABLE_MAX_PRIVILEGE): none it could enable
    /// either, which a plain copy of Cabinette's token would still hold (disabled) and could switch on.
    /// </summary>
    [Fact]
    public async Task The_child_holds_no_privilege_but_change_notify()
    {
        var path = _folder.Text("page.html", "<html><body><p>Privileges</p></body></html>");
        var state = await MeasureExtractionAsync(path);

        output.WriteLine($"child privileges: {string.Join(", ", state.Privileges.Select(p => p.Enabled ? p.Name + " (enabled)" : p.Name))}");
        Assert.Equal(["SeChangeNotifyPrivilege"], state.Privileges.Select(p => p.Name));
        Assert.DoesNotContain(state.Privileges, p => p.Enabled && p.Name != "SeChangeNotifyPrivilege");
        Assert.Equal(ChildProcess.LowIntegrity, state.Integrity);
        Assert.False(state.UserShadowStacks, "the child runs with CET shadow stacks");
    }

    /// <summary>
    /// What Low integrity means for anything the child runs (here cmd.exe through the same launcher): it reads a file in
    /// the user's temp folder, but can't create one there. Only this test's own folder is written to, even were it broken.
    /// </summary>
    [Fact]
    public async Task A_child_reads_the_users_files_but_cannot_write_to_their_folders()
    {
        _folder.Text("note.txt", "readable");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var read = await SafeChildRun(new ChildCommand(cmd, ["/d", "/c", "type note.txt"], _folder.Path));
        Assert.Equal(0, read.ExitCode);
        Assert.Equal("readable", Encoding.UTF8.GetString(read.Output));

        var write = await SafeChildRun(new ChildCommand(cmd, ["/d", "/c", "echo written>written.txt"], _folder.Path));
        output.WriteLine($"write: exit code {write.ExitCode}, {write.Error.Trim()}");
        Assert.NotEqual(0, write.ExitCode);
        Assert.False(File.Exists(Path.Combine(_folder.Path, "written.txt")), "the child wrote to the user's folder");
    }

    [OfficeFilterFact("docx")]
    public async Task Office_extracts_docx_in_the_sandboxed_child()
    {
        var path = _folder.Bytes("hello.docx", TestDocuments.Docx("Hello Cabinette DOCX", "Second paragraph"));
        var text = await ExtractInSandboxAsync(path);
        Assert.Contains("Hello Cabinette DOCX", text);
        Assert.Contains("Second paragraph", text);
    }

    [OfficeFilterFact("xlsx")]
    public async Task Office_extracts_xlsx_in_the_sandboxed_child()
    {
        var path = _folder.Bytes("hello.xlsx", TestDocuments.Xlsx("Hello Cabinette XLSX", "Second cell"));
        var text = await ExtractInSandboxAsync(path);
        Assert.Contains("Hello Cabinette XLSX", text);
        Assert.Contains("Second cell", text);
    }

    [OfficeFilterFact("pptx")]
    public async Task Office_extracts_pptx_in_the_sandboxed_child()
    {
        var path = _folder.Bytes("hello.pptx", TestDocuments.Pptx("Hello Cabinette PPTX"));
        var text = await ExtractInSandboxAsync(path);
        Assert.Contains("Hello Cabinette PPTX", text);
    }

    [IFilterFact("pdf")]
    public async Task Pdf_extracts_in_the_sandboxed_child()
    {
        var path = _folder.Bytes("hello.pdf", TestDocuments.Pdf("Hello Cabinette PDF"));
        Assert.Contains("Hello Cabinette PDF", await ExtractInSandboxAsync(path));
    }

    [IFilterFact("rtf")]
    public async Task Rtf_extracts_in_the_sandboxed_child()
    {
        var path = _folder.Text("hello.rtf", @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs24 Hello Cabinette RTF\par Second line\par}");
        var text = await ExtractInSandboxAsync(path);
        Assert.Contains("Hello Cabinette RTF", text);
        Assert.Contains("Second line", text);
    }

    [IFilterFact("html")]
    public async Task Html_extracts_in_the_sandboxed_child()
    {
        var path = _folder.Text("page.html", "<html><body><p>Hello <b>sandboxed</b> child çğü</p></body></html>");
        Assert.Contains("Hello sandboxed child çğü", await ExtractInSandboxAsync(path));
    }

    /// <summary>
    /// A Word 97-2003 document: the one Windows ships (System32\MSDRM\MsoIrmProtector.doc, the page older Word versions show
    /// for a document protected with rights management), copied to the test folder.
    /// </summary>
    [IFilterFact("doc")]
    public async Task Doc_extracts_in_the_sandboxed_child()
    {
        var source = Path.Combine(Environment.SystemDirectory, "MSDRM", "MsoIrmProtector.doc");
        if (!File.Exists(source))
        {
            Assert.Fail($"{source} is missing: no Word 97-2003 document to test with.");
        }

        var path = Path.Combine(_folder.Path, "protector.doc");
        File.Copy(source, path);
        var text = await ExtractInSandboxAsync(path);
        Assert.Contains("This document has been IRM protected by policy.", text);
    }

    private static Task<ChildProcessRunner.Result> SafeChildRun(ChildCommand command) => SafeChild.RunAsync(command, TimeSpan.FromSeconds(30));

    /// <summary>Extracts through the real child, measures the child while it runs, and checks its sandbox.</summary>
    private async Task<string> ExtractInSandboxAsync(string path)
    {
        var (state, result) = await ExtractAndMeasureAsync(path);
        Assert.Equal(ChildProcess.LowIntegrity, state.Integrity);
        Assert.False(state.UserShadowStacks, "the child runs with CET shadow stacks");
        Assert.Equal(["SeChangeNotifyPrivilege"], state.Privileges.Select(p => p.Name));
        Assert.NotNull(result);
        return result.Text;
    }

    private async Task<ProcessSecurity.State> MeasureExtractionAsync(string path) => (await ExtractAndMeasureAsync(path)).State;

    private async Task<(ProcessSecurity.State State, ExtractedText? Result)> ExtractAndMeasureAsync(string path)
    {
        var extractor = SafeChild.Extractor(_folder.Path);
        ProcessSecurity.State? state = null;
        extractor.Started = pid => state = ProcessSecurity.Measure(pid);

        var result = await extractor.ExtractAsync(path, 100_000);

        Assert.NotNull(state);
        output.WriteLine($"{Path.GetExtension(path)}: child integrity {state.Integrity}, user shadow stacks {state.UserShadowStacks}, " +
                         $"privileges [{string.Join(", ", state.Privileges.Select(p => p.Name))}], measured while running: {state.Running}");
        Assert.True(state.Running, "the child had exited before it was measured");
        Assert.False(Directory.Exists(SafeChild.DataDirectory(_folder.Path)), "the child started like the app (it made a data folder)");
        return (state, result);
    }
}

/// <summary>Reads a process's integrity level and CET state from outside, and an exe's CETCOMPAT flag.</summary>
internal static class ProcessSecurity
{
    private const int ProcessUserShadowStackPolicy = 15;
    private const int TokenPrivileges = 3;
    private const int TokenIntegrityLevel = 25;
    private const uint PrivilegeEnabled = 0x00000002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint TokenQuery = 0x0008;
    private const uint StillActive = 259;

    /// <param name="Integrity">The mandatory label of the process's token (S-1-16-4096 is Low, S-1-16-8192 Medium).</param>
    /// <param name="UserShadowStacks">CET user-mode shadow stacks are on.</param>
    /// <param name="Running">The process hadn't exited yet when it was measured.</param>
    /// <param name="Privileges">Every privilege its token holds, and whether it is enabled.</param>
    public sealed record State(SecurityIdentifier Integrity, bool UserShadowStacks, bool Running, IReadOnlyList<(string Name, bool Enabled)> Privileges);

    public static State Measure(int pid)
    {
        using var process = OpenProcess(ProcessQueryInformation, false, pid);
        if (process.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var running = GetExitCodeProcess(process, out var code) && code == StillActive;
        var (integrity, privileges) = ReadToken(process);
        return new State(integrity, ShadowStacks(process), running, privileges);
    }

    /// <summary>The CETCOMPAT bit of the exe's extended DLL characteristics (IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS).</summary>
    public static bool IsCetCompatible(string exe)
    {
        using var pe = new PEReader(File.OpenRead(exe));
        foreach (var entry in pe.ReadDebugDirectory())
        {
            if ((int)entry.Type == 20)
            {
                var flags = pe.GetSectionData(entry.DataRelativeVirtualAddress).GetContent(0, 4);
                return (BitConverter.ToUInt32(flags.AsSpan()) & 0x1) != 0;
            }
        }

        return false;
    }

    private static bool ShadowStacks(SafeProcessHandle process)
    {
        if (!GetProcessMitigationPolicy(process, ProcessUserShadowStackPolicy, out var flags, sizeof(uint)))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return (flags & 0x1) != 0; // EnableUserShadowStack
    }

    private static (SecurityIdentifier Integrity, List<(string Name, bool Enabled)> Privileges) ReadToken(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var tokenHandle))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        using var token = new SafeAccessTokenHandle(tokenHandle);
        var integrity = Read(token, TokenIntegrityLevel, buffer => new SecurityIdentifier(Marshal.ReadIntPtr(buffer)));

        // TOKEN_PRIVILEGES: a count, then LUID_AND_ATTRIBUTES (an 8-byte LUID and 4 bytes of attributes) each.
        var privileges = Read(token, TokenPrivileges, buffer =>
        {
            var list = new List<(string, bool)>();
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var entry = buffer + 4 + (i * 12);
                var luid = Marshal.ReadInt64(entry);
                var attributes = (uint)Marshal.ReadInt32(entry + 8);
                list.Add((PrivilegeName(luid), (attributes & PrivilegeEnabled) != 0));
            }

            return list;
        });
        return (integrity, privileges);
    }

    private static T Read<T>(SafeAccessTokenHandle token, int informationClass, Func<IntPtr, T> parse)
    {
        GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var length);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, length, out _))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return parse(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string PrivilegeName(long luid)
    {
        var name = new StringBuilder(64);
        var length = name.Capacity;
        return LookupPrivilegeNameW(null, ref luid, name, ref length) ? name.ToString() : $"LUID {luid}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint flags, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeNameW(string? systemName, ref long luid, StringBuilder name, ref int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);
}

/// <summary>Minimal spreadsheet and presentation packages for the Office tests (see <see cref="TestDocuments.Docx"/>).</summary>
internal static partial class TestDocuments
{
    private const string Rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>A workbook with one sheet, one shared-string cell per text in column A.</summary>
    public static byte[] Xlsx(params string[] cells) => Package(new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""",
        ["_rels/.rels"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/officeDocument" Target="xl/workbook.xml"/></Relationships>""",
        ["xl/workbook.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="{Rels}"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""",
        ["xl/_rels/workbook.xml.rels"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{Rels}/sharedStrings" Target="sharedStrings.xml"/><Relationship Id="rId3" Type="{Rels}/styles" Target="styles.xml"/></Relationships>""",
        ["xl/styles.xml"] = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf xfId="0"/></cellXfs></styleSheet>""",
        ["xl/sharedStrings.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{cells.Length}" uniqueCount="{cells.Length}">{string.Concat(cells.Select(c => $"<si><t>{Escape(c)}</t></si>"))}</sst>""",
        ["xl/worksheets/sheet1.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{string.Concat(cells.Select((_, i) => $"""<row r="{i + 1}"><c r="A{i + 1}" t="s"><v>{i}</v></c></row>"""))}</sheetData></worksheet>""",
    });

    /// <summary>A presentation with one slide holding one text box (plus the master, layout and theme it needs).</summary>
    public static byte[] Pptx(string text)
    {
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string emptyTree = """<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>""";
        var textBox = $"""<p:sp><p:nvSpPr><p:cNvPr id="2" name="Text 1"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="457200" y="457200"/><a:ext cx="8229600" cy="914400"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US"/><a:t>{Escape(text)}</a:t></a:r></a:p></p:txBody></p:sp>""";
        return Package(new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/><Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/><Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/></Types>""",
            ["_rels/.rels"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/officeDocument" Target="ppt/presentation.xml"/></Relationships>""",
            ["ppt/presentation.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="{a}" xmlns:r="{Rels}" xmlns:p="{p}"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst><p:sldSz cx="9144000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>""",
            ["ppt/_rels/presentation.xml.rels"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/slideMaster" Target="slideMasters/slideMaster1.xml"/><Relationship Id="rId2" Type="{Rels}/slide" Target="slides/slide1.xml"/><Relationship Id="rId3" Type="{Rels}/theme" Target="theme/theme1.xml"/></Relationships>""",
            ["ppt/slides/slide1.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="{a}" xmlns:r="{Rels}" xmlns:p="{p}"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{textBox}</p:spTree></p:cSld></p:sld>""",
            ["ppt/slides/_rels/slide1.xml.rels"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/></Relationships>""",
            ["ppt/slideLayouts/slideLayout1.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldLayout xmlns:a="{a}" xmlns:r="{Rels}" xmlns:p="{p}" type="blank">{emptyTree}</p:sldLayout>""",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>""",
            ["ppt/slideMasters/slideMaster1.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldMaster xmlns:a="{a}" xmlns:r="{Rels}" xmlns:p="{p}">{emptyTree}<p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst></p:sldMaster>""",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="{Rels}/theme" Target="../theme/theme1.xml"/></Relationships>""",
            ["ppt/theme/theme1.xml"] = $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><a:theme xmlns:a="{a}" name="Plain"><a:themeElements><a:clrScheme name="Plain"><a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="1F497D"/></a:dk2><a:lt2><a:srgbClr val="EEECE1"/></a:lt2><a:accent1><a:srgbClr val="4F81BD"/></a:accent1><a:accent2><a:srgbClr val="C0504D"/></a:accent2><a:accent3><a:srgbClr val="9BBB59"/></a:accent3><a:accent4><a:srgbClr val="8064A2"/></a:accent4><a:accent5><a:srgbClr val="4BACC6"/></a:accent5><a:accent6><a:srgbClr val="F79646"/></a:accent6><a:hlink><a:srgbClr val="0000FF"/></a:hlink><a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme><a:fontScheme name="Plain"><a:majorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Plain"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>""",
        });
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text);

    private static byte[] Package(Dictionary<string, string> parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }
}
