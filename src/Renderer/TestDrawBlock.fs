﻿module TestDrawBlock
open GenerateData
open SymbolView
open Symbol
open SymbolResizeHelpers
open Elmish


//-------------------------------------------------------------------------------------------//
//--------Types to represent tests with (possibly) random data, and results from tests-------//
//-------------------------------------------------------------------------------------------//
module TestLib =

    /// convenience unsafe function to extract Ok part of Result or fail if value is Error
    let getOkOrFail (res: Result<'a,string>) =
        match res with
        | Ok x -> x
        | Error mess ->
            failwithf "%s" mess


    type TestStatus =
            | Fail of string
            | Exception of string

    type Test<'a> = {
        Name: string
        Samples: Gen<'a>
        StartFrom: int
        /// The 1st argument is the test number: allows assertions that fail on a specific sample
        /// to display just one sample.
        /// The return value is None if test passes, or Some message if it fails.
        Assertion: int -> 'a -> string option
        }

    type TestResult<'a> = {
        TestName: string
        TestData: Gen<'a>
        FirstSampleTested: int
        TestErrors: (int * TestStatus) list
    }

    let catchException name func arg =
        try
            Ok (func arg)
        with
            | e ->
                Error ($"Exception when running {name}\n" + e.StackTrace)
            
    /// Run the Test samples from 0 up to test.Size - 1.
    /// The return list contains all failures or exceptions: empty list => everything has passed.
    /// This will always run to completion: use truncate if text.Samples.Size is too large.
    let runTests (test: Test<'a>) : TestResult<'a>  =
        [test.StartFrom..test.Samples.Size - 1]
        |> List.map (fun n ->
                catchException $"generating test {n} from {test.Name}" test.Samples.Data n
                |> (fun res -> n,res)
           )           
        |> List.collect (function
                            | n, Error mess -> [n, Exception mess]
                            | n, Ok sample ->
                                match catchException $"'test.Assertion' on test {n} from 'runTests'" (test.Assertion n) sample with
                                | Ok None -> []
                                | Ok (Some failure) -> [n,Fail failure]
                                | Error (mess) -> [n,Exception mess])
        |> (fun resL ->                
                {
                    TestName = test.Name
                    FirstSampleTested = test.StartFrom
                    TestData = test.Samples
                    TestErrors =  resL
                })
 
 
            
(******************************************************************************************
   This submodule contains a set of functions that enable random data generation
   for property-based testing of Draw Block wire routing functions.
   basic idea.
   1. Generate, in various ways, random circuit layouts
   2. For each layout apply smartautoroute to regenerate all wires
   3. Apply check functions to see if the resulting wire routing obeys "good layout" rules.
   4. Output any layouts with anomalous wire routing
*******************************************************************************************)
module HLPTick3 =
    open EEExtensions
    open Optics
    open Optics.Operators
    open DrawHelpers
    open Helpers
    open CommonTypes
    open ModelType
    open DrawModelType
    open Sheet.SheetInterface
    open GenerateData
    open TestLib

    /// create an initial empty Sheet Model 
    let initSheetModel = DiagramMainView.init().Sheet

    /// Optic to access SheetT.Model from Issie Model
    let sheetModel_ = sheet_

    /// Optic to access BusWireT.Model from SheetT.Model
    let busWireModel_ = SheetT.wire_

    /// Optic to access SymbolT.Model from SheetT.Model
    let symbolModel_ = SheetT.symbol_

    /// allowed max X or y coord of svg canvas
    let maxSheetCoord = Sheet.Constants.defaultCanvasSize
    let middleOfSheet = {X=maxSheetCoord/2.;Y=maxSheetCoord/2.}

    let diffFromMiddle (diff: XYPos) =
        middleOfSheet - diff

    /// Used throughout to compare labels since these are case invariant "g1" = "G1"
    let caseInvariantEqual str1 str2 =
        String.toUpper str1 = String.toUpper str2
 

    /// Identify a port from its component label and number.
    /// Usually both an input and output port will mathc this, so
    /// the port is only unique if it is known to be input or output.
    /// used to specify the ends of wires, since tehee are known to be
    /// connected to outputs (source) or inputs (target).
    type SymbolPort = { Label: string; PortNumber: int }

    /// convenience function to make SymbolPorts
    let portOf (label:string) (number: int) =
        {Label=label; PortNumber = number}


    //-----------------------------------------------------------------------------------------------
    // visibleSegments is included here as ahelper for info, and because it is needed in project work
    //-----------------------------------------------------------------------------------------------

    /// The visible segments of a wire, as a list of vectors, from source end to target end.
    /// Note that in a wire with n segments a zero length (invisible) segment at any index [1..n-2] is allowed 
    /// which if present causes the two segments on either side of it to coalesce into a single visible segment.
    /// A wire can have any number of visible segments - even 1.
    let visibleSegments (wId: ConnectionId) (model: SheetT.Model): XYPos list =

        let wire = model.Wire.Wires[wId] // get wire from model

        /// helper to match even and off integers in patterns (active pattern)
        let (|IsEven|IsOdd|) (n: int) = match n % 2 with | 0 -> IsEven | _ -> IsOdd

        /// Convert seg into its XY Vector (from start to end of segment).
        /// index must be the index of seg in its containing wire.
        let getSegmentVector (index:int) (seg: BusWireT.Segment) =
            // The implicit horizontal or vertical direction  of a segment is determined by 
            // its index in the list of wire segments and the wire initial direction
            match index, wire.InitialOrientation with
            | IsEven, BusWireT.Vertical | IsOdd, BusWireT.Horizontal -> {X=0.; Y=seg.Length}
            | IsEven, BusWireT.Horizontal | IsOdd, BusWireT.Vertical -> {X=seg.Length; Y=0.}

        /// Return a list of segment vectors with 3 vectors coalesced into one visible equivalent
        /// if this is possible, otherwise return segVecs unchanged.
        /// Index must be in range 1..segVecs
        let tryCoalesceAboutIndex (segVecs: XYPos list) (index: int)  =
            if segVecs[index] =~ XYPos.zero
            then
                segVecs[0..index-2] @
                [segVecs[index-1] + segVecs[index+1]] @
                segVecs[index+2..segVecs.Length - 1]
            else
                segVecs

        wire.Segments
        |> List.mapi getSegmentVector
        |> (fun segVecs ->
                (segVecs,[1..segVecs.Length-2])
                ||> List.fold tryCoalesceAboutIndex)


//------------------------------------------------------------------------------------------------------------------------//
//------------------------------functions to build issue schematics programmatically--------------------------------------//
//------------------------------------------------------------------------------------------------------------------------//
    module Builder =

        /// Place a new symbol with label symLabel onto the Sheet with given position.
        /// Return error if symLabel is not unique on sheet, or if position is outside allowed sheet coordinates (0 - maxSheetCoord).
        /// To be safe place components close to (maxSheetCoord/2.0, maxSheetCoord/2.0).
        /// symLabel - the component label, will be uppercased to make a standard label name
        /// compType - the type of the component
        /// position - the top-left corner of the symbol outline.
        /// model - the Sheet model into which the new symbol is added.
        let placeSymbol (symLabel: string) (compType: ComponentType) (position: XYPos) (model: SheetT.Model) : Result<SheetT.Model, string> =
            let symLabel = String.toUpper symLabel // make label into its standard casing so uppercase label
            let symModel, symId = SymbolUpdate.addSymbol [] (model.Wire.Symbol) position compType symLabel //add symbol to sheet(model) and return the updated model(sheet) and the symbol ID in the sheet
            let sym = symModel.Symbols.[symId] //access the symbol on the sheet using the symbol ID: symID
            match position + sym.getScaledDiagonal with //do sym diagonal pos + position (position is the location we want our symbol at)
            | {X=x;Y=y} when x > maxSheetCoord || y > maxSheetCoord -> //see if symbol position exceeds sheetCoord
                Error $"symbol '{symLabel}' position {position + sym.getScaledDiagonal} lies outside allowed coordinates"
            | _ ->
                model
                |> Optic.set symbolModel_ symModel
                |> SheetUpdate.updateBoundingBoxes // could optimise this by only updating symId bounding boxes
                |> Ok
        
        /// Place a new symbol onto the Sheet with given position and scaling (use default scale if this is not specified).
        /// The ports on the new symbol will be determined by the input and output components on some existing sheet in project.
        /// Return error if symLabel is not unique on sheet, or ccSheetName is not the name of some other sheet in project.
        let placeCustomSymbol
                (symLabel: string)
                (ccSheetName: string)
                (project: Project)
                (scale: XYPos)
                (position: XYPos)
                (model: SheetT.Model)
                    : Result<SheetT.Model, string> =
           let symbolMap = model.Wire.Symbol.Symbols
           if caseInvariantEqual ccSheetName project.OpenFileName then
                Error "Can't create custom component with name same as current opened sheet"        
            elif not <| List.exists (fun (ldc: LoadedComponent) -> caseInvariantEqual ldc.Name ccSheetName) project.LoadedComponents then
                Error "Can't create custom component unless a sheet already exists with smae name as ccSheetName"
            elif symbolMap |> Map.exists (fun _ sym ->  caseInvariantEqual sym.Component.Label symLabel) then
                Error "Can't create custom component with duplicate Label"
            else
                let canvas = model.GetCanvasState()
                let ccType: CustomComponentType =
                    {
                        Name = ccSheetName
                        InputLabels = Extractor.getOrderedCompLabels (Input1 (0, None)) canvas
                        OutputLabels = Extractor.getOrderedCompLabels (Output 0) canvas
                        Form = None
                        Description = None
                    }
                placeSymbol symLabel (Custom ccType) position model

        // Rotate a symbol
        let rotateSymbol (symLabel: string) (rotate: Rotation) (model: SheetT.Model) : (SheetT.Model) =
            
            // 1. Find the symbol in the model by symLabel.
            //let foundSymbol = model.Symbols |> List.tryFind (fun sym -> sym.Label = symLabel)
            // 2. Apply the rotation to the symbol. This might involve updating the symbol's rotation property or transforming its coordinates.
            // 3. Return the updated model with the rotated symbol.

            let symbolsMap = model.Wire.Symbol.Symbols //fine
            let getSymbol = //can use symLabel as para
                mapValues symbolsMap
                |> Array.tryFind (fun sym -> caseInvariantEqual sym.Component.Label symLabel)
                |> function | Some x -> Ok x | None -> Error "Can't find symbol with label '{symPort.Label}'"

            match getSymbol with
            | Ok symbol ->
                let rotatedSymbol = SymbolResizeHelpers.rotateSymbol rotate symbol
                // Create a new map with the rotated symbol. Assuming Symbol has a unique identifier or label for key.
                let updatedSymbolsMap = Map.add symbol.Id rotatedSymbol symbolsMap
                
                //let newModel = { model with model.Wire.Symbol.Symbols = updatedSymbols }
                { model with Wire = { model.Wire with Symbol = { model.Wire.Symbol with Symbols = updatedSymbolsMap } } }

            | _ -> model

        // Flip a symbol
        let flipSymbol (symLabel: string) (flip: SymbolT.FlipType) (model: SheetT.Model) : (SheetT.Model) =

            let symbolsMap = model.Wire.Symbol.Symbols //fine
            let getSymbol = //can use symLabel as para
                mapValues symbolsMap
                |> Array.tryFind (fun sym -> caseInvariantEqual sym.Component.Label symLabel)
                |> function | Some x -> Ok x | None -> Error "Can't find symbol with label '{symPort.Label}'"

            match getSymbol with
            | Ok symbol ->
                let flippedSymbol = SymbolResizeHelpers.flipSymbol flip symbol
                // Create a new map with the rotated symbol. Assuming Symbol has a unique identifier or label for key.
                let updatedSymbolsMap = Map.add symbol.Id flippedSymbol symbolsMap
    
                //let newModel = { model with model.Wire.Symbol.Symbols = updatedSymbols }
                { model with Wire = { model.Wire with Symbol = { model.Wire.Symbol with Symbols = updatedSymbolsMap } } }

            | _ -> model

        /// Add a (newly routed) wire, source specifies the Output port, target the Input port.
        /// Return an error if either of the two ports specified is invalid, or if the wire duplicates and existing one.
        /// The wire created will be smart routed but not separated from other wires: for a nice schematic
        /// separateAllWires should be run after  all wires are added.
        /// source, target: respectively the output port and input port to which the wire connects.
        //so take in a current sheet with our components and from (source) and to (target) port and return the sheet with the components but with the wire
        let placeWire (source: SymbolPort) (target: SymbolPort) (model: SheetT.Model) : Result<SheetT.Model,string> =
            let symbols = model.Wire.Symbol.Symbols
            let getPortId (portType:PortType) symPort =
                mapValues symbols
                |> Array.tryFind (fun sym -> caseInvariantEqual sym.Component.Label symPort.Label)
                |> function | Some x -> Ok x | None -> Error "Can't find symbol with label '{symPort.Label}'"
                |> Result.bind (fun sym ->
                    match portType with
                    | PortType.Input -> List.tryItem symPort.PortNumber sym.Component.InputPorts
                    | PortType.Output -> List.tryItem symPort.PortNumber sym.Component.OutputPorts
                    |> function | Some port -> Ok port.Id
                                | None -> Error $"Can't find {portType} port {symPort.PortNumber} on component {symPort.Label}")
            
            match getPortId PortType.Input target, getPortId PortType.Output source with
            | Error e, _ | _, Error e -> Error e
            | Ok inPort, Ok outPort ->
                let newWire = BusWireUpdate.makeNewWire (InputPortId inPort) (OutputPortId outPort) model.Wire
                if model.Wire.Wires |> Map.exists (fun wid wire -> wire.InputPort=newWire.InputPort && wire.OutputPort = newWire.OutputPort) then
                        // wire already exists
                        Error "Can't create wire from {source} to {target} because a wire already exists between those ports"
                else
                     model
                     |> Optic.set (busWireModel_ >-> BusWireT.wireOf_ newWire.WId) newWire
                     |> Ok
            

        /// Run the global wire separation algorithm (should be after all wires have been placed and routed)
        let separateAllWires (model: SheetT.Model) : SheetT.Model =
            model
            |> Optic.map busWireModel_ (BusWireSeparate.updateWireSegmentJumpsAndSeparations (model.Wire.Wires.Keys |> Seq.toList))

        /// Copy testModel into the main Issie Sheet making its contents visible
        let showSheetInIssieSchematic (testModel: SheetT.Model) (dispatch: Dispatch<Msg>) =
            let sheetDispatch sMsg = dispatch (Sheet sMsg)
            dispatch <| UpdateModel (Optic.set sheet_ testModel) // set the Sheet component of the Issie model to make a new schematic.
            sheetDispatch <| SheetT.KeyPress SheetT.CtrlW // Centre & scale the schematic to make all components viewable.


        /// 1. Create a set of circuits from Gen<'a> samples by applying sheetMaker to each sample.
        /// 2. Check each ciruit with sheetChecker.
        /// 3. Return a TestResult record with errors those samples for which sheetChecker returns false,
        /// or where there is an exception.
        /// If there are any test errors display the first in Issie, and its error message on the console.
        /// sheetMaker: generates a SheetT.model from the random sample
        /// sheetChecker n model: n is sample number, model is the genrated model. Return false if test fails.
        let runTestOnSheets
            (name: string)
            (sampleToStartFrom: int)
            (samples : Gen<'a>)
            (sheetMaker: 'a -> SheetT.Model)
            (sheetChecker: int -> SheetT.Model -> string option)
            (dispatch: Dispatch<Msg>)
                : TestResult<'a> =
            let generateAndCheckSheet n = sheetMaker >> sheetChecker n
            let result =
                {
                    Name=name;
                    Samples=samples; //samples is a set of circuits we want to test that are part of the test Name
                    StartFrom = sampleToStartFrom //so which circuit index in the Gen<'a> to start from
                    Assertion = generateAndCheckSheet //what is the assertion called
                } //so pass this Test<'a> to runTests which will return a list of all the failed tests (for the given circuits) (empty if all tests passed)
                |> runTests
            match result.TestErrors with
            | [] -> // no errors
                printf $"Test {result.TestName} has PASSED."
            | (n,first):: _ -> // display in Issie editor and print out first error
                printf $"Test {result.TestName} has FAILED on sample {n} with error message:\n{first}"
                match catchException "" sheetMaker (samples.Data n) with
                | Ok sheet -> showSheetInIssieSchematic sheet dispatch
                | Error mess -> ()
            result
//--------------------------------------------------------------------------------------------------//
//----------------------------------------Example Test Circuits using Gen<'a> samples---------------//
//--------------------------------------------------------------------------------------------------//

    open Builder
    /// Sample data based on 11 equidistant points on a horizontal line
    let horizLinePositions =
        fromList [-100..20..100] //so [-100 -80 -60... 60 80 100] passes to map and that returns a gen with Data = l[i % l.Length] |> middleOfSheet + {X=float n; Y=0.},
        //so pick a value from the l list then use it as X and add to middleOfSheet, so basically adding an X translation through points [-100..20..100] to middleOfSheet for a certain component
        //so randomize this, maybe use the same map function (or maybe add a threshold to ensure position is at least at threshold and then randomly choose values the list and add in no order
        |> map (fun n -> middleOfSheet + {X=float n; Y=float n})

        //so given a list [-50..10..50] it is convert to a Gen<'a>, and then map is run on it (go over map and Gen<a'> functions
    let verticalLinePositions =
        fromList [-50..10..50]
        |> map (fun n -> middleOfSheet + {X=0.; Y=float n})

    /// demo test circuit consisting of a DFF & And gate
    let makeTest1Circuit (andPos:XYPos) =
        initSheetModel
        |> placeSymbol "G1" (GateN(And,2)) andPos
        |> Result.bind (placeSymbol "FF1" DFF middleOfSheet)
        |> Result.bind (placeWire (portOf "G1" 0) (portOf "FF1" 0))
        |> Result.bind (placeWire (portOf "FF1" 0) (portOf "G1" 0) )
        |> getOkOrFail

///D3 HELPERS

    //maybe use result of this in another genXYPos to check for overlapping
    let genXYPos lim =
        //create an initial set of coords
        let coords =
            fromList [-lim..20..lim]
            |> map (fun n -> float n)
        //make a random pos by picking randomly from these coords using product
        product (fun x y -> {X=x; Y=y}) coords coords

    //SHOULD PASS AND BE HANDLED BY D3
    let makeD3TestCircuitEasy (threshold: float) (
                            sample: {|
                                FlipMux: SymbolT.FlipType option;
                                RotMux: Rotation option;
                                DemuxPos: XYPos;
                                Mux1Pos: XYPos;
                                Mux2Pos: XYPos
                            |}) : SheetT.Model = 
        //so recreate figure C1 so long wires
        let s = sample
        initSheetModel
        //adding DM1
        //adding small changes to position of components, changes given by a gen
        |> placeSymbol "DM1" (Demux4) (middleOfSheet - {X=threshold; Y=0.} + s.DemuxPos)
        //adding MUX1
        |> Result.bind (placeSymbol "MUX1" (Mux4) (middleOfSheet + s.Mux1Pos))
        |> match s.RotMux with
            | Some rot -> Result.map (rotateSymbol "MUX1" rot) //so if flip is not none hten do result.map else do identity function to pass model as is without flip
            | None -> id
        |> match s.FlipMux with
            | Some flip -> Result.map (flipSymbol "MUX1" flip) //so if flip is not none hten do result.map else do identity function to pass model as is without flip
            | None -> id
        |> Result.bind (placeWire (portOf "DM1" 0) (portOf "MUX1" 0))
        |> Result.bind (placeWire (portOf "DM1" 1) (portOf "MUX1" 1))
        |> Result.bind (placeWire (portOf "DM1" 2) (portOf "MUX1" 2))
        |> Result.bind (placeWire (portOf "DM1" 3) (portOf "MUX1" 3))
        //adding MUX2
        |> Result.bind (placeSymbol "MUX2" (Mux4) (middleOfSheet + {X=0.; Y=threshold} + s.Mux2Pos))
        |> Result.bind (placeWire (portOf "DM1" 0) (portOf "MUX2" 0))
        |> Result.bind (placeWire (portOf "DM1" 1) (portOf "MUX2" 1))
        |> Result.bind (placeWire (portOf "DM1" 2) (portOf "MUX2" 2))
        |> Result.bind (placeWire (portOf "DM1" 3) (portOf "MUX2" 3))
        |> getOkOrFail
        |> separateAllWires

    //Generate samples for D3 Easy circuit
    let makeSamplesD3Easy =
        let rotations = fromList [Some Rotation.Degree90; Some Rotation.Degree270; None]
        let flips = fromList [Some SymbolT.FlipType.FlipHorizontal; None]
        genXYPos 100
        |> product (fun a b -> (a,b)) (genXYPos 100)
        |> product (fun a b -> (a,b)) rotations
        |> product (fun a b -> (a,b)) flips
        |> map (fun (flipMux,(rotMux,(demuxPos, muxPos))) ->
            {|
                FlipMux = flipMux;
                RotMux = rotMux;
                DemuxPos = demuxPos;
                Mux1Pos = muxPos;
                Mux2Pos = muxPos
            |})

    //VERY DIFFICULT CASE - LIKELY TO FAIL
    let makeD3TestCircuitHard (threshold: float) (
                            sample: {|
                                FlipMux: SymbolT.FlipType option;
                                RotMux: Rotation option;
                                AndPos: XYPos;
                                OrPos: XYPos;
                                XorPos: XYPos;
                                MuxPos: XYPos
                            |}) : SheetT.Model = 
        //so recreate figure C1 so long wires
        let s = sample
        initSheetModel
        //adding MUX1
        //adding small changes to position of components, changes given by a gen
        |> placeSymbol "MUX1" (Mux2) (middleOfSheet - {X=threshold; Y=0.} + s.MuxPos)
        |> match s.RotMux with
            | Some rot -> Result.map (rotateSymbol "MUX1" rot) //so if flip is not none hten do result.map else do identity function to pass model as is without flip
            | None -> id
        |> match s.FlipMux with
            | Some flip -> Result.map (flipSymbol "MUX1" flip) //so if flip is not none hten do result.map else do identity function to pass model as is without flip
            | None -> id
        //adding OR1
        |> Result.bind (placeSymbol "OR1" (GateN(Or, 2)) (middleOfSheet + s.OrPos))
        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "OR1" 0))
        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "OR1" 1))
        //adding AND1
        |> Result.bind (placeSymbol "AND1" (GateN(And, 2)) (middleOfSheet + {X=threshold; Y=threshold} + s.AndPos))
        |> Result.bind (placeWire (portOf "OR1" 0) (portOf "AND1" 0))
        |> Result.bind (placeWire (portOf "OR1" 0) (portOf "AND1" 1))
        |> Result.bind (placeWire (portOf "OR1" 0) (portOf "MUX1" 2))
        |> Result.bind (placeWire (portOf "AND1" 0) (portOf "MUX1" 1))
        //adding XOR1
        |> Result.bind (placeSymbol "XOR1" (GateN(Xor, 2)) (middleOfSheet + {X=threshold; Y=0.} + s.XorPos))
        |> Result.bind (placeWire (portOf "AND1" 0) (portOf "XOR1" 0))
        |> Result.bind (placeWire (portOf "AND1" 0) (portOf "XOR1" 1))
        |> Result.bind (placeWire (portOf "XOR1" 0) (portOf "MUX1" 0))
        |> getOkOrFail
        |> separateAllWires

    //Generate samples for D3 Hard circuit
    let makeSamplesD3Hard =
        let rotations = fromList [Some Rotation.Degree90; Some Rotation.Degree270; None]
        let flips = fromList [Some SymbolT.FlipType.FlipHorizontal; None]
        genXYPos 100
        |> product (fun a b -> (a,b)) (genXYPos 100)
        |> product (fun a b -> (a,b)) rotations
        |> product (fun a b -> (a,b)) flips
        |> map (fun (flipMux,(rotMux,(orPos, muxPos))) ->
            {|
                FlipMux = flipMux;
                RotMux = rotMux;
                AndPos = muxPos;
                OrPos = orPos;
                XorPos = orPos;
                MuxPos = muxPos
            |})
    
    type componentInfo = {
        Label: string
        CompType: ComponentType
    }

    type connectionInfo = {
        SourceCompLabel: string
        SourceCompPort: int
        TargetCompLabel: string
        TargetCompPort: int
    }

    //should return the position of the component such that it is > threshold dist relative to all other components on the sheet
    //so probably pass in my sheet, current component, threshold, and then find a pos in the sheet that's more than threshold dist from all other components
    let generatePosition (model: SheetT.Model) (threshold:float) (isGreaterThanThreshold: bool): XYPos =
        let initPosition = middleOfSheet

        let getExistingPositions =
            model.Wire.Symbol.Symbols
            |> Map.toList
            |> List.map (fun (_, sym) -> sym.Pos)

        let rec findPosition (position: XYPos) =

            let isPositionValid (existingPos: XYPos) =
                if isGreaterThanThreshold then
                    euclideanDistance existingPos position > threshold
                else
                    euclideanDistance existingPos position <= threshold


            // Check if the position is more than threshold distance from all existing component positions
            if getExistingPositions |> List.exists (fun existingPos ->
                euclideanDistance existingPos position <= threshold) then
                // Adjust position and try again if it's too close to an existing component - not sure if this is correct way to do it, maybe just use Gen<'a> + threshold?
                if random.Next(0,2) = 0 then
                    findPosition { position with X = position.X + threshold }
                else
                    findPosition { position with Y = position.Y + threshold }
            else
                position //so when no other component's position is within threshold then return component's pos

        findPosition initPosition

    let placeComponentsOnModel (components: componentInfo list) (threshold: float) (genSamples: XYPos) : SheetT.Model =
        components
        |> List.fold (fun currModel comp ->
            let compPosition = generatePosition currModel threshold 
            placeSymbol comp.Label comp.CompType (compPosition + genSamples) currModel
            |> getOkOrFail) initSheetModel

    //randomly connect components
    //given the list of components, shuffle them, then connect pairwise components
    let randomConnectComponents (components: componentInfo list) (threshold: float) (genSamples: XYPos) =
        components
        |> List.toArray
        |> shuffleA
        //could do pairwise and then connect the ports of the 1st element in the tuple with the 2nd
        |> Array.pairwise
        |> Array.fold (fun currModel (comp1, comp2) ->
            placeWire (portOf comp1.Label 0) (portOf comp2.Label 0) currModel
            |> Result.bind (placeWire (portOf comp1.Label 0) (portOf comp2.Label 1))
            |> getOkOrFail
            |> separateAllWires
        ) (placeComponentsOnModel components threshold genSamples)

    //so connect components based on a list of connections
    let givenConnectComponents (components: componentInfo list) (connections: connectionInfo list) (threshold: float) (genSamples: XYPos)=
        connections
        |> List.fold (fun currModel connect ->
            placeWire (portOf connect.SourceCompLabel connect.SourceCompPort) (portOf connect.TargetCompLabel connect.TargetCompPort) currModel
            |> getOkOrFail
            |> separateAllWires
        ) (placeComponentsOnModel components threshold genSamples)

    //want to create a test like this, so d3 creator must write components list and connections list (or not and use random connects) and call either use random connect components or given
    let makeD3HelperTestCircuitGiven (threshold: float) (genSamples : XYPos) : SheetT.Model =
        let components = [
            { Label = "AND1"; CompType = GateN(And, 2) }; // Example component, adjust types and labels as needed
            { Label = "OR1"; CompType = GateN(Or, 2) };
            { Label = "XOR1"; CompType = GateN(Xor, 2) };
            { Label = "MUX1"; CompType = Mux2 };
            // Add more components as needed for the test
        ]

        let connections = [
            { SourceCompLabel = "AND1"; SourceCompPort = 0; TargetCompLabel = "OR1"; TargetCompPort = 0 };
            { SourceCompLabel = "OR1"; SourceCompPort = 0; TargetCompLabel = "XOR1"; TargetCompPort = 1 };
            { SourceCompLabel = "XOR1"; SourceCompPort = 0; TargetCompLabel = "MUX1"; TargetCompPort = 1 };
            { SourceCompLabel = "MUX1"; SourceCompPort = 0; TargetCompLabel = "AND1"; TargetCompPort = 1 };
            // Further connections as required
        ]

        //Choose whether to create a D3 test by providing a components and connections list or just a components list and randomly connecting:
        givenConnectComponents components connections threshold genSamples

    //want to create a test like this, so d3 creator must write components list and connections list (or not and use random connects) and call either use random connect components or given
    let makeD3HelperTestCircuitRandom (threshold: float) (genSamples : XYPos) : SheetT.Model =
        let components = [
            { Label = "AND1"; CompType = GateN(And, 2) }; // Example component, adjust types and labels as needed
            { Label = "OR1"; CompType = GateN(Or, 2) };
            { Label = "XOR1"; CompType = GateN(Xor, 2) };
            { Label = "MUX1"; CompType = Mux2 };
            // Add more components as needed for the test
        ]

        //Choose whether to create a D3 test by providing a components and connections list or just a components list and randomly connecting:
        randomConnectComponents components threshold genSamples

    //Generate samples for D3 circuit maker helper function
    let makeSamplesD3Helper =
        let rotations = fromList [Some Rotation.Degree90; Some Rotation.Degree270; None]
        let flips = fromList [Some SymbolT.FlipType.FlipHorizontal; None]
        genXYPos 100
        |> product (fun a b -> (a,b)) (genXYPos 100)
        |> product (fun a b -> (a,b)) rotations
        |> product (fun a b -> (a,b)) flips
        |> map (fun (flipMux,(rotMux,(demuxPos, muxPos))) ->
            {|
                FlipMux = flipMux;
                RotMux = rotMux;
                DemuxPos = demuxPos;
                Mux1Pos = muxPos;
                Mux2Pos = muxPos
            |})


