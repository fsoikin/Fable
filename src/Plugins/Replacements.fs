module Fabel.Plugins.Replacements
open Fabel
open Fabel.AST
open Fabel.AST.Fabel.Util

module private Util =
    let [<Literal>] system = "System."
    let [<Literal>] fsharp = "Microsoft.FSharp."
    let [<Literal>] genericCollections = "System.Collections.Generic."

    let inline (=>) first second = first, second

    let (|DicContains|_|) (dic: System.Collections.Generic.IDictionary<'k,'v>) key =
        let success, value = dic.TryGetValue key
        if success then Some value else None

    let (|SetContains|_|) set item =
        if Set.contains item set then Some item else None

    // The core lib expects non-curried lambdas
    let deleg = List.mapi (fun i x ->
        if i=0 then (makeDelegate x) else x)

    let instanceArgs (callee: Fabel.Expr option) (args: Fabel.Expr list) =
        match callee with
        | Some callee -> (callee, args)
        | None -> (args.Head, args.Tail)

    let staticArgs (callee: Fabel.Expr option) (args: Fabel.Expr list) =
        match callee with
        | Some callee -> callee::args
        | None -> args

module private AstPass =
    open Util
    
    let (|Null|_|) = function
        | Fabel.Value Fabel.Null -> Some null
        | _ -> None
        
    let (|TypeName|_|) (expr: Fabel.Expr) =
        match expr.Type with
        | Fabel.DeclaredType ent -> Some ent.Name
        | _ -> None

    let (|OneArg|_|) (callee: Fabel.Expr option, args: Fabel.Expr list) =
        match callee, args with None, [arg] -> Some arg | _ -> None

    let (|TwoArgs|_|) (callee: Fabel.Expr option, args: Fabel.Expr list) =
        match callee, args with None, [left;right] -> Some (left, right) | _ -> None

    let (|ThreeArgs|_|) (callee: Fabel.Expr option, args: Fabel.Expr list) =
        match callee, args with None, [arg1;arg2;arg3] -> Some (arg1, arg2, arg3) | _ -> None

    let private checkType (args: Fabel.Expr list) successContinuation =
        match args.Head.Type with
        | Fabel.UnknownType ->
            successContinuation () |> Some
        | Fabel.PrimitiveType kind ->
            match kind with
            | Fabel.Number _ | Fabel.String _ | Fabel.Boolean | Fabel.Unit ->
                successContinuation () |> Some
            | Fabel.Function _ | Fabel.Array _ | Fabel.Regex _ ->
                failwithf "Unexpected operands: %A" args
        | Fabel.DeclaredType typ ->
            None

    let unaryOp range typ args op =
        checkType args (fun () ->
            let op = Fabel.UnaryOp op |> Fabel.Value
            Fabel.Apply(op, args, Fabel.ApplyMeth, typ, range))

    let binaryOp range typ args op =
        checkType args (fun () ->
            let op = Fabel.BinaryOp op |> Fabel.Value
            Fabel.Apply(op, args, Fabel.ApplyMeth, typ, range))

    let logicalOp range typ args op =
        checkType args (fun () ->
            let op = Fabel.LogicalOp op |> Fabel.Value
            Fabel.Apply(op, args, Fabel.ApplyMeth, typ, range))
            
    let emit (i: Fabel.ApplyInfo) emit args =
        Fabel.Apply(Fabel.Emit(emit) |> Fabel.Value, args, Fabel.ApplyMeth, i.returnType, i.range)

    let emitNoInfo emit args =
        Fabel.Apply(Fabel.Emit(emit) |> Fabel.Value, args, Fabel.ApplyMeth, Fabel.UnknownType, None)
        
    let wrap typ expr =
        Fabel.Wrapped (expr, typ)

    let toString com (i: Fabel.ApplyInfo) (arg: Fabel.Expr) =
        match arg.Type with
        | Fabel.PrimitiveType (Fabel.String) ->
            arg
        | _ ->
            InstanceCall (arg, "toString", [])
            |> makeCall com i.range i.returnType

    let toInt, toFloat =
        let toNumber com (i: Fabel.ApplyInfo) typ (arg: Fabel.Expr) =
            match arg.Type with
            | Fabel.PrimitiveType Fabel.String ->
                GlobalCall ("Number", Some ("parse"+typ), false, [arg])
                |> makeCall com i.range i.returnType
            | _ ->
                if typ = "Int"
                then GlobalCall ("Math", Some "floor", false, [arg])
                     |> makeCall com i.range i.returnType
                else arg
        (fun com i arg -> toNumber com i "Int" arg),
        (fun com i arg -> toNumber com i "Float" arg)
            
    let operators com (info: Fabel.ApplyInfo) =
        // TODO: Check primitive args also here?
        let math range typ args methName =
            GlobalCall ("Math", Some methName, false, args)
            |> makeCall com range typ |> Some
        let r, typ, args = info.range, info.returnType, info.args
        match info.methodName with
        // F# Compiler actually converts all logical operations to IfThenElse expressions
        | "&&" -> logicalOp r typ args LogicalAnd
        | "||" -> logicalOp r typ args LogicalOr
        | "<>" | "neq" ->
            match args with
            | [Fabel.Value Fabel.Null; _]
            | [_; Fabel.Value Fabel.Null] -> makeEqOp r args BinaryUnequal |> Some
            | _ -> makeEqOp r args BinaryUnequalStrict |> Some
        | "=" | "eq" ->
            match args with
            | [Fabel.Value Fabel.Null; _]
            | [_; Fabel.Value Fabel.Null] -> makeEqOp r args BinaryEqual |> Some
            | _ -> makeEqOp r args BinaryEqualStrict |> Some
        | "<" | "lt" -> binaryOp r typ args BinaryLess
        | "<=" | "lte" -> binaryOp r typ args BinaryLessOrEqual
        | ">" | "gt" -> binaryOp r typ args BinaryGreater
        | ">=" | "gte" -> binaryOp r typ args BinaryGreaterOrEqual
        | "+" -> binaryOp r typ args BinaryPlus
        | "-" -> binaryOp r typ args BinaryMinus
        | "*" -> binaryOp r typ args BinaryMultiply
        | "/" -> binaryOp r typ args BinaryDivide
        | "%" -> binaryOp r typ args BinaryModulus
        | "<<<" -> binaryOp r typ args BinaryShiftLeft
        | ">>>" -> binaryOp r typ args BinaryShiftRightSignPropagating
        | "&&&" -> binaryOp r typ args BinaryAndBitwise
        | "|||" -> binaryOp r typ args BinaryOrBitwise
        | "^^^" -> binaryOp r typ args BinaryXorBitwise
        | "~~~" -> unaryOp r typ args UnaryNotBitwise
        | "not" -> unaryOp r typ args UnaryNot
        | "~-" -> unaryOp r typ args UnaryMinus
        // Math functions
        // TODO: optimize square pow: x * x
        | "pow" | "pown" | "**" -> math r typ args "pow"
        | "ceil" | "ceiling" -> math r typ args "ceil"
        | "abs" | "acos" | "asin" | "atan" | "atan2" 
        | "cos"  | "exp" | "floor" | "log" | "log10"
        | "round" | "sin" | "sqrt" | "tan" ->
            math r typ args info.methodName
        | "compare" ->
            emit info "$0 < $1 ? -1 : ($0 == $1 ? 0 : 1)" args |> Some
        // Function composition
        | ">>" | "<<" ->
            // If expression is a holder we have to protect the variable declarations
            let wrap expr placeholder =
                match expr with
                | Fabel.Sequential _ -> sprintf "(function(){return %s}())" placeholder
                | _ -> placeholder
            let args = if info.methodName = ">>" then args else List.rev args
            let f0 = wrap args.Head "$0"
            let f1 = wrap args.Tail.Head "$1"
            emit info (sprintf "x=>%s(%s(x))" f1 f0) args |> Some
        // Reference
        | "!" -> makeGet r Fabel.UnknownType args.Head (makeConst "contents") |> Some
        | ":=" -> Fabel.Set(args.Head, Some(makeConst "contents"), args.Tail.Head, r) |> Some
        | "ref" -> Fabel.ObjExpr([("contents", args.Head)], r) |> Some
        // Conversions
        | "seq" | "id" | "box" | "unbox" -> wrap typ args.Head |> Some
        | "int" -> toInt com info args.Head |> Some
        | "float" -> toFloat com info args.Head |> Some
        | "char" | "string" -> toString com info args.Head |> Some
        | "dict" | "set" ->
            let modName = if info.methodName = "dict" then "Map" else "Set"
            CoreLibCall(modName, Some "ofSeq", false, args)
            |> makeCall com r typ |> Some
        // Ignore: wrap to keep Unit type (see Fabel2Babel.transformFunction)
        | "ignore" -> Fabel.Wrapped (args.Head, Fabel.PrimitiveType Fabel.Unit) |> Some
        // Ranges
        | ".." | ".. .." ->
            let meth = if info.methodName = ".." then "range" else "rangeStep"
            CoreLibCall("Seq", Some meth, false, args)
            |> makeCall com r typ |> Some
        // Tuples
        | "fst" | "snd" ->
            if info.methodName = "fst" then 0 else 1
            |> makeConst
            |> makeGet r typ args.Head |> Some
        // Strings
        | "sprintf" | "printf" | "printfn" ->
            let emit = 
                match info.methodName with
                | "sprintf" -> "x=>x"
                | "printf" | "printfn" | _ -> "x=>{console.log(x)}"
                |> Fabel.Emit |> Fabel.Value
            Fabel.Apply(args.Head, [emit], Fabel.ApplyMeth, typ, r)
            |> Some
        // Exceptions
        | "failwith" | "failwithf" | "raise" | "invalidOp" ->
            Fabel.Throw (args.Head, r) |> Some
        | _ -> None

    let strings com (i: Fabel.ApplyInfo) =
        let icall meth =
            let c, args = instanceArgs i.callee i.args
            InstanceCall(c, meth, args)
            |> makeCall com i.range i.returnType
        match i.methodName with
        | ".ctor" ->
            CoreLibCall("String", Some "fsFormat", false, i.args)
            |> makeCall com i.range i.returnType |> Some
        | "get_Length" ->
            let c, _ = instanceArgs i.callee i.args
            makeGet i.range i.returnType c (makeConst "length") |> Some
        | "contains" ->
            makeEqOp i.range [icall "indexOf"; makeConst 0] BinaryGreaterOrEqual |> Some
        | "startsWith" ->
            makeEqOp i.range [icall "indexOf"; makeConst 0] BinaryEqualStrict |> Some
        | "substring" -> icall "substr" |> Some
        | "toUpper" -> icall "toLocaleUpperCase" |> Some
        | "toUpperInvariant" -> icall "toUpperCase" |> Some
        | "toLower" -> icall "toLocaleLowerCase" |> Some
        | "toLowerInvariant" -> icall "toLowerCase" |> Some
        | "indexOf" | "lastIndexOf" | "trim" -> icall i.methodName |> Some
        | "toCharArray" ->
            InstanceCall(i.callee.Value, "split", [makeConst ""])
            |> makeCall com i.range i.returnType |> Some
        | _ -> None

    let console com (i: Fabel.ApplyInfo) =
        match i.methodName with
        | "Write" | "WriteLine" ->
            let inner =
                CoreLibCall("String", Some "format", false, i.args)
                |> makeCall com i.range (Fabel.PrimitiveType Fabel.String)
            GlobalCall("console", Some "log", false, [inner])
            |> makeCall com i.range i.returnType |> Some
        | _ -> None

    let regex com (i: Fabel.ApplyInfo) =
        let prop p callee =
            makeGet i.range i.returnType callee (makeConst p)
        let isGroup =
            match i.callee with
            | Some (TypeName "Group") -> true
            | _ -> false
        match i.methodName with
        | ".ctor" ->
            // TODO: Use RegexConst if no options have been passed?
            CoreLibCall("RegExp", Some "create", false, i.args)
            |> makeCall com i.range i.returnType |> Some
        | "get_Options" ->
            CoreLibCall("RegExp", Some "options", false, [i.callee.Value])
            |> makeCall com i.range i.returnType |> Some
        // Capture
        | "get_Index" ->
            if isGroup
            then failwithf "Accessing index of Regex groups is not supported"
            else prop "index" i.callee.Value |> Some
        | "get_Value" ->
            if isGroup
            then i.callee.Value |> wrap i.returnType |> Some
            else prop 0 i.callee.Value |> Some
        | "get_Length" ->
            if isGroup
            then prop "length" i.callee.Value |> Some
            else prop 0 i.callee.Value |> prop "length" |> Some
        // Group
        | "get_Success" ->
            makeEqOp i.range [i.callee.Value; Fabel.Value Fabel.Null] BinaryUnequal |> Some
        // Match
        | "get_Groups" -> wrap i.returnType i.callee.Value |> Some
        // MatchCollection & GroupCollection
        | "get_Item" ->
            makeGet i.range i.returnType i.callee.Value i.args.Head |> Some
        | "get_Count" ->
            prop "length" i.callee.Value |> Some
        | _ -> None

    let intrinsicFunctions com (i: Fabel.ApplyInfo) =
        match i.methodName, (i.callee, i.args) with
        | "getString", TwoArgs (ar, idx)
        | "getArray", TwoArgs (ar, idx) ->
            makeGet i.range i.returnType ar idx |> Some
        | "setArray", ThreeArgs (ar, idx, value) ->
            Fabel.Set (ar, Some idx, value, i.range) |> Some
        | "getArraySlice", ThreeArgs (ar, lower, upper) ->
            let upper =
                match upper with
                | Null _ -> emitNoInfo "$0.length" [ar]
                | _ -> emitNoInfo "$0 + 1" [upper]
            InstanceCall (ar, "slice", [lower; upper])
            |> makeCall com i.range i.returnType |> Some
        | "setArraySlice", (None, args) ->
            CoreLibCall("Array", Some "setSlice", false, args)
            |> makeCall com i.range i.returnType |> Some
        | _ -> None

    let options com (i: Fabel.ApplyInfo) =
        let callee = match i.callee with Some c -> c | None -> i.args.Head
        match i.methodName with
        | "value" | "get" | "toObj" | "ofObj" | "toNullable" | "ofNullable" ->
           wrap i.returnType callee |> Some
        | "isSome" -> makeEqOp i.range [callee; Fabel.Value Fabel.Null] BinaryUnequal |> Some
        | "isNone" -> makeEqOp i.range [callee; Fabel.Value Fabel.Null] BinaryEqual |> Some
        | _ -> None
        
    let toList com (i: Fabel.ApplyInfo) expr =
        CoreLibCall ("Seq", Some "toList", false, [expr])
        |> makeCall com i.range i.returnType

    let toArray com (i: Fabel.ApplyInfo) expr =
        let dynamicArray =
            CoreLibCall ("Seq", Some "toArray", false, [expr])
            |> makeCall com i.range i.returnType
        match i.methodTypeArgs with
        | [Fabel.PrimitiveType(Fabel.Number numberKind)] ->
            let arrayKind = Fabel.TypedArray numberKind
            Fabel.ArrayConst(Fabel.ArrayConversion dynamicArray, arrayKind) |> Fabel.Value
        | _ -> dynamicArray

    let rawCollections com (i: Fabel.ApplyInfo) =
        match i.methodName with
        | "get_Count" ->
            CoreLibCall ("Seq", Some "length", false, [i.callee.Value])
            |> makeCall com i.range i.returnType |> Some
        | _ -> None

    let keyValuePairs com (i: Fabel.ApplyInfo) =
        let get (k: obj) =
            makeGet i.range i.returnType i.callee.Value (makeConst k) |> Some
        match i.methodName with
        | "get_Key" | "key" -> get 0
        | "get_Value" | "value" -> get 1
        | _ -> None

    let dictionaries com (i: Fabel.ApplyInfo) =
        let icall meth =
            InstanceCall (i.callee.Value, meth, i.args)
            |> makeCall com i.range i.returnType |> Some
        match i.methodName with
        | ".ctor" ->
            let makeMap args =
                GlobalCall("Map", None, true, args) |> makeCall com i.range i.returnType
            match i.args with
            | [] -> makeMap [] |> Some
            | _ ->
                match i.args.Head.Type with
                | Fabel.PrimitiveType (Fabel.Number Int32) ->
                    makeMap [] |> Some
                | _ -> makeMap i.args |> Some
        | "isReadOnly" ->
            Fabel.BoolConst false |> Fabel.Value |> Some
        | "get_Count" ->
            makeGet i.range i.returnType i.callee.Value (makeConst "size") |> Some
        | "containsValue" ->
            CoreLibCall ("Map", Some "containsValue", false, [i.args.Head; i.callee.Value])
            |> makeCall com i.range i.returnType |> Some
        | "get_Item" -> icall "get"
        | "get_Keys" -> icall "keys"
        | "get_Values" -> icall "values"
        | "containsKey" -> icall "has"
        | "clear" -> icall "clear"
        | "add" -> icall "set"
        | "remove" -> icall "delete"
        | _ -> None

    let mapAndSets com (i: Fabel.ApplyInfo) =
        let instanceArgs () =
            match i.callee with
            | Some c -> c, i.args
            | None -> List.last i.args, List.take (i.args.Length-1) i.args
        let prop (prop: string) =
            let callee, _ = instanceArgs()
            makeGet i.range i.returnType callee (makeConst prop)
        let icall meth =
            let callee, args = instanceArgs()
            InstanceCall (callee, meth, args)
            |> makeCall com i.range i.returnType
        let modName =
            if i.ownerFullName.EndsWith("Map")
            then "Map" else "Set"
        let _of colType expr =
            CoreLibCall(modName, Some ("of" + colType), false, [expr])
            |> makeCall com i.range i.returnType
        match i.methodName with
        // Instance and static shared methods
        | "add" ->
            match modName with
            | "Map" -> icall "set" |> Some
            | _ -> icall "add" |> Some
        | "count" -> prop "size" |> Some
        | "contains" | "containsKey" -> icall "has" |> Some
        | "remove" ->
            let callee, args = instanceArgs()
            CoreLibCall(modName, Some "removeInPlace", false, [args.Head; callee])
            |> makeCall com i.range i.returnType |> Some
        | "isEmpty" ->
            makeEqOp i.range [prop "size"; makeConst 0] BinaryEqualStrict |> Some
        // Map only instance and static methods
        | "tryFind" | "find" -> icall "get" |> Some
        | "item" -> icall "get" |> Some
        // Set only instance and static methods
        // | "isProperSubsetOf" -> failwith "TODO"
        // | "isProperSupersetOf" -> failwith "TODO"
        // | "isSubsetOf" -> failwith "TODO"
        // | "isSupersetOf" -> failwith "TODO"
        // | "maximumElement" | "maxElement" -> failwith "TODO"
        // | "minimumElement" | "minElement" -> failwith "TODO"
        // Set only static methods
        // | "+" | "-" -> failwith "TODO"        
        // Constructors
        | "empty" ->
            GlobalCall(modName, None, true, [])
            |> makeCall com i.range i.returnType |> Some
        | ".ctor" ->
            CoreLibCall(modName, Some "ofSeq", false, i.args)
            |> makeCall com i.range i.returnType |> Some
        // Conversions
        | "toArray" -> toArray com i i.args.Head |> Some
        | "toList" -> toList com i i.args.Head |> Some
        | "toSeq" -> Some i.args.Head
        | "ofArray" -> _of "Array" i.args.Head |> Some
        | "ofList" | "ofSeq" -> _of "Seq" i.args.Head |> Some
        // Non-build static methods shared with Seq
        | "exists" | "fold" | "foldBack" | "forall" | "iter" ->
            let modName = if modName = "Map" then "Map" else "Seq"
            CoreLibCall(modName, Some i.methodName, false, deleg i.args)
            |> makeCall com i.range i.returnType |> Some
        // Build static methods shared with Seq
        | "filter" | "map" ->
            match modName with
            | "Map" ->
                CoreLibCall(modName, Some i.methodName, false, deleg i.args)
                |> makeCall com i.range i.returnType |> Some
            | _ ->
                CoreLibCall("Seq", Some i.methodName, false, deleg i.args)
                |> makeCall com i.range i.returnType
                |> _of "Seq" |> Some
        // Static method
        | "partition" ->
            CoreLibCall(modName, Some i.methodName, false, deleg i.args)
            |> makeCall com i.range i.returnType |> Some
        // Map only static methods (make delegate)
        | "findKey" | "tryFindKey" | "pick" | "tryPick" ->
            CoreLibCall("Map", Some i.methodName, false, deleg i.args)
            |> makeCall com i.range i.returnType |> Some
        // Set only static methods
        // | "singleton" -> failwith "TODO"
        // | "difference" -> failwith "TODO"
        // | "intersect" -> failwith "TODO"
        // | "intersectMany" -> failwith "TODO"
        // | "union" -> failwith "TODO"
        // | "unionMany" -> failwith "TODO"
        | _ -> None

    type CollectionKind =
        | Seq | List | Array
    
    // Functions which don't return a new collection of the same type
    let implementedSeqNonBuildFunctions =
        set [ "average"; "averageBy"; "countBy"; "compareWith"; "empty";
              "exactlyOne"; "exists"; "exists2"; "fold"; "fold2"; "foldBack"; "foldBack2";
              "forall"; "forall2"; "head"; "item"; "iter"; "iteri"; "iter2"; "iteri2";
              "isEmpty"; "last"; "length"; "max"; "maxBy"; "min"; "minBy";
              "reduce"; "reduceBack"; "sum"; "sumBy"; "tail"; "toArray"; "toList";
              "tryFind"; "find"; "tryFindIndex"; "findIndex"; "tryPick"; "pick"; "unfold" ]

    // Functions that must return a collection of the same type
    let implementedSeqBuildFunctions =
        set [ "append"; "choose"; "collect"; "concat"; "distinctBy"; "distinctBy";
              "filter"; "where"; "groupBy"; "init";
              "map"; "mapi"; "map2"; "mapi2"; "map3";
              "ofArray"; "ofList"; "pairwise"; "permute"; "replicate"; "rev";
              "scan"; "scanBack"; "singleton"; "skip"; "skipWhile";
              "take"; "takeWhile"; "sort"; "sortBy"; "sortWith";
              "sortDescending"; "sortByDescending"; "zip"; "zip3" ]

    let implementedListFunctions =
        set [ "append"; "choose"; "collect"; "concat"; "filter"; "where";
              "init"; "map"; "mapi"; "ofArray"; "partition";
              "replicate"; "rev"; "singleton"; "unzip"; "unzip3" ]

    let implementedArrayFunctions =
        set [ "partition"; "permute"; "sortInPlaceBy"; "unzip"; "unzip3" ]
        
    let nativeArrayFunctions =
        dict [ "exists" => "some"; "filter" => "filter";
               "find" => "find"; "findIndex" => "findIndex"; "forall" => "every";
               "indexed" => "entries"; "iter" => "forEach"; "map" => "map";
               "reduce" => "reduce"; "reduceBack" => "reduceRight";
               "sortInPlace" => "sort"; "sortInPlaceWith" => "sort" ]

    let collectionsSecondPass com (i: Fabel.ApplyInfo) kind =
        let prop (meth: string) callee =
            makeGet i.range i.returnType callee (makeConst meth)
        let icall meth (callee, args) =
            InstanceCall (callee, meth, args)
            |> makeCall com i.range i.returnType
        let ccall modName meth args =
            CoreLibCall (modName, Some meth, false, args)
            |> makeCall com i.range i.returnType
        let meth, c, args =
            i.methodName, i.callee, i.args
        match meth with
        // Deal with special cases first
        // | "sum" | "sumBy" -> // TODO: Check if we need to use a custom operator
        | "cast" -> Some i.args.Head // Seq only, erase
        | "isEmpty" ->
            match kind with
            | Seq -> ccall "Seq" meth args
            | Array ->
                makeEqOp i.range [prop "length" args.Head; makeConst 0] BinaryEqualStrict
            | List ->
                let c, _ = instanceArgs c args
                makeEqOp i.range [prop "tail" c; Fabel.Value Fabel.Null] BinaryEqual
            |> Some
        | "head" | "tail" | "length" | "get_Length" | "get_Count" ->
            match kind with
            | Seq -> ccall "Seq" meth (staticArgs c args)
            | List -> let c, _ = instanceArgs c args in prop meth c
            | Array ->
                let c, _ = instanceArgs c args
                if meth = "head" then makeGet i.range i.returnType c (makeConst 0)
                elif meth = "tail" then icall "slice" (c, [makeConst 1])
                else prop "length" c
            |> Some
        | "item" ->
            match kind with
            | Seq -> ccall "Seq" meth args
            | Array -> makeGet i.range i.returnType args.Tail.Head args.Head
            | List -> match i.callee with Some x -> i.args@[x] | None -> i.args
                      |> ccall "Seq" meth
            |> Some
        | "get_Item" ->
            makeGet i.range i.returnType i.callee.Value i.args.Head |> Some
        | "set_Item" ->
            Fabel.Set (i.callee.Value, Some i.args.Head, i.args.Tail.Head, i.range) |> Some
        | "sort" ->
            match c, kind with
            | Some c, _ -> icall "sort" (c, deleg args)
            | None, Seq -> ccall "Seq" meth (deleg args)
            | None, List -> ccall "Seq" meth (deleg args) |> toList com i
            | None, Array -> ccall "Seq" meth (deleg args) |> toArray com i
            |> Some
        // Constructors ('cons' only applies to List)
        | "empty" | "cons" ->
            match kind with
            | Seq -> ccall "Seq" meth args
            | Array ->
                match i.returnType with
                | Fabel.PrimitiveType (Fabel.Array kind) ->
                    Fabel.ArrayConst (Fabel.ArrayAlloc (makeConst 0), kind) |> Fabel.Value
                | _ -> failwithf "Expecting array type but got %A" i.returnType
            | List -> CoreLibCall ("List", None, true, args)
                      |> makeCall com i.range i.returnType
            |> Some
        | "zeroCreate" ->
            match i.methodTypeArgs with
            | [Fabel.PrimitiveType(Fabel.Number numberKind)] ->
                Fabel.ArrayConst(Fabel.ArrayAlloc i.args.Head, Fabel.TypedArray numberKind)
                |> Fabel.Value |> Some
            | _ -> failwithf "Unexpected arguments for Array.zeroCreate: %A" i.args
        // ResizeArray only
        | ".ctor" ->
            let makeEmptyArray () =
                (Fabel.ArrayValues [], Fabel.DynamicArray)
                |> Fabel.ArrayConst |> Fabel.Value |> Some
            match i.args with
            | [] -> makeEmptyArray ()
            | _ ->
                match i.args.Head.Type with
                | Fabel.PrimitiveType (Fabel.Number Int32) ->
                    makeEmptyArray ()
                | _ -> ccall "Seq" "toArray" i.args |> Some
        | "add" ->
            icall "push" (c.Value, args) |> Some
        | "addRange" ->
            ccall "Array" "addRangeInPlace" [args.Head; c.Value] |> Some
        | "clear" ->
            icall "splice" (c.Value, [makeConst 0]) |> Some
        | "contains" ->
            emit i "$0.indexOf($1) > -1" (c.Value::args) |> Some
        | "indexOf" ->
            icall "indexOf" (c.Value, args) |> Some
        | "insert" ->
            icall "splice" (c.Value, [args.Head; makeConst 0; args.Tail.Head]) |> Some
        | "remove" ->
            ccall "Array" "removeInPlace" [args.Head; c.Value] |> Some
        | "removeAt" ->
            icall "splice" (c.Value, [args.Head; makeConst 1]) |> Some
        | "reverse" ->
            icall "reverse" (c.Value, []) |> Some
        // Conversions
        | "toSeq" | "ofSeq" ->
            let meth =
                match kind with
                | Seq -> failwithf "Unexpected method called on seq %s in %A" meth i.range
                | List -> if meth = "toSeq" then "ofList" else "toList"
                | Array -> if meth = "toSeq" then "ofArray" else "toArray"
            ccall "Seq" meth args |> Some
        // Default to Seq implementation in core lib
        | SetContains implementedSeqNonBuildFunctions meth ->
            ccall "Seq" meth (deleg args) |> Some
        | SetContains implementedSeqBuildFunctions meth ->
            match kind with
            | Seq -> ccall "Seq" meth (deleg args)
            | List -> ccall "Seq" meth (deleg args) |> toList com i
            | Array -> ccall "Seq" meth (deleg args) |> toArray com i
            |> Some
        | _ -> None
        
    let collectionsFirstPass com (i: Fabel.ApplyInfo) kind =
        match kind with
        | List ->
            match i.methodName with
            | "getSlice" ->
                InstanceCall (i.callee.Value, "slice", i.args) |> Some
            | SetContains implementedListFunctions meth ->
                CoreLibCall ("List", Some meth, false, deleg i.args) |> Some
            | _ -> None
        | Array ->
            match i.methodName with
            | "take" ->
                InstanceCall (i.args.Tail.Head, "slice", [makeConst 0; i.args.Head]) |> Some
            | "skip" ->
                InstanceCall (i.args.Tail.Head, "slice", [i.args.Head]) |> Some
            | SetContains implementedArrayFunctions meth ->
                CoreLibCall ("Array", Some meth, false, deleg i.args) |> Some
            | DicContains nativeArrayFunctions meth ->
                let revArgs = List.rev i.args
                InstanceCall (revArgs.Head, meth, deleg (List.rev revArgs.Tail)) |> Some
            | _ -> None
        | _ -> None
        |> function
            | Some callKind -> makeCall com i.range i.returnType callKind |> Some
            | None -> collectionsSecondPass com i kind

    let asserts com (i: Fabel.ApplyInfo) =
        match i.methodName with
        | "areEqual" ->
            ImportCall("assert", true, None, Some "equal", false, i.args)
            |> makeCall com i.range i.returnType |> Some
        | _ -> None
        
    let exceptions com (i: Fabel.ApplyInfo) =
        match i.methodName with
        // TODO: Constructor with inner exception
        | ".ctor" -> Some i.args.Head
        | "get_Message" -> i.callee
        | _ -> None

    let objects com (i: Fabel.ApplyInfo) =
        match i.methodName with
        | ".ctor" -> Fabel.ObjExpr ([], i.range) |> Some
        | "toString" -> toString com i i.callee.Value |> Some
        | _ -> failwithf "TODO: Object method: %s" i.methodName

    let tryReplace com (info: Fabel.ApplyInfo) =
        match info.ownerFullName with
        | "System.String"
        | "Microsoft.FSharp.Core.String"
        | "Microsoft.FSharp.Core.PrintfFormat" -> strings com info
        | "System.Console"
        | "System.Diagnostics.Debug" -> console com info
        | "Microsoft.FSharp.Core.Option" -> options com info
        | "System.Object" -> objects com info
        | "System.Exception" -> exceptions com info
        | "System.Math"
        | "Microsoft.FSharp.Core.Operators"
        | "Microsoft.FSharp.Core.ExtraTopLevelOperators" -> operators com info
        | "IntrinsicFunctions"
        | "OperatorIntrinsics" -> intrinsicFunctions com info
        | "System.Text.RegularExpressions.Capture"
        | "System.Text.RegularExpressions.Match"
        | "System.Text.RegularExpressions.Group"
        | "System.Text.RegularExpressions.MatchCollection"
        | "System.Text.RegularExpressions.GroupCollection"
        | "System.Text.RegularExpressions.Regex" -> regex com info
        | "System.Collections.Generic.Dictionary"
        | "System.Collections.Generic.IDictionary" -> dictionaries com info
        | "System.Collections.Generic.KeyValuePair" -> keyValuePairs com info 
        | "KeyCollection" | "ValueCollection"
        | "System.Collections.Generic.ICollection" -> rawCollections com info
        | "System.Array"
        | "System.Collections.Generic.List"
        | "System.Collections.Generic.IList" -> collectionsSecondPass com info Array
        | "Microsoft.FSharp.Collections.Array" -> collectionsFirstPass com info Array
        | "Microsoft.FSharp.Collections.List" -> collectionsFirstPass com info List
        | "Microsoft.FSharp.Collections.Seq" -> collectionsSecondPass com info Seq
        | "Microsoft.FSharp.Collections.Map"
        | "Microsoft.FSharp.Collections.Set" -> mapAndSets com info
        | "NUnit.Framework.Assert" -> asserts com info
        | _ -> None

