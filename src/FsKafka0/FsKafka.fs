namespace FsKafka

open Confluent.Kafka
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Serilog
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

module Binding =

    let message (x : Confluent.Kafka.Message<string, string>) = x
    let offsetValue (x : Offset) : int64 = x.Value
    let partitionValue (partition : int) : int = partition
    let internal makeTopicPartition (topic : string) (partition : int) = TopicPartition(topic, partition)
    let internal offsetUnset = Offset.Invalid

/// Defines config/semantics for grouping of messages into message sets in order to balance:
/// - Latency per produce call
/// - Using maxInFlight=1 to prevent message sets getting out of order in the case of failure
type Batching =
    /// Produce individually, lingering for throughput+compression. Confluent.Kafka < 1.5 default: 0.5ms. Confluent.Kafka >= 1.5 default: 5ms
    | Linger of linger : TimeSpan
    /// Use in conjunction with BatchedProducer.ProduceBatch to to obtain best-effort batching semantics (see comments in BatchedProducer for more detail)
    /// Uses maxInFlight=1 batch so failed transmissions should be much less likely to result in broker appending items out of order
    | BestEffortSerial of linger : TimeSpan
    /// Apply custom-defined settings. Not recommended.
    /// NB Having a <> 1 value for maxInFlight runs two risks due to the intrinsic lack of batching mechanisms within the Confluent.Kafka client:
    /// 1) items within the initial 'batch' can get written out of order in the face of timeouts and/or retries
    /// 2) items beyond the linger period may enter a separate batch, which can potentially get scheduled for transmission out of order
    | Custom of linger : TimeSpan * maxInFlight : int