//------------------------------------------------------------------------------------------------//
//-------------------------Example assertions used to test sheets---------------------------------//
//------------------------------------------------------------------------------------------------//


    module Asserts =

        (* Each assertion function from this module has as inputs the sample number of the current test and the corresponding schematic sheet.
           It returns a boolean indicating (true) that the test passes or 9false) that the test fails. The sample numbr is included to make it
           easy to document tests and so that any specific sampel schematic can easily be displayed using failOnSampleNumber. *)

        /// Ignore sheet and fail on the specified sample, useful for displaying a given sample
        let failOnSampleNumber (sampleToFail :int) (sample: int) _sheet =
            if sampleToFail = sample then
                Some $"Failing forced on Sample {sampleToFail}."
            else
                None

        /// Fails all tests: useful to show in sequence all the sheets generated in a test
        let failOnAllTests (sample: int) _ =
            Some <| $"Sample {sample}"

        /// Fail when sheet contains a wire segment that overlaps (or goes too close to) a symbol outline  
        let failOnWireIntersectsSymbol (sample: int) (sheet: SheetT.Model) =
            let wireModel = sheet.Wire
            wireModel.Wires
            |> Map.exists (fun _ wire -> BusWireRoute.findWireSymbolIntersections wireModel wire <> [])
            |> (function | true -> Some $"Wire intersects a symbol outline in Sample {sample}"
                         | false -> None)

        /// Fail when sheet contains two symbols which overlap
        let failOnSymbolIntersectsSymbol (sample: int) (sheet: SheetT.Model) =
            let wireModel = sheet.Wire
            let boxes =
                mapValues sheet.BoundingBoxes
                |> Array.toList
                |> List.mapi (fun n box -> n,box)
            List.allPairs boxes boxes 
            |> List.exists (fun ((n1,box1),(n2,box2)) -> (n1 <> n2) && BlockHelpers.overlap2DBox box1 box2)
            |> (function | true -> Some $"Symbol outline intersects another symbol outline in Sample {sample}"
                         | false -> None)

