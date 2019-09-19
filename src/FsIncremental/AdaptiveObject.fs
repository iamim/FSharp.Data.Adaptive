﻿namespace FsIncremental

open System
open System.Threading
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Collections
open System.Collections.Generic

#nowarn "9"


/// <summary>
/// IAdaptiveObject represents the core interface for all
/// adaptive objects and contains everything necessary for
/// tracking OutOfDate flags and managing in-/outputs in the
/// dependency tree.
///
/// Since eager evalutation might be desirable in some scenarios
/// the interface also contains a Level representing the execution
/// order when evaluating inside a transaction and a function called
/// Mark allowing implementations to actually perform the evaluation.
/// Mark returns a bool since eager evaluation might cause the change
/// propagation process to exit early (if the actual value was unchanged)
/// In order to make adaptive objects easily identifiable all adaptive
/// objects must also provide a globally unique id (Id)
/// </summary>
[<AllowNullLiteral>]
type IAdaptiveObject =
    abstract member Weak : WeakReference<IAdaptiveObject>
    /// <summary>
    /// the level for an adaptive object represents the
    /// maximal distance from an input cell in the depdency graph
    /// Note that this level is entirely managed by the system 
    /// and shall not be accessed directly by users of the system.
    /// </summary>
    abstract member Level : int with get, set

    /// <summary>
    /// Mark allows a specific implementation to
    /// evaluate the cell during the change propagation process.
    /// </summary>
    abstract member Mark : unit -> bool

    /// <summary>
    /// the outOfDate flag for the object is true
    /// whenever the object has been marked and shall
    /// be set to false by specific implementations.
    /// Note that this flag shall only be accessed when holding
    /// a lock on the adaptive object (allowing for concurrency)
    /// </summary>
    abstract member OutOfDate : bool with get, set

    /// <summary>
    /// the adaptive outputs for the object which are recommended
    /// to be represented by Weak references in order to allow for
    /// unused parts of the graph to be garbage collected.
    /// </summary>
    abstract member Outputs : WeakOutputSet


    abstract member InputChanged : obj * IAdaptiveObject -> unit
    abstract member AllInputsProcessed : obj -> unit
    abstract member ReaderCount : int with get, set



and [<StructLayout(LayoutKind.Explicit)>] private VolatileSetData =
    struct
        [<FieldOffset(0)>]
        val mutable public Single : WeakReference<IAdaptiveObject>
        [<FieldOffset(0)>]
        val mutable public Array : WeakReference<IAdaptiveObject>[]
        [<FieldOffset(0)>]
        val mutable public Set : HashSet<WeakReference<IAdaptiveObject>>
        [<FieldOffset(8)>]
        val mutable public Tag : int
    end