/// See https://github.com/edenhill/librdkafka/blob/master/CONFIGURATION.md for documentation on the implications of specific settings
[<NoComparison>]
type KafkaProducerConfig private (inner, bootstrapServers : string) =
    member __.Inner : ProducerConfig = inner
    member __.BootstrapServers = bootstrapServers
    member __.Acks = let v = inner.Acks in v.Value
    member __.Linger = let v = inner.LingerMs in v.Value
    member __.MaxInFlight = let v = inner.MaxInFlight in v.Value
    member __.Compression = let v = inner.CompressionType in v.GetValueOrDefault(CompressionType.None)

    /// Creates and wraps a Confluent.Kafka ProducerConfig with the specified settings
    static member Create
        (   clientId : string, bootstrapServers : string, acks,
            /// Defines combination of linger/maxInFlight settings to effect desired batching semantics
            batching : Batching,
            /// Message compression. Default: None.
            ?compression,
            /// Number of retries. Confluent.Kafka default: 2. Default: 60.
            ?retries,
            /// Backoff interval. Confluent.Kafka default: 100ms. Default: 1s.
            ?retryBackoff,
            /// Ack timeout (assuming Acks != Acks.0). Confluent.Kafka default: 5s.
            ?requestTimeout,
            /// Statistics Interval. Default: no stats.
            ?statisticsInterval,
            /// Confluent.Kafka default: false. Defaults to true.
            ?socketKeepAlive,
            /// Partition algorithm. Default: `ConsistentRandom`.
            ?partitioner,
            /// Miscellaneous configuration parameters to be passed to the underlying Confluent.Kafka producer configuration. Same as constructor argument for Confluent.Kafka >=1.2.
            ?config : IDictionary<string,string>,
            /// Miscellaneous configuration parameters to be passed to the underlying Confluent.Kafka producer configuration.
            ?custom,
            /// Postprocesses the ProducerConfig after the rest of the rules have been applied
            ?customize) =
        let linger, maxInFlight =
            match batching with
            | Linger l -> l, None
            | BestEffortSerial l -> l, Some 1
            | Custom (l,m) -> l, Some m
        let c =
            ProducerConfig(
                ClientId = clientId, BootstrapServers = bootstrapServers,
                RetryBackoffMs = Nullable (match retryBackoff with Some (t : TimeSpan) -> int t.TotalMilliseconds | None -> 1000), // CK default 100ms
                MessageSendMaxRetries = Nullable (defaultArg retries 60), // default 2
                Acks = Nullable acks,
                SocketKeepaliveEnable = Nullable (defaultArg socketKeepAlive true), // default: false
                LogConnectionClose = Nullable false, // https://github.com/confluentinc/confluent-kafka-dotnet/issues/124#issuecomment-289727017
                LingerMs=Nullable (int linger.TotalMilliseconds), // default 0.5ms (CK 1.5 makes default 5ms)
                MaxInFlight=Nullable (defaultArg maxInFlight 1_000_000)) // default 1_000_000
        config |> Option.iter (fun xs -> for KeyValue (k,v) in xs do c.Set(k,v))
        partitioner |> Option.iter (fun x -> c.Partitioner <- Nullable x)
        compression |> Option.iter (fun x -> c.CompressionType <- Nullable x)
        requestTimeout |> Option.iter<TimeSpan> (fun x -> c.RequestTimeoutMs <- Nullable (int x.TotalMilliseconds))
        statisticsInterval |> Option.iter<TimeSpan> (fun x -> c.StatisticsIntervalMs <- Nullable (int x.TotalMilliseconds))
        custom |> Option.iter (fun xs -> for KeyValue (k,v) in xs do c.Set(k,v))
        customize |> Option.iter (fun f -> f c)
        KafkaProducerConfig(c, bootstrapServers)
    /// Creates and wraps a Confluent.Kafka ProducerConfig with the specified settings
    [<Obsolete "linger is now mandatory as a result of Confluent.Kafka 1.5's changing the default from 0.5ms to 5ms">]
    // TODO remove in 2.0.0
    static member Create
        (   clientId : string, bootstrapServers : string, acks,
            /// Message compression. Default: None.
            ?compression,
            /// Maximum in-flight requests. Default: 1_000_000.
            /// NB <> 1 implies potential reordering of writes should a batch fail and then succeed in a subsequent retry
            ?maxInFlight,
            /// Time to wait for other items to be produced before sending a batch. Default: 0ms.
            /// NB the linger setting alone does provide any hard guarantees; see BatchedProducer.Create/ProduceBatch
            ?linger : TimeSpan,
            /// Number of retries. Confluent.Kafka default: 2. Default: 60.
            ?retries,
            /// Backoff interval. Confluent.Kafka default: 100ms. Default: 1s.
            ?retryBackoff,
            /// Statistics Interval. Default: no stats.
            ?statisticsInterval,
            /// Ack timeout (assuming Acks != Acks.Zero). Confluent.Kafka default: 5s.
            ?requestTimeout,
            /// Confluent.Kafka default: false. Defaults to true.
            ?socketKeepAlive,
            /// Partition algorithm. Default: `ConsistentRandom`.
            ?partitioner,
            /// Miscellaneous configuration parameters to be passed to the underlying Confluent.Kafka producer configuration. Same as constructor argument for Confluent.Kafka >=1.2.
            ?config : IDictionary<string,string>,
            /// Miscellaneous configuration parameters to be passed to the underlying Confluent.Kafka producer configuration.
            ?custom,
            /// Postprocesses the ProducerConfig after the rest of the rules have been applied
            ?customize) =
        KafkaProducerConfig.Create(
            clientId, bootstrapServers, acks, Custom (defaultArg linger (TimeSpan.FromMilliseconds 0.5), defaultArg maxInFlight 1_000_000),
            ?compression=compression, ?retries=retries, ?retryBackoff=retryBackoff,
            ?statisticsInterval=statisticsInterval, ?requestTimeout=requestTimeout, ?socketKeepAlive=socketKeepAlive,
            ?partitioner=partitioner, ?config=config, ?custom=custom, ?customize=customize)

[<AutoOpen>]
module Impl =
    let encoding = System.Text.Encoding.UTF8
    let mkSerializer() = Confluent.Kafka.Serialization.StringSerializer(encoding)
    let mkDeserializer() = Confluent.Kafka.Serialization.StringDeserializer(encoding)

