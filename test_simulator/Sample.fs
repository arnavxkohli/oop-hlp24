module Tests

open Expecto
open SimulatorTypes
open Simulator
open FastRun
open FastCreate
open FastReduce
open CommonTypes
open TestCases

let testcases2loadedComponents: LoadedComponent list =
    canvasStates
    |> List.map (fun (cs, name) -> Extractor.extractLoadedSimulatorComponent cs name)

let runNewSimulator
    (diagramName: string)
    (simulationArraySize: int)
    (canvasState: CommonTypes.CanvasState)
    (loadedDependencies: CommonTypes.LoadedComponent list)
    =
    // Build simulation graph.
    match
        Simulator.startCircuitSimulation
            simulationArraySize
            diagramName
            canvasState
            loadedDependencies
    with
    | Error err -> Error err
    | Ok simData ->
        printfn "new simData"
        Ok simData.FastSim.Drivers

let runOldSimulator
    (diagramName: string)
    (simulationArraySize: int)
    (canvasState: CommonTypes.CanvasState)
    (loadedDependencies: CommonTypes.LoadedComponent list)
    =
    // Build simulation graph.
    match
        OldSimulator.startCircuitSimulation
            simulationArraySize
            diagramName
            canvasState
            loadedDependencies
    with
    | Error err -> Error err
    | Ok simData ->
        printfn "old simData"
        Ok simData.FastSim.Drivers

[<Tests>]
let simulatorTests =
    testList
        "fake"
        [ testCase "fake"
          <| fun _ ->
              let n = 2
              let ldcs = testcases2loadedComponents
              let observed = runNewSimulator "" n ldcs[0].CanvasState ldcs
              let expected = runOldSimulator "" n ldcs[0].CanvasState ldcs
              Expect.equal
                  expected
                  observed
                  "Outputs of new simulator is not the same as outputs of old simulator" ]