//---------------------------------------------------------------------------------------//
//-----------------------------Demo tests on Draw Block code-----------------------------//
//---------------------------------------------------------------------------------------//

    module Tests =

        let rotationDegrees = [| Degree0; Degree90; Degree180; Degree270 |]
        let flipTypes = [|SymbolT.FlipHorizontal; SymbolT.FlipVertical|]

        // Function to pick a random value from an array
        let pickRandomValue<'T> (array: 'T[]) : 'T =
            let index = GenerateData.random.Next(array.Length)
            array.[index]

        /// Allow test errors to be viewed in sequence by recording the current error
        /// in the Issie Model (field DrawblockTestState). This contains all Issie persistent state.
        let recordPositionInTest (testNumber: int) (dispatch: Dispatch<Msg>) (result: TestResult<'a>) =
            dispatch <| UpdateDrawBlockTestState(fun _ ->
                match result.TestErrors with
                | [] ->
                    printf "Test finished"
                    None
                | (numb, _) :: _ ->
                    printf $"Sample {numb}"
                    Some { LastTestNumber=testNumber; LastTestSampleIndex= numb})
            
        /// Example test: Horizontally positioned AND + DFF: fail on sample 0
        let test1 testNum firstSample dispatch =
            runTestOnSheets
                "Horizontally positioned AND + DFF: fail on sample 0"
                firstSample
                horizLinePositions
                makeTest1Circuit
                (Asserts.failOnSampleNumber 10)
                dispatch
            |> recordPositionInTest testNum dispatch

        /// Example test: Horizontally positioned AND + DFF: fail on sample 10
        //let test2 testNum firstSample dispatch =
        //    runTestOnSheets
        //        "Horizontally positioned AND + DFF: fail on sample 10"
        //        firstSample
        //        horizLinePositions
        //        makeTest1Circuit
        //        (Asserts.failOnSampleNumber 10)
        //        dispatch
        //    |> recordPositionInTest testNum dispatch

        let testD3Easy testNum firstSample dispatch =
            let threshold = 450.0
            runTestOnSheets
                "D3 Test 1 Easy"
                firstSample
                makeSamplesD3Easy
                (makeD3TestCircuitEasy threshold)
                //Asserts.failOnSymbolIntersectsSymbol
                (Asserts.failOnSampleNumber 100)
                dispatch
            |> recordPositionInTest testNum dispatch

        let testD3Hard testNum firstSample dispatch =
            let threshold = 200.0
            runTestOnSheets
                "D3 Test 1 Hard"
                firstSample
                makeSamplesD3Hard
                (makeD3TestCircuitHard threshold)
                //Asserts.failOnSymbolIntersectsSymbol
                (Asserts.failOnSampleNumber 100)
                dispatch
            |> recordPositionInTest testNum dispatch

        let testD3HelperGiven testNum firstSample dispatch =
            let threshold = 200.0
            runTestOnSheets
                "D3 Test Giving connections list"
                firstSample
                (genXYPos 100)
                (makeD3HelperTestCircuitGiven threshold)
                //Asserts.failOnSymbolIntersectsSymbol
                (Asserts.failOnSampleNumber 10)
                dispatch
            |> recordPositionInTest testNum dispatch
//
        /// Example test: Horizontally positioned AND + DFF: fail on symbols intersect
        let testD3HelperRandom testNum firstSample dispatch =
            let threshold = 200.0
            runTestOnSheets
                "Horizontally positioned AND + DFF: fail on symbols intersect"
                firstSample
                (genXYPos 100)
                (makeD3HelperTestCircuitRandom threshold)
                //Asserts.failOnSymbolIntersectsSymbol
                (Asserts.failOnSampleNumber 10)
                dispatch
            |> recordPositionInTest testNum dispatch

        /// Example test: Horizontally positioned AND + DFF: fail all tests
        let test4 testNum firstSample dispatch =
            runTestOnSheets
                "Horizontally positioned AND + DFF: fail all tests"
                firstSample
                horizLinePositions
                makeTest1Circuit
                Asserts.failOnAllTests
                dispatch
            |> recordPositionInTest testNum dispatch

        /// List of tests available which can be run ftom Issie File Menu.
        /// The first 9 tests can also be run via Ctrl-n accelerator keys as shown on menu
        let testsToRunFromSheetMenu : (string * (int -> int -> Dispatch<Msg> -> Unit)) list =
            // Change names and test functions as required
            // delete unused tests from list
            [
                "Test1", test1 // example
                "Test2", testD3Easy // example
                "Test3", testD3Hard // example
                "Test4", testD3HelperGiven
                "Test5", testD3HelperRandom
                //"Test5", fun _ _ _ -> printf "Test5" // dummy test - delete line or replace by real test as needed
                "Test6", fun _ _ _ -> printf "Test6"
                "Test7", fun _ _ _ -> printf "Test7"
                "Test8", fun _ _ _ -> printf "Test8"
                "Next Test Error", fun _ _ _ -> printf "Next Error:" // Go to the nexterror in a test

            ]

        /// Display the next error in a previously started test
        let nextError (testName, testFunc) firstSampleToTest dispatch =
            let testNum =
                testsToRunFromSheetMenu
                |> List.tryFindIndex (fun (name,_) -> name = testName)
                |> Option.defaultValue 0
            testFunc testNum firstSampleToTest dispatch

        /// common function to execute any test.
        /// testIndex: index of test in testsToRunFromSheetMenu
        let testMenuFunc (testIndex: int) (dispatch: Dispatch<Msg>) (model: Model) =
            let name,func = testsToRunFromSheetMenu[testIndex] 
            printf "%s" name
            match name, model.DrawBlockTestState with
            | "Next Test Error", Some state ->
                nextError testsToRunFromSheetMenu[state.LastTestNumber] (state.LastTestSampleIndex+1) dispatch
            | "Next Test Error", None ->
                printf "Test Finished"
                ()
            | _ ->
                func testIndex 0 dispatch
        


    

<<<<<<< Updated upstream
*)



































