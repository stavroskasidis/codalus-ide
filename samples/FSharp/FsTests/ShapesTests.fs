module ShapesTests

open Expecto
open FsLibrary.Shapes
open FsLibrary.Geometry

[<Tests>]
let tests =
    testList "Shapes" [
        test "a rectangle's area is width times height" {
            Expect.equal (area (Rectangle(2.0, 3.0))) 6.0 "2 × 3"
        }

        test "the total area adds the areas" {
            Expect.floatClose Accuracy.medium (totalArea [ Rectangle(1.0, 1.0); Rectangle(2.0, 2.0) ]) 5.0 "1 + 4"
        }
    ]
