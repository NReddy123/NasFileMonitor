# **On-Premises NAS File Monitoring & Verification Service — Architecture Design Document**

| Metadata Attribute | Document Details |
| :---- | :---- |
| **Document Status** | Draft for Team Review |
| **Date** | November 25, 2026 |
| **Target Runtime** | .NET 8 Worker Service (Windows Service via `Microsoft.Extensions.Hosting.WindowsServices`) |
| **Primary Scope** | On-Premises NAS File Discovery, Processing Verification (Folder-to-Folder, Folder-to-DB, Hybrid), Regex Archive Matching, and SLA Alerting |

# **1\. Executive Summary & Objectives**

This document defines the architecture for an on-premises file tracking and monitoring service built in C\# (.NET 8\) and deployed as a Windows Service. The service monitors inbound on-premises NAS (SMB/NFS) paths, tracks both newly arriving and pre-existing files, and verifies downstream business application processing across three configuration-driven modes:

* **Folder-to-Folder (`FolderToFolder`)**: Confirms that a processed copy of the inbound file appears in a secondary NAS or archive path, supporting filename transformations via regex templates.  
* **Folder-to-Database (`FolderToDb`)**: Tracks files that remain permanently in the inbound folder by querying an underlying SQL Server status table for success or failure outcomes.  
* **Hybrid (`Hybrid`)**: Requires both a verified destination file copy and a successful database status record before marking a file complete.

To avoid the silent event drops and buffer overflows common with `FileSystemWatcher` over network shares, the architecture uses scheduled delta polling backed by a persistent relational State Ledger.

# **2\. High-Level Architecture**

The end-to-end data flow operates through a decoupled processing pipeline:

1. **Source NAS Shares (UNC paths)** receive inbound files from upstream producers.  
2. **.NET 8 Windows Service (Delta Scanner)** executes bounded periodic scans driven by a hot-reloadable JSON configuration file (`monitors.json`) via `IOptionsMonitor<MonitorConfig>`.  
3. **State Ledger Database** (SQL Server or local SQLite for single-node setups) persists file discovery metadata, stability checks, verification states, heartbeats, and alert deduplication timestamps.  
4. **Pluggable Verifier Engine** (`IFileVerifier` strategy) evaluates `PENDING_VERIFICATION` files against Target NAS Shares (via `RegexTargetMatcher`), Business SQL Databases (via batched named queries), or both.  
5. **Alerting & Observability Layer** emits deduplicated, per-rule aggregated SLA breach alerts, failure alerts, and dead-man's switch heartbeat metrics.

## **Component Summary**

| Component | Responsibility | Key Implementation Detail |
| :---- | :---- | :---- |
| **Windows Service Host** | Runs isolated polling ticks with graceful shutdown and OS auto-recovery | `BackgroundService` \+ `PeriodicTimer` \+ `Parallel.ForEachAsync` (capped by `MaxParallelRules`) |
| **Configuration Manager** | Loads and hot-reloads path rules, verification modes, and regex patterns | `monitors.json` bound via `IOptionsMonitor<MonitorConfig>` \+ `IValidateOptions` startup/reload guard |
| **Delta Scanner & Stability Gate** | Discovers new/modified files and prevents partial-write evaluation | `FileSystemEnumerable<T>` single-pass metadata reads; requires *N* unchanged consecutive ticks |
| **State Ledger DB** | Tracks file lifecycle, permanent inbound files, and HA lease state | Keyed by `(RuleId, RelativePath, FileSizeBytes, LastWriteTimeUtc)` |
| **Verifier Strategy Engine** | Resolves `FolderToFolderVerifier`, `FolderToDbVerifier`, or `HybridVerifier` per rule | Dependency-injected `IFileVerifier` implementations with per-rule `CancellationTokenSource` timeouts |
| **Regex Target Matcher** | Matches renamed/prefixed archive files safely and efficiently | Token-interpolated regex (`{FileName}`, `{BaseName}`, `{Ext}`), `Regex.Escape`, 200ms timeout, `Contains` pre-filter |

# **3\. File Lifecycle & State Machine**

The service evaluates every discovered file through a strict state machine to guarantee idempotency and prevent duplicate processing or alerting.

