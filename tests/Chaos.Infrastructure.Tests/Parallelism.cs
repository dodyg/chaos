using TUnit.Core;
using TUnit.Core.Interfaces;

// One Infrastructure test at a time; see InfrastructureParallelLimit for why.
[assembly: ParallelLimiter<InfrastructureParallelLimit>]

/// <summary>
/// Runs the Infrastructure tests one at a time. The suite is several hundred file-backed SQLite tests; at TUnit's
/// default (one test per logical processor, 12 on a 12-thread desktop) they take every core and the disk, the machine
/// stops responding, and a host that runs out of resources fails the run after the last test. One test at a time
/// keeps the desktop usable; <c>--maximum-parallel-tests</c> and <c>TUNIT_MAX_PARALLEL_TESTS</c> still apply and can
/// only lower this further.
/// </summary>
public sealed class InfrastructureParallelLimit : IParallelLimit
{
    public int Limit => 1;
}
