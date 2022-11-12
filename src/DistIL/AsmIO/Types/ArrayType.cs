namespace DistIL.AsmIO;

using System.Reflection;

/// <summary> Represents a single dimensional array type. </summary>
public class ArrayType : CompoundType
{
    public override TypeKind Kind => TypeKind.Array;
    public override StackType StackType => StackType.Object;
    public override TypeDesc? BaseType => PrimType.Array;

    protected override string Postfix => "[]";

    internal ArrayType(TypeDesc elemType)
        : base(elemType)
    {
    }

    protected override CompoundType New(TypeDesc elemType)
        => new ArrayType(elemType);
}

/// <summary> Represents an over complicated multi-dimensional array type. </summary>
public class MDArrayType : CompoundType
{
    public int Rank { get; }
    public ImmutableArray<int> LowerBounds { get; }
    public ImmutableArray<int> Sizes { get; }

    public override TypeKind Kind => TypeKind.Array;
    public override StackType StackType => StackType.Object;
    public override TypeDesc? BaseType => PrimType.Array;

    private List<MDArrayMethod>? _methods;
    public override IReadOnlyList<MDArrayMethod> Methods {
        get {
            if (_methods == null) {
                int count = (int)MDArrayMethod.Kind.Count_;
                _methods = new List<MDArrayMethod>(count);
                for (int i = 0; i < count; i++) {
                    _methods.Add(new MDArrayMethod(this, (MDArrayMethod.Kind)i));
                }
            }
            return _methods;
        }
    }
    protected override string Postfix {
        get {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < Rank; i++) {
                if (i != 0) sb.Append(',');

                int lowerBound = 0;

                if (i < LowerBounds.Length) {
                    lowerBound = LowerBounds[i];
                    sb.Append(lowerBound);
                }
                sb.Append("...");

                if (i < Sizes.Length) {
                    sb.Append(lowerBound + Sizes[i] - 1);
                }
            }
            sb.Append(']');
            return sb.ToString();
        }
    }

    public MDArrayType(TypeDesc elemType, int rank, ImmutableArray<int> lowerBounds, ImmutableArray<int> sizes)
        : base(elemType)
    {
        Rank = rank;
        LowerBounds = lowerBounds;
        Sizes = sizes;
    }

    protected override CompoundType New(TypeDesc elemType)
        => new MDArrayType(elemType, Rank, LowerBounds, Sizes);

    public override bool Equals(TypeDesc? other)
        => other is MDArrayType o && o.ElemType.Equals(ElemType) && o.Rank == Rank && 
           o.Sizes.SequenceEqual(Sizes) && o.LowerBounds.SequenceEqual(LowerBounds);
}
/// <summary> Represents a multi-dimensional array VES intrinsic (II.14.2) </summary>
public class MDArrayMethod : MethodDesc
{
    public enum Kind { SizeCtor, RangeCtor, Get, Set, Address, Count_ }

    public override MDArrayType DeclaringType { get; }
    public override string Name { get; }
    public Kind MethodKind { get; }

    public override TypeSig ReturnSig { get; }

    internal MDArrayMethod(MDArrayType type, Kind kind)
    {
        bool isCtor = kind <= Kind.RangeCtor;
        DeclaringType = type;
        Name = isCtor ? ".ctor" : kind.ToString();
        MethodKind = kind;
        Attribs = MethodAttributes.Public | (isCtor ? MethodAttributes.SpecialName : 0);
        ImplAttribs = MethodImplAttributes.InternalCall;

        int dims = type.Rank;

#pragma warning disable format
        var readParams = CreateParams(dims);
        (ReturnSig, Params) = kind switch {
            Kind.SizeCtor  => (PrimType.Void, readParams),
            Kind.RangeCtor => (PrimType.Void, CreateParams(dims * 2)),
            Kind.Get       => (type.ElemType, readParams),
            Kind.Set       => (PrimType.Void, CreateParams(dims, true)),
            Kind.Address   => (type.ElemType.CreateByref(), readParams),
        };
#pragma warning restore format

        ImmutableArray<ParamDef> CreateParams(int count, bool isSetter = false)
        {
            var b = ImmutableArray.CreateBuilder<ParamDef>(count + (isSetter ? 2 : 1));
            b.Add(new ParamDef(type, "this"));
            for (int i = 0; i < count; i++) {
                b.Add(new ParamDef(PrimType.Int32, "idx" + i));
            }
            if (isSetter) {
                b.Add(new ParamDef(type.ElemType, "value"));
            }
            return b.MoveToImmutable();
        }
    }

    public override MethodDesc GetSpec(GenericContext ctx)
    {
        throw new UnreachableException();
    }
}
