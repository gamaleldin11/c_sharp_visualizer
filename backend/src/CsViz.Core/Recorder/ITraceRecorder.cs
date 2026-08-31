using CsViz.Core.Values;

namespace CsViz.Core.Recorder;

public interface ITraceRecorder
{
    void RecordSetLocal(int frameId, int slotId, IValue value);
    void RecordPushFrame(Frames.Frame frame);
    void RecordPopFrame();
    void RecordSetField(int objId, string name, IValue value);
    void RecordSetElem(int objId, int index, IValue value); // 1D for now
    void RecordNewObj(int objId, Heap.InterpObject obj);
    void RecordStdout(string text);
    void RecordScope(int frameId, int slotId, bool inScope);
    
    // We also need to record step boundaries
    void BeginStep(Microsoft.CodeAnalysis.IOperation op, string kind);
    void EndStep();
}