(*
/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

                                              REMOVE LATER - FOR TESTING ONLY                                  

/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
*)


module TestDrawBlock
open GenerateData
open SheetBeautifyHelpers //use for flip and rotate
open CommonTypes
open ModelType
open DrawModelType
open SheetUpdateHelpers
open Elmish


//-------------------------------------------------------------------------------------------//
//--------Types to represent tests with (possibly) random data, and results from tests-------//
//-------------------------------------------------------------------------------------------//
module TestLib =

    /// convenience unsafe function to extract Ok part of Result or fail if value is Error
    let getOkOrFail (res: Result<'a,string>) =
        match res with
        | Ok x -> x
        | Error mess ->
            failwithf "%s" mess


    type TestStatus =
            | Fail of string
            | Exception of string

    type Test<'a> = {
        Name: string
        Samples: Gen<'a>
        StartFrom: int
        /// The 1st argument is the test number: allows assertions that fail on a specific sample
        /// to display just one sample.
        /// The return value is None if test passes, or Some message if it fails.
        Assertion: int -> 'a -> string option
        }

    type TestResult<'a> = {
        TestName: string
        TestData: Gen<'a>
        FirstSampleTested: int
        TestErrors: (int * TestStatus) list
    }

    let catchException name func arg =
        try
            Ok (func arg)
        with
            | e ->
                Error ($"Exception when running {name}\n" + e.StackTrace)
            
    /// Run the Test samples from 0 up to test.Size - 1.
    /// The return list contains all failures or exceptions: empty list => everything has passed.
    /// This will always run to completion: use truncate if text.Samples.Size is too large.
    let runTests (test: Test<'a>) : TestResult<'a>  =
        [test.StartFrom..test.Samples.Size - 1]
        |> List.map (fun n ->
                catchException $"generating test {n} from {test.Name}" test.Samples.Data n
                |> (fun res -> n,res)
           )           
        |> List.collect (function
                            | n, Error mess -> [n, Exception mess]
                            | n, Ok sample ->
                                match catchException $"'test.Assertion' on test {n} from 'runTests'" (test.Assertion n) sample with
                                | Ok None -> []
                                | Ok (Some failure) -> [n,Fail failure]
                                | Error (mess) -> [n,Exception mess])
        |> (fun resL ->                
                {
                    TestName = test.Name
                    FirstSampleTested = test.StartFrom
                    TestData = test.Samples
                    TestErrors =  resL
                })
 
 
            
(******************************************************************************************
   This submodule contains a set of functions that enable random data generation
   for property-based testing of Draw Block wire routing functions.
   basic idea.
   1. Generate, in various ways, random circuit layouts
   2. For each layout apply smartautoroute to regenerate all wires
   3. Apply check functions to see if the resulting wire routing obeys "good layout" rules.
   4. Output any layouts with anomalous wire routing
*******************************************************************************************)
module HLPTick3 =
    open EEExtensions
    open Optics
    open Optics.Operators
    open DrawHelpers
    open Helpers
    open CommonTypes
    open ModelType
    open DrawModelType
    open Sheet.SheetInterface
    open GenerateData
    open TestLib

    /// create an initial empty Sheet Model 
    let initSheetModel = DiagramMainView.init().Sheet

    /// Optic to access SheetT.Model from Issie Model
    let sheetModel_ = sheet_

    /// Optic to access BusWireT.Model from SheetT.Model
    let busWireModel_ = SheetT.wire_

    /// Optic to access SymbolT.Model from SheetT.Model
    let symbolModel_ = SheetT.symbol_

    /// allowed max X or y coord of svg canvas
    let maxSheetCoord = Sheet.Constants.defaultCanvasSize
    let middleOfSheet = {X=maxSheetCoord/2.;Y=maxSheetCoord/2.}

    /// Used throughout to compare labels since these are case invariant "g1" = "G1"
    let caseInvariantEqual str1 str2 =
        String.toUpper str1 = String.toUpper str2
 

    /// Identify a port from its component label and number.
    /// Usually both an input and output port will mathc this, so
    /// the port is only unique if it is known to be input or output.
    /// used to specify the ends of wires, since tehee are known to be
    /// connected to outputs (source) or inputs (target).
    type SymbolPort = { Label: string; PortNumber: int }

    /// convenience function to make SymbolPorts
    let portOf (label:string) (number: int) =
        {Label=label; PortNumber = number}


    //-----------------------------------------------------------------------------------------------
    // visibleSegments is included here as ahelper for info, and because it is needed in project work
    //-----------------------------------------------------------------------------------------------

    /// The visible segments of a wire, as a list of vectors, from source end to target end.
    /// Note that in a wire with n segments a zero length (invisible) segment at any index [1..n-2] is allowed 
    /// which if present causes the two segments on either side of it to coalesce into a single visible segment.
    /// A wire can have any number of visible segments - even 1.
    let visibleSegments (wId: ConnectionId) (model: SheetT.Model): XYPos list =

        let wire = model.Wire.Wires[wId] // get wire from model

        /// helper to match even and off integers in patterns (active pattern)
        let (|IsEven|IsOdd|) (n: int) = match n % 2 with | 0 -> IsEven | _ -> IsOdd

        /// Convert seg into its XY Vector (from start to end of segment).
        /// index must be the index of seg in its containing wire.
        let getSegmentVector (index:int) (seg: BusWireT.Segment) =
            // The implicit horizontal or vertical direction  of a segment is determined by 
            // its index in the list of wire segments and the wire initial direction
            match index, wire.InitialOrientation with
            | IsEven, BusWireT.Vertical | IsOdd, BusWireT.Horizontal -> {X=0.; Y=seg.Length}
            | IsEven, BusWireT.Horizontal | IsOdd, BusWireT.Vertical -> {X=seg.Length; Y=0.}

        /// Return the list of segment vectors with 3 vectors coalesced into one visible equivalent
        /// wherever this is possible
        let rec coalesce (segVecs: XYPos list)  =
            match List.tryFindIndex (fun segVec -> segVec =~ XYPos.zero) segVecs[1..segVecs.Length-2] with          
            | Some zeroVecIndex ->
                let index = zeroVecIndex + 1 // base index as it should be on full segVecs
                segVecs[0..index-2] @
                [segVecs[index-1] + segVecs[index+1]] @
                segVecs[index+2..segVecs.Length - 1]
                |> coalesce
            | None -> segVecs
     
        wire.Segments
        |> List.mapi getSegmentVector
        |> coalesce
                


//------------------------------------------------------------------------------------------------------------------------//
//------------------------------functions to build issue schematics programmatically--------------------------------------//
//------------------------------------------------------------------------------------------------------------------------//
    module Builder =


                

            



        /// Place a new symbol with label symLabel onto the Sheet with given position.
        /// Return error if symLabel is not unique on sheet, or if position is outside allowed sheet coordinates (0 - maxSheetCoord).
        /// To be safe place components close to (maxSheetCoord/2.0, maxSheetCoord/2.0).
        /// symLabel - the component label, will be uppercased to make a standard label name
        /// compType - the type of the component
        /// position - the top-left corner of the symbol outline.
        /// model - the Sheet model into which the new symbol is added.
        let placeSymbol (symLabel: string) (compType: ComponentType) (position: XYPos) (model: SheetT.Model) : Result<SheetT.Model, string> =
            let symLabel = String.toUpper symLabel // make label into its standard casing
            let symModel, symId = SymbolUpdate.addSymbol [] (model.Wire.Symbol) position compType symLabel
            let sym = symModel.Symbols[symId]
            match position + sym.getScaledDiagonal with
            | {X=x;Y=y} when x > maxSheetCoord || y > maxSheetCoord ->
                Error $"symbol '{symLabel}' position {position + sym.getScaledDiagonal} lies outside allowed coordinates"
            | _ ->
                model
                |> Optic.set symbolModel_ symModel
                |> SheetUpdateHelpers.updateBoundingBoxes // could optimise this by only updating symId bounding boxes
                |> Ok
        


    
        /// Place a new symbol onto the Sheet with given position and scaling (use default scale if this is not specified).
        /// The ports on the new symbol will be determined by the input and output components on some existing sheet in project.
        /// Return error if symLabel is not unique on sheet, or ccSheetName is not the name of some other sheet in project.
        let placeCustomSymbol
                (symLabel: string)
                (ccSheetName: string)
                (project: Project)
                (scale: XYPos)
                (position: XYPos)
                (model: SheetT.Model)
                    : Result<SheetT.Model, string> =
           let symbolMap = model.Wire.Symbol.Symbols
           if caseInvariantEqual ccSheetName project.OpenFileName then
                Error "Can't create custom component with name same as current opened sheet"        
            elif not <| List.exists (fun (ldc: LoadedComponent) -> caseInvariantEqual ldc.Name ccSheetName) project.LoadedComponents then
                Error "Can't create custom component unless a sheet already exists with smae name as ccSheetName"
            elif symbolMap |> Map.exists (fun _ sym ->  caseInvariantEqual sym.Component.Label symLabel) then
                Error "Can't create custom component with duplicate Label"
            else
                let canvas = model.GetCanvasState()
                let ccType: CustomComponentType =
                    {
                        Name = ccSheetName
                        InputLabels = Extractor.getOrderedCompLabels (Input1 (0, None)) canvas
                        OutputLabels = Extractor.getOrderedCompLabels (Output 0) canvas
                        Form = None
                        Description = None
                    }
                placeSymbol symLabel (Custom ccType) position model

        /// Rotate the symbol given by symLabel by an amount rotate.
        /// Takes in a symbol label, a rotate fixed amount, and a sheet containing the symbol.
        /// Return the sheet with the rotated symbol.
        let rotateSymbol (symLabel: string) (rotate: Rotation) (model: SheetT.Model) : (SheetT.Model) =

            let symbolsMap = model.Wire.Symbol.Symbols
            let getSymbol = 
                mapValues symbolsMap
                |> Array.tryFind (fun sym -> caseInvariantEqual sym.Component.Label symLabel)
                |> function | Some x -> Ok x | None -> Error "Can't find symbol with label '{symPort.Label}'"

            match getSymbol with
            | Ok symbol ->
                let rotatedSymbol = SymbolResizeHelpers.rotateSymbol rotate symbol
                let updatedSymbolsMap = Map.add symbol.Id rotatedSymbol symbolsMap
                { model with Wire = { model.Wire with Symbol = { model.Wire.Symbol with Symbols = updatedSymbolsMap } } }

            | _ -> model

        /// Flip the symbol given by symLabel by an amount flip.
        /// Takes in a symbol label, a flip fixed amount, and a sheet containing the symbol.
        /// Return the sheet with the flipped symbol.
        let flipSymbol (symLabel: string) (flip: SymbolT.FlipType) (model: SheetT.Model) : (SheetT.Model) =

            let symbolsMap = model.Wire.Symbol.Symbols
            let getSymbol =
                mapValues symbolsMap
                |> Array.tryFind (fun sym -> caseInvariantEqual sym.Component.Label symLabel)
                |> function | Some x -> Ok x | None -> Error "Can't find symbol with label '{symPort.Label}'"

            match getSymbol with
            | Ok symbol ->
                let flippedSymbol = SymbolResizeHelpers.flipSymbol flip symbol
                let updatedSymbolsMap = Map.add symbol.Id flippedSymbol symbolsMap
                { model with Wire = { model.Wire with Symbol = { model.Wire.Symbol with Symbols = updatedSymbolsMap } } }

            | _ -> model

        /// Add a (newly routed) wire, source specifies the Output port, target the Input port.
        /// Return an error if either of the two ports specified is invalid, or if the wire duplicates and existing one.
        /// The wire created will be smart routed but not separated from other wires: for a nice schematic
        /// separateAllWires should be run after  all wires are added.
        /// source, target: respectively the output port and input port to which the wire connects.
        let placeWire (source: SymbolPort) (target: SymbolPort) (model: SheetT.Model) : Result<SheetT.Model,string> =
            let symbols = model.Wire.Symbol.Symbols
            let getPortId (portType:PortType) symPort =
                mapValues symbols
                |> Array.tryFind (fun sym -> caseInvariantEqual sym.Component.Label symPort.Label)
                |> function | Some x -> Ok x | None -> Error "Can't find symbol with label '{symPort.Label}'"
                |> Result.bind (fun sym ->
                    match portType with
                    | PortType.Input -> List.tryItem symPort.PortNumber sym.Component.InputPorts
                    | PortType.Output -> List.tryItem symPort.PortNumber sym.Component.OutputPorts
                    |> function | Some port -> Ok port.Id
                                | None -> Error $"Can't find {portType} port {symPort.PortNumber} on component {symPort.Label}")
            
            match getPortId PortType.Input target, getPortId PortType.Output source with
            | Error e, _ | _, Error e -> Error e
            | Ok inPort, Ok outPort ->
                let newWire = BusWireUpdate.makeNewWire (InputPortId inPort) (OutputPortId outPort) model.Wire
                if model.Wire.Wires |> Map.exists (fun wid wire -> wire.InputPort=newWire.InputPort && wire.OutputPort = newWire.OutputPort) then
                        // wire already exists
                        Error "Can't create wire from {source} to {target} because a wire already exists between those ports"
                else
                     model
                     |> Optic.set (busWireModel_ >-> BusWireT.wireOf_ newWire.WId) newWire
                     |> Ok
            

        /// Run the global wire separation algorithm (should be after all wires have been placed and routed)
        let separateAllWires (model: SheetT.Model) : SheetT.Model =
            model
            |> Optic.map busWireModel_ (BusWireSeparate.updateWireSegmentJumpsAndSeparations (model.Wire.Wires.Keys |> Seq.toList))

        /// Copy testModel into the main Issie Sheet making its contents visible
        let showSheetInIssieSchematic (testModel: SheetT.Model) (dispatch: Dispatch<Msg>) =
            let sheetDispatch sMsg = dispatch (Sheet sMsg)
            dispatch <| UpdateModel (Optic.set sheet_ testModel) // set the Sheet component of the Issie model to make a new schematic.
            sheetDispatch <| SheetT.KeyPress SheetT.CtrlW // Centre & scale the schematic to make all components viewable.


        /// 1. Create a set of circuits from Gen<'a> samples by applying sheetMaker to each sample.
        /// 2. Check each ciruit with sheetChecker.
        /// 3. Return a TestResult record with errors those samples for which sheetChecker returns false,
        /// or where there is an exception.
        /// If there are any test errors display the first in Issie, and its error message on the console.
        /// sheetMaker: generates a SheetT.model from the random sample
        /// sheetChecker n model: n is sample number, model is the genrated model. Return false if test fails.
        let runTestOnSheets
            (name: string)
            (sampleToStartFrom: int)
            (samples : Gen<'a>)
            (sheetMaker: 'a -> SheetT.Model)
            (sheetChecker: int -> SheetT.Model -> string option)
            (dispatch: Dispatch<Msg>)
                : TestResult<'a> =
            let generateAndCheckSheet n = sheetMaker >> sheetChecker n
            let result =
                {
                    Name=name;
                    Samples=samples;
                    StartFrom = sampleToStartFrom
                    Assertion = generateAndCheckSheet
                }
                |> runTests
            match result.TestErrors with
            | [] -> // no errors
                printf $"Test {result.TestName} has PASSED."
            | (n,first):: _ -> // display in Issie editor and print out first error
                printf $"Test {result.TestName} has FAILED on sample {n} with error message:\n{first}"
                match catchException "" sheetMaker (samples.Data n) with
                | Ok sheet -> showSheetInIssieSchematic sheet dispatch
                | Error mess -> ()
            result

//--------------------------------------------------------------------------------------------------//
//----------------------------------------D3 Testing Helper functions-------------------------------//
//--------------------------------------------------------------------------------------------------//

        type componentInfo = {
            Label: string
            CompType: ComponentType
        }

        type connectionInfo = {
            SourceCompLabel: string
            SourceCompPort: int
            TargetCompLabel: string
            TargetCompPort: int
        }

        let genXYPos lim =
               let coords =
                   fromList [-lim..20..lim]
                   |> map (fun n -> float n)
               product (fun x y -> {X=x; Y=y}) coords coords

        let makePositions = genXYPos 10

        ///Generate samples for sheetWireLabelSymbol (D3) Easy Test.
        ///Return a Gen of flip, rotations and positions.
        let makeSamplesD3Easy =
            let rotations = fromList [Rotation.Degree90; Rotation.Degree270]
            let flips = fromList [Some SymbolT.FlipType.FlipHorizontal; None]
            genXYPos 10
            |> product (fun a b -> (a,b)) (genXYPos 10)
            |> product (fun a b -> (a,b)) rotations
            |> product (fun a b -> (a,b)) flips
            |> map (fun (flipMux,(rotMux,(demuxPos, muxPos))) ->
                {|
                    FlipMux = flipMux;
                    RotMux = rotMux;
                    DemuxPos = demuxPos;
                    Mux1Pos = muxPos;
                    Mux2Pos = muxPos
                |})

        ///Generate samples for sheetWireLabelSymbol (D3) Hard Test.
        ///Return a Gen of flip, rotations and positions.
        let makeSamplesD3Hard =
            let rotations = fromList [Rotation.Degree90; Rotation.Degree270]
            let flips = fromList [Some SymbolT.FlipType.FlipHorizontal; None]
            genXYPos 10
            |> product (fun a b -> (a,b)) (genXYPos 10)
            |> product (fun a b -> (a,b)) rotations
            |> product (fun a b -> (a,b)) flips
            |> map (fun (flipMux,(rotMux,(orPos, muxPos))) ->
                {|
                    FlipMux = flipMux;
                    RotMux = rotMux;
                    AndPos = muxPos;
                    OrPos = orPos;
                    XorPos = orPos;
                    MuxPos = muxPos
                |})

        ///Generates position for a component in circuit such that it's threshold distance away from another component.
        ///Takes the sheet and the required threshold distance between components.
        ///Returns X,Y coordinate position of the component in the sheet.
        let generatePosition (model: SheetT.Model) (threshold:float) : XYPos =
            let initPosition = middleOfSheet

            let getExistingPositions =
                model.Wire.Symbol.Symbols
                |> Map.toList
                |> List.map (fun (_, sym) -> sym.Pos)

            let rec findPosition (position: XYPos) =
                if getExistingPositions |> List.exists (fun existingPos ->
                    euclideanDistance existingPos position <= threshold) then
                    if random.Next(0,2) = 0 then
                        findPosition { position with X = position.X + threshold }
                    else
                        findPosition { position with Y = position.Y + threshold }
                else
                    position

            findPosition initPosition

        ///Places a list of components on a sheet such that they're all threshold distance apart with small random changes in position.
        ///Takes a list of components, threshold distance and set of X,Y coordinates to apply small changes in position.
        ///Returns a sheet with the components placed.
        let placeComponentsOnModel (components: componentInfo list) (threshold: float) (genSamples: XYPos) : SheetT.Model =
            components
            |> List.fold (fun currModel comp ->
                let compPosition = generatePosition currModel threshold 
                placeSymbol comp.Label comp.CompType (compPosition + genSamples) currModel
                |> getOkOrFail) initSheetModel

        ///Constructs a circuit using a list of components and randomly connects components at threshold distance apart.
        ///Takes a list of components, threshold distance and set of X,Y coordinates.
        ///Returns a sheet with components placed and wires routed.
        let randomConnectCircuitGen (components: componentInfo list) (threshold: float) (genSamples: XYPos) =
            components
            |> List.toArray
            |> shuffleA
            |> Array.pairwise
            |> Array.fold (fun currModel (comp1, comp2) ->
                placeWire (portOf comp1.Label 0) (portOf comp2.Label 0) currModel
                |> Result.bind (placeWire (portOf comp1.Label 0) (portOf comp2.Label 1))
                |> getOkOrFail
                |> separateAllWires
            ) (placeComponentsOnModel components threshold genSamples)

        ///Constructs a circuit by connecting a given list of components based on given list of connections at threshold distance apart.
        ///Takes a list of components, list of connections, threshold distance and set of X,Y coordinates.
        ///Returns a sheet with the components placed and wires routed.
        let fixedConnectCircuitGen (components: componentInfo list) (connections: connectionInfo list) (threshold: float) (genSamples: XYPos)=
            connections
            |> List.fold (fun currModel connect ->
                placeWire (portOf connect.SourceCompLabel connect.SourceCompPort) (portOf connect.TargetCompLabel connect.TargetCompPort) currModel
                |> getOkOrFail
                |> separateAllWires
            ) (placeComponentsOnModel components threshold genSamples)

        ///Counts number of overlapping symbols, number of wires intersecting symbols and number of wire bends in a given sheet.
        ///Takes in a sheet.
        ///Returns a record with the number of overlapping symbols, wires intersecting symbols and wire bends.
        let countMetrics (model: SheetT.Model) =
            //Metric 1: count number of symbols overlapping
            let boxes =
                model.BoundingBoxes
                |> Map.toList

            let numOfSymbolOverlaps =
                List.allPairs boxes boxes
                |> List.filter (fun ((n1, box1), (n2, box2)) -> (n1 <> n2) && BlockHelpers.overlap2DBox box1 box2)
                |> List.length

            //Metric 2: count number of wires intersecting symbols
            let wireModel = model.Wire
        
            let allWires = wireModel.Wires
                            |> Map.toList

            let numOfWireIntersectSym =
                allWires
                |> List.fold (fun totalCount (_, wire) -> 
                    totalCount + (BusWireRoute.findWireSymbolIntersections wireModel wire |> List.length)
                ) 0

            //Metric 3: Number of wire bends - use alvi function for right angles and apply for all wires in a sheet - use number of wire crossings
            //let numOfWireBends =
            //    T5 model
   
            {|SymbolOverlaps = numOfSymbolOverlaps; WireIntersectSym = numOfWireIntersectSym|}//; WireBends = numOfWireBends|}

        ///Runs tests through testFunction and prints the resultant metrics for each test.
        ///Takes in the set of tests, a test/circuit maker function, and a testing function.
        ///Prints the number of overlapping symbols, wires intersecting symbols and wire bends.
        let collectMetricsOfTests (samples: Gen<'a>) (sheetMaker: 'a -> SheetT.Model) (testFunction) =
            [0..samples.Size-1]
            |> List.map (fun n -> 
                let sample = samples.Data n
                let sheetModel = sheetMaker sample

                let result = testFunction sheetModel

                let metrics = countMetrics result
                (n, metrics))

            |> List.iter (fun (n, m) ->
                printfn "Sample %d Metrics:" n
                printfn "Symbol overlaps: %d" m.SymbolOverlaps
                printfn "Wire intersect symbols: %d" m.WireIntersectSym)
                //printfn "Wire bends: %d" m.WireBends)




//--------------------------------------------------------------------------------------------------//
//----------------------------------------Example Test Circuits using Gen<'a> samples---------------//
//--------------------------------------------------------------------------------------------------//

    open Builder
    open SheetBeautifyD2
    open SheetBeautifyD3

    /// Sample data based on 11 equidistant points on a horizontal line
    let horizLinePositions =
        fromList [-100..20..100]
        |> map (fun n -> middleOfSheet + {X=float n; Y=0.})

    /// demo test circuit consisting of a DFF & And gate
    let makeTest1Circuit (andPos:XYPos) =
        initSheetModel
        |> placeSymbol "G1" (GateN(And,2)) andPos
        |> Result.bind (placeSymbol "FF1" DFF middleOfSheet)
        |> Result.bind (placeWire (portOf "G1" 0) (portOf "FF1" 0))
        |> Result.bind (placeWire (portOf "FF1" 0) (portOf "G1" 0) )
        |> getOkOrFail

    let makeOFTestCircuitDemo1 (beautify: bool) =
        initSheetModel
        |> placeSymbol "MUX1" (Mux2) (middleOfSheet + { X = -700; Y = -500 })
        |> Result.bind (placeSymbol "MUX2" (Mux2) (middleOfSheet))
        |> Result.bind (placeSymbol "S1" (Input1 (1,None) ) (middleOfSheet + { X = -500; Y = -10 }))
(*        |> Result.bind (placeSymbol "S2" (Input1 (1,None)) (middleOfSheet + { X = -250; Y = 70 }))*)
   (*     |> Result.bind (placeSymbol "G1" (GateN (And,2)) (middleOfSheet + { X = 250; Y = -250 }))*)
        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "MUX2" 1))
(*        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "G1" 1))*)
        |> Result.bind (placeWire (portOf "S1" 0) (portOf "MUX2" 0))
