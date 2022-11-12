﻿namespace DistIL.Frontend;

using ExceptionRegionKind = System.Reflection.Metadata.ExceptionRegionKind;

public class ILImporter
{
    internal MethodDef _method;
    internal MethodBody _body;
    internal RegionNode? _regionTree;

    internal Variable?[] _argSlots;
    internal VarFlags[] _varFlags;
    internal Dictionary<Variable, Value>? _blockLocalVarStates;

    readonly Dictionary<int, BlockState> _blocks = new();

    private ILImporter(MethodDef method)
    {
        Ensure.That(method.ILBody != null);
        _method = method;
        _body = new MethodBody(method);

        _argSlots = new Variable?[method.Params.Length];
        _regionTree = RegionNode.BuildTree(method.ILBody.ExceptionRegions);

        _varFlags = new VarFlags[_body.Args.Length + method.ILBody!.Locals.Length];
    }

    public static MethodBody ImportCode(MethodDef method)
    {
        return new ILImporter(method).ImportCode();
    }

    private MethodBody ImportCode()
    {
        var ilBody = _method.ILBody!;
        var code = ilBody.Instructions.AsSpan();
        var ehRegions = ilBody.ExceptionRegions;
        var leaders = FindLeaders(code, ehRegions);

        AnalyseVars(code, leaders);
        CreateBlocks(leaders);
        CreateGuards(ehRegions);
        ImportBlocks(code, leaders);
        return _body;
    }

    private void CreateBlocks(BitSet leaders)
    {
        //Remove 0th label to avoid creating 2 blocks
        bool firstHasPred = leaders.Remove(0);
        var entryBlock = firstHasPred ? _body.CreateBlock() : null!;

        int startOffset = 0;
        foreach (int endOffset in leaders) {
            _blocks[startOffset] = new BlockState(this, startOffset);
            startOffset = endOffset;
        }
        //Ensure that the entry block don't have predecessors
        if (firstHasPred) {
            var firstBlock = GetBlock(0).Block;
            entryBlock.SetBranch(firstBlock);
        }
    }

    private void CreateGuards(ExceptionRegion[] clauses)
    {
        var mappings = new Dictionary<GuardInst, ExceptionRegion>(clauses.Length);

        //I.12.4.2.5 Overview of exception handling
        foreach (var clause in clauses) {
            var kind = clause.Kind switch {
                ExceptionRegionKind.Catch or
                ExceptionRegionKind.Filter  => GuardKind.Catch,
                ExceptionRegionKind.Finally => GuardKind.Finally,
                ExceptionRegionKind.Fault   => GuardKind.Fault,
                _ => throw new InvalidOperationException()
            };
            bool hasFilter = clause.Kind == ExceptionRegionKind.Filter;

            var startBlock = GetOrSplitStartBlock(clause);
            var handlerBlock = GetBlock(clause.HandlerStart);
            var filterBlock = hasFilter ? GetBlock(clause.FilterStart) : null;

            var guard = new GuardInst(kind, handlerBlock.Block, clause.CatchType, filterBlock?.Block);
            startBlock.InsertAnteLast(guard);

            //Push exception on handler/filter entry stack
            if (kind == GuardKind.Catch) {
                handlerBlock.PushNoEmit(guard);
            }
            if (hasFilter) {
                filterBlock!.PushNoEmit(guard);
            }
            mappings[guard] = clause;
        }

        BasicBlock GetOrSplitStartBlock(ExceptionRegion region)
        {
            var state = GetBlock(region.TryStart);

            //Create a new dominating block for this region if it nests any other in the current block.
            //Note that this code relies on the region table to be correctly ordered, as required by ECMA335:
            //  "If handlers are nested, the most deeply nested try blocks shall come
            //  before the try blocks that enclose them."
            //TODO: consider using the region tree for this
            if (IsBlockNestedBy(region, state.EntryBlock)) {
                var newBlock = _body.CreateBlock(insertAfter: state.EntryBlock.Prev);

                //FIXME: Block.RedirectPreds()?
                foreach (var pred in state.EntryBlock.Preds) {
                    Debug.Assert(pred.NumSuccs == 1);
                    pred.SetBranch(newBlock);
                }
                newBlock.SetBranch(state.EntryBlock);
                state.EntryBlock = newBlock;
            }
            return state.EntryBlock;
        }
        bool IsBlockNestedBy(ExceptionRegion region, BasicBlock block)
        {
            foreach (var guard in block.Guards()) {
                var currRegion = mappings[guard];
                if (currRegion.TryStart >= region.TryStart && currRegion.TryEnd < region.TryEnd) {
                    return true;
                }
            }
            return false;
        }
    }