| State | Entry Condition | System Behavior |
| :---- | :---- | :---- |
| `BASELINE_IGNORED` | File already existed on initial rule startup with `ExistingFilesPolicy = BaselineOnly` | Recorded in ledger; skipped on all future scans without alerting. |
| `DETECTED` | Newly discovered file (or overwritten file with changed size/`LastWriteTimeUtc`) | Held until size and modified timestamp remain identical across `StabilityChecksRequired` consecutive scans. |
| `PENDING_VERIFICATION` | File passed stability gate (or existed at startup with `BaselineAndVerify`) | SLA timer starts from monitor's `FirstSeenUtc`; evaluated by configured `IFileVerifier` each tick. |
| `SUCCEEDED` | Verification criteria met (target copy found, DB status in `SuccessValues`, or both in `Hybrid`) | Terminal state; completion time and latency recorded; skipped on future scans. |
| `SUCCEEDED_FAST_PATH` | New file appears in `TargetPath` (post-baseline) with no prior `SourcePath` sighting while `TrackFastPathMoves = true` | Terminal state; recorded via Target-Side Reverse Discovery so daily volume reconciliation stays 100% accurate when files move faster than the poll interval. |
| `FAILED` | Business DB reports status in `FailureValues`, or file appears in configured `ErrorPath` | Terminal state; immediate failure alert emitted (does not wait for SLA expiry). |
| `SLA_BREACHED` | File remains unverified past `SlaMinutes` | Batched alert emitted once per rule per tick (`AlertedAtUtc` recorded); verification continues until resolved. |
| `PARTIAL_MISMATCH` | Hybrid mode SLA expired with only one check passing (e.g., DB Succeeded but archive copy missing) | Flagged with explicit diagnostic detail for ops triage. |
| `VANISHED_UNVERIFIED` | File disappeared from `SourcePath` before verification and never appeared in `TargetPath` or `ErrorPath` within `SlaMinutes` | High-priority anomaly alert indicating silent deletion, unconfigured move destination, or an app crash mid-move. |

## **Permanent Inbound Files & Re-Drops**

* Because `FolderToDb` files remain in the Source NAS permanently, terminal file keys are cached in memory per rule so subsequent scans skip them in \$O(1)\$ time.  
* If a file with the same filename is later overwritten, its `FileSizeBytes` or `LastWriteTimeUtc` changes, producing a new ledger generation row (`AttemptNumber + 1`) and triggering fresh verification.  
* When `SourceDisposition = "Moved"`, if a `DETECTED` file disappears from `SourcePath` before its second stability tick, it immediately promotes to `PENDING_VERIFICATION` and runs `FolderToFolderVerifier` rather than stalling in the stability gate.

# **4\. Configuration Schema (`monitors.json`)**

The `monitors.json` file is bound via `IOptionsMonitor<MonitorConfig>` for zero-downtime rule updates and validated at startup via IValidateOptions\<MonitorConfig\>. Raw SQL strings are never placed in JSON; instead, DbCheck references a `QueryKey` mapped to parameterized SQL statements in code.

```
{
  "PollingDefaults": {
    "ScanIntervalSeconds": 30,
    "StabilityChecksRequired": 2,
    "MaxParallelRules": 4,
    "PerRuleTimeoutSeconds": 45
  },
  "Monitors": [
    {
      "RuleId": "checks-inbound-x9",
      "Enabled": true,
      "SourcePath": "\\\\YOUR_NAS_SERVER\\inbound\\checks",
      "FilePattern": "*.x9",
      "IncludeSubdirectories": false,
      "ExistingFilesPolicy": "BaselineAndVerify",
      "SlaMinutes": 15,
      "VerificationMode": "FolderToFolder",
      "FolderCheck": {
        "SourceDisposition": "Moved",
        "TrackFastPathMoves": true,
        "TargetPath": "\\\\YOUR_NAS_SERVER\\archive\\checks\\{yyyy}\\{MM}\\{dd}",
        "ErrorPath": "\\\\YOUR_NAS_SERVER\\error\\checks",
        "MatchMode": "RegexTemplate",
        "TargetFileNameRegex": "^(?:[a-zA-Z0-9]+[_\\.])?{BaseName}{Ext}$",
        "SourceExtractRegex": null,
        "IgnoreCase": true,
        "MinTargetModifiedOffsetSeconds": -120,
        "Disambiguation": "NewestAfterSource",
        "VerifySizeMatch": false
      }
    },
    {
      "RuleId": "correspondence-stays-in-place",
      "Enabled": true,
      "SourcePath": "\\\\YOUR_NAS_SERVER\\inbound\\correspondence",
      "FilePattern": "*.pdf",
      "ExistingFilesPolicy": "BaselineOnly",
      "SlaMinutes": 20,
      "VerificationMode": "FolderToDb",
      "DbCheck": {
        "ConnectionStringName": "BusinessAppDb",
        "QueryKey": "CorrespondenceStatusByFileName",
        "KeySource": "FileName",
        "SuccessValues": ["PROCESSED", "COMPLETED"],
        "FailureValues": ["FAILED", "REJECTED"]
      }
    },
    {
      "RuleId": "critical-wire-audit",
      "Enabled": true,
      "SourcePath": "\\\\nas01\\inbound\\wires",
      "FilePattern": "*.xml",
      "ExistingFilesPolicy": "BaselineAndVerify",
      "SlaMinutes": 10,
      "VerificationMode": "Hybrid",
      "FolderCheck": {
        "SourceDisposition": "Copied",
        "TrackFastPathMoves": false,
        "TargetPath": "\\\\YOUR_NAS_SERVER\\archive\\wires",
        "MatchMode": "RegexTemplate",
        "TargetFileNameRegex": "^(?:[a-zA-Z0-9]+[_\\.])?{BaseName}{Ext}$",
        "IgnoreCase": true,
        "MinTargetModifiedOffsetSeconds": -120
      },
      "DbCheck": {
        "ConnectionStringName": "BusinessAppDb",
        "QueryKey": "WireStatusByFileName",
        "KeySource": "FileName",
        "SuccessValues": ["SUCCESS"],
        "FailureValues": ["ERROR", "FAILED"]
      }
    }
  ]
}
```