(*        |> Result.bind (placeWire (portOf "S2" 0) (portOf "MUX2" 2))*)
(*        |> Result.bind (placeWire (portOf "MUX2" 0) (portOf "G1" 0))*)
        |> (fun res -> if beautify then Result.map sheetWireLabelSymbol res else res)
        |> Result.map autoRouteAllWires
        |> getOkOrFail

    let makeOFTestCircuitDemo2 (beautify: bool) =
        initSheetModel
        |> placeSymbol "MUX1" (Mux2) (middleOfSheet + { X = -700; Y = -300 })
        |> Result.bind (placeSymbol "MUX2" (Mux2) (middleOfSheet))
        |> Result.bind (placeSymbol "DEMUX2" (Demux2) (middleOfSheet + { X = -600; Y = -150 }))
        |> Result.bind (placeSymbol "G1" (GateN (And,2)) (middleOfSheet + { X = 400; Y = -100}))
        |> Result.bind (placeSymbol "G2" (GateN (Or,2)) (middleOfSheet + { X = 300; Y = 120}))
        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "MUX2" 1))
        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "G1" 1))
        |> Result.bind (placeWire (portOf "MUX2" 0) (portOf "G1" 0))
        |> Result.bind (placeWire (portOf "DEMUX2" 0) (portOf "MUX2" 2))
        |> Result.bind (placeWire (portOf "DEMUX2" 1) (portOf "MUX2" 0))
        |> Result.bind (placeWire (portOf "DEMUX2" 0) (portOf "G2" 0))
        |> Result.bind (placeWire (portOf "MUX2" 0) (portOf "G2" 1))
        |> (fun res -> if beautify then Result.map sheetWireLabelSymbol res else res)
        |> Result.map autoRouteAllWires
        |> getOkOrFail