/// Creates and wraps a Confluent.Kafka Producer with the supplied configuration
type KafkaProducer private (inner : Producer<string, string>, topic : string, unsub) =
    member __.Inner = inner
    member __.Topic = topic

    interface IDisposable with member __.Dispose() = unsub(); inner.Dispose()

    /// Produces a single item, yielding a response upon completion/failure of the ack
    /// <remarks>
    ///     There's no assurance of ordering [without dropping `maxInFlight` down to `1` and annihilating throughput].
    ///     Thus its critical to ensure you don't submit another message for the same key until you've had a success / failure response from the call.<remarks/>
    member __.ProduceAsync(key, value) : Async<DeliveryResult<string, string>> = async {
        let! res = inner.ProduceAsync(topic, key = key, ``val`` = value) |> Async.AwaitTaskCorrect
        // Propulsion.Kafka.Producer duplicates this check, but this one should remain for consistency with Confluent.Kafka v1
        if res.Error.HasError then return failwithf "ProduceAsync error %O" res.Error
        return res }

    static member Create(log : ILogger, config : KafkaProducerConfig, topic : string): KafkaProducer =
        if String.IsNullOrEmpty topic then nullArg "topic"
        log.Information("Producing... {bootstrapServers} / {topic} compression={compression} acks={acks} linger={lingerMs}",
            config.BootstrapServers, topic, config.Compression, config.Acks, config.Linger)
        let p = new Producer<string, string>(config.Inner.Render(), mkSerializer (), mkSerializer())
        let d1 = p.OnLog.Subscribe(fun m -> log.Information("Producing... {message} level={level} name={name} facility={facility}", m.Message, m.Level, m.Name, m.Facility))
        let d2 = p.OnError.Subscribe(fun e -> log.Error("Producing... {reason} code={code} isBrokerError={isBrokerError}", e.Reason, e.Code, e.IsBrokerError))
        new KafkaProducer(p, topic, fun () -> for x in [d1;d2] do x.Dispose())

