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

namespace WebSharper.MSBuild

open System
open System.Diagnostics
open System.IO
open System.Reflection
open Microsoft.Build.Framework
open Microsoft.Build.Utilities
open IntelliFactory.Core
open WebSharper
open WebSharper.Compiler
module FE = FrontEnd

[<AutoOpen>]
module WebSharperTaskModule =

    let NotNull (def: 'T) (x: 'T) =
        if Object.ReferenceEquals(x, null) then def else x

    type Settings() =
        inherit MarshalByRefObject()
        member val Command : string = "" with get, set
        member val Configuration : string = "" with get, set
        member val DocumentationFile : string = "" with get, set
        member val EmbeddedResources : string [] = [||] with get, set
        member val ItemInput : string [] = [||] with get, set
        member val ItemOutput : string [] = [||] with get, set
        member val KeyOriginatorFile : string = "" with get, set
        member val Log : TaskLoggingHelper = null with get, set
        member val MSBuildProjectDirectory : string = "" with get, set
        member val Name : string = "" with get, set
        member val OutputPath : string = "" with get, set
        member val WebProjectOutputDir : string = "" with get, set
        member val WebSharperBundleOutputDir : string = "" with get, set
        member val WebSharperHtmlDirectory : string = "" with get, set
        member val WebSharperProject : string = "" with get, set
        member val WebSharperSourceMap : bool = false with get, set
        member val WebSharperTypeScriptDeclaration : bool = false with get, set

    type ProjectType =
        | Bundle of webroot: option<string>
        | Extension
        | Html
        | Library
        | Website of webroot: string
        | Ignore

    let GetWebRoot (settings: Settings) =
        match settings.WebProjectOutputDir with
        | "" ->
            let dir = settings.MSBuildProjectDirectory
            let isWeb =
                File.Exists(Path.Combine(dir, "Web.config"))
                || File.Exists(Path.Combine(dir, "web.config"))
            if isWeb then Some dir else None
        | out -> Some out

    let GetProjectType (settings: Settings) =
        match settings.WebSharperProject with
        | null | "" ->
            match GetWebRoot settings with
            | None -> Ignore
            | Some dir -> Website dir
        | proj ->
            match proj.ToLower() with
            | "ignore" -> Ignore
            | "bundle" -> Bundle (GetWebRoot settings)
            | "extension" | "interfacegenerator" -> Extension
            | "html" -> Html
            | "library" -> Library
            | "site" | "web" | "website" | "export" ->
                match GetWebRoot settings with
                | None -> Library
                | Some dir -> Website dir
            | _ -> invalidArg "type" ("Invalid project type: " + proj)

    let Fail (settings: Settings) fmt =
        fmt
        |> Printf.ksprintf (fun msg ->
            settings.Log.LogError(msg)
            false)

    let SendResult (settings: Settings) result =
        match result with
        | Compiler.Commands.Ok -> true
        | Compiler.Commands.Errors errors ->
            for e in errors do
                settings.Log.LogError(e)
            true

    let BundleOutputDir (settings: Settings) webRoot =
        match settings.WebSharperBundleOutputDir with
        | null | "" ->
            match webRoot with
            | Some webRoot ->
                let d = Path.Combine(webRoot, "Content")
                let di = DirectoryInfo(d)
                if not di.Exists then
                    di.Create()
                d
            | None -> failwith "WebSharperBundleOutputDir property is required"
        | dir -> dir

    let Bundle settings =
        match GetProjectType settings with
        | Bundle webRoot ->
            let outputDir = BundleOutputDir settings webRoot
            let fileName =
                match settings.Name with
                | null | "" -> "Bundle"
                | name -> name
            match List.ofArray settings.ItemInput with
            | raw :: refs ->
                let cfg =
                    {
                        Compiler.BundleCommand.Config.Create() with
                            AssemblyPaths = raw :: refs
                            FileName = fileName
                            OutputDirectory = outputDir
                    }
                let env = Compiler.Commands.Environment.Create()
                Compiler.BundleCommand.Instance.Execute(env, cfg)
                |> SendResult settings
            | _ -> Fail settings "Invalid options for Bundle command"
        | _ -> true

    let BundleClean settings webRoot =
        let outputDir = BundleOutputDir settings webRoot
        if Directory.Exists outputDir then
            let fileName =
                match settings.Name with
                | null | "" -> "Bundle"
                | name -> name
            let files =
                Directory.EnumerateFiles(outputDir, "*.*")
                |> Seq.filter (fun p -> Path.GetFileName(p).StartsWith(fileName))
            for f in files do
                File.Delete(f)

    let Timed f =
        let sw = Stopwatch()
        sw.Start()
        let r = f ()
        (r, sw.Elapsed)

    let Compile settings =
        if GetProjectType settings = Ignore then true else
        match List.ofArray settings.ItemInput with
        | raw :: refs ->
            let rawInfo = FileInfo(raw)
            let temp = raw + ".tmp"
            let tempInfo = FileInfo(temp)
            if not tempInfo.Exists || tempInfo.LastWriteTimeUtc < rawInfo.LastWriteTimeUtc then
                let main () =
                    let out =
                        CompilerUtility.Compile {
                            AssemblyFile = raw
                            KeyOriginatorFile = settings.KeyOriginatorFile
                            EmbeddedResources =
                                [
                                    for r in settings.EmbeddedResources ->
                                        Path.Combine(settings.MSBuildProjectDirectory, r)
                                ]
                            References = refs
                            ProjectDir = settings.MSBuildProjectDirectory
                            RunInterfaceGenerator =
                                match GetProjectType settings with
                                | Extension -> true
                                | _ -> false
                            DocumentationFile =
                                if String.IsNullOrEmpty settings.DocumentationFile then
                                    None
                                else Some settings.DocumentationFile
                            IncludeSourceMap = settings.WebSharperSourceMap
                        }
                    for msg in out.Messages do
                        msg.SendTo(settings.Log)
                    if out.Ok then
                        File.WriteAllText(tempInfo.FullName, "")
                    out.Ok
                settings.Log.LogMessage(MessageImportance.High, "Compiling with WebSharper...")
                let (res, t) = Timed main
                if res then
                    settings.Log.LogMessage(MessageImportance.High,
                        "WebSharper: compiled ok in {0} seconds",
                        round (t.TotalSeconds * 100.0) / 100.0)
                res
            else true
        | _ ->
            Fail settings "Need 1+ items for Compile command"

    [<Sealed>]
    type Marker = class end

    let BaseDir =
        typeof<Marker>.Assembly.Location
        |> Path.GetDirectoryName

    let Unpack settings =
        match GetProjectType settings with
        | Website webRoot ->
            let assemblies =
                let dir =
                    match settings.OutputPath with
                    | "" -> Path.Combine(webRoot, "bin")
                    | p -> p
                settings.Log.LogMessage(MessageImportance.High, 
                    sprintf "Unpacking with WebSharper: %s -> %s" dir webRoot)
                [
                    yield! Directory.EnumerateFiles(dir, "*.dll")
                    yield! Directory.EnumerateFiles(dir, "*.exe")
                ]
            for d in ["Scripts/WebSharper"; "Content/WebSharper"] do
                let dir = DirectoryInfo(Path.Combine(webRoot, d))
                if not dir.Exists then
                    dir.Create()
            let cfg =
                {
                    Compiler.UnpackCommand.Config.Create() with
                        Assemblies = assemblies
                        RootDirectory = webRoot
                        UnpackSourceMap = settings.WebSharperSourceMap
                        UnpackTypeScript = settings.WebSharperTypeScriptDeclaration
                }
            let env = Compiler.Commands.Environment.Create()
            Compiler.UnpackCommand.Instance.Execute(env, cfg)
            |> SendResult settings
        | _ -> true

    let HtmlOutputDirectory (settings: Settings) =
        match settings.WebSharperHtmlDirectory with
        | "" -> Path.Combine(settings.MSBuildProjectDirectory, "bin", "html")
        | dir -> dir

    let Html settings =
        match GetProjectType settings with
        | Html ->
            match List.ofArray settings.ItemInput with
            | main :: refs ->
                let main = main
                let cfg =
                    {
                        Compiler.HtmlCommand.Config.Create(main) with
                            Mode =
                                match settings.Configuration with
                                | x when x.ToLower().Contains("debug") -> Compiler.HtmlCommand.Debug
                                | x when x.ToLower().Contains("release") -> Compiler.HtmlCommand.Release
                                | _ -> Compiler.HtmlCommand.Debug
                            OutputDirectory = HtmlOutputDirectory settings
                            ProjectDirectory = settings.MSBuildProjectDirectory
                            ReferenceAssemblyPaths = refs
                            UnpackSourceMap = settings.WebSharperSourceMap
                            UnpackTypeScript = settings.WebSharperTypeScriptDeclaration
                    }
                let env = Compiler.Commands.Environment.Create()
                Compiler.HtmlCommand.Instance.Execute(env, cfg)
                |> SendResult settings
            | _ -> Fail settings "Invalid arguments for Html command"
        | _ -> true

    let HtmlClean settings =
        let d = DirectoryInfo(HtmlOutputDirectory settings)
        if d.Exists then
            d.Delete(``recursive`` = true)

    let Clean (settings: Settings) =
        // clean temp file used during compilation
        do
            match settings.ItemInput with
            | [| intermAssembly |] ->
                let tmp = FileInfo(intermAssembly + ".tmp")
                if tmp.Exists then
                    tmp.Delete()
            | _ -> ()
        match GetProjectType settings with
        | ProjectType.Bundle webRoot ->
            BundleClean settings webRoot
            true
        | ProjectType.Extension ->
            true
        | ProjectType.Html ->
            HtmlClean settings
            true
        | ProjectType.Library ->
            true
        | ProjectType.Ignore ->
            true
        | ProjectType.Website webRoot ->
            // clean what Unpack command generated:
            for d in ["Scripts/WebSharper"; "Content/WebSharper"] do
                let dir = DirectoryInfo(Path.Combine(webRoot, d))
                if dir.Exists then
                    dir.Delete(``recursive`` = true)
            true

    let Execute (settings: Settings) =
        try
            match settings.Command with
            | "Bundle" -> Bundle settings
            | "Clean" -> Clean settings
            | "Compile" -> Compile settings
            | "Html" -> Html settings
            | "Unpack" -> Unpack settings
            | cmd -> Fail settings "Unknown command: %s" (string cmd)
        with e ->
            settings.Log.LogErrorFromException(e)
            false

    type Settings with

        member private this.AddProjectReferencesToAssemblyResolution() =
            let referencedAsmNames =
                this.ItemInput
                |> Seq.append (Directory.GetFiles(BaseDir, "*.dll"))
                |> Seq.map (fun i -> Path.GetFileNameWithoutExtension(i), i)
                |> Seq.filter (fst >> (<>) this.Name)
                |> Map.ofSeq
            System.AppDomain.CurrentDomain.add_AssemblyResolve(fun sender e ->
                let assemblyName = AssemblyName(e.Name).Name
                match Map.tryFind assemblyName referencedAsmNames with
                | None -> null
                | Some p -> System.Reflection.Assembly.LoadFrom(p)
            )

        member this.Execute() =
            this.AddProjectReferencesToAssemblyResolution()
            Execute this

