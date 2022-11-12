namespace DistIL.IR;

using System.IO;

public abstract class Value
{
    public TypeDesc ResultType { get; protected set; } = PrimType.Void;
    /// <summary> Whether this value's result type is not void. </summary>
    public bool HasResult => ResultType.Kind != TypeKind.Void;

    public abstract void Print(PrintContext ctx);
    public virtual void PrintAsOperand(PrintContext ctx) => Print(ctx);

    public override string ToString()
    {
        var sw = new StringWriter();
        Print(new PrintContext(sw, (this as TrackedValue)?.GetSymbolTable() ?? SymbolTable.Detached));
        return sw.ToString();
    }

    internal virtual void AddUse(Instruction user, int operIdx) { }
    internal virtual void RemoveUse(ref UseDef use) { }
}
/// <summary> The base class for a value that tracks it uses. </summary>
public abstract class TrackedValue : Value
{
    //The use list is a doubly-linked list where nodes are keept in a array owned by
    //user instructions. Nodes (defs) contains the actual refs for the prev/next links.
    //References are represented as (Inst Owner, int Index).
    UseRef _firstUse;

    /// <summary> The number of (operand) uses this value have. </summary>
    public int NumUses { get; private set; }

    internal override void AddUse(Instruction user, int operIdx)
    {
        var use = new UseRef() { Owner = user, Index = operIdx };
        if (_firstUse.Exists) {
            _firstUse.Prev = use;
            use.Next = _firstUse;
        }
        _firstUse = use;
        NumUses++;
    }
    internal override void RemoveUse(ref UseDef use)
    {
        if (use.Prev.Exists) {
            use.Prev.Next = use.Next;
        } else {
            _firstUse = use.Next!;
        }
        if (use.Next.Exists) {
            use.Next.Prev = use.Prev;
        }
        use = default; //clear prev/next links
        NumUses--;
    }

    public Instruction? GetFirstUser()
    {
        return _firstUse.Owner;
    }

    /// <summary> Replace all uses of this value with `newValue`. </summary>
    public void ReplaceUses(Value newValue)
    {
        if (newValue == this || !_firstUse.Exists) return;

        //Update user operands and find last use
        var use = _firstUse;
        while (true) {
            use.Owner._operands[use.Index] = newValue;

            var next = use.Next;
            if (!next.Exists) break;
            use = next;
        }
        //Merge use lists
        if (newValue is TrackedValue n) {
            if (n._firstUse.Exists) {
                use.Next = n._firstUse;
                n._firstUse.Prev = use;
            }
            n._firstUse = _firstUse;
            n.NumUses += NumUses;
        }
        _firstUse = default;
        NumUses = 0;
    }

    /// <summary> Returns an enumerator of instructions using this value. Neither order nor uniqueness is guaranteed. </summary>
    public ValueUserIterator Users() => new() { _use = _firstUse };
    /// <summary> Returns an enumerator of operands using this value. </summary>
    public ValueUseIterator Uses() => new() { _use = _firstUse };

    public virtual SymbolTable? GetSymbolTable() => null;
}

public struct ValueUserIterator : Iterator<Instruction>
{
    internal UseRef _use;
    public Instruction Current { get; private set; }

    public bool MoveNext()
    {
        if (_use.Exists) {
            Current = _use.Owner;
            _use = _use.Next;
            return true;
        }
        return false;
    }
}
public struct ValueUseIterator : Iterator<(Instruction Inst, int OperIdx)>
{
    internal UseRef _use;
    public (Instruction Inst, int OperIdx) Current { get; private set; }

    public bool MoveNext()
    {
        if (_use.Exists) {
            Current = (_use.Owner, _use.Index);
            _use = _use.Next;
            return true;
        }
        return false;
    }
}

internal struct UseRef
{
    public Instruction Owner;
    public int Index;

    public bool Exists => Owner != null;

    public ref UseRef Prev => ref Def.Prev;
    public ref UseRef Next => ref Def.Next;

    public ref UseDef Def => ref Owner._useDefs[Index];

    public override string ToString() => Owner == null ? "<null>" : $"<{Owner}> at {Index}";
}
internal struct UseDef
{
    public UseRef Prev, Next;
}