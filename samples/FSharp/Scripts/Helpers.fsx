module Helpers

/// Describes a list of numbers.
let describe (numbers: int list) = sprintf "%d numbers, sum %d" numbers.Length (List.sum numbers)