    private void ImportBlocks(Span<ILInstruction> code, BitSet leaders)
    {
        var entryBlock = _body.EntryBlock ?? GetBlock(0).Block;

        //Copy stored/address taken arguments to local variables
        for (int i = 0; i < _argSlots.Length; i++) {
            if (!Has(_varFlags[i], VarFlags.AddrTaken | VarFlags.Stored)) continue;

            var arg = _body.Args[i];
            var slot = _argSlots[i] = new Variable(arg.ResultType, name: $"a_{arg.Name}");
            slot.IsExposed = Has(_varFlags[i], VarFlags.AddrTaken);
            entryBlock.InsertAnteLast(new StoreVarInst(slot, arg));
        }

        //Import code
        int startIndex = 0;
        foreach (int endOffset in leaders) {
            var block = GetBlock(code[startIndex].Offset);
            int endIndex = FindIndex(code, endOffset);
            block.ImportCode(code[startIndex..endIndex]);
            startIndex = endIndex;
        }
    }

    private void AnalyseVars(Span<ILInstruction> code, BitSet leaders)
    {
        int blockStartIdx = 0;
        var localVars = _method.ILBody!.Locals;
        var lastUseBlocks = new int[localVars.Length];

        foreach (int endOffset in leaders) {
            int blockEndIdx = FindIndex(code, endOffset);
            foreach (ref var inst in code[blockStartIdx..blockEndIdx]) {
                var (op, varIdx) = GetVarInstOp(inst.OpCode, inst.Operand);
                if (op == VarFlags.None) continue;

                int slotIdx = varIdx;

                if (Has(op, VarFlags.IsLocal)) {
                    slotIdx += _body.Args.Length;
                    var currFlags = _varFlags[slotIdx];

                    if (lastUseBlocks[varIdx] != blockStartIdx) {
                        if (currFlags != VarFlags.None) {
                            op |= VarFlags.CrossesBlock;

                            int lastOffset = code[lastUseBlocks[varIdx]].Offset;
                            if (_regionTree != null && !_regionTree.AreOnSameRegion(lastOffset, inst.Offset)) {
                                op |= VarFlags.CrossesRegions;
                            }
                        }
                        lastUseBlocks[varIdx] = blockStartIdx;
                    }

                    if (Has(op & currFlags, VarFlags.Stored)) {
                        op |= VarFlags.MultipleStores;
                    }
                    if (Has(op, VarFlags.Loaded) && !Has(currFlags, VarFlags.Stored)) {
                        op |= VarFlags.LoadBeforeStore;
                    }

                    if (Has(op, VarFlags.AddrTaken | VarFlags.CrossesRegions)) {
                        localVars[varIdx].IsExposed = true;
                    }
                }
                _varFlags[slotIdx] |= op;
            }
            blockStartIdx = blockEndIdx;
        }
    }

