// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2015 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

module WebSharper.Core.JavaScript.Writer


module S = Syntax
type StringBuilder = System.Text.StringBuilder
type StringWriter = System.IO.StringWriter
type TextWriter = System.IO.TextWriter

type Layout =
    | Empty
    | Token of string
    | Word of string
    | Horizontal of Layout * Layout
    | Vertical of Layout * Layout
    | Indent of Layout
    | SourceMapping of S.SourcePos
    | SourceMappingEnd of S.SourcePos
    | SourceName of string

let inline ( ++ ) a b = Horizontal (a, b)
let inline ( -- ) a b = Vertical (a, b)

type Associativity =
    | LeftAssociative
    | NonAssociative
    | RightAssociative

type B = S.BinaryOperator

let BinaryOperatorPrecedence operator =
    match operator with
    | B.``.`` -> 1
    | B.``*`` | B.``/`` | B.``%`` -> 5
    | B.``+`` | B.``-`` -> 6
    | B.``<<`` | B.``>>`` | B.``>>>`` -> 7
    | B.``<`` | B.``<=`` | B.``>`` | B.``>=``
    | B.``in`` | B.``instanceof`` -> 8
    | B.``==`` | B.``!=`` | B.``===`` | B.``!==`` -> 9
    | B.``&`` -> 10
    | B.``^`` -> 11
    | B.``|`` -> 12
    | B.``&&`` -> 13
    | B.``||`` -> 14
    | B.``,`` -> 17
    | _ -> 16

let BinaryOperatorAssociativity operator =
    match operator with
    | B.``=`` | B.``+=`` | B.``-=`` | B.``*=`` | B.``/=`` | B.``%=``
    | B.``<<=`` | B.``>>=`` | B.``>>>=`` | B.``&=`` | B.``^=`` | B.``|=`` ->
        RightAssociative
    | _ ->
        LeftAssociative

[<Literal>]
let PrefixOperatorPrecedence =
    4

let PrefixOperatorAssociativity operator =
    match operator with
    | S.UnaryOperator.``++``
    | S.UnaryOperator.``--`` -> NonAssociative
    | _ -> RightAssociative

[<Literal>]
let PostfixOperatorPrecedence =
    4

let PostfixOperatorAssociativity =
    NonAssociative

let Precedence expression =
    match expression with
    | S.Application _ -> 2
    | S.Binary (_, op, _) -> BinaryOperatorPrecedence op
    | S.Conditional _ -> 15
    | S.New _ -> 1
    | S.Postfix (_, op) -> PostfixOperatorPrecedence
    | S.Unary (op, _) -> PrefixOperatorPrecedence
    | _ -> 0

let Associativity expression =
    match expression with
    | S.Application _ -> LeftAssociative
    | S.Binary (_, op, _) -> BinaryOperatorAssociativity op
    | S.Conditional _ -> RightAssociative
    | S.New _ -> RightAssociative
    | S.Postfix (_, _) -> PostfixOperatorAssociativity
    | S.Unary (op, _) -> PrefixOperatorAssociativity op
    | _ -> NonAssociative

let (|QualifiedName|_|) expression =
    let rec loop acc = function
        | S.Binary (x, B.``.``, S.Constant (S.String y)) ->
            loop (y :: acc) x
        | S.Var _ | S.This as expr ->
            Some (expr, List.rev acc)
        | _ ->
            None
    loop [] expression

let BuildString (buf: StringBuilder) =
    let s = buf.ToString()
    buf.Remove(0, s.Length) |> ignore
    s

let IsKeyword word =
    match word with
    | "break"
    | "case"
    | "catch"
    | "class"
    | "const"
    | "continue"
    | "debugger"
    | "default"
    | "delete"
    | "do"
    | "else"
    | "enum"
    | "export"
    | "extends"
    | "finally"
    | "for"
    | "function"
    | "if"
    | "implements"
    | "import"
    | "in"
    | "instanceof"
    | "interface"
    | "let"
    | "new"
    | "package"
    | "private"
    | "protected"
    | "public"
    | "return"
    | "static"
    | "super"
    | "switch"
    | "this"
    | "throw"
    | "try"
    | "typeof"
    | "var"
    | "void"
    | "while"
    | "with"
    | "yield" ->
        true
    | _ ->
        false