//--------------------------------------------------------------------------------------------------//
//-------------------------Example Test Circuits using Gen<'a> samples testing D3 Function----------//
//--------------------------------------------------------------------------------------------------//

    ///Generate easy/likely-to-pass tests for sheetWireLabelSymbol (D3) with all components being threshold distance apart.
    ///Takes in a threshold distance between components
    ///Returns a sheet with the circuit placed on it.
    let makeTestD3Easy (threshold: float) (beautify: bool) (sample: {|FlipMux: SymbolT.FlipType option; RotMux: Rotation; DemuxPos: XYPos; Mux1Pos: XYPos; Mux2Pos: XYPos|}) : SheetT.Model = 
        //so recreate figure C1 so long wires
        let s = sample
        initSheetModel
        //adding DM1
        //adding small changes to position of components, changes given by a gen
        |> placeSymbol "DM1" (Demux4) (middleOfSheet - {X=threshold; Y=0.} + s.DemuxPos)
        //adding MUX1
        |> Result.bind (placeSymbol "MUX1" (Mux4) (middleOfSheet + s.Mux1Pos))
        |> Result.map (rotateSymbol "MUX1" s.RotMux)
        |> match s.FlipMux with
            | Some f -> Result.map (flipSymbol "MUX1" f) //so if flip is not none hten do result.map else do identity function to pass model as is without flip
            | None -> id
        |> Result.bind (placeWire (portOf "DM1" 0) (portOf "MUX1" 0))
        |> Result.bind (placeWire (portOf "DM1" 1) (portOf "MUX1" 1))
        |> Result.bind (placeWire (portOf "DM1" 2) (portOf "MUX1" 2))
        |> Result.bind (placeWire (portOf "DM1" 3) (portOf "MUX1" 3))
        //adding MUX2
        |> Result.bind (placeSymbol "MUX2" (Mux4) (middleOfSheet + {X=0.; Y=threshold} + s.Mux2Pos))
        |> Result.bind (placeWire (portOf "DM1" 0) (portOf "MUX2" 0))
        |> Result.bind (placeWire (portOf "DM1" 1) (portOf "MUX2" 1))
        |> Result.bind (placeWire (portOf "DM1" 2) (portOf "MUX2" 2))
        |> Result.bind (placeWire (portOf "DM1" 3) (portOf "MUX2" 3))
        |> (fun res -> if beautify then Result.map sheetWireLabelSymbol res else res)
        |> Result.map autoRouteAllWires
        |> getOkOrFail
        //|> separateAllWires

    ///Generate hard/likely-to-fail tests for sheetWireLabelSymbol (D3) with all components being threshold distance apart.
    ///Takes in a threshold distance between components
    ///Returns a sheet with the circuit placed on it.
    let makeTestD3Hard (threshold: float) (sample: {|FlipMux: SymbolT.FlipType option; RotMux: Rotation; AndPos: XYPos; OrPos: XYPos; XorPos: XYPos; MuxPos: XYPos|}) : SheetT.Model =    
        //so recreate figure C1 so long wires
        let s = sample
        initSheetModel
        //adding DM1
        //adding small changes to position of components, changes given by a gen
        |> placeSymbol "MUX1" (Mux2) (middleOfSheet - {X=threshold; Y=0.} + s.MuxPos)
        //adding MUX1
        |> Result.bind (placeSymbol "OR1" (GateN(Or, 2)) (middleOfSheet + s.OrPos))
        |> Result.map (rotateSymbol "MUX1" s.RotMux)
        |> match s.FlipMux with
            | Some f -> Result.map (flipSymbol "MUX1" f) //so if flip is not none hten do result.map else do identity function to pass model as is without flip
            | None -> id
        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "OR1" 0))
        |> Result.bind (placeWire (portOf "MUX1" 0) (portOf "OR1" 1))
        |> Result.bind (placeSymbol "AND1" (GateN(And, 2)) (middleOfSheet + {X=threshold; Y=threshold} + s.AndPos))
        |> Result.bind (placeWire (portOf "OR1" 0) (portOf "AND1" 0))
        |> Result.bind (placeWire (portOf "OR1" 0) (portOf "AND1" 1))
        |> Result.bind (placeWire (portOf "OR1" 0) (portOf "MUX1" 2))
        |> Result.bind (placeWire (portOf "AND1" 0) (portOf "MUX1" 1))
        |> Result.bind (placeSymbol "XOR1" (GateN(Xor, 2)) (middleOfSheet + {X=threshold; Y=0.} + s.XorPos))
        |> Result.bind (placeWire (portOf "AND1" 0) (portOf "XOR1" 0))
        |> Result.bind (placeWire (portOf "AND1" 0) (portOf "XOR1" 1))
        |> Result.bind (placeWire (portOf "XOR1" 0) (portOf "MUX1" 0))
        |> getOkOrFail
        |> separateAllWires

    ///Generate tests using circuit generation DSL with given components and connections list
    ///Takes in a threshold distance between components and X,Y position.
    ///Returns a sheet with the circuit placed on it.
    let makeTestFixedConnectCircuitGen (threshold: float) (genSamples : XYPos) : SheetT.Model =
        let components = [
            { Label = "AND1"; CompType = GateN(And, 2) };
            { Label = "OR1"; CompType = GateN(Or, 2) };
            { Label = "XOR1"; CompType = GateN(Xor, 2) };
            { Label = "MUX1"; CompType = Mux2 };
        ]

        let connections = [
            { SourceCompLabel = "AND1"; SourceCompPort = 0; TargetCompLabel = "OR1"; TargetCompPort = 0 };
            { SourceCompLabel = "OR1"; SourceCompPort = 0; TargetCompLabel = "XOR1"; TargetCompPort = 1 };
            { SourceCompLabel = "XOR1"; SourceCompPort = 0; TargetCompLabel = "MUX1"; TargetCompPort = 1 };
            { SourceCompLabel = "MUX1"; SourceCompPort = 0; TargetCompLabel = "AND1"; TargetCompPort = 1 };
        ]

        fixedConnectCircuitGen components connections threshold genSamples

    ///Generate tests using circuit generation DSL with only given components list
    ///Takes in a threshold distance between components and X,Y position.
    ///Returns a sheet with the circuit placed on it.
    let makeTestRandomConnectCircuitGen (threshold: float) (genSamples : XYPos) : SheetT.Model =
        let components = [
            { Label = "AND1"; CompType = GateN(And, 2) };
            { Label = "OR1"; CompType = GateN(Or, 2) };
            { Label = "XOR1"; CompType = GateN(Xor, 2) };
            { Label = "MUX1"; CompType = Mux2 };
        ]

        randomConnectCircuitGen components threshold genSamples