    //Returns a bitset containing all instruction offsets where a block starts (branch targets).
    private static BitSet FindLeaders(Span<ILInstruction> code, ExceptionRegion[] ehRegions)
    {
        int codeSize = code[^1].GetEndOffset();
        var leaders = new BitSet(codeSize);

        foreach (ref var inst in code) {
            if (!inst.OpCode.IsTerminator()) continue;

            if (inst.Operand is int targetOffset) {
                leaders.Add(targetOffset);
            }
            //switch
            else if (inst.Operand is int[] targetOffsets) {
                foreach (int offset in targetOffsets) {
                    leaders.Add(offset);
                }
            }
            leaders.Add(inst.GetEndOffset()); //fallthrough
        }

        foreach (var region in ehRegions) {
            //Note: end offsets must have already been marked by leave/endfinally
            leaders.Add(region.TryStart);

            if (region.HandlerStart >= 0) {
                leaders.Add(region.HandlerStart);
            }
            if (region.FilterStart >= 0) {
                leaders.Add(region.FilterStart);
            }
        }
        return leaders;
    }

    //Binary search to find instruction index using offset
    private static int FindIndex(Span<ILInstruction> code, int offset)
    {
        int start = 0;
        int end = code.Length - 1;
        while (start <= end) {
            int mid = (start + end) / 2;
            int c = offset - code[mid].Offset;
            if (c < 0) {
                end = mid - 1;
            } else if (c > 0) {
                start = mid + 1;
            } else {
                return mid;
            }
        }
        //Special case last instruction
        if (offset >= code[^1].Offset) {
            return code.Length;
        }
        throw new InvalidProgramException("Invalid instruction offset");
    }

    /// <summary> Gets or creates a block for the specified instruction offset. </summary>
    internal BlockState GetBlock(int offset) => _blocks[offset];

    internal (Value VarOrArg, VarFlags CombinedFlags, VarFlags InstOp) GetVar(ref ILInstruction inst)
    {
        var (op, index) = GetVarInstOp(inst.OpCode, inst.Operand);
        Debug.Assert(op != VarFlags.None);

        return Has(op, VarFlags.IsArg)
            ? (_argSlots[index] ?? _body.Args[index] as Value, _varFlags[index], op)
            : (_method.ILBody!.Locals[index], _varFlags[index + _body.Args.Length], op);
    }

    internal ref Value? GetBlockLocalVarSlot(Variable var)
    {
        _blockLocalVarStates ??= new();
        return ref _blockLocalVarStates.GetOrAddRef(var);
    }

    private static (VarFlags Op, int Index) GetVarInstOp(ILCode code, object? operand)
    {
        var op = code switch {
            >= ILCode.Ldarg_0 and <= ILCode.Ldarg_3 => VarFlags.Loaded | VarFlags.IsArg,
            >= ILCode.Ldloc_0 and <= ILCode.Ldloc_3 => VarFlags.Loaded | VarFlags.IsLocal,
            >= ILCode.Stloc_0 and <= ILCode.Stloc_3 => VarFlags.Stored | VarFlags.IsLocal,
            ILCode.Ldarg_S or ILCode.Ldarg          => VarFlags.Loaded | VarFlags.IsArg,
            ILCode.Ldloc_S or ILCode.Ldloc          => VarFlags.Loaded | VarFlags.IsLocal,
            ILCode.Starg_S or ILCode.Starg          => VarFlags.Stored | VarFlags.IsArg,
            ILCode.Stloc_S or ILCode.Stloc          => VarFlags.Stored | VarFlags.IsLocal,
            ILCode.Ldarga_S or ILCode.Ldarga        => VarFlags.AddrTaken | VarFlags.IsArg,
            ILCode.Ldloca_S or ILCode.Ldloca        => VarFlags.AddrTaken | VarFlags.IsLocal,
            _ => VarFlags.None
        };
        int index = op == 0 ? 0 :
            code is >= ILCode.Ldarg_0 and <= ILCode.Stloc_3
                ? (code - ILCode.Ldarg_0) & 3
                : (int)operand!;
        return (op, index);
    }
    private static bool Has(VarFlags x, VarFlags y) => (x & y) != 0;
}