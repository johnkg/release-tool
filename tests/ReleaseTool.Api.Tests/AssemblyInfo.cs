// Serilog's request logging writes through the static Log.Logger, which every
// hosted app overwrites as it starts. Running test classes in parallel therefore
// mixes one app's request logs into another's sink. The suite is fast, so run it
// sequentially rather than special-casing the logging assertions.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
