/// Shapes and their areas.
module FsLibrary.Shapes

/// A shape.
type Shape =
    | Circle of radius: float
    | Rectangle of width: float * height: float

/// The area of a shape.
val area: shape: Shape -> float
