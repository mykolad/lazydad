# Performance history

Load tests of production's setup and the optimizations they led to, newest last. Each run uses the **Load Test
Environment** workflow and `tests/load/visitors.js` (how to run one: `infra/deployment-setup.md`, section 12, "Load
test"). Simulated visitors load the page, then read for about 15 seconds, vote on two jokes and scroll, up to 10
times; arrivals grow in 3-minute steps, and k6 stops once p95 latency passes 2 seconds or more than 2% of requests
fail.

Add a section per run: what changed, the setup (commit, replicas, jokes, steps) and the table, by minute or by step,
with the app's CPU and memory and the database's DTU from Azure Monitor.

## 2026-10-02: the baseline

`master` at `fe3bc19`, 2 replicas of 0.25 vCPU / 0.5 GiB, the Basic database (5 DTU), 1,200 jokes,
`-e STAGES=450,600,800,1000`:

| Visitors at once | Requests per minute | p95 latency | App CPU, busiest replica (of 250 m) | Database DTU |
|---|---|---|---|---|
| 220 | 3,000 | 102 ms | 69 m | 41% |
| 430 | 5,900 | 119 ms | 161 m | 81% |
| 480 | 6,600 | 504 ms | 164 m | 76% |
| 540 | 7,200 | 1.1 s | 187 m | 91% |
| 620 | 7,300 | 2.4 s | 204 m | **100%** |

The **database runs out first**: from about 7,300 requests a minute (about 120 a second, 500 visitors at once)
throughput stops growing and requests queue (latency climbs, no errors yet), with the app's CPU at about 80%. Memory
stayed at about 250 MB of 512 MB. Comfortable load is about 430 visitors at once. Next: cache what every visitor gets
the same, or a bigger database (S0, 10 DTU, about $15 a month).

An earlier run with only 50 virtual visitors prepared up front stopped at about 250 at once: k6 dropped 202 visits it
couldn't start in time. `visitors.js` now prepares them all before the run.

## 2026-10-02: the read cache (#57)

`JokeReadCache` keeps the joke count, the Top 3 and each feed page in memory per replica for 30 seconds. Same setup and
steps, at `c3fe8a4`: no step reached a limit.

| Visitors at once | Requests per minute | p95 latency | App CPU, busiest replica (of 250 m) | Database DTU |
|---|---|---|---|---|
| 460 | 7,100 | 91 ms | 112 m | 36% |
| 640 | 9,800 | 97 ms | 162 m | 47% |
| 790 | 12,100 | 97 ms | 186 m | 61% |
| 860 | 12,800 | 155 ms | 218 m | 70% |

Throughput is **at least 1.75 times** the baseline's limit, at the same latency; 120,447 requests, none failed. What's
left in the database is mostly votes (this test votes twice every 15 seconds per visitor, far more than real visitors
do), and the app's CPU (about 87% at 860 at once) is the next limit, so about 850 visitors at once is comfortable. The
database's workers (Basic allows 30 at once) briefly hit 100% a few times without a failed request; S0 would add
headroom there.