type BatchedProducer private (inner : Producer<string, string>, topic : string) =
    member __.Inner = inner
    member __.Topic = topic

    interface IDisposable with member __.Dispose() = inner.Dispose()

    /// Produces a batch of supplied key/value messages. Results are returned in order of writing (which may vary from order of submission).
    /// <throws>
    ///    1. if there is an immediate local config issue
    ///    2. upon receipt of the first failed `DeliveryReport` (NB without waiting for any further reports, which can potentially leave some results in doubt should a 'batch' get split) </throws>
    /// <remarks>
    ///    Note that the delivery and/or write order may vary from the supplied order unless `maxInFlight` is 1 (which massively constrains throughput).
    ///    Thus it's important to note that supplying >1 item into the queue bearing the same key without maxInFlight=1 risks them being written out of order onto the topic.<remarks/>
    member __.ProduceBatch(keyValueBatch : (string * string)[]) : Async<DeliveryReport<string,string>[]> = async {
        if Array.isEmpty keyValueBatch then return [||] else

        let! ct = Async.CancellationToken

        let tcs = TaskCompletionSource<Message<_,_>[]>()
        let numMessages = keyValueBatch.Length
        let results = Array.zeroCreate<Message<_,_>> numMessages
        let numCompleted = ref 0

        use _ = ct.Register(fun _ -> tcs.TrySetCanceled() |> ignore)

        let handler (m : Message<string,string>) =
            if m.Error.HasError then
                let errorMsg = exn (sprintf "Error on message topic=%s code=%O reason=%s" m.Topic m.Error.Code m.Error.Reason)
                tcs.TrySetException errorMsg |> ignore
            else
                let i = Interlocked.Increment numCompleted
                results.[i - 1] <- m
                if i = numMessages then tcs.TrySetResult results |> ignore
        let handler' =
            { new IDeliveryHandler<string, string> with
                member __.MarshalData = false
                member __.HandleDeliveryReport m = handler m }
        for key,value in keyValueBatch do
            inner.ProduceAsync(topic, key, value, blockIfQueueFull = true, deliveryHandler = handler')
        return! Async.AwaitTaskCorrect tcs.Task }

    /// Creates and wraps a Confluent.Kafka Producer that affords a best effort batched production mode.
    /// NB See caveats on the `ProduceBatch` API for further detail as to the semantics
    /// Throws ArgumentOutOfRangeException if config has a non-zero linger value as this is absolutely critical to the semantics
    static member Create(log : ILogger, config : KafkaProducerConfig, topic : string) =
        match config.Inner.LingerMs, config.Inner.MaxInFlight with
        | l, _ when l.HasValue && l.Value = 0 -> invalidArg "linger" "A non-zero linger value is required in order to have a hope of batching items"
        | l, m ->
            let level = if m.Value = 1 then Serilog.Events.LogEventLevel.Information else Serilog.Events.LogEventLevel.Warning
            log.Write(level, "Producing... Using batch Mode with linger={lingerMs} maxInFlight={maxInFlight}", l, m)
        let inner = KafkaProducer.Create(log, config, topic)
        new BatchedProducer(inner.Inner, topic)

module Core =

    [<NoComparison>]
    type ConsumerBufferingConfig = { minInFlightBytes : int64; maxInFlightBytes : int64; maxBatchSize : int; maxBatchDelay : TimeSpan }

    module Constants =
        let messageCounterSourceContext = "FsKafka.Core.InFlightMessageCounter"

    type InFlightMessageCounter(log : ILogger, minInFlightBytes : int64, maxInFlightBytes : int64) =
        do  if minInFlightBytes < 1L then invalidArg "minInFlightBytes" "must be positive value"
            if maxInFlightBytes < 1L then invalidArg "maxInFlightBytes" "must be positive value"
            if minInFlightBytes > maxInFlightBytes then invalidArg "maxInFlightBytes" "must be greater than minInFlightBytes"

        let mutable inFlightBytes = 0L

        member __.InFlightMb = float inFlightBytes / 1024. / 1024.
        member __.Delta(numBytes : int64) = Interlocked.Add(&inFlightBytes, numBytes) |> ignore
        member __.IsOverLimitNow() = Volatile.Read(&inFlightBytes) > maxInFlightBytes
        member __.AwaitThreshold(ct : CancellationToken, busyWork) =
            if __.IsOverLimitNow() then
                log.ForContext("maxB", maxInFlightBytes).Information("Consuming... breached in-flight message threshold (now ~{currentB:n0}B), quiescing until it drops to < ~{minMb:n1}MiB",
                    inFlightBytes, float minInFlightBytes / 1024. / 1024. / 1024.)
                while Volatile.Read(&inFlightBytes) > minInFlightBytes && not ct.IsCancellationRequested do
                    busyWork ()
                log.Information "Consumer resuming polling"

/// See https://github.com/edenhill/librdkafka/blob/master/CONFIGURATION.md for documentation on the implications of specific settings
[<NoComparison>]
type KafkaConsumerConfig = private { inner: ConsumerConfig; topics: string list; buffering: Core.ConsumerBufferingConfig } with
    member __.Buffering  = __.buffering
    member __.Inner  = __.inner
    member __.Topics = __.topics

    /// Builds a Kafka Consumer Config suitable for KafkaConsumer.Start*
    static member Create
        (   /// Identify this consumer in logs etc
            clientId : string, bootstrapServers : string, topics,
            /// Consumer group identifier.
            groupId,
            /// Specifies handling when Consumer Group does not yet have an offset recorded. Confluent.Kafka default: start from Latest.
            autoOffsetReset,
            /// Default 100kB. Confluent.Kafka default: 500MB
            ?fetchMaxBytes,
            /// Minimum number of bytes to wait for (subject to timeout with default of 100ms). Default 1B.
            ?fetchMinBytes,
            /// Stats reporting interval for the consumer. Default: no reporting.
            ?statisticsInterval,
            /// Consumed offsets commit interval. Default 5s.
            ?autoCommitInterval,
            /// Override default policy wrt auto-creating topics. Confluent.Kafka < 1.5 default: true; Confluent.Kafka >= 1.5 default: false
            ?allowAutoCreateTopics,
            /// Misc configuration parameters to be passed to the underlying CK consumer. Same as constructor argument for Confluent.Kafka >=1.2.
            ?config : IDictionary<string,string>,
            /// Misc configuration parameter to be passed to the underlying CK consumer.
            ?custom,
            /// Postprocesses the ConsumerConfig after the rest of the rules have been applied
            ?customize,

            (* Client-side batching / limiting of reading ahead to constrain memory consumption *)

            /// Minimum total size of consumed messages in-memory for the consumer to attempt to fill. Default 2/3 of maxInFlightBytes.
            ?minInFlightBytes,
            /// Maximum total size of consumed messages in-memory before broker polling is throttled. Default 24MiB.
            ?maxInFlightBytes,
            /// Message batch linger time. Default 500ms.
            ?maxBatchDelay,
            /// Maximum number of messages to group per batch on consumer callbacks for BatchedConsumer. Default 1000.
            ?maxBatchSize) =
        let maxInFlightBytes = defaultArg maxInFlightBytes (16L * 1024L * 1024L)
        let minInFlightBytes = defaultArg minInFlightBytes (maxInFlightBytes * 2L / 3L)
        let fetchMaxBytes = defaultArg fetchMaxBytes 100_000
        let c =
            ConsumerConfig(
                ClientId = clientId, BootstrapServers = bootstrapServers, GroupId = groupId,
                AutoOffsetReset = Nullable autoOffsetReset, // default: latest
                FetchMaxBytes = Nullable fetchMaxBytes, // default: 524_288_000
                EnableAutoCommit = Nullable true, // at AutoCommitIntervalMs interval, write value supplied by StoreOffset call
                EnableAutoOffsetStore = Nullable false, // explicit calls to StoreOffset are the only things that effect progression in offsets
                LogConnectionClose = Nullable false) // https://github.com/confluentinc/confluent-kafka-dotnet/issues/124#issuecomment-289727017
        config |> Option.iter (fun xs -> for KeyValue (k,v) in xs do c.Set(k,v))
        fetchMinBytes |> Option.iter (fun x -> c.FetchMinBytes <- x) // Fetch waits for this amount of data for up to FetchWaitMaxMs (100)
        autoCommitInterval |> Option.iter<TimeSpan> (fun x -> c.AutoCommitIntervalMs <- Nullable <| int x.TotalMilliseconds)
        statisticsInterval |> Option.iter<TimeSpan> (fun x -> c.StatisticsIntervalMs <- Nullable <| int x.TotalMilliseconds)
        allowAutoCreateTopics |> Option.iter (fun x -> c.AllowAutoCreateTopics <- Nullable x)
        custom |> Option.iter (fun xs -> for KeyValue (k,v) in xs do c.Set(k,v))
        customize |> Option.iter<ConsumerConfig -> unit> (fun f -> f c)
        {   inner = c
            topics = match Seq.toList topics with [] -> invalidArg "topics" "must be non-empty collection" | ts -> ts
            buffering = {
                maxBatchDelay = defaultArg maxBatchDelay (TimeSpan.FromMilliseconds 500.); maxBatchSize = defaultArg maxBatchSize 1000
                minInFlightBytes = minInFlightBytes; maxInFlightBytes = maxInFlightBytes } }

// Stats format: https://github.com/edenhill/librdkafka/blob/master/STATISTICS.md
type KafkaPartitionMetrics =
    {   partition: int
        [<JsonProperty("fetch_state")>]
        fetchState: string
        [<JsonProperty("next_offset")>]
        nextOffset: int64
        [<JsonProperty("stored_offset")>]
        storedOffset: int64
        [<JsonProperty("committed_offset")>]
        committedOffset: int64
        [<JsonProperty("lo_offset")>]
        loOffset: int64
        [<JsonProperty("hi_offset")>]
        hiOffset: int64
        [<JsonProperty("consumer_lag")>]
        consumerLag: int64 }

type OffsetValue =
    | Unset
    | Valid of value: int64
    override this.ToString() =
        match this with
        | Unset -> "Unset"
        | Valid value -> value.ToString()
module OffsetValue =
    let ofOffset (offset : Offset) =
        match offset.Value with
        | _ when offset = Offset.Invalid -> Unset
        | valid -> Valid valid

type ConsumerBuilder =
    static member private WithLogging(log: ILogger, c : Consumer<_,_>, ?onRevoke) =
        let d1 = c.OnLog.Subscribe(fun m ->
            log.Information("Consuming... {message} level={level} name={name} facility={facility}", m.Message, m.Level, m.Name, m.Facility))
        let d2 = c.OnError.Subscribe(fun e ->
            log.Warning("Consuming... Error reason={reason} code={code} broker={isBrokerError}", e.Reason, e.Code, e.IsBrokerError))
        let d3 = c.OnPartitionsAssigned.Subscribe(fun tps ->
            for topic,partitions in tps |> Seq.groupBy (fun p -> p.Topic) |> Seq.map (fun (t,ps) -> t, [| for p in ps -> p.Partition |]) do
                log.Information("Consuming... Assigned {topic:l} {partitions}", topic, partitions)
            c.Assign tps)
        let d4 = c.OnPartitionsRevoked.Subscribe(fun tps ->
            for topic,partitions in tps |> Seq.groupBy (fun p -> p.Topic) |> Seq.map (fun (t,ps) -> t, [| for p in ps -> p.Partition |]) do
                log.Information("Consuming... Revoked {topic:l} {partitions}", topic, partitions)
            c.Unassign ()
            onRevoke |> Option.iter (fun f -> f tps))
        let d5 = c.OnPartitionEOF.Subscribe(fun tpo ->
            log.Verbose("Consuming... EOF {topic} partition={partition} offset={offset}", tpo.Topic, tpo.Partition, Binding.offsetValue tpo.Offset))
        let d6 = c.OnOffsetsCommitted.Subscribe(fun cos ->
            for t,ps in cos.Offsets |> Seq.groupBy (fun p -> p.Topic) do
                let o = seq { for p in ps -> p.Partition, OffsetValue.ofOffset p.Offset(*, fmtError p.Error*) }
                let e = cos.Error
                if not e.HasError then log.Information("Consuming... Committed {topic} {offsets}", t, o)
                else log.Warning("Consuming... Committed {topic} {offsets} reason={error} code={code} isBrokerError={isBrokerError}", t, o, e.Reason, e.Code, e.IsBrokerError))
        let d7 = c.OnStatistics.Subscribe(fun json ->
            let stats = JToken.Parse json
            for t in stats.Item("topics").Children() do
                if t.HasValues && c.Subscription |> Seq.exists (fun ct -> ct = t.First.Item("topic").ToString()) then
                    let topic, partitions = let tm = t.First in tm.Item("topic").ToString(), tm.Item("partitions").Children()
                    let metrics = [|
                        for tm in partitions do
                            if tm.HasValues then
                                let kpm = tm.First.ToObject<KafkaPartitionMetrics>()
                                if kpm.partition <> -1 then
                                    yield kpm |]
                    let totalLag = metrics |> Array.sumBy (fun x -> x.consumerLag)
                    log.Information("Consuming... Stats {topic:l} totalLag {totalLag} {@stats}", topic, totalLag, metrics))
        let d8 = c.OnConsumeError.Subscribe (fun msg ->
            log.Error("Consuming... Error reason={reason} topic={topic} partition={partition} offset={offset}", msg.Error.Reason, msg.Topic, msg.Partition, msg.Offset))
        fun () -> for d in [d1;d2;d3;d4;d5;d6;d7;d8] do d.Dispose()
    static member WithLogging(log : ILogger, config : ConsumerConfig, ?onRevoke) =
        let consumer = new Consumer<_,_>(config.Render(), mkDeserializer(), mkDeserializer())
        let unsubLog = ConsumerBuilder.WithLogging(log, consumer, ?onRevoke = onRevoke)
        consumer, unsubLog

module private ConsumerImpl =

   /// used for calculating approximate message size in bytes
    let getMessageSize (result : Message<string, string>) =
        let message = Binding.message result
        if message = null then invalidOp "Cannot compute size of null message"
        let inline len (x:string) = match x with null -> 0 | x -> sizeof<char> * x.Length
        16 + len message.Key + len message.Value |> int64

    let getBatchOffset (batch : Message<string, string>[]) =
        let maxOffset = batch |> Array.maxBy (fun m -> let o = m.Offset in o.Value)
        maxOffset.TopicPartitionOffset

    let mkMessage (msg : Message<string, string>) =
        if msg.Error.HasError then
            failwithf "error consuming message topic=%s partition=%d offset=%O code=%O reason=%s"
                            msg.Topic msg.Partition msg.Offset msg.Error.Code msg.Error.Reason
        msg

    let storeOffset (consumer : Consumer<string, string>) (tpo : TopicPartitionOffset) =
        consumer.StoreOffsets[| TopicPartitionOffset(tpo.Topic, tpo.Partition, Offset(let o = tpo.Offset in o.Value + 1L)) |]
        |> ignore

    let runPoll (consumer : Consumer<string, string>) (pollTimeout : TimeSpan) (counter : Core.InFlightMessageCounter) =
        let cts = new CancellationTokenSource()
        let poll() =
            while not cts.IsCancellationRequested do
                counter.AwaitThreshold(cts.Token, fun () -> Thread.Sleep 1)
                consumer.Poll(pollTimeout)

        let _ = Async.StartAsTask(async { poll() })
        { new IDisposable with member __.Dispose() = cts.Cancel() }

    type BlockingCollection<'T> with
        member bc.FillBuffer(buffer : 'T[], maxDelay : TimeSpan) : int =
            let cts = new CancellationTokenSource()
            do cts.CancelAfter maxDelay

            let n = buffer.Length
            let mutable i = 0
            let mutable t = Unchecked.defaultof<'T>

            while i < n && not cts.IsCancellationRequested do
                if bc.TryTake(&t, 5 (* ms *)) then
                    buffer.[i] <- t ; i <- i + 1
                    while i < n && not cts.IsCancellationRequested && bc.TryTake(&t) do
                        buffer.[i] <- t ; i <- i + 1
            i

    type PartitionedBlockingCollection<'Key, 'Message when 'Key : equality>(?perPartitionCapacity : int) =
        let collections = ConcurrentDictionary<'Key, Lazy<BlockingCollection<'Message>>>()
        let onPartitionAdded = Event<'Key * BlockingCollection<'Message>>()

        let createCollection() =
            match perPartitionCapacity with
            | None -> new BlockingCollection<'Message>()
            | Some c -> new BlockingCollection<'Message>(boundedCapacity = c)

        [<CLIEvent>]
        member __.OnPartitionAdded = onPartitionAdded.Publish

        member __.Add (key : 'Key, message : 'Message) =
            let factory key = lazy(
                let coll = createCollection()
                onPartitionAdded.Trigger(key, coll)
                coll)

            let buffer = collections.GetOrAdd(key, factory)
            buffer.Value.Add message

        member __.Revoke(key : 'Key) =
            match collections.TryRemove key with
            | true, coll -> Task.Delay(10000).ContinueWith(fun _ -> coll.Value.CompleteAdding()) |> ignore
            | _ -> ()

    let mkBatchedMessageConsumer (log: ILogger) (buf : Core.ConsumerBufferingConfig) (ct : CancellationToken) (consumer : Consumer<string, string>) (handler : Message<string, string>[] -> Async<unit>) = async {
        let tcs = TaskCompletionSource<unit>()
        use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
        use _ = ct.Register(fun _ -> tcs.TrySetResult () |> ignore)

        use _ = consumer

        let counter = Core.InFlightMessageCounter(log, buf.maxInFlightBytes, buf.maxInFlightBytes)
        let partitionedCollection = PartitionedBlockingCollection<TopicPartition, Message<string, string>>()

        // starts a tail recursive loop which dequeues batches for a given partition buffer and schedules the user callback
        let consumePartition (key : TopicPartition, collection : BlockingCollection<Message<string, string>>) =
            let buffer = Array.zeroCreate buf.maxBatchSize
            let nextBatch () =
                let count = collection.FillBuffer(buffer, buf.maxBatchDelay)
                let batch = Array.init count (fun i -> mkMessage buffer.[i])
                Array.Clear(buffer, 0, count)
                batch

            let processBatch = async {
                let batchWatch = System.Diagnostics.Stopwatch()
                let mutable batchLen, batchSize = 0, 0L
                try match nextBatch() with
                    | [||] -> ()
                    | batch ->
                        batchLen <- batch.Length
                        batchSize <- batch |> Array.sumBy (fun m -> getMessageSize m)
                        batchWatch.Start()
                        log.ForContext("batchSize", batchSize).Debug("Dispatching {batchLen} message(s) to handler", batchLen)
                        // run the handler function
                        do! handler batch

                        // store completed offsets
                        storeOffset consumer (getBatchOffset batch)

                        // decrement in-flight message counter
                        counter.Delta -batchSize
                with e ->
                    log.ForContext("batchSize", batchSize).ForContext("batchLen", batchLen).ForContext("handlerDuration", batchWatch.Elapsed)
                        .Information(e, "Exiting batch processing loop due to handler exception")
                    tcs.TrySetException e |> ignore
                    cts.Cancel() }
            
            let loop = async {
                use __ = Serilog.Context.LogContext.PushProperty("partition", Binding.partitionValue key.Partition)
                while not collection.IsCompleted do
                    do! processBatch }

            Async.Start(loop, cts.Token)

        use _ = partitionedCollection.OnPartitionAdded.Subscribe consumePartition
        use _ = consumer.OnPartitionsRevoked.Subscribe (fun ps -> for p in ps do partitionedCollection.Revoke p)
        use _ = consumer.OnMessage.Subscribe (fun m -> counter.Delta(getMessageSize m) ; partitionedCollection.Add(m.TopicPartition, m))

        use _ = runPoll consumer (TimeSpan.FromMilliseconds 200.) counter // run the consumer

        // await for handler faults or external cancellation
        return! Async.AwaitTaskCorrect tcs.Task
    }

/// Creates and wraps a Confluent.Kafka IConsumer, wrapping it to afford a batched consumption mode with implicit offset progression at the end of each
/// (parallel across partitions, sequenced/monotonic within) batch of processing carried out by the `partitionHandler`
/// Conclusion of the processing (when a `partitionHandler` throws and/or `Stop()` is called) can be awaited via `AwaitCompletion()`
type BatchedConsumer private (inner : Consumer<string, string>, task : Task<unit>, triggerStop) =
    member __.Inner = inner

    interface IDisposable with member __.Dispose() = __.Stop()
    /// Request cancellation of processing
    member __.Stop() = triggerStop ()
    /// Inspects current status of processing task
    member __.Status = task.Status
    member __.RanToCompletion = task.Status = System.Threading.Tasks.TaskStatus.RanToCompletion
    /// Asynchronously awaits until consumer stops or is faulted
    member __.AwaitCompletion() = Async.AwaitTaskCorrect task

    /// Starts a Kafka consumer with the provided configuration. Batches are grouped by topic partition.
    /// Batches belonging to the same topic partition will be scheduled sequentially and monotonically; however batches from different partitions can run concurrently.
    /// Completion of the `partitionHandler` saves the attained offsets so the auto-commit can mark progress; yielding an exception terminates the processing
    static member Start(log : ILogger, config : KafkaConsumerConfig, partitionHandler : Message<string,string>[] -> Async<unit>) =
        if List.isEmpty config.topics then invalidArg "config" "must specify at least one topic"
        log.Information("Consuming... {bootstrapServers} {topics} {groupId} autoOffsetReset={autoOffsetReset} fetchMaxBytes={fetchMaxB} maxInFlight={maxInFlightMb:n1}MiB maxBatchDelay={maxBatchDelay}s maxBatchSize={maxBatchSize}",
            config.inner.BootstrapServers, config.topics, config.inner.GroupId, (let x = config.inner.AutoOffsetReset in x.Value), config.inner.FetchMaxBytes,
            float config.buffering.maxInFlightBytes / 1024. / 1024., (let t = config.buffering.maxBatchDelay in t.TotalSeconds), config.buffering.maxBatchSize)
        let partitionedCollection = ConsumerImpl.PartitionedBlockingCollection<TopicPartition, Message<string, string>>()
        let onRevoke (xs : seq<TopicPartition>) =
            for x in xs do
                partitionedCollection.Revoke(x)
        let consumer, unsubLog = ConsumerBuilder.WithLogging(log, config.inner, onRevoke = onRevoke)
        let cts = new CancellationTokenSource()
        let triggerStop () =
            log.Information("Consuming... Stopping {name:l}", config.inner.ClientId)
            cts.Cancel()
            unsubLog ()
        let task = ConsumerImpl.mkBatchedMessageConsumer log config.buffering cts.Token consumer partitionHandler |> Async.StartAsTask
        let c = new BatchedConsumer(consumer, task, triggerStop)
        consumer.Subscribe config.topics
        c

    /// Starts a Kafka consumer instance that schedules handlers grouped by message key. Additionally accepts a global degreeOfParallelism parameter
    /// that controls the number of handlers running concurrently across partitions for the given consumer instance.
    static member StartByKey(log: ILogger, config : KafkaConsumerConfig, degreeOfParallelism : int, keyHandler : Message<_,_> [] -> Async<unit>) =
        let semaphore = new SemaphoreSlim(degreeOfParallelism)
        let partitionHandler (results : Message<_,_>[]) = async {
            return!
                results
                |> Seq.groupBy (fun r -> r.Key)
                |> Seq.map (fun (_,gp) -> async {
                    let! ct = Async.CancellationToken
                    let! _ = semaphore.WaitAsync ct |> Async.AwaitTaskCorrect
                    try do! keyHandler (Seq.toArray gp)
                    finally semaphore.Release() |> ignore })
                |> Async.Parallel
                |> Async.Ignore
        }

        BatchedConsumer.Start(log, config, partitionHandler)
