# Deployment vs StatefulSet

A Deployment assumes its pods are INTERCHANGEABLE. Any replica can serve any request, any
replica can be killed and replaced by an identical one. That is what makes stateless
services trivial to scale.

A StatefulSet assumes its pods are NOT interchangeable. Each one has an identity that
outlives the container.

Three things you get, and only the first is about the ordinal.

# 1. Stable identity

Pods are named `<statefulset>-<ordinal>`: `gateway-0`, `gateway-1`, `gateway-2`.
The ordinal is stable across rescheduling -- if `gateway-1` dies, its replacement is also
`gateway-1`, not `gateway-3`.

Paired with a headless Service, each pod also gets a stable DNS name:

```
gateway-1.gateway-svc.default.svc.cluster.local
```

So other things can address a SPECIFIC pod, which you cannot do with a Deployment.

This is the piece Docker Compose has no answer to (see docker.md).

# 2. Per-pod storage: volumeClaimTemplates

Vocabulary first, because the acronyms are the hard part:

| term | what it is |
| --- | --- |
| PersistentVolume (PV) | an actual piece of storage in the cluster -- an EBS volume, an NFS share, a disk |
| PersistentVolumeClaim (PVC) | a REQUEST for storage: "I need 10Gi, ReadWriteOnce". Kubernetes binds it to a PV |
| StorageClass | the recipe for provisioning a PV on demand, so you do not pre-create disks by hand |

A PVC is a claim ticket. The pod mounts the PVC; Kubernetes figures out which real disk
that resolves to.

`accessModes` says how many things may mount it at once:

| mode | short | meaning |
| --- | --- | --- |
| ReadWriteOnce | RWO | read-write by a single NODE (several pods on that node can share it) |
| ReadWriteOncePod | RWOP | read-write by exactly ONE POD. The strict version. |
| ReadOnlyMany | ROX | read-only, many nodes |
| ReadWriteMany | RWX | read-write, many nodes. Needs a filesystem that supports it (NFS, CephFS) -- a cloud block device like EBS cannot do this. |

For the gateway's SQLite buffer you want RWO or RWOP, because SQLite expects one writer.

The problem with a Deployment: you attach ONE PVC, and every replica mounts THE SAME
volume. For anything that owns its data, that is wrong -- three gateways writing to one
SQLite file is corruption, not replication.

A StatefulSet has `volumeClaimTemplates` instead. Kubernetes creates a SEPARATE PVC per
pod, named after the template and the pod:

```yaml
volumeClaimTemplates:
  - metadata:
      name: buffer
    spec:
      accessModes: ["ReadWriteOnce"]
      resources:
        requests:
          storage: 10Gi
```

gives you `buffer-gateway-0`, `buffer-gateway-1`, `buffer-gateway-2`. Each pod gets its
own disk, and crucially the PVC is RE-ATTACHED to the same ordinal when the pod is
rescheduled onto another node. `gateway-1` always gets `gateway-1`'s data back.

Deleting the StatefulSet does NOT delete the PVCs, deliberately -- the data outliving the
workload is the whole point.

## Why this repo cares

The edge gateway's SQLite buffer. In docker-compose it is a named volume, which works
because there is exactly one gateway.

Run several gateways as a Deployment and they would fight over one volume. As a
StatefulSet, each gets its own buffer that survives rescheduling -- which is the only
version where "the queue is still there after the gateway restarts" stays true in a
cluster.

# 3. Ordered rollout

A Deployment updates pods in parallel batches (governed by maxSurge/maxUnavailable).

A StatefulSet updates them ONE AT A TIME. Two rules make up "ordered":

1. ONE AT A TIME, highest ordinal first: gateway-2, then gateway-1, then gateway-0.
   Reverse order because ordinal 0 is conventionally the oldest or primary member in
   stateful systems, so you disturb it last -- and it matches scale-down, which also
   removes the highest ordinal first.
2. WAIT FOR READY. After restarting a pod, Kubernetes waits until that pod reports Ready
   before touching the next one. So two members are never down simultaneously.

Scale-UP goes the other way: 0, then 1, then 2, each waiting on the previous.

This matters when simultaneous restarts are dangerous: a database losing quorum because
two members bounced at once, or a partitioned consumer group rebalancing repeatedly.

It is also SLOWER, on purpose. A 20-pod StatefulSet rollout is 20 sequential restarts.

# What StatefulSets do not give you

- No load balancing across pods for free. You typically use a headless Service and let
  clients address pods directly, or put a normal Service in front for the cases where any
  pod will do.
- No automatic data replication. Kubernetes gives each pod its own disk; keeping the data
  on those disks consistent is entirely the application's problem.
- No help with leader election or sharding logic. The ordinal is a hint; what you do with
  it is up to you.