and WeakOutputSet() =
    
    let mutable data = Unchecked.defaultof<VolatileSetData>
    let mutable setOps = 0

    member x.Cleanup() =
        lock x (fun () ->
            if setOps > 100 then
                setOps <- 0
                let all = x.Consume()
                for a in all do x.Add a |> ignore
        )

    member x.Add(obj : IAdaptiveObject) =
        lock x (fun () ->
            let mutable value = Unchecked.defaultof<IAdaptiveObject>

            let weakObj = obj.Weak
            match data.Tag with
            | 0 ->  
                if isNull data.Single then 
                    data.Single <- weakObj
                    true
                elif data.Single = weakObj then
                    false
                elif data.Single.TryGetTarget(&value) then
                    if Object.ReferenceEquals(value, obj) then
                        false
                    else
                        let arr = Array.zeroCreate 8
                        arr.[0] <- data.Single
                        arr.[1] <- weakObj
                        data.Tag <- 1
                        data.Array <- arr
                        true
                else
                    data.Single <- weakObj
                    true
            | 1 ->
                let mutable freeIndex = -1
                let mutable i = 0
                let len = data.Array.Length
                while i < len do
                    if isNull data.Array.[i] then
                        if freeIndex < 0 then freeIndex <- i
                    elif data.Array.[i] = weakObj then
                        freeIndex <- -2
                        i <- len
                    else
                        if data.Array.[i].TryGetTarget(&value) then
                            if Object.ReferenceEquals(value, obj) then
                                freeIndex <- -2
                                i <- len
                        else
                            if freeIndex < 0 then freeIndex <- i
                    i <- i + 1

                if freeIndex = -2 then
                    false
                elif freeIndex >= 0 then
                    data.Array.[freeIndex] <- weakObj
                    true
                else
                    // r cannot be null here (empty index would have been found)
                    let all = data.Array |> Array.choose (fun r -> if r.TryGetTarget(&value) then Some r else None)
                    let set = HashSet all
                    let res = set.Add weakObj
                    data.Tag <- 2
                    data.Set <- set
                    res
            | _ ->
                if data.Set.Add weakObj then
                    setOps <- setOps + 1
                    x.Cleanup()
                    true
                else
                    false
        )

    member x.Remove(obj : IAdaptiveObject) =
        lock x (fun () ->
            //let obj = obj.WeakSelf
            let mutable old = Unchecked.defaultof<IAdaptiveObject>

            match data.Tag with
            | 0 ->  
                if isNull data.Single then
                    false
                else
                    if data.Single.TryGetTarget(&old) then
                        if Object.ReferenceEquals(old, obj) then
                            data.Single <- null
                            true
                        else
                            false
                    else
                        data.Single <- null
                        false
            | 1 ->
                let mutable found = false
                let mutable i = 0
                let len = data.Array.Length
                let mutable count = 0
                let mutable living = null
                while i < len do
                    if not (isNull data.Array.[i]) then
                        let ref = data.Array.[i]
                        if ref.TryGetTarget(&old) then
                            if Object.ReferenceEquals(old, obj) then
                                data.Array.[i] <- null
                                found <- true
                            else
                                count <- count + 1
                                living <- ref
                        else
                            data.Array.[i] <- null
                    i <- i + 1

                if count = 0 then
                    data.Tag <- 0
                    data.Single <- null
                elif count = 1 then
                    data.Tag <- 0
                    data.Single <- living

                found
     
            | _ ->  
                if data.Set.Remove obj.Weak then
                    setOps <- setOps + 1
                    x.Cleanup()
                    true
                else
                    false
        )

    member x.Consume() : IAdaptiveObject[] =
        lock x (fun () ->
            let n = data
            data <- Unchecked.defaultof<_>
            setOps <- 0
            match n.Tag with
            | 0 ->  
                if isNull n.Single then 
                    [||]
                else 
                    match n.Single.TryGetTarget() with
                    | (true, v) -> [| v |]
                    | _ -> [||]
            | 1 ->  
                n.Array |> Array.choose (fun r ->
                    if isNull r then None
                    else 
                        match r.TryGetTarget() with
                        | (true, v) -> Some v
                        | _ -> None
                )
            | _ ->
                let mutable cnt = 0
                let mutable arr = Array.zeroCreate n.Set.Count
                let mutable o = Unchecked.defaultof<_>
                for r in n.Set do
                    if r.TryGetTarget(&o) then
                        arr.[cnt] <- o
                        cnt <- cnt + 1
                if cnt < arr.Length then Array.Resize(&arr, cnt)
                arr
        )

module WeakOutputSet =
    let inline create () = WeakOutputSet()

    let inline add (o : IAdaptiveObject) (set : WeakOutputSet) =
        set.Add o

    let inline remove (o : IAdaptiveObject) (set : WeakOutputSet) =
        set.Remove o

exception LevelChangedException of IAdaptiveObject * int * int

[<AutoOpen>]
module LockingExtensions =
    type IAdaptiveObject with
        member inline o.EnterWrite() =
            Monitor.Enter o
            while o.ReaderCount > 0 do
                Monitor.Wait o |> ignore
            
        member inline o.ExitWrite() =
            Monitor.Exit o
        
        member inline o.IsOutdatedCaller() =
            Monitor.IsEntered o && o.OutOfDate


type AdaptiveToken =
    struct
        val mutable public Caller : IAdaptiveObject
        val mutable public Locked : HashSet<IAdaptiveObject>

        member inline x.EnterRead(o : IAdaptiveObject) =
            Monitor.Enter o
                
        member inline x.ExitFaultedRead(o : IAdaptiveObject) =
            Monitor.Exit o

        member inline x.Downgrade(o : IAdaptiveObject) =
            if x.Locked.Add o then
                o.ReaderCount <- o.ReaderCount + 1
            Monitor.Exit o

        member inline x.ExitRead(o : IAdaptiveObject) =
            if x.Locked.Remove o then
                lock o (fun () ->
                    let rc = o.ReaderCount - 1
                    o.ReaderCount <- rc
                    if rc = 0 then Monitor.PulseAll o
                )

        member inline x.Release() =
            for o in x.Locked do
                lock o (fun () ->
                    let rc = o.ReaderCount - 1
                    o.ReaderCount <- rc
                    if rc = 0 then Monitor.PulseAll o
                )
            x.Locked.Clear()



        member inline x.WithCaller (c : IAdaptiveObject) =
            AdaptiveToken(c, x.Locked)

        member inline x.WithTag (t : obj) =
            AdaptiveToken(x.Caller, x.Locked)


        member inline x.Isolated =
            AdaptiveToken(x.Caller, HashSet())

        static member inline Top = AdaptiveToken(null, HashSet())
        static member inline Empty = Unchecked.defaultof<AdaptiveToken>

        new(caller : IAdaptiveObject, locked : HashSet<IAdaptiveObject>) =
            {
                Caller = caller
                Locked = locked
            }
    end