let WriteUnicodeEscape (buf: StringBuilder) (c: char) =
    buf.AppendFormat(@"\u{0:x4}", int c)
    |> ignore

let EscapeId (buf: StringBuilder) (id: string) =
    if System.String.IsNullOrEmpty id then
        invalidArg "id" "Cannot escape null and empty identifiers."
    let isFirst = function
        | '_' | '$' -> true
        | c when System.Char.IsLetter c -> true
        | _ -> false
    let isFollow c =
        isFirst c || System.Char.IsDigit c
    let writeChar i =
        let c = id.[i]
        if isFollow c then
            buf.Append c |> ignore
        else
            WriteUnicodeEscape buf c
    match id.[0] with
    | k when not (isFirst k) || IsKeyword id ->
        WriteUnicodeEscape buf id.[0]
        for i in 1 .. id.Length - 1 do
            writeChar i
    | _ ->
        for i in 0 .. id.Length - 1 do
            writeChar i
    BuildString buf

let QuoteString (buf: StringBuilder) (s: string) =
    buf.Append '"' |> ignore
    for c in s do
        match c with
        | '\r' ->
            buf.Append "\\r" |> ignore
        | '\n' ->
            buf.Append "\\n" |> ignore
        | '\\' | '"' ->
            buf.Append '\\' |> ignore
            buf.Append c |> ignore
        | c ->
            if int c < 256 && not (System.Char.IsControl c) then
                buf.Append c |> ignore
            else
                WriteUnicodeEscape buf c
    buf.Append '"' |> ignore
    BuildString buf

let ListLayout separator brush items =
    match items with
    | [] -> Empty
    | _ -> Seq.map brush items
            |> Seq.reduce (fun a b -> separator a b)

let CommaSeparated brush items =
    ListLayout (fun a b -> a ++ Token "," ++ b) brush items

let Parens layout =
    Token "(" ++ layout ++ Token ")"

let Id (buf: StringBuilder) (id: string) =
    Word (EscapeId buf id)

let Optional f layout =
    match layout with
    | None -> Empty
    | Some x -> f x

let BlockLayout items =
    Token "{"
    -- ListLayout (fun a b -> a -- b) Indent items
    -- Token "}"

let rec Expression (buf: StringBuilder) expression =
    match expression with
    | S.ExprPos (x, pos) -> 
        SourceMapping pos ++ Expression buf x ++ SourceMappingEnd pos
    | S.Application (f, xs) ->
        MemberExpression buf f
        ++ Parens (CommaSeparated (AssignmentExpression buf) xs)
    | S.NewArray xs ->
        let element = function
            | None -> Token ","
            | Some x -> AssignmentExpression buf x
        Token "[" ++ CommaSeparated element xs ++ Token "]"
    | S.Binary (x, op, y) ->
        match op, y with
        | B.``.``, S.Constant (S.String y) when Identifier.IsValid y ->
            let e = Expression buf x
            let eL =
                match x with
                | S.Constant (S.Number _) -> Parens e
                | S.Application _
                | S.Binary (_, B.``.``, _)
                | S.This
                | S.Var _
                | S.Constant _
                | S.NewArray _
                | S.NewObject _ -> e
                | _ -> Parens e
            eL ++ Token "." ++ Word y
        | B.``.``, _ ->
            MemberExpression buf x ++
            Token "[" ++ Expression buf y ++ Token "]"
        | _ ->
            let p = BinaryOperatorPrecedence op
            let (pL, pR) =
                match BinaryOperatorAssociativity op with
                | LeftAssociative -> (p + 1, p)
                | RightAssociative -> (p, p + 1)
                | NonAssociative -> (p, p)
            ParensExpression buf pL x
            ++ match op with
               | B.``in`` | B.``instanceof`` -> Word (string op)
               | _ -> Token (string op)
            ++ ParensExpression buf pR y
    | S.Constant x ->
        match x with
        | S.Null -> Word "null"
        | S.True -> Word "true"
        | S.False -> Word "false"
        | S.Number x -> Word x
        | S.String x -> Token (QuoteString buf x)
    | S.Conditional (a, b, c) ->
        LogicalOrExpression buf a
        ++ Token "?"
        ++ AssignmentExpression buf b
        ++ Token ":"
        ++ AssignmentExpression buf c
    | S.VarNamed (x, n) ->
        SourceName n ++ Word (EscapeId buf x)
    | S.Var x ->
        Word (EscapeId buf x)
    | S.Lambda (name, formals, body) ->
        Word "function"
        ++ Optional (Id buf) name
        ++ Parens (CommaSeparated (Id buf) formals)
        -- BlockLayout (List.map (Element buf) body)
    | S.New (x, xs) ->
        Word "new"
        ++ MemberExpression buf x
        ++ Parens (CommaSeparated (AssignmentExpression buf) xs)
    | S.NewObject [] ->
        Token "{}"
    | S.NewObject fields ->
        let pair (k, v) =
            Token (if Identifier.IsValid k then k else QuoteString buf k)
            ++ Token ":"
            ++ AssignmentExpression buf v
        Token "{"
        -- Indent (ListLayout (fun a b -> a ++ Token "," -- b) pair fields)
        -- Token "}"
    | S.Postfix (x, op) ->
        ParensExpression buf PostfixOperatorPrecedence x
        ++ Token (string op)
    | S.Unary (op, x) ->
        match op with
        | S.UnaryOperator.``delete``
        | S.UnaryOperator.``typeof``
        | S.UnaryOperator.``void`` -> Word (string op)
        | _ -> Token (string op)
        ++ ParensExpression buf PrefixOperatorPrecedence x
    | S.NewRegex x ->
        Token (string x)
    | S.This ->
        Word "this"
    | _ ->
        failwith "Syntax.Expression not recognized"