//------------------------------------------------------------------------------------------------//
//-------------------------Example assertions used to test sheets---------------------------------//
//------------------------------------------------------------------------------------------------//


    module Asserts =

        (* Each assertion function from this module has as inputs the sample number of the current test and the corresponding schematic sheet.
           It returns a boolean indicating (true) that the test passes or 9false) that the test fails. The sample numbr is included to make it
           easy to document tests and so that any specific sampel schematic can easily be displayed using failOnSampleNumber. *)

        /// Ignore sheet and fail on the specified sample, useful for displaying a given sample
        let failOnSampleNumber (sampleToFail :int) (sample: int) _sheet =
            if sampleToFail = sample then
                Some $"Failing forced on Sample {sampleToFail}."
            else
                None

        /// Fails all tests: useful to show in sequence all the sheets generated in a test
        let failOnAllTests (sample: int) _ =
            Some <| $"Sample {sample}"

        /// Fail when sheet contains a wire segment that overlaps (or goes too close to) a symbol outline  
        let failOnWireIntersectsSymbol (sample: int) (sheet: SheetT.Model) =
            let wireModel = sheet.Wire
            wireModel.Wires
            |> Map.exists (fun _ wire -> BusWireRoute.findWireSymbolIntersections wireModel wire <> [])
            |> (function | true -> Some $"Wire intersects a symbol outline in Sample {sample}"
                         | false -> None)

        /// Fail when sheet contains two symbols which overlap
        let failOnSymbolIntersectsSymbol (sample: int) (sheet: SheetT.Model) =
            let wireModel = sheet.Wire
            let boxes =
                mapValues sheet.BoundingBoxes
                |> Array.toList
                |> List.mapi (fun n box -> n,box)
            List.allPairs boxes boxes 
            |> List.exists (fun ((n1,box1),(n2,box2)) -> (n1 <> n2) && BlockHelpers.overlap2DBox box1 box2)
            |> (function | true -> Some $"Symbol outline intersects another symbol outline in Sample {sample}"
                         | false -> None)

        ///Fails when sheet contains two overlapping symbols or a wire intersecting symbol
        ///Takes in a sample number for the current test and a sheet.
        ///Returns a fail or success.
        let failD3 (sample: int) (sheet: SheetT.Model) =
            let metrics = countMetrics sheet

            if metrics.SymbolOverlaps > 0 || metrics.WireIntersectSym > 0 then
                Some ($"Test failed on sample {sample}: {metrics.SymbolOverlaps} symbol overlaps and {metrics.WireIntersectSym} wire symbol intersections.")
            else
                None

