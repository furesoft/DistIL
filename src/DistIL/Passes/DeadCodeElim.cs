namespace DistIL.Passes;

public class DeadCodeElim : MethodPass
{
    public override void Run(MethodTransformContext ctx)
    {
        bool changed = false;
        changed |= RemoveUnreachableBlocks(ctx.Method);
        changed |= RemoveUselessCode(ctx.Method);

        if (changed) {
            ctx.InvalidateAll();
        }
    }

    public static bool RemoveUnreachableBlocks(MethodBody method)
    {
        var worklist = new DiscreteStack<BasicBlock>();

        //Mark reachable blocks with a depth first search
        worklist.Push(method.EntryBlock);

        while (worklist.TryPop(out var block)) {
            //(goto 1 ? T : F)  ->  (goto T)
            if (block.Last is BranchInst { Cond: ConstInt { Value: var cond } } br) {
                var (blockT, blockF) = cond != 0 ? (br.Then, br.Else!) : (br.Else!, br.Then);

                blockF.RedirectPhis(block, newPred: null);
                block.SetBranch(blockT);
            }
            
            foreach (var succ in block.Succs) {
                worklist.Push(succ);
            }
        }

        //Sweep unreachable blocks
        bool changed = false;

        foreach (var block in method) {
            if (worklist.WasPushed(block)) continue;

            //Remove incomming args from phis in reachable blocks
            foreach (var succ in block.Succs) {
                if (worklist.WasPushed(succ)) {
                    succ.RedirectPhis(block, newPred: null);
                }
            }
            block.Remove();
            changed = true;
        }
        return changed;
    }

    public static bool RemoveUselessCode(MethodBody method)
    {
        var worklist = new DiscreteStack<Instruction>();

        //Mark useful instructions
        foreach (var inst in method.Instructions()) {
            if (inst.SafeToRemove) continue;

            //Mark `inst` and its entire dependency chain
            worklist.Push(inst);

            while (worklist.TryPop(out var chainInst)) {
                foreach (var oper in chainInst.Operands) {
                    if (oper is Instruction operI) {
                        worklist.Push(operI);
                    }
                }
            }
        }

        //Sweep useless instructions
        bool changed = false;

        foreach (var inst in method.Instructions()) {
            if (!worklist.WasPushed(inst)) {
                inst.Remove();
                changed = true;
            }
            else if (inst is PhiInst phi) {
                PeelTrivialPhi(phi);
            }
        }
        return changed;
    }

    //Remove phi-webs where all arguments have the same value
    private static void PeelTrivialPhi(PhiInst phi)
    {
        while (true) {
            var firstArg = phi.GetValue(0);

            for (int i = 1; i < phi.NumArgs; i++) {
                var arg = phi.GetValue(i);

                if (arg != firstArg && arg != phi) return;
            }
            phi.ReplaceWith(firstArg);

            if (firstArg is PhiInst nextPhi) {
                phi = nextPhi;
            } else break;
        }
    }
}