and Statement (buf: StringBuilder) statement =
    match statement with
    | S.Block ss ->
        BlockLayout (List.map (Statement buf) ss)
    | S.Break id ->
        Word "break" ++ Optional (Id buf) id ++ Token ";"
    | S.Continue id ->
        Word "continue" ++ Optional (Id buf) id ++ Token ";"
    | S.Debugger ->
        Word "debugger" ++ Token ";"
    | S.Do (s, e) ->
        Word "do"
        ++ Statement buf s
        ++ Word "while"
        ++ Parens (Expression buf e)
        ++ Token ";"
    | S.Empty ->
        Token ";"
    | S.Ignore e ->
        let rec dangerous = function
            | S.Lambda _ | S.NewObject _ -> true
            | S.Application (x, _)
            | S.Binary (x, _, _)
            | S.Conditional (x, _, _)
            | S.Postfix (x, _) -> dangerous x
            | _ -> false
        if dangerous e then
            Parens (Expression buf e) ++ Token ";"
        else
            Expression buf e ++ Token ";"
    | S.For (e1, e2, e3, body) ->
        Word "for"
        ++ Parens (Optional (ExpressionNoIn buf) e1
                   ++ Token ";"
                   ++ Optional (Expression buf) e2
                   ++ Token ";"
                   ++ Optional (Expression buf) e3)
        ++ Statement buf body
    | S.ForVars (vs, e1, e2, body) ->
        Word "for"
        ++ Parens (Word "var"
                   ++ VarsNoIn buf vs
                   ++ Token ";"
                   ++ Optional (Expression buf) e1
                   ++ Token ";"
                   ++ Optional (Expression buf) e2)
        ++ Statement buf body
    | S.ForIn (e1, e2, body) ->
        Word "for"
        ++ Parens (LeftHandSideExpression buf e1
                   ++ Word "in"
                   ++ Expression buf e2)
        ++ Statement buf body
    | S.ForVarIn (id, e1, e2, body) ->
        Word "for"
        ++ Parens (Word "var"
                   ++ Id buf id
                   ++ (e1 |> Optional (fun x ->
                       Token "=" ++ AssignmentExpressionNoIn buf x))
                   ++ Word "in"
                   ++ Expression buf e2)
        ++ Statement buf body
    | S.If (e, s, S.Empty) ->
        Word "if"
        ++ Parens (Expression buf e)
        -- Indent (Statement buf s)
    | S.If (e, s1, s2) ->
        let rec isEmpty s =
            match s with
            | S.Empty -> true
            | S.Block ss -> List.forall isEmpty ss
            | _ -> false
        let s2L =
            if isEmpty s2 then Empty else
                Word "else"
                -- Indent (Statement buf s2)
        Word "if"
        ++ Parens (Expression buf e)
        -- Indent (Statement buf s1)
        -- s2L
    | S.Labelled (label, s) ->
        Id buf label ++ Token ":" ++ Statement buf s
    | S.Return e ->
        Word "return"
        ++ Optional (Expression buf) e
        ++ Token ";"
    | S.Switch (e, cases) ->
        Word "switch"
        ++ Parens (Expression buf e)
        -- BlockLayout [
            for c in cases do
                match c with
                | S.Default ss ->
                    yield Word "default" ++ Token ":"
                    for s in ss do
                        yield Indent (Statement buf s)
                | S.Case (e, ss)->
                    yield Word "case" ++ Expression buf e ++ Token ":"
                    for s in ss do
                        yield Indent (Statement buf s)
           ]
    | S.Throw e ->
        Word "throw" ++ Expression buf e ++ Token ";"
    | S.TryWith (s1, id, s2, f) ->
        Word "try"
        -- Block buf s1
        -- Word "catch" ++ Parens (Id buf id)
        -- Block buf s2
        -- Optional (fun x -> Word "finally" ++ Block buf x) f
    | S.TryFinally (s1, s2) ->
        Word "try"
        -- Block buf s1
        -- Word "finally"
        -- Block buf s2
    | S.Vars [] ->
        Empty
    | S.Vars vs ->
        Word "var" ++ Vars buf vs ++ Token ";"
    | S.While (e, s) ->
        Word "while" ++ Parens (Expression buf e)
        -- Indent (Statement buf s)
    | S.With (e, s) ->
        Word "with" ++ Parens (Expression buf e) ++ Statement buf s

