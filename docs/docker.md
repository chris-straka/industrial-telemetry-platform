# Environment Variables vs appsettings.json

You must use environment variables in the Compose file.
The Problem: Your appsettings.json says localhost.
However, localhost means this current container

To talk to Postgres, the worker needs to look for a host named postgres (the service name).
.NET automatically maps environment variables with double underscores to JSON sections.
`DB__ConnectionString` in Compose overrides appsettings.json's `DB: { ConnectionString: ... }`.

service_healthy: Docker started the container and the cmd defined in the healthcheck block returned a success code (0).

# Replica identity

`docker compose up --scale sensor-emulator=3` creates 3 CONTAINERS from 1 SERVICE
definition. (`deploy.replicas` in the compose file does the same thing declaratively; that
key originated with Swarm and `docker stack deploy`, and Compose v2 also honours it.)

All 3 containers get BYTE-IDENTICAL environment variables. There is no per-replica ordinal
exposed to the app, so a replica cannot tell which one it is.

That is a feature, not an oversight. Identical replicas are interchangeable, which is what
makes a stateless service trivial to load balance and scale. Identity is only wanted when
the workload is PARTITIONED -- when each replica owns a distinct shard of something.

The emulator is partitioned: replica 0 must own EQ-0..EQ-3 and nothing else, replica 1
EQ-4..EQ-7, and so on -- or two containers both claim to be EQ-0, their sequence numbers
collide, and `make verify` reports duplicates that no part of the pipeline actually
caused.

## How each orchestrator answers this

| | mechanism |
| --- | --- |
| Kubernetes | StatefulSet. A pod is always `<name>-<ordinal>` -- stable, readable from the pod itself, and it survives rescheduling. |
| Docker Swarm | Service templates. `.Task.Slot` gives a stable slot number, usable in `hostname`, `env`, and mount targets. |
| Docker Compose | Nothing. Define distinct services. |

See Kubernetes.md for what StatefulSets actually give you beyond the ordinal.

Swarm template syntax, which is the answer if you are on Swarm rather than k8s:

```yaml
services:
  sensor-emulator:
    image: sensor-emulator
    deploy:
      replicas: 3
    environment:
      - SLOT={{.Task.Slot}}
```

`{{.Task.Slot}}` is a SWARM MODE feature (`docker stack deploy`). Plain
`docker compose up` does not expand it -- you get the literal string.

So Swarm can do partitioned workloads, and people do run it in production. It is weaker
than StatefulSets (no ordered rollout, no per-pod PersistentVolumeClaim templates, no
stable per-task DNS name in the same way) but "you need k8s the moment you need identity"
is not true.

## Which number the app takes

`Emulator__ReplicaId`, 0-based to match a Kubernetes StatefulSet ordinal, since k8s is the
intended prod target. Device tags are 0-based too (`EQ-0`..), so the worker is
`Range(ReplicaId * DeviceCount, DeviceCount)` with no off-by-one anywhere.

Swarm slots are 1-based and would need `SLOT - 1` in an entrypoint. Not a concern here --
this repo is compose for dev and k8s for prod, so Swarm gets no say in the config shape.

Rejected: `FirstDevice`, the starting device number itself (0, 4, 8). It reads better in
the compose file and keeps `[Range(1, N)]` doubling as a presence check, but k8s hands out
an ordinal rather than a device number, so it moves a multiply into the deployment to save
one in the app. `ReplicaId` passes straight through from the pod name.

The cost of 0-based: 0 is a legal value, so `[Range]` can no longer prove the key was set.
That is why `ReplicaId` is `int?` + `[Required]` while every other option is a plain int.

## What this repo does

Three explicit services with hardcoded `Emulator__ReplicaId` (0, 1, 2), behind a compose
PROFILE so they do not start by default:

```sh
docker compose --profile fleet up -d   # or: make fleet
```

A profile is just a label meaning "skip this service unless the profile is requested". It
is NOT an identity mechanism -- the identity comes from writing three separate service
definitions by hand. Manual, and honest about being manual.

Rejected here: `docker compose --scale sensor-emulator=3`. It is one word shorter and it
is wrong -- all three containers inherit byte-identical env, all three own EQ-0..EQ-3, and
the resulting duplicate MessageIds are indistinguishable from a real durability bug.

# One process, many simulated devices

The emulator runs N devices inside ONE container rather than N containers, and that is a
cost decision, not a design preference.

A container per device means a .NET RUNTIME per device -- roughly 50-100MB of RAM each.
1,000 of those is 50-100GB and is simply not happening on a laptop. 1,000 async tasks in
one process is a few hundred MB total.

What you keep by faking it: the traffic volume the gateway has to absorb.
What you lose: independent connection pools, independent failure, real client concurrency.

So the split is a HANDFUL of containers (proves multi-client behaviour) times MANY devices
each (gets the volume). That is what the fleet profile does.