module private CoreLibPass =
    open Util

    type MapKind = Static | Both

    // TODO: Decimal
    let mappings =
        dict [
            // system + "Random" => ("Random", Both)
            // system + "DateTime" => ("Time", Static)
            // system + "TimeSpan" => ("Time", Static)
            fsharp + "Control.Async" => ("Async", Both)
            fsharp + "Control.AsyncBuilder" => ("Async", Both)
            fsharp + "Core.CompilerServices.RuntimeHelpers" => ("Seq", Static)
            system + "String" => ("String", Static)
            fsharp + "Core.String" => ("String", Static)
            system + "Text.RegularExpressions.Regex" => ("RegExp", Static)
            fsharp + "Collections.Seq" => ("Seq", Static)
        ]

open Util

// TODO: Constructors
let private coreLibPass com (info: Fabel.ApplyInfo) =
    match info.ownerFullName with
    | DicContains CoreLibPass.mappings (modName, kind) ->
        match kind, info.callee with
        | CoreLibPass.Both, Some callee -> 
            InstanceCall (callee, info.methodName, info.args)
            |> makeCall com info.range info.returnType
        | _ ->
            CoreLibCall(modName, Some info.methodName, false, staticArgs info.callee info.args)
            |> makeCall com info.range info.returnType
        |> Some
    | _ -> None

let tryReplace (com: ICompiler) (info: Fabel.ApplyInfo) =
    match AstPass.tryReplace com info with
    | Some res -> Some res
    | None -> coreLibPass com info