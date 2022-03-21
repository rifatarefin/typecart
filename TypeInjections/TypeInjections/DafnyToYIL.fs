namespace TypeInjections

// DotNet
open System
open System.Numerics
open Microsoft.BaseTypes
// Dafny
open Microsoft.Dafny
// Yucca
open UtilsFR

(* convert C# Dafny AST to F# Dafny AST

   adapted from 15bd2b1d9a45fb1799afacd60439454e56f431cd of YuccaDafnyCompiler

   This module contains mutually recursive functions, roughly one for every non-terminal in the syntax of our Dafny fragment.
   The name of each function corresponds to the return type in the F# AST, e.g., program, decl, expr, tp.
   Together, they traverse the C# AST and create corresponding elements in the F# AST.
   We avoid non-trivial transformations in this code and focus on mirroring the C# AST.
   It has the following purposes:
   - It abstracts from the Dafny internals and their C# implementation.
   - It drops C# boilerplate and those parts of the Dafny implementation that are not essential for our purposes.
   - This file is the only one that needs to be reviewed by a Dafny expert or re-reviewed when Dafny changes.
*)
module DafnyToYIL =
    // references to C# input classes are unqualified, all references to output F# types (sometimes of the same name) are qualified with "Y."
    module Y = YIL

    // ***** helper functions

    let unsupported msg = failwith msg
    let error msg = failwith msg

    // infix operators for recursing into lists (C# list input yields F# list output)
    let (@) f l = List.map f (fromIList l)
    let (@/) f l = List.collect f (fromIList l)

    // special strings that Dafny uses for built-in objects
    let DafnySystem = "_System"
    let DafnyTuple = "_tuple#"
    let DafnyFun = "_#Func"
    let DafnyTotalFun = "_#TotalFunc"
    let DafnyPartialFun = "_#PartialFunc"
    let DafnyMap = "map"
    let DafnyKeys = "Keys"

    // ***** the mutually recursive functions

    (* a program concatenates the input file with all its dependencies, in reverse dependency order
       declarations in a file or module are wrapped in default classes (and a default module if needed)
       and implicitly static *)
    let rec program (p: Program) : Y.Program =
        let decls = p.DefaultModuleDef.TopLevelDecls
        let declsRev = List.rev (fromIList decls)
        let ddecls = List.collect decl declsRev

        { name = p.Name; decls = ddecls }

    // meta information attached to a named declaration
    and namedMeta (dcl: Declaration) : Y.Meta = { position = Some(position dcl.tok) }

    // Dafny does not define a common superclass of INamedRegion and IAttributeBearingDeclaration and F# does not support intersection types
    // So we need to duplicate the method here for Declaration and ModuleDefinition
    and namedMetaModDef (dcl: ModuleDefinition) : Y.Meta = { position = Some(position dcl.tok) }

    // trivial conversion of Dafny source position to YIL source positions
    and position (t: Microsoft.Boogie.IToken) : Y.Position =
        { filename = IO.Path.GetFileName t.filename // whole path would be better, but it'd make error messages system-specific, which breaks the negative tests
          pos = t.pos
          line = t.line
          col = t.col }

    and decl (td: TopLevelDecl) : Y.Decl list =
        match td with
        | :? DatatypeDecl as d ->
            let tpvars = typeParameter @ d.TypeArgs
            let dName = d.Name
            let meta = namedMeta d
            [ Y.Datatype(dName, tpvars, constructorDecl @ d.Ctors, memberDecl @ d.Members, meta) ]
        | :? LiteralModuleDecl as d ->
            // TODO check inheritance etc.
            if d.TypeArgs.Count <> 0 then
                unsupported "module with type parameters"

            let ms = d.ModuleDef.TopLevelDecls
            let dName = d.Name
            let meta = namedMetaModDef d.ModuleDef
            [ Y.Module(dName, decl @/ ms, meta) ]
        | :? AliasModuleDecl as d ->
            (* Dafny allows "import M", "import m = M" or "import opened M" where M is a module name.
               Either way, the names of M later appear with fully qualified paths. *)
            []
        | :? TypeSynonymDecl as d ->
            // type synonyms and HOL-style subtype definitions
            let tpvars = typeParameter @ d.TypeArgs

            let super, pred =
                match d with
                | :? SubsetTypeDecl as d ->
                    let bv = boundVar d.Var
                    bv.tp, Some(bv.name, expr d.Constraint)
                | _ -> tp d.Rhs, None

            [ Y.TypeDef(d.Name, tpvars, super, pred, false, namedMeta d) ]
        | :? NewtypeDecl as d ->
            // like SubsetTypeDecl but only for a numeric supertype and new type is not a subtype of the old type
            let bv = boundVar d.Var
            [ Y.TypeDef(d.Name, [], bv.tp, Some(bv.name, expr d.Constraint), true, namedMeta d) ]
        | :? IteratorDecl ->
            unsupported
                "Dafny iterators are too idiosyncratic to be compiled easily to other languages and are therefore not supported"
        | :? DefaultClassDecl as d ->
            // we skip the default class declaration and instead shift its members to the containing module
            memberDecl @ d.Members
        | :? ClassDecl as d ->
            let dName = d.Name
            let meta = namedMeta d
            let typeVars = typeParameter @ d.TypeArgs
            [ Y.Class(dName, isTrait d, typeVars, classType @ d.ParentTraits, memberDecl @ d.Members, meta) ]
        | :? OpaqueTypeDecl as d ->
            let dName = d.Name
            let meta = namedMeta d
            // Misuse Datatype for now when translating opaque types
            let typeVars = typeParameter @ d.TypeArgs
            [ Y.Datatype(dName, typeVars, [], memberDecl @ d.Members, meta) ]
        | :? ModuleExportDecl as d ->
            let exportPath (expSig: ExportSignature) =
                match expSig.Decl with
                | :? MemberDecl as md -> Some(pathOfMemberDecl md)
                | :? NonNullTypeDecl as td -> Some(pathOfTopLevelDecl td)
                | :? IndDatatypeDecl as dd -> Some(pathOfTopLevelDecl dd)
                | _ -> None

            let exportPaths : YIL.Path list =
                d.Exports |> List.ofSeq |> List.choose exportPath

            [ Y.Export exportPaths ]
        | _ ->
            // default module contains a default class, which contains non-nesting declarations
            unsupported (
                "Toplevel declaration "
                + td.Name
                + " of type "
                + td.GetType().FullName
            )

    and constructorDecl (c: DatatypeCtor) : Y.DatatypeConstructor =
        let cName = c.Name

        { name = cName
          ins = formal @ c.Formals
          meta = namedMeta c }

    and case (e: MatchCase) : Y.Case =
        let vardecls = boundVar @ e.Arguments

        let bd =
            match e with
            | :? MatchCaseExpr as c -> expr c.Body
            | :? MatchCaseStmt as c -> Y.EBlock(statement @ c.Body)
            | _ -> error "Unexpected match case"

        { cons = e.Ctor.Name
          vars = vardecls
          body = bd }

    and isTrait (d: TopLevelDecl) =
        match d with
        | :? TraitDecl -> true
        | _ -> false

    and memberDecl (m: MemberDecl) : Y.Decl =
        match m with
        | :? Constructor as m ->
            let tpvars = typeParameter @ m.TypeArgs

            let body =
                if (m.Body = null) then
                    None
                else
                    Some(statement m.Body)

            let mName = m.Name
            Y.ClassConstructor(mName, tpvars, formal @ m.Ins, body, namedMeta m)
        | :? Function as m ->
            // keywords function (ghost), function method, predicate (ghost)
            let tpvars = typeParameter @ m.TypeArgs

            let body =
                if (m.Body = null) then
                    None
                else
                    Some(expr m.Body)

            let mName = m.Name
            let meta = namedMeta m

            Y.Method(
                mName,
                tpvars,
                formal @ m.Formals,
                Y.OutputType(tp m.ResultType),
                body,
                m.IsGhost,
                m.IsStatic,
                meta
            )
        | :? Method as m ->
            // keywords method, lemma (ghost)
            let tpvars = typeParameter @ m.TypeArgs
            let ins = formal @ m.Ins
            let outs = formal @ m.Outs
            (* Dafny allows multiple outputs, which are named to allow mentioning them in the post-conditions.
              We only allow one computational output followed by some ghost outputs (which can be dropped for computation).
              Dafny allows zero outputs, in which case we use the unit type.
           *)
            let output =
                if outs.IsEmpty then
                    Y.OutputType(Y.TUnit)
                else
                    let onlyOneNonGhost =
                        List.forall (fun (d: Y.LocalDecl) -> d.ghost) outs.Tail

                    if not onlyOneNonGhost then
                        unsupported "More than one non-ghost return value in method"

                    Y.OutputDecls(outs)

            let body =
                if (m.Body = null) then
                    None
                else
                    Some(statement m.Body)

            let mName = m.Name

            Y.Method(mName, tpvars, ins, output, body, m.IsGhost, m.IsStatic, namedMeta m)
        | :? ConstantField as m ->
            let mName = m.Name
            let meta = namedMeta m
            Y.Field(mName, tp m.Type, Some(expr m.Rhs), m.IsGhost, m.IsStatic, isMutable = false, meta = meta)
        | :? Field as m ->
            let mName = m.Name
            let meta = namedMeta m
            // Non-constant fields do not have a RHS in Dafny
            // They are always initialized in the `constructor`
            Y.Field(mName, tp m.Type, None, m.IsGhost, m.IsStatic, isMutable = true, meta = meta)
        | _ -> unsupported (m.ToString())

    and formal (f: Formal) : Y.LocalDecl =
        { name = f.Name
          tp = tp f.Type
          ghost = f.IsGhost }

    and typeParameter (t: TypeParameter) : string =
        if t.Variance <> TypeParameter.TPVariance.Non
           || (not t.StrictVariance) then
            unsupported "Type parameter with variance"

        t.Name

    and tp (t: Type) : Y.Type =
        match t with
        | :? UserDefinedType as t ->
            // Detection of type parameters: https://github.com/dafny-lang/dafny/pull/1188
            match t.ResolvedClass with
            | :? TypeParameter -> Y.TVar(t.Name)
            | _ ->
                let p = pathOfUserDefinedType (t)
                let args = tp @ t.TypeArgs
                // Dafny puts a few built-in types into the DafnySystem namespace instead of making them primitive
                if p.names.Head = DafnySystem then
                    let n = p.names.Item(1)

                    if n = "string" then
                        Y.TString
                    elif n = "nat" then
                        Y.TNat
                    elif n = "array" then
                        if args.Length = 1 then
                            Y.TArray(args.Head)
                        else
                            error $"array {p.name} must have exactly one type argument"
                    elif
                        n.StartsWith(DafnyFun)
                        || n.StartsWith(DafnyTotalFun)
                        || n.StartsWith(DafnyPartialFun)
                    then
                        // _#FuncN where N is arity
                        // _#TotalFuncN where N is arity
                        // _#PartialFuncN where N is arity
                        let numIns = args.Length - 1
                        Y.TFun(args.GetSlice(Some 0, Some(numIns - 1)), args.Item(numIns))
                    elif n.StartsWith(DafnyTuple) then
                        // DafnyTuple + "N" where N is arity
                        Y.TTuple(args)
                    elif n = "object" then
                        // Only allowed in ghost code
                        Y.TObject
                    else
                        unsupported $"built-in type {n}"
                else
                    Y.TApply(p, args)
        | :? BoolType -> Y.TBool
        | :? CharType -> Y.TChar
        | :? IntType -> Y.TInt
        | :? RealType -> Y.TReal
        | :? SetType as t -> Y.TSet(tp t.Arg)
        | :? SeqType as t ->
            let aT = tp t.Arg
            // Dafny treats string as seq<char> and sometimes expands it
            if aT = Y.TChar then
                Y.TString
            else
                Y.TSeq(tp t.Arg)
        | :? MapType as t -> Y.TMap(tp t.Domain, tp t.Range)
        | :? TypeProxy as t -> tp t.T // e.g., wrapper for inferred types
        | :? BitvectorType as t -> Y.TBitVector(t.Width)
        | _ -> unsupported $"Type {t.ToString()}"

    and exprO (e: Expression) : Y.Expr option =
        if e = null then
            None
        else
            Some(expr (e))

    and expr (e: Expression) : Y.Expr =
        match e with
        // case: `var foo :- MonadicExpr;` in function methods
        // LetOrFailExpr is a subtype of ConcreteSyntaxExpression, so we need to pattern match on this case before
        // LetOrFailExpr desugars to LetExpr (i.e. e.ResolvedExpression is a LetExpr), so we lose the information
        // about the if-then-else structure by doing that.
        | :? LetOrFailExpr as e ->
            match e.ResolvedExpression with
            | :? LetExpr as e ->
                if e.LHSs.Count <> 1 then
                    unsupported "let with more than 1 LHS"

                if e.RHSs.Count <> 1 then
                    unsupported "let with more than 1 RHS"

                let v = e.LHSs.Item(0)

                if v.Var = null then
                    unsupported "let with constructor pattern"
                else
                    let rhs = expr (e.RHSs.Item(0))

                    match e.Body with
                    | :? ITEExpr as iteE ->
                        let elseExpr = (iteE.Els :?> LetExpr)
                        let var = elseExpr.LHSs.Item(0).Var
                        let body = expr (elseExpr.Body)
                        Y.ELet(var.Name, tp var.Type, rhs, body)
                    | _ -> error "LetOrFailExpr must have an ITEExpr"
            | _ -> error "LetOrFailExpr always resolves to LetExpr"
        | :? ConcreteSyntaxExpression as e ->
            // cases that are eliminated during resolution
            expr e.ResolvedExpression
        // identifiers/names
        | :? IdentifierExpr as e -> Y.EVar(e.Var.Name)
        | :? MemberSelectExpr as e ->
            let r = receiver (e.Obj.Resolved)
            let p = pathOfMemberDecl (e.Member)

            if p.names.Item(0) = DafnySystem then
                let e =
                    match r with
                    | Y.ObjectReceiver (e) -> e
                    | Y.StaticReceiver _ -> error "Unknown receiver"

                if p.names.Item(1).StartsWith(DafnyTuple) then
                    let i = p.name |> int
                    Y.EProj(e, i)
                elif p.names.Item(1) = DafnyMap
                     && p.names.Item(2) = DafnyKeys then
                    Y.EMapKeys(e)
                else
                    unsupported $"Unknown member {p}"
            else
                let tpargs = tp @ e.TypeApplication_AtEnclosingClass
                // const field vs field
                let isPrivate = e.Member.WhatKind.Equals("field")
                Y.EMemberRef(r, p, tpargs)
        | :? ThisExpr -> Y.EThis
        // literals
        | :? CharLiteralExpr as e -> Y.EChar(string e.Value) // always a string according to Dafny spec
        | :? StringLiteralExpr as e -> Y.EString(string e.Value) // always a string according to Dafny spec
        | :? LiteralExpr as e ->
            // superclass of the above, so must come last
            match e.Value with
            | :? bool as v -> Y.EBool(v)
            | :? BigInteger as v -> Y.EInt(v, tp e.Type)
            | :? BigDec as v -> Y.EReal(v, tp e.Type)
            | null ->
                // StaticReceiverExpr lands here if it is not handled earlier
                Y.ENull(tp e.Type)
            | _ -> unsupported $"Literal value {e.ToString()}"
        | :? LambdaExpr as e ->
            let vars = boundVar @ e.BoundVars
            Y.EFun(vars, tp e.Body.Type, expr e.Body)
        | :? SeqSelectExpr as e ->
            let s = expr e.Seq
            let e0 = exprO e.E0
            let e1 = exprO e.E1
            let t = tp (e.Seq.Resolved.Type)
            // Dafny syntax merges several cases here that YIL distinguishes, so we need the type to distinguish
            match (t, e.SelectOne) with
            | (Y.TSeq _, true) -> Y.ESeqAt(s, Option.get e0)
            | (Y.TSeq _, false) -> Y.ESeqRange(s, e0, e1)
            | (Y.TString, true) -> Y.ECharAt(s, Option.get e0) // Dafny strings are character arrays
            | (Y.TString, false) -> Y.EStringRange(s, e0, e1)
            | (Y.TArray _, true) -> Y.EArrayAt(s, [ Option.get e0 ])
            | (Y.TMap _, true) -> Y.EMapAt(s, Option.get e0)
            // User-defined type alias
            | (Y.TApply _, true) ->
                // TODO: can we use this approach without resorting to `tp`?
                match e.Seq.Resolved.Type.AsCollectionType with
                | :? MapType -> Y.EMapAt(s, Option.get e0)
                | _ ->
                    unsupported (
                        sprintf
                            "Type of sequence in sequence access: %s"
                            (e.Seq.Resolved.Type.AsCollectionType.ToString())
                    )
            | _ -> unsupported (sprintf "Type of sequence in sequence access: %s" (t.ToString()))
        | :? MultiSelectExpr as e ->
            // TODO check if this can occur for anything but multi-dimensional arrays
            Y.EArrayAt(expr e.Array, expr @ e.Indices)
        | :? SetDisplayExpr as e ->
            if not (e.Finite) then
                unsupported "Infinite set definition"

            let elems = expr @ e.Elements

            let t =
                match (tp e.Type) with
                | Y.TSet (a) -> a
                | _ -> error "Unexpected set type"

            Y.ESet(t, elems)
        | :? SeqDisplayExpr as e ->
            let elems = expr @ e.Elements

            match tp e.Type with
            // empty string literal sometimes presents as empty char sequence
            | Y.TString when List.isEmpty elems -> Y.EString ""
            | Y.TString -> Y.EToString elems
            | Y.TSeq (a) -> Y.ESeq(a, elems)
            | _ -> unsupported (sprintf "unexpected sequence type: %s" ((tp e.Type).ToString()))
        | :? SeqUpdateExpr as e -> Y.ESeqUpdate(expr e.Seq, expr e.Index, expr e.Value)
        // applications
        | :? FunctionCallExpr as e ->
            let r = e.Receiver
            let recv = receiver (r.Resolved)
            let args = expr @ e.Args

            let tpargs =
                List.append (tp @ e.TypeApplication_AtEnclosingClass) (tp @ e.TypeApplication_JustFunction)

            Y.EMethodApply(recv, pathOfMemberDecl (e.Function), tpargs, args, false)
        | :? ApplyExpr as e -> Y.EAnonApply(expr e.Function, expr @ e.Args)
        | :? UnaryOpExpr as e ->
            let o = e.Op.ToString()
            // disambiguate Dafny's ad-hoc polymorphism
            let oT =
                match o, tp e.E.Type with
                | "Cardinality", Y.TString -> "Cardinality-String"
                | "Cardinality", Y.TSeq _ -> "Cardinality-Seq"
                | "Cardinality", Y.TSet _ -> "Cardinality-Set"
                | "Cardinality", Y.TArray _ -> "Cardinality-Array"
                | "Cardinality", _ -> unsupported (sprintf "cardinality %s" ((tp e.E.Type).ToString()))
                | o, _ -> o

            Y.EUnOpApply(oT, expr e.E)
        | :? BinaryExpr as e ->
            let o = e.ResolvedOp.ToString()
            // disambiguate Dafny's ad-hoc polymorphism and the string = Seq<char> merger
            let oT =
                match o, tp e.E0.Type, tp e.E1.Type with
                | "InSeq", _, Y.TString -> o + "-String"
                | "NotInSeq", _, Y.TString -> o + "-String"
                | _ -> o

            Y.EBinOpApply(oT, expr e.E0, expr e.E1)
        | :? DatatypeValue as e ->
            let ctor = e.Ctor
            let n = ctor.Name
            // we assume constructor names live in the scope of the containing datatype
            // Dafny's FullName function actually prefixes them with "#"
            let path =
                pathOfTopLevelDecl(ctor.EnclosingDatatype)
                    .child (n)

            let tpargs = tp @ e.InferredTypeArgs
            let args = expr @ e.Arguments

            if n.StartsWith("_#Make") then
                // built-in datatype for tuples
                Y.ETuple(args) // tpargs are the types of the components
            else
                if n.Contains("#") then
                    // make sure we caught all the built-in names
                    unsupported $"special name: {n}"

                Y.EConstructorApply(path, tpargs, args)
        // others
        | :? ConversionExpr as e -> Y.ETypeConversion(expr e.E, tp e.ToType)
        | :? StmtExpr as e ->
            // S;E
            Y.EBlock([ statement e.S; expr e.E ])
        | :? LetExpr as e ->
            if e.LHSs.Count <> 1 then
                unsupported "let with more than 1 LHS"

            if e.RHSs.Count <> 1 then
                unsupported "let with more than 1 RHS"

            let v = e.LHSs.Item(0)

            if v.Var = null then
                unsupported "let with constructor pattern"
            else
                Y.ELet(v.Var.Name, tp v.Var.Type, expr (e.RHSs.Item(0)), expr e.Body)
        | :? ITEExpr as e -> Y.EIf(expr e.Test, expr e.Thn, Some(expr e.Els))
        | :? MatchExpr as e -> Y.EMatch(expr e.Source, tp e.Source.Type, case @ e.Cases)
        | :? QuantifierExpr as e ->
            // mostly in logic parts; but can only be computational if domain is finite (occurs once in Yucca)
            if e.TypeArgs.Count > 0 then
                // Dafny quantifiers can only have type args when using the attribute `{:typeQuantifier}`,
                // https://github.com/dafny-lang/dafny/blob/288cab1c53eefbddaf13e2f8fb60eda394f87aa8/Source/Dafny/AST/DafnyAst.cs#L11481
                unsupported "quantifier with type arguments"

            let q =
                match e with
                | :? ForallExpr -> Y.Forall
                | :? ExistsExpr -> Y.Exists
                | _ -> error "unknown quantifier"

            Y.EQuant(q, boundVar @ e.BoundVars, exprO e.Range, expr e.Term)
        | :? MapComprehension as e -> unsupported "missing case: map comprehension"
        | :? MapDisplayExpr -> unsupported "missing case: map display"
        | :? SetComprehension -> unsupported "missing case: set comprehension"
        | null -> error "expression is null"
        | _ -> unsupported ("expression " + e.ToString())

    and statement (s: Statement) : Y.Expr =
        match s with
        | :? ConcreteSyntaxStatement as s ->
            // cases that are eliminated during resolution
            statement s.ResolvedStatement
        | :? BlockStmt as b -> Y.EBlock(statement @ b.Body)
        | :? VarDeclStmt as s ->
            let vs = boundVar @ s.Locals

            let uninitialized =
                Y.EDecls(List.map (fun (v: Y.LocalDecl) -> (v, None)) vs)

            match s.Update with
            | null -> uninitialized
            | :? UpdateStmt as u ->
                // Rewrite var _, _ := rhs1, rhs2 to rhs1; rhs2
                if List.forall (fun (x: LocalVariable) -> x.DisplayName = "_") (fromIList s.Locals) then
                    let ds = rhsOfUpdate (u)
                    Y.EBlock(List.map (fun (x: Y.UpdateRHS) -> x.df) ds)
                else
                    let ds = rhsOfUpdate (u)

                    if vs.Length <> ds.Length then
                        error "Number of LHSs in variable declaration does not match number of RHSs"

                    let vds = List.zip vs (List.map Some ds)

                    Y.EDecls(vds)
            | :? AssignSuchThatStmt as u ->
                if vs.Length <> 1 then
                    unsupported "Variable declaration with more than 1 LHS"

                let v = vs.Head
                let c = expr u.Expr
                Y.EDeclChoice(v, c)
            | :? AssignOrReturnStmt as u ->
                (* See the comment on the case for AssignOrReturnStmt in the method 'statement'
                  This is just the special case where a monadic value is used to initialize a variable *)
                if vs.Length <> 1 then
                    unsupported "Variable declaration with more than 1 LHS"

                let d = Some(rhsOfMonadicUpdate u)
                Y.EDecls([ (vs.Head, d) ])
            | _ -> unsupported "Non-trivial RHS in variable declaration"
        | :? UpdateStmt as s ->
            (* general form is pattern, ..., pattern := value, ..., value
                Lengths need not be the same: RHS can be single expression evaluating to sequence (e.g., method call).
                usually resolves into an AssignStmt or a CallStmt, which seem to occur only after resolution
                the former has a single expression (i.e., pattern on the LHS)
                the latter has a single method call on the RHS, but may have multiple LHS *)
            let res = fromIList s.ResolvedStatements

            if res.Length = 1 then
                statement (res.Item(0))
            else
                (* Presumably this is what happens to more complex updates
                   we could handle this too, but it's good to first see if and how it occurs before deciding how to handle it *)
                unsupported "Update statement with complex resolution"
        | :? CallStmt as s ->
            // pattern, ..., pattern := receiver.method(args)
            // the RHS is easy
            let r = receiver (s.Receiver.Resolved)
            let meth = s.MethodSelect

            let tpargs =
                List.append (tp @ meth.TypeApplication_AtEnclosingClass) (tp @ meth.TypeApplication_JustMember)

            let args = expr @ s.Args
            let ghost = s.Method.IsGhost // true if this is a lemma call

            let rhs =
                Y.EMethodApply(r, pathOfMemberDecl (s.Method), tpargs, args, ghost)
            (* the LHS is a bit more complicated
              The only case we allow is name, g1, ..., gn := value,
              where the gi are ghost variables. If n!=0, the value must be a method call returning n+1 values,
              of which all but the first are ghosts - see the comment on translating method declarations.
            *)
            let ls = fromIList s.Lhs

            if s.Lhs.Count = 0 then
                rhs
            else
                let lhsIsGhost (e: Expression) : bool =
                    match e.Resolved with
                    | :? IdentifierExpr as r -> r.Var.IsGhost
                    | _ -> false

                let onlyOneNonGhost = List.forall lhsIsGhost ls.Tail

                if not onlyOneNonGhost then
                    unsupported "Variable update with more than one non-ghost LHS"
                else
                    let doOne (e: Expression) =
                        match e with
                        // plain assignment name := value
                        | :? IdentifierExpr as l -> expr l
                        | :? MemberSelectExpr as e -> expr e
                        // complex assignment pattern := value
                        | _ -> unsupported "Variable declaration with non-atomic LHS"

                    let ns = List.map doOne ls
                    Y.EUpdate(ns, { df = rhs; monadic = None })
        | :? AssignStmt as s ->
            let rhs = assignmentRhs (s.Rhs)

            match s.Lhs with
            | :? IdentifierExpr as e -> Y.EUpdate([ expr e ], { df = rhs; monadic = None })
            | :? SeqSelectExpr as e ->
                (* TODO check if this is true: We assume this case always means an array update.
                   We only support one-dimensional case a[i] := e for now
                   This only occurs once in Yucca, albeit in string comparison where efficiency may be critical.
                 *)
                match e.Seq.Resolved with
                | :? IdentifierExpr as s -> Y.EArrayUpdate(Y.EVar(s.Name), [ expr e.E0 ], rhs)
                | _ -> unsupported "Complex sequence update"
            | :? MemberSelectExpr as e -> Y.EUpdate([ expr e ], { df = rhs; monadic = None })
            | _ -> unsupported "Non-atomic LHS of assignment"
        | :? VarDeclPattern as s ->
            (* Because we do not cover constructor patterns anyway, we can simply use a Decl to represent a let statement.
               These statements (only?) occur when a match statement is rewritten during resolution
               They are used to declare the pattern variables and bind them to the matched subexpressions
               (which are themselves generated variables).
            *)
            let v = s.LHS

            if v.Var = null then
                unsupported "Variable declaration pattern with constructor pattern"
            else
                Y.EDecls([ (boundVar v.Var, Some({ df = expr (s.RHS); monadic = None })) ])
        | :? ReturnStmt as s ->
            (* There may be more than one return value - see the comment on the translation of the method header.
               There may be no or multiple return values - see the comment on EReturn. *)
            let rs = s.rhss

            let es =
                if rs = null then
                    [] // no return value; it seems rs is never the empty list
                else
                    let doOne (r: AssignmentRhs) =
                        match r with
                        | :? ExprRhs as r -> expr r.Expr
                        | _ -> unsupported "Non-trivial return statement"

                    doOne @ rs

            Y.EReturn(es)
        | :? AssignOrReturnStmt as s ->
            (* name [: A] :- value, monadic assignment that propagates
               user-defined in the respective user-written monad datatype (here: Result).
               In anticipation of the translation, we use the original statement and not s.ResolvedStatements.
               See also the case for VarDeclStmt, which must be treated analogously.
            *)
            let u = rhsOfMonadicUpdate (s)

            let n =
                match s.Lhss.Count with
                | 0 ->
                    // :- v is monadic return v, never used in Yucca
                    unsupported "Empty LHS of :- statement"
                | 1 ->
                    // bind n :- v
                    match s.Lhss.Item(0) with
                    | :? NameSegment as l -> Y.EVar(l.Name)
                    | _ -> unsupported "Non-atomic LHS of :- statement"
                | _ -> unsupported "Multiple LHSs in :- statement"

            Y.EUpdate([ n ], u)
        | :? IfStmt as s ->
            if s.IsBindingGuard then
                unsupported "if statement used as binding guard"

            let els =
                if s.Els = null then
                    None
                else
                    Some(statement s.Els)

            Y.EIf(expr s.Guard, statement s.Thn, els)
        | :? WhileStmt as s -> Y.EWhile(expr s.Guard, statement s.Body, None)
        | :? ForLoopStmt as s -> Y.EFor(boundVar s.LoopIndex, expr s.Start, expr s.End, s.GoingUp, statement s.Body)
        | :? BreakStmt as s ->
            if s.TargetLabel <> null || s.BreakCount > 1 then
                unsupported "Non-trivial break statement"

            Y.EBreak None
        | :? MatchStmt as s -> Y.EMatch(expr s.Source, tp s.Source.Type, case @ s.Cases)
        | :? PrintStmt as s -> Y.EPrint(expr @ s.Args)
        // | :? AssertStmt as s ->
        // | :? AssumeStmt ->
        // | :? ForallStmt ->
        // | :? AssumeStmt as s when expr s.Expr = Y.EBool true -> Y.EUnimplemented
        // | :? CalcStmt -> Y.EUnimplemented
        | _ -> unsupported $"statement {s.ToString()}"
    // ***** qualified names; Dafny has methods for this, but they are a bit confusing and work with .-separated strings
    and pathOfModule (d: ModuleDefinition) : Y.Path =
        if d = null || d.IsDefaultModule then
            Y.Path([])
        else
            pathOfModule(d.EnclosingModule).child (d.Name)

    and pathOfTopLevelDecl (d: TopLevelDecl) : Y.Path =
        let p =
            pathOfModule (d.EnclosingModuleDefinition)

        match d with
        | :? DefaultClassDecl -> p
        | _ -> p.child (d.Name)

    and pathOfMemberDecl (d: MemberDecl) : Y.Path =
        pathOfTopLevelDecl(d.EnclosingClass)
            .child (d.Name)

    and pathOfUserDefinedType (u: UserDefinedType) : Y.Path = pathOfTopLevelDecl (u.ResolvedClass)
    // ***** auxiliary translation functions
    and boundVar (bv: IVariable) : Y.LocalDecl =
        { name = bv.DisplayName
          tp = tp bv.Type
          ghost = bv.IsGhost }

    and classType (t: Type) : Y.ClassType =
        match t with
        | :? UserDefinedType as u ->
            let p = pathOfUserDefinedType (u)
            let ts = tp @ u.TypeArgs
            { path = p; tpargs = ts }
        | _ -> error "unknown type"

    and receiver (r: Expression) : Y.Receiver =
        match r with
        | :? StaticReceiverExpr as r ->
            let ct = classType (r.Type)
            Y.StaticReceiver(ct)
        | _ -> Y.ObjectReceiver(expr r)

    and assignmentRhs (r: AssignmentRhs) : Y.Expr =
        match r with
        | :? ExprRhs as r ->
            // name := value
            expr r.Expr
        | :? TypeRhs as r ->
            // When using an array initialization, make sure it matches Java's default
            // TODO: Translate the lambda expression and get rid of it in the YILToJava translation to make this code agnostic of Java
            if r.ElementInit <> null then
                match expr r.ElementInit with
                | Y.EFun (_, Y.TBool _, Y.EBool false) -> ()
                | _ -> unsupported "Array initialization not supported or does not match Java default type"
            else
                ()
            // name := new ...
            if r.ArrayDimensions <> null then
                // name := new A[dimensions]
                Y.EArray(tp r.EType, expr @ r.ArrayDimensions)
            else
                (* This is not correct if anything but a default constructor is to be applied.
                   That is find because in Yucca, this case only occurs for constructing iterators.
                *)
                let ct = classType (r.Type)

                let exprFromBinding (e: ActualBinding) = e.Actual

                let args =
                    List.map expr (exprFromBinding @ r.Bindings.ArgumentBindings)

                Y.ENew(ct, args)
        | _ -> unsupported "Non-trivial RHS in update"
    (* Dafny stores the RHS of a variable declaration or an update in an update statement
       The RHS in there does not get resolved though.
       Instead, the entire statement is resolved into another statement.
       So we translate that statement and then extract the RHS from it.
       If there are multiple LHSs, there should also be multiple RHSs, so we return a list.
    *)
    and rhsOfUpdate (u: UpdateStmt) : Y.UpdateRHS list =
        let doOne (s: Statement) : Y.UpdateRHS =
            match statement (s) with
            | Y.EUpdate (_, e) -> e
            | _ -> error "unexpected update"

        doOne @ u.ResolvedStatements
    (* for a monadic RHS, it's even trickier: the statement gets resolved into three statements,
       the first of which contains a variable declaration, whose update statement has the needed RHS *)
    and rhsOfMonadicUpdate (ar: AssignOrReturnStmt) : Y.UpdateRHS =
        let res = fromIList ar.ResolvedStatements

        match res with
        | [ :? VarDeclStmt as v; :? UpdateStmt as u; :? IfStmt; :? UpdateStmt ] ->
            let ds = rhsOfUpdate (u)

            if ds.Length <> 1 then
                error "Unexpected number of RHSs in update"

            let d = ds.Head.df
            let t = (boundVar @ v.Locals).Head.tp
            { df = d; monadic = Some(t) }
        | _ -> error "Unexpected resolution"