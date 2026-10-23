using Xunit;

// These integration classes launch real Git processes. Bound concurrency so
// large-core workstations do not turn version probes into CPU starvation tests.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
