/// A type provider: JsonProvider makes the types from the sample.
module FsData.Weather

open FSharp.Data

type Forecast = JsonProvider<"""{ "city": "Athens", "days": [ { "day": "Mon", "high": 31.5 } ] }""">

let sample = Forecast.GetSample()

/// The first day's high temperature.
let firstHigh = sample.Days[0].High
