/// Uses Shapes, which comes before it in the compile order.
module FsLibrary.Geometry

open FsLibrary.Shapes

/// The total area of the shapes.
let totalArea (shapes: Shape list) = shapes |> List.sumBy area