# **5\. Verification Strategies & Regex Destination Matching**

## **5.1 FolderToFolderVerifier & RegexTargetMatcher**

* **Token Interpolation**: Supports `{FileName}`, `{BaseName}`, and `{Ext}`. For a source file `fileA.txt`, the template `^(?:[a-zA-Z0-9]+[_\.])?{BaseName}{Ext}$` compiles to `^(?:[a-zA-Z0-9]+[_\.])?fileA\.txt$`, matching `fileA.txt`, `abc_fileA.txt`, `abc.fileA.txt`, and `123.fileA.txt` while rejecting unrelated files.  
* **Correlation Extraction**: Optional `SourceExtractRegex` (e.g., `^INV_(?<DocId>\d+)_\`) extracts named capture groups for `TargetFileNameRegex` (e.g., `^ARCH-{DocId}\.pdf$`) or DB lookup keys.  
* **Safety & Performance Controls**:  
  1. Always apply `Regex.Escape()` to every dynamic token before interpolation.  
  2. Compile regexes with `RegexOptions.CultureInvariant` and a strict 200ms match timeout to prevent catastrophic backtracking.  
  3. Skip scanning `TargetPath` entirely when a rule has zero `PENDING_VERIFICATION` files (unless `TrackFastPathMoves` is enabled for that rule).  
  4. Snapshot `TargetPath` once per tick via `FileSystemEnumerable<T>` and pre-filter in memory with `Contains(baseName, OrdinalIgnoreCase)` before evaluating regexes.  
  5. Disambiguate recurring filenames using `target.LastWriteTimeUtc >= source.FirstSeenUtc.AddSeconds(MinTargetModifiedOffsetSeconds)` and optional `VerifySizeMatch`.  
  6. Check optional `ErrorPath` to flag failed files immediately instead of waiting for SLA expiry.

### **Move vs. Copy Handling & Target-Side Reverse Discovery**

* **SourceDisposition ("Copied" vs. "Moved")**: Verification never requires a file to remain in both folders simultaneously. In "Moved" mode, disappearing from `SourcePath` accelerates verification, and remaining in `SourcePath` past SLA triggers an alert even if a copy exists in `TargetPath`. In "Copied" mode, the source file stays in place and is skipped once terminal.  
* **Target-Side Reverse Discovery (TrackFastPathMoves)**: When a business app processes and moves a file between two poll ticks, the monitor never sees it in `SourcePath`. With `TrackFastPathMoves = true`, any newly arrived file in `TargetPath` (newer than startup baseline) without an active `TrackedFiles` match is logged directly as `SUCCEEDED_FAST_PATH`, keeping reconciliation counts complete without false alerts.

## **5.2 FolderToDbVerifier**

* Batches all `PENDING_VERIFICATION` keys for a rule into a single parameterized SQL query (using Table-Valued Parameters or `OPENJSON`, chunked at \~200 keys per query) to eliminate \$N+1\$ database calls.  
* Queries indexed columns using `READ COMMITTED SNAPSHOT` (or non-blocking reads) so monitoring never contends with the business application.

## **5.3 HybridVerifier**

* Runs both `FolderToFolderVerifier` and `FolderToDbVerifier` independently, persisting `FolderVerifiedAtUtc` and `DbStatus` separately.  
* Transitions to `SUCCEEDED` only when both pass; transitions to `FAILED` immediately on DB failure or `ErrorPath` match; transitions to `PARTIAL_MISMATCH` if SLA expires with only one side complete.

# **6\. State Ledger & High-Availability Design**

## **6.1 Core Tables**

| Table | Purpose | Key Columns |
| :---- | :---- | :---- |
| `TrackedFiles` | Stores active and terminal file lifecycles | `TrackedFileId`, `RuleId`, `RelativePath`, `FileSizeBytes`, `LastWriteTimeUtc`, `AttemptNumber`, `State`, `FirstSeenUtc`, `VerifiedAtUtc`, `FolderVerifiedAtUtc`, `DbStatus`, `MatchedTargetFile`, `AlertedAtUtc` |
| `RuleHeartbeats` | Powers dead-man's switch monitoring | `RuleId`, `LastTickStartedUtc`, `LastTickCompletedUtc`, `RuleHealth` (Healthy / ShareUnavailable / DbUnavailable), `PendingCount` |
| `ServiceLeases` | Coordinates Active/Passive HA failover | `LeaseName`, `OwnerMachineName`, `AcquiredUtc`, `ExpiresUtc` |

## **6.2 Active/Passive Failover & Data Retention**

* **High Availability**: Deploy two Windows Service instances (Active/Passive) sharing a SQL Server State Ledger. The active node holds a distributed lease (via `sp_getapplock` or `ServiceLeases` with a 60-second TTL renewed every tick); the passive node takes over automatically during OS patching or node failure without duplicate alerts.  
* **Ledger Retention**: A nightly cleanup job archives or purges verbose audit details older than 30–90 days while retaining the compact `(RuleId, RelativePath, FileSizeBytes, LastWriteTimeUtc, TerminalState)` index so permanent inbound files are never re-processed.

# **7\. Production Readiness, Security & Operations**

| Area | Production Risk | Architectural Mitigation |
| :---- | :---- | :---- |
| **Session 0 & Least Privilege** | Mapped drives (`Z:\`) fail in Session 0; write access risks file corruption | Enforce UNC paths only; run under a Group Managed Service Account (gMSA) with strictly read-only NAS permissions. |
| **NAS Outage Isolation** | Unreachable SMB shares cause false SLA breaches or worker hangs | Catch `IOException`/`UnauthorizedAccessException` per rule, set `RuleHealth = ShareUnavailable`, pause SLA timers, and enforce `PerRuleTimeoutSeconds`. |
| **Silent Monitor Failure** | Service crash or deadlock leaves files unmonitored | Configure Windows Service recovery actions (auto-restart) \+ external dead-man's switch alerting if `RuleHeartbeats` stalls for \$3\\times\$ `ScanIntervalSeconds`. |
| **Alert Fatigue** | Downstream app outage causes hundreds of individual alerts | Deduplicate per file via `AlertedAtUtc` and aggregate notifications per rule per tick (breach count \+ oldest file). |
| **Clock Skew** | NAS timestamps drift relative to SQL Server or monitor host | Anchor SLA timers to the monitor's own `FirstSeenUtc`; allow configurable `MinTargetModifiedOffsetSeconds` skew buffer. |
| **Safe Config Changes** | Malformed regex or typo breaks a production rule | Provide `--validate-config` and `--dry-run` CLI flags on the same binary to test UNC access, SQL connectivity, and sample regex matches before rollout. |

# **8\. Recommended Implementation Roadmap**

* **Phase 1 (Core Foundation)**: Scaffold .NET 8 Worker Service, `monitors.json` \+ `IOptionsMonitor` validation, State Ledger schema, Stability Gate, and `FolderToFolderVerifier`.  
* **Phase 2 (Advanced Verification)**: Implement `RegexTargetMatcher` (with `ErrorPath` and date-token paths), `FolderToDbVerifier` (batched queries), and `HybridVerifier`.  
* **Phase 3 (Production Hardening)**: Add Active/Passive SQL lease failover, `RuleHeartbeats` dead-man's switch, aggregated alerting, nightly ledger retention job, and `--validate-config` CLI mode.
