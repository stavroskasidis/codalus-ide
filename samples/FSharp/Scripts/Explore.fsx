// A script: run it with dotnet fsi, or send parts of it to F# Interactive.
#load "Helpers.fsx"

open Helpers

let square x = x * x
let squares = [ 1; 2; 3 ] |> List.map square
// [fsi-end]

printfn "%s" (describe squares)