and Element (buf: StringBuilder) elem =
    match elem with
    | S.Function (name, formals, body) ->
        Word "function"
        ++ Id buf name
        ++ Parens (CommaSeparated (Id buf) formals)
        -- BlockLayout (List.map (Element buf) body)
    | S.Action s ->
        Statement buf s

and Block buf statement =
    match statement with
    | S.Block ss -> List.map (Statement buf) (Seq.toList ss)
    | s -> [Statement buf s]
    |> BlockLayout

and AssignmentExpression buf expression =
    let prec = BinaryOperatorPrecedence B.``,``
    ParensExpression buf prec expression

and LeftHandSideExpression buf expression =
    match expression with
    | S.Application _
    | S.New _
    | _ when Precedence expression <= BinaryOperatorPrecedence B.``.`` ->
        Expression buf expression
    | _ ->
        Parens (Expression buf expression)

and LogicalOrExpression buf expression =
    let prec = BinaryOperatorPrecedence B.``||``
    ParensExpression buf (prec + 1) expression

and MemberExpression buf expression =
    let prec = BinaryOperatorPrecedence B.``.``
    ParensExpression buf (prec + 1) expression

and ParensExpression buf level expression =
    if Precedence expression >= level then
        Parens (Expression buf expression)
    else
        Expression buf expression

and VarsGeneric expr buf vars =
    let init x = Token "=" ++ expr buf x
    let var (x, y) = Id buf x ++ Optional init y
    CommaSeparated var vars

and AssignmentExpressionNoIn buf expression =
    if HasIn expression then
        Parens (Expression buf expression)
    else
        AssignmentExpression buf expression

and ExpressionNoIn buf expression =
    if HasIn expression then
        Parens (Expression buf expression)
    else
        Expression buf expression

and HasIn expression =
    match expression with
    | S.Binary (a, op, b) ->
        match op with
        | B.``in`` -> true
        | _ -> HasIn a || HasIn b
    | S.Conditional (a, b, c) ->
        HasIn a || HasIn b || HasIn c
    | _ ->
        false

and Vars = VarsGeneric AssignmentExpression
and VarsNoIn = VarsGeneric AssignmentExpressionNoIn

type Atom =
    | T of string
    | W of string
    | P of S.SourcePos
    | E of S.SourcePos
    | N of string

type Line =
    {
        Indent : int
        Atoms : list<Atom>
    }

let Simplify layout =
    let pair f a b =
        match a, b with
        | Empty, x | x, Empty -> x
        | _ -> f (a, b)
    let rec simp = function
        | Horizontal (a, b) -> pair Horizontal (simp a) (simp b)
        | Vertical (a, b) -> pair Vertical (simp a) (simp b)
        | Indent Empty -> Empty
        | Indent x -> Indent (simp x)
        | x -> x
    simp layout

