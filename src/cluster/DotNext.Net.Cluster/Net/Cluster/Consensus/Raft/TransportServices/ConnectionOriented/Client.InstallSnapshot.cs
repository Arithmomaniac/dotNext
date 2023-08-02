using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Debug = System.Diagnostics.Debug;

namespace DotNext.Net.Cluster.Consensus.Raft.TransportServices.ConnectionOriented;

internal partial class Client : RaftClusterMember
{
    [StructLayout(LayoutKind.Auto)]
    [RequiresPreviewFeatures]
    private readonly struct InstallSnapshotExchange : IClientExchange<Result<HeartbeatResult>>
    {
        private const string Name = "InstallSnapshot";

        private readonly ILocalMember localMember;
        private readonly IRaftLogEntry snapshot;
        private readonly long term, snapshotIndex;

        internal InstallSnapshotExchange(ILocalMember localMember, long term, IRaftLogEntry snapshot, long snapshotIndex)
        {
            Debug.Assert(localMember is not null);
            Debug.Assert(snapshot is not null);
            Debug.Assert(snapshot.IsSnapshot);

            this.localMember = localMember;
            this.term = term;
            this.snapshot = snapshot;
            this.snapshotIndex = snapshotIndex;
        }

        ValueTask IClientExchange<Result<HeartbeatResult>>.RequestAsync(ProtocolStream protocol, Memory<byte> buffer, CancellationToken token)
            => protocol.WriteInstallSnapshotRequestAsync(localMember.Id, term, snapshotIndex, snapshot, buffer, token);

        static ValueTask<Result<HeartbeatResult>> IClientExchange<Result<HeartbeatResult>>.ResponseAsync(ProtocolStream protocol, Memory<byte> buffer, CancellationToken token)
            => protocol.ReadHeartbeatResult(token);

        static string IClientExchange<Result<HeartbeatResult>>.Name => Name;
    }

    [RequiresPreviewFeatures]
    private protected sealed override Task<Result<HeartbeatResult>> InstallSnapshotAsync(long term, IRaftLogEntry snapshot, long snapshotIndex, CancellationToken token)
        => RequestAsync<Result<HeartbeatResult>, InstallSnapshotExchange>(new(localMember, term, snapshot, snapshotIndex), token);
}