//---------------------------------------------------------------------------------------//
//-----------------------------Demo tests on Draw Block code-----------------------------//
//---------------------------------------------------------------------------------------//

    module Tests =

        /// Allow test errors to be viewed in sequence by recording the current error
        /// in the Issie Model (field DrawblockTestState). This contains all Issie persistent state.
        let recordPositionInTest (testNumber: int) (dispatch: Dispatch<Msg>) (result: TestResult<'a>) =
            dispatch <| UpdateDrawBlockTestState(fun _ ->
                match result.TestErrors with
                | [] ->
                    printf "Test finished"
                    None
                | (numb, _) :: _ ->
                    printf $"Sample {numb}"
                    Some { LastTestNumber=testNumber; LastTestSampleIndex= numb})
            
        /// Example test: Horizontally positioned AND + DFF: fail on sample 0
        let test1 testNum firstSample dispatch =
            runTestOnSheets
                "Horizontally positioned AND + DFF: fail on sample 0"
                firstSample
                horizLinePositions
                makeTest1Circuit
                (Asserts.failOnSampleNumber 0)
                dispatch
            |> recordPositionInTest testNum dispatch

        /// Example test: Horizontally positioned AND + DFF: fail on sample 10
        let test2 testNum firstSample dispatch =
            runTestOnSheets
                "Horizontally positioned AND + DFF: fail on sample 10"
                firstSample
                horizLinePositions
                makeTest1Circuit
                (Asserts.failOnSampleNumber 10)
                dispatch
            |> recordPositionInTest testNum dispatch

        /// Example test: Horizontally positioned AND + DFF: fail on symbols intersect
        let test3 testNum firstSample dispatch =
            runTestOnSheets
                "Horizontally positioned AND + DFF: fail on symbols intersect"
                firstSample
                horizLinePositions
                makeTest1Circuit
                Asserts.failOnSymbolIntersectsSymbol
                dispatch
            |> recordPositionInTest testNum dispatch

        /// Example test: Horizontally positioned AND + DFF: fail all tests
        let test4 testNum firstSample dispatch =
            runTestOnSheets
                "Horizontally positioned AND + DFF: fail all tests"
                firstSample
                horizLinePositions
                makeTest1Circuit
                Asserts.failOnAllTests
                dispatch
            |> recordPositionInTest testNum dispatch

        /// sheetOrderFlip test: Default circuit in Figure B1: fail all tests
        let test5 testNum firstSample dispatch =
            runTestOnSheets
                "Default circuit in Figure B1: fail all tests"
                firstSample
                (fromList [ false; true ])
                makeOFTestCircuitDemo1
                Asserts.failOnAllTests
                dispatch
            |> recordPositionInTest testNum dispatch

        /// sheetOrderFlip test: Multiple muxes and gates: fail all tests
        let test6 testNum firstSample dispatch =
            runTestOnSheets
                "Multiple muxes and gates: fail all tests"
                firstSample
                (fromList [ false; true ])
                makeOFTestCircuitDemo2
                Asserts.failOnAllTests
                dispatch
            |> recordPositionInTest testNum dispatch

        let testD3Easy testNum firstSample dispatch =
            let threshold = 200.0
            runTestOnSheets
                "2 Mux4 and 1 DeMux threshold distance apart: fail on overlapping symbols or symbol wire intersect"
                firstSample
                makeSamplesD3Easy                
                (makeTestD3Easy threshold false)
                //Asserts.failD3
                (Asserts.failOnSampleNumber 10)
                dispatch
            |> recordPositionInTest testNum dispatch
            (collectMetricsOfTests makeSamplesD3Easy (makeTestD3Easy threshold false) id)

        let testD3Hard testNum firstSample dispatch =
            let threshold = 200.0
            runTestOnSheets
                "Mux2, AND, OR, XOR threshold distance apart: fail on overlapping symbols or symbol wire intersect"
                firstSample
                makeSamplesD3Hard
                (makeTestD3Hard threshold)
                //Asserts.failD3
                (Asserts.failOnSampleNumber 10)
                dispatch
            |> recordPositionInTest testNum dispatch

        let testFixedConnectCircuitGen testNum firstSample dispatch =
            let threshold = 200.0
            runTestOnSheets
                "Circuit made of components and connections list, threshold distance apart: fail on overlapping symbols or symbol wire intersect"
                firstSample
                makePositions
                (makeTestFixedConnectCircuitGen threshold)
                Asserts.failD3
                dispatch
            |> recordPositionInTest testNum dispatch

        let testRandomConnectCircuitGen testNum firstSample dispatch =
            let threshold = 200.0
            runTestOnSheets
                "Circuit made of components list and randomly connected components, threshold distance apart: fail on overlapping symbols or symbol wire intersect"
                firstSample
                makePositions
                (makeTestRandomConnectCircuitGen threshold)
                Asserts.failD3
                dispatch
            |> recordPositionInTest testNum dispatch

        /// List of tests available which can be run ftom Issie File Menu.
        /// The first 9 tests can also be run via Ctrl-n accelerator keys as shown on menu
        let testsToRunFromSheetMenu : (string * (int -> int -> Dispatch<Msg> -> Unit)) list =
            // Change names and test functions as required
            // delete unused tests from list
            [
                "Test1", test1 // example
                "Test2", testD3Easy // example
                "Test3", test3 // example
                "Test4", test4 // example
                "Test5", test5 // sheetOrderFlip: default circuit
                "Test6", test6 // sheetOrderFlip: multiple mux and gates
                "Test7", fun _ _ _ -> printf "Test7: Empty"
                "Test8", fun _ _ _ -> printf "Test8: Empty"
                "Next Test Error", fun _ _ _ -> printf "Next Error:" // Go to the nexterror in a test
            ]

        /// Display the next error in a previously started test
        let nextError (testName, testFunc) firstSampleToTest dispatch =
            let testNum =
                testsToRunFromSheetMenu
                |> List.tryFindIndex (fun (name,_) -> name = testName)
                |> Option.defaultValue 0
            testFunc testNum firstSampleToTest dispatch

        /// common function to execute any test.
        /// testIndex: index of test in testsToRunFromSheetMenu
        let testMenuFunc (testIndex: int) (dispatch: Dispatch<Msg>) (model: Model) =
            let name,func = testsToRunFromSheetMenu[testIndex] 
            printf "%s" name
            match name, model.DrawBlockTestState with
            | "Next Test Error", Some state ->
                nextError testsToRunFromSheetMenu[state.LastTestNumber] (state.LastTestSampleIndex+1) dispatch
            | "Next Test Error", None ->
                printf "Test Finished"
                ()
            | _ ->
                func testIndex 0 dispatch
        


    
=======
>>>>>>> Stashed changes