let ToLines mode layout =
    let empty = { Indent = 0; Atoms = [] }
    let append level atom lines =
        match lines with
        | [] ->
            [{Indent = level; Atoms = [atom]}]
        | l :: ls ->
            let indent = max l.Indent level
            let l =
                {
                    Atoms = atom :: l.Atoms
                    Indent = indent
                }
            l :: ls
    let rec lines level tail = function
        | Empty ->
            tail
        | Horizontal (a, b) ->
            lines level (lines level tail b) a
        | Vertical (a, b) ->
            match mode with
            | Readable -> lines level (empty :: lines level tail b) a
            | Compact -> lines level (lines level tail b) a
        | Indent layout ->
            lines (level + 1) tail layout
        | Word x ->
            append level (W x) tail
        | Token x ->
            append level (T x) tail
        | SourceMapping p ->
            append level (P p) tail
        | SourceMappingEnd p ->
            append level (E p) tail
        | SourceName n ->
            append level (N n) tail
    lines 0 [] (Simplify layout)

type CodeMapping =
    {
        OutputLine : int
        OutputColumn : int
        SourcePos : S.SourcePos
    }

let base64Digits =
    lazy [|
        yield! { 'A' .. 'Z' }
        yield! { 'a' .. 'z' }
        yield! { '0' .. '9' }
        yield '+'
        yield '/'
    |]
    
let encodeBase64VLQ value (builder: StringBuilder) =
    let base64Digits = base64Digits.Value
    let mutable v = abs value

    let mutable digit = (v &&& 0b00001111) <<< 1
    if value < 0 then digit <- digit + 1 
    v <- v >>> 4
    if v > 0 then digit <- digit ||| 0b00100000
    builder.Append base64Digits.[digit] |> ignore

    while v > 0 do
        let mutable digit = v &&& 0b00011111    
        v <- v >>> 5
        if v > 0 then
            digit <- digit ||| 0b00100000
        builder.Append base64Digits.[digit] |> ignore

type CodeWriter(?assemblyName: string) =
    let code = StringBuilder()
    let mappings = StringBuilder()
    let sourceMap = Option.isSome assemblyName
    let mutable insertComma = false
    let mutable colFromLastMapping = 0
    let sources = ResizeArray()
    let sourcesDict = System.Collections.Generic.Dictionary()
    let names = ResizeArray()
    let namesDict = System.Collections.Generic.Dictionary()

    let mutable lastFileName = ""
    let mutable lastFileIndex = 0
    let mutable lastSourceLine = 0
    let mutable lastSourceColumn = 0
    let mutable lastNameIndex = 0

    member this.Write(s: string) =
        code.Append s |> ignore
        if sourceMap then
            colFromLastMapping <- colFromLastMapping + s.Length

    member this.Write(s: char) =
        code.Append s |> ignore
        if sourceMap then
            colFromLastMapping <- colFromLastMapping + 1

    member this.WriteLine() =
        code.AppendLine() |> ignore
        if sourceMap then
            mappings.Append ';' |> ignore
            insertComma <- false
            colFromLastMapping <- 0

    member this.AddCodeMapping(pos : S.SourcePos, start: bool, ?name : string) =
        if sourceMap then
            if insertComma then
                mappings.Append ',' |> ignore
            else
                insertComma <- true

            mappings |> encodeBase64VLQ colFromLastMapping
            colFromLastMapping <- 0

            let fileName = pos.File
            if lastFileName = fileName then
                mappings.Append 'A' |> ignore
            else
                let fileIndex =
                    match sourcesDict.TryGetValue fileName with
                    | true, i ->  i
                    | _ ->
                        let i = sources.Count
                        sources.Add(fileName, pos.Assembly)
                        sourcesDict.Add(fileName, i)   
                        i
        
                mappings |> encodeBase64VLQ (fileIndex - lastFileIndex)   
                lastFileIndex <- fileIndex   
                lastFileName <- fileName
        
            let sourceLine = if start then pos.Line - 1 else pos.EndLine - 1
            mappings |> encodeBase64VLQ (sourceLine - lastSourceLine)
            lastSourceLine <- sourceLine
        
            let sourceColumn = if start then pos.Column else pos.EndColumn
            mappings |> encodeBase64VLQ (sourceColumn - lastSourceColumn)
            lastSourceColumn <- sourceColumn

            match name with
            | Some name ->
                let nameIndex =
                    match namesDict.TryGetValue name with
                    | true, i -> i
                    | _ ->
                        let i = names.Count
                        names.Add name
                        namesDict.Add(name, i)
                        i
                mappings |> encodeBase64VLQ (nameIndex - lastNameIndex)
                lastNameIndex <- nameIndex
            | _ -> ()

    member this.GetCodeFile() = string code

    override this.ToString() = string code

    member this.GetMapFile() =
        if sources.Count = 0 then None else
        let mapFile = StringBuilder()
        let inline mapC (c: char) = mapFile.Append c |> ignore 
        let inline mapS (s: string) = mapFile.Append s |> ignore 
        let inline mapN (s: string) = mapFile.AppendLine s |> ignore 

        mapN "{"
        mapN "\"version\": 3,"
        mapN "\"sourceRoot\": \"FSharpSource\","
        mapS "\"sources\": [\""
        let im = sources.Count - 1
        for i = 0 to im do
            let file, assembly = sources.[i]
            mapS assembly
            mapC '/'
            mapS (System.IO.Path.GetFileName file)
            if i < im then
                mapS "\", \""
        mapN "\"],"
        mapS "\"sourcesContent\": ["
        for i = 0 to im do
            let file, assembly = sources.[i]
            if Some assembly = assemblyName then
                mapC '"'
                for c in System.IO.File.ReadAllText file do
                    match c with
                    | '\\' -> mapS "\\\\"
                    | '"' ->  mapS "\\\""
                    | '\n' -> mapS "\\n"
                    | '\r' -> ()
                    | _ -> mapC c
                mapC '"'
            else 
                mapS "null"
            if i < im then
                mapS ", "
        mapN "],"
        mapS "\"names\": [" 
        let im = names.Count - 1
        for i = 0 to im do
            mapC '"'
            mapS names.[i]
            mapC '"'
            if i < im then
                mapS ", "
        mapN "],"
        mapS "\"mappings\": \""
        mapFile.Append mappings |> ignore
        mapN "\""  
        mapN "}"
        Some (string mapFile)        