[<Sealed>]
type WebSharperTask() =
    inherit AppDomainIsolatedTask()

    do System.AppDomain.CurrentDomain.add_AssemblyResolve(fun sender e ->
        let asm = typeof<WebSharperTask>.Assembly
        if AssemblyName(e.Name).Name = asm.GetName().Name then
            asm
        else null)

    member val EmbeddedResources : ITaskItem [] = Array.empty with get, set
    member val Configuration = "" with get, set
    member val ItemInput : ITaskItem [] = Array.empty with get, set
    member val KeyOriginatorFile = "" with get, set
    member val MSBuildProjectDirectory = "" with get, set
    member val Name = "" with get, set
    member val OutputPath = "" with get, set
    member val WebProjectOutputDir = "" with get, set
    member val WebSharperBundleOutputDir = "" with get, set
    member val WebSharperHtmlDirectory = "" with get, set
    member val WebSharperProject = "" with get, set
    member val WebSharperSourceMap = "" with get, set
    member val WebSharperTypeScriptDeclaration = "" with get, set
    member val DocumentationFile = "" with get, set
    member val TargetFSharpCoreVersion = "" with get, set

    [<Required>]
    member val Command = "" with get, set

    [<Output>]
    member val ItemOutput : ITaskItem [] = Array.empty with get, set

    [<Output>]
    member val ReferenceCopyLocalPaths : ITaskItem [] = Array.empty with get, set

    member this.InvalidTargetFSharpCoreVersion =
        "Invalid TargetFSharpCoreVersion: \"" +
        this.TargetFSharpCoreVersion +
        "\"; should be \"4.3.0.0\", \"4.3.1.0\" or \"4.4.0.0\""

    override this.Execute() =
        let taskRefdFsCore = typeof<option<_>>.Assembly.GetName().Version
        let projRefdFsCore =
            try Version(this.TargetFSharpCoreVersion)
            with _ ->
                match this.Command with
                | "Compile" | "Html" -> this.Log.LogWarning this.InvalidTargetFSharpCoreVersion
                | _ -> ()
                taskRefdFsCore
        let settings, ad =
            if taskRefdFsCore >= projRefdFsCore then
                Settings(), None
            else
                // The FSharp.Core that MSBuild is running is different
                // from the FSharp.Core referenced by the project.
                // We need to run in an AppDomain to reference the right one.
                let config =
                    match projRefdFsCore.Minor, projRefdFsCore.Build with
                    | 3, 0 -> "WebSharper.exe.config"
                    | 3, 1 -> "WebSharper31.exe.config"
                    | 4, 0 -> "WebSharper40.exe.config"
                    | _ -> failwith this.InvalidTargetFSharpCoreVersion
                let asm = Assembly.GetExecutingAssembly()
                let loc = asm.Location
                let dir = Path.GetDirectoryName(loc)
                let setup = AppDomainSetup(ConfigurationFile = Path.Combine(dir, config))
                let ad = AppDomain.CreateDomain("WebSharperBuild", null, setup)
                let t = ad.CreateInstanceFromAndUnwrap(loc, typeof<Settings>.FullName, false, BindingFlags.CreateInstance, null, [||], null, null) :?> Settings
                t, Some ad
        let res = this.DoExecute settings
        Option.iter AppDomain.Unload ad
        res

    member this.DoExecute(settings: Settings) =
        let bool s =
            match s with
            | null | "" -> false
            | t when t.ToLower() = "true" -> true
            | _ -> false
        settings.Command <- this.Command
        settings.Configuration <- NotNull "Release" this.Configuration
        settings.DocumentationFile <- NotNull "" this.DocumentationFile
        settings.EmbeddedResources <- (NotNull [||] this.EmbeddedResources) |> Array.map (fun i -> i.ItemSpec)
        settings.ItemInput <- (NotNull [||] this.ItemInput) |> Array.map (fun i -> i.ItemSpec)
        settings.ItemOutput <- (NotNull [||] this.ItemOutput) |> Array.map (fun i -> i.ItemSpec)
        settings.KeyOriginatorFile <- NotNull "" this.KeyOriginatorFile
        settings.Log <- this.Log
        settings.MSBuildProjectDirectory <- NotNull "." this.MSBuildProjectDirectory
        settings.Name <- NotNull "Project" this.Name
        settings.OutputPath <- NotNull "" this.OutputPath
        settings.WebProjectOutputDir <- NotNull "" this.WebProjectOutputDir
        settings.WebSharperBundleOutputDir <- NotNull "" this.WebSharperBundleOutputDir
        settings.WebSharperHtmlDirectory <- NotNull "" this.WebSharperHtmlDirectory
        settings.WebSharperProject <- NotNull "" this.WebSharperProject
        settings.WebSharperSourceMap <- bool this.WebSharperSourceMap
        settings.WebSharperTypeScriptDeclaration <- bool this.WebSharperTypeScriptDeclaration
        settings.Execute()
