open CsLibrary
open FsLibrary.Shapes
open FsLibrary.Geometry

let shapes = [ Circle 1.0; Rectangle(2.0, 3.0) ]
let greeter = Greeter("Hello")
printfn "%s" (greeter.Greet "F#")
printfn "Total area: %.2f" (totalArea shapes) // [fs-total]
