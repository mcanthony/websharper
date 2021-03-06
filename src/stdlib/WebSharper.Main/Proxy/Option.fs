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

namespace WebSharper

open WebSharper.JavaScript

[<Proxy(typeof<option<_>>)>]
[<Name "WebSharper.Option.T">]
type private OptionProxy<'T> =
    | [<Name "None">] NoneCase
    | [<Name "Some">] SomeCase of 'T

    member this.Value with [<Inline "$this.$0">] get () = X<'T>

    [<Inline "$x.$ == 1">]
    static member get_IsSome(x: option<'T>) = false

    [<Inline "$x.$ == 0">]
    static member get_IsNone(x: option<'T>) = false

    [<Inline; JavaScript>]
    static member Some(v: 'T) = Some v

    static member None 
        with [<Inline; JavaScript>] get() = None