let Render mode (out: CodeWriter) layout =
    let rec (|O|_|) xs =
        match xs with
        | [] -> None
        | P _ :: ys 
        | E _ :: ys
        | N _ :: ys -> (|O|_|) ys
        | y :: _ -> Some y
    let (|FirstP|_|) xs =
        let rec skipP xs =
            match xs with
            | N n :: ys -> 
                let ys, _ = skipP ys
                ys, Some n 
            | P _ :: ys -> skipP ys
            | _ -> xs, None
        match xs with
        | P p :: ys -> Some (p, skipP ys)
        | _ -> None
    let rec (|LastE|_|) xs =
        match xs with
        | E _ :: LastE pys -> Some pys
        | E p :: ys -> Some (p, ys)
        | _ -> None
    let rec renderAtoms xs =
        match xs with
        | [] -> ()
        | (W s as x) :: ys | (T s as x) :: ys ->
            out.Write s   
            match x, ys with
            | _, [] -> ()
            | W _, O(W _) | T "+", O(T "+") | T "-", O(T "-") ->
                out.Write ' '   
            | _ -> () 
            renderAtoms ys
        | FirstP (p, (ys, n)) ->
            out.AddCodeMapping(p, true, ?name = n)
            renderAtoms ys   
        | LastE (p, ys) ->
            out.AddCodeMapping(p, false)
            renderAtoms ys   
        | N n :: ys ->
            renderAtoms ys   
        | _ -> failwith "wrong source position tokens"            
    let renderLine line =
        match line.Atoms with
        | [] -> ()
        | xs ->
            match mode with
            | Compact -> ()
            | Readable -> for k in 1 .. line.Indent do
                              out.Write ' '
            renderAtoms xs
        out.WriteLine()
    ToLines mode layout
    |> Seq.iter renderLine

let WriteExpression options writer expression =
    let buf = StringBuilder 32
    Expression buf expression
    |> Render options writer

let WriteProgram options writer (program: S.Program) =
    let buf = StringBuilder 32
    for elem in program do
        Element buf elem
        |> Render options writer

let ExpressionToString options expression =
    let w = CodeWriter()
    WriteExpression options w expression
    w.ToString()

let ProgramToString options program =
    let w = CodeWriter()
    WriteProgram options w program
    w.ToString()
