using System.IO;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace RoslynCodeLens.Metadata;

public sealed class IlDisassemblerAdapter
{
    private readonly PEFileCache _cache;

    public IlDisassemblerAdapter(PEFileCache cache) { _cache = cache; }

    public string DisassembleMethod(string assemblyPath, int metadataToken)
    {
        var pe = _cache.Get(assemblyPath);
        using var writer = new StringWriter();
        var output = new PlainTextOutput(writer);
        var disasm = new ReflectionDisassembler(output, default)
        {
            DetectControlStructure = true,
            ShowSequencePoints = false,
        };
        var handle = MetadataTokens.EntityHandle(metadataToken);
        disasm.DisassembleMethod(pe, (System.Reflection.Metadata.MethodDefinitionHandle)handle);
        return writer.ToString();
    }